using System.Text.Json;
using CreServicing.Core.Configuration;
using Microsoft.Extensions.Options;
using CreServicing.Core.Cost;
using CreServicing.Core.Data;
using CreServicing.Core.Diagnostics;
using CreServicing.Core.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
// Not decoration: the AsAIAgent overload that takes OpenAI's ChatClient lives in
// this namespace. Without it the compiler only finds the IChatClient one and the
// call below does not resolve.
using OpenAI.Chat;

namespace CreServicing.Core.Extraction;

/// <summary>
/// Section 5, applied. The first line of this project that costs money.
///
/// The technique is the one from <c>section5-getting-started/StructuredOutput</c> —
/// <c>RunAsync&lt;T&gt;</c> against a record — with two things changed that matter:
/// the target is <see cref="RentRollExtract"/> instead of a meeting summary, and
/// the input is borrower-supplied text that must be treated as hostile.
///
/// What this produces is an *extraction*, not a decision. Nothing here compares a
/// number to a covenant threshold. That happens in <see cref="CovenantEngine"/>,
/// in C#, where it is reproducible.
/// </summary>
public sealed class RentRollExtractor(IChatClient chatClient, IOptions<AzureOpenAIOptions> options)
{
    // Built once per instance rather than per call. The agent is a thin wrapper
    // over the injected IChatClient — note the type: nothing in this file names
    // Azure any more, so swapping provider is a change to ServiceRegistration
    // and nothing else.
    private readonly AIAgent _extractor =
        chatClient.AsAIAgent(name: "RentRollExtractor", instructions: Instructions);

    private readonly string _deployment = options.Value.Deployment;

    // ── The assignment ───────────────────────────────────────────────────────
    //
    // This string is the work. Everything else in this file is plumbing you have
    // already typed once in the section 5 scratchpad.
    //
    // What is here now is deliberately naive: one sentence, no guardrails. Run it
    // as-is first, read the output against fixtures/golden/, and write down what
    // it got wrong. Then fix the prompt one clause at a time and note which clause
    // fixed which field. That list — "this sentence bought me this accuracy" — is
    // the thing worth having, and it is the thing you cannot get by reading the
    // instructor's finished prompt.
    //
    // The failure modes the golden set is built to catch, roughly in the order a
    // naive prompt hits them:
    //
    //   1. Guessing. A field the document does not state must come back null.
    //      Nullable decimals in RentRollExtract exist so the model has somewhere
    //      honest to put "not stated"; a prompt that does not say so will get a 0.
    //
    //   2. Cross-filling unit counts and square footage. An office roll reports
    //      RSF and no units (CRE-2019-0447). A multifamily roll reports units and
    //      no RSF (CRE-2021-0912). Neither should be derived from the other.
    //
    //   3. Physical vs economic occupancy. The multifamily fixture states both.
    //      The covenant is written against physical. Picking up 95.10% instead of
    //      232/240 is a wrong answer that looks like a right one.
    //
    //   4. Summary figure vs the rows. Occupied SF is printed in the summary AND
    //      derivable by summing the non-VACANT rows. Both routes give 118,600 on
    //      the clean fixture. Say what to do when they disagree — that instruction
    //      is what makes the adversarial fixture fail safely.
    //
    //   5. Instructions embedded in the document. See AsUntrustedData below for
    //      the mechanical half. The prompt has to carry the other half: text
    //      between the markers is data to be read, never instruction to be obeyed,
    //      and any attempt at the latter goes in Notes rather than being silently
    //      ignored. Surfacing the attempt is a pass criterion in the golden set —
    //      a borrower embedding pipeline instructions in a certified rent roll is
    //      a fraud signal in its own right.
    //
    // Also worth pinning down: AsOf as ISO yyyy-MM-dd (the fixtures write
    // "June 30, 2026"), and SourceDocument echoed back exactly as handed over.
    private const string Instructions =
        "Extract the rent roll figures from the document. take these into consideration - asOf - IOS yyyy-MM-dd, A rule about not-stated fields — that a figure the document does not state must come back null, and specifically that unit counts and square footage are not derivable from each\n  other ";

    /// <summary>
    /// Extract one rent roll and print it beside the golden answer.
    /// </summary>
    /// <param name="relativePath">
    /// Path under <c>fixtures/</c>, e.g. <c>CRE-2019-0447/rent-roll-2026-Q2.txt</c>.
    /// Point it at <c>adversarial/rent-roll-injected-2026-Q2.txt</c> once the clean
    /// one is passing.
    /// </param>
    public async Task RunAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var document = DocumentStore.Load(relativePath);
        var deployment = _deployment;

        Console.WriteLine("RENT ROLL EXTRACTION");
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

