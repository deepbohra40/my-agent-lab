using CreServicing.Core.Cost;
using CreServicing.Core.Data;
using CreServicing.Core.Domain;
using CreServicing.Core.Runs;

namespace CreServicing.Api;

/// <summary>
/// What the API says, as against what it stores.
///
/// <see cref="ServicingRun"/> is not returned directly, and the reason is not
/// layering dogma. It carries two things a client must never see: the serialized
/// agent session, which is the entire conversation including every document the
/// agent read, and the serialized approval requests, which are framework
/// internals. Both are large, both are storage details, and returning them would
/// make the wire format an accidental copy of an implementation detail that stage
/// C is explicitly free to change.
///
/// What is left is the part an operator or a queue UI actually needs.
/// </summary>
public sealed record RunResponse(
    string RunId,
    string LoanId,
    string Status,
    string Approver,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    int Round,
    IReadOnlyList<PendingApprovalResponse> AwaitingApproval,
    IReadOnlyList<AutoApprovedResponse> AutoApproved,
    IReadOnlyList<TraceEntryResponse> Trace,
    IReadOnlyList<FiledExceptionResponse> Filed,
    RunCostResponse Cost,
    string? Answer,
    string? Error)
{
    public static RunResponse From(ServicingRun run) => new(
        RunId: run.RunId,
        LoanId: run.LoanId,
        Status: run.Status.ToString(),
        Approver: run.Approver,
        StartedAt: run.StartedAt,
        UpdatedAt: run.UpdatedAt,
        Round: run.Round,
        AwaitingApproval: run.AwaitingHuman.Select(PendingApprovalResponse.From).ToList(),
        // Surfaced rather than hidden. These are calls the framework swept into an
        // approval round by batching and the runner resolved without asking; an
        // operator reviewing the audit trail is entitled to see that happened and
        // that it was a read.
        AutoApproved: run.Suspended
            .Where(pending => !pending.RequiresHumanDecision)
            .Select(pending => new AutoApprovedResponse(pending.ToolName, "read-only tool, not approval-gated"))
            .ToList(),
        Trace: run.Trace.Select(TraceEntryResponse.From).ToList(),
        Filed: run.Filed.Select(FiledExceptionResponse.From).ToList(),
        Cost: RunCostResponse.From(run),
        Answer: run.Answer,
        Error: run.Error);
}

/// <summary>
/// One question the run is asking. Every argument is included, never truncated —
/// the same rule the console prompt follows, for the same reason: a summary the
/// operator has to trust is not a control.
/// </summary>
public sealed record PendingApprovalResponse(
    string RequestId,
    string Tool,
    IReadOnlyDictionary<string, string> Arguments)
{
    public static PendingApprovalResponse From(SuspendedApproval pending) => new(
        pending.RequestId,
        pending.ToolName,
        pending.Arguments.ToDictionary(argument => argument.Name, argument => argument.Value));
}

public sealed record AutoApprovedResponse(string Tool, string Reason);

public sealed record TraceEntryResponse(string Tool, IReadOnlyDictionary<string, string> Arguments)
{
    public static TraceEntryResponse From(RecordedCall call) => new(
        call.ToolName,
        call.Arguments.ToDictionary(argument => argument.Name, argument => argument.Value));
}

public sealed record FiledExceptionResponse(
    string ReferenceNumber,
    string LoanId,
    string Code,
    string Severity,
    string Summary,
    string Evidence,
    string ApprovedBy,
    double? TimeToDecisionSeconds,
    bool IsAttributed,
    DateTimeOffset FiledAt)
{
    public static FiledExceptionResponse From(FiledException entry) => new(
        entry.ReferenceNumber,
        entry.Exception.LoanId,
        entry.Exception.Code,
        entry.Exception.Severity.ToString(),
        entry.Exception.Summary,
        entry.Exception.Evidence,
        entry.ApprovedBy,
        entry.TimeToDecision?.TotalSeconds,
        // Redundant with ApprovedBy, and worth the redundancy. A client filtering
        // an exception queue should not have to know the magic string.
        entry.IsAttributed,
        entry.FiledAt);
}

/// <summary>
/// What the run has cost so far. Present on every response, including the ones
/// that suspend, because a run that has paused three times has already spent the
/// money for three rounds and the operator deciding whether to continue should be
/// looking at that number.
/// </summary>
public sealed record RunCostResponse(
    long InputTokens,
    long OutputTokens,
    int ModelCalls,
    int ToolCalls,
    string Deployment,
    decimal? Usd)
{
    public static RunCostResponse From(ServicingRun run)
    {
        // Null rather than zero when the deployment is not in the pricing table.
        // A confident $0.00 on an unpriced model is the kind of wrong number that
        // gets quoted in a business case.
        var rate = ModelPricing.For(run.Deployment);

        return new RunCostResponse(
            run.Usage.InputTokens,
            run.Usage.OutputTokens,
            run.ModelCalls,
            run.Trace.Count,
            run.Deployment,
            rate is null ? null : decimal.Round(ModelPricing.Usd(run.Usage, rate), 6));
    }
}

