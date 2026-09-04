using CreServicing.Core.Agents;
using CreServicing.Core.Configuration;
using CreServicing.Core.Data;
using CreServicing.Core.Runs;
using CreServicing.Testing;
using Microsoft.Extensions.Options;

namespace CreServicing.Core.Tests;

/// <summary>
/// Item 3 stage C: a run that stops to ask a human, and can be picked up again by
/// something that was not holding it.
///
/// ── What is actually being tested ────────────────────────────────────────────
///
/// The console approval loop worked because <c>Console.ReadLine()</c> blocked and
/// the whole run stayed on the stack. Everything below is about what had to
/// replace that stack: the agent's conversation, the tool trace, the ledger and
/// the pending question all written down, and a resume that reconstitutes them
/// from storage.
///
/// The load-bearing test is
/// <see cref="A_run_survives_a_full_round_trip_through_storage"/>. It resumes from
/// a run that has been through <see cref="InMemoryRunStore"/> — which serializes
/// and deserializes on every save and load — so nothing can pass by holding a live
/// object reference. That is the property that makes the difference between a
/// suspended run and a cached one, and it is the one that would fail first behind
/// two instances.
///
/// No model is called. See <see cref="ScriptedChatClient"/> for why that is worth
/// the trouble.
/// </summary>
public class SuspendedRunTests
{
    private const string KnownLoan = "CRE-2019-0447";
    private const string Approver = "test-operator";

    private static ServicingRunner Runner(ScriptedChatClient client)
        => new(client, Options.Create(new AzureOpenAIOptions
        {
            Endpoint = "https://example.openai.azure.com/",
            Deployment = "gpt-5-mini"
        }));

    // ── Suspending ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_gated_call_suspends_the_run_rather_than_executing_it()
    {
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException", ScriptedChatClient.Filing()));

        var run = await Runner(client).StartAsync(KnownLoan, Approver);

        Assert.Equal(ServicingRunStatus.AwaitingApproval, run.Status);
        Assert.Equal(1, run.Round);

        var pending = Assert.Single(run.AwaitingHuman);
        Assert.Equal("CreateServicingException", pending.ToolName);
        Assert.Equal(KnownLoan, pending.LoanId);
        Assert.Equal("DSCR-MIN", pending.Code);

        // The whole point of the gate: nothing was written while it waits.
        Assert.Empty(run.Filed);

        // And the operator can see every argument they are being asked to
        // authorise, not a summary of them.
        Assert.Equal(
            ["loanId", "code", "severity", "summary", "evidence"],
            pending.Arguments.Select(argument => argument.Name));
    }

    [Fact]
    public async Task A_run_with_no_gated_call_never_suspends()
    {
        // The compliant package. There is nothing to file, so there is nothing to
        // approve, and an approval loop that paused anyway would be training the
        // operator to click through.
        var client = new ScriptedChatClient(
            ScriptedChatClient.Text("The loan is in compliance. No exceptions filed."));

        var run = await Runner(client).StartAsync(KnownLoan, Approver);

        Assert.Equal(ServicingRunStatus.Completed, run.Status);
        Assert.Equal(0, run.Round);
        Assert.Empty(run.Filed);
        Assert.Contains("compliance", run.Answer);
    }

    // ── Resuming ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_approved_filing_lands_on_the_ledger_attributed_to_the_operator()
    {
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException", ScriptedChatClient.Filing()),
            ScriptedChatClient.Text("Filed EX-1 against the DSCR breach."));

        var runner = Runner(client);
        var run = await runner.StartAsync(KnownLoan, Approver);
        var pending = Assert.Single(run.AwaitingHuman);

        var resumed = await runner.ResumeAsync(
            run, [new ApprovalDecisionInput(pending.RequestId, Approved: true, TimeSpan.FromSeconds(9))]);

        Assert.Equal(ServicingRunStatus.Completed, resumed.Status);

