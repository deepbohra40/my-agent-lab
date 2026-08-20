namespace CreServicing.Agent.Cost;

/// <summary>
/// Tokens consumed by model calls, and what they cost.
///
/// This is the roadmap's COST item, and the reason it earns its own folder rather
/// than a <c>Console.WriteLine</c> at the end of a run: a per-document cost times a
/// realistic portfolio is the number that decides whether any of this ships. An
/// extraction pipeline that is 98% accurate and costs more per package than the
/// analyst it replaces is a demo, not a system.
///
/// Everything here is pure arithmetic over counts the SDK already reports, so it
/// is unit-testable without an Azure call — same property the covenant engine has,
/// for the same reason.
/// </summary>
public readonly record struct ModelUsage(long InputTokens, long OutputTokens)
{
    public static readonly ModelUsage None = new(0, 0);

    public long TotalTokens => InputTokens + OutputTokens;

    public static ModelUsage operator +(ModelUsage left, ModelUsage right)
        => new(left.InputTokens + right.InputTokens, left.OutputTokens + right.OutputTokens);

    public static ModelUsage Sum(IEnumerable<ModelUsage> usages)
        => usages.Aggregate(None, static (running, next) => running + next);
}

/// <summary>
/// Published rates for one deployment, per million tokens.
///
/// Output tokens are the expensive ones — roughly 8x input on the model this
/// project runs — and on a reasoning model they include hidden reasoning tokens
/// you never see in the response. That asymmetry is why the structured-output
/// schemas here are kept narrow: every field the extractor is asked to return is
/// billed at the output rate on every document, forever.
/// </summary>
public sealed record ModelRate(string Deployment, decimal InputUsdPer1M, decimal OutputUsdPer1M);

public static class ModelPricing
{
    /// <summary>
    /// Rates move. Pinning the date they were read means a stale projection is
    /// visibly stale rather than quietly wrong — the same reason findings carry
    /// evidence strings instead of just verdicts.
    /// </summary>
    public const string PricedAsOf = "2026-08-20";

    /// <summary>South India, Global Standard, USD per 1M tokens.</summary>
    public const string PricingRegion = "South India / Global Standard";

    /// <summary>
    /// Indicative only, and not to be used for anything but reading the USD figure
    /// in a familiar unit. A real cost model would take the rate from the invoice.
    /// </summary>
    public const decimal UsdToInr = 88m;

    private static readonly IReadOnlyDictionary<string, ModelRate> Rates =
        new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5-mini"] = new("gpt-5-mini", InputUsdPer1M: 0.25m, OutputUsdPer1M: 2.00m),
            ["gpt-5"] = new("gpt-5", InputUsdPer1M: 1.25m, OutputUsdPer1M: 10.00m),
            ["gpt-5-nano"] = new("gpt-5-nano", InputUsdPer1M: 0.05m, OutputUsdPer1M: 0.40m)
        };

    /// <summary>
    /// The rate for a deployment, or null if it is not in the table.
    ///
    /// Null rather than a throw on purpose. An unrecognised deployment name means
    /// the cost display cannot be rendered; it does not mean the covenant review
    /// that just ran is invalid. Failing the run over a reporting line would be
    /// the tail wagging the dog.
    /// </summary>
    public static ModelRate? For(string deployment)
        => Rates.TryGetValue(deployment, out var rate) ? rate : null;

    public static decimal Usd(ModelUsage usage, ModelRate rate)
        => (usage.InputTokens / 1_000_000m * rate.InputUsdPer1M)
           + (usage.OutputTokens / 1_000_000m * rate.OutputUsdPer1M);
}

/// <summary>What one document cost to extract.</summary>
public sealed record DocumentCost(string FileName, ModelUsage Usage);

/// <summary>
/// What one borrower's reporting package cost to process, document by document.
///
/// Kept per-document rather than as a single total because the per-document figure
/// is the one that generalises: a package is however many documents the borrower
/// happened to send, but "a rent roll costs about this much to read" is a number
/// you can multiply by a portfolio.
/// </summary>
public sealed record PackageCost(string LoanId, IReadOnlyList<DocumentCost> Documents)
{
    public ModelUsage TotalUsage => ModelUsage.Sum(Documents.Select(d => d.Usage));

    public int DocumentCount => Documents.Count;
}

/// <summary>
/// A package cost extended over a portfolio. The arithmetic is trivial and the
/// number is not: it is the difference between "this works" and "this ships."
/// </summary>
public static class PortfolioProjection
{
    /// <summary>
    /// Quarterly reporting is the CRE norm — four packages per loan per year. Loan
    /// agreements vary and some require monthly, which is why this is a parameter
    /// with a default rather than a constant.
    /// </summary>
    public const int QuarterlyReportingPeriods = 4;

    public static decimal AnnualUsd(decimal usdPerPackage, int loanCount, int periodsPerYear = QuarterlyReportingPeriods)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usdPerPackage);
        ArgumentOutOfRangeException.ThrowIfNegative(loanCount);
        ArgumentOutOfRangeException.ThrowIfNegative(periodsPerYear);

        return usdPerPackage * loanCount * periodsPerYear;
    }
}
