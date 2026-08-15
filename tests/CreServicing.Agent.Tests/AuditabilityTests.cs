using System.Globalization;
using CreServicing.Agent.Domain;

namespace CreServicing.Agent.Tests;

/// <summary>
/// The properties that justify computing any of this in C# rather than asking a
/// model for the answer.
///
/// The band tests next door prove the engine gets today's arithmetic right. These
/// prove the three things that make a finding usable as an audit artifact:
/// it is the same on every run, it is the same on every machine, and it never
/// asserts a code that is not part of the covenant vocabulary. A model can be
/// coaxed into the right answer; it cannot offer any of these.
/// </summary>
public class AuditabilityTests
{
    /// <summary>
    /// A loan breaching several covenants at once, so the assertions below have
    /// real output to chew on rather than an empty list.
    /// </summary>
    private static (LoanTerms Terms, FinancialSnapshot Snapshot) DistressedLoan()
        => (Given.CompliantTerms with
            {
                CurrentPrincipal = 31_000_000m,          // LTV 0.775 — over the 0.75 ceiling
                MaturityDate = Given.AsOf.AddDays(90)    // inside the 180-day horizon
            },
            Given.CompliantSnapshot with
            {
                NetOperatingIncome = Given.NoiForDscr(1.10m),      // under the 1.25 floor
                OccupancyRate = 0.72m,                             // under the 0.85 floor
                InsuranceCoverage = 18_000_000m,                   // under the 25m requirement
                InsuranceExpiration = Given.AsOf.AddDays(14)       // inside the 60-day horizon
            });

    [Fact]
    public void The_same_inputs_produce_byte_identical_findings_every_time()
    {
        // "Same inputs, same findings, every run" is asserted in the header comment
        // of Program.cs and in the README. This is the assertion that makes it true
        // rather than aspirational. Iteration order, severity, and the evidence
        // strings all have to match — the evidence is what an auditor re-checks by
        // hand, so drift there is drift in the audit trail.
        var (terms, snapshot) = DistressedLoan();

        var first = CovenantEngine.Evaluate(terms, snapshot, Given.AsOf);

        for (var run = 0; run < 50; run++)
        {
            var again = CovenantEngine.Evaluate(terms, snapshot, Given.AsOf);

            Assert.Equal(first.Count, again.Count);
            for (var i = 0; i < first.Count; i++)
            {
                // Records compare by value, so this covers code, severity, summary,
                // evidence and clause citation in one comparison.
                Assert.Equal(first[i], again[i]);
            }
        }
    }

    [Theory]
    [InlineData("hi-IN")]   // ₹ and the lakh/crore digit grouping
    [InlineData("de-DE")]   // swaps the roles of '.' and ','
    [InlineData("ja-JP")]   // no minor units on currency
    public void Evidence_renders_identically_regardless_of_the_machines_culture(string culture)
    {
        // The failure this prevents is quiet and nasty: the same breach filed from
        // a Bangalore laptop reads "₹2,21,80,000" and from a Frankfurt one
        // "22.180.000 $", and now two audit records of the same event disagree.
        // CovenantEngine pins en-US internally (its private `Us` field) precisely
        // so the ambient culture cannot reach the output. This test is what stops
        // someone "simplifying" that away.
        var (terms, snapshot) = DistressedLoan();
        var expected = CovenantEngine.Evaluate(terms, snapshot, Given.AsOf);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var actual = CovenantEngine.Evaluate(terms, snapshot, Given.AsOf);

            Assert.Equal(expected, actual);
            Assert.All(actual, finding =>
            {
                Assert.DoesNotContain("₹", finding.Evidence);
                Assert.DoesNotContain("₹", finding.Summary);
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Dollar_amounts_in_evidence_are_always_us_formatted()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("hi-IN");

            var findings = Given.Evaluate(
                snapshot: Given.CompliantSnapshot with { InsuranceCoverage = 18_000_000m });

            // 25,000,000 required less 18,000,000 held = a 7,000,000 shortfall,
            // grouped in thousands rather than the lakhs hi-IN would otherwise use.
            Assert.Contains("$7,000,000", findings.Single("INS-COVERAGE").Evidence);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Every_code_the_engine_emits_is_declared_in_KnownCodes()
    {
        // ServicingTools rejects any filing under a code outside KnownCodes, to
        // catch an agent inventing "OCCUPANCY-LOW". That check is only as good as
        // the set behind it: add a covenant test, forget to register its code, and
        // the engine starts emitting findings its own write path will refuse.
        var scenarios = new List<IReadOnlyList<ServicingException>>();

        var (terms, snapshot) = DistressedLoan();
        scenarios.Add(CovenantEngine.Evaluate(terms, snapshot, Given.AsOf));
        scenarios.Add(Given.Evaluate(snapshot: Given.CompliantSnapshot with { AppraisedValue = null }));
        scenarios.Add(Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { InsuranceExpiration = Given.AsOf.AddDays(-30) }));
        scenarios.Add(Given.Evaluate(terms: Given.CompliantTerms with { MaturityDate = Given.AsOf.AddDays(30) }));

        var emitted = scenarios.SelectMany(s => s).Select(f => f.Code).Distinct().ToList();

        Assert.NotEmpty(emitted);
        Assert.All(emitted, code =>
            Assert.True(
                CovenantEngine.KnownCodes.Contains(code),
                $"Engine emitted '{code}', which is not in CovenantEngine.KnownCodes. " +
                "ServicingTools would refuse to file it."));
    }

    [Fact]
    public void Every_finding_carries_evidence_a_human_can_recheck()
    {
        // A finding without its arithmetic is an assertion, not an audit record.
        // The whole point of the evidence field is that a servicer can re-derive
        // the call without rerunning the system.
        var (terms, snapshot) = DistressedLoan();

        Assert.All(CovenantEngine.Evaluate(terms, snapshot, Given.AsOf), finding =>
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.Summary));
            Assert.False(string.IsNullOrWhiteSpace(finding.Evidence));
            Assert.Equal(Given.LoanId, finding.LoanId);
        });
    }

    [Fact]
    public void Clause_citations_are_absent_until_section_11_grounds_them()
    {
        // Pins the current honest state: findings assert a breach but cannot yet
        // quote the covenant language it breaches. When S11 fills ClauseCitation
        // from the loan agreement, this test fails and gets rewritten — which is
        // the point. It marks the gap instead of letting it be forgotten.
        var (terms, snapshot) = DistressedLoan();

        Assert.All(
            CovenantEngine.Evaluate(terms, snapshot, Given.AsOf),
            finding => Assert.Null(finding.ClauseCitation));
    }
}