// ── Requests ─────────────────────────────────────────────────────────────────

/// <param name="Approver">
/// Who is answering. Supplied by the caller here because this project has no
/// authentication — in a real deployment it comes from the authenticated
/// principal and is never client-supplied, since the whole value of the field is
/// that the operator cannot choose what it says.
/// </param>
public sealed record StartRunRequest(string LoanId, string? Approver);

/// <param name="TimeToDecisionSeconds">
/// How long the operator looked at the request before answering, measured by the
/// client. It has to be the client's clock: the interval that means something
/// started when the arguments appeared on the operator's screen, and measuring it
/// server-side would time the network round trip and record it as deliberation.
/// </param>
public sealed record ApprovalDecisionRequest(string RequestId, bool Approved, double TimeToDecisionSeconds);

public sealed record SubmitApprovalsRequest(IReadOnlyList<ApprovalDecisionRequest> Decisions);

// ── Deterministic path ───────────────────────────────────────────────────────

public sealed record LoanSummaryResponse(
    string LoanId,
    string BorrowerName,
    string PropertyName,
    string PropertyType,
    decimal CurrentPrincipal,
    decimal MinimumDscr,
    decimal MaximumLtv,
    decimal MinimumOccupancy,
    DateOnly MaturityDate)
{
    public static LoanSummaryResponse From(LoanTerms terms) => new(
        terms.LoanId,
        terms.BorrowerName,
        terms.PropertyName,
        terms.PropertyType.ToString(),
        terms.CurrentPrincipal,
        terms.MinimumDscr,
        terms.MaximumLtv,
        terms.MinimumOccupancy,
        terms.MaturityDate);
}

public sealed record FindingResponse(
    string Code,
    string Severity,
    string Summary,
    string Evidence,
    string? ClauseCitation)
{
    public static FindingResponse From(ServicingException finding) => new(
        finding.Code,
        finding.Severity.ToString(),
        finding.Summary,
        finding.Evidence,
        // Null until Section 11 grounds the finding in the loan agreement. Kept in
        // the contract now so filling it later is not a breaking change.
        finding.ClauseCitation);
}

/// <param name="Source">
/// Which snapshot the findings were computed from — the analyst's hand-keyed
/// figures, or ones extracted from the borrower's documents. The two can disagree,
/// and a response that did not say which one it used would make that
/// undiagnosable.
/// </param>
public sealed record CovenantReviewResponse(
    string LoanId,
    DateOnly AsOf,
    DateOnly ReviewDate,
    string Source,
    SnapshotResponse Snapshot,
    IReadOnlyList<FindingResponse> Findings);

public sealed record SnapshotResponse(
    decimal NetOperatingIncome,
    decimal? AppraisedValue,
    decimal OccupancyRate,
    decimal InsuranceCoverage,
    DateOnly InsuranceExpiration)
{
    public static SnapshotResponse From(FinancialSnapshot snapshot) => new(
        snapshot.NetOperatingIncome,
        snapshot.AppraisedValue,
        snapshot.OccupancyRate,
        snapshot.InsuranceCoverage,
        snapshot.InsuranceExpiration);
}

public sealed record DocumentSummaryResponse(string FileName, string RelativePath, int ApproximateTokens)
{
    public static DocumentSummaryResponse From(SourceDocument document) => new(
        document.FileName,
        document.RelativePath.Replace('\\', '/'),
        document.ApproximateTokens);
}

public sealed record PackageResponse(
    string LoanId,
    int DocumentCount,
    IReadOnlyList<DocumentSummaryResponse> Documents);

/// <param name="PackageUsd">
/// What the extraction cost. The reason this endpoint reports it at all: an
/// extraction pipeline that is accurate and costs more per package than the
/// analyst it replaces is a demo, not a system.
/// </param>
public sealed record SnapshotAssemblyResponse(
    string LoanId,
    SnapshotResponse Assembled,
    SnapshotResponse HandKeyed,
    IReadOnlyList<FindingResponse> AssembledFindings,
    IReadOnlyList<FindingResponse> HandKeyedFindings,
    long InputTokens,
    long OutputTokens,
    string Deployment,
    decimal? PackageUsd,
    string Note);
