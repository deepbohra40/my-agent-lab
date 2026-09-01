namespace CreServicing.Core.Domain;

/// <summary>
/// Covenant arithmetic. Pure functions, no model, no I/O.
///
/// This file is the whole argument of the project. A language model is good at
/// reading a scanned rent roll and telling you the property is 83.5% occupied.
/// It is the wrong tool for deciding whether 83.5% breaches an 85% floor —
/// that comparison must be reproducible, auditable, and identical on every run,
/// which is a property no sampled decoder can offer.
///
/// So: the model extracts, C# decides. Every number below is traceable to an
/// input, and the same inputs always yield the same exceptions.
/// </summary>
public static class Covenants
{
    /// <summary>Effective gross income less operating expenses.</summary>
    public static decimal NetOperatingIncome(decimal effectiveGrossIncome, decimal operatingExpenses)
        => effectiveGrossIncome - operatingExpenses;

    /// <summary>Debt service coverage ratio: NOI over annual debt service.</summary>
    public static decimal DebtServiceCoverageRatio(decimal netOperatingIncome, decimal annualDebtService)
        => annualDebtService == 0
            ? throw new ArgumentException("Annual debt service cannot be zero.", nameof(annualDebtService))
            : netOperatingIncome / annualDebtService;

    /// <summary>Loan to value: outstanding principal over appraised value.</summary>
    public static decimal LoanToValue(decimal currentPrincipal, decimal appraisedValue)
        => appraisedValue == 0
            ? throw new ArgumentException("Appraised value cannot be zero.", nameof(appraisedValue))
            : currentPrincipal / appraisedValue;

    /// <summary>Physical occupancy by unit count or by rentable square feet.</summary>
    public static decimal Occupancy(decimal occupied, decimal total)
        => total == 0
            ? throw new ArgumentException("Total cannot be zero.", nameof(total))
            : occupied / total;
}

public enum CovenantStatus
{
    /// <summary>Comfortably inside the threshold.</summary>
    Pass,

    /// <summary>Inside the threshold but within the warning band — surface it, don't cure it.</summary>
    Watch,

    /// <summary>Outside the threshold. Servicing exception, notice to borrower.</summary>
    Breach
}

public enum ExceptionSeverity
{
    Informational,
    Watch,
    Breach
}

/// <summary>
/// One finding against one loan.
///
/// <paramref name="Evidence"/> carries the arithmetic so a human can re-check the
/// call without rerunning anything. <paramref name="ClauseCitation"/> stays null
/// until Section 11 — that is the slot agentic RAG fills, quoting the covenant
/// language from the loan agreement that this finding is asserted under.
/// </summary>
public record ServicingException(
    string LoanId,
    string Code,
    ExceptionSeverity Severity,
    string Summary,
    string Evidence,
    string? ClauseCitation = null);
