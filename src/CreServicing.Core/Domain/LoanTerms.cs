namespace CreServicing.Core.Domain;

/// <summary>
/// The covenant package for one loan, as it would live in the servicing system
/// of record. This is authoritative data — the agent reads it, never writes it,
/// and never infers it from a document.
/// </summary>
public record LoanTerms(
    string LoanId,
    string BorrowerName,
    string PropertyName,
    PropertyType PropertyType,
    decimal OriginalPrincipal,
    decimal CurrentPrincipal,
    decimal InterestRate,
    decimal AnnualDebtService,
    decimal MinimumDscr,
    decimal MaximumLtv,
    decimal MinimumOccupancy,
    decimal RequiredInsuranceCoverage,
    int FinancialReportingDueDays,
    DateOnly MaturityDate);

public enum PropertyType
{
    Office,
    Retail,
    Multifamily,
    Industrial,
    Hospitality
}

/// <summary>
/// The measured state of the collateral for a reporting period — the numbers a
/// covenant test is run against.
///
/// Today an analyst hand-keys this after reading the borrower's package. That is
/// exactly the step this project automates: by Section 6 the agent produces this
/// record from the fixtures in <c>fixtures/</c>, and everything downstream of it
/// stays deterministic C#.
/// </summary>
public record FinancialSnapshot(
    string LoanId,
    DateOnly AsOf,
    // Always the computed figure: effective gross income less operating
    // expenses. Never the borrower's own NOI line, even when the package prints
    // one. Every covenant test that touches NOI runs on this.
    decimal NetOperatingIncome,
    // Nullable, one of two. A reporting package routinely arrives without a
    // current appraisal — they are commissioned every few years, not every
    // quarter. That is a gap in one covenant test, not grounds to abandon the
    // other four. Null here means "LTV untested", which CovenantEngine reports
    // as an Informational finding so the omission is visible in the record
    // rather than silently absent.
    decimal? AppraisedValue,
    decimal OccupancyRate,
    decimal InsuranceCoverage,
    DateOnly InsuranceExpiration,
    // What the borrower says their NOI is, carried alongside what it actually
    // computes to. This field is never tested against a covenant — it exists to
    // be *disagreed with*. Where it and NetOperatingIncome diverge materially,
    // the borrower has made a judgement call inside a number the lender treats
    // as arithmetic, and CovenantEngine raises NOI-RECONCILE so the call is
    // visible before anyone relies on the ratio built from it.
    //
    // Null means the package printed no NOI line, so there is nothing to
    // reconcile — which is not the same as reconciling to zero difference, and
    // is why this is nullable rather than defaulted.
    decimal? ReportedNetOperatingIncome);
