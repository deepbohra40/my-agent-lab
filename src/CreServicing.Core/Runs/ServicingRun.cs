using System.Text.Json;
using CreServicing.Core.Cost;
using CreServicing.Core.Data;

namespace CreServicing.Core.Runs;

public enum ServicingRunStatus
{
    /// <summary>Suspended at a write the agent wants to make. Nothing advances until a human answers.</summary>
    AwaitingApproval,

    /// <summary>The agent finished its turn with no outstanding approval request.</summary>
    Completed,

    /// <summary>The run threw. <see cref="ServicingRun.Error"/> says what; the ledger still says what was filed.</summary>
    Failed
}

/// <summary>One tool-call argument, flattened to a string for display and transport.</summary>
public sealed record NamedArgument(string Name, string Value);

/// <summary>
/// One entry in the tool trace. This is the run's evidence, not its narration —
/// see the note on <see cref="Agents.ServicingRunner"/> about why the trace grades
/// a run and the answer text does not.
/// </summary>
public sealed record RecordedCall(string CallId, string ToolName, IReadOnlyList<NamedArgument> Arguments);

/// <summary>
/// One approval request the run is suspended on.
///
/// <paramref name="RequiresHumanDecision"/> is the load-bearing field, and it is
/// false more often than you would expect. <c>FunctionInvokingChatClient</c>'s own
/// remarks say that if any call in a response is for an approval-required
/// function, *every* call in that response requires approval — so a model that
/// batches <c>GetDocumentText</c> alongside the filing produces two approval
/// requests, only one of which is a write. Asking a human to authorise the read is
/// asking them to rubber-stamp something the design never gated, and a prompt that
/// misdescribes what it gates trains the operator that the wording is noise.
///
/// So the runner resolves the ungated ones itself and records them here as
/// auto-approved, visible but not asked about.
///
/// <paramref name="Request"/> is the serialized <c>ToolApprovalRequestContent</c>.
/// It is kept verbatim rather than rebuilt from <paramref name="Arguments"/>
/// because the framework matches the response to the pending call by request id
/// and tool call, and a re-derived call with stringified arguments is not the same
/// call. Round-tripping the framework's own content type is the only version of
/// this that is safe.
/// </summary>
public sealed record SuspendedApproval(
    string RequestId,
    string ToolName,
    bool RequiresHumanDecision,
    string LoanId,
    string Code,
    IReadOnlyList<NamedArgument> Arguments,
    JsonElement Request);

/// <summary>
/// A human's answer to one <see cref="SuspendedApproval"/>.
///
/// <paramref name="TimeToDecision"/> is supplied by the caller rather than
/// measured here, because the only clock that means anything is the one that
/// started when the arguments went on the operator's screen. Over HTTP that is the
/// client's clock, and taking the server's would silently measure network latency
/// and call it deliberation.
/// </summary>
public sealed record ApprovalDecisionInput(string RequestId, bool Approved, TimeSpan TimeToDecision);

/// <summary>
/// One servicing review, in whatever state it currently occupies — running to
/// completion, or suspended mid-flight waiting for a human.
///
/// ── Why this type exists ─────────────────────────────────────────────────────
///
/// The console approval loop worked because <c>Console.ReadLine()</c> blocks: the
/// agent session, the tool trace, the accumulated usage and the pending request
/// all stayed alive on the stack while a person decided. Behind HTTP there is no
/// stack to hold them. The request that asks for approval must return, and a
/// different request — possibly minutes later, possibly to a different instance —
/// must pick the run up exactly where it stopped.
///
/// So everything that was implicit in that stack is written down here, and every
/// field is chosen so the whole record survives a round trip through JSON. That
/// constraint is enforced rather than hoped for: <see cref="InMemoryRunStore"/>
/// serializes on save and deserializes on load, so a live object reference
/// smuggled into this type fails a test rather than failing in production behind
/// a second instance.
///
/// <see cref="SessionState"/> is the piece that makes this more than bookkeeping.
/// MAF exposes <c>SerializeSessionAsync</c>/<c>DeserializeSessionAsync</c>, so the
/// conversation the agent has had — every document it read before it asked — is
/// storable, not merely holdable. That is the difference between a suspended run
/// and a cached one.
/// </summary>
public sealed class ServicingRun
{
    public string RunId { get; set; } = string.Empty;

    public string LoanId { get; set; } = string.Empty;

    /// <summary>Who is answering the prompts for this run. Recorded on every filing they authorise.</summary>
    public string Approver { get; set; } = string.Empty;

    public string Deployment { get; set; } = string.Empty;

    public ServicingRunStatus Status { get; set; } = ServicingRunStatus.AwaitingApproval;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The agent session, serialized. A JSON <c>null</c> — never the default
    /// <see cref="JsonElement"/>, which cannot be written — on a run that failed
    /// before its first turn produced one. See <see cref="RunSerialization.NullElement"/>.
    /// </summary>
    public JsonElement SessionState { get; set; } = RunSerialization.NullElement;

    /// <summary>What the run is currently blocked on. Empty unless <see cref="Status"/> is AwaitingApproval.</summary>
    public IReadOnlyList<SuspendedApproval> Suspended { get; set; } = [];

    /// <summary>
    /// Every tool call the agent has made across all rounds, accumulated rather
    /// than read off the last response — the calls that matter most for grading
    /// happened before the first pause.
    /// </summary>
    public IReadOnlyList<RecordedCall> Trace { get; set; } = [];

    /// <summary>What actually landed on the loan file. The record, as against the agent's account of itself.</summary>
    public IReadOnlyList<FiledException> Filed { get; set; } = [];

    /// <summary>
    /// Approvals granted but not yet spent, keyed by <see cref="ApprovalLedger.Key"/>.
    /// Normally empty between requests — a filing is executed in the same turn its
    /// approval is submitted — but persisted so that a framework that defers a
    /// call to a later round cannot lose the authorisation for it.
    /// </summary>
    public Dictionary<string, ApprovalDecision> OutstandingApprovals { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Accumulated across rounds for the same reason the trace is. Getting this
    /// wrong would understate the run rather than fail it — the quiet direction of
    /// error. The first round is the expensive one: it reads every document.
    /// </summary>
    public ModelUsage Usage { get; set; } = ModelUsage.None;

    public int ModelCalls { get; set; }

    /// <summary>How many times this run has suspended for approval.</summary>
    public int Round { get; set; }

    /// <summary>The agent's closing text. Null until the run completes.</summary>
    public string? Answer { get; set; }

    public string? Error { get; set; }

    public bool IsTerminal => Status is ServicingRunStatus.Completed or ServicingRunStatus.Failed;

    /// <summary>The subset of <see cref="Suspended"/> a human is actually being asked about.</summary>
    public IEnumerable<SuspendedApproval> AwaitingHuman
        => Suspended.Where(approval => approval.RequiresHumanDecision);
}
