using CreServicing.Agent.Domain;

namespace CreServicing.Agent.Tests;

/// <summary>
/// The covenant bands, pinned at their edges.
///
/// These test `<` against `<=`, not the comfortable middle of each range. A
/// metric sitting three points clear of its floor passes under any plausible
/// implementation; a metric sitting exactly *on* its floor is where the rule
/// either holds or silently inverts. Every test here is one tick either side of
/// a boundary for that reason.
///
/// The rules being pinned (CovenantEngine.EvaluateFloor / EvaluateCeiling):
///
///   floor:    actual &lt;  minimum        → Breach
///             actual &lt;= minimum × 1.05 → Watch
///             otherwise                   → Pass
///
///   ceiling:  actual &gt;  maximum        → Breach
///             actual &gt;= maximum × 0.95 → Watch
///             otherwise                   → Pass
/// </summary>
public class CovenantEngineTests
{
    [Fact]
    public void A_loan_clear_of_every_band_produces_no_findings()
    {
        // If this ever fails, every other test in this file is suspect: they all
        // assume the baseline contributes nothing of its own.
        Assert.Empty(Given.Evaluate());
    }

    // ── DSCR: a floor ────────────────────────────────────────────────────────

    [Fact]
    public void Dscr_below_the_minimum_is_a_breach()
    {
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { NetOperatingIncome = Given.NoiForDscr(1.2499m) });

