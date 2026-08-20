using System.Globalization;
using CreServicing.Agent.Configuration;
using CreServicing.Agent.Agents;
using CreServicing.Agent.Cost;
using CreServicing.Agent.Data;
using CreServicing.Agent.Domain;
using CreServicing.Agent.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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

// ── The composition root ─────────────────────────────────────────────────────
//
// The model-backed paths resolve their dependencies from here rather than newing
// up a client inline. Two things follow from that which are easy to miss:
//
//   1. The container is built lazily, inside each branch. The default path below
//      is documented as needing no credential and no configuration, and eagerly
//      building a host that validates AZURE_OPENAI_ENDPOINT would break that for
//      everyone running the free path — turning a startup-validation improvement
//      into a regression for the one path that was always meant to just work.
//
//   2. Ctrl+C is wired to a CancellationToken rather than killing the process.
//      A servicing run that is halfway through filing exceptions should unwind,
//      not vanish.
using var lifetime = new ConsoleLifetime();

static IHost BuildHost()
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddCreServicing(builder.Configuration);
    var host = builder.Build();

    // Force the options to bind and validate here rather than letting the first
    // model call trip over it. A misconfiguration should read as a setup problem
    // with a named setting, not as an unhandled exception three frames inside an
    // extractor — the whole point of validating at startup is lost if the operator
    // still has to read a stack trace to find out which key is missing.
    try
    {
        _ = host.Services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
    }
    catch (OptionsValidationException ex)
    {
        Console.Error.WriteLine("Configuration error — nothing was run and nothing was charged.");
        foreach (var failure in ex.Failures)
        {
            Console.Error.WriteLine($"  {failure}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("The default `dotnet run` with no flag needs none of this and still works.");
        host.Dispose();
        Environment.Exit(1);
    }

    return host;
}

// The extraction path is opt-in, because it is the only thing here that calls a
// model and therefore the only thing that costs money and needs a credential.
// Everything below this branch runs offline, always.
//
//   dotnet run --project src/CreServicing.Agent -- --extract
//   dotnet run --project src/CreServicing.Agent -- --extract CRE-2021-0912/rent-roll-2026-Q2.txt
if (args.FirstOrDefault() == "--extract")
{
    using var host = BuildHost();
    await host.Services.GetRequiredService<RentRollExtractor>().RunAsync(
        args.ElementAtOrDefault(1) ?? "CRE-2019-0447/rent-roll-2026-Q2.txt",
        lifetime.Token);
    return;
}

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
    using var host = BuildHost();
    await RunExtractSnapshotDemo(
        host.Services.GetRequiredService<FinancialSnapshotAssembler>(),
        host.Services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value.Deployment,
        args.ElementAtOrDefault(1) ?? "CRE-2019-0447",
        lifetime.Token);
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
    using var host = BuildHost();
    await host.Services.GetRequiredService<ServicingAgentHost>().RunAsync(
        args.ElementAtOrDefault(1) ?? "CRE-2019-0447",
        lifetime.Token);
    return;
}

// The review is happening now; the snapshots it reads carry their own period-close
// date. CovenantEngine takes both because the measured covenants and the
// time-horizon ones run on different clocks — see the header on Evaluate.
var reviewDate = DateOnly.FromDateTime(DateTime.Today);
var requested = args.FirstOrDefault();

var loanIds = requested is not null
    ? [requested]
    : MockServicingSystem.LoanIds.OrderBy(id => id).ToArray();

Console.WriteLine("CRE SERVICING — COVENANT COMPLIANCE REVIEW");
Console.WriteLine($"Review date: {reviewDate:yyyy-MM-dd}");
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

    // The period-close date comes off the snapshot itself rather than being
    // passed in — the snapshot is the thing that knows which period it measured.
    var findings = CovenantEngine.Evaluate(terms, snapshot, snapshot.AsOf, reviewDate);
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
static async Task RunExtractSnapshotDemo(
    FinancialSnapshotAssembler assembler,
    string deployment,
    string loanId,
    CancellationToken cancellationToken)
{
    var terms = MockServicingSystem.GetLoanTerms(loanId);
    var reviewDate = DateOnly.FromDateTime(DateTime.Today);

    Console.WriteLine("FINANCIAL SNAPSHOT ASSEMBLY");
    Console.WriteLine($"Loan  {loanId}");
    Console.WriteLine(new string('=', 78));
    Console.WriteLine();

    var handKeyed = MockServicingSystem.GetHandKeyedSnapshot(loanId);

    // The assembled snapshot is stamped with the same period-close date the
    // hand-keyed one carries, so the two are compared like with like. Deriving it
    // from the rent roll's own asOf would be better still — the document states
    // it — but the assembler does not read that field back out today, and using
    // DateTime.Today here would have made the two snapshots disagree about which
    // period they measured while the demo claims they are the same numbers.
    var assembly = await assembler.AssembleAsync(loanId, handKeyed.AsOf, cancellationToken);
    var assembled = assembly.Snapshot;

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
    PrintFindings(CovenantEngine.Evaluate(terms, assembled, assembled.AsOf, reviewDate));

    Console.WriteLine("COVENANT FINDINGS — hand-keyed snapshot");
    PrintFindings(CovenantEngine.Evaluate(terms, handKeyed, handKeyed.AsOf, reviewDate));

    // The other half of the comparison, and the one that decides whether any of
    // this ships. The hand-keyed path above costs an analyst's time; this one
    // costs tokens, and until both are on the page the trade is not visible.
    CostReport.PrintPackage(assembly.Cost, deployment);
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
// COST   Done — Cost/ModelCost.cs and Cost/CostReport.cs, printed at the end of
//        --extract-snapshot. Extractors return ExtractionResult<T> carrying the
//        usage for that call rather than writing into an ambient ledger, so
//        concurrent extraction (the S9 fan-out) accounts correctly with no
//        coordination.
//
//        The measured answer, CRE-2019-0447 on gpt-5-mini: 2,276 input and
//        1,885 output tokens across three documents, $0.0043 per package.
//        Quarterly across 5,000 loans that is $87 a year. Cost is not the
//        constraint on this system and it is worth knowing that with a number
//        rather than assuming it either way — the constraint is extraction
//        accuracy and the human review loop, which is where the effort went.
//
//        Two things the figure does not cover, both called out in the report
//        itself: the S6 agent loop is several round trips per package rather
//        than three one-shot calls, and a real package is scanned pages
//        through OCR rather than clean text.
//
// OCR    Real packages are scans. Azure Document Intelligence behind
//        DocumentStore. Know that prebuilt-layout exists and roughly what it
//        costs per page even if you never wire it up.
//
// FAIL   What happens when extraction confidence is low, a field is null, or two
//        documents disagree? The answer that impresses is never "retry" — it is
//        "route to a human, with the specific question that needs answering."
