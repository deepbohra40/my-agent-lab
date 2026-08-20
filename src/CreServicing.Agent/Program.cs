using System.Globalization;
using CreServicing.Agent.Agents;
using CreServicing.Agent.Data;
using CreServicing.Agent.Domain;
using CreServicing.Agent.Extraction;

// CRE post-close document intake and covenant compliance.
//
// The scenario: a borrower submits a quarterly reporting package — rent roll,
// operating statement, insurance certificate, tax bill. Somebody has to read it,
// pull the numbers out, test them against the covenants in the loan agreement,
// and raise an exception when something fails. Today that somebody is an
// analyst with a spreadsheet.
//
// What runs right now is the half of that which must never be a model's job:
// load the package, run the covenant tests against known numbers, print the
// exception report. No Azure call, no cost, no API key. Run it and it works.
//
// The half that IS the model's job — reading the documents and producing those
// numbers — is the roadmap at the bottom of this file. Build it as the lectures
// land.
//
// Everything here is fabricated. See the header comment in MockServicingSystem.

// These are US dollars regardless of where the machine is. CovenantEngine pins
// its own formatting for the same reason; this covers the display lines below.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

// The extraction path is opt-in, because it is the only thing here that calls a
// model and therefore the only thing that costs money and needs `az login`.
// Everything below this branch runs offline, always.
//
//   dotnet run --project src/CreServicing.Agent -- --extract
//   dotnet run --project src/CreServicing.Agent -- --extract CRE-2021-0912/rent-roll-2026-Q2.txt
//if (args.FirstOrDefault() == "--extract")
//{
//    await RentRollExtractor.RunAsync(
//        args.ElementAtOrDefault(1) ?? "CRE-2019-0447/rent-roll-2026-Q2.txt");
//    return;
//}

// The assembled-snapshot path. Same caveat as --extract, times four: it calls
// one model per document type instead of one, so it costs more and still needs
// `az login`. This is the S5 milestone made runnable — the same FinancialSnapshot
// the free path below reads from MockServicingSystem's hand-keyed dictionary,
// produced instead from the borrower's actual documents via
// FinancialSnapshotAssembler. Deliberately additive rather than a replacement of
// the free path: the zero-cost default below stays zero-cost.
//
//   dotnet run --project src/CreServicing.Agent -- --extract-snapshot CRE-2019-0447
//   dotnet run --project src/CreServicing.Agent -- --extract-snapshot CRE-2021-0912
if (args.FirstOrDefault() == "--extract-snapshot")
{
    await RunExtractSnapshotDemo(args.ElementAtOrDefault(1) ?? "CRE-2019-0447");
    return;
}

// The section 6 path. Same caveat as --extract — it calls a model, so it costs
// money and needs `az login`. Costs more than --extract, because the tool loop
// is several round trips rather than one.
//
//   dotnet run --project src/CreServicing.Agent -- --agent
//   dotnet run --project src/CreServicing.Agent -- --agent CRE-2021-0912
if (args.FirstOrDefault() == "--agent")
{
    await ServicingAgentHost.RunAsync(args.ElementAtOrDefault(1) ?? "CRE-2019-0447");
    return;
}

var asOfDate = DateOnly.FromDateTime(DateTime.Today);
var requested = args.FirstOrDefault();

var loanIds = requested is not null
    ? [requested]
    : MockServicingSystem.LoanIds.OrderBy(id => id).ToArray();

Console.WriteLine("CRE SERVICING — COVENANT COMPLIANCE REVIEW");
Console.WriteLine($"Review date: {asOfDate:yyyy-MM-dd}");
Console.WriteLine($"Loans in scope: {loanIds.Length}");
Console.WriteLine(new string('=', 78));

var allFindings = new List<ServicingException>();

