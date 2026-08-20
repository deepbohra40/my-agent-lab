using CreServicing.Agent.Data;
using CreServicing.Agent.Extraction;

namespace CreServicing.Agent.Eval;

/// <summary>
/// Field-level accuracy for <see cref="InsuranceCertificateExtractor"/>. Both
/// fixtures list a business-income limit alongside the building limit
/// specifically to test that coverageAmount picks the building limit alone
/// rather than summing them.
/// </summary>
[Trait("Category", "Eval")]
public class InsuranceCertificateEvalTests
{
    [Fact]
    public Task Office_certificate_extracts_the_building_limit_not_the_sum()
        => AssertMatchesGolden("CRE-2019-0447/insurance-certificate-2026.txt");

    [Fact]
    public Task Multifamily_certificate_extracts_the_building_limit_not_the_sum()
        => AssertMatchesGolden("CRE-2021-0912/insurance-certificate-2026.txt");

    private static async Task AssertMatchesGolden(string relativePath)
    {
        var document = DocumentStore.Load(relativePath);
        var expected = GoldenSet.Entry(relativePath).Expected();

        var extract = (await InsuranceCertificateExtractor.ExtractAsync(document)).Value;

        Assert.NotNull(extract);
        Assert.Equal(expected.String("carrier"), extract!.Carrier);
        Assert.Equal(expected.String("policyNumber"), extract.PolicyNumber);
        Assert.Equal(expected.Decimal("coverageAmount"), extract.CoverageAmount);
        Assert.Equal(expected.String("effectiveDate"), extract.EffectiveDate);
        Assert.Equal(expected.String("expirationDate"), extract.ExpirationDate);
        Assert.Equal(expected.Bool("lenderNamedAsMortgagee"), extract.LenderNamedAsMortgagee);
    }
}
