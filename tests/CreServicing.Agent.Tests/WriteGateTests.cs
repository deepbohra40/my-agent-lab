using System.Text.Json;
using CreServicing.Agent.Data;
using CreServicing.Agent.Domain;
using CreServicing.Agent.Tools;

namespace CreServicing.Agent.Tests;

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
/// the gate is a property of how the tool is registered (see ServicingAgentHost),
/// and what this file tests is the second half: that the method itself refuses
/// nonsense before a human is ever asked to approve it. A gate that presents
/// garbage to a human is a worse gate.
/// </summary>
[Collection(nameof(WriteGateTests))]
[CollectionDefinition(nameof(WriteGateTests), DisableParallelization = true)]
public class WriteGateTests
{
    /// <summary>A loan that really is in the mock servicing system.</summary>
    private const string KnownLoan = "CRE-2019-0447";

    private const string Approver = "test-operator";

    public WriteGateTests() => ExceptionLedger.Clear();

    /// <summary>
    /// Files under an approval granted for the same loan and code, the way the
    /// host does. <see cref="ApprovalContext"/> is AsyncLocal, so the run has to
    /// be opened inside the test method rather than the constructor — context does
    /// not reliably flow from one to the other.
    /// </summary>
    private static string FileApproved(
        string loanId = KnownLoan,
        string code = "DSCR-MIN",
        string severity = "Breach",
        string summary = "DSCR of 1.156 is below the 1.25 covenant minimum.",
        string evidence = "NOI $2,130,000 / annual debt service $1,842,000 = 1.1564",
        TimeSpan? timeToDecision = null)
    {
        ApprovalContext.BeginRun(Approver);
        ApprovalContext.RecordApproval(loanId, code, timeToDecision ?? TimeSpan.FromSeconds(12));
        return ServicingTools.CreateServicingException(loanId, code, severity, summary, evidence);
    }

    // ── The happy path, so the rejections below mean something ───────────────

    [Fact]
    public void An_approved_filing_reaches_the_ledger_with_its_approver()
    {
        var result = FileApproved(timeToDecision: TimeSpan.FromSeconds(9));

        using var json = JsonDocument.Parse(result);
        Assert.Equal("FILED", json.RootElement.GetProperty("status").GetString());

        var entry = Assert.Single(ExceptionLedger.All);
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
        ApprovalContext.BeginRun(Approver);

        var result = ServicingTools.CreateServicingException(
            "CRE-9999-0000", "DSCR-MIN", "Breach", "summary", "evidence");

        Assert.StartsWith("REJECTED", result);
        Assert.Empty(ExceptionLedger.All);
    }

    [Fact]
    public void A_filing_under_an_invented_code_is_rejected_and_the_valid_codes_are_named()
    {
        // The failure this catches: an agent that reasons its way to a real
        // problem and files it under a code it made up. The finding might even be
        // correct — it is still not a code any covenant test produces, so nothing
        // downstream knows what to do with it.
        ApprovalContext.BeginRun(Approver);

        var result = ServicingTools.CreateServicingException(
            KnownLoan, "OCCUPANCY-LOW", "Breach", "summary", "evidence");

        Assert.StartsWith("REJECTED", result);
        Assert.Contains("OCC-MIN", result);   // the error tells it what it should have used
        Assert.Empty(ExceptionLedger.All);
    }

    [Theory]
    [InlineData("Critical")]    // plausible, and not a severity this system has
    [InlineData("breach!")]
    [InlineData("")]
    public void A_filing_with_an_unparseable_severity_is_rejected(string severity)
    {
        ApprovalContext.BeginRun(Approver);

        var result = ServicingTools.CreateServicingException(
            KnownLoan, "DSCR-MIN", severity, "summary", "evidence");

        Assert.StartsWith("REJECTED", result);
        Assert.Empty(ExceptionLedger.All);
    }

    [Fact]
    public void Severity_is_matched_case_insensitively()
    {
        // Not laxness — the model copies severity out of EvaluateCovenants' JSON,
        // and rejecting "BREACH" would fail a filing that is entirely correct.
        var result = FileApproved(severity: "bReAcH");

        Assert.DoesNotContain("REJECTED", result);
        Assert.Equal(ExceptionSeverity.Breach, Assert.Single(ExceptionLedger.All).Exception.Severity);
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
        ApprovalContext.BeginRun(Approver);

        var result = ServicingTools.CreateServicingException(
            KnownLoan, "DSCR-MIN", "Breach", summary, evidence);

        Assert.StartsWith("REJECTED", result);
        Assert.Empty(ExceptionLedger.All);
    }

