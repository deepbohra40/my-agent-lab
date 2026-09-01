using System.Globalization;

namespace CreServicing.Core.Domain;

/// <summary>
/// Runs the covenant tests for one loan against one reporting period and returns
/// the exceptions. Deterministic: same terms plus same snapshot, same findings,
/// forever.
///
/// Nothing here is a tool the model calls directly. Section 6 exposes a thin
/// wrapper (<c>ServicingTools.EvaluateCovenants</c>) so the agent can *request*
/// an evaluation, but the judgement itself never leaves this file.
/// </summary>
public static class CovenantEngine
{
    /// <summary>
    /// How close to a threshold counts as Watch rather than Pass. 5% of the
    /// threshold value — a policy choice, which is why it lives in code where it
    /// can be reviewed, not in a prompt where it can be paraphrased away.
    /// </summary>
    private const decimal WatchBand = 0.05m;

    /// <summary>Insurance expiring inside this window is flagged before it lapses.</summary>
    private const int InsuranceExpiryWarningDays = 60;

    /// <summary>Loans maturing inside this window enter the workout conversation early.</summary>
    private const int MaturityWarningDays = 180;

    /// <summary>
    /// Evidence strings are audit artifacts. A finding that renders as $22,180,000
    /// on one machine and ₹2,21,80,000 on another is not reproducible, and
    /// reproducibility is the only reason to compute this in C# rather than ask a
    /// model. Formatting is pinned, never inherited from the thread.
    /// </summary>
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    private static string Usd(decimal value) => value.ToString("C0", Us);

    private static string Pct(decimal value, int decimals) => value.ToString($"P{decimals}", Us);

    private static string Ratio(decimal value, int decimals) => value.ToString($"F{decimals}", Us);

