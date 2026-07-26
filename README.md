# my-agent-lab

My own build-along project for the Udemy course *Agentic AI Development with Agent Framework, MCP and .NET*.
Kept deliberately **outside** the instructor's clone (`../develop-agents`, remote `mehmetozkaya/develop-agents`)
so that `git pull` there never collides with my work.

**Domain: an automated C# code reviewer.** Chosen because it gives every section
something real to bite on — tools that read files, documents to ground answers in,
specialist agents worth routing between, and a write action that genuinely warrants
a human approval step. Deliberately *not* IT-support ticket triage, which is what the
course samples use, so I derive the structure instead of copying it.

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
# Review the built-in flawed sample
dotnet run --project src/ReviewAgent.Console

# Review a real file
dotnet run --project src/ReviewAgent.Console -- path/to/File.cs
```

## Structure

```
my-agent-lab/
├── my-agent-lab.slnx
└── src/
    └── ReviewAgent.Console/     # S5: one agent, one call
```

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