    // ── Approval accounting ─────────────────────────────────────────────────

    [Fact]
    public void A_filing_with_no_approval_recorded_is_marked_unattributed()
    {
        // Should be unreachable while the host's gate holds. It is recorded rather
        // than thrown because the ledger's job is to say what happened, and a
        // write that arrived without an approval is precisely what an audit trail
        // exists to expose. Throwing here would also break the legitimate case the
        // ServicingTools header describes: this method called from a batch job
        // with sign-off upstream.
        ApprovalContext.BeginRun(Approver);

        var result = ServicingTools.CreateServicingException(
            KnownLoan, "DSCR-MIN", "Breach", "summary", "evidence");

        Assert.DoesNotContain("REJECTED", result);

        var entry = Assert.Single(ExceptionLedger.All);
        Assert.False(entry.IsAttributed);
        Assert.Equal(FiledException.Unattributed, entry.ApprovedBy);
        Assert.Null(entry.TimeToDecision);
    }

    [Fact]
    public void One_approval_authorises_exactly_one_filing()
    {
        // The bug this rules out: a model that files the same finding twice, and a
        // ledger that credits both to the single approval the human actually gave.
        // TakeApproval consumes, so the second filing is unattributed and visibly
        // so.
        ApprovalContext.BeginRun(Approver);
        ApprovalContext.RecordApproval(KnownLoan, "DSCR-MIN", TimeSpan.FromSeconds(5));

        ServicingTools.CreateServicingException(KnownLoan, "DSCR-MIN", "Breach", "s", "e");
        ServicingTools.CreateServicingException(KnownLoan, "DSCR-MIN", "Breach", "s", "e");

        Assert.Equal(2, ExceptionLedger.All.Count);
        Assert.True(ExceptionLedger.All[0].IsAttributed);
        Assert.False(ExceptionLedger.All[1].IsAttributed);
    }

    [Fact]
    public void An_approval_cannot_be_spent_on_a_different_finding()
    {
        // Approving the DSCR breach is not approving the insurance one. Keying the
        // approval to loan-plus-code is what makes that true; a single ambient
        // "the operator approved something" value could not tell them apart.
        ApprovalContext.BeginRun(Approver);
        ApprovalContext.RecordApproval(KnownLoan, "DSCR-MIN", TimeSpan.FromSeconds(5));

        ServicingTools.CreateServicingException(KnownLoan, "INS-COVERAGE", "Breach", "s", "e");

        Assert.False(Assert.Single(ExceptionLedger.All).IsAttributed);
    }

    [Fact]
    public void Each_approved_filing_keeps_its_own_decision_time()
    {
        // The reason ApprovalContext holds a map rather than one value. Three
        // findings approved in three separate prompts must not all report the last
        // prompt's elapsed time — that would make the one field capable of
        // exposing rubber-stamping report the wrong number.
        ApprovalContext.BeginRun(Approver);
        ApprovalContext.RecordApproval(KnownLoan, "DSCR-MIN", TimeSpan.FromSeconds(30));
        ApprovalContext.RecordApproval(KnownLoan, "INS-COVERAGE", TimeSpan.FromMilliseconds(200));

        ServicingTools.CreateServicingException(KnownLoan, "DSCR-MIN", "Breach", "s", "e");
        ServicingTools.CreateServicingException(KnownLoan, "INS-COVERAGE", "Breach", "s", "e");

        Assert.Equal(TimeSpan.FromSeconds(30), ExceptionLedger.All[0].TimeToDecision);
        Assert.Equal(TimeSpan.FromMilliseconds(200), ExceptionLedger.All[1].TimeToDecision);
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
            ExceptionLedger.Clear();
            var result = FileApproved(code: code, severity: "Informational");

            Assert.DoesNotContain("REJECTED", result);
            Assert.Equal(code, Assert.Single(ExceptionLedger.All).Exception.Code);
        }
    }
}
