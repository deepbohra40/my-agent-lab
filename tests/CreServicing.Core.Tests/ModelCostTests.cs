using CreServicing.Core.Cost;

namespace CreServicing.Core.Tests;

/// <summary>
/// The cost arithmetic, pinned.
///
/// These belong in the free suite rather than the eval project for the same
/// reason the covenant tests do: nothing here calls a model. <c>Cost/</c> holds no
/// SDK reference at all — the mapping from the SDK's usage report lives in
/// <c>Extraction/</c> — which is what makes a per-package dollar figure something
/// CI can assert on without credentials.
///
/// Worth being clear about what is and is not being tested. The rate table is
/// data: if Azure changes its published prices these tests keep passing and the
/// projection is quietly wrong, which is why <see cref="ModelPricing.PricedAsOf"/>
/// exists and is printed alongside every figure. What is tested is that the
/// arithmetic over those rates is right, that summation does not lose calls, and
/// that an unknown deployment degrades to "unpriced" rather than throwing in the
/// middle of a covenant review.
/// </summary>
public class ModelCostTests
{
    private static readonly ModelRate GptFiveMini =
        ModelPricing.For("gpt-5-mini") ?? throw new InvalidOperationException("gpt-5-mini must be priced.");

    // ── Usage arithmetic ─────────────────────────────────────────────────────

    [Fact]
    public void Usage_adds_both_directions_independently()
    {
        var sum = new ModelUsage(1_000, 200) + new ModelUsage(500, 75);

        Assert.Equal(1_500, sum.InputTokens);
        Assert.Equal(275, sum.OutputTokens);
        Assert.Equal(1_775, sum.TotalTokens);
    }

    [Fact]
    public void Summing_no_calls_is_zero_not_an_exception()
    {
        // An empty package is a real case — CRE-2018-0233 has no documents on
        // file. It should report a zero cost, not blow up the run.
        Assert.Equal(ModelUsage.None, ModelUsage.Sum([]));
    }

    [Fact]
    public void Summing_a_package_keeps_every_call()
    {
        // Three documents, three calls. A fold that drops one is the failure this
        // catches — and it would understate the portfolio projection, which is the
        // direction of error nobody notices.
        var usages = new[]
        {
            new ModelUsage(1_000, 100),
            new ModelUsage(2_000, 200),
            new ModelUsage(3_000, 300)
        };

        var total = ModelUsage.Sum(usages);

        Assert.Equal(6_000, total.InputTokens);
        Assert.Equal(600, total.OutputTokens);
    }

    // ── Pricing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Usd_prices_input_and_output_at_their_own_rates()
    {
        // 1M input at $0.25 + 1M output at $2.00 = $2.25. The whole point of
        // separate rates is that output is the expensive side; a formula that
        // averaged them would pass a total-token test and fail this one.
        var usd = ModelPricing.Usd(new ModelUsage(1_000_000, 1_000_000), GptFiveMini);

        Assert.Equal(2.25m, usd);
    }

    [Fact]
    public void Usd_does_not_round_a_realistic_document_to_zero()
    {
        // A ~550-token fixture with a small structured response. Fractions of a
        // cent per document are the norm here, so a decimal that truncated to
        // cents would report every extraction as free — and "free" times a
        // portfolio is still free, which is exactly the wrong conclusion.
        var usd = ModelPricing.Usd(new ModelUsage(600, 250), GptFiveMini);

        Assert.True(usd > 0m, "A priced call must not cost zero.");
        Assert.Equal(0.00065m, usd);
    }

    // ── Prompt caching ───────────────────────────────────────────────────────

    [Fact]
    public void Cached_input_is_billed_at_the_cached_rate_not_the_full_one()
    {
        // 1M input of which 800k came from cache: 200k at $0.25/1M plus 800k at
        // a tenth of that, and no output.
        //   fresh  200,000 / 1M * 0.25  = 0.05
        //   cached 800,000 / 1M * 0.025 = 0.02
        var usd = ModelPricing.Usd(new ModelUsage(1_000_000, 0, CachedInputTokens: 800_000), GptFiveMini);

        Assert.Equal(0.07m, usd);

        // The number this replaced. Billing all 1M at the full rate — which is
        // what this did before the live run surfaced a cache_read tag on a span
        // that the cost model knew nothing about — reads 3.5x too high.
        Assert.Equal(0.25m, ModelPricing.Usd(new ModelUsage(1_000_000, 0), GptFiveMini));
    }

    [Fact]
    public void Cached_tokens_are_a_subset_of_input_and_are_never_added_to_it()
    {
        // The failure mode worth pinning: reading the provider's cached count as
        // additional rather than included. 4,564 input with 1,664 cached is one
        // call of 4,564 tokens, not 6,228.
        var usage = new ModelUsage(4_564, 86, CachedInputTokens: 1_664);

        Assert.Equal(4_564, usage.InputTokens);
        Assert.Equal(2_900, usage.BillableInputTokens);
        Assert.Equal(4_650, usage.TotalTokens);
    }

