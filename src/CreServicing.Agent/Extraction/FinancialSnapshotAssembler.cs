using CreServicing.Agent.Data;
using CreServicing.Agent.Domain;

namespace CreServicing.Agent.Extraction;

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
public static class FinancialSnapshotAssembler
{
    /// <summary>
    /// Locates each document in the loan's package by filename convention — there
    /// is no classification step in this project (Section 8 stays out of scope by
    /// design; the trade-off is worth being able to state, not worth building).
    /// The tax bill is extracted for completeness but not consumed: no covenant
    /// test in this project depends on it yet.
    /// </summary>
    public static async Task<FinancialSnapshot> AssembleAsync(string loanId, DateOnly asOf)
    {
        var package = DocumentStore.GetPackage(loanId);

        var rentRollDoc = FindDocument(package, loanId, "rent-roll");
        var operatingStatementDoc = FindDocument(package, loanId, "operating-statement");
        var insuranceDoc = FindDocument(package, loanId, "insurance-certificate");

        var rentRoll = await RentRollExtractor.ExtractAsync(rentRollDoc)
            ?? throw new InvalidOperationException($"Rent roll extraction returned nothing for {loanId}.");
        var operatingStatement = await OperatingStatementExtractor.ExtractAsync(operatingStatementDoc)
            ?? throw new InvalidOperationException($"Operating statement extraction returned nothing for {loanId}.");
        var insurance = await InsuranceCertificateExtractor.ExtractAsync(insuranceDoc)
            ?? throw new InvalidOperationException($"Insurance certificate extraction returned nothing for {loanId}.");

        var noi = Covenants.NetOperatingIncome(
            Require(operatingStatement.EffectiveGrossIncome, "effectiveGrossIncome", operatingStatementDoc),
            Require(operatingStatement.OperatingExpenses, "operatingExpenses", operatingStatementDoc));

        var occupancy = ComputeOccupancy(rentRoll, rentRollDoc);

        var insuranceExpiration = ParseDate(
            Require(insurance.ExpirationDate, "expirationDate", insuranceDoc), insuranceDoc);

        return new FinancialSnapshot(
            LoanId: loanId,
            AsOf: asOf,
            NetOperatingIncome: noi,
            AppraisedValue: null,
            OccupancyRate: occupancy,
            InsuranceCoverage: Require(insurance.CoverageAmount, "coverageAmount", insuranceDoc),
            InsuranceExpiration: insuranceExpiration);
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
