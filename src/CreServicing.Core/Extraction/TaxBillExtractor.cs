using System.Text.Json;
using CreServicing.Core.Configuration;
using Microsoft.Extensions.Options;
using CreServicing.Core.Cost;
using CreServicing.Core.Data;
using CreServicing.Core.Diagnostics;
using CreServicing.Core.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace CreServicing.Core.Extraction;

/// <summary>
/// Fourth of the four Section 5 extractors.
///
/// The trap this fixture is built to catch: a county tax bill carries no loan
/// number — it is addressed to the borrower's property, not to a lender's loan
/// file. <see cref="TaxBillExtract"/> deliberately has no LoanId property for
/// this reason; the pipeline associates a tax bill with a loan by folder / parcel
/// / address, never by asking the model to read one off the page. A model that
/// confidently writes a loan reference into notes here is guessing, and guessing
/// is exactly what the golden set's <c>expectedLoanId: null</c> entry is testing
/// for.
///
/// Also worth noting: unlike the other three extracts, nothing in
/// <see cref="FinancialSnapshotAssembler"/> consumes this one today —
/// <see cref="Domain.CovenantEngine"/> has no tax-delinquency covenant. It still
/// gets extracted because a real servicing analyst confirms taxes are current as
/// part of reading the package, and because the golden set grades it; wiring a
/// tax-delinquency finding into the covenant engine is future scope, not an
/// oversight here.
/// </summary>
public sealed class TaxBillExtractor(IChatClient chatClient, IOptions<AzureOpenAIOptions> options)
{
    private readonly AIAgent _extractor =
        chatClient.AsAIAgent(name: "TaxBillExtractor", instructions: Instructions);

    private readonly string _deployment = options.Value.Deployment;

    private const string Instructions =
        """
        Extract the figures from this county property tax bill.

        taxYear is the tax year the bill covers, as a four-digit integer.

        amountDue is the total tax levied for the year (the "total due" figure),
        not the remaining balance after payment. Report it even if the bill shows
        the balance as zero because it has already been paid — isPaid captures
        that separately.

        dueDate as ISO yyyy-MM-dd.

        isPaid is true only if the bill states the tax has been paid in full.

        Do not extract or invent a loan number, loan reference, or borrower loan
        id from this document. A tax bill is addressed to a property and a parcel,
        not to a loan file, and it will not state one. If you notice the document
        does not identify a loan, that is expected — do not treat it as a missing
        field to fill in and do not guess one from context.

        Every figure the bill does not state must come back null. Never estimate one.
        """;

    public async Task RunAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var document = DocumentStore.Load(relativePath);
        var deployment = _deployment;

        Console.WriteLine("TAX BILL EXTRACTION");
        Console.WriteLine($"Document   {document.RelativePath}  (~{document.ApproximateTokens:N0} tokens)");
        Console.WriteLine($"Deployment {deployment}");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine();

        var result = await ExtractAsync(document, cancellationToken);
        if (result.Value is not { } extract)
        {
            Console.WriteLine("No structured result came back. Check the deployment and the schema.");
            return;
        }

        PrintExtract(extract);
        PrintGoldenComparison(relativePath, extract);
        CostReport.PrintDocument(document.FileName, result.Usage, deployment);
    }

    /// <summary>No console I/O — what the eval harness calls directly.</summary>
    public async Task<ExtractionResult<TaxBillExtract>> ExtractAsync(
        SourceDocument document, CancellationToken cancellationToken = default)
    {
        using var activity = ServicingTelemetry.Extraction("tax-bill", document);

        AgentResponse<TaxBillExtract> response =
            await _extractor.RunAsync<TaxBillExtract>(
                BuildInput(document), cancellationToken: cancellationToken);

        // Tokens before Result. See the note in RentRollExtractor — reading
        // Result parses and can throw, and the tokens were billed regardless.
        var usage = response.ToModelUsage();
        activity.SetUsage(usage);

        TaxBillExtract? value;
        try
        {
            value = response.Result;
        }
        catch (JsonException ex)
        {
            activity.Unparseable(ex);
            throw;
        }

        if (value is null)
        {
            activity.NoResult();
        }

        return new ExtractionResult<TaxBillExtract>(value, usage);
    }

    private static string BuildInput(SourceDocument document)
        => UntrustedDocument.Wrap(document, "Extract the tax bill below.");

    private static void PrintExtract(TaxBillExtract extract)
    {
        Console.WriteLine("EXTRACTED");
        Console.WriteLine($"  sourceDocument   {extract.SourceDocument}");
        Console.WriteLine($"  taxYear          {Show(extract.TaxYear)}");
        Console.WriteLine($"  parcelId         {Show(extract.ParcelId)}");
        Console.WriteLine($"  amountDue        {Show(extract.AmountDue)}");
        Console.WriteLine($"  dueDate          {Show(extract.DueDate)}");
        Console.WriteLine($"  isPaid           {Show(extract.IsPaid)}");
        Console.WriteLine();

        Console.WriteLine($"  confidence (unscored)  {extract.Confidence:F2}");
        Console.WriteLine($"  notes                  {extract.Notes ?? "(none)"}");
        Console.WriteLine();
    }

    private static void PrintGoldenComparison(string relativePath, TaxBillExtract actual)
    {
        var goldenPath = Path.Combine(DocumentStore.Root, "golden", "expected-extractions.json");
        if (!File.Exists(goldenPath))
        {
            Console.WriteLine($"No golden set at {goldenPath}.");
            return;
        }

        using var golden = JsonDocument.Parse(File.ReadAllText(goldenPath));

        var entry = golden.RootElement
            .GetProperty("documents")
            .EnumerateArray()
            .FirstOrDefault(d => SamePath(d.GetProperty("path").GetString(), relativePath));

        if (entry.ValueKind != JsonValueKind.Object)
        {
            Console.WriteLine($"{relativePath} is not in the golden set yet — add it before trusting this run.");
            return;
        }

        var expected = entry.GetProperty("expected");

        Console.WriteLine("AGAINST GOLDEN");
        Console.WriteLine($"  {"field",-16}{"extracted",-20}expected");
        Console.WriteLine($"  {new string('-', 16)}{new string('-', 20)}{new string('-', 20)}");
        Row("taxYear", Show(actual.TaxYear));
        Row("parcelId", Show(actual.ParcelId));
        Row("amountDue", Show(actual.AmountDue));
        Row("dueDate", Show(actual.DueDate));
        Row("isPaid", Show(actual.IsPaid));
        Console.WriteLine();

        if (entry.TryGetProperty("notes", out var notes))
        {
            Console.WriteLine("  Why this fixture exists:");
            Console.WriteLine($"  {notes.GetString()}");
            Console.WriteLine();
        }

        void Row(string field, string extracted)
            => Console.WriteLine($"  {field,-16}{extracted,-20}{ExpectedValue(expected, field)}");
    }

    private static bool SamePath(string? goldenPath, string relativePath)
        => string.Equals(
            goldenPath?.Replace('\\', '/'),
            relativePath.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static string ExpectedValue(JsonElement expected, string field)
        => expected.TryGetProperty(field, out var value)
            ? value.ValueKind == JsonValueKind.Null ? "(null)" : value.ToString()
            : "(not scored)";

    private static string Show<T>(T? value) where T : struct
        => value?.ToString() ?? "(null)";

    private static string Show(string? value) => value ?? "(null)";
}
