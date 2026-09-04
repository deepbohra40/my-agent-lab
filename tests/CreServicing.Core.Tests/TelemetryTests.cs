using System.Diagnostics;
using System.Text.Json;
using CreServicing.Core.Agents;
using CreServicing.Core.Configuration;
using CreServicing.Core.Data;
using CreServicing.Core.Diagnostics;
using CreServicing.Core.Extraction;
using CreServicing.Core.Runs;
using CreServicing.Testing;
using Microsoft.Extensions.Options;

namespace CreServicing.Core.Tests;

/// <summary>
/// Item 4: that the spans exist, carry the domain, and appear only when the thing
/// they describe actually happened.
///
/// ── Why this is a test rather than a screenshot ──────────────────────────────
///
/// The value of tracing a servicing run is entirely in whether the trace is a
/// faithful account of the run. That is a property, and properties rot silently —
/// a renamed <see cref="ActivitySource"/>, a dropped AddSource line, or a tool
/// that stops opening a span all leave a dashboard that looks fine and is lying by
/// omission. Nothing about "I looked at the Aspire dashboard once and it was
/// green" survives the next refactor.
///
/// No exporter and no OpenTelemetry SDK is involved here. An
/// <see cref="ActivityListener"/> is the BCL's own subscription mechanism, which
/// is the whole reason CreServicing.Core emits through <see cref="ActivitySource"/>
/// and takes no telemetry package reference — this runs in the free CI job with
/// the rest of them.
///
/// The model is scripted, so this costs nothing. See <see cref="ScriptedChatClient"/>.
/// </summary>
public class TelemetryTests
{
    private const string KnownLoan = "CRE-2019-0447";
    private const string Approver = "test-operator";

    private static ServicingRunner Runner(ScriptedChatClient client)
        => new(client, Options.Create(new AzureOpenAIOptions
        {
            Endpoint = "https://example.openai.azure.com/",
            Deployment = "gpt-5-mini"
        }));

    /// <summary>
    /// Captures every activity this library emits, and nothing else.
    ///
    /// ── Why the results are filtered by trace id ─────────────────────────────
    ///
    /// An ActivityListener is process-wide, and xUnit runs test classes in
    /// parallel. Without this, a run started by SuspendedRunTests on another
    /// thread lands in this test's captured list and the assertions below pass or
    /// fail depending on scheduling — the worst kind of flake, because it looks
    /// like a real intermittent bug in the thing under test.
    ///
    /// Every span a run produces descends from the parent started here, so
    /// filtering on trace id keeps one test's spans out of another's.
    /// </summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _captured = [];
        private readonly Activity _parent;

