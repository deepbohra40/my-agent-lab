using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using CreServicing.Agent.Data;
using CreServicing.Agent.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
// Not decoration: the AsAIAgent overload that takes OpenAI's ChatClient lives in
// this namespace. Without it the compiler only finds the IChatClient one and the
// call below does not resolve.
using OpenAI.Chat;

namespace CreServicing.Agent.Extraction;

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
public static class RentRollExtractor
{
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
    public static async Task RunAsync(string relativePath)
    {
        var document = DocumentStore.Load(relativePath);
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME") ?? "gpt-5-mini";

        Console.WriteLine("RENT ROLL EXTRACTION");
        Console.WriteLine($"Document   {document.RelativePath}  (~{document.ApproximateTokens:N0} tokens)");
        Console.WriteLine($"Deployment {deployment}");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine();

        var extract = await ExtractAsync(document);
        if (extract is null)
        {
            Console.WriteLine("No structured result came back. Check the deployment and the schema.");
            return;
        }

        PrintExtract(extract);
        PrintGoldenComparison(relativePath, extract);

        // COST — roadmap item. response carries usage; print tokens and dollars
        // per document here once you have found the property on this package
        // version. A per-document cost times a realistic portfolio is the number
        // that decides whether any of this ships.
    }

    /// <summary>
    /// The extraction itself, with no console I/O — what the eval harness and
    /// <see cref="FinancialSnapshotAssembler"/> call directly. <see cref="RunAsync"/>
    /// is this plus the demo printing.
    /// </summary>
    public static async Task<RentRollExtract?> ExtractAsync(SourceDocument document)
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
            ?? "gpt-5-mini";

        AIAgent extractor = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
            .GetChatClient(deployment)
            .AsAIAgent(
                name: "RentRollExtractor",
                instructions: Instructions);

        AgentResponse<RentRollExtract> response =
            await extractor.RunAsync<RentRollExtract>(BuildInput(document));

        return response.Result;
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
