using System.Text.Json;
using CreServicing.Core.Data;
using CreServicing.Core.Domain;
using CreServicing.Core.Tools;

namespace CreServicing.Core.Tests;

/// <summary>
/// The one tool that writes, and the validation standing between a confused agent
/// and a borrower's loan file.
///
/// The covenant tests next door prove the engine reaches the right verdict. These
/// prove the verdict cannot be *filed* wrong: an invented code, a rewritten
/// severity, or an exception with no evidence never reaches the ledger, and every
/// entry that does reach it names the human who authorised it.
///
/// Everything here is a pure function call. No model, no Azure, no approval loop —
/// the gate is a property of how the tool is registered (see ServicingRunner), and
/// what this file tests is the second half: that the method itself refuses
/// nonsense before a human is ever asked to approve it. A gate that presents
/// garbage to a human is a worse gate.
///
/// ── What stage C changed here ────────────────────────────────────────────────
///
/// This class used to carry a <c>[CollectionDefinition(DisableParallelization)]</c>
/// and call <c>ExceptionLedger.Clear()</c> in its constructor, because the ledger
/// and the approval context were both static and every test in the file was
/// fighting the others for them. Approvals also had to be opened inside the test
/// method rather than the constructor, because <c>AsyncLocal</c> does not reliably
/// flow between the two.
///
/// All of that is gone. Each test builds its own ledger, approvals and tool
/// instance, so the tests are isolated for the same structural reason two
/// concurrent HTTP requests now are — not because a test attribute says so. That
/// the ceremony could be deleted is the most direct evidence that the static state
/// really is gone.
/// </summary>
public class WriteGateTests
{
    /// <summary>A loan that really is in the mock servicing system.</summary>
    private const string KnownLoan = "CRE-2019-0447";

    private const string Approver = "test-operator";

    private readonly ExceptionLedger _ledger = new();
    private readonly ApprovalLedger _approvals = new(Approver);
    private readonly ServicingTools _tools;

    public WriteGateTests() => _tools = new ServicingTools(_ledger, _approvals);

    /// <summary>Files under an approval granted for the same loan and code, the way the runner does.</summary>
    private string FileApproved(
        string loanId = KnownLoan,
        string code = "DSCR-MIN",
        string severity = "Breach",
        string summary = "DSCR of 1.156 is below the 1.25 covenant minimum.",
        string evidence = "NOI $2,130,000 / annual debt service $1,842,000 = 1.1564",
        TimeSpan? timeToDecision = null)
    {
        _approvals.Record(loanId, code, timeToDecision ?? TimeSpan.FromSeconds(12));
        return _tools.CreateServicingException(loanId, code, severity, summary, evidence);
    }

    // ── The happy path, so the rejections below mean something ───────────────

    [Fact]
    public void An_approved_filing_reaches_the_ledger_with_its_approver()
    {
        var result = FileApproved(timeToDecision: TimeSpan.FromSeconds(9));

        using var json = JsonDocument.Parse(result);
        Assert.Equal("FILED", json.RootElement.GetProperty("status").GetString());

        var entry = Assert.Single(_ledger.All);
        Assert.Equal(KnownLoan, entry.Exception.LoanId);
        Assert.Equal("DSCR-MIN", entry.Exception.Code);
        Assert.Equal(ExceptionSeverity.Breach, entry.Exception.Severity);
        Assert.Equal(Approver, entry.ApprovedBy);
        Assert.True(entry.IsAttributed);
        Assert.Equal(TimeSpan.FromSeconds(9), entry.TimeToDecision);
    }

    // ── Rejections. Every one of these must leave the ledger untouched ───────

    [Fact]
    public void A_filing_against_an_unknown_loan_is_rejected()
    {
        var result = _tools.CreateServicingException(
            "CRE-9999-0000", "DSCR-MIN", "Breach", "summary", "evidence");

        Assert.StartsWith("REJECTED", result);
        Assert.Empty(_ledger.All);
    }

    [Fact]
    public void A_filing_under_an_invented_code_is_rejected_and_the_valid_codes_are_named()
    {
        // The failure this catches: an agent that reasons its way to a real
        // problem and files it under a code it made up. The finding might even be
        // correct — it is still not a code any covenant test produces, so nothing
        // downstream knows what to do with it.
        var result = _tools.CreateServicingException(
            KnownLoan, "OCCUPANCY-LOW", "Breach", "summary", "evidence");

        Assert.StartsWith("REJECTED", result);
        Assert.Contains("OCC-MIN", result);   // the error tells it what it should have used
        Assert.Empty(_ledger.All);
    }

