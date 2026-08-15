# my-agent-lab

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

## Structure

```
my-agent-lab/
├── my-agent-lab.slnx
└── src/
    ├── CreServicing.Agent/          # the derived project — CRE covenant compliance
    │   ├── Domain/                  #   LoanTerms, Covenants, CovenantEngine, Extractions
    │   ├── Data/                    #   MockServicingSystem (system of record), DocumentStore
    │   ├── Tools/                   #   ServicingTools — S6 scaffold, [Description] is the work
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
