using CreServicing.Agent.Cost;

namespace CreServicing.Agent.Tests;

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
