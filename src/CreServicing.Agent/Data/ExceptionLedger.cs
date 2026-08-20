using CreServicing.Agent.Domain;

namespace CreServicing.Agent.Data;

/// <summary>
/// Where filed servicing exceptions land. In-memory, because the point of this
/// class is not persistence — it is that a write target exists at all.
///
/// Everything else the agent touches is a read. This is the one place where the
/// run leaves a mark, and in a real servicer that mark is consequential: an
/// exception on a loan file drives a notice to the borrower, a reserve decision,
/// and eventually a workout conversation. It is the reason CreateServicingException
/// is approval-gated and the four reads are not.
///
/// The <see cref="FiledException"/> record carries who approved it. An exception
/// with no approver is not an audit trail, and an audit trail is the entire
/// justification for letting a model near this workflow.
/// </summary>
public static class ExceptionLedger
{
    private static readonly List<FiledException> Entries = [];

    public static IReadOnlyList<FiledException> All => Entries;

    public static FiledException File(
        ServicingException exception,
        ApprovalDecision? approval,
        DateTimeOffset filedAt)
    {
        var entry = new FiledException(
            ReferenceNumber: $"EX-{DateTime.UtcNow:yyyyMMdd}-{Entries.Count + 1:D3}",
            Exception: exception,
            ApprovedBy: approval?.ApprovedBy ?? FiledException.Unattributed,
            TimeToDecision: approval?.TimeToDecision,
            FiledAt: filedAt);

        Entries.Add(entry);
        return entry;
    }

    public static void Clear() => Entries.Clear();
}

/// <summary>
/// One exception as it sits on the loan file, with its approval attached.
///
/// <paramref name="TimeToDecision"/> is how long the operator took between being
/// shown the arguments and answering. It prevents nothing — a determined human
/// can still hold down <c>y</c> — but it makes having done so *visible* in the
/// record, which is the difference between an audit trail and a formality. A
/// three-breach package cleared in 900ms is a finding about the process, and
/// without this field nobody could ever see it.
/// </summary>
public sealed record FiledException(
    string ReferenceNumber,
    ServicingException Exception,
    string ApprovedBy,
    TimeSpan? TimeToDecision,
    DateTimeOffset FiledAt)
{
    /// <summary>
    /// Recorded when a filing arrives with no approval keyed to it. Not an error
    /// path to be silenced — <see cref="ApprovalContext"/> hands out one approval
    /// per filing and consumes it, so this value means the write reached the
    /// ledger without passing the gate. Worth seeing, loudly.
    /// </summary>
    public const string Unattributed = "UNATTRIBUTED";

    public bool IsAttributed => ApprovedBy != Unattributed;
}
