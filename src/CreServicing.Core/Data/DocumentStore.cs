namespace CreServicing.Core.Data;

/// <summary>One document out of a borrower's reporting package.</summary>
public sealed record SourceDocument(string PackageId, string FileName, string RelativePath, string Text)
{
    /// <summary>Rough token budget check — the fixtures are small, real packages will not be.</summary>
    public int ApproximateTokens => Text.Length / 4;
}

/// <summary>
/// Reads the synthetic borrower packages from <c>fixtures/</c>.
///
/// Plain text on purpose. Real CRE packages arrive as scanned PDFs and the OCR
/// step is a genuine part of the problem — but it is Azure Document Intelligence
/// work, not agent work, and mixing the two would mean debugging OCR when the
/// thing you are trying to learn is orchestration. Swap this class for a real
/// document-intelligence call once the agent layer is working; nothing above it
/// needs to change, which is the point of it being a class.
/// </summary>
public static class DocumentStore
{
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>Package folders under fixtures/, excluding the golden answer key.</summary>
    public static IReadOnlyList<string> ListPackages()
        => Directory.Exists(Root)
            ? Directory.GetDirectories(Root)
                .Select(Path.GetFileName)
                .Where(name => name is not null && name != "golden")
                .Select(name => name!)
                .OrderBy(name => name)
                .ToList()
            : [];

    /// <summary>Every document in one package, in a stable order.</summary>
    public static IReadOnlyList<SourceDocument> GetPackage(string packageId)
    {
        var folder = Path.Combine(Root, packageId);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(
                $"No fixture package '{packageId}' under {Root}. " +
                $"Available: {string.Join(", ", ListPackages())}");
        }

        return Directory.GetFiles(folder, "*.txt")
            .OrderBy(path => path)
            .Select(path => new SourceDocument(
                PackageId: packageId,
                FileName: Path.GetFileName(path),
                RelativePath: Path.Combine(packageId, Path.GetFileName(path)),
                Text: File.ReadAllText(path)))
            .ToList();
    }

    /// <summary>One document by its path relative to fixtures/, e.g. "CRE-2019-0447/tax-bill-2025.txt".</summary>
    public static SourceDocument Load(string relativePath)
    {
        var full = Path.Combine(Root, relativePath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"No fixture at {relativePath}.", full);
        }

        return new SourceDocument(
            PackageId: Path.GetFileName(Path.GetDirectoryName(full)) ?? string.Empty,
            FileName: Path.GetFileName(full),
            RelativePath: relativePath,
            Text: File.ReadAllText(full));
    }
}
