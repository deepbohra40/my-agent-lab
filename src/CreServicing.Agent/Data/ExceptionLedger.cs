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
/// The <see cref="Filed"/> record carries who approved it. An exception with no
/// approver is not an audit trail, and an audit trail is the entire justification
/// for letting a model near this workflow.
/// </summary>
public static class ExceptionLedger
{
    private static readonly List<FiledException> Entries = [];

    public static IReadOnlyList<FiledException> All => Entries;

    public static FiledException File(ServicingException exception, string approvedBy, DateTimeOffset filedAt)
    {
        var entry = new FiledException(
            ReferenceNumber: $"EX-{DateTime.UtcNow:yyyyMMdd}-{Entries.Count + 1:D3}",
            Exception: exception,
            ApprovedBy: approvedBy,
            FiledAt: filedAt);

        Entries.Add(entry);
        return entry;
    }

    public static void Clear() => Entries.Clear();
}

/// <summary>One exception as it sits on the loan file, with its approval attached.</summary>
public sealed record FiledException(
    string ReferenceNumber,
    ServicingException Exception,
    string ApprovedBy,
    DateTimeOffset FiledAt);
