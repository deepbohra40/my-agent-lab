using System.Globalization;
using CreServicing.Cli;
using CreServicing.Core.Configuration;
using CreServicing.Core.Cost;
using CreServicing.Core.Data;
using CreServicing.Core.Domain;
using CreServicing.Core.Extraction;
using CreServicing.Core.Runs;
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
// The default path is the half of that which must never be a model's job:
// load the package, run the covenant tests against known numbers, print the
// exception report. No Azure call, no cost, no API key. Run it and it works.
//
// The half that IS the model's job — reading the documents, and deciding which
// ones to open — is built too, behind the three flags below, because it costs
// money on every run and the free path above is meant to stay free. What is
// still missing, and what was skipped on purpose, is the roadmap at the bottom
// of this file.
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
//   dotnet run --project src/CreServicing.Cli -- --extract
//   dotnet run --project src/CreServicing.Cli -- --extract CRE-2021-0912/rent-roll-2026-Q2.txt
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
//   dotnet run --project src/CreServicing.Cli -- --extract-snapshot CRE-2019-0447
//   dotnet run --project src/CreServicing.Cli -- --extract-snapshot CRE-2021-0912
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
//   dotnet run --project src/CreServicing.Cli -- --agent
//   dotnet run --project src/CreServicing.Cli -- --agent CRE-2021-0912
if (args.FirstOrDefault() == "--agent")
{
    using var host = BuildHost();
    await ConsoleApprovalLoop.RunAsync(
        host.Services.GetRequiredService<ServicingRunService>(),
        args.ElementAtOrDefault(1) ?? "CRE-2019-0447",
        host.Services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value.Deployment,
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
// Numbered by the course sections this was built alongside, so the two can be
// read against each other. Every entry says done, open, or skipped — a section
// left out because it would demonstrate the framework rather than the domain is
// a decision, and a decision is worth more written down than a gap is.
//
// Open, in the order worth doing them: S11, then S14, then the S9 fan-out.
// Smaller open items sit under EVAL, OCR and FAIL below. Everything else is
// finished or deliberately not being built.
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
// S6  Tools. Done — Tools/ServicingTools.cs. The agent decides which documents
//     to open instead of being handed all of them, and CreateServicingException
//     goes behind human approval. Reachable via --agent.
//
//     The demo beat this was written for has actually been run: five rounds,
//     four approvals and one deliberate refusal, and the agent honoured the
//     refusal without retrying or rewording it. That run is also what surfaced
//     the approval loop dying after round one — a bug no test caught, because
//     every test approved everything. See ServicingRunService.
//
// S7  Memory. Split, and only half of it is deprioritised.
//
//     The half that exists: agent sessions and persisted run state, which the
//     approval loop needed anyway. See Core/Runs/.
//
//     The half deliberately not built: prior-period snapshots per loan, so the
//     agent sees the trend. "Occupancy has fallen for three consecutive
//     quarters" is an asset management conversation that no single-period test
//     can produce — worth being able to describe, not worth the fixture cost
//     of three quarters of hand-verified packages per loan.
//
// S8  Workflows and orchestration. Not built, on purpose. The orchestration
//     here is an agent tool loop; re-expressing it as a graph of executors and
//     edges would demonstrate the framework rather than the domain.
//
//     Document routing is not a workflow problem either, and is already
//     solved: FinancialSnapshotAssembler routes by filename substring. That is
//     an honest stand-in for a classifier — no classification step exists in
//     this project and none is planned — and the
//     interesting part — that the tax bill and the rent roll must not share a
//     prompt — is already true, since each extractor carries its own.
//
// S9  Patterns. Two of the three are worth building; handoff is not.
//
//     Worth doing, still open: fan the four extractors out concurrently and
//     aggregate into one snapshot, then one covenant pass. Cheap since the
//     extractors became injected instances with no shared static state.
//
//     Done — the reconciliation the operating statement fixture sets up.
//     Borrower-reported NOI is 2,284,000, recomputed from the statement's own
//     line items it is 2,130,000, and the 154,000 add-back is now a finding in
//     its own right: NOI-RECONCILE, raised by CovenantEngine against a 1%
//     materiality tolerance that sits beside the watch band. Direction sets
//     severity — overstated is a Watch because it flatters every ratio built on
//     it, understated is Informational.
//
//     The reconciliation changes no covenant outcome, deliberately. DSCR is
//     tested on the computed figure whether the finding fires or not, and
//     CovenantEngineTests pins exactly that: computed NOI breaching at 1.10
//     against a reported figure that would clear at 1.50, with the breach
//     standing. Rebasing a ratio onto the borrower's number is the failure this
//     work exists to prevent, so it is asserted rather than assumed.
//
//     Skipped: handoff to an escalation agent, and group chat. The routing
//     decision is a severity threshold, which is an if statement, and dressing
//     it as an agent pattern would make the system harder to audit rather than
//     more capable.
//
// S11 RAG. Open. Index the loan agreement and fill ClauseCitation with the
//     covenant language the finding is asserted under. An exception that quotes
//     section 7.3(b) is a document a servicer can send. One that says "DSCR is
//     low" is not.
//
//     Decide the store before writing any of it. The lectures use Qdrant in
//     Docker and this machine has none — so it is Qdrant Cloud, an in-memory
//     Microsoft.Extensions.VectorData store, or installing Docker. The design
//     consequence outlives the choice: CI is deliberately credential-free and
//     offline, so nothing Qdrant-backed can run there. ClauseCitation goes
//     behind an interface with an in-memory implementation for tests — the same
//     seam, for the same reason, as IRunStore and InMemoryRunStore.
//
// S13 A2A. Not built, on purpose. Exposing covenant evaluation as a service is
//     the credible story — a portfolio-surveillance agent calling it across many
//     loans — but CreServicing.Api already exposes it over HTTP, so what A2A
//     adds is a protocol, not a capability. Lowest weight of anything left.
//
// S14 MCP. Open. Turn MockServicingSystem into an MCP server so the agent
//     reaches the system of record over a protocol. This is the section that
//     maps most directly onto integrating with a real servicing platform.
//
// S15 AG-UI. Mostly done, and not by working on S15.
//
//     The hard part was never the UI — it was suspending a run mid-approval and
//     resuming it in a later request, which the HTTP surface needed first. That
//     state machine exists: Core/Runs/ServicingRun.cs and the two-request
//     approval flow in CreServicing.Api. What is left is streaming and a front
//     end. ServicingRunner does not stream yet on purpose;
//     ScriptedChatClient.GetStreamingResponseAsync throws with a comment
//     pointing here.
//
// ─────────────────────────────────────────────────────────────────────────────
// Not in the course, and asked about in interviews anyway.
// ─────────────────────────────────────────────────────────────────────────────
//
// EVAL   Done — tests/CreServicing.Core.Eval, a separate project from the free
//        suite because the moment a test needs a model it stops belonging in a
//        job that runs on every push. Field-level accuracy against
//        fixtures/golden/expected-extractions.json, live calls and exact-match
//        assertions rather than record/replay, so a prompt edit that regresses
//        extraction fails a test.
//
//        Two things still open, both known deferrals rather than oversights:
//        the golden set is 9 documents against a target of ~20, and the Eval
//        project is not wired into CI — that needs its own scheduled workflow,
//        a cost budget, and Azure credentials in the repo.
//
// SEC    Done — fixtures/adversarial/ is a passing test in that suite, not a
//        curiosity. Untrusted document text, delimited and labelled as data.
//        The injection fails and the attempt is surfaced rather than swallowed.
//
// HOST   Done — the composition root above, CreServicing.Api, and Core/Runs/.
//        DI, validated configuration, DefaultAzureCredential, bounds and
//        cancellation, then the same domain behind HTTP with the approval loop
//        surviving across two requests. None of this is a course section, and
//        it is half of what a .NET screen actually asks about.
//
// OTEL   Done — Core/Diagnostics/ServicingTelemetry.cs and Api/Telemetry.cs.
//        Spans on both runner turns and all five tools, IChatClient wrapped
//        with UseOpenTelemetry, and src/CreServicing.AppHost renders the span
//        tree in the Aspire dashboard. Core carries no OTel package, so the
//        free suite stays free. Sensitive-data capture is off on purpose: it
//        would ship whole borrower documents to the collector, undoing the
//        "names and sizes, never content" rule the tools follow.
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
// OCR    Open, and probably stays open. Real packages are scans, so this is
//        Azure Document Intelligence behind DocumentStore. Know that
//        prebuilt-layout exists and roughly what it costs per page even if you
//        never wire it up — the fixtures here are clean text, and saying so is
//        more honest than pretending the pipeline has seen a scan.
//
// FAIL   Open, and the one worth thinking about before writing. What happens
//        when extraction confidence is low, a field is null, or two documents
//        disagree? The answer that impresses is never "retry" — it is "route to
//        a human, with the specific question that needs answering." The
//        approval loop under S6 is already the mechanism for that; what is
//        missing is the trigger.
