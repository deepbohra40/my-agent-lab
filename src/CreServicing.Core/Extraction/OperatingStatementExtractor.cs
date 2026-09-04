using System.Text.Json;
using CreServicing.Core.Configuration;
using CreServicing.Core.Cost;
using CreServicing.Core.Data;
using CreServicing.Core.Diagnostics;
using CreServicing.Core.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace CreServicing.Core.Extraction;

/// <summary>
/// Second of the four Section 5 extractors. Same shape as
/// <see cref="RentRollExtractor"/>; the interesting part is the instruction, not
/// the plumbing.
///
/// The trap this fixture is built to catch: the golden CRE-2019-0447 statement
/// reports NOI of 2,284,000 by adding back a 154,000 roof repair as capital
/// rather than operating. <see cref="OperatingStatementExtract.ReportedNetOperatingIncome"/>
/// must capture that number exactly as printed — it is what the borrower claims,
/// and a claim is evidence about the borrower, not a number to adopt. The
/// recomputed figure that actually feeds <see cref="Domain.FinancialSnapshot"/>
/// is EGI minus OpEx, done in <see cref="FinancialSnapshotAssembler"/> with
/// <see cref="Covenants.NetOperatingIncome"/> — never in this class, and never by
/// the model.
/// </summary>
public sealed class OperatingStatementExtractor(IChatClient chatClient, IOptions<AzureOpenAIOptions> options)
{
    private readonly AIAgent _extractor =
        chatClient.AsAIAgent(name: "OperatingStatementExtractor", instructions: Instructions);

    private readonly string _deployment = options.Value.Deployment;

    private const string Instructions =
        """
        Extract the figures from this borrower operating statement.

        periodStart and periodEnd as ISO yyyy-MM-dd.

        effectiveGrossIncome and operatingExpenses are the totals printed on the
        statement, not a number you compute.

        reportedNetOperatingIncome is whatever the statement itself labels as net
        operating income — copy it exactly, even if it does not equal effective
        gross income minus operating expenses. Do not correct it, average it, or
        substitute your own subtraction. If the statement explains the gap with a
        footnote (an add-back, an exclusion, a one-time item), summarise that
        explanation in notes. Your job is to report what the document says, not to
        decide whether the borrower's math is right.

        Every figure the statement does not state must come back null. Never
        estimate one.
        """;

    public async Task RunAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var document = DocumentStore.Load(relativePath);
        var deployment = _deployment;

        Console.WriteLine("OPERATING STATEMENT EXTRACTION");
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
    public async Task<ExtractionResult<OperatingStatementExtract>> ExtractAsync(
        SourceDocument document, CancellationToken cancellationToken = default)
    {
        using var activity = ServicingTelemetry.Extraction("operating-statement", document);

        AgentResponse<OperatingStatementExtract> response =
            await _extractor.RunAsync<OperatingStatementExtract>(
                BuildInput(document), cancellationToken: cancellationToken);

        // Tokens before Result. See the note in RentRollExtractor — reading
        // Result parses and can throw, and the tokens were billed regardless.
        var usage = response.ToModelUsage();
        activity.SetUsage(usage);

        OperatingStatementExtract? value;
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

        return new ExtractionResult<OperatingStatementExtract>(value, usage);
    }

    private static string BuildInput(SourceDocument document)
        => UntrustedDocument.Wrap(document, "Extract the operating statement below.");

    private static void PrintExtract(OperatingStatementExtract extract)
    {
        Console.WriteLine("EXTRACTED");
        Console.WriteLine($"  sourceDocument              {extract.SourceDocument}");
        Console.WriteLine($"  periodStart                 {extract.PeriodStart}");
        Console.WriteLine($"  periodEnd                   {extract.PeriodEnd}");
        Console.WriteLine($"  effectiveGrossIncome        {Show(extract.EffectiveGrossIncome)}");
        Console.WriteLine($"  operatingExpenses           {Show(extract.OperatingExpenses)}");
        Console.WriteLine($"  reportedNetOperatingIncome  {Show(extract.ReportedNetOperatingIncome)}");
        Console.WriteLine();

        Console.WriteLine($"  confidence (unscored)       {extract.Confidence:F2}");
        Console.WriteLine($"  notes                       {extract.Notes ?? "(none)"}");
        Console.WriteLine();
    }

    private static void PrintGoldenComparison(string relativePath, OperatingStatementExtract actual)
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
        Console.WriteLine($"  {"field",-28}{"extracted",-20}expected");
        Console.WriteLine($"  {new string('-', 28)}{new string('-', 20)}{new string('-', 20)}");
        Row("periodStart", actual.PeriodStart);
        Row("periodEnd", actual.PeriodEnd);
        Row("effectiveGrossIncome", Show(actual.EffectiveGrossIncome));
        Row("operatingExpenses", Show(actual.OperatingExpenses));
        Row("reportedNetOperatingIncome", Show(actual.ReportedNetOperatingIncome));
        Console.WriteLine();

        if (entry.TryGetProperty("notes", out var notes))
        {
            Console.WriteLine("  Why this fixture exists:");
            Console.WriteLine($"  {notes.GetString()}");
            Console.WriteLine();
        }

        void Row(string field, string extracted)
            => Console.WriteLine($"  {field,-28}{extracted,-20}{ExpectedValue(expected, field)}");
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
}
