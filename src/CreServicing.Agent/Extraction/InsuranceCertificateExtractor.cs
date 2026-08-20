using System.Text.Json;
using CreServicing.Agent.Configuration;
using Microsoft.Extensions.Options;
using CreServicing.Agent.Cost;
using CreServicing.Agent.Data;
using CreServicing.Agent.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace CreServicing.Agent.Extraction;

/// <summary>
/// Third of the four Section 5 extractors.
///
/// The trap this fixture is built to catch: a certificate of insurance lists
/// several limits — a building blanket limit, business income / rental value,
/// sometimes ordinance-or-law coverage as a separate line. The covenant is
/// tested against property coverage, i.e. the building limit alone. A model that
/// sums the building limit and the business-income limit produces a number that
/// clears the required coverage while the actual property coverage is short —
/// silently dropping a real breach. <see cref="InsuranceCertificateExtract.CoverageAmount"/>
/// must be the building limit only.
/// </summary>
public sealed class InsuranceCertificateExtractor(IChatClient chatClient, IOptions<AzureOpenAIOptions> options)
{
    private readonly AIAgent _extractor =
        chatClient.AsAIAgent(name: "InsuranceCertificateExtractor", instructions: Instructions);

    private readonly string _deployment = options.Value.Deployment;

    private const string Instructions =
        """
        Extract the figures from this certificate of property insurance.

        coverageAmount is the building / property blanket limit ONLY. Certificates
        often list several limits on separate lines — building, business income or
        rental value, ordinance or law, contents. Do not sum any of them together
        and do not report a limit other than the building/property blanket limit
        in this field. If more than one limit is present, say which ones you saw
        and which one you picked in notes.

        effectiveDate and expirationDate as ISO yyyy-MM-dd.

        lenderNamedAsMortgagee is true only if the certificate explicitly names the
        lender as mortgagee or loss payee under a mortgage clause. If the
        certificate does not address this, return null rather than assuming either
        way.

        Every figure the certificate does not state must come back null. Never
        estimate one.
        """;

    public async Task RunAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var document = DocumentStore.Load(relativePath);
        var deployment = _deployment;

        Console.WriteLine("INSURANCE CERTIFICATE EXTRACTION");
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

    /// <summary>No console I/O — what the eval harness and <see cref="FinancialSnapshotAssembler"/> call directly.</summary>
    public async Task<ExtractionResult<InsuranceCertificateExtract>> ExtractAsync(
        SourceDocument document, CancellationToken cancellationToken = default)
    {
        AgentResponse<InsuranceCertificateExtract> response =
            await _extractor.RunAsync<InsuranceCertificateExtract>(
                BuildInput(document), cancellationToken: cancellationToken);

        return new ExtractionResult<InsuranceCertificateExtract>(response.Result, response.ToModelUsage());
    }

    private static string BuildInput(SourceDocument document)
        => UntrustedDocument.Wrap(document, "Extract the insurance certificate below.");

    private static void PrintExtract(InsuranceCertificateExtract extract)
    {
        Console.WriteLine("EXTRACTED");
        Console.WriteLine($"  sourceDocument            {extract.SourceDocument}");
        Console.WriteLine($"  carrier                   {Show(extract.Carrier)}");
        Console.WriteLine($"  policyNumber              {Show(extract.PolicyNumber)}");
        Console.WriteLine($"  coverageAmount            {Show(extract.CoverageAmount)}");
        Console.WriteLine($"  effectiveDate             {Show(extract.EffectiveDate)}");
        Console.WriteLine($"  expirationDate            {Show(extract.ExpirationDate)}");
        Console.WriteLine($"  lenderNamedAsMortgagee    {Show(extract.LenderNamedAsMortgagee)}");
        Console.WriteLine();

        Console.WriteLine($"  confidence (unscored)     {extract.Confidence:F2}");
        Console.WriteLine($"  notes                     {extract.Notes ?? "(none)"}");
        Console.WriteLine();
    }

    private static void PrintGoldenComparison(string relativePath, InsuranceCertificateExtract actual)
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
        Console.WriteLine($"  {"field",-26}{"extracted",-22}expected");
        Console.WriteLine($"  {new string('-', 26)}{new string('-', 22)}{new string('-', 20)}");
        Row("carrier", Show(actual.Carrier));
        Row("policyNumber", Show(actual.PolicyNumber));
        Row("coverageAmount", Show(actual.CoverageAmount));
        Row("effectiveDate", Show(actual.EffectiveDate));
        Row("expirationDate", Show(actual.ExpirationDate));
        Row("lenderNamedAsMortgagee", Show(actual.LenderNamedAsMortgagee));
        Console.WriteLine();

        if (entry.TryGetProperty("notes", out var notes))
        {
            Console.WriteLine("  Why this fixture exists:");
            Console.WriteLine($"  {notes.GetString()}");
            Console.WriteLine();
        }

        void Row(string field, string extracted)
            => Console.WriteLine($"  {field,-26}{extracted,-22}{ExpectedValue(expected, field)}");
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
