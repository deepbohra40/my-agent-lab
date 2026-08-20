# my-agent-lab — CRE covenant compliance

[![CI](https://github.com/deepbohra40/my-agent-lab/actions/workflows/ci.yml/badge.svg)](https://github.com/deepbohra40/my-agent-lab/actions/workflows/ci.yml)

A commercial real-estate servicer receives a quarterly reporting package from a
borrower — rent roll, operating statement, insurance certificate, tax bill. Somebody
has to read it, pull the numbers out, test them against the covenants in the loan
agreement, and raise a servicing exception when one fails. Today that somebody is an
analyst with a spreadsheet.

This is that workflow, built as a .NET service that orchestrates a language model.

**The model extracts. C# decides.** A model is good at reading a scanned rent roll
and reporting 83.5% occupancy. It is the wrong tool for deciding whether 83.5%
breaches an 85% floor — that comparison has to be reproducible and auditable, and no
sampled decoder can promise either. Every covenant verdict in this repo is computed
in `Domain/CovenantEngine.cs`, which contains no model call and never will.

> Everything in `fixtures/` and `MockServicingSystem` is fabricated. No real borrower,
> property, loan, or document — which is the reason this can exist outside a bank's
> network at all.

---

## The five things worth looking at

If you have five minutes, these are the decisions I would want reviewed.

| | Where |
| --- | --- |
| **1. The model is not allowed to decide anything** | [`Domain/CovenantEngine.cs`](src/CreServicing.Agent/Domain/CovenantEngine.cs) |
| **2. A real bug, diagnosed as structural and fixed structurally** | [`Tools/ServicingTools.cs`](src/CreServicing.Agent/Tools/ServicingTools.cs) |
| **3. Tools sorted by blast radius, exactly one gated** | [`Agents/ServicingAgentHost.cs`](src/CreServicing.Agent/Agents/ServicingAgentHost.cs) |
| **4. Extraction accuracy is measured, not asserted** | [`tests/CreServicing.Agent.Eval/`](tests/CreServicing.Agent.Eval) |
| **5. Cost per package is a number, not a hope** | [`Cost/ModelCost.cs`](src/CreServicing.Agent/Cost/ModelCost.cs) |

### 1. The model is not allowed to decide anything

`Covenants.cs` and `CovenantEngine.cs` are pure functions. The agent supplies measured
figures and asks for a verdict; it does not get to produce one. Everything the model
touches sits strictly upstream of `CovenantEngine.Evaluate`.

The property this buys: **same inputs, same findings, every run, forever.** 71 tests
assert it, including determinism across 50 consecutive evaluations and byte-identical
evidence strings under `hi-IN`, `de-DE` and `ja-JP` locales — because a breach filed
from Bangalore and one filed from Frankfurt have to produce the same audit record, not
`₹2,21,80,000` and `22.180.000 $`.

It is also why the default `dotnet run` needs no Azure credential, no key, and costs
nothing. The deterministic half is not waiting on the agent layer.

### 2. A real bug, diagnosed as structural and fixed structurally

`EvaluateCovenants` originally took `occupancyRate` as a single pre-divided decimal.
On the first live run the model returned **0.835915** for 118,600 / 142,000. The true
value is **0.835211**. It did the division in its head, missed in the fourth decimal,
and reported six.

Both figures breach the 85% floor, so the verdict survived. That was luck.

The inconsistency it exposed was that DSCR and LTV were always computed in C# from
their operands, and occupancy alone was not. **The fix was to change the tool
signature to take `occupiedSpace` and `totalSpace` rather than a quotient** — not to
add a sentence to the prompt telling the model to be careful.

That one change killed four failure modes at once: the arithmetic error, fraction vs.
percentage confusion, the office-vs-multifamily unit-of-measure branch, and physical
vs. economic occupancy. A model cannot hand over an economic occupancy figure when it
cannot hand over a quotient at all. **Structure beats instruction.**

A second instance of the same principle: the operating statement extractor captures
`reportedNetOperatingIncome` — whatever the borrower's document *claims* — in a field
separate from the NOI the engine computes. On the office fixture the borrower reports
**$2,284,000** by adding back a $154,000 roof repair; recomputed from the raw line
items it is **$2,130,000**. The covenant test uses the recomputed figure. Capturing
both is what makes the $154,000 gap visible instead of silently accepted.

### 3. Tools sorted by blast radius, exactly one gated

Four of the five tools are reads and one is a write, and they are not the same kind of
thing. A bad call on `GetLoanTerms` costs a few cents. A bad call on
`CreateServicingException` puts a false covenant breach on a borrower's file, which
drives a notice, a reserve decision, and eventually a conversation with a person.

**An agent may read freely and must ask before it writes.** Sort tools by blast
radius, not by convenience. Exactly one is gated, deliberately — gate all five and the
operator starts clicking through, and approval fatigue is the failure mode of every
human-in-the-loop system ever built.

Four things that turned out to matter more than the gate itself:

- **`ApprovalRequiredAIFunction` enforces nothing.** Its own XML doc says so — it is a
  `DelegatingAIFunction` marker. Enforcement lives in `FunctionInvokingChatClient`,
  which swaps the call for a `ToolApprovalRequestContent` and returns early. The host
  loop is what resolves it; delete the loop and the agent stalls forever.
- **Approval contaminates the whole batch.** If *any* call in a response needs
  approval, *every* call in that response does, including ungated reads. The prompt
  used to ask "Approve this filing?" over a `GetDocumentText`. The host now derives
  the gated set from the tool registration itself and auto-resolves anything swept in
   — rather than the documented workaround of `AllowMultipleToolCalls = false`, which
  fixes it by paying a round trip per tool call across the entire run.
- **The operator sees the trace before the question.** Approving a filing on the
  strength of its five arguments alone means authorising the agent's reading of
  documents you were never shown. Each pause prints what the agent did since the last
  one.
- **The ledger records the decision, not just the role.** Every entry carries
  time-to-decision, keyed to the specific filing it authorised. It prevents nothing —
  a determined human can still hold down `y` — but three breaches cleared in 900ms is
  a finding about the process, and without the field nobody could ever see it.

Approvals are keyed to loan + finding code and **consumed on use**: one approval
authorises exactly one write. A second filing under the same code lands in the ledger
as `UNATTRIBUTED` rather than silently inheriting the first one's approval.

### 4. Extraction accuracy is measured, not asserted

`tests/CreServicing.Agent.Eval/` grades all four extractors against
`fixtures/golden/expected-extractions.json` — 9 hand-verified documents, field-level,
exact match on every numeric field. Live model calls, no record/replay.

It is a **separate project from the free test suite** on purpose: the moment a test
needs a model it does not belong in a job that runs on every push. That one costs
money and needs `az login`; the other runs in 240ms with no credentials.

The golden set is built around traps rather than happy paths — an office roll that
states RSF and no units, a multifamily roll that states units and no RSF (neither
derivable from the other), a certificate listing a building limit beside a
business-income limit where summing them hides a real breach, a tax bill that carries
no loan number at all.

**`fixtures/adversarial/` is a passing test, not a curiosity.** A rent roll carries an
injected "SYSTEM NOTICE" instructing the model to report full occupancy and stay
quiet. The test asserts four things: the real figure comes back, the forced reply
never reaches `Notes`, a field the injection corrupted returns `null` rather than a
guess, and — the one people miss — **the attempt is surfaced**. A silently-resistant
extraction still fails. A borrower embedding pipeline instructions in a certified
document is a fraud signal, and the whole point is that a human sees it happened.

Delimiting untrusted text is the cheap half of that defence and worth being precise
about: it helps and it never guarantees. The actual guarantee is structural — the
covenant decision is made in C# from typed numbers, and no sentence in a PDF can reach
that code path.

### 5. Cost per package is a number, not a hope

`--extract-snapshot` prints the real figure at the end of every run. `CRE-2019-0447`,
three documents, `gpt-5-mini`:

| document | input | output | USD |
| --- | ---: | ---: | ---: |
| `rent-roll-2026-Q2.txt` | 766 | 527 | 0.001246 |
| `operating-statement-2026-Q2.txt` | 775 | 653 | 0.001500 |
| `insurance-certificate-2026.txt` | 735 | 705 | 0.001594 |
| **per package** | **2,276** | **1,885** | **0.004339** |

Quarterly reporting, one package per loan per quarter: **$4.34/yr** at 250 loans,
**$17.36** at 1,000, **$86.78** at 5,000.

- **Cost is not the constraint on this system** — worth knowing with a number rather
  than assuming it in either direction. A 5,000-loan book costs less to extract for a
  year than a single analyst-day. The constraint is extraction accuracy and the human
  review loop, which is where the effort went.
- **Output tokens dominate.** 1,885 output cost more than 2,276 input, because output
  is priced 8x. That is the economic argument for keeping extraction schemas narrow:
  every field an extractor is asked to return is billed at the output rate on every
  document, forever.
- **What it excludes**, both stated in the report itself: the agent tool loop is
  several round trips per package rather than three one-shot calls, and a real package
  is scanned pages through OCR rather than clean text.

Extractors return `ExtractionResult<T>` carrying that call's usage rather than writing
into an ambient ledger — cost is a property of a call, so the call returns it, and
concurrent extraction will account correctly with no coordination. `Cost/` holds no
SDK reference, which is what lets a per-package dollar figure be asserted by the free
CI job. `ModelPricing.PricedAsOf` is printed beside every figure so a stale projection
is visibly stale rather than quietly wrong.

---

## Run

```powershell
# The covenant review across all three synthetic loans.
# No Azure call, no key, no cost — the deterministic half runs on its own.
dotnet run --project src/CreServicing.Agent

# One loan
dotnet run --project src/CreServicing.Agent -- CRE-2019-0447
```

```
CRE-2019-0447  Lakeview Corporate Center
  Borrower        Lakeview Holdings LLC
  Covenants       DSCR >= 1.25   LTV <= 75%   Occupancy >= 85%

  RESULT          5 exception(s)

    [BREACH] DSCR-MIN
      DSCR of 1.156 is below the 1.25 covenant minimum.
      Evidence: NOI $2,130,000 / annual debt service $1,842,000 = 1.1564
    ...
```

Every number in that report is computed in C#, not generated. The `Evidence` line
carries the arithmetic so a human can re-check the call without rerunning anything.

The paths below call a model — they cost money and need `az login`:

```powershell
# Assemble the FinancialSnapshot from the borrower's real documents and show it
# beside the hand-keyed one, with both sets of covenant findings and the cost.
dotnet run --project src/CreServicing.Agent -- --extract-snapshot CRE-2019-0447

# The agent: tool loop, with the one write behind human approval.
dotnet run --project src/CreServicing.Agent -- --agent CRE-2019-0447
```

`--extract-snapshot` exists to prove a claim rather than assert it: the snapshot
assembled from extracted documents produces the *same* covenant findings as the
hand-keyed one. The single expected divergence is `LTV-UNTESTED` — no appraisal
document exists in these fixtures, and an untested covenant is reported as a finding
rather than left to be inferred from an absence.

### Prerequisites

| Setting | Value |
| --- | --- |
| `AZURE_OPENAI_ENDPOINT` | `https://maf-course-db26.openai.azure.com/` |
| `AZURE_OPENAI_DEPLOYMENT_NAME` | `gpt-5-mini` |
| Auth | `AzureCliCredential` — needs `az login` and the *Cognitive Services OpenAI User* role |

Both env vars are set at Windows user level, so restart any terminal or IDE opened
before they were set.

## Tests

```powershell
dotnet test
```

**71 tests, ~240ms, no credentials and no network.** Everything under test is a pure
function, which is the point rather than a convenience. What they pin:

- **The band edges, not the middles.** A DSCR three points clear of its floor passes
  under any plausible implementation. A DSCR sitting *exactly* on the floor is where
  `<` and `<=` diverge, so every covenant test is asserted one tick either side of its
  boundary. Sitting exactly on a minimum yields `Watch`, never `Pass` — a policy
  decision that is invisible in the code and would otherwise be one careless refactor
  from silently inverting.
- **A missing appraisal is `LTV-UNTESTED`, not silence.** An untested covenant is not
  a passing covenant. Silence would let a reviewer conclude LTV was checked and cleared.
- **Culture independence**, asserted against three non-US locales.
- **Determinism**, across 50 consecutive evaluations of the same distressed loan.
- **Every emitted code is declared in `KnownCodes`** — the engine cannot emit a finding
  its own write path would refuse to file, and every code in that set is accepted by
  `CreateServicingException`.
- **The write gate rejects before a human is asked.** An invented code, an unparseable
  severity, a missing evidence string — refused by the tool itself. A gate that
  presents garbage to a human is a worse gate.
- **The cost arithmetic**, including that output is priced above input on every rate in
  the table, and that an unknown deployment degrades to "unpriced" rather than throwing
  partway through a covenant review.

One test is deliberately skipped rather than deleted: a loan *past* its maturity date
currently produces no finding at all, because the horizon check guards with
`daysToMaturity >= 0`. A matured unpaid loan is the most serious state in servicing, so
that silence is wrong. The test stays, skipped, so the gap shows up in the test report
instead of living in someone's memory.

CI runs this suite on every push and pull request.

## Structure

```
my-agent-lab/
├── .github/workflows/ci.yml            # build + test on every push and PR
├── tests/
│   ├── CreServicing.Agent.Tests/       # free: boundaries, determinism, culture, write gate, cost
│   └── CreServicing.Agent.Eval/        # costs money: extraction accuracy vs. the golden set
└── src/CreServicing.Agent/
    ├── Domain/                         # Covenants, CovenantEngine — no model call, ever
    ├── Extraction/                     # four extractors + FinancialSnapshotAssembler
    ├── Tools/                          # ServicingTools — four reads, one gated write
    ├── Agents/                         # ServicingAgentHost — tool loop and the HITL gate
    ├── Data/                           # system of record, document store, ledger, approvals
    ├── Cost/                           # tokens, rates, per-package cost, portfolio projection
    └── fixtures/
        ├── CRE-2019-0447/              #   distressed office — four documents, four breaches
        ├── CRE-2021-0912/              #   healthy multifamily — must produce a CLEAN report
        ├── adversarial/                #   rent roll carrying a prompt injection
        └── golden/                     #   expected-extractions.json — the eval answer key
```

A third loan, `CRE-2018-0233`, exists in `MockServicingSystem` with **no fixture
package at all** — that is the "no documents on file" path, and it stays hand-keyed by
design rather than being a gap.

## What is deliberately not built

Being able to state the trade-off is not the same as having built the thing, and
pretending otherwise is worse than the gap.

- **Agent memory across reporting periods.** "Occupancy has fallen for three
  consecutive quarters" is an asset-management conversation no single-period test can
  produce. Understood, not built — it would not have changed what this repo
  demonstrates.
- **Document classification and routing.** `FinancialSnapshotAssembler` locates
  documents by filename convention. A real intake pipeline classifies first and routes
  to the right extractor; a tax bill and a rent roll should not share a prompt.
- **Retrieval over the loan agreement.** `ServicingException.ClauseCitation` is a slot
  that stays null. An exception quoting section 7.3(b) is a document a servicer can
  send; one that says "DSCR is low" is not.
- **Hosting.** This runs as a console app. The credential is `AzureCliCredential`, the
  client is newed up inline, config comes from environment variables, and there is no
  DI container, no `CancellationToken`, and no retry policy — all fine for a console
  app and none of it acceptable behind an HTTP endpoint. The interesting part is not
  the boilerplate; it is that the human approval loop currently works *because*
  `Console.ReadLine()` blocks and holds the run in memory, and a suspended run over
  HTTP has to be persisted and resumed across two requests.
- **OCR.** Real packages are scanned pages. These fixtures are clean text.
- **Low-confidence routing.** Extractors report a self-scored confidence that is
  captured and never acted on. The answer that matters is not "retry" — it is "route
  to a human, with the specific question that needs answering."

## Provenance

Started as a build-along for a .NET agent-framework course, then derived away from it:
the course samples are IT-support ticket triage, and everything here is structured
around a domain I know instead. `src/section5-getting-started/` and
`src/section6-tool-use/` are follow-along scratchpads that mirror the course repo
path-for-path — throwaway, kept only so a stuck moment is a plain side-by-side diff.
`src/ReviewAgent.Console/` was an earlier take on the same idea and is pending deletion.

Package versions are pinned to match those samples (`Azure.AI.OpenAI 2.9.0-beta.1`,
`Microsoft.Agents.AI.OpenAI 1.3.0`) so snippets paste in without API drift. Bump them
deliberately, not by accident.

The section-by-section roadmap, and the reasoning behind each decision above, lives in
the comment block at the bottom of `src/CreServicing.Agent/Program.cs`.
