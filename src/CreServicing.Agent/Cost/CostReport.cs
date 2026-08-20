using System.Globalization;

namespace CreServicing.Agent.Cost;

/// <summary>
/// Renders the cost figures. Display only — every number it prints is computed in
/// <see cref="ModelPricing"/> and <see cref="PortfolioProjection"/>, which are
/// pure and tested.
///
/// Formatting is pinned to en-US for the same reason <c>CovenantEngine</c> pins
/// its own: a cost figure that renders differently depending on the machine's
/// locale is not a number anyone can quote in a business case.
/// </summary>
public static class CostReport
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// Portfolio sizes to project against. Three points rather than one because
    /// the interesting property of this cost model is how it scales, and a single
    /// number invites the reader to assume it is the only one that was checked.
    /// The lab's own MockServicingSystem holds three loans, which is a fixture
    /// count, not a portfolio.
    /// </summary>
    private static readonly int[] PortfolioSizes = [250, 1_000, 5_000];

    public static void PrintDocument(string fileName, ModelUsage usage, string deployment)
    {
        Console.WriteLine("COST — this document");
        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"  {fileName}");
        PrintUsageLines(usage, deployment, indent: "  ");
        Console.WriteLine();
    }

    /// <summary>
    /// The package view, and the projection off the back of it. This is the COST
    /// roadmap item's actual deliverable: not "what did that run cost" but "what
    /// would this cost across a book of loans, every quarter, forever."
    /// </summary>
    public static void PrintPackage(PackageCost cost, string deployment)
    {
        Console.WriteLine("COST — this package");
        Console.WriteLine(new string('-', 78));

        var rate = ModelPricing.For(deployment);

        Console.WriteLine($"  {"document",-40}{"input",10}{"output",10}{"USD",12}");
        Console.WriteLine($"  {new string('-', 40)}{new string('-', 10)}{new string('-', 10)}{new string('-', 12)}");

        foreach (var document in cost.Documents)
        {
            var usd = rate is null ? "(unpriced)" : ModelPricing.Usd(document.Usage, rate).ToString("F6", Us);
            Console.WriteLine(
                $"  {Truncate(document.FileName, 38),-40}" +
                $"{document.Usage.InputTokens,10:N0}" +
                $"{document.Usage.OutputTokens,10:N0}" +
                $"{usd,12}");
        }

        var total = cost.TotalUsage;
        Console.WriteLine($"  {new string('-', 40)}{new string('-', 10)}{new string('-', 10)}{new string('-', 12)}");
        Console.WriteLine(
            $"  {$"{cost.DocumentCount} document(s)",-40}" +
            $"{total.InputTokens,10:N0}" +
            $"{total.OutputTokens,10:N0}" +
            $"{(rate is null ? "(unpriced)" : ModelPricing.Usd(total, rate).ToString("F6", Us)),12}");
        Console.WriteLine();

        if (rate is null)
        {
            Console.WriteLine($"  No published rate on file for deployment '{deployment}', so the dollar");
            Console.WriteLine("  columns are blank. Token counts above are still real.");
            Console.WriteLine();
            return;
        }

        var perPackage = ModelPricing.Usd(total, rate);

        Console.WriteLine($"  Deployment  {rate.Deployment}  " +
                          $"(${rate.InputUsdPer1M:F2} in / ${rate.OutputUsdPer1M:F2} out per 1M tokens, " +
                          $"{ModelPricing.PricingRegion}, priced {ModelPricing.PricedAsOf})");
        Console.WriteLine($"  Per package {perPackage.ToString("F6", Us)} USD  " +
                          $"≈ ₹{(perPackage * ModelPricing.UsdToInr).ToString("F4", Us)}");
        Console.WriteLine();

        Console.WriteLine("  PROJECTED ANNUAL COST — quarterly reporting, one package per loan per quarter");
        Console.WriteLine($"    {"loans",-10}{"USD/year",14}{"INR/year",16}");
        Console.WriteLine($"    {new string('-', 10)}{new string('-', 14)}{new string('-', 16)}");
        foreach (var loans in PortfolioSizes)
        {
            var annual = PortfolioProjection.AnnualUsd(perPackage, loans);
            Console.WriteLine(
                $"    {loans,-10:N0}{annual.ToString("N2", Us),14}{(annual * ModelPricing.UsdToInr).ToString("N0", Us),16}");
        }

        Console.WriteLine();
        Console.WriteLine("  What this number is for: compare it against the analyst hours it displaces.");
        Console.WriteLine("  Extraction that is accurate and costs more than the person it replaces is a");
        Console.WriteLine("  demo, not a system. Note also that this covers extraction only — the S6 agent");
        Console.WriteLine("  loop is several round trips per package and costs materially more.");
        Console.WriteLine();
    }

    private static void PrintUsageLines(ModelUsage usage, string deployment, string indent)
    {
        Console.WriteLine($"{indent}input   {usage.InputTokens,9:N0} tokens");
        Console.WriteLine($"{indent}output  {usage.OutputTokens,9:N0} tokens  (includes hidden reasoning tokens)");

        if (ModelPricing.For(deployment) is not { } rate)
        {
            Console.WriteLine($"{indent}cost    no published rate on file for '{deployment}'");
            return;
        }

        var usd = ModelPricing.Usd(usage, rate);
        Console.WriteLine($"{indent}cost    {usd.ToString("F6", Us),9} USD  ≈ ₹{(usd * ModelPricing.UsdToInr).ToString("F4", Us)}");
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";
}
