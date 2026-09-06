using CreServicing.Core.Domain;

namespace CreServicing.Core.Data;

/// <summary>
/// Stands in for the servicing system of record. Three synthetic loans, chosen
/// to exercise all three outcomes: a distressed office asset in breach, a healthy
/// multifamily asset, and a retail asset sitting in the watch band with a near
/// maturity.
///
/// Everything in this file is fabricated. No real borrower, property, or loan.
/// That is not a limitation of the demo — it is the reason the demo can exist
/// outside a bank's network at all, and it is the right answer when an
/// interviewer asks where the data came from.
///
/// Section 14 turns this class into an MCP server, at which point the agent
/// reaches the system of record over a protocol instead of a static field. The
/// shape of the data does not change; only the transport does.
/// </summary>
public static class MockServicingSystem
{
    private static readonly Dictionary<string, LoanTerms> Loans = new[]
    {
        // Distressed office. Post-2020 occupancy erosion, stale appraisal, and an
        // insurance certificate the borrower under-bound at renewal.
        new LoanTerms(
            LoanId: "CRE-2019-0447",
            BorrowerName: "Lakeview Holdings LLC",
            PropertyName: "Lakeview Corporate Center",
            PropertyType: PropertyType.Office,
            OriginalPrincipal: 24_500_000m,
            CurrentPrincipal: 22_180_000m,
            InterestRate: 0.0625m,
            AnnualDebtService: 1_842_000m,
            MinimumDscr: 1.25m,
            MaximumLtv: 0.75m,
            MinimumOccupancy: 0.85m,
            RequiredInsuranceCoverage: 25_000_000m,
            FinancialReportingDueDays: 45,
            MaturityDate: new DateOnly(2029, 7, 1)),

        // Healthy multifamily. Everything clears; the pipeline should produce a
        // clean report, not a manufactured finding.
        new LoanTerms(
            LoanId: "CRE-2021-0912",
            BorrowerName: "Magnolia Trace Partners LP",
            PropertyName: "Magnolia Trace Apartments",
            PropertyType: PropertyType.Multifamily,
            OriginalPrincipal: 21_000_000m,
            CurrentPrincipal: 18_750_000m,
            InterestRate: 0.0475m,
            AnnualDebtService: 1_412_000m,
            MinimumDscr: 1.20m,
            MaximumLtv: 0.75m,
            MinimumOccupancy: 0.90m,
            RequiredInsuranceCoverage: 20_000_000m,
            FinancialReportingDueDays: 45,
            MaturityDate: new DateOnly(2031, 3, 1)),

        // Retail, inside every covenant but close to two of them, and maturing
        // within six months. The interesting case: nothing is broken, and the
        // asset manager still needs to hear about it.
        new LoanTerms(
            LoanId: "CRE-2018-0233",
            BorrowerName: "Brookfield Commons Retail LLC",
            PropertyName: "Brookfield Commons",
            PropertyType: PropertyType.Retail,
            OriginalPrincipal: 13_500_000m,
            CurrentPrincipal: 11_240_000m,
            InterestRate: 0.0550m,
            AnnualDebtService: 878_000m,
            MinimumDscr: 1.20m,
            MaximumLtv: 0.75m,
            MinimumOccupancy: 0.85m,
            RequiredInsuranceCoverage: 14_000_000m,
            FinancialReportingDueDays: 60,
            MaturityDate: new DateOnly(2026, 11, 1))
    }.ToDictionary(loan => loan.LoanId);

    /// <summary>
    /// What an analyst hand-keys today, after reading the borrower's package.
    ///
    /// This method is the target. By Section 6 the agent produces these values by
    /// extracting them from <c>fixtures/</c>, and this dictionary survives only as
    /// the expected answer the extraction is graded against.
    /// </summary>
    private static readonly Dictionary<string, FinancialSnapshot> HandKeyedSnapshots = new[]
    {
        new FinancialSnapshot(
            LoanId: "CRE-2019-0447",
            AsOf: new DateOnly(2026, 6, 30),
            NetOperatingIncome: 2_130_000m,      // EGI 4,520,000 less OpEx 2,390,000
            AppraisedValue: 29_000_000m,          // 2019 appraisal, never refreshed
            OccupancyRate: 0.8352m,               // 118,600 of 142,000 rentable SF
            InsuranceCoverage: 22_000_000m,
            InsuranceExpiration: new DateOnly(2026, 9, 30),
            // The borrower's own NOI line, keyed as printed and not corrected.
            // 154,000 above the computed figure: a roof membrane replacement they
            // treat as capital while leaving it inside the repairs and
            // maintenance total. An analyst keys what the document says; the
            // engine decides what to make of it.
            ReportedNetOperatingIncome: 2_284_000m),

        new FinancialSnapshot(
            LoanId: "CRE-2021-0912",
            AsOf: new DateOnly(2026, 6, 30),
            NetOperatingIncome: 1_986_000m,
            AppraisedValue: 27_400_000m,
            OccupancyRate: 0.9667m,               // 232 of 240 units
            InsuranceCoverage: 22_000_000m,
            InsuranceExpiration: new DateOnly(2027, 4, 1),
            // Ties exactly: 4,986,000 less 3,000,000. The control case — a
            // reconciliation test that fires on a clean statement is worse than
            // no reconciliation test at all.
            ReportedNetOperatingIncome: 1_986_000m),

        new FinancialSnapshot(
            LoanId: "CRE-2018-0233",
            AsOf: new DateOnly(2026, 6, 30),
            NetOperatingIncome: 1_105_000m,
            AppraisedValue: 15_100_000m,
            OccupancyRate: 0.8800m,
            InsuranceCoverage: 15_500_000m,
            InsuranceExpiration: new DateOnly(2027, 1, 15),
            // Null, and correctly so. This is the loan with no package on file —
            // the figures above came off a servicer's spreadsheet, not off a
            // statement, so there is no borrower NOI line to disagree with.
            ReportedNetOperatingIncome: null)
    }.ToDictionary(snapshot => snapshot.LoanId);

    public static IReadOnlyCollection<string> LoanIds => Loans.Keys;

    public static LoanTerms GetLoanTerms(string loanId)
        => Loans.TryGetValue(loanId, out var terms)
            ? terms
            : throw new KeyNotFoundException($"No loan {loanId} in the servicing system.");

    public static bool TryGetLoanTerms(string loanId, out LoanTerms? terms)
        => Loans.TryGetValue(loanId, out terms);

    public static FinancialSnapshot GetHandKeyedSnapshot(string loanId)
        => HandKeyedSnapshots.TryGetValue(loanId, out var snapshot)
            ? snapshot
            : throw new KeyNotFoundException($"No snapshot on file for {loanId}.");
}