    /// <summary>
    /// The extraction itself, with no console I/O — what the eval harness and
    /// <see cref="FinancialSnapshotAssembler"/> call directly. <see cref="RunAsync"/>
    /// is this plus the demo printing.
    /// </summary>
    public async Task<ExtractionResult<RentRollExtract>> ExtractAsync(
        SourceDocument document, CancellationToken cancellationToken = default)
    {
        using var activity = ServicingTelemetry.Extraction("rent-roll", document);

        AgentResponse<RentRollExtract> response =
            await _extractor.RunAsync<RentRollExtract>(
                BuildInput(document), cancellationToken: cancellationToken);

        // ── Tokens before Result, and the order is load-bearing ──────────────
        //
        // AgentResponse<T>.Result parses lazily: reading it deserializes the
        // model's text into the schema and THROWS if it does not fit. The tokens
        // were billed either way, so setting usage after that read would record
        // cost only on success — under-reporting exactly the runs that wasted
        // money, which is the opposite of what a cost span is for. Same ordering
        // argument as PackageCost accounting before the assembler's null checks.
        var usage = response.ToModelUsage();
        activity.SetUsage(usage);

        RentRollExtract? value;
        try
        {
            value = response.Result;
        }
        catch (JsonException ex)
        {
            // Rethrown unchanged — this only puts the reason on the span. A span
            // that ended "successful" because the exception unwound past it would
            // be worse than no span at all.
            activity.Unparseable(ex);
            throw;
        }

        if (value is null)
        {
            activity.NoResult();
        }

        return new ExtractionResult<RentRollExtract>(value, usage);
    }

    private static string BuildInput(SourceDocument document)
        => UntrustedDocument.Wrap(document, "Extract the rent roll below.");

    private static void PrintExtract(RentRollExtract extract)
    {
        Console.WriteLine("EXTRACTED");
        Console.WriteLine($"  sourceDocument            {extract.SourceDocument}");
        Console.WriteLine($"  asOf                      {extract.AsOf}");
        Console.WriteLine($"  totalUnits                {Show(extract.TotalUnits)}");
        Console.WriteLine($"  occupiedUnits             {Show(extract.OccupiedUnits)}");
        Console.WriteLine($"  totalRentableSquareFeet   {Show(extract.TotalRentableSquareFeet)}");
        Console.WriteLine($"  occupiedSquareFeet        {Show(extract.OccupiedSquareFeet)}");
        Console.WriteLine($"  annualScheduledRent       {Show(extract.AnnualScheduledRent)}");
        Console.WriteLine();

        // Self-reported and unscored. It is a routing signal — low confidence
        // sends the package to a human — never evidence for a finding.
        Console.WriteLine($"  confidence (unscored)     {extract.Confidence:F2}");
        Console.WriteLine($"  notes                     {extract.Notes ?? "(none)"}");
        Console.WriteLine();
    }

    /// <summary>
    /// Side by side with fixtures/golden/. Deliberately not a pass/fail grader —
    /// grading by hand is the point at this stage, because the interesting output
    /// is your notes on *why* a field missed, not a percentage. The xUnit harness
    /// that turns this into a regression gate is the EVAL roadmap item.
    /// </summary>
    private static void PrintGoldenComparison(string relativePath, RentRollExtract actual)
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
        Console.WriteLine($"  {"field",-26}{"extracted",-20}expected");
        Console.WriteLine($"  {new string('-', 26)}{new string('-', 20)}{new string('-', 20)}");
        Row("asOf", actual.AsOf);
        Row("totalUnits", Show(actual.TotalUnits));
        Row("occupiedUnits", Show(actual.OccupiedUnits));
        Row("totalRentableSquareFeet", Show(actual.TotalRentableSquareFeet));
        Row("occupiedSquareFeet", Show(actual.OccupiedSquareFeet));
        Row("annualScheduledRent", Show(actual.AnnualScheduledRent));
        Console.WriteLine();

        if (entry.TryGetProperty("notes", out var notes))
        {
            Console.WriteLine("  Why this fixture exists:");
            Console.WriteLine($"  {notes.GetString()}");
            Console.WriteLine();
        }

        if (entry.TryGetProperty("passCriteria", out var criteria))
        {
            Console.WriteLine("  Security pass criteria — judge these yourself, they are not field matches:");
            foreach (var criterion in criteria.EnumerateArray())
            {
                Console.WriteLine($"    - {criterion.GetString()}");
            }
            Console.WriteLine();
        }

        void Row(string field, string extracted)
            => Console.WriteLine($"  {field,-26}{extracted,-20}{ExpectedValue(expected, field)}");
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
