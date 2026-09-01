using System.Net;
using System.Net.Http.Json;
using CreServicing.Testing;

namespace CreServicing.Api.Tests;

/// <summary>
/// Stage C, end to end: a run that suspends in one HTTP request and is resumed by
/// another.
///
/// This is the test the whole stage was for. The console loop worked because
/// <c>Console.ReadLine()</c> blocked and held the run on the stack; here the first
/// request returns while the run is still mid-flight, everything it needs is
/// written down, and a second request picks it up. Nothing is held open in
/// between — no connection, no thread, no object either request has a reference
/// to.
///
/// The model is scripted, so this costs nothing and runs in the free CI job. What
/// is being tested is the state machine and the transport, not whether the model
/// behaves; that is the eval harness's job, with its own budget.
/// </summary>
public class ApprovalOverHttpTests
{
    private const string Loan = "CRE-2019-0447";

    private static ScriptedChatClient FilesOneException() => new(
        ScriptedChatClient.ToolCall("c1", "CreateServicingException", ScriptedChatClient.Filing()),
        ScriptedChatClient.Text("Filed one exception against the DSCR breach."));

    [Fact]
    public async Task A_run_suspends_on_the_first_request_and_resumes_on_the_second()
    {
        using var factory = ServicingApiFactory.WithScriptedModel(FilesOneException());
        using var client = factory.CreateClient();

        // ── Request one: start ───────────────────────────────────────────────
        var started = await client.PostAsJsonAsync(
            "/servicing-runs", new StartRunRequest(Loan, "http-operator"));

        // 201 even though it has not finished. The resource exists and is
        // addressable; "waiting for a human" is its state, not a failure to create
        // it.
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);

        var run = await started.Content.ReadFromJsonAsync<RunResponse>();
        Assert.NotNull(run);

        // Location points at the run, so a client that wants to poll rather than
        // hold the response knows where to look.
        Assert.Equal($"/servicing-runs/{run.RunId}", started.Headers.Location?.ToString());
        Assert.Equal("AwaitingApproval", run.Status);

        var pending = Assert.Single(run.AwaitingApproval);
        Assert.Equal("CreateServicingException", pending.Tool);

        // The operator is shown every argument they are being asked to authorise.
        Assert.Equal(Loan, pending.Arguments["loanId"]);
        Assert.Equal("DSCR-MIN", pending.Arguments["code"]);

        // Nothing has been written while it waits. That is the gate working.
        Assert.Empty(run.Filed);

        // ── Between requests: the run is retrievable by anyone ───────────────
        var fetched = await client.GetFromJsonAsync<RunResponse>($"/servicing-runs/{run.RunId}");
        Assert.NotNull(fetched);
        Assert.Equal("AwaitingApproval", fetched.Status);
        Assert.Equal(pending.RequestId, Assert.Single(fetched.AwaitingApproval).RequestId);

        // ── Request two: answer, and the run continues ───────────────────────
        var resumed = await client.PostAsJsonAsync(
            $"/servicing-runs/{run.RunId}/approvals",
            new SubmitApprovalsRequest([new ApprovalDecisionRequest(pending.RequestId, true, 9.0)]));

        resumed.EnsureSuccessStatusCode();

        var done = await resumed.Content.ReadFromJsonAsync<RunResponse>();
        Assert.NotNull(done);
        Assert.Equal("Completed", done.Status);
        Assert.Equal(run.RunId, done.RunId);

        var filed = Assert.Single(done.Filed);
        Assert.Equal("DSCR-MIN", filed.Code);
        Assert.Equal("http-operator", filed.ApprovedBy);
        Assert.True(filed.IsAttributed);
        Assert.Equal(9.0, filed.TimeToDecisionSeconds);

