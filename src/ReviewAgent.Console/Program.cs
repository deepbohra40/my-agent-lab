using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// ── Configuration ────────────────────────────────────────────────────────────
var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
    ?? "gpt-5-mini";

// ── Pillar 1: the provider-neutral primitive ─────────────────────────────────
// Everything below depends only on IChatClient, so switching model provider is
// a change to these three lines and nothing else in the file.
IChatClient chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

// ── Pillar 2: the agent ──────────────────────────────────────────────────────
AIAgent reviewer = chatClient.AsAIAgent(
    name: "Reviewer",
    instructions: """
        You are a senior C# code reviewer.
        Report only defects that change behaviour: correctness bugs, resource leaks,
        deadlocks, race conditions, and swallowed exceptions. Ignore pure style and
        naming preferences.
        For each finding give the line, one sentence naming the defect, and the fix.
        If the code has no such defects, say so in one sentence and stop.
        """);

// ── Input: a file path from the command line, or the built-in sample ─────────
var target = args.Length > 0 ? args[0] : "built-in sample";
var code = args.Length > 0 ? await File.ReadAllTextAsync(args[0]) : SampleCode();

Console.WriteLine($"Reviewing: {target}\n");

AgentResponse response = await reviewer.RunAsync(
    $"Review this C#:\n\n```csharp\n{code}\n```");

Console.WriteLine(response.Text);

// A deliberately flawed snippet so the very first run has something to find.
static string SampleCode() => """
    public class RateCache
    {
        private readonly Dictionary<string, decimal> _rates = new();

        public decimal Get(string currency)
        {
            if (!_rates.ContainsKey(currency))
            {
                _rates[currency] = Fetch(currency).Result;
            }
            return _rates[currency];
        }

        private async Task<decimal> Fetch(string currency)
        {
            var http = new HttpClient();
            var text = await http.GetStringAsync($"https://api.example.com/rate/{currency}");
            return decimal.Parse(text);
        }
    }
    """;

// ── Roadmap: what this project grows into, section by section ────────────────
// S5  Streaming (RunStreamingAsync), multi-turn thread, typed output via RunAsync<T>
// S6  Tools: ReadFile, ListChangedFiles, RunAnalyzer  + human approval before writes
// S7  Memory: remember the reviewer persona and per-repo conventions across runs
// S8  Workflow: Triage node routes to Security / Performance / Correctness agents
// S9  Patterns: fan out reviewers in parallel, aggregate into one report
// S11 RAG: index your team's coding standards in Qdrant, ground findings in them
// S13 A2A: expose the reviewer so another agent can call it
// S14 MCP: consume the GitHub MCP server to review a real PR
// S15 AG-UI: put a chat front end on it