    [Theory]
    [InlineData("Critical")]    // plausible, and not a severity this system has
    [InlineData("breach!")]
    [InlineData("")]
    public void A_filing_with_an_unparseable_severity_is_rejected(string severity)
    {
        var result = _tools.CreateServicingException(
            KnownLoan, "DSCR-MIN", severity, "summary", "evidence");

        Assert.StartsWith("REJECTED", result);
        Assert.Empty(_ledger.All);
    }

    [Fact]
    public void Severity_is_matched_case_insensitively()
    {
        // Not laxness — the model copies severity out of EvaluateCovenants' JSON,
        // and rejecting "BREACH" would fail a filing that is entirely correct.
        var result = FileApproved(severity: "bReAcH");

        Assert.DoesNotContain("REJECTED", result);
        Assert.Equal(ExceptionSeverity.Breach, Assert.Single(_ledger.All).Exception.Severity);
    }

    [Theory]
    [InlineData("", "evidence")]
    [InlineData("   ", "evidence")]
    [InlineData("summary", "")]
    [InlineData("summary", "  ")]
    public void A_filing_missing_its_summary_or_evidence_is_rejected(string summary, string evidence)
    {
        // An exception without its arithmetic is an assertion, not an audit
        // record — the same property AuditabilityTests pins on the engine's
        // output, enforced again at the point it would be written down.
        var result = _tools.CreateServicingException(
            KnownLoan, "DSCR-MIN", "Breach", summary, evidence);

        Assert.StartsWith("REJECTED", result);
        Assert.Empty(_ledger.All);
    }

    // ── Approval accounting ─────────────────────────────────────────────────

    [Fact]
    public void A_filing_with_no_approval_recorded_is_marked_unattributed()
    {
        // Should be unreachable while the runner's gate holds. It is recorded
        // rather than thrown because the ledger's job is to say what happened, and
        // a write that arrived without an approval is precisely what an audit
        // trail exists to expose. Throwing here would also break the legitimate
        // case the ServicingTools header describes: this method called from a
        // batch job with sign-off upstream.
        var result = _tools.CreateServicingException(
            KnownLoan, "DSCR-MIN", "Breach", "summary", "evidence");

        Assert.DoesNotContain("REJECTED", result);

        var entry = Assert.Single(_ledger.All);
        Assert.False(entry.IsAttributed);
        Assert.Equal(FiledException.Unattributed, entry.ApprovedBy);
        Assert.Null(entry.TimeToDecision);
    }

    [Fact]
    public void One_approval_authorises_exactly_one_filing()
    {
        // The bug this rules out: a model that files the same finding twice, and a
        // ledger that credits both to the single approval the human actually gave.
        // Take consumes, so the second filing is unattributed and visibly so.
        _approvals.Record(KnownLoan, "DSCR-MIN", TimeSpan.FromSeconds(5));

        _tools.CreateServicingException(KnownLoan, "DSCR-MIN", "Breach", "s", "e");
        _tools.CreateServicingException(KnownLoan, "DSCR-MIN", "Breach", "s", "e");

        Assert.Equal(2, _ledger.All.Count);
        Assert.True(_ledger.All[0].IsAttributed);
        Assert.False(_ledger.All[1].IsAttributed);
    }

    [Fact]
    public void An_approval_cannot_be_spent_on_a_different_finding()
    {
        // Approving the DSCR breach is not approving the insurance one. Keying the
        // approval to loan-plus-code is what makes that true; a single ambient
        // "the operator approved something" value could not tell them apart.
        _approvals.Record(KnownLoan, "DSCR-MIN", TimeSpan.FromSeconds(5));

        _tools.CreateServicingException(KnownLoan, "INS-COVERAGE", "Breach", "s", "e");

        Assert.False(Assert.Single(_ledger.All).IsAttributed);
    }