    /// <summary>
    /// Every code this engine can emit. Exposed so the write tool can reject a
    /// filing under a code no covenant test produces — an agent inventing
    /// "OCCUPANCY-LOW" is either confused or improvising, and either way that
    /// exception should not reach a borrower's file.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "DSCR-MIN",
        "LTV-MAX",
        "LTV-UNTESTED",
        "OCC-MIN",
        "INS-COVERAGE",
        "INS-EXPIRY",
        "MATURITY"
    };

    /// <summary>
    /// Runs every covenant test for one loan.
    ///
    /// ── Why there are two dates, and why neither defaults to the other ───────
    ///
    /// These tests ask two different questions and the answers move on different
    /// clocks:
    ///
    ///   • DSCR, LTV and occupancy ask "how did the collateral perform *during
    ///     the reporting period*?" — anchored to <paramref name="asOfDate"/>,
    ///     the date the period closed. That answer never changes once the period
    ///     is shut; re-running this in six months must produce the same DSCR.
    ///
    ///   • Insurance expiry and maturity horizon ask "is the policy valid, is
    ///     this loan maturing, *right now*?" — anchored to
    ///     <paramref name="reviewDate"/>. That answer changes every single day,
    ///     and it is a question about the present, not about the period.
    ///
    /// A single date collapsed both, and the failure was not hypothetical: an
    /// agent run passed the rent roll's as-of date (2026-06-30, correctly — that
    /// is what the tool asks for) against a policy expiring 2026-09-30. Ninety-two
    /// days out, so no finding. Reviewed on 2026-08-20 the same policy is 41 days
    /// from lapsing and inside the warning window. The agent path silently
    /// dropped an INS-EXPIRY the deterministic path raised.
    ///
    /// In servicing that is the wrong direction to be wrong. A lapsed policy on a
    /// $22M building is a same-day phone call, and "it was fine at period close"
    /// is not a defence. So both dates are required: a caller that has not thought
    /// about which clock a test runs on should not compile.
    /// </summary>
    /// <param name="asOfDate">
    /// When the reporting period closed. Drives the measured covenants.
    /// </param>
    /// <param name="reviewDate">
    /// When this review is being run — today, in practice. Drives the
    /// time-horizon tests. Passing <paramref name="asOfDate"/> here is legitimate
    /// only when reproducing a historical review as it stood on that day.
    /// </param>
    public static IReadOnlyList<ServicingException> Evaluate(
        LoanTerms terms,
        FinancialSnapshot snapshot,
        DateOnly asOfDate,
        DateOnly reviewDate)
    {
        if (terms.LoanId != snapshot.LoanId)
        {
            throw new ArgumentException(
                $"Snapshot is for {snapshot.LoanId} but terms are for {terms.LoanId}.");
        }

        var findings = new List<ServicingException>();

        // ── DSCR: floor test ─────────────────────────────────────────────────
        var dscr = Covenants.DebtServiceCoverageRatio(snapshot.NetOperatingIncome, terms.AnnualDebtService);
        var dscrStatus = EvaluateFloor(dscr, terms.MinimumDscr);
        if (dscrStatus != CovenantStatus.Pass)
        {
            findings.Add(new ServicingException(
                terms.LoanId,
                "DSCR-MIN",
                ToSeverity(dscrStatus),
                dscrStatus == CovenantStatus.Breach
                    ? $"DSCR of {Ratio(dscr, 3)} is below the {Ratio(terms.MinimumDscr, 2)} covenant minimum."
                    : $"DSCR of {Ratio(dscr, 3)} is within the warning band of the {Ratio(terms.MinimumDscr, 2)} minimum.",
                $"NOI {Usd(snapshot.NetOperatingIncome)} / annual debt service {Usd(terms.AnnualDebtService)} = {Ratio(dscr, 4)}"));
        }

        // ── LTV: ceiling test ────────────────────────────────────────────────
        //
        // The one test that can be skipped. An untested covenant is not a passing
        // covenant, so the omission is recorded as a finding rather than left to
        // be inferred from an absence — a reviewer reading this report must be
        // able to see that LTV was not evaluated and why.
        if (snapshot.AppraisedValue is not { } appraisedValue)
        {
            findings.Add(new ServicingException(
                terms.LoanId,
                "LTV-UNTESTED",
                ExceptionSeverity.Informational,
                "LTV could not be tested: no appraised value in the reporting package.",
                $"Current principal {Usd(terms.CurrentPrincipal)}; appraisal not on file as of {asOfDate:yyyy-MM-dd}."));
        }
        else
        {
            var ltv = Covenants.LoanToValue(terms.CurrentPrincipal, appraisedValue);
            var ltvStatus = EvaluateCeiling(ltv, terms.MaximumLtv);
            if (ltvStatus != CovenantStatus.Pass)
            {
                findings.Add(new ServicingException(
                    terms.LoanId,
                    "LTV-MAX",
                    ToSeverity(ltvStatus),
                    ltvStatus == CovenantStatus.Breach
                        ? $"LTV of {Pct(ltv, 2)} exceeds the {Pct(terms.MaximumLtv, 0)} covenant maximum."
                        : $"LTV of {Pct(ltv, 2)} is within the warning band of the {Pct(terms.MaximumLtv, 0)} maximum.",
                    $"Current principal {Usd(terms.CurrentPrincipal)} / appraised value {Usd(appraisedValue)} = {Ratio(ltv, 4)}"));
            }
        }

        // ── Occupancy: floor test ────────────────────────────────────────────
        var occupancyStatus = EvaluateFloor(snapshot.OccupancyRate, terms.MinimumOccupancy);
        if (occupancyStatus != CovenantStatus.Pass)
        {
            findings.Add(new ServicingException(
                terms.LoanId,
                "OCC-MIN",
                ToSeverity(occupancyStatus),
                occupancyStatus == CovenantStatus.Breach
                    ? $"Occupancy of {Pct(snapshot.OccupancyRate, 2)} is below the {Pct(terms.MinimumOccupancy, 0)} covenant minimum."
                    : $"Occupancy of {Pct(snapshot.OccupancyRate, 2)} is within the warning band of the {Pct(terms.MinimumOccupancy, 0)} minimum.",
                $"Reported occupancy {Ratio(snapshot.OccupancyRate, 4)} as of {snapshot.AsOf:yyyy-MM-dd}"));
        }

        // ── Insurance: coverage adequacy ─────────────────────────────────────
        if (snapshot.InsuranceCoverage < terms.RequiredInsuranceCoverage)
        {
            var shortfall = terms.RequiredInsuranceCoverage - snapshot.InsuranceCoverage;
            findings.Add(new ServicingException(
                terms.LoanId,
                "INS-COVERAGE",
                ExceptionSeverity.Breach,
                $"Property insurance of {Usd(snapshot.InsuranceCoverage)} is below the required {Usd(terms.RequiredInsuranceCoverage)}.",
                $"Shortfall of {Usd(shortfall)} against the required coverage."));
        }

        // ── Insurance: expiry ────────────────────────────────────────────────
        //
        // reviewDate, not asOfDate. "Is this building insured today" is a question
        // about today. See the header on Evaluate for the run where getting this
        // wrong silently dropped a finding.
        var daysToExpiry = snapshot.InsuranceExpiration.DayNumber - reviewDate.DayNumber;
        if (daysToExpiry < 0)
        {
            findings.Add(new ServicingException(
                terms.LoanId,
                "INS-EXPIRY",
                ExceptionSeverity.Breach,
                $"Property insurance lapsed on {snapshot.InsuranceExpiration:yyyy-MM-dd}.",
                $"{-daysToExpiry} days past expiration as of {reviewDate:yyyy-MM-dd}."));
        }
        else if (daysToExpiry <= InsuranceExpiryWarningDays)
        {
            findings.Add(new ServicingException(
                terms.LoanId,
                "INS-EXPIRY",
                ExceptionSeverity.Watch,
                $"Property insurance expires on {snapshot.InsuranceExpiration:yyyy-MM-dd}.",
                $"{daysToExpiry} days remaining as of {reviewDate:yyyy-MM-dd}; renewal certificate not yet on file."));
        }

        // ── Maturity horizon ─────────────────────────────────────────────────
        //
        // reviewDate for the same reason: a loan does not stop approaching
        // maturity because the reporting period it is measured against is old.
        var daysToMaturity = terms.MaturityDate.DayNumber - reviewDate.DayNumber;
        if (daysToMaturity >= 0 && daysToMaturity <= MaturityWarningDays)
        {
            findings.Add(new ServicingException(
                terms.LoanId,
                "MATURITY",
                ExceptionSeverity.Informational,
                $"Loan matures on {terms.MaturityDate:yyyy-MM-dd}.",
                $"{daysToMaturity} days to maturity as of {reviewDate:yyyy-MM-dd}; confirm payoff or extension intent."));
        }

        return findings;
    }

    /// <summary>A metric that must stay at or above a threshold.</summary>
    private static CovenantStatus EvaluateFloor(decimal actual, decimal minimum)
    {
        if (actual < minimum) return CovenantStatus.Breach;
        return actual <= minimum * (1 + WatchBand) ? CovenantStatus.Watch : CovenantStatus.Pass;
    }

    /// <summary>A metric that must stay at or below a threshold.</summary>
    private static CovenantStatus EvaluateCeiling(decimal actual, decimal maximum)
    {
        if (actual > maximum) return CovenantStatus.Breach;
        return actual >= maximum * (1 - WatchBand) ? CovenantStatus.Watch : CovenantStatus.Pass;
    }

    private static ExceptionSeverity ToSeverity(CovenantStatus status) => status switch
    {
        CovenantStatus.Breach => ExceptionSeverity.Breach,
        CovenantStatus.Watch => ExceptionSeverity.Watch,
        _ => ExceptionSeverity.Informational
    };
}
