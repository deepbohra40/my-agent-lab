namespace CreServicing.Core.Cost;

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
/// <param name="CachedInputTokens">
/// How many of <paramref name="InputTokens"/> were served from the provider's
/// prompt cache.
///
/// ── A SUBSET, not an addition ────────────────────────────────────────────────
///
/// This is the one thing to get right here, because getting it wrong is silent.
/// The provider reports cached tokens as part of the input count, not alongside
/// it: 4,564 input with 1,664 cached means 2,900 were billed at full rate and
/// 1,664 at the cached rate — it does NOT mean 6,228 tokens were processed.
/// Treating it as additive would inflate every figure this folder produces.
///
/// Defaulted so the two-argument construction used throughout the tests keeps
/// meaning "nothing cached", which is the right reading of a provider that did
/// not report it.
/// </param>
public readonly record struct ModelUsage(long InputTokens, long OutputTokens, long CachedInputTokens = 0)
{
    public static readonly ModelUsage None = new(0, 0, 0);

    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// Input tokens actually billed at the full rate — the ones the cache did not
    /// cover. Clamped because a provider reporting more cached than input is
    /// reporting nonsense, and nonsense should not produce a negative bill.
    /// </summary>
    public long BillableInputTokens => InputTokens - EffectiveCachedInputTokens;

    public long EffectiveCachedInputTokens => Math.Clamp(CachedInputTokens, 0, InputTokens);

    public static ModelUsage operator +(ModelUsage left, ModelUsage right)
        => new(
            left.InputTokens + right.InputTokens,
            left.OutputTokens + right.OutputTokens,
            left.CachedInputTokens + right.CachedInputTokens);

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
/// <param name="CachedInputUsdPer1M">
/// The rate for input tokens served from the prompt cache.
///
/// Not a rounding detail on the agent path. A servicing run re-sends the whole
/// conversation on every resume, so the cache hit rate climbs with each approval
/// round — exactly where a multi-finding package spends the most. Billing those
/// at the full input rate overstates the run, and
/// <see cref="PortfolioProjection.AnnualUsd"/> then multiplies the overstatement
/// by every loan and every quarter. An overstated cost model kills a project that
/// would have paid for itself, which is the same class of error as an understated
/// one and rather easier to miss.
/// </param>
public sealed record ModelRate(
    string Deployment,
    decimal InputUsdPer1M,
    decimal OutputUsdPer1M,
    decimal CachedInputUsdPer1M);

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

    /// <summary>
    /// Cached input is priced at a tenth of fresh input across this family, which
    /// is the published convention rather than a figure read off an invoice —
    /// same caveat as <see cref="PricedAsOf"/>, and the first thing to check if
    /// these numbers are ever quoted at anyone.
    /// </summary>
    private const decimal CachedInputDiscount = 0.10m;

    private static readonly IReadOnlyDictionary<string, ModelRate> Rates =
        new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5-mini"] = new("gpt-5-mini", 0.25m, 2.00m, 0.25m * CachedInputDiscount),
            ["gpt-5"] = new("gpt-5", 1.25m, 10.00m, 1.25m * CachedInputDiscount),
            ["gpt-5-nano"] = new("gpt-5-nano", 0.05m, 0.40m, 0.05m * CachedInputDiscount)
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

    /// <summary>
    /// Three lines, not two, because cached input is not free — it is cheap. The
    /// split is <see cref="ModelUsage.BillableInputTokens"/> at the full rate plus
    /// the cached remainder at the discounted one, which relies on cached tokens
    /// being a subset of the input count. See the note on
    /// <see cref="ModelUsage.CachedInputTokens"/>.
    /// </summary>
    public static decimal Usd(ModelUsage usage, ModelRate rate)
        => (usage.BillableInputTokens / 1_000_000m * rate.InputUsdPer1M)
           + (usage.EffectiveCachedInputTokens / 1_000_000m * rate.CachedInputUsdPer1M)
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
