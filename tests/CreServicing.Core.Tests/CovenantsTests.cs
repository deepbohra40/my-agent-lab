using CreServicing.Core.Domain;

namespace CreServicing.Core.Tests;

/// <summary>
/// The four arithmetic primitives. Small enough to look obviously correct, which
/// is exactly why the divide-by-zero guards are worth pinning: a zero denominator
/// is not a rounding problem, it is a decimal.DivideByZeroException in the middle
/// of a covenant run, or worse, a silently absurd ratio.
/// </summary>
public class CovenantsTests
{
    [Fact]
    public void Net_operating_income_is_income_less_expenses()
        => Assert.Equal(2_130_000m, Covenants.NetOperatingIncome(3_480_000m, 1_350_000m));

    [Fact]
    public void Dscr_divides_noi_by_annual_debt_service()
        => Assert.Equal(1.5m, Covenants.DebtServiceCoverageRatio(2_400_000m, 1_600_000m));

    [Fact]
    public void Ltv_divides_principal_by_appraised_value()
        => Assert.Equal(0.5m, Covenants.LoanToValue(20_000_000m, 40_000_000m));

    [Fact]
    public void Occupancy_divides_occupied_by_total()
        => Assert.Equal(0.835m, Covenants.Occupancy(83.5m, 100m));

    [Fact]
    public void Occupancy_takes_operands_in_the_order_occupied_then_total()
    {
        // The operand order is the whole risk in this function: 83.5/100 and
        // 100/83.5 are both plausible-looking numbers, and only one of them is
        // occupancy. An inverted pair here reads as 119% occupancy — which the
        // covenant test would pass, cheerfully, forever.
        var occupancy = Covenants.Occupancy(occupied: 172m, total: 200m);

        Assert.Equal(0.86m, occupancy);
        Assert.True(occupancy <= 1m, "Occupancy above 1.0 means the operands are inverted.");
    }

    [Fact]
    public void Dscr_with_zero_debt_service_throws_rather_than_dividing()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Covenants.DebtServiceCoverageRatio(2_400_000m, 0m));

        Assert.Equal("annualDebtService", ex.ParamName);
    }

    [Fact]
    public void Ltv_with_a_zero_appraisal_throws_rather_than_dividing()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Covenants.LoanToValue(20_000_000m, 0m));

        Assert.Equal("appraisedValue", ex.ParamName);
    }

    [Fact]
    public void Occupancy_of_an_empty_property_throws_rather_than_dividing()
    {
        var ex = Assert.Throws<ArgumentException>(() => Covenants.Occupancy(0m, 0m));

        Assert.Equal("total", ex.ParamName);
    }
}
