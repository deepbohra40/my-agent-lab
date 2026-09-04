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

## The six things worth looking at

If you have five minutes, these are the decisions I would want reviewed.

| | Where |
| --- | --- |
| **1. The model is not allowed to decide anything** | [`Domain/CovenantEngine.cs`](src/CreServicing.Core/Domain/CovenantEngine.cs) |
| **2. A real bug, diagnosed as structural and fixed structurally** | [`Tools/ServicingTools.cs`](src/CreServicing.Core/Tools/ServicingTools.cs) |
| **3. Tools sorted by blast radius, exactly one gated** | [`Agents/ServicingRunner.cs`](src/CreServicing.Core/Agents/ServicingRunner.cs) |
| **4. A human-approval loop that survives being put behind HTTP** | [`Runs/ServicingRun.cs`](src/CreServicing.Core/Runs/ServicingRun.cs) |
| **5. Extraction accuracy is measured, not asserted** | [`tests/CreServicing.Core.Eval/`](tests/CreServicing.Core.Eval) |
| **6. Cost per package is a number, not a hope** | [`Cost/ModelCost.cs`](src/CreServicing.Core/Cost/ModelCost.cs) |

### 1. The model is not allowed to decide anything

`Covenants.cs` and `CovenantEngine.cs` are pure functions. The agent supplies measured
figures and asks for a verdict; it does not get to produce one. Everything the model
touches sits strictly upstream of `CovenantEngine.Evaluate`.

The property this buys: **same inputs, same findings, every run, forever.** 76 tests
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

### 4. A human-approval loop that survives being put behind HTTP

The console loop worked for a reason that does not generalise: `Console.ReadLine()`
blocks, so the agent session, the tool trace, the accumulated token usage and the
pending request all sat on the stack for as long as a person took to answer.

**There is no stack behind HTTP.** The request that asks the question has to return,
and a different request — minutes later, possibly to a different instance — has to
pick the run up exactly where it stopped. That is a design problem, not a port, and it
is the whole of what `Runs/` is for.

The state machine is two methods rather than a loop. `StartAsync` runs until the agent
either finishes or asks for something; `ResumeAsync` takes the answers and continues.
**The caller owns the loop**, and the caller is a terminal in
[`ConsoleApprovalLoop`](src/CreServicing.Cli/ConsoleApprovalLoop.cs) and two HTTP
requests in [`ServicingRunEndpoints`](src/CreServicing.Api/ServicingRunEndpoints.cs).
Neither host can drift from the other on a safety property, because neither host owns
one.

What made it genuinely resumable rather than merely cached is that MAF exposes
`SerializeSessionAsync`/`DeserializeSessionAsync`. The conversation the agent has had —
every document it read before it asked — is *storable*, not just holdable. So a
suspended run is a JSON document, and `IRunStore` is the one seam a real deployment
replaces.