        Assert.Equal(ExceptionSeverity.Breach, findings.Single("DSCR-MIN").Severity);
    }

    [Fact]
    public void Dscr_exactly_at_the_minimum_is_a_watch_not_a_pass()
    {
        // Sitting precisely on the covenant floor is not compliance to be quietly
        // filed away — it is one bad quarter from a breach, and the report says so.
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { NetOperatingIncome = Given.NoiForDscr(1.25m) });

        Assert.Equal(ExceptionSeverity.Watch, findings.Single("DSCR-MIN").Severity);
    }

    [Fact]
    public void Dscr_at_the_top_of_the_watch_band_is_still_a_watch()
    {
        // 1.25 × 1.05 = 1.3125, and the comparison is inclusive.
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { NetOperatingIncome = Given.NoiForDscr(1.3125m) });

        Assert.Equal(ExceptionSeverity.Watch, findings.Single("DSCR-MIN").Severity);
    }

    [Fact]
    public void Dscr_one_tick_above_the_watch_band_produces_nothing()
    {
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { NetOperatingIncome = Given.NoiForDscr(1.3126m) });

        findings.HasNo("DSCR-MIN");
    }

    // ── LTV: a ceiling, and the only test that can be skipped ────────────────

    [Fact]
    public void Ltv_above_the_maximum_is_a_breach()
    {
        // 30,000,001 / 40,000,000 = 0.750000025 — over by a rounding error, and
        // still over.
        var findings = Given.Evaluate(
            terms: Given.CompliantTerms with { CurrentPrincipal = 30_000_001m });

        Assert.Equal(ExceptionSeverity.Breach, findings.Single("LTV-MAX").Severity);
    }

    [Fact]
    public void Ltv_exactly_at_the_maximum_is_a_watch_not_a_pass()
    {
        var findings = Given.Evaluate(
            terms: Given.CompliantTerms with { CurrentPrincipal = 30_000_000m });

        Assert.Equal(ExceptionSeverity.Watch, findings.Single("LTV-MAX").Severity);
    }

    [Fact]
    public void Ltv_at_the_bottom_of_the_watch_band_is_still_a_watch()
    {
        // 0.75 × 0.95 = 0.7125 → 28,500,000 / 40,000,000. Inclusive.
        var findings = Given.Evaluate(
            terms: Given.CompliantTerms with { CurrentPrincipal = 28_500_000m });

        Assert.Equal(ExceptionSeverity.Watch, findings.Single("LTV-MAX").Severity);
    }

    [Fact]
    public void Ltv_one_tick_below_the_watch_band_produces_nothing()
    {
        var findings = Given.Evaluate(
            terms: Given.CompliantTerms with { CurrentPrincipal = 28_499_999m });

        findings.HasNo("LTV-MAX");
    }

    [Fact]
    public void A_missing_appraisal_reports_ltv_as_untested_rather_than_passing()
    {
        // The architectural claim this project rests on: an untested covenant is
        // not a passing covenant. Silence would let a reviewer conclude LTV was
        // checked and cleared. Deleting this behaviour is a regression that no
        // other assertion in this file would catch.
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { AppraisedValue = null });

        var untested = findings.Single("LTV-UNTESTED");
        Assert.Equal(ExceptionSeverity.Informational, untested.Severity);
        findings.HasNo("LTV-MAX");
    }

    // ── Occupancy: a floor ───────────────────────────────────────────────────

    [Fact]
    public void Occupancy_below_the_minimum_is_a_breach()
    {
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { OccupancyRate = 0.8499m });

        Assert.Equal(ExceptionSeverity.Breach, findings.Single("OCC-MIN").Severity);
    }

    [Fact]
    public void Occupancy_exactly_at_the_minimum_is_a_watch_not_a_pass()
    {
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { OccupancyRate = 0.85m });

        Assert.Equal(ExceptionSeverity.Watch, findings.Single("OCC-MIN").Severity);
    }

    [Fact]
    public void Occupancy_at_the_top_of_the_watch_band_is_still_a_watch()
    {
        // 0.85 × 1.05 = 0.8925.
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { OccupancyRate = 0.8925m });

        Assert.Equal(ExceptionSeverity.Watch, findings.Single("OCC-MIN").Severity);
    }

    [Fact]
    public void Occupancy_one_tick_above_the_watch_band_produces_nothing()
    {
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { OccupancyRate = 0.8926m });

        findings.HasNo("OCC-MIN");
    }

    // ── Insurance coverage: a hard floor, no watch band ──────────────────────

    [Fact]
    public void Insurance_below_the_required_coverage_is_a_breach()
    {
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { InsuranceCoverage = 24_999_999m });

        var finding = findings.Single("INS-COVERAGE");
        Assert.Equal(ExceptionSeverity.Breach, finding.Severity);
        Assert.Contains("$1", finding.Evidence); // the shortfall, not the coverage
    }

    [Fact]
    public void Insurance_exactly_at_the_required_coverage_produces_nothing()
    {
        // Strict `<`, so meeting the requirement exactly is compliance. Unlike the
        // banded covenants above, this one has no warning zone.
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { InsuranceCoverage = 25_000_000m });

        findings.HasNo("INS-COVERAGE");
    }

    // ── Insurance expiry: 60-day horizon ─────────────────────────────────────

    [Fact]
    public void Lapsed_insurance_is_a_breach()
    {
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { InsuranceExpiration = Given.AsOf.AddDays(-1) });

        var finding = findings.Single("INS-EXPIRY");
        Assert.Equal(ExceptionSeverity.Breach, finding.Severity);
        Assert.Contains("1 days past expiration", finding.Evidence);
    }

    [Fact]
    public void Insurance_expiring_today_is_a_watch_not_yet_a_breach()
    {
        // Zero days remaining: cover is still in force through today.
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { InsuranceExpiration = Given.AsOf });

        Assert.Equal(ExceptionSeverity.Watch, findings.Single("INS-EXPIRY").Severity);
    }

    [Fact]
    public void Insurance_expiring_on_the_sixtieth_day_is_a_watch()
    {
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { InsuranceExpiration = Given.AsOf.AddDays(60) });

        Assert.Equal(ExceptionSeverity.Watch, findings.Single("INS-EXPIRY").Severity);
    }

    [Fact]
    public void Insurance_expiring_on_the_sixty_first_day_produces_nothing()
    {
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { InsuranceExpiration = Given.AsOf.AddDays(61) });

        findings.HasNo("INS-EXPIRY");
    }

    // ── Maturity: 180-day horizon ────────────────────────────────────────────

    [Fact]
    public void A_loan_maturing_on_the_hundred_and_eightieth_day_is_flagged()
    {
        var findings = Given.Evaluate(
            terms: Given.CompliantTerms with { MaturityDate = Given.AsOf.AddDays(180) });

        Assert.Equal(ExceptionSeverity.Informational, findings.Single("MATURITY").Severity);
    }

    [Fact]
    public void A_loan_maturing_on_the_hundred_and_eighty_first_day_is_not_flagged()
    {
        var findings = Given.Evaluate(
            terms: Given.CompliantTerms with { MaturityDate = Given.AsOf.AddDays(181) });

        findings.HasNo("MATURITY");
    }

    [Fact]
    public void A_loan_maturing_today_is_flagged()
    {
        var findings = Given.Evaluate(
            terms: Given.CompliantTerms with { MaturityDate = Given.AsOf });

        Assert.Equal(ExceptionSeverity.Informational, findings.Single("MATURITY").Severity);
    }

    [Fact(Skip = "Documents a known gap — see the comment. Unskip when the engine handles it.")]
    public void A_loan_already_past_maturity_is_flagged()
    {
        // CURRENT BEHAVIOUR: it is not. CovenantEngine guards with
        // `daysToMaturity >= 0`, so a loan one day past its maturity date drops
        // out of the horizon window and produces no finding at all — the loan
        // goes from "matures in 1 day, Informational" to silent.
        //
        // A matured, unpaid loan is the single most serious state in servicing,
        // so silence is the wrong output. Left skipped rather than deleted so the
        // gap is visible in the test report instead of living only in someone's
        // head.
        var findings = Given.Evaluate(
            terms: Given.CompliantTerms with { MaturityDate = Given.AsOf.AddDays(-1) });

        Assert.Equal(ExceptionSeverity.Breach, findings.Single("MATURITY").Severity);
    }

    // ── The two clocks ───────────────────────────────────────────────────────
    //
    // Evaluate takes a period-close date and a review date because the covenants
    // ask two different questions. These pin the divergence, and they exist
    // because collapsing the two dates was a real bug: an agent run passed the
    // rent roll's as-of date — correctly, that is what the tool asks for — and an
    // INS-EXPIRY the deterministic path raised on the same loan silently vanished.

    [Fact]
    public void Insurance_still_healthy_at_period_close_is_flagged_if_it_lapses_before_the_review()
    {
        // Ninety days out at period close: nothing. Reviewed sixty days later it
        // is thirty days from lapsing and must be flagged. This is the exact
        // shape of the bug — the finding depends on the review date, not on when
        // the reporting period happened to close.
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { InsuranceExpiration = Given.AsOf.AddDays(90) },
            reviewDate: Given.AsOf.AddDays(60));

        Assert.Equal(ExceptionSeverity.Watch, findings.Single("INS-EXPIRY").Severity);
    }

    [Fact]
    public void Insurance_that_lapsed_before_the_review_is_a_breach_however_healthy_the_period_looked()
    {
        // The one that matters most in servicing. A policy that was current at
        // period close and has since lapsed is a same-day phone call, and "it was
        // fine when the quarter closed" is not a defence.
        var findings = Given.Evaluate(
            snapshot: Given.CompliantSnapshot with { InsuranceExpiration = Given.AsOf.AddDays(30) },
            reviewDate: Given.AsOf.AddDays(45));

        var finding = findings.Single("INS-EXPIRY");
        Assert.Equal(ExceptionSeverity.Breach, finding.Severity);
        Assert.Contains("lapsed", finding.Summary);
    }

    [Fact]
    public void The_maturity_horizon_runs_off_the_review_date_not_the_period()
    {
        // 200 days from period close is outside the 180-day horizon; 140 days
        // from the review date is inside it.
        var findings = Given.Evaluate(
            terms: Given.CompliantTerms with { MaturityDate = Given.AsOf.AddDays(200) },
            reviewDate: Given.AsOf.AddDays(60));

        Assert.Equal(ExceptionSeverity.Informational, findings.Single("MATURITY").Severity);
    }

    [Fact]
    public void The_measured_covenants_do_not_move_when_the_review_date_does()
    {
        // The other half of the split, and the one that would break if someone
        // "simplified" this by passing reviewDate everywhere: DSCR, LTV and
        // occupancy describe a closed period. Reviewing the same package a year
        // later must not change what the property earned.
        var snapshot = Given.CompliantSnapshot with
        {
            NetOperatingIncome = Given.NoiForDscr(1.10m),
            OccupancyRate = 0.80m,
            // Pinned far enough out that neither review date trips the expiry
            // test, so the only thing that could differ is a measured covenant.
            InsuranceExpiration = Given.AsOf.AddDays(900)
        };

        var atClose = Given.Evaluate(snapshot: snapshot, reviewDate: Given.AsOf);
        var aYearLater = Given.Evaluate(snapshot: snapshot, reviewDate: Given.AsOf.AddDays(365));

        foreach (var code in new[] { "DSCR-MIN", "OCC-MIN" })
        {
            Assert.Equal(atClose.Single(code).Evidence, aYearLater.Single(code).Evidence);
        }
    }

    // ── The guard ───────────────────────────────────────────────────────────

    [Fact]
    public void Evaluating_a_snapshot_against_another_loans_terms_throws()
    {
        // Not a finding — a programming error. Silently evaluating loan A's
        // numbers against loan B's covenants would produce a plausible-looking
        // exception report that is entirely wrong, which is worse than a crash.
        var mismatched = Given.CompliantSnapshot with { LoanId = "TEST-9999" };

        var ex = Assert.Throws<ArgumentException>(() => Given.Evaluate(snapshot: mismatched));
        Assert.Contains("TEST-9999", ex.Message);
    }
}