        // The trace survived the pause. An audit record covering only the leg
        // after the approval would not be one.
        Assert.Contains(done.Trace, call => call.Tool == "CreateServicingException");
        Assert.True(done.Cost.ModelCalls >= 2);
    }

    [Fact]
    public async Task A_rejected_filing_leaves_the_ledger_empty()
    {
        using var factory = ServicingApiFactory.WithScriptedModel(new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException", ScriptedChatClient.Filing()),
            ScriptedChatClient.Text("The filing was rejected; nothing was recorded.")));
        using var client = factory.CreateClient();

        var run = await StartAsync(client);
        var pending = Assert.Single(run.AwaitingApproval);

        var response = await client.PostAsJsonAsync(
            $"/servicing-runs/{run.RunId}/approvals",
            new SubmitApprovalsRequest([new ApprovalDecisionRequest(pending.RequestId, false, 4.0)]));

        response.EnsureSuccessStatusCode();
        var done = await response.Content.ReadFromJsonAsync<RunResponse>();

        Assert.Equal("Completed", done!.Status);
        Assert.Empty(done.Filed);
    }

    [Fact]
    public async Task Submitting_the_same_approval_twice_does_not_file_twice()
    {
        // The worst bug available in this system: a duplicate covenant breach on a
        // borrower's file, authorised by one human decision. An impatient operator
        // double-clicking is not an exotic scenario, and over HTTP a retry is the
        // default behaviour of half the clients in existence.
        using var factory = ServicingApiFactory.WithScriptedModel(FilesOneException());
        using var client = factory.CreateClient();

        var run = await StartAsync(client);
        var pending = Assert.Single(run.AwaitingApproval);
        var submission = new SubmitApprovalsRequest(
            [new ApprovalDecisionRequest(pending.RequestId, true, 5.0)]);

        var first = await client.PostAsJsonAsync($"/servicing-runs/{run.RunId}/approvals", submission);
        first.EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync($"/servicing-runs/{run.RunId}/approvals", submission);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        // The state of record, fetched fresh rather than read off either response.
        var ledger = await client.GetFromJsonAsync<List<FiledExceptionResponse>>(
            $"/servicing-runs/{run.RunId}/ledger");

        Assert.Single(ledger!);
    }

    [Fact]
    public async Task A_read_batched_alongside_the_filing_is_never_put_to_the_operator()
    {
        // FunctionInvokingChatClient escalates every call in a batch to
        // approval-required if any one of them is. Surfacing the read as a
        // question would ask a human to authorise something the design never
        // gated, and a prompt that misdescribes what it gates teaches the operator
        // that the wording is noise.
        using var factory = ServicingApiFactory.WithScriptedModel(new ScriptedChatClient(
            ScriptedChatClient.ToolCalls(
                input: 400, output: 60,
                ScriptedChatClient.Call("c1", "GetDocumentText",
                    new Dictionary<string, object?> { ["relativePath"] = $"{Loan}/rent-roll-2026-Q2.txt" }),
                ScriptedChatClient.Call("c2", "CreateServicingException", ScriptedChatClient.Filing())),
            ScriptedChatClient.Text("Filed.")));
        using var client = factory.CreateClient();

        var run = await StartAsync(client);

        var asked = Assert.Single(run.AwaitingApproval);
        Assert.Equal("CreateServicingException", asked.Tool);

        // Visible in the audit trail, but not as a question.
        var swept = Assert.Single(run.AutoApproved);
        Assert.Equal("GetDocumentText", swept.Tool);

        var response = await client.PostAsJsonAsync(
            $"/servicing-runs/{run.RunId}/approvals",
            new SubmitApprovalsRequest([new ApprovalDecisionRequest(asked.RequestId, true, 6.0)]));

        response.EnsureSuccessStatusCode();
        var done = await response.Content.ReadFromJsonAsync<RunResponse>();
        Assert.Equal("Completed", done!.Status);
        Assert.Single(done.Filed);
    }

    [Fact]
    public async Task A_partial_submission_is_a_400_and_changes_nothing()
    {
        using var factory = ServicingApiFactory.WithScriptedModel(new ScriptedChatClient(
            ScriptedChatClient.ToolCalls(
                input: 400, output: 60,
                ScriptedChatClient.Call("c1", "CreateServicingException", ScriptedChatClient.Filing(code: "DSCR-MIN")),
                ScriptedChatClient.Call("c2", "CreateServicingException", ScriptedChatClient.Filing(code: "OCC-MIN")))));
        using var client = factory.CreateClient();

        var run = await StartAsync(client);
        Assert.Equal(2, run.AwaitingApproval.Count);

        var response = await client.PostAsJsonAsync(
            $"/servicing-runs/{run.RunId}/approvals",
            new SubmitApprovalsRequest(
                [new ApprovalDecisionRequest(run.AwaitingApproval[0].RequestId, true, 3.0)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("still waiting", await response.Content.ReadAsStringAsync());

        // Refused means refused: the run is untouched and still asking both.
        var after = await client.GetFromJsonAsync<RunResponse>($"/servicing-runs/{run.RunId}");
        Assert.Equal("AwaitingApproval", after!.Status);
        Assert.Equal(2, after.AwaitingApproval.Count);
        Assert.Empty(after.Filed);
    }

    [Fact]
    public async Task Answering_a_run_that_does_not_exist_is_a_404()
    {
        using var factory = ServicingApiFactory.WithScriptedModel(FilesOneException());
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/servicing-runs/does-not-exist/approvals",
            new SubmitApprovalsRequest([new ApprovalDecisionRequest("whatever", true, 1.0)]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Two_runs_in_flight_at_once_do_not_see_each_other()
    {
        // The reason the ledger and the approvals stopped being static. Under the
        // old design these two runs shared both, and the failure was silent: one
        // borrower's exception on the other's report.
        using var factory = ServicingApiFactory.WithScriptedModel(new ScriptedChatClient(
            ScriptedChatClient.ToolCall("a1", "CreateServicingException", ScriptedChatClient.Filing(code: "DSCR-MIN")),
            ScriptedChatClient.ToolCall("b1", "CreateServicingException",
                ScriptedChatClient.Filing(loanId: "CRE-2021-0912", code: "OCC-MIN")),
            ScriptedChatClient.Text("Filed."),
            ScriptedChatClient.Text("Filed.")));
        using var client = factory.CreateClient();

        var runA = await StartAsync(client, Loan, "operator-a");
        var runB = await StartAsync(client, "CRE-2021-0912", "operator-b");

        Assert.NotEqual(runA.RunId, runB.RunId);

        // Answered out of order, deliberately.
        await client.PostAsJsonAsync($"/servicing-runs/{runB.RunId}/approvals",
            new SubmitApprovalsRequest(
                [new ApprovalDecisionRequest(runB.AwaitingApproval[0].RequestId, true, 2.0)]));

        await client.PostAsJsonAsync($"/servicing-runs/{runA.RunId}/approvals",
            new SubmitApprovalsRequest(
                [new ApprovalDecisionRequest(runA.AwaitingApproval[0].RequestId, true, 3.0)]));

        var ledgerA = await client.GetFromJsonAsync<List<FiledExceptionResponse>>(
            $"/servicing-runs/{runA.RunId}/ledger");
        var ledgerB = await client.GetFromJsonAsync<List<FiledExceptionResponse>>(
            $"/servicing-runs/{runB.RunId}/ledger");

        var a = Assert.Single(ledgerA!);
        Assert.Equal(Loan, a.LoanId);
        Assert.Equal("operator-a", a.ApprovedBy);

        var b = Assert.Single(ledgerB!);
        Assert.Equal("CRE-2021-0912", b.LoanId);
        Assert.Equal("operator-b", b.ApprovedBy);
    }

    [Fact]
    public async Task The_wire_format_never_leaks_the_session_or_the_raw_request()
    {
        // The agent session is the whole conversation, including every document it
        // read. It is storage, not a contract, and returning it would both leak
        // borrower content and freeze an implementation detail stage C is
        // explicitly free to change.
        using var factory = ServicingApiFactory.WithScriptedModel(FilesOneException());
        using var client = factory.CreateClient();

        var run = await StartAsync(client);
        var body = await client.GetStringAsync($"/servicing-runs/{run.RunId}");

        Assert.DoesNotContain("sessionState", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chatHistory", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$type", body, StringComparison.Ordinal);
    }

    private static async Task<RunResponse> StartAsync(
        HttpClient client, string loanId = Loan, string approver = "http-operator")
    {
        var response = await client.PostAsJsonAsync("/servicing-runs", new StartRunRequest(loanId, approver));
        response.EnsureSuccessStatusCode();

        var run = await response.Content.ReadFromJsonAsync<RunResponse>();
        Assert.NotNull(run);
        return run;
    }
}