    [Fact]
    public void A_provider_reporting_more_cached_than_input_cannot_produce_a_negative_bill()
    {
        // Nonsense in, zero-or-positive out. Nothing should be able to make a
        // covenant review report that it earned money.
        var usage = new ModelUsage(100, 0, CachedInputTokens: 5_000);

        Assert.Equal(0, usage.BillableInputTokens);
        Assert.Equal(100, usage.EffectiveCachedInputTokens);
        Assert.True(ModelPricing.Usd(usage, GptFiveMini) > 0m);
    }

    [Fact]
    public void Unreported_caching_prices_exactly_as_it_did_before()
    {
        // A provider that says nothing about caching must not get a silent
        // discount. Two-argument construction means "nothing cached", so this has
        // to match the full-rate arithmetic to the last decimal.
        Assert.Equal(
            ModelPricing.Usd(new ModelUsage(600, 250), GptFiveMini),
            ModelPricing.Usd(new ModelUsage(600, 250, CachedInputTokens: 0), GptFiveMini));
    }

    [Fact]
    public void Summing_keeps_cached_tokens_alongside_the_rest()
    {
        var total = ModelUsage.Sum([
            new ModelUsage(1_000, 100, CachedInputTokens: 400),
            new ModelUsage(2_000, 200, CachedInputTokens: 1_500)
        ]);

        Assert.Equal(3_000, total.InputTokens);
        Assert.Equal(300, total.OutputTokens);
        Assert.Equal(1_900, total.CachedInputTokens);
    }

    [Fact]
    public void Zero_usage_costs_nothing()
        => Assert.Equal(0m, ModelPricing.Usd(ModelUsage.None, GptFiveMini));

    [Fact]
    public void An_unknown_deployment_is_unpriced_rather_than_fatal()
    {
        // The covenant findings from a run are valid whether or not the cost line
        // can be rendered. Failing the run over a display concern would be the
        // tail wagging the dog.
        Assert.Null(ModelPricing.For("some-deployment-nobody-priced"));
    }

    [Fact]
    public void Deployment_lookup_ignores_case()
        => Assert.NotNull(ModelPricing.For("GPT-5-MINI"));

    [Fact]
    public void Output_is_priced_above_input_on_every_rate_in_the_table()
    {
        // Not arbitrary: the extraction schemas are kept narrow because output is
        // the expensive side. If a rate ever inverts, that design note stops being
        // true and this test is where it should surface.
        foreach (var deployment in new[] { "gpt-5-mini", "gpt-5", "gpt-5-nano" })
        {
            var rate = ModelPricing.For(deployment);
            Assert.NotNull(rate);
            Assert.True(
                rate!.OutputUsdPer1M > rate.InputUsdPer1M,
                $"{deployment}: output rate should exceed input rate.");
        }
    }

    // ── Package aggregation ──────────────────────────────────────────────────

    [Fact]
    public void Package_total_is_the_sum_of_its_documents()
    {
        var cost = new PackageCost("CRE-2019-0447", [
            new DocumentCost("rent-roll-2026-Q2.txt", new ModelUsage(600, 250)),
            new DocumentCost("operating-statement-2026-Q2.txt", new ModelUsage(700, 300)),
            new DocumentCost("insurance-certificate-2026.txt", new ModelUsage(500, 200))
        ]);

        Assert.Equal(3, cost.DocumentCount);
        Assert.Equal(1_800, cost.TotalUsage.InputTokens);
        Assert.Equal(750, cost.TotalUsage.OutputTokens);
    }

    // ── Portfolio projection ─────────────────────────────────────────────────

    [Fact]
    public void Annual_projection_multiplies_by_loans_and_periods()
    {
        // $0.01 a package, 1,000 loans, four quarters = $40 a year. The number
        // being small is the finding; the arithmetic still has to be right for
        // anyone to believe it.
        Assert.Equal(40m, PortfolioProjection.AnnualUsd(0.01m, loanCount: 1_000));
    }

    [Fact]
    public void Annual_projection_honours_a_non_quarterly_reporting_frequency()
    {
        // Some loan agreements require monthly reporting — three times the
        // packages, three times the bill.
        Assert.Equal(120m, PortfolioProjection.AnnualUsd(0.01m, loanCount: 1_000, periodsPerYear: 12));
    }

    [Fact]
    public void An_empty_portfolio_costs_nothing()
        => Assert.Equal(0m, PortfolioProjection.AnnualUsd(0.01m, loanCount: 0));

    [Theory]
    [InlineData(-1)]
    public void A_negative_loan_count_is_rejected(int loanCount)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => PortfolioProjection.AnnualUsd(0.01m, loanCount));

    [Fact]
    public void A_negative_package_cost_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => PortfolioProjection.AnnualUsd(-0.01m, loanCount: 10));
}