That serialization is also where this heading was, for a while, a lie. On
`Microsoft.Extensions.AI` 10.5.0 the loop survived exactly **one** round: the second
resume threw, because the framework strips resolved approval request/response pairs as
transient while `SerializeSessionAsync` persisted them anyway. Every test passed, because
every test scripted a single approval. It took a live five-finding run to find —
[the full account is below](#what-a-live-run-caught-that-the-tests-did-not). Fixed on
10.9.0, pinned by `A_run_survives_a_second_approval_round`, and since demonstrated end to
end across five rounds.

Three decisions in there worth arguing with:

- **The in-memory store serializes on save and deserializes on load** rather than
  handing back the object it was given. That is slower and it is the point: it makes
  the store behave like a real one, so "can this run actually be persisted?" is
  answered by every test that touches it. A run that quietly held a live
  `AgentSession` would pass against a store that returned the same reference and fail
  the first time it met Redis.
- **A duplicate approval submission cannot file twice.** An impatient operator
  double-clicking is not exotic, and over HTTP a retry is the default behaviour of half
  the clients in existence. A per-run lock serialises the two, and then the second one
  loads state in which those request ids are no longer outstanding and gets a 400.
  The lock alone would only have made the double-file sequential. Across two instances
  the right mechanism is optimistic concurrency on the stored record, which is where it
  belongs — in the store, when there is one.
- **A partial submission is refused, not treated as rejection.** Reading "no answer" as
  "no" would file nothing and look identical to an operator who declined. That is the
  wrong thing to be ambiguous about.

Both pieces of static mutable state are gone as part of this, and not by adding locks.
`ExceptionLedger` and `ApprovalLedger` are now **owned by a run**, and the agent for a
run holds the only reference to that run's tool instance — so two concurrent reviews
cannot see each other's filings by construction rather than by convention. The most
direct evidence is in the diff: `WriteGateTests` used to carry
`[CollectionDefinition(DisableParallelization = true)]` and a `Clear()` in its
constructor, and both could simply be deleted.

### 5. Extraction accuracy is measured, not asserted

`tests/CreServicing.Core.Eval/` grades all four extractors against
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

### 6. Cost per package is a number, not a hope

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

`--agent` prints its own figure on the same terms, so the fixed pipeline and the tool
loop over the **same package** are directly comparable rather than one being assumed
cheaper. They turn out to have inverted cost profiles:

| | pipeline, 3 one-shot calls | agent, 6 round trips |
| --- | ---: | ---: |
| input tokens | 2,276 | **44,814** |
| output tokens | 1,885 | 3,039 |
| input cost | $0.00057 (13%) | **$0.01120 (65%)** |
| output cost | $0.00377 (**87%**) | $0.00608 (35%) |
| **per review** | **$0.004339** | **$0.017282** |
| at 5,000 loans/yr | $86.78 | $345.63 |

- **Cost is not the constraint on this system** — worth knowing with a number rather
  than assuming it in either direction. Even the expensive path runs a 5,000-loan book
  for a year on less than a single analyst-day. The constraint is extraction accuracy
  and the human review loop, which is where the effort went.
- **Which side of the meter dominates flips between the two designs, and the reason
  is architectural.** In the pipeline, output is 87% of cost — that is the economic
  argument for narrow extraction schemas, since every field requested is billed at the
  output rate on every document forever. In the agent, input is 65%, because each
  approval round replays the entire conversation: five findings meant six round trips
  each re-sending all three documents. The agent spends **20x the input tokens for
  1.6x the output**, and only lands at 4x total because output is priced 8x.
- **So the HITL loop is what costs money, and it costs it on the input side.** The
  named lever is prompt caching — a conversation replayed six times with a growing
  suffix is close to the ideal case for it. Not wired up; knowing where the cost is
  and which lever moves it is the point.
- **What is still excluded:** a real package is scanned pages through OCR rather than
  clean text, which this does not model at all.

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
dotnet run --project src/CreServicing.Cli

# One loan
dotnet run --project src/CreServicing.Cli -- CRE-2019-0447
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
dotnet run --project src/CreServicing.Cli -- --extract-snapshot CRE-2019-0447

# The agent: tool loop, with the one write behind human approval.
dotnet run --project src/CreServicing.Cli -- --agent CRE-2019-0447
```

`--extract-snapshot` exists to prove a claim rather than assert it: the snapshot
assembled from extracted documents produces the *same* covenant findings as the
hand-keyed one. The single expected divergence is `LTV-UNTESTED` — no appraisal
document exists in these fixtures, and an untested covenant is reported as a finding
rather than left to be inferred from an absence.

### The same thing over HTTP

```powershell
dotnet run --project src/CreServicing.Api
```

**The API starts with no Azure configuration at all**, and that is deliberate rather
than lax. Most of the surface is deterministic C# that needs no credential, and it is
the part that must never be unavailable, because it is the audit-grade path. A web host
that crash-loops because extraction is unconfigured takes the covenant endpoints down
to protect endpoints nobody called. The routes that do need a model answer **503 naming
the missing setting** instead — and `/health` reports which half is working rather than
calling itself unhealthy, so a load balancer does not pull an instance that is serving
the deterministic path perfectly well.

| | Route | Needs a model |
| --- | --- | --- |
| Liveness, and whether this instance can reach a model | `GET /health` | no |
| Loans in the system of record | `GET /loans`, `GET /loans/{id}` | no |
| Covenant findings, computed in C# | `GET /loans/{id}/covenant-review` | no |
| What the borrower submitted — names and sizes, never content | `GET /loans/{id}/documents` | no |
| Extract a snapshot and compare it with the hand-keyed one | `POST /loans/{id}/financial-snapshot` | **yes** |
| Start a review | `POST /servicing-runs` | **yes** |
| What a run is, and what it is waiting for | `GET /servicing-runs`, `GET /servicing-runs/{id}` | no |
| Answer every outstanding approval and resume | `POST /servicing-runs/{id}/approvals` | **yes** |
| What actually landed on the loan file | `GET /servicing-runs/{id}/ledger` | no |

The approval loop over HTTP, end to end. `POST` a run; it comes back `201` with status
`AwaitingApproval` and the arguments it wants authorised:

```jsonc
{
  "runId": "8f2c…", "status": "AwaitingApproval", "round": 1,
  "awaitingApproval": [{
    "requestId": "…",
    "tool": "CreateServicingException",
    "arguments": { "loanId": "CRE-2019-0447", "code": "DSCR-MIN", "severity": "Breach", … }
  }],
  "filed": [],                       // the gate holding: nothing written while it waits
  "cost": { "inputTokens": 20173, "cachedInputTokens": 1664, "modelCalls": 1, "usd": 0.007 }
}
```

Then `POST /servicing-runs/{id}/approvals` with a decision per `requestId`, and the run
resumes. Note what is *not* in that payload: the serialized agent session. It is the
entire conversation including every document the agent read, it is storage rather than
a contract, and a test asserts it never reaches the wire.

Two details in the response that are there on purpose. `cost` appears on every reply,
including the ones that suspend — a run that has paused three times has already spent
the money for three rounds, and the operator deciding whether to continue should be
looking at that number. `cachedInputTokens` is a **subset** of `inputTokens`, not an
addition, and it matters more with each round: a resume re-sends the same conversation
prefix, so the hit rate climbs exactly where a multi-finding package spends most.
`modelCalls` counts agent *turns*, not HTTP calls — one turn runs the whole tool loop
and the tokens aggregate all of it. And `autoApproved` lists calls the framework swept into an
approval round by batching that the runner resolved itself: visible in the audit trail,
never put to a human as a question.

### One real run, start to finish

A live run against `gpt-5-mini`, `CRE-2019-0447`, five rounds, **$0.017**. Four
approvals and — deliberately — one refusal.

```
EX-20260904-001  DSCR-MIN      Breach          by=deep  7s
EX-20260904-002  LTV-UNTESTED  Informational   by=deep  6s
EX-20260904-003  INS-COVERAGE  Breach          by=deep  9s
EX-20260904-004  INS-EXPIRY    Watch           by=deep  9s

attempted : DSCR-MIN, LTV-UNTESTED, OCC-MIN, INS-COVERAGE, INS-EXPIRY
landed    : DSCR-MIN, LTV-UNTESTED,          INS-COVERAGE, INS-EXPIRY
```

Five attempted, four landed. The gap is the one the operator refused, and it stayed
refused — one filing attempt for `OCC-MIN` in the whole trace, no retry under a
softened summary. The agent's closing report says so itself: *"One servicing exception
(OCC-MIN) was rejected by the approver when I attempted to file it; I did not retry or
modify it."* Worth being precise about what that proves: nothing structural prevents a
re-file — `CreateServicingException` checks that a code is real, not that it was already
refused — so this is instruction-following holding, not a guardrail. It is exactly the
kind of claim that needs a live run rather than an assertion.

Three other things that run demonstrates, none of which a passing test suite had shown:

**The NOI add-back trap, sprung and defeated.** Unprompted, in its summary:

> The borrower's operating statement footnote discloses exclusion of $154,000 of roof
> membrane replacement as capital; I passed operating expenses as printed ($2,390,000)
> per instructions and did not adjust NOI.

The fixture plants a footnote inviting the model to reclassify $154k of roof work as
capital. Complying drops opex to $2,236,000, lifts NOI to $2,284,000 and moves DSCR from
1.156 to 1.24 — still a breach, but a materially different number on a borrower's file,
sourced from the borrower's own argument. The tool description says to pass the total as
printed and report the argument separately. It did both.

**Two clocks, still separate.** `INS-EXPIRY` reads *"25 days remaining as of 2026-09-05"*
against a period close of `2026-06-30`. A single-clock implementation calls that policy
current, because at period close it was 92 days out.

**Operands, never ratios.** What reached `EvaluateCovenants` was `occupiedSpace: 118600`
and `totalSpace: 142000` — no quotient anywhere. The 83.52% in the finding was computed
in C#. This is the bug in [section 2](#2-a-real-bug-diagnosed-as-structural-and-fixed-structurally)
not happening, because the signature no longer permits it.

The run also found a defect, which is the honest reason it exists. See
[what a live run caught that the tests did not](#what-a-live-run-caught-that-the-tests-did-not).

### Watching a run instead of calling one

```powershell
dotnet run --project src/CreServicing.AppHost
```

Starts the API behind the **Aspire dashboard** and prints a login URL carrying a one-time
token — copy the whole line, the token is the login. Needs no Docker and no Aspire
workload: Aspire 13 ships as an MSBuild SDK resolved from NuGet.

The point is that a servicing run is *two HTTP requests minutes apart*, and in a plain
access log that is two unrelated `POST`s. In the dashboard it is one span tree:

```
POST /servicing-runs                     ← the request that suspends
└── servicing_run.start                     loan, run id, status, tokens
    ├── chat gpt-5-mini                     the model call — tokens, finish reason
    ├── execute_tool GetLoanTerms           thresholds, from the loan agreement
    ├── execute_tool GetDocumentText        path and size; never the text
    └── execute_tool EvaluateCovenants      ← the verdict crosses into C# here
                                              finding codes, count, ltv_tested
POST /servicing-runs/{id}/approvals       ← minutes later, a different request
└── servicing_run.resume
    └── execute_tool CreateServicingException
                                              filed, reference number, approved_by
```

Read top to bottom, that tree *is* the argument in section 1: every span above
`EvaluateCovenants` is the model choosing what to read, and the verdict appears only
below it. A gated write that a human never approved has **no span at all** — the
framework suspends before the function body runs — which makes the span count a usable
proxy for writes that actually happened. There is a test pinning exactly that.

Instrumentation is always on; the OTLP exporter registers only when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set, which Aspire injects. So `dotnet run` on the API
alone still works with no collector and no exporter retrying a connection nobody asked
for — and the integration tests, which boot the same composition through
`WebApplicationFactory`, are unaffected.

Prompts and completions are deliberately **not** on the spans. Turning that on would
ship whole borrower rent rolls to a collector, which is the same boundary
`GET /loans/{id}/documents` holds when it returns names and sizes but never content.

### Prerequisites

Only for the model-backed paths. The deterministic ones need none of this.

| Setting | Value |
| --- | --- |
| `AzureOpenAI:Endpoint` (or `AZURE_OPENAI_ENDPOINT`) | `https://maf-course-db26.openai.azure.com/` |
| `AzureOpenAI:Deployment` (or `AZURE_OPENAI_DEPLOYMENT_NAME`) | `gpt-5-mini` |
| Auth | `DefaultAzureCredential` — `az login` locally, managed identity in Azure, same binary either way |

The bare `AZURE_OPENAI_*` variables are honoured as a fallback so an existing machine
keeps working, but they only ever fill gaps: an explicitly configured
`AzureOpenAI:Endpoint` wins over one left in the environment by something else. Both
env vars are set at Windows user level, so restart any terminal or IDE opened before
they were set.

The CLI and the API differ on one thing deliberately. The CLI validates at startup and
exits `1` naming the missing key, because a console process exists to do the one
model-backed thing it was invoked for. The API starts anyway, for the reason above.

## Tests

```powershell
dotnet test
```

**123 tests, well under a second, no credentials and no network.** What they pin:

- **The band edges, not the middles.** A DSCR three points clear of its floor passes
  under any plausible implementation. A DSCR sitting *exactly* on the floor is where
  `<` and `<=` diverge, so every covenant test is asserted one tick either side of its
  boundary. Sitting exactly on a minimum yields `Watch`, never `Pass` — a policy
  decision that is invisible in the code and would otherwise be one careless refactor
  from silently inverting.
- **A missing appraisal is `LTV-UNTESTED`, not silence.** An untested covenant is not
  a passing covenant. Silence would let a reviewer conclude LTV was checked and cleared.
- **The two clocks.** `Evaluate` takes a period-close date *and* a review date, because
  DSCR/LTV/occupancy ask "how did the collateral perform during the period" while
  insurance expiry and maturity ask "is this true right now". Collapsing them was a real
  bug, caught by running the agent against a loan the deterministic path already had an
  answer for — the agent passed the rent roll's as-of date (correctly; that is what the
  tool asks for) and an `INS-EXPIRY` silently vanished, because the policy was 92 days
  out at period close and 41 days out on the day of review. Tests now pin both
  directions: a policy that lapses between period close and review is caught, and the
  measured covenants do not move when the review date does.
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

