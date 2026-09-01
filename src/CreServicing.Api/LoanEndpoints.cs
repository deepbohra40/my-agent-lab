using CreServicing.Core.Cost;
using CreServicing.Core.Data;
using CreServicing.Core.Domain;
using CreServicing.Core.Extraction;

namespace CreServicing.Api;

/// <summary>
/// Stage B: the paths with no approval step.
///
/// Everything down to <c>financial-snapshot</c> is the deterministic half of the
/// system — no model, no credential, no cost, and the same answer on every call.
/// Those endpoints are the ones that must never be unavailable, which is why the
/// API is configured to start without an Azure OpenAI endpoint rather than
/// crash-looping when one is missing.
///
/// Only the last endpoint calls a model, and it announces that in its own
/// response by reporting what it spent.
/// </summary>
public static class LoanEndpoints
{
    public static void MapLoanEndpoints(this WebApplication app)
    {
        var loans = app.MapGroup("/loans").WithTags("Loans");

        loans.MapGet("/", () => Results.Ok(
                MockServicingSystem.LoanIds
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .Select(id => LoanSummaryResponse.From(MockServicingSystem.GetLoanTerms(id)))
                    .ToList()))
            .WithSummary("Every loan in the servicing system of record.");

        loans.MapGet("/{loanId}", (string loanId) =>
                MockServicingSystem.TryGetLoanTerms(loanId, out var terms) && terms is not null
                    ? Results.Ok(LoanSummaryResponse.From(terms))
                    : NotFoundLoan(loanId))
            .WithSummary("Covenant terms for one loan, from the loan agreement.");

        // GET, not POST. It computes rather than changes: same loan, same day,
        // same findings, and nothing is written anywhere. Making it a POST because
        // it does arithmetic would be misdescribing it to every cache and proxy
        // between here and the caller.
        loans.MapGet("/{loanId}/covenant-review", (string loanId) =>
            {
                if (!MockServicingSystem.TryGetLoanTerms(loanId, out var terms) || terms is null)
                {
                    return NotFoundLoan(loanId);
                }

                var snapshot = MockServicingSystem.GetHandKeyedSnapshot(loanId);
                var reviewDate = DateOnly.FromDateTime(DateTime.Today);

                // Two clocks, deliberately. The measured covenants are tested as at
                // the period close the snapshot carries; the time-horizon ones
                // (insurance expiry, maturity, reporting lateness) are tested as at
                // today. Collapsing them into one date is how an expired policy
                // silently passes.
                var findings = CovenantEngine.Evaluate(terms, snapshot, snapshot.AsOf, reviewDate);

                return Results.Ok(new CovenantReviewResponse(
                    LoanId: loanId,
                    AsOf: snapshot.AsOf,
                    ReviewDate: reviewDate,
                    Source: "hand-keyed",
                    Snapshot: SnapshotResponse.From(snapshot),
                    Findings: findings.Select(FindingResponse.From).ToList()));
            })
            .WithSummary("Covenant findings computed in C# from the analyst's hand-keyed figures. No model, no cost.");

        loans.MapGet("/{loanId}/documents", (string loanId) =>
            {
                if (!MockServicingSystem.TryGetLoanTerms(loanId, out _))
                {
                    return NotFoundLoan(loanId);
                }

                IReadOnlyList<SourceDocument> package;
                try
                {
                    package = DocumentStore.GetPackage(loanId);
                }
                catch (DirectoryNotFoundException)
                {
                    // A real loan with no documents on file. Not an error — it is
                    // the ordinary state of a borrower who has not reported yet,
                    // and the covenant engine has a finding for exactly that.
                    package = [];
                }

                return Results.Ok(new PackageResponse(
                    loanId,
                    package.Count,
                    package.Select(DocumentSummaryResponse.From).ToList()));
            })
            .WithSummary("What the borrower submitted. File names and sizes only — never content.");

        // ── The one endpoint here that spends money ──────────────────────────
        // ── Note the IServiceProvider, which is not laziness ─────────────────
        //
        // Minimal APIs resolve every DI-injected handler parameter before the
        // handler body runs. FinancialSnapshotAssembler needs an IChatClient,
        // which needs an AzureOpenAIClient, which reads the validated options —
        // so taking it as a parameter meant an unconfigured host threw inside DI
        // and returned 500 before the availability check below could ever run.
        // The endpoint whose entire job is to answer 503 politely was the one that
        // could not.
        //
        // Resolving after the check is the fix. The alternative — an endpoint
        // filter — does not help, because parameter binding still happens first.
        loans.MapPost("/{loanId}/financial-snapshot", async (
                string loanId,
                IServiceProvider services,
                ModelAvailability availability,
                CancellationToken cancellationToken) =>
            {
                if (availability.Unavailable() is { } problem)
                {
                    return problem;
                }

                if (!MockServicingSystem.TryGetLoanTerms(loanId, out var terms) || terms is null)
                {
                    return NotFoundLoan(loanId);
                }

                var assembler = services.GetRequiredService<FinancialSnapshotAssembler>();

                var handKeyed = MockServicingSystem.GetHandKeyedSnapshot(loanId);
                var reviewDate = DateOnly.FromDateTime(DateTime.Today);

                AssembledSnapshot assembly;
                try
                {
                    // Stamped with the hand-keyed period close so the two are
                    // compared like with like — see the same note in the CLI demo.
                    assembly = await assembler.AssembleAsync(loanId, handKeyed.AsOf, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    // Extraction returned a hole. That is a 422, not a 500: the
                    // request was fine and the borrower's package was not, and the
                    // caller needs to know which field was missing.
                    return Results.Problem(
                        title: "The package could not be assembled into a snapshot.",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                var rate = ModelPricing.For(availability.Deployment);
                var usage = assembly.Cost.TotalUsage;

                return Results.Ok(new SnapshotAssemblyResponse(
                    LoanId: loanId,
                    Assembled: SnapshotResponse.From(assembly.Snapshot),
                    HandKeyed: SnapshotResponse.From(handKeyed),
                    AssembledFindings: CovenantEngine
                        .Evaluate(terms, assembly.Snapshot, assembly.Snapshot.AsOf, reviewDate)
                        .Select(FindingResponse.From).ToList(),
                    HandKeyedFindings: CovenantEngine
                        .Evaluate(terms, handKeyed, handKeyed.AsOf, reviewDate)
                        .Select(FindingResponse.From).ToList(),
                    InputTokens: usage.InputTokens,
                    OutputTokens: usage.OutputTokens,
                    Deployment: availability.Deployment,
                    PackageUsd: rate is null ? null : decimal.Round(ModelPricing.Usd(usage, rate), 6),
                    Note: "appraisedValue is expected to differ: no appraisal document exists in these "
                          + "fixtures, so the assembled path always reports LTV-UNTESTED where the "
                          + "hand-keyed path (a stale prior appraisal on file) tests LTV normally."));
            })
            .WithSummary("Extracts a snapshot from the borrower's documents and compares it with the hand-keyed one. Calls a model; costs money.");
    }

    private static IResult NotFoundLoan(string loanId) => Results.Problem(
        title: "No such loan.",
        detail: $"No loan '{loanId}' in the servicing system. Known loans: "
                + string.Join(", ", MockServicingSystem.LoanIds.OrderBy(id => id, StringComparer.Ordinal)),
        statusCode: StatusCodes.Status404NotFound);
}
