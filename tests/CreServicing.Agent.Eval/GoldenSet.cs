using System.Text.Json;
using CreServicing.Agent.Data;

namespace CreServicing.Agent.Eval;

/// <summary>
/// Reads <c>fixtures/golden/expected-extractions.json</c> as the eval harness's
/// answer key, so expected values live in exactly one place — the same file the
/// console extractors already compare against — instead of being retyped into
/// test code where they could drift from it.
/// </summary>
internal static class GoldenSet
{
    private static readonly JsonElement Documents = Load();

    private static JsonElement Load()
    {
        var path = Path.Combine(DocumentStore.Root, "golden", "expected-extractions.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("documents").Clone();
    }

    /// <summary>The golden entry for one fixture, by its path under fixtures/.</summary>
    public static JsonElement Entry(string relativePath)
    {
        var entry = Documents.EnumerateArray()
            .FirstOrDefault(d => string.Equals(
                d.GetProperty("path").GetString()?.Replace('\\', '/'),
                relativePath.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase));

        return entry.ValueKind == JsonValueKind.Object
            ? entry
            : throw new InvalidOperationException($"No golden entry for '{relativePath}'.");
    }

    public static JsonElement Expected(this JsonElement entry) => entry.GetProperty("expected");

    public static decimal? Decimal(this JsonElement expected, string field)
        => expected.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetDecimal()
            : null;

    public static int? Int(this JsonElement expected, string field)
        => expected.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt32()
            : null;

    public static string? String(this JsonElement expected, string field)
        => expected.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    public static bool? Bool(this JsonElement expected, string field)
        => expected.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetBoolean()
            : null;
}