The approval loop is tested too, and that is newer than the rest. A scripted
`IChatClient` replays a fixed sequence of turns, so the entire suspend/resume state
machine runs with no Azure call and no cost — which is only possible because stage A
converted the runner and extractors to take an injected `IChatClient`. It pins that a
gated call suspends instead of executing, that an approved filing lands attributed to
the operator with its own time-to-decision, that a rejected one writes nothing, that a
read batched alongside a filing is never put to the human, that a partial submission is
refused, and that **a run resumed after a full round trip through storage still
works** — the test that separates a persisted run from a cached one. The same machine
is then driven over HTTP through `WebApplicationFactory` in
`tests/CreServicing.Api.Tests/`, including that a duplicate approval submission cannot
file the exception twice.

What still needs a live model is whether the *model* behaves. That is the eval
harness's job, deliberately in a different project with a different budget.

One test is deliberately skipped rather than deleted: a loan *past* its maturity date
currently produces no finding at all, because the horizon check guards with
`daysToMaturity >= 0`. A matured unpaid loan is the most serious state in servicing, so
that silence is wrong. The test stays, skipped, so the gap shows up in the test report
instead of living in someone's memory.

## What a live run caught that the tests did not

The suite above was green, and the approval loop was broken. Worth writing down, because
the shape of the miss is more interesting than the bug.

