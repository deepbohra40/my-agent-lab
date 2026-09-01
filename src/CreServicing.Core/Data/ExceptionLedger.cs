using CreServicing.Core.Domain;

namespace CreServicing.Core.Data;

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
///
/// ── Why this stopped being static ────────────────────────────────────────────
///
/// It was a <c>static List&lt;FiledException&gt;</c>, which worked precisely as
/// long as the process ran one review at a time — true of a console app and false
/// of a web host the moment two operators review two loans at once. The failure
/// mode was not a crash: it was loan A's exceptions appearing on loan B's report,
/// and an unsynchronised <c>List</c> being appended from two request threads.
///
/// The fix is not a lock around a static. It is that the ledger is now *owned by a
/// run*, so two runs cannot see each other's filings at all. One instance per
/// <see cref="Runs.ServicingRun"/>, handed to the tools that run's agent was
/// built with. The lock below covers the narrower remaining case — the framework
/// may invoke batched tool calls concurrently within a single run.
///
/// <see cref="Restore"/> exists because a run behind HTTP is reconstituted from
/// storage on every request, and a ledger that forgot the first request's filings
/// would report the last round's work as the whole run.
/// </summary>
public sealed class ExceptionLedger
{
    private readonly List<FiledException> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<FiledException> All
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public FiledException File(
        ServicingException exception,
        ApprovalDecision? approval,
        DateTimeOffset filedAt)
    {
        lock (_gate)
        {
            var entry = new FiledException(
                ReferenceNumber: $"EX-{filedAt.UtcDateTime:yyyyMMdd}-{_entries.Count + 1:D3}",
                Exception: exception,
                ApprovedBy: approval?.ApprovedBy ?? FiledException.Unattributed,
                TimeToDecision: approval?.TimeToDecision,
                FiledAt: filedAt);

            _entries.Add(entry);
            return entry;
        }
    }

    /// <summary>
    /// Reloads entries filed during earlier rounds of the same run. Replaces
    /// rather than appends: the caller is restoring a known state, not merging.
    /// </summary>
    public void Restore(IEnumerable<FiledException> entries)
    {
        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(entries);
        }
    }
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
    /// path to be silenced — <see cref="ApprovalLedger"/> hands out one approval
    /// per filing and consumes it, so this value means the write reached the
    /// ledger without passing the gate. Worth seeing, loudly.
    /// </summary>
    public const string Unattributed = "UNATTRIBUTED";

    public bool IsAttributed => ApprovedBy != Unattributed;
}