foreach (var loanId in loanIds)
{
    if (!MockServicingSystem.TryGetLoanTerms(loanId, out var terms) || terms is null)
    {
        Console.WriteLine($"\n{loanId} — not in the servicing system. Known loans: " +
                          string.Join(", ", MockServicingSystem.LoanIds));
        continue;
    }

    Console.WriteLine();
    Console.WriteLine($"{terms.LoanId}  {terms.PropertyName}");
    Console.WriteLine($"  Borrower        {terms.BorrowerName}");
    Console.WriteLine($"  Collateral      {terms.PropertyType}");
    Console.WriteLine($"  Principal       {terms.CurrentPrincipal:C0} of {terms.OriginalPrincipal:C0} original");
    Console.WriteLine($"  Covenants       DSCR >= {terms.MinimumDscr:F2}   " +
                      $"LTV <= {terms.MaximumLtv:P0}   " +
                      $"Occupancy >= {terms.MinimumOccupancy:P0}");
    Console.WriteLine($"  Maturity        {terms.MaturityDate:yyyy-MM-dd}");

    // The reporting package the agent will eventually read.
    var package = SafeGetPackage(terms.LoanId);
    Console.WriteLine($"  Package         {(package.Count == 0 ? "no documents on file" : $"{package.Count} document(s)")}");
    foreach (var document in package)
    {
        Console.WriteLine($"                    {document.FileName}  (~{document.ApproximateTokens:N0} tokens)");
    }

    // Today: numbers an analyst hand-keyed from that package.
    // Section 6: the same record, produced by the agent from the same documents.
    var snapshot = MockServicingSystem.GetHandKeyedSnapshot(terms.LoanId);

    var findings = CovenantEngine.Evaluate(terms, snapshot, asOfDate);
    allFindings.AddRange(findings);

    Console.WriteLine();
    if (findings.Count == 0)
    {
        Console.WriteLine("  RESULT          Compliant — no exceptions.");
        continue;
    }

    Console.WriteLine($"  RESULT          {findings.Count} exception(s)");
    foreach (var finding in findings.OrderByDescending(f => f.Severity))
    {
        Console.WriteLine();
        Console.WriteLine($"    [{finding.Severity.ToString().ToUpperInvariant()}] {finding.Code}");
        Console.WriteLine($"      {finding.Summary}");
        Console.WriteLine($"      Evidence: {finding.Evidence}");
        Console.WriteLine($"      Clause:   {finding.ClauseCitation ?? "(pending — Section 11 grounds this in the loan agreement)"}");
    }
}

Console.WriteLine();
Console.WriteLine(new string('=', 78));
Console.WriteLine(
    $"PORTFOLIO SUMMARY — " +
    $"{allFindings.Count(f => f.Severity == ExceptionSeverity.Breach)} breach, " +
    $"{allFindings.Count(f => f.Severity == ExceptionSeverity.Watch)} watch, " +
    $"{allFindings.Count(f => f.Severity == ExceptionSeverity.Informational)} informational");
Console.WriteLine();
Console.WriteLine("Every number above was computed in C#, not generated. Same inputs, same");
Console.WriteLine("findings, every run. That property is what the agent layer must not break.");

static IReadOnlyList<SourceDocument> SafeGetPackage(string loanId)
{
    try
    {
        return DocumentStore.GetPackage(loanId);
    }
    catch (DirectoryNotFoundException)
    {
        return [];
    }
}

/// <summary>
/// The --extract-snapshot demo: assembles a FinancialSnapshot from the loan's
/// documents and prints it beside the hand-keyed one, then runs CovenantEngine
/// against both so the payoff — same findings, real documents — is visible
/// rather than asserted.
/// </summary>
static async Task RunExtractSnapshotDemo(string loanId)
{
    var terms = MockServicingSystem.GetLoanTerms(loanId);
    var asOfDate = DateOnly.FromDateTime(DateTime.Today);

    Console.WriteLine("FINANCIAL SNAPSHOT ASSEMBLY");
    Console.WriteLine($"Loan  {loanId}");
    Console.WriteLine(new string('=', 78));
    Console.WriteLine();

    var assembled = await FinancialSnapshotAssembler.AssembleAsync(loanId, asOfDate);
    var handKeyed = MockServicingSystem.GetHandKeyedSnapshot(loanId);

    Console.WriteLine("SNAPSHOT — assembled from extraction vs. hand-keyed");
    Console.WriteLine($"  {"field",-22}{"assembled",-20}hand-keyed");
    Console.WriteLine($"  {new string('-', 22)}{new string('-', 20)}{new string('-', 20)}");
    Console.WriteLine($"  {"netOperatingIncome",-22}{assembled.NetOperatingIncome.ToString("C0"),-20}{handKeyed.NetOperatingIncome:C0}");
    Console.WriteLine($"  {"appraisedValue",-22}{(assembled.AppraisedValue?.ToString("C0") ?? "(null)"),-20}{handKeyed.AppraisedValue?.ToString("C0") ?? "(null)"}");
    Console.WriteLine($"  {"occupancyRate",-22}{assembled.OccupancyRate.ToString("P2"),-20}{handKeyed.OccupancyRate:P2}");
    Console.WriteLine($"  {"insuranceCoverage",-22}{assembled.InsuranceCoverage.ToString("C0"),-20}{handKeyed.InsuranceCoverage:C0}");
    Console.WriteLine($"  {"insuranceExpiration",-22}{assembled.InsuranceExpiration.ToString("yyyy-MM-dd"),-20}{handKeyed.InsuranceExpiration:yyyy-MM-dd}");
    Console.WriteLine();
    Console.WriteLine("  appraisedValue is expected to differ: no appraisal document exists in this");
    Console.WriteLine("  project's fixtures, so the assembled path always reports LTV-UNTESTED where");
    Console.WriteLine("  the hand-keyed path (a stale prior appraisal on file) tests LTV normally.");
    Console.WriteLine();

    Console.WriteLine("COVENANT FINDINGS — assembled snapshot");
    PrintFindings(CovenantEngine.Evaluate(terms, assembled, asOfDate));

    Console.WriteLine("COVENANT FINDINGS — hand-keyed snapshot");
    PrintFindings(CovenantEngine.Evaluate(terms, handKeyed, asOfDate));
}