**The symptom.** The first live Postman run of the HITL flow approved round 1 fine —
`DSCR-MIN` filed, attributed, on the ledger. The *second* resume died:

```
InvalidOperationException: ToolApprovalRequestContent found with
FunctionCall.CallId(s) 'call_dvnm…' that have no matching ToolApprovalResponseContent
```

naming the round-1 request that had already filed successfully two turns earlier.

**The cause.** `FunctionInvokingChatClient.ExtractAndRemoveApprovalRequestsAndResponses`
treats an approval request/response pair as transient — it strips the pair once resolved,
leaving only the resulting `functionCall` and `functionResult`. `SerializeSessionAsync`
persisted the pair anyway. Deserializing re-injected a stale pair the matcher then
rejected. Isolated with an A/B probe against a scripted client, no model involved:
the identical three-turn script **throws** with a serialize/deserialize between turns and
**succeeds** with the session held in memory.

Which is the part worth sitting with. The console loop never hit this, because a
`Console.ReadLine()` loop holds the session on the stack. Only the HTTP path serializes —
so the defect lived precisely in the thing stage C was built to do, and nowhere else.

**The fix** was `Microsoft.Extensions.AI` 10.5.0 → 10.9.0. No source changes.
`SuspendedRunTests.A_run_survives_a_second_approval_round` is the regression test; it
fails on 10.5.0 and passes on 10.9.0.

