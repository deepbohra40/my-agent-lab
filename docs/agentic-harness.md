# The Agentic Harness

This project's agentic harness is the model-driven loop that reads a
borrower's reporting package and decides its own next step — while the
covenant verdict stays in deterministic C#.

## What is an agentic harness

A one-shot LLM call takes an input and returns an output: you hand the
model a document and it answers once. An agentic harness is the model
plus a set of tools plus a loop. The model is given a goal, chooses a
tool to call, sees the result, and chooses again — round after round —
until it decides it is done. The harness is the code that owns that
loop: it registers the tools, runs the model, executes the tool calls it
requests, and feeds the results back.

## The harness in this project

The real loop lives in
`src/CreServicing.Agent/Agents/ServicingAgentHost.cs`. It is handed a
loan id and nothing else; which documents exist, which are worth opening,
and what to do with what it finds are all the model's calls.

**Tools.** Five functions are registered on the agent. Four are reads and
go in raw:

- `GetLoanTerms` — the authoritative covenant thresholds and property type.
- `ListPackageDocuments` — the file names in the borrower's package, no content.
- `GetDocumentText` — the full text of one document, fenced as untrusted data.
- `EvaluateCovenants` — runs the covenant tests over extracted figures and
  returns the findings.

The fifth, `CreateServicingException`, is the one write. It is wrapped in
`ApprovalRequiredAIFunction` at the point of registration. That
asymmetry — reads raw, the write gated — is the design: sort tools by
blast radius, and let the tool surface express it rather than a branch
buried in the host.

**The session.** The host creates an `AgentSession` before the run
starts. The session exists because the run has to survive being paused:
without it the agent would lose everything it read before asking for
approval, and the human would be approving a filing the agent could no
longer justify.

**The loop.** After the first `RunAsync`, the host enters a `while (true)`
loop. Each iteration collects any `ToolApprovalRequestContent` from the
last response. If there are none, the loop breaks and the run is done. If
there are approval requests, the host prints what the agent did since the
last pause, resolves each request, and calls `RunAsync` again with the
decisions — so the run resumes inside the same session. A package with
three breaches pauses three times.

**The human-in-the-loop gate.** A gated call prints the tool name and
every argument in full, then asks `Approve this filing? [y/N]:` and reads
a line from the console. Only `y` approves; anything else rejects and
nothing is written. A subtlety the host handles: the framework requires
approval for *every* call batched alongside a gated one, including reads,
so a read swept into an approval round is auto-approved and said out loud
rather than presented as a filing to authorise.

**Cost accounting.** Token usage and a model-call count are accumulated
across rounds, not read off the final response — `usage +=
response.ToModelUsage()` and `modelCalls++` after each resume. The first
round is the expensive one because it reads every document, and the calls
that matter most for grading happen before the first pause. A cost report
is printed at the end.

## The guiding principle: "The model extracts. C# decides."

The harness and the extractors sit *upstream* of the deterministic
covenant engine in `src/CreServicing.Agent/Domain/CovenantEngine.cs`. That
file contains no model call. It takes loan terms plus a financial
snapshot and returns findings under a fixed set of codes — `DSCR-MIN`,
`LTV-MAX`, `LTV-UNTESTED`, `OCC-MIN`, `INS-COVERAGE`, `INS-EXPIRY`,
`MATURITY`. Same terms plus same snapshot, same findings, every run. The
model supplies numbers; C# decides whether they breach. That is what
makes the verdict reproducible and auditable.

**Structure beats instruction.** An earlier version of `EvaluateCovenants`
took occupancy as a single pre-divided decimal. On the first live run the
model handed back `0.835915` for `118,600 / 142,000` — the true value is
`0.835211`. It did the division in its head and missed in the fourth
decimal. Both figures happen to breach the 85% floor, so the verdict
survived; that was luck, not design. The fix was not a sterner prompt. It
was structural: change the tool signature to take the raw operands
(occupied space and total space) so the model cannot hand over a quotient
at all — the C# does the arithmetic. A model cannot get the math wrong on
a step it is no longer allowed to take.

## How to run it

All commands run from the repo root against
`src/CreServicing.Agent`.

- **Default — deterministic, offline, free.**
  `dotnet run --project src/CreServicing.Agent`
  Runs the covenant tests over hand-keyed snapshots and prints the
  exception report. No Azure call, no cost, no API key.
- **`--extract`** — run one extractor over one document.
  `dotnet run --project src/CreServicing.Agent -- --extract`
  Calls a model, so it needs a credential and costs money. Takes an
  optional document path.
- **`--extract-snapshot <loanId>`** — assemble a full `FinancialSnapshot`
  from a loan's documents and compare it against the hand-keyed one.
  `dotnet run --project src/CreServicing.Agent -- --extract-snapshot CRE-2019-0447`
- **`--agent <loanId>`** — the agentic harness described above.
  `dotnet run --project src/CreServicing.Agent -- --agent CRE-2019-0447`
  Costs the most: the tool loop is several round trips per package.

The loan id is optional on the last two and defaults to `CRE-2019-0447`.

## Read the code in this order

1. `src/CreServicing.Agent/Program.cs` — the entry point and CLI modes;
   see how the free default path stays free.
2. `src/CreServicing.Agent/Domain/CovenantEngine.cs` — the deterministic
   verdict, and the codes it can emit.
3. `src/CreServicing.Agent/Tools/ServicingTools.cs` — the tool surface the
   model sees; the `[Description]` strings are the real work.
4. `src/CreServicing.Agent/Agents/ServicingAgentHost.cs` — the loop, the
   session, and the approval gate.

## Course provenance

This began as a Microsoft Agent Framework course build-along. The
section 5 and section 6 scratchpads still live under
`src/section5-getting-started/` and `src/section6-tool-use/`. From there
it diverged into the commercial-real-estate covenant domain: the
extractors, the covenant engine, and the gated servicing agent are the
course's ideas applied to a problem where the difference between "the
model extracts" and "the model decides" has legal consequences.
