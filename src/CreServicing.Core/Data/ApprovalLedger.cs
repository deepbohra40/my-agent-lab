namespace CreServicing.Core.Data;

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
/// code — and <see cref="Take"/> *consumes* it. One approval authorises exactly
/// one write. A second filing under the same code finds nothing and lands in the
/// ledger as <see cref="FiledException.Unattributed"/> rather than silently
/// inheriting the first one's approval.
///
/// ── Why this stopped being AsyncLocal ────────────────────────────────────────
///
/// The predecessor was a static <c>AsyncLocal&lt;RunState&gt;</c> whose own
/// summary called it a shortcut that worked because a console app has one
/// operator and one run in flight. Behind HTTP neither holds. Worse, the failure
/// is silent in the dangerous direction: execution context does not flow the way
/// you expect across an await that resumes on a different request, so the tool
/// would find no approval and file the exception as UNATTRIBUTED — a real write,
/// recorded as unauthorised, on a borrower's file.
///
/// It is now an ordinary object owned by a <see cref="Runs.ServicingRun"/> and
/// handed to that run's tools by constructor. Nothing is ambient, so nothing can
/// leak between runs or fail to flow into one.
///
/// The approvals survive between HTTP requests because the run record persists
/// them — see <see cref="Outstanding"/> and <see cref="Restore"/>. That matters
/// for the case where the framework defers a filing to a later round than the one
/// its approval was granted in.
/// </summary>
public sealed class ApprovalLedger
{
    private readonly Dictionary<string, ApprovalDecision> _decisions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ApprovalLedger(string approver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);
        Approver = approver;
    }

    /// <summary>Who is answering the prompts. Recorded on every exception they authorise.</summary>
    public string Approver { get; }

    /// <summary>Records that a human authorised one specific filing.</summary>
    public void Record(string loanId, string code, TimeSpan timeToDecision)
    {
        lock (_gate)
        {
            _decisions[Key(loanId, code)] = new ApprovalDecision(Approver, timeToDecision);
        }
    }

    /// <summary>
    /// Returns the approval for this filing and removes it, so it cannot
    /// authorise a second write. Null means no human approved this exact filing.
    /// </summary>
    public ApprovalDecision? Take(string loanId, string code)
    {
        lock (_gate)
        {
            return _decisions.Remove(Key(loanId, code), out var decision) ? decision : null;
        }
    }

    /// <summary>
    /// Approvals granted but not yet spent, so they can be persisted with a
    /// suspended run and be waiting when it resumes.
    /// </summary>
    public IReadOnlyDictionary<string, ApprovalDecision> Outstanding
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, ApprovalDecision>(_decisions, StringComparer.Ordinal);
            }
        }
    }

    public void Restore(IReadOnlyDictionary<string, ApprovalDecision> outstanding)
    {
        lock (_gate)
        {
            _decisions.Clear();
            foreach (var (key, decision) in outstanding)
            {
                _decisions[key] = decision;
            }
        }
    }

    public static string Key(string loanId, string code) => $"{loanId}|{code}";
}

/// <summary>One human decision: who made it, and how long they spent making it.</summary>
public sealed record ApprovalDecision(string ApprovedBy, TimeSpan TimeToDecision);