static void PrintFindings(IReadOnlyList<ServicingException> findings)
{
    Console.WriteLine(new string('-', 78));
    if (findings.Count == 0)
    {
        Console.WriteLine("  Compliant — no exceptions.");
        Console.WriteLine();
        return;
    }

    foreach (var finding in findings.OrderByDescending(f => f.Severity))
    {
        Console.WriteLine($"  [{finding.Severity.ToString().ToUpperInvariant()}] {finding.Code}  {finding.Summary}");
    }
    Console.WriteLine();
}

// ─────────────────────────────────────────────────────────────────────────────
// Roadmap: what this grows into, section by section
// ─────────────────────────────────────────────────────────────────────────────
//
// S5  Extraction. Done — all four extractors exist (Extraction/), graded by
//     hand against fixtures/golden/expected-extractions.json, and
//     FinancialSnapshotAssembler turns the four extracts into the record
//     CovenantEngine tests. Reachable via --extract-snapshot <loanId>.
//
//     GetHandKeyedSnapshot was NOT deleted, on purpose: the free default path
//     above is documented as "no Azure call, no cost, no API key," and the
//     extraction path costs money on every run. Replacing the free path would
//     have broken that invariant silently. --extract-snapshot is additive —
//     it proves the assembled snapshot produces the same covenant findings as
//     the hand-keyed one (modulo the expected LTV-UNTESTED divergence, since
//     no appraisal document exists in these fixtures) without touching the
//     zero-cost default. CRE-2018-0233 has no fixture package by design — it
//     stays hand-keyed-only, demonstrating the "no documents on file" path.
//
// S6  Tools. Fill in Tools/ServicingTools.cs. The agent now decides which
//     documents to open instead of being handed all of them, and
//     CreateServicingException goes behind human approval.
//     Demo beat: approve one exception, reject another, show the run resume.
//
// S7  Memory. Prior-period snapshots per loan, so the agent sees the trend —
//     "occupancy has fallen for three consecutive quarters" is an asset
//     management conversation that no single-period test can produce.
//
// S8  Workflow. Classify → route by DocumentType to the right extractor.
//     The tax bill and the rent roll should not share a prompt.
//
// S9  Patterns. Fan the four extractors out concurrently, aggregate into one
//     snapshot, then one covenant pass. Handoff to an escalation agent when a
//     breach is severe enough to involve the asset manager.
//     Also: the reconciliation the operating statement fixture sets up —
//     borrower-reported NOI is 2,284,000, recomputed is 2,130,000, and the
//     154,000 add-back is a finding in its own right. Do not let it pass.
//
// S11 RAG. Index the loan agreement in Qdrant and fill ClauseCitation with the
//     covenant language the finding is asserted under. An exception that quotes
//     section 7.3(b) is a document a servicer can send. One that says "DSCR is
//     low" is not.
//
// S13 A2A. Expose covenant evaluation as a service. The credible story is a
//     portfolio-surveillance agent that calls it across many loans.
//
// S14 MCP. Turn MockServicingSystem into an MCP server so the agent reaches the
//     system of record over a protocol. This is the section that maps most
//     directly onto integrating with a real servicing platform.
//
// S15 AG-UI. Exception queue with the approval step in the loop, streaming.
//
// ─────────────────────────────────────────────────────────────────────────────
// Not in the course, and asked about in interviews. Budget time separately.
// ─────────────────────────────────────────────────────────────────────────────
//
// EVAL   An xUnit project over fixtures/golden/. Field-level extraction accuracy,
//        and a test that fails when a prompt edit regresses it. Grow the golden
//        set to ~20 documents. This is the strongest single artifact here —
//        "I measured it" is rare, and it is the difference between having taken
//        a course and having built something.
//
// SEC    fixtures/adversarial/ must be a passing test, not a curiosity. Untrusted
//        document text, delimited and labelled as data. Prove the injection fails
//        AND that the attempt gets surfaced.
//
// COST   Tokens and dollars per package, from response.Usage — the same reporting
//        you already kept in my-agent-lab after dropping OTel. A per-document
//        cost, times a realistic portfolio, is the number that decides whether
//        any of this ships.
//
// OCR    Real packages are scans. Azure Document Intelligence behind
//        DocumentStore. Know that prebuilt-layout exists and roughly what it
//        costs per page even if you never wire it up.
//
// FAIL   What happens when extraction confidence is low, a field is null, or two
//        documents disagree? The answer that impresses is never "retry" — it is
//        "route to a human, with the specific question that needs answering."
