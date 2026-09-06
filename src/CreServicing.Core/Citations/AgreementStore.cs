using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreServicing.Core.Citations;

/// <summary>One clause of a loan agreement as the manifest records it.</summary>
public sealed record ClauseRecord(
    [property: JsonPropertyName("clauseId")] string ClauseId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("heading")] string Heading,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// Reads the hand-verified clause manifests from <c>agreements/</c>.
///
/// The manifest, not the agreement text, is what gets indexed. That is a
/// deliberate inversion of how the course does RAG, where a document is chunked
/// automatically and whatever falls out is what you retrieve.
///
/// Chunking a legal document by token count splits clauses mid-sentence and
/// merges unrelated ones, and neither outcome is quotable on a notice to a
/// borrower. More importantly, an automatic chunk has no idea which covenant it
/// governs — and that association is the only thing standing between a
/// similarity search and a wrong clause on a loan file. So the split is made by
/// hand, once, and recorded next to the document it came from. Exactly the same
/// argument as <c>fixtures/golden/expected-extractions.json</c>: where a machine
/// needs ground truth, a person writes it down.
///
/// The cost is honest and worth stating: this does not scale to a real
/// portfolio, where agreements are hundreds of pages and no one is hand-indexing
/// them. The production shape is an extraction pass that proposes the clause
/// split and a person who approves it — the same "model proposes, human
/// disposes" pattern the write gate already uses, applied one layer earlier.
/// </summary>
public static class AgreementStore
{
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "agreements");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Loan ids that have an indexed agreement, in a stable order.</summary>
    public static IReadOnlyList<string> ListAgreements()
        => Directory.Exists(Root)
            ? Directory.GetFiles(Root, "*-clauses.json")
                .Select(path => Path.GetFileName(path).Replace("-clauses.json", string.Empty, StringComparison.Ordinal))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>
    /// The clauses for one loan. Returns empty rather than throwing when no
    /// agreement is on file — most loans in a portfolio will not have one
    /// indexed, and that is a gap in citation coverage, not a failure of the run.
    /// </summary>
    public static IReadOnlyList<ClauseRecord> GetClauses(string loanId)
    {
        var path = Path.Combine(Root, $"{loanId}-clauses.json");
        if (!File.Exists(path))
        {
            return [];
        }

        var manifest = JsonSerializer.Deserialize<ClauseManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"{path} did not deserialize to a clause manifest.");

        return manifest.Clauses;
    }

    private sealed record ClauseManifest(
        [property: JsonPropertyName("loanId")] string LoanId,
        [property: JsonPropertyName("sourceDocument")] string SourceDocument,
        [property: JsonPropertyName("clauses")] IReadOnlyList<ClauseRecord> Clauses);
}
