using System.Diagnostics;
using CreServicing.Core.Cost;
using CreServicing.Core.Data;
using CreServicing.Core.Diagnostics;
using CreServicing.Core.Domain;

namespace CreServicing.Core.Extraction;

/// <summary>
/// One assembled snapshot and what it cost to produce.
///
/// The cost rides along with the snapshot rather than being reported out of band
/// because they are the same fact viewed twice: this snapshot is worth having
/// only if that number is small enough. Separating them invites quoting the
/// accuracy without the price.
/// </summary>
public sealed record AssembledSnapshot(FinancialSnapshot Snapshot, PackageCost Cost);

/// <summary>
/// Turns the four per-document extracts into the one <see cref="FinancialSnapshot"/>
/// <see cref="Domain.CovenantEngine"/> actually tests against. This is the S5
/// milestone Program.cs's roadmap comment calls out: before this class existed,
/// that record was hand-keyed in <see cref="MockServicingSystem"/>; now it is
/// produced from the same documents an analyst would read.
///
/// Two decisions worth being explicit about, because both are easy to get wrong
/// silently:
///
///   1. NetOperatingIncome is <see cref="Covenants.NetOperatingIncome"/> applied
///      to the extracted EGI and OpEx — never
///      <see cref="OperatingStatementExtract.ReportedNetOperatingIncome"/>. That
///      field exists to capture what the borrower claims, not to feed the
///      covenant test. See <see cref="OperatingStatementExtractor"/> for the
///      fixture this guards against.
///
///   2. AppraisedValue is always null here. No document type in this pipeline is
///      an appraisal — real packages routinely arrive without a current one,
///      which is exactly the case <see cref="Domain.FinancialSnapshot.AppraisedValue"/>
///      being nullable exists for. The practical effect: an assembled snapshot
///      always produces "LTV-UNTESTED" where a hand-keyed snapshot with a stale
///      appraisal on file tests LTV normally. That is an expected divergence
///      between the two paths, not a bug — see the demo output in Program.cs.
/// </summary>
public sealed class FinancialSnapshotAssembler(
    RentRollExtractor rentRollExtractor,
    OperatingStatementExtractor operatingStatementExtractor,
    InsuranceCertificateExtractor insuranceCertificateExtractor)
{
    /// <summary>
    /// Locates each document in the loan's package by filename convention — there
    /// is no classification step in this project (Section 8 stays out of scope by
    /// design; the trade-off is worth being able to state, not worth building).
    /// The tax bill is extracted for completeness but not consumed: no covenant
    /// test in this project depends on it yet.
    /// </summary>
    public async Task<AssembledSnapshot> AssembleAsync(
        string loanId, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        using var activity = ServicingTelemetry.Assembly(loanId);

        try
        {
            return await AssembleCoreAsync(loanId, asOf, activity, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // The 422 path — a document missing from the package, an extraction
            // that returned nothing, or a required field that came back null.
            // Caught only to put the reason on the span before letting it go: the
            // endpoint still turns this into the same 422 with the same message.
            //
            // Worth the extra frame because this is the failure that costs money.
            // The extractions that already ran are billed whether or not the
            // assembly completed, and a trace that showed a bare exception would
            // hide which of the four fell over.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private async Task<AssembledSnapshot> AssembleCoreAsync(
        string loanId, DateOnly asOf, Activity? activity, CancellationToken cancellationToken)
    {
        var package = DocumentStore.GetPackage(loanId);

        var rentRollDoc = FindDocument(package, loanId, "rent-roll");
        var operatingStatementDoc = FindDocument(package, loanId, "operating-statement");
        var insuranceDoc = FindDocument(package, loanId, "insurance-certificate");

        var rentRollResult = await rentRollExtractor.ExtractAsync(rentRollDoc, cancellationToken);
        var operatingStatementResult =
            await operatingStatementExtractor.ExtractAsync(operatingStatementDoc, cancellationToken);
        var insuranceResult =
            await insuranceCertificateExtractor.ExtractAsync(insuranceDoc, cancellationToken);

        // Cost is accounted before the null checks below, so a package that fails
        // to assemble still reports what it spent failing. Money spent on a run
        // that threw is money spent.
        var cost = new PackageCost(loanId, [
            new DocumentCost(rentRollDoc.FileName, rentRollResult.Usage),
            new DocumentCost(operatingStatementDoc.FileName, operatingStatementResult.Usage),
            new DocumentCost(insuranceDoc.FileName, insuranceResult.Usage)
        ]);

        // Set before the null checks below, for the same reason the cost is
        // accounted before them: a package that fails to assemble still spent
        // this, and the span is where that is read.
        activity?.SetTag("cre.document_count", cost.DocumentCount);
        activity.SetUsage(cost.TotalUsage);

        var rentRoll = rentRollResult.Value
            ?? throw new InvalidOperationException($"Rent roll extraction returned nothing for {loanId}.");
        var operatingStatement = operatingStatementResult.Value
            ?? throw new InvalidOperationException($"Operating statement extraction returned nothing for {loanId}.");
        var insurance = insuranceResult.Value
            ?? throw new InvalidOperationException($"Insurance certificate extraction returned nothing for {loanId}.");

        var noi = Covenants.NetOperatingIncome(
            Require(operatingStatement.EffectiveGrossIncome, "effectiveGrossIncome", operatingStatementDoc),
            Require(operatingStatement.OperatingExpenses, "operatingExpenses", operatingStatementDoc));

        var occupancy = ComputeOccupancy(rentRoll, rentRollDoc);

        var insuranceExpiration = ParseDate(
            Require(insurance.ExpirationDate, "expirationDate", insuranceDoc), insuranceDoc);

        var snapshot = new FinancialSnapshot(
            LoanId: loanId,
            AsOf: asOf,
            NetOperatingIncome: noi,
            AppraisedValue: null,
            OccupancyRate: occupancy,
            InsuranceCoverage: Require(insurance.CoverageAmount, "coverageAmount", insuranceDoc),
            InsuranceExpiration: insuranceExpiration);

        return new AssembledSnapshot(snapshot, cost);
    }

    private static decimal ComputeOccupancy(RentRollExtract rentRoll, SourceDocument document)
        => (rentRoll.OccupiedSquareFeet, rentRoll.TotalRentableSquareFeet) switch
        {
            ({ } occupiedSf, { } totalSf) => Covenants.Occupancy(occupiedSf, totalSf),
            _ => Covenants.Occupancy(
                Require(rentRoll.OccupiedUnits, "occupiedUnits", document),
                Require(rentRoll.TotalUnits, "totalUnits", document))
        };

    private static SourceDocument FindDocument(
        IReadOnlyList<SourceDocument> package, string loanId, string filenameContains)
        => package.FirstOrDefault(doc => doc.FileName.Contains(filenameContains, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No document matching '{filenameContains}' in the package for {loanId}.");

    private static T Require<T>(T? value, string fieldName, SourceDocument document) where T : struct
        => value ?? throw new InvalidOperationException(
            $"{document.RelativePath}: extraction returned null for required field '{fieldName}'.");

    private static string Require(string? value, string fieldName, SourceDocument document)
        => value ?? throw new InvalidOperationException(
            $"{document.RelativePath}: extraction returned null for required field '{fieldName}'.");

    private static DateOnly ParseDate(string value, SourceDocument document)
        => DateOnly.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"{document.RelativePath}: '{value}' is not a parseable ISO date.");
}
