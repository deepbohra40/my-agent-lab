# my-agent-lab

[![CI](https://github.com/deepbohra40/my-agent-lab/actions/workflows/ci.yml/badge.svg)](https://github.com/deepbohra40/my-agent-lab/actions/workflows/ci.yml)

My own build-along project for the Udemy course *Agentic AI Development with Agent Framework, MCP and .NET*.
Kept deliberately **outside** the instructor's clone (`../develop-agents`, remote `mehmetozkaya/develop-agents`)
so that `git pull` there never collides with my work.

**Domain: CRE post-close document intake and covenant compliance.** A borrower
submits a quarterly package — rent roll, operating statement, insurance certificate,
tax bill. Something has to read it, extract the numbers, test them against the
covenants in the loan agreement, and raise an exception when one fails. Today that
something is an analyst with a spreadsheet.

The domain gives every section something real to bite on — documents to extract from,
a system of record to call, specialist agents worth routing between, covenant language
worth grounding answers in, and a write action (a servicing exception that reaches a
borrower) that genuinely warrants a human approval step. It is deliberately *not* the
IT-support ticket triage the course samples use, so I derive the structure instead of
copying it.

**Everything in `fixtures/` and `MockServicingSystem` is fabricated.** No real
borrower, property, loan, or document. That is not a limitation — it is the reason
this can exist outside a bank's network at all.

> `src/ReviewAgent.Console` was the earlier code-review take on the same idea,
> superseded by `CreServicing.Agent`. Kept until the section 5 material is fully
> rebuilt in the new domain, then deleted.

## How I use this

Per lecture:

1. Watch the section.
2. Run the instructor's sample from `../develop-agents` and see it work.
3. **Close it.** Build the equivalent here from memory and the docs.
4. Diff against his version only when genuinely stuck.

Step 3 is the point. Reading code produces recognition; rebuilding produces recall.
Copy the Aspire `AppHost`/`ServiceDefaults` boilerplate verbatim when the time comes —
there's nothing to learn there and it's pure friction.

## Prerequisites

Azure is already provisioned (`rg-maf-course` / `maf-course-db26`, South India):

| Setting | Value |
| --- | --- |
| `AZURE_OPENAI_ENDPOINT` | `https://maf-course-db26.openai.azure.com/` |
| `AZURE_OPENAI_DEPLOYMENT_NAME` | `gpt-5-mini` |
| Auth | `AzureCliCredential` — needs `az login` and the *Cognitive Services OpenAI User* role |

Both env vars are set at Windows user level, so **restart any terminal or Visual Studio
instance opened before they were set.**

## Run

```powershell
# The covenant review across all three synthetic loans.
# No Azure call, no key, no cost — the deterministic half runs on its own.
dotnet run --project src/CreServicing.Agent

# One loan
dotnet run --project src/CreServicing.Agent -- CRE-2019-0447

# Superseded, see note above
dotnet run --project src/ReviewAgent.Console

# Section 5 follow-along scratchpads
dotnet run --project src/section5-getting-started/BasicAgentApp
dotnet run --project src/section5-getting-started/Streaming
dotnet run --project src/section5-getting-started/MultiTurn
dotnet run --project src/section5-getting-started/StructuredOutput

# Aspire app — launches WebApi and opens the dashboard
dotnet run --project src/section5-getting-started/MinimalAgent/AppHost

# Section 6
dotnet run --project src/section6-tool-use/FunctionCall
```

Ports for `MinimalAgent` are shifted off the course repo's defaults (WebApi `5143`/`7227`,
dashboard `15379`/`17242`) so both can run side by side without collisions.

## Tests

```powershell
dotnet test
```

57 tests over `CovenantEngine`, `Covenants` and the write gate, running in about
200ms. No Azure credentials, no network, no cost — everything under test is a pure
function, which is the point rather than a convenience.

What they actually pin:

- **The band edges, not the middles.** A DSCR three points clear of its floor
  passes under any plausible implementation. A DSCR sitting *exactly* on the floor
  is where `<` and `<=` diverge, so every covenant test is asserted one tick either
  side of its boundary. Sitting exactly on a covenant minimum yields `Watch`, never
  `Pass` — a policy decision that is invisible in the code and would otherwise be
  one careless refactor from silently inverting.
- **A missing appraisal is `LTV-UNTESTED`, not silence.** An untested covenant is
  not a passing covenant. Silence would let a reviewer conclude LTV was checked and
  cleared.
- **Culture independence.** Evidence strings are pinned to `en-US` inside the
  engine, so the same breach filed from Bangalore and from Frankfurt produces
  byte-identical audit records rather than `₹2,21,80,000` and `22.180.000 $`.
  Asserted against `hi-IN`, `de-DE` and `ja-JP`.
- **Determinism**, across 50 consecutive evaluations of the same distressed loan.
- **Every emitted code is declared in `KnownCodes`**, so the engine can never emit a
  finding that its own write path would refuse to file — and, from the other
  direction, every code in that set is actually accepted by `CreateServicingException`.
- **The write gate rejects before a human is asked.** An invented code, an
  unparseable severity, a missing evidence string — all refused by the tool itself.
  A gate that presents garbage to a human to approve is a worse gate.
- **One approval authorises exactly one filing.** Approvals are keyed to loan +
  finding code and consumed on use, so a duplicate filing cannot inherit the
  approval a human gave the first one, and approving the DSCR breach is not
  approving the insurance one.

One test is deliberately `[Skip]`-ed: a loan *past* its maturity date currently
produces no finding at all, because the horizon check guards with
`daysToMaturity >= 0`. A matured unpaid loan is the most serious state in
servicing, so that silence is wrong. The test is left in place, skipped, so the gap
shows up in the test report rather than living in someone's memory.

CI runs the same suite on every push and pull request. It is scoped to the test
project rather than the whole solution, so the Aspire follow-along scratchpad does
not drag its workload into every run.

## Structure

```
my-agent-lab/
├── my-agent-lab.slnx
├── .github/workflows/ci.yml        # build + test on every push and PR
├── tests/
│   └── CreServicing.Agent.Tests/   # xUnit — boundaries, determinism, culture pinning
│       ├── Given.cs                #   one compliant loan; each test perturbs one field
│       ├── CovenantEngineTests.cs  #   the band edges, one tick either side
│       ├── CovenantsTests.cs       #   the four primitives and their divide-by-zero guards
│       ├── AuditabilityTests.cs    #   the properties that justify C# over a model
│       └── WriteGateTests.cs       #   what the one write tool refuses, and approval accounting
└── src/
    ├── CreServicing.Agent/          # the derived project — CRE covenant compliance
    │   ├── Domain/                  #   LoanTerms, Covenants, CovenantEngine, Extractions
    │   ├── Data/                    #   MockServicingSystem, DocumentStore, ExceptionLedger,
    │   │                            #   ApprovalContext — who authorised which filing
    │   ├── Agents/                  #   ServicingAgentHost — the tool loop and the HITL gate
    │   ├── Tools/                   #   ServicingTools — four reads, one gated write
    │   └── fixtures/                #   synthetic borrower packages
    │       ├── CRE-2019-0447/       #     distressed office — four documents, four breaches
    │       ├── CRE-2021-0912/       #     healthy multifamily — must produce a CLEAN report
    │       ├── adversarial/         #     rent roll carrying a prompt injection
    │       └── golden/              #     expected-extractions.json — the eval answer key
    ├── ReviewAgent.Console/         # superseded, pending deletion
    ├── section6-tool-use/
    │   └── FunctionCall/            # AIFunctionFactory — the model calls your C#
    └── section5-getting-started/
        ├── BasicAgentApp/           # follow-along scratchpad, mirrors the course repo
        ├── MultiTurn/               # AgentSession — history across turns
        ├── Streaming/               # RunStreamingAsync + AgentResponseUpdate
        ├── StructuredOutput/        # RunAsync<T> — typed results, no hand-parsing
        └── MinimalAgent/            # Aspire + DevUI (lecture 22)
            ├── AppHost/             #   orchestrator — startup project, F5 here
            ├── ServiceDefaults/     #   OTel, health, resilience — copied verbatim
            └── WebApi/              #   the only file worth typing
```

Two kinds of project live here and they serve different purposes. `CreServicing.Agent`
is the derived work — step 3 of the loop above, built from memory in the CRE domain.
The `sectionN-*` folders are throwaway follow-along scratchpads that mirror
`../develop-agents/src/sectionN-*` path-for-path, so a stuck moment is a plain
side-by-side diff. Type into them during the video; derive in the real project after.

### The line the project is built around

`Domain/Covenants.cs` and `Domain/CovenantEngine.cs` contain no model call and never
will. A language model is good at reading a scanned rent roll and reporting 83.5%
occupancy; it is the wrong tool for deciding whether 83.5% breaches an 85% floor,
because that comparison has to be reproducible and auditable and no sampled decoder
can promise either.

**The model extracts. C# decides.** Everything the agent layer adds sits upstream of
`CovenantEngine.Evaluate`, and being able to point at where I refused to let the model
decide is the single clearest signal I can send about understanding these systems.

That is also why the deterministic half runs today with no Azure call at all — the
agent layer is the part still being built, not the part holding it up.

### The human approval gate

Four of the five tools are reads and one is a write, and they are not the same kind
of thing. A bad call on `GetLoanTerms` costs a few cents. A bad call on
`CreateServicingException` puts a false covenant breach on a borrower's file, which
drives a notice, a reserve decision, and a conversation with a person. **The agent
may read freely and must ask before it writes** — sort tools by blast radius, not by
convenience. Exactly one tool is gated, deliberately: gate five and the operator
starts clicking through, and approval fatigue is the failure mode of every
human-in-the-loop system ever built.

Four things that turned out to matter more than the gate itself:

- **`ApprovalRequiredAIFunction` enforces nothing.** Its own XML doc says so — it is
  a `DelegatingAIFunction` marker. Enforcement lives in `FunctionInvokingChatClient`,
  which swaps the call for a `ToolApprovalRequestContent` and returns early. The host
  loop is what resolves it; delete the loop and the agent stalls forever.
- **Approval contaminates the whole batch.** Per the same docs: if *any* call in a
  response needs approval, *every* call in that response does, including ungated
  reads. The prompt used to say "Approve this filing?" over a `GetDocumentText`.
  `ServicingAgentHost` now derives the gated set from the tool registration itself
  and auto-resolves anything swept in — rather than taking the docs' suggestion of
  `AllowMultipleToolCalls = false`, which fixes it by paying a round trip per tool
  call across the entire run.
- **The operator sees the trace before the question.** Approving a filing on the
  strength of its five arguments alone means authorising the agent's reading of
  documents you were never shown. Each pause now prints what the agent did since the
  last one.
- **The ledger records the decision, not just the role.** Every entry carries
  time-to-decision, keyed to the specific filing it authorised. It prevents nothing
  — a determined human can still hold down `y` — but three breaches cleared in 900ms
  is a finding about the process, and without the field nobody could ever see it.

### Roadmap

Section-by-section, at the bottom of `src/CreServicing.Agent/Program.cs`, along with
the five things the course does not cover and interviews do: **evaluation** against
`fixtures/golden/`, **prompt injection** via `fixtures/adversarial/`, **cost per
package**, **OCR** for real scanned documents, and **failure routing** when
confidence is low or two documents disagree.

Package versions are pinned to match the course repo's section 5 projects
(`Azure.AI.OpenAI 2.9.0-beta.1`, `Microsoft.Agents.AI.OpenAI 1.3.0`) so the
instructor's snippets paste in without API drift. Bump them deliberately, not by accident.

## The three pillars, as they show up here

| Pillar | Where it lives in this repo |
| --- | --- |
| 1 — `Microsoft.Extensions.AI` primitives | The `IChatClient` construction in `Program.cs`. Provider swap = 3 lines |
| 2 — Microsoft Agent Framework | `AsAIAgent(...)`, and later the tools, threads, and workflow graph |
| 3 — Foundry / Azure | `AzureCliCredential` (keyless RBAC), the deployment, TPM quota, OTel |

## Cost

`gpt-5-mini` Global Standard, capped at 10K TPM: $0.25 /1M input, $2.00 /1M output.
About $0.0017 (~₹0.15) per review call. A $10/month budget alert
(`maf-course-monthly`) is armed on the resource group at 50/80/100%.

## Roadmap

See the section-by-section comment block at the bottom of `Program.cs`.
