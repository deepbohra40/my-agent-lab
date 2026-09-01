using Microsoft.Extensions.AI;

// Deliberately not in either test project's own namespace: this file is compiled
// into both CreServicing.Core.Tests and CreServicing.Api.Tests, and naming it
// after one of them would read as a dependency on that project from the other.
namespace CreServicing.Testing;

/// <summary>
/// An <see cref="IChatClient"/> that replays a fixed script instead of calling a
/// model.
///
/// ── Why this is worth having ─────────────────────────────────────────────────
///
/// The approval loop is the part of this system with the most ways to be subtly
/// wrong — an approval spent on the wrong filing, a resumed run that forgot what
/// it read, a duplicate submission that files twice — and until stage A converted
/// the extractors and the runner to take an injected <see cref="IChatClient"/>,
/// none of it could be tested without a live deployment and a bill.
///
/// It can now. Everything in the suspend/resume state machine is exercised here
/// with no Azure call, no credential, and no cost, which is what lets these tests
/// live in the free CI job alongside the covenant engine. What still needs a live
/// model is whether the *model* behaves — that is the eval harness's job, and it
/// is deliberately a different job with a different budget.
///
/// The script is a queue of responses, returned in order. Each entry is one round
/// trip: a tool call the framework will act on, or the final text.
/// </summary>
internal sealed class ScriptedChatClient(params ChatResponse[] script) : IChatClient
{
    private readonly Queue<ChatResponse> _script = new(script);

    /// <summary>What the pipeline actually sent, for tests that assert on continuity.</summary>
    public List<List<ChatMessage>> Received { get; } = [];

    public int Calls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Honoured rather than ignored, because a real client honours it and the
        // runner's cancellation behaviour is worth testing. A stub that quietly
        // completes cancelled work would make the run look like it succeeded.
        cancellationToken.ThrowIfCancellationRequested();

        Received.Add(messages.ToList());
        Calls++;

        if (_script.Count == 0)
        {
            throw new InvalidOperationException(
                $"The scripted client ran out of responses on call {Calls}. The pipeline made more "
                + "round trips than the test expected, which is usually the interesting part.");
        }

        return Task.FromResult(_script.Dequeue());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The servicing runner does not stream. S15 is where that changes.");

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    // ── Script builders ──────────────────────────────────────────────────────

    /// <summary>A turn in which the model asks to call one tool.</summary>
    public static ChatResponse ToolCall(
        string callId, string toolName, Dictionary<string, object?> arguments, long input = 100, long output = 20)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, arguments)]))
        {
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output }
        };

    /// <summary>A turn in which the model asks to call several tools at once — the batching case.</summary>
    public static ChatResponse ToolCalls(long input, long output, params FunctionCallContent[] calls)
        => new(new ChatMessage(ChatRole.Assistant, calls.Cast<AIContent>().ToList()))
        {
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output }
        };

    /// <summary>A turn in which the model answers.</summary>
    public static ChatResponse Text(string text, long input = 50, long output = 10)
        => new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output }
        };

    public static FunctionCallContent Call(string callId, string toolName, Dictionary<string, object?> arguments)
        => new(callId, toolName, arguments);

    /// <summary>The arguments a well-formed filing carries, so tests can vary one at a time.</summary>
    public static Dictionary<string, object?> Filing(
        string loanId = "CRE-2019-0447",
        string code = "DSCR-MIN",
        string severity = "Breach",
        string summary = "DSCR of 1.156 is below the 1.25 covenant minimum.",
        string evidence = "NOI $2,130,000 / annual debt service $1,842,000 = 1.1564")
        => new()
        {
            ["loanId"] = loanId,
            ["code"] = code,
            ["severity"] = severity,
            ["summary"] = summary,
            ["evidence"] = evidence
        };
}
