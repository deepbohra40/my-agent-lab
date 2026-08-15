using System.Text.Json.Serialization;

namespace CreServicing.Agent.Domain;

// The data contracts the model fills in. Each one is a RunAsync<T> target — the
// same move as MeetingAnalysis in the section 5 scratchpad, applied to documents
// that actually vary.
//
// Two rules these records follow, both of which are interview answers:
//
//   1. Every extract carries its own provenance (SourceDocument, plus a
//      Confidence the model self-reports). An extraction you cannot trace back
//      to a page is not evidence, and a servicing exception has to be evidence.
//
//   2. Nullable where the document may genuinely not say. Forcing a non-null
//      decimal invites the model to invent one. A null is a routable signal —
//      it becomes an information request to the borrower, not a silent zero.

public enum DocumentType
{
    RentRoll,
    OperatingStatement,
    InsuranceCertificate,
    TaxBill,
    AppraisalReport,
    BorrowerCorrespondence,
    Unknown
}

/// <summary>Step one of the pipeline: what am I even looking at?</summary>
public record DocumentClassification(
    [property: JsonPropertyName("documentType")] DocumentType DocumentType,
    [property: JsonPropertyName("loanId")] string? LoanId,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reasoning")] string Reasoning);

public record RentRollExtract(
    [property: JsonPropertyName("sourceDocument")] string SourceDocument,
    [property: JsonPropertyName("asOf")] string AsOf,
    [property: JsonPropertyName("totalUnits")] int? TotalUnits,
    [property: JsonPropertyName("occupiedUnits")] int? OccupiedUnits,
    [property: JsonPropertyName("totalRentableSquareFeet")] decimal? TotalRentableSquareFeet,
    [property: JsonPropertyName("occupiedSquareFeet")] decimal? OccupiedSquareFeet,
    [property: JsonPropertyName("annualScheduledRent")] decimal? AnnualScheduledRent,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("notes")] string? Notes);

public record OperatingStatementExtract(
    [property: JsonPropertyName("sourceDocument")] string SourceDocument,
    [property: JsonPropertyName("periodStart")] string PeriodStart,
    [property: JsonPropertyName("periodEnd")] string PeriodEnd,
    [property: JsonPropertyName("effectiveGrossIncome")] decimal? EffectiveGrossIncome,
    [property: JsonPropertyName("operatingExpenses")] decimal? OperatingExpenses,
    [property: JsonPropertyName("reportedNetOperatingIncome")] decimal? ReportedNetOperatingIncome,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("notes")] string? Notes);

public record InsuranceCertificateExtract(
    [property: JsonPropertyName("sourceDocument")] string SourceDocument,
    [property: JsonPropertyName("carrier")] string? Carrier,
    [property: JsonPropertyName("policyNumber")] string? PolicyNumber,
    [property: JsonPropertyName("coverageAmount")] decimal? CoverageAmount,
    [property: JsonPropertyName("effectiveDate")] string? EffectiveDate,
    [property: JsonPropertyName("expirationDate")] string? ExpirationDate,
    [property: JsonPropertyName("lenderNamedAsMortgagee")] bool? LenderNamedAsMortgagee,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("notes")] string? Notes);

public record TaxBillExtract(
    [property: JsonPropertyName("sourceDocument")] string SourceDocument,
    [property: JsonPropertyName("taxYear")] int? TaxYear,
    [property: JsonPropertyName("parcelId")] string? ParcelId,
    [property: JsonPropertyName("amountDue")] decimal? AmountDue,
    [property: JsonPropertyName("dueDate")] string? DueDate,
    [property: JsonPropertyName("isPaid")] bool? IsPaid,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("notes")] string? Notes);