        var filed = Assert.Single(resumed.Filed);
        Assert.Equal("DSCR-MIN", filed.Exception.Code);
        Assert.Equal(Approver, filed.ApprovedBy);
        Assert.True(filed.IsAttributed);
        Assert.Equal(TimeSpan.FromSeconds(9), filed.TimeToDecision);
    }

    [Fact]
    public async Task A_rejected_filing_writes_nothing()
    {
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException", ScriptedChatClient.Filing()),
            ScriptedChatClient.Text("The filing was rejected by the operator; nothing was recorded."));

        var runner = Runner(client);
        var run = await runner.StartAsync(KnownLoan, Approver);
        var pending = Assert.Single(run.AwaitingHuman);

        var resumed = await runner.ResumeAsync(
            run, [new ApprovalDecisionInput(pending.RequestId, Approved: false, TimeSpan.FromSeconds(3))]);

        Assert.Equal(ServicingRunStatus.Completed, resumed.Status);
        Assert.Empty(resumed.Filed);
    }

    /// <summary>
    /// Two approval rounds in one run — the case a real package always is.
    ///
    /// ── Why this test exists ─────────────────────────────────────────────────
    ///
    /// Every other resume test above scripts exactly ONE approval and then
    /// completes, which is not what a servicing review looks like: CRE-2019-0447
    /// produces five findings, and the model asks for them one at a time. That
    /// gap was found the expensive way, by a live run against gpt-5-mini failing
    /// on its second resume with
    ///
    ///   "ToolApprovalRequestContent found with FunctionCall.CallId(s) '…' that
    ///    have no matching ToolApprovalResponseContent"
    ///
    /// naming the call id of the approval answered TWO turns earlier — the one
    /// that had already filed successfully.
    /// </summary>
    [Fact]
    public async Task A_run_survives_a_second_approval_round()
    {
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException",
                ScriptedChatClient.Filing(code: "DSCR-MIN")),
            ScriptedChatClient.ToolCall("c2", "CreateServicingException",
                ScriptedChatClient.Filing(code: "OCC-MIN", summary: "Occupancy below the covenant floor.",
                    evidence: "118,600 / 142,000 = 0.8352 against an 85% minimum.")),
            ScriptedChatClient.Text("Filed two exceptions."));

        var runner = Runner(client);

        var run = await runner.StartAsync(KnownLoan, Approver);
        var first = Assert.Single(run.AwaitingHuman);

        run = await runner.ResumeAsync(
            run, [new ApprovalDecisionInput(first.RequestId, Approved: true, TimeSpan.FromSeconds(5))]);

        // Round one landed and the agent immediately asks for the second.
        Assert.Equal(ServicingRunStatus.AwaitingApproval, run.Status);
        Assert.Single(run.Filed);

        var second = Assert.Single(run.AwaitingHuman);

        run = await runner.ResumeAsync(
            run, [new ApprovalDecisionInput(second.RequestId, Approved: true, TimeSpan.FromSeconds(4))]);

        // The assertion that fails today. ResumeAsync catches and records rather
        // than throwing, so the symptom is a Failed run carrying the framework's
        // message — not an exception out of this call.
        Assert.Null(run.Error);
        Assert.Equal(ServicingRunStatus.Completed, run.Status);
        Assert.Equal(2, run.Filed.Count);
        Assert.Equal(["DSCR-MIN", "OCC-MIN"], run.Filed.Select(f => f.Exception.Code));
    }

    /// <summary>
    /// The one that proves stage C rather than merely exercising it.
    /// </summary>
    [Fact]
    public async Task A_run_survives_a_full_round_trip_through_storage()
    {
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException", ScriptedChatClient.Filing()),
            ScriptedChatClient.Text("Filed."));

        var runner = Runner(client);
        var store = new InMemoryRunStore();

        var started = await runner.StartAsync(KnownLoan, Approver);
        await store.SaveAsync(started);

        // Everything the first call held is now gone. What comes back is
        // reconstructed from JSON — no shared object, no live session, nothing the
        // resume could be leaning on by accident.
        var reloaded = await store.GetAsync(started.RunId);
        Assert.NotNull(reloaded);
        Assert.NotSame(started, reloaded);

        var pending = Assert.Single(reloaded.AwaitingHuman);

        var resumed = await runner.ResumeAsync(
            reloaded, [new ApprovalDecisionInput(pending.RequestId, true, TimeSpan.FromSeconds(6))]);

        Assert.Equal(ServicingRunStatus.Completed, resumed.Status);
        Assert.Equal(Approver, Assert.Single(resumed.Filed).ApprovedBy);

        // The resumed leg carried the conversation forward rather than starting
        // over: the second model call saw the first turn's history.
        Assert.Equal(2, client.Calls);
        Assert.True(
            client.Received[1].Count > client.Received[0].Count,
            "the resumed request should carry more history than the first, not restart the conversation");
    }

    [Fact]
    public async Task Usage_and_trace_accumulate_across_the_pause()
    {
        // Getting this wrong understates the run rather than failing it — the
        // quiet direction of error. The first round is the expensive one: it reads
        // every document, and its tokens are gone from the last response.
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCalls(
                input: 900, output: 120,
                ScriptedChatClient.Call("c1", "GetLoanTerms", new Dictionary<string, object?> { ["loanId"] = KnownLoan })),
            ScriptedChatClient.ToolCall("c2", "CreateServicingException", ScriptedChatClient.Filing(), input: 400, output: 60),
            ScriptedChatClient.Text("Done.", input: 200, output: 30));

        var runner = Runner(client);
        var run = await runner.StartAsync(KnownLoan, Approver);
        var pending = Assert.Single(run.AwaitingHuman);

        var usageBeforeResume = run.Usage;
        Assert.True(usageBeforeResume.TotalTokens > 0);

        var resumed = await runner.ResumeAsync(
            run, [new ApprovalDecisionInput(pending.RequestId, true, TimeSpan.FromSeconds(2))]);

        Assert.True(resumed.Usage.InputTokens > usageBeforeResume.InputTokens);
        Assert.True(resumed.ModelCalls >= 2);

        // The read from before the pause is still in the trace. An audit record
        // that only covers the last leg is not one.
        Assert.Contains(resumed.Trace, call => call.ToolName == "GetLoanTerms");
        Assert.Contains(resumed.Trace, call => call.ToolName == "CreateServicingException");
    }

    // ── The batching case the gate has to get right ──────────────────────────

    [Fact]
    public async Task A_read_batched_alongside_a_filing_is_not_put_to_the_human()
    {
        // FunctionInvokingChatClient's own remarks: if any call in a response is
        // for an approval-required function, EVERY call in that response requires
        // approval. A model that batches GetDocumentText alongside the filing
        // therefore produces two approval requests, one of which was never gated.
        //
        // Asking a human to authorise the read is asking them to rubber-stamp
        // something the design never gated, and a prompt that misdescribes what it
        // gates teaches the operator that the wording is noise.
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCalls(
                input: 500, output: 80,
                ScriptedChatClient.Call("c1", "GetDocumentText",
                    new Dictionary<string, object?> { ["relativePath"] = $"{KnownLoan}/rent-roll-2026-Q2.txt" }),
                ScriptedChatClient.Call("c2", "CreateServicingException", ScriptedChatClient.Filing())),
            ScriptedChatClient.Text("Filed."));

        var runner = Runner(client);
        var run = await runner.StartAsync(KnownLoan, Approver);

        Assert.Equal(ServicingRunStatus.AwaitingApproval, run.Status);

        // Two suspended requests, exactly one of which the operator sees.
        Assert.Equal(2, run.Suspended.Count);
        var asked = Assert.Single(run.AwaitingHuman);
        Assert.Equal("CreateServicingException", asked.ToolName);

        var swept = Assert.Single(run.Suspended, pending => !pending.RequiresHumanDecision);
        Assert.Equal("GetDocumentText", swept.ToolName);

        // Answering only the gated one resumes the whole round — the runner
        // resolves the read itself.
        var resumed = await runner.ResumeAsync(
            run, [new ApprovalDecisionInput(asked.RequestId, true, TimeSpan.FromSeconds(5))]);

        Assert.Equal(ServicingRunStatus.Completed, resumed.Status);
        Assert.Equal(Approver, Assert.Single(resumed.Filed).ApprovedBy);
    }

    // ── Submissions that do not match what the run is waiting for ────────────

    [Fact]
    public async Task Answering_only_some_of_the_outstanding_approvals_is_rejected()
    {
        // Silently reading "no answer" as "no" would file nothing and look
        // identical to an operator who declined. That is the wrong thing to be
        // ambiguous about, so a partial submission is refused outright.
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCalls(
                input: 500, output: 80,
                ScriptedChatClient.Call("c1", "CreateServicingException", ScriptedChatClient.Filing(code: "DSCR-MIN")),
                ScriptedChatClient.Call("c2", "CreateServicingException", ScriptedChatClient.Filing(code: "OCC-MIN"))));

        var runner = Runner(client);
        var run = await runner.StartAsync(KnownLoan, Approver);
        Assert.Equal(2, run.AwaitingHuman.Count());

        var first = run.AwaitingHuman.First();

        var ex = await Assert.ThrowsAsync<RunOperationException>(() => runner.ResumeAsync(
            run, [new ApprovalDecisionInput(first.RequestId, true, TimeSpan.FromSeconds(1))]));

        Assert.Contains("still waiting", ex.Message);

        // Refused means refused: the run is untouched and still asking.
        Assert.Equal(ServicingRunStatus.AwaitingApproval, run.Status);
        Assert.Empty(run.Filed);
    }

    [Fact]
    public async Task Answering_an_approval_the_run_is_not_holding_is_rejected()
    {
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException", ScriptedChatClient.Filing()));

        var runner = Runner(client);
        var run = await runner.StartAsync(KnownLoan, Approver);

        var ex = await Assert.ThrowsAsync<RunOperationException>(() => runner.ResumeAsync(
            run, [new ApprovalDecisionInput("not-a-request-id", true, TimeSpan.FromSeconds(1))]));

        Assert.Contains("not waiting on approval", ex.Message);
    }

    [Fact]
    public async Task A_completed_run_cannot_be_resumed()
    {
        // The duplicate-submission guard, at the runner level. Two POSTs of the
        // same approval must not file the exception twice; the second one arrives
        // to find a run that is no longer asking anything.
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException", ScriptedChatClient.Filing()),
            ScriptedChatClient.Text("Filed."));

        var runner = Runner(client);
        var run = await runner.StartAsync(KnownLoan, Approver);
        var pending = Assert.Single(run.AwaitingHuman);
        var decisions = new[] { new ApprovalDecisionInput(pending.RequestId, true, TimeSpan.FromSeconds(4)) };

        var resumed = await runner.ResumeAsync(run, decisions);
        Assert.Equal(ServicingRunStatus.Completed, resumed.Status);
        Assert.Single(resumed.Filed);

        var ex = await Assert.ThrowsAsync<RunOperationException>(() => runner.ResumeAsync(resumed, decisions));
        Assert.Contains("not waiting", ex.Message);

        // Still exactly one filing. This is the assertion that matters — a
        // duplicate covenant breach on a borrower's file, authorised by one human
        // decision, is the worst bug available in this system.
        Assert.Single(resumed.Filed);
    }

    // ── Isolation, which is the reason any of this is per-run ────────────────

    [Fact]
    public async Task Two_runs_in_flight_at_once_keep_separate_ledgers()
    {
        var runnerA = Runner(new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException", ScriptedChatClient.Filing(code: "DSCR-MIN")),
            ScriptedChatClient.Text("Filed.")));

        var runnerB = Runner(new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException",
                ScriptedChatClient.Filing(loanId: "CRE-2021-0912", code: "OCC-MIN")),
            ScriptedChatClient.Text("Filed.")));

        var runA = await runnerA.StartAsync(KnownLoan, "operator-a");
        var runB = await runnerB.StartAsync("CRE-2021-0912", "operator-b");

        // Interleaved on purpose: B resumes before A does.
        var doneB = await runnerB.ResumeAsync(runB,
            [new ApprovalDecisionInput(runB.AwaitingHuman.Single().RequestId, true, TimeSpan.FromSeconds(1))]);
        var doneA = await runnerA.ResumeAsync(runA,
            [new ApprovalDecisionInput(runA.AwaitingHuman.Single().RequestId, true, TimeSpan.FromSeconds(2))]);

        Assert.Equal("DSCR-MIN", Assert.Single(doneA.Filed).Exception.Code);
        Assert.Equal("operator-a", doneA.Filed[0].ApprovedBy);

        Assert.Equal("OCC-MIN", Assert.Single(doneB.Filed).Exception.Code);
        Assert.Equal("operator-b", doneB.Filed[0].ApprovedBy);
    }

    // ── The store's own contract ─────────────────────────────────────────────

    [Fact]
    public async Task The_store_hands_back_a_copy_rather_than_the_object_it_was_given()
    {
        var store = new InMemoryRunStore();
        var run = new ServicingRun
        {
            RunId = "run-1",
            LoanId = KnownLoan,
            Approver = Approver,
            Deployment = "gpt-5-mini",
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Answer = "original"
        };

        await store.SaveAsync(run);

        // Mutating what we saved must not change what the store holds. With a
        // dictionary of live references it would, and every test that resumed a
        // run would be leaning on an object the caller could still edit.
        run.Answer = "mutated after save";

        var loaded = await store.GetAsync("run-1");
        Assert.Equal("original", loaded!.Answer);
    }

    [Fact]
    public async Task A_run_that_failed_before_its_first_turn_is_still_storable()
    {
        // The bug this pins: JsonElement's default is ValueKind.Undefined and
        // System.Text.Json throws when asked to write one, so a run that threw
        // before producing a session could not be saved at all. The symptom was a
        // 500 from the endpoint that starts a run — on exactly the path where
        // something has already gone wrong and the record matters most.
        var client = new ScriptedChatClient();   // empty script: the first call throws
        var run = await Runner(client).StartAsync(KnownLoan, Approver);

        Assert.Equal(ServicingRunStatus.Failed, run.Status);
        Assert.NotNull(run.Error);

        var store = new InMemoryRunStore();
        await store.SaveAsync(run);

        var loaded = await store.GetAsync(run.RunId);
        Assert.Equal(ServicingRunStatus.Failed, loaded!.Status);
        Assert.Equal(run.Error, loaded.Error);

        // And it reports itself as unresumable rather than half-resuming into a
        // conversation that never happened.
        var ex = await Assert.ThrowsAsync<RunOperationException>(() =>
            Runner(new ScriptedChatClient()).ResumeAsync(loaded, []));
        Assert.Contains("not waiting", ex.Message);
    }

    [Fact]
    public async Task A_cancelled_run_still_reports_what_it_filed()
    {
        // Money may already have been spent and exceptions may already have been
        // filed. A run that throws away its evidence on the way to reporting an
        // error is the worst of both.
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException", ScriptedChatClient.Filing()));

        var runner = Runner(client);
        var run = await runner.StartAsync(KnownLoan, Approver);
        var pending = Assert.Single(run.AwaitingHuman);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var cancelled = await runner.ResumeAsync(
            run,
            [new ApprovalDecisionInput(pending.RequestId, true, TimeSpan.FromSeconds(1))],
            cts.Token);

        Assert.Equal(ServicingRunStatus.Failed, cancelled.Status);
        Assert.Contains("Cancelled", cancelled.Error);
    }

    [Fact]
    public async Task An_unknown_run_is_null_rather_than_an_exception()
    {
        var store = new InMemoryRunStore();
        Assert.Null(await store.GetAsync("nope"));
        Assert.False(await store.DeleteAsync("nope"));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Outstanding_approvals_survive_a_save_and_load()
    {
        // Normally empty between requests, because a filing executes in the same
        // turn its approval is submitted. Persisted anyway, so a framework that
        // defers a call to a later round cannot lose the authorisation for it —
        // which would land the filing as UNATTRIBUTED, a real write recorded as
        // unauthorised.
        var store = new InMemoryRunStore();
        var run = new ServicingRun
        {
            RunId = "run-2",
            LoanId = KnownLoan,
            Approver = Approver,
            Deployment = "gpt-5-mini",
            OutstandingApprovals = new Dictionary<string, ApprovalDecision>(StringComparer.Ordinal)
            {
                [ApprovalLedger.Key(KnownLoan, "DSCR-MIN")] = new(Approver, TimeSpan.FromSeconds(11))
            }
        };

        await store.SaveAsync(run);
        var loaded = await store.GetAsync("run-2");

        var kept = Assert.Single(loaded!.OutstandingApprovals);
        Assert.Equal(ApprovalLedger.Key(KnownLoan, "DSCR-MIN"), kept.Key);
        Assert.Equal(Approver, kept.Value.ApprovedBy);
        Assert.Equal(TimeSpan.FromSeconds(11), kept.Value.TimeToDecision);
    }
}
