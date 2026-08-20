namespace CreServicing.Agent.Data;

/// <summary>
/// Carries the human approvals granted during one agent run, so the ledger can
/// record who authorised each filing and how long they took to decide.
///
/// ── Why this is keyed rather than ambient ────────────────────────────────────
///
/// The first version of this was a single <c>AsyncLocal&lt;string&gt;</c> holding
/// the operator's name, set once before the run. That is wrong as soon as a
/// package produces more than one finding: three exceptions get approved in
/// separate prompts, execute in the same resumed turn, and all three read back
/// the same ambient value. The ledger then says all three were approved
/// identically, which is not what happened.
///
/// So an approval is keyed to the filing it authorised — loan id plus finding
/// code — and <see cref="TakeApproval"/> *consumes* it. One approval authorises
/// exactly one write. A second filing under the same code finds nothing and
/// lands in the ledger as <see cref="FiledException.Unattributed"/> rather than
/// silently inheriting the first one's approval.
///
/// ── What this still is not ───────────────────────────────────────────────────
///
/// An <c>AsyncLocal</c> is a shortcut, and naming it as one is the honest move:
/// it works because a console app has one operator and one run in flight. Behind
/// an HTTP endpoint the approval has to be persisted with the suspended run and
/// resumed across two requests, which is a real design problem and not a
/// find-and-replace. What does not change is the requirement underneath it —
/// "the agent filed this" is not an acceptable audit entry, and the file has to
/// say which human authorised it.
/// </summary>
public static class ApprovalContext
{
    private sealed class RunState
    {
        public required string Approver { get; init; }

        public Dictionary<string, ApprovalDecision> Decisions { get; } = new(StringComparer.Ordinal);
    }

    private static readonly AsyncLocal<RunState?> Current = new();

    /// <summary>
    /// Opens a fresh approval record for one run. Must be called before the run
    /// starts: the state object is mutated in place afterwards, so the reference
    /// has to be established on the calling context for it to flow down into the
    /// tool invocations.
    /// </summary>
    public static void BeginRun(string approver) => Current.Value = new RunState { Approver = approver };

    /// <summary>Records that a human authorised one specific filing.</summary>
    public static void RecordApproval(string loanId, string code, TimeSpan timeToDecision)
    {
        if (Current.Value is not { } state)
        {
            return;
        }

        state.Decisions[Key(loanId, code)] = new ApprovalDecision(state.Approver, timeToDecision);
    }

    /// <summary>
    /// Returns the approval for this filing and removes it, so it cannot
    /// authorise a second write. Null means no human approved this exact filing.
    /// </summary>
    public static ApprovalDecision? TakeApproval(string loanId, string code)
        => Current.Value is { } state && state.Decisions.Remove(Key(loanId, code), out var decision)
            ? decision
            : null;

    private static string Key(string loanId, string code) => $"{loanId}|{code}";
}

/// <summary>One human decision: who made it, and how long they spent making it.</summary>
public sealed record ApprovalDecision(string ApprovedBy, TimeSpan TimeToDecision);
