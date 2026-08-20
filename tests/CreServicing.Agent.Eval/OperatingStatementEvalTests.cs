using CreServicing.Agent.Data;
using CreServicing.Agent.Extraction;

namespace CreServicing.Agent.Eval;

/// <summary>
/// Field-level accuracy for <see cref="OperatingStatementExtractor"/>. The office
/// fixture is the reported-vs-recomputed trap (borrower adds back a roof repair);
/// the multifamily one is the control case where they agree — both must pass for
/// the extractor to be trusted, since a model that always distrusts the reported
/// figure would pass the first and fail the second.
/// </summary>
[Trait("Category", "Eval")]
public class OperatingStatementEvalTests
{
    [Fact]
    public Task Office_statement_captures_the_reported_figure_verbatim()
        => AssertMatchesGolden("CRE-2019-0447/operating-statement-2026-Q2.txt");

    [Fact]
    public Task Multifamily_statement_extracts_correctly()
        => AssertMatchesGolden("CRE-2021-0912/operating-statement-2026-Q2.txt");

    private static async Task AssertMatchesGolden(string relativePath)
    {
        var document = DocumentStore.Load(relativePath);
        var expected = GoldenSet.Entry(relativePath).Expected();

        var extract = (await OperatingStatementExtractor.ExtractAsync(document)).Value;

        Assert.NotNull(extract);
        Assert.Equal(expected.String("periodStart"), extract!.PeriodStart);
        Assert.Equal(expected.String("periodEnd"), extract.PeriodEnd);
        Assert.Equal(expected.Decimal("effectiveGrossIncome"), extract.EffectiveGrossIncome);
        Assert.Equal(expected.Decimal("operatingExpenses"), extract.OperatingExpenses);
        Assert.Equal(expected.Decimal("reportedNetOperatingIncome"), extract.ReportedNetOperatingIncome);
    }
}