        public SpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ServicingTelemetry.ActivitySourceName,
                // AllData rather than PropagationData: without it StartActivity
                // returns null, every `activity?.SetTag` below is a no-op, and the
                // test passes by measuring nothing.
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    lock (_captured)
                    {
                        _captured.Add(activity);
                    }
                }
            };

            ActivitySource.AddActivityListener(_listener);

            _parent = ServicingTelemetry.Source.StartActivity("test")
                      ?? throw new InvalidOperationException(
                          "The listener did not sample. Nothing below would be measuring anything.");
        }

        public IReadOnlyList<Activity> Spans
        {
            get
            {
                lock (_captured)
                {
                    return [.. _captured.Where(a => a.TraceId == _parent.TraceId)];
                }
            }
        }

        public Activity Single(string name)
            => Assert.Single(Spans, span => span.OperationName == name);

        public void Dispose()
        {
            _parent.Dispose();
            _listener.Dispose();
        }
    }

    private static string? Tag(Activity activity, string key)
        => activity.GetTagItem(key)?.ToString();

    [Fact]
    public void The_activity_source_is_named_what_the_host_subscribes_to()
    {
        // Pinned because the name is a contract between two projects that do not
        // reference each other's telemetry: Core emits under it, and the API's
        // Telemetry.cs calls AddSource with it. A rename that updated only one
        // side would produce an empty dashboard and no build error.
        Assert.Equal("CreServicing.Core", ServicingTelemetry.ActivitySourceName);
        Assert.Equal("CreServicing.Core", ServicingTelemetry.Source.Name);
    }

    [Fact]
    public async Task A_suspended_run_traces_the_turn_and_the_tools_it_actually_ran()
    {
        using var capture = new SpanCapture();

        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "GetLoanTerms", new Dictionary<string, object?>
            {
                ["loanId"] = KnownLoan
            }),
            ScriptedChatClient.ToolCall("c2", "CreateServicingException", ScriptedChatClient.Filing()));

        var run = await Runner(client).StartAsync(KnownLoan, Approver);
        Assert.Equal(ServicingRunStatus.AwaitingApproval, run.Status);

        var turn = capture.Single("servicing_run.start");
        Assert.Equal(KnownLoan, Tag(turn, "cre.loan_id"));
        Assert.Equal(run.RunId, Tag(turn, "cre.run_id"));
        Assert.Equal("AwaitingApproval", Tag(turn, "cre.status"));
        Assert.Equal("1", Tag(turn, "cre.awaiting_human_count"));
        Assert.Equal("0", Tag(turn, "cre.filed_count"));

        // Usage is on the span, not only in the response body. The dashboard is
        // where "what did that run cost" gets asked, and an answer that requires
        // fetching the run record separately is one nobody looks up.
        Assert.Equal("gpt-5-mini", Tag(turn, "gen_ai.request.model"));
        Assert.Equal(run.Usage.InputTokens.ToString(), Tag(turn, "gen_ai.usage.input_tokens"));

        var read = capture.Single("execute_tool GetLoanTerms");
        Assert.Equal(KnownLoan, Tag(read, "cre.loan_id"));
        Assert.Equal("Office", Tag(read, "cre.property_type"));

        // ── The assertion this test exists for ───────────────────────────────
        //
        // The gated write was requested and is sitting in run.AwaitingHuman, and
        // it has NO span, because the framework suspended before the function body
        // ran. That is what makes the span count a usable proxy for "writes that
        // actually happened" — a claim the comment on CreateServicingException
        // makes and that nothing else in the suite would catch going stale.
        Assert.DoesNotContain(
            capture.Spans,
            span => span.OperationName.EndsWith("CreateServicingException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_approved_filing_traces_the_write_with_who_approved_it()
    {
        using var capture = new SpanCapture();

        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException", ScriptedChatClient.Filing()),
            ScriptedChatClient.Text("Filed one exception against the DSCR breach."));

        var runner = Runner(client);
        var run = await runner.StartAsync(KnownLoan, Approver);
        var pending = Assert.Single(run.AwaitingHuman);

        var resumed = await runner.ResumeAsync(run, [
            new ApprovalDecisionInput(pending.RequestId, Approved: true, TimeSpan.FromSeconds(12))
        ]);

        Assert.Equal(ServicingRunStatus.Completed, resumed.Status);

        var write = capture.Single("execute_tool CreateServicingException");
        Assert.Equal("filed", Tag(write, "cre.outcome"));
        Assert.Equal("DSCR-MIN", Tag(write, "cre.finding_code"));
        Assert.Equal("Breach", Tag(write, "cre.severity"));

        // The audit question the ledger endpoint exists to answer, asked of the
        // trace instead. An unattributed filing sets an error status; this one
        // must not.
        Assert.Equal(Approver, Tag(write, "cre.approved_by"));
        Assert.Equal(ActivityStatusCode.Unset, write.Status);

        var turn = capture.Single("servicing_run.resume");
        Assert.Equal("Completed", Tag(turn, "cre.status"));
        Assert.Equal("1", Tag(turn, "cre.filed_count"));
        Assert.Equal("1", Tag(turn, "cre.approvals_answered"));
    }

    // ── Extraction spans ─────────────────────────────────────────────────────

    [Fact]
    public async Task An_extraction_names_the_document_it_read_and_what_it_cost()
    {
        using var capture = new SpanCapture();

        // Structured output, scripted: RunAsync<T> parses the response text as
        // JSON into T, so a text turn carrying valid JSON stands in for a model
        // that got the schema right. Nothing here is asserting the model can — the
        // eval harness does that, live, with a budget.
        var client = new ScriptedChatClient(ScriptedChatClient.Text(
            """
            {"sourceDocument":"rent-roll-2026-Q2.txt","asOf":"2026-06-30","totalUnits":null,
             "occupiedUnits":null,"totalRentableSquareFeet":142000,"occupiedSquareFeet":118600,
             "annualScheduledRent":3100000,"confidence":0.95,"notes":null}
            """,
            input: 2400,
            output: 180));

        var extractor = new RentRollExtractor(client, Options.Create(new AzureOpenAIOptions
        {
            Endpoint = "https://example.openai.azure.com/",
            Deployment = "gpt-5-mini"
        }));

        var document = DocumentStore.Load("CRE-2019-0447/rent-roll-2026-Q2.txt");
        var result = await extractor.ExtractAsync(document);

        Assert.NotNull(result.Value);

        var span = capture.Single("extract rent-roll");

        // The whole reason this span exists. Before it, the snapshot endpoint's
        // trace was three anonymous model calls under one POST — you could see
        // that something cost 2,400 tokens and not which document it was.
        Assert.Equal("rent-roll", Tag(span, "cre.document_type"));
        Assert.Equal("CRE-2019-0447/rent-roll-2026-Q2.txt", Tag(span, "cre.document"));
        Assert.Equal("2400", Tag(span, "gen_ai.usage.input_tokens"));
        Assert.Equal("180", Tag(span, "gen_ai.usage.output_tokens"));

        // Forward slashes on every platform. The same normalisation the documents
        // endpoint applies, and a Windows-shaped path here would not match a tag
        // filter written against a trace captured on CI.
        Assert.DoesNotContain('\\', Tag(span, "cre.document")!);

        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task An_extraction_the_model_botched_still_reports_what_it_cost()
    {
        using var capture = new SpanCapture();

        // Prose where the schema was required. Worth knowing how this actually
        // behaves, because it is not what the shape of the code suggests:
        // AgentResponse<T>.Result parses lazily and THROWS here rather than
        // returning null. The extractors set usage before reading it for exactly
        // this reason, and this test is what pins that ordering.
        var client = new ScriptedChatClient(
            ScriptedChatClient.Text("I was unable to read that document.", input: 900, output: 15));

        var extractor = new RentRollExtractor(client, Options.Create(new AzureOpenAIOptions
        {
            Endpoint = "https://example.openai.azure.com/",
            Deployment = "gpt-5-mini"
        }));

        await Assert.ThrowsAsync<JsonException>(() => extractor.ExtractAsync(
            DocumentStore.Load("CRE-2019-0447/rent-roll-2026-Q2.txt")));

        var span = capture.Single("extract rent-roll");
        Assert.Equal("unparseable", Tag(span, "cre.outcome"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);

        // ── The assertion this test exists for ───────────────────────────────
        //
        // Money spent on a call that produced nothing is still money spent, and
        // the span says so. Written the other way round first — usage set after
        // reading Result — this read (null) instead of 900, because the throw
        // unwound past the SetUsage call. Same property PackageCost has when it
        // accounts cost before the assembler's null checks.
        Assert.Equal("900", Tag(span, "gen_ai.usage.input_tokens"));
        Assert.Equal("15", Tag(span, "gen_ai.usage.output_tokens"));
    }

    [Fact]
    public async Task A_rejected_filing_is_an_error_span_rather_than_a_quiet_string()
    {
        using var capture = new SpanCapture();

        // A code no covenant test produces. The tool returns an ordinary string
        // the model reads and works around, which is exactly why the failure needs
        // to be visible somewhere the model cannot influence.
        var client = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("c1", "CreateServicingException",
                ScriptedChatClient.Filing(code: "INVENTED-CODE")),
            ScriptedChatClient.Text("That code was rejected."));

        var runner = Runner(client);
        var run = await runner.StartAsync(KnownLoan, Approver);
        var pending = Assert.Single(run.AwaitingHuman);

        await runner.ResumeAsync(run, [
            new ApprovalDecisionInput(pending.RequestId, Approved: true, TimeSpan.FromSeconds(3))
        ]);

        var write = capture.Single("execute_tool CreateServicingException");
        Assert.Equal("rejected", Tag(write, "cre.outcome"));
        Assert.Equal(ActivityStatusCode.Error, write.Status);
        Assert.Equal("unknown finding code", write.StatusDescription);
    }
}
