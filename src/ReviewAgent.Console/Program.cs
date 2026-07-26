using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ── Configuration ────────────────────────────────────────────────────────────
var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
    ?? "gpt-5-mini";

// Set OTEL_EXPORTER_OTLP_ENDPOINT (e.g. http://localhost:4317) to ship traces to
// the Aspire dashboard instead of dumping them to stdout.
var otlp = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

// ── Pillar 3: observability ──────────────────────────────────────────────────
// MEAI emits spans and metrics following the OpenTelemetry GenAI semantic
// conventions, so token counts and latency come from the framework, not from
// bookkeeping code you write yourself.
var resource = ResourceBuilder.CreateDefault().AddService("ReviewAgent.Console");

using var traces = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddSource("*")
    .AddConsoleExporter()
    .AddOtlpExporterIf(otlp)
    .Build();

using var metrics = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter("*")
    .AddConsoleExporter()
    .AddOtlpExporterIf(otlp)
    .Build();

// ── Pillar 1: the provider-neutral primitive ─────────────────────────────────
// Everything below depends only on IChatClient, so switching model provider is
// a change to these lines and nothing else in the file.
// EnableSensitiveData logs prompts and completions — fine locally, never in prod.
IChatClient chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
    .GetChatClient(deployment)
    .AsIChatClient()
    .AsBuilder()
    .UseOpenTelemetry(configure: o => o.EnableSensitiveData = true)
    .Build();

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

// ── Cost of this single call, computed from the reported token usage ─────────
PrintCost(response);

static void PrintCost(AgentResponse response)
{
    // South India, gpt-5-mini, Global Standard (USD per 1M tokens).
    const decimal InputPer1M = 0.25m;
    const decimal OutputPer1M = 2.00m;
    const decimal UsdToInr = 88m;

    var usage = response.Usage;
    if (usage is null)
    {
        Console.WriteLine("\n[cost] No usage reported on this response.");
        return;
    }

    var inTok = usage.InputTokenCount ?? 0;
    var outTok = usage.OutputTokenCount ?? 0;
    var usd = (inTok / 1_000_000m * InputPer1M) + (outTok / 1_000_000m * OutputPer1M);

    Console.WriteLine($"""

        [cost] input  {inTok,7:N0} tokens
        [cost] output {outTok,7:N0} tokens  (includes hidden reasoning tokens)
        [cost] total  {usd,10:F6} USD  ≈ ₹{usd * UsdToInr:F4}
        """);
}

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

// Small helpers so the pipeline setup above stays readable.
static class OtelExtensions
{
    public static TracerProviderBuilder AddOtlpExporterIf(
        this TracerProviderBuilder builder, string? otlpEndpoint) =>
        string.IsNullOrWhiteSpace(otlpEndpoint) ? builder : builder.AddOtlpExporter();

    public static MeterProviderBuilder AddOtlpExporterIf(
        this MeterProviderBuilder builder, string? otlpEndpoint) =>
        string.IsNullOrWhiteSpace(otlpEndpoint) ? builder : builder.AddOtlpExporter();
}
