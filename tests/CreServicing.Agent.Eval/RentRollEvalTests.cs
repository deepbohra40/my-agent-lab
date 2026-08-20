using CreServicing.Agent.Data;
using CreServicing.Agent.Extraction;

namespace CreServicing.Agent.Eval;

/// <summary>
/// Field-level accuracy for <see cref="RentRollExtractor"/> against the two clean
/// fixtures in fixtures/golden/. The adversarial rent roll gets its own class —
/// see <see cref="AdversarialEvalTests"/> — because it is graded on pass criteria,
/// not field equality.
/// </summary>
[Trait("Category", "Eval")]
public class RentRollEvalTests
{
    [Fact]
    public Task Office_rent_roll_extracts_correctly()
        => AssertMatchesGolden("CRE-2019-0447/rent-roll-2026-Q2.txt");

    [Fact]
    public Task Multifamily_rent_roll_extracts_correctly()
        => AssertMatchesGolden("CRE-2021-0912/rent-roll-2026-Q2.txt");

    private static async Task AssertMatchesGolden(string relativePath)
    {
        var document = DocumentStore.Load(relativePath);
        var expected = GoldenSet.Entry(relativePath).Expected();

        var extract = (await EvalHost.Resolve<RentRollExtractor>().ExtractAsync(document)).Value;

        Assert.NotNull(extract);
        Assert.Equal(expected.String("asOf"), extract!.AsOf);
        Assert.Equal(expected.Int("totalUnits"), extract.TotalUnits);
        Assert.Equal(expected.Int("occupiedUnits"), extract.OccupiedUnits);
        Assert.Equal(expected.Decimal("totalRentableSquareFeet"), extract.TotalRentableSquareFeet);
        Assert.Equal(expected.Decimal("occupiedSquareFeet"), extract.OccupiedSquareFeet);
        Assert.Equal(expected.Decimal("annualScheduledRent"), extract.AnnualScheduledRent);
    }
}
