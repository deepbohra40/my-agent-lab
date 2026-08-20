using CreServicing.Agent.Data;
using CreServicing.Agent.Extraction;

namespace CreServicing.Agent.Eval;

/// <summary>
/// Field-level accuracy for <see cref="TaxBillExtractor"/>. Both golden entries
/// carry <c>expectedLoanId: null</c> — there is nothing to assert here against
/// that, because <see cref="Domain.TaxBillExtract"/> has no LoanId property at
/// all; the point being tested is that a tax bill is never associated to a loan
/// by asking the model to read one off the page.
/// </summary>
[Trait("Category", "Eval")]
public class TaxBillEvalTests
{
    [Fact]
    public Task Office_property_tax_bill_extracts_correctly()
        => AssertMatchesGolden("CRE-2019-0447/tax-bill-2025.txt");

    [Fact]
    public Task Multifamily_property_tax_bill_extracts_correctly()
        => AssertMatchesGolden("CRE-2021-0912/tax-bill-2025.txt");

    private static async Task AssertMatchesGolden(string relativePath)
    {
        var document = DocumentStore.Load(relativePath);
        var expected = GoldenSet.Entry(relativePath).Expected();

        var extract = (await TaxBillExtractor.ExtractAsync(document)).Value;

        Assert.NotNull(extract);
        Assert.Equal(expected.Int("taxYear"), extract!.TaxYear);
        Assert.Equal(expected.String("parcelId"), extract.ParcelId);
        Assert.Equal(expected.Decimal("amountDue"), extract.AmountDue);
        Assert.Equal(expected.String("dueDate"), extract.DueDate);
        Assert.Equal(expected.Bool("isPaid"), extract.IsPaid);
    }
}
