using CreServicing.Core.Domain;

namespace CreServicing.Core.Tests;

/// <summary>
/// A loan that passes every covenant test, and the snapshot that clears it.
///
/// Every test below starts here and perturbs exactly one field with a `with`
/// expression. That is deliberate: when a test named "occupancy just under the
/// floor breaches" fails, the only thing it can be about is occupancy. Tests
/// that each build their own bespoke loan drift apart, and then a failure means
/// reading forty lines of setup to find out what was different.
/// </summary>
internal static class Given
{
    /// <summary>
    /// Fixed, never DateTime.Today. Evaluate() takes the date as a parameter
    /// precisely so the insurance-expiry and maturity tests mean the same thing
    /// on every machine on every day. Passing Today here would throw that away.
    /// </summary>
    public static readonly DateOnly AsOf = new(2026, 6, 30);

    public const string LoanId = "TEST-0001";

    /// <summary>
    /// DSCR floor 1.25, LTV ceiling 0.75, occupancy floor 0.85. With the 5%
    /// watch band that puts the interesting boundaries at DSCR 1.3125,
    /// LTV 0.7125, and occupancy 0.8925.
    /// </summary>
    public static LoanTerms CompliantTerms => new(
        LoanId: LoanId,
        BorrowerName: "Test Borrower LLC",
        PropertyName: "Test Property",
        PropertyType: PropertyType.Office,
        OriginalPrincipal: 25_000_000m,
        CurrentPrincipal: 20_000_000m,
        InterestRate: 0.0525m,
        AnnualDebtService: 1_600_000m,
        MinimumDscr: 1.25m,
        MaximumLtv: 0.75m,
        MinimumOccupancy: 0.85m,
        RequiredInsuranceCoverage: 25_000_000m,
        FinancialReportingDueDays: 45,
        MaturityDate: AsOf.AddYears(5));

    /// <summary>
    /// DSCR 1.50, LTV 0.50, occupancy 0.95, insurance adequate and not expiring.
    /// Comfortably clear of every band, so a finding in any test is caused by
    /// that test's perturbation and nothing else.
    /// </summary>
    public static FinancialSnapshot CompliantSnapshot => new(
        LoanId: LoanId,
        AsOf: AsOf,
        NetOperatingIncome: 2_400_000m,
        AppraisedValue: 40_000_000m,
        OccupancyRate: 0.95m,
        InsuranceCoverage: 30_000_000m,
        InsuranceExpiration: AsOf.AddDays(300));

    /// <summary>NOI that produces exactly the DSCR asked for, given the baseline debt service.</summary>
    public static decimal NoiForDscr(decimal dscr) => dscr * CompliantTerms.AnnualDebtService;

    /// <summary>
    /// <paramref name="reviewDate"/> defaults to <paramref name="asOf"/> so the
    /// band tests — which care about DSCR/LTV/occupancy and not about clocks —
    /// stay terse. <see cref="CovenantEngine.Evaluate"/> itself requires both,
    /// deliberately: production code must not be able to conflate them by
    /// omission. The tests that care about the difference pass it explicitly.
    /// </summary>
    public static IReadOnlyList<ServicingException> Evaluate(
        LoanTerms? terms = null,
        FinancialSnapshot? snapshot = null,
        DateOnly? asOf = null,
        DateOnly? reviewDate = null)
        => CovenantEngine.Evaluate(
            terms ?? CompliantTerms,
            snapshot ?? CompliantSnapshot,
            asOf ?? AsOf,
            reviewDate ?? asOf ?? AsOf);

    /// <summary>The one finding with this code, asserting there is exactly one.</summary>
    public static ServicingException Single(this IReadOnlyList<ServicingException> findings, string code)
    {
        var matching = findings.Where(f => f.Code == code).ToList();
        Assert.True(
            matching.Count == 1,
            $"Expected exactly one '{code}' finding but got {matching.Count}. " +
            $"All findings: [{string.Join(", ", findings.Select(f => f.Code))}]");
        return matching[0];
    }

    public static void HasNo(this IReadOnlyList<ServicingException> findings, string code)
        => Assert.True(
            findings.All(f => f.Code != code),
            $"Expected no '{code}' finding, got: [{string.Join(", ", findings.Select(f => f.Code))}]");
}