    [Fact]
    public void Each_approved_filing_keeps_its_own_decision_time()
    {
        // The reason ApprovalLedger holds a map rather than one value. Three
        // findings approved in three separate prompts must not all report the last
        // prompt's elapsed time — that would make the one field capable of
        // exposing rubber-stamping report the wrong number.
        _approvals.Record(KnownLoan, "DSCR-MIN", TimeSpan.FromSeconds(30));
        _approvals.Record(KnownLoan, "INS-COVERAGE", TimeSpan.FromMilliseconds(200));

        _tools.CreateServicingException(KnownLoan, "DSCR-MIN", "Breach", "s", "e");
        _tools.CreateServicingException(KnownLoan, "INS-COVERAGE", "Breach", "s", "e");

        Assert.Equal(TimeSpan.FromSeconds(30), _ledger.All[0].TimeToDecision);
        Assert.Equal(TimeSpan.FromMilliseconds(200), _ledger.All[1].TimeToDecision);
    }

    // ── The property that replaced DisableParallelization ────────────────────

    [Fact]
    public void Two_runs_filing_at_once_cannot_see_each_other_s_ledgers()
    {
        // The bug that made this whole refactor necessary, pinned so it cannot
        // come back. With a static ledger, loan A's exception appeared on loan B's
        // report and the List<> was appended from two threads with no lock. Two
        // tool instances now means two ledgers, and there is nothing shared to
        // race on.
        var otherLedger = new ExceptionLedger();
        var otherApprovals = new ApprovalLedger("other-operator");
        var otherTools = new ServicingTools(otherLedger, otherApprovals);

        _approvals.Record(KnownLoan, "DSCR-MIN", TimeSpan.FromSeconds(3));
        otherApprovals.Record("CRE-2021-0912", "OCC-MIN", TimeSpan.FromSeconds(4));

        _tools.CreateServicingException(KnownLoan, "DSCR-MIN", "Breach", "s", "e");
        otherTools.CreateServicingException("CRE-2021-0912", "OCC-MIN", "Breach", "s", "e");

        Assert.Equal(KnownLoan, Assert.Single(_ledger.All).Exception.LoanId);
        Assert.Equal("CRE-2021-0912", Assert.Single(otherLedger.All).Exception.LoanId);
        Assert.Equal(Approver, _ledger.All[0].ApprovedBy);
        Assert.Equal("other-operator", otherLedger.All[0].ApprovedBy);
    }

    [Fact]
    public async Task Concurrent_filings_within_one_run_all_reach_the_ledger()
    {
        // The narrower race the per-run lock still has to cover: the framework may
        // invoke batched tool calls concurrently inside a single run, so one
        // ledger really can be appended from several threads at once. An
        // unsynchronised List<> loses entries here — silently, and in the
        // direction that makes an audit trail incomplete.
        var codes = CovenantEngine.KnownCodes.ToArray();

        await Task.WhenAll(codes.Select(code => Task.Run(() =>
            _tools.CreateServicingException(KnownLoan, code, "Informational", "s", "e"))));

        Assert.Equal(codes.Length, _ledger.All.Count);
        Assert.Equal(
            codes.OrderBy(c => c, StringComparer.Ordinal),
            _ledger.All.Select(e => e.Exception.Code).OrderBy(c => c, StringComparer.Ordinal));

        // Reference numbers are handed out under the same lock, so they are unique
        // even when the filings are not ordered.
        Assert.Equal(codes.Length, _ledger.All.Select(e => e.ReferenceNumber).Distinct().Count());
    }

    // ── The vocabulary the gate is enforced against ─────────────────────────

    [Fact]
    public void Every_code_the_engine_can_emit_is_accepted_by_the_write_tool()
    {
        // The other direction of AuditabilityTests' KnownCodes check. That one
        // proves the engine emits nothing outside the set; this proves the set is
        // actually fileable, so the engine can never produce a finding its own
        // write path would refuse.
        foreach (var code in CovenantEngine.KnownCodes)
        {
            var ledger = new ExceptionLedger();
            var approvals = new ApprovalLedger(Approver);
            var tools = new ServicingTools(ledger, approvals);

            approvals.Record(KnownLoan, code, TimeSpan.FromSeconds(1));
            var result = tools.CreateServicingException(KnownLoan, code, "Informational", "s", "e");

            Assert.DoesNotContain("REJECTED", result);
            Assert.Equal(code, Assert.Single(ledger.All).Exception.Code);
        }
    }
}