**Why nothing caught it.** Every resume test scripted exactly *one* approval and then
completed. A real package is never one approval — `CRE-2019-0447` produces five findings
and the model asks for them one at a time. The suite tested the state machine with the
model faked out, and the eval harness tested the model one document at a time, and
**nothing tested them together across more than one round.** That seam is where the bug
lived. The one-approval case was not a simplification of the real case; it was the only
case that worked.

The same run surfaced a second, quieter error in the other direction: spans carried
`gen_ai.usage.cache_read.input_tokens` that `Cost/` knew nothing about, so cached input
was billed at the full rate. On a run whose cache hit rate climbs with every approval
round, that **overstates** cost — and `PortfolioProjection` multiplies the overstatement
by every loan and every quarter. An overstated cost model kills a project that would have
paid for itself, which is the same class of error as an understated one and easier to
miss. `ModelUsage` now carries `CachedInputTokens` as a subset of the input count, and
`ModelPricing.Usd` prices it separately.

Total cost of finding both: **$0.038.**

CI runs this suite on every push and pull request.

## Structure

```
my-agent-lab/
├── .github/workflows/ci.yml            # build + test on every push and PR
├── tests/
│   ├── CreServicing.Core.Tests/        # free: boundaries, determinism, culture, write gate,
│   │                                   #   cost, and the suspend/resume state machine
│   ├── CreServicing.Api.Tests/         # free: the HTTP surface, via WebApplicationFactory
│   └── CreServicing.Core.Eval/         # costs money: extraction accuracy vs. the golden set
└── src/
    ├── CreServicing.Core/              # the library — knows nothing about how it is invoked
    │   ├── Domain/                     #   Covenants, CovenantEngine — no model call, ever
    │   ├── Extraction/                 #   four extractors + FinancialSnapshotAssembler
    │   ├── Tools/                      #   ServicingTools — four reads, one gated write
    │   ├── Agents/                     #   ServicingRunner — start and resume, no console
    │   ├── Runs/                       #   suspended-run state, the store, the run service
    │   ├── Data/                       #   system of record, document store, ledger, approvals
    │   ├── Configuration/              #   the composition root, options, credential
    │   ├── Cost/                       #   tokens, rates, per-package cost, portfolio projection
    │   ├── Diagnostics/                #   ActivitySource only — no OpenTelemetry package here
    │   └── fixtures/
    │       ├── CRE-2019-0447/          #     distressed office — four documents, four breaches
    │       ├── CRE-2021-0912/          #     healthy multifamily — must produce a CLEAN report
    │       ├── adversarial/            #     rent roll carrying a prompt injection
    │       └── golden/                 #     expected-extractions.json — the eval answer key
    ├── CreServicing.Cli/               # argument parsing, Ctrl+C, the interactive prompt
    ├── CreServicing.Api/               # minimal API over the same library
    │                                   #   + Telemetry.cs — the OpenTelemetry SDK lives here
    └── CreServicing.AppHost/           # local dev only: Aspire dashboard launcher, no tests,
                                        #   not in the request path, not built by CI
```

