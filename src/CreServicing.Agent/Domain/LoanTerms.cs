namespace CreServicing.Agent.Domain;

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
    decimal NetOperatingIncome,
    // Nullable, and the only one that is. A reporting package routinely arrives
    // without a current appraisal — they are commissioned every few years, not
    // every quarter. That is a gap in one covenant test, not grounds to abandon
    // the other four. Null here means "LTV untested", which CovenantEngine
    // reports as an Informational finding so the omission is visible in the
    // record rather than silently absent.
    decimal? AppraisedValue,
    decimal OccupancyRate,
    decimal InsuranceCoverage,
    DateOnly InsuranceExpiration);