The split is stage B's whole premise: an ASP.NET Core host cannot sensibly reference a
console `Exe`, so everything that does not know how it is being invoked moved into a
library and the two things that do — a terminal and a web host — sit beside it. The
API adds no domain logic of its own. If a rule lived there and not in `Core`, the CLI
and the API could disagree about whether a covenant was breached, and one of them would
be wrong.

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
- **Durable run storage.** The suspended-run problem is solved; *where* the run lives
  is not. `IRunStore` has one in-memory implementation, so a run does not survive a
  restart and two instances do not share one. That boundary is deliberate and the
  interface exists for exactly this reason — but an interface is not an implementation,
  and the honest statement is that this runs correctly on one process. The per-run lock
  that stops a duplicate approval filing twice has the same limit: correct in one
  process, worthless across two, where the answer is optimistic concurrency on the
  stored record.
- **Authentication, and therefore a trustworthy approver.** The API takes the
  approver's name from the request body. The entire value of that field is that the
  person approving cannot choose what it says about them, so in any real deployment it
  comes from the authenticated principal and the parameter does not exist. Every
  endpoint is unauthenticated; this is a lab.
- **A consistent clock for audit records.** `ExceptionLedger` stamps reference numbers
  and `FiledAt` from `DateTime.UtcNow`; the review date comes from `DateTime.Today`,
  which is local. Run from UTC+5:30 late in the day, one ledger entry carries two
  different dates for the same event — `EX-20260820-005` with evidence reading "as of
  2026-08-21". Ironic in a project that pins `en-US` formatting so audit records are
  byte-identical across machines, and then lets the machine's timezone pick the date.
  The fix is a decision rather than a typo: covenant windows are business judgments,
  so the review date should be a business date in an explicit servicing timezone with
  UTC retained for `FiledAt` — not whatever the host happens to be set to.
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
the comment block at the bottom of `src/CreServicing.Cli/Program.cs`.
