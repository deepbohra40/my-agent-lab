using System.Diagnostics;
using CreServicing.Core.Cost;
using CreServicing.Core.Data;

namespace CreServicing.Core.Diagnostics;

/// <summary>
/// Item 4, the Core half: the spans this library emits, and nothing about where
/// they go.
///
/// ── Why there is no OpenTelemetry package reference in this project ──────────
///
/// <see cref="ActivitySource"/> is the BCL's instrumentation API, not
/// OpenTelemetry's. Emitting an activity with no listener attached costs a null
/// check, which is why this file can sit in the middle of the covenant path
/// without anyone having to think about it. The OpenTelemetry SDK — the exporters,
/// the samplers, the OTLP wire format — is a *hosting* concern and lives in
/// CreServicing.Api, which is the project that knows whether there is a collector
/// to talk to.
///
/// That split is the same one Cost/ already makes for the same reason: a folder
/// with no SDK reference keeps its behaviour testable in the free CI job. The
/// covenant tests run with no listener and therefore no telemetry, and they still
/// exercise every line below.
///
/// ── What is deliberately NOT tagged ──────────────────────────────────────────
///
/// No document text, no extracted figures beyond the ones that are already in the
/// findings, no prompt or response content. The /loans/{id}/documents endpoint
/// returns "names and sizes but never content" and there is a test pinning it;
/// a trace exporter that shipped whole borrower rent rolls to a collector would
/// undo that discipline through a side channel nobody thought to write a test for.
/// See the sensitive-data note in the API's telemetry registration for the same
/// argument applied to the model calls themselves.
/// </summary>
public static class ServicingTelemetry
{
    /// <summary>
    /// The name a host has to subscribe to before any of this is visible. Named
    /// for the assembly rather than for the domain so it reads correctly beside
    /// the framework's own sources in a dashboard's source list.
    /// </summary>
    public const string ActivitySourceName = "CreServicing.Core";

    public static readonly ActivitySource Source = new(ActivitySourceName);

    /// <summary>
    /// One agent turn: everything between a caller handing over control and the
    /// runner handing it back, suspended or finished.
    ///
    /// This is the span the whole item exists for. A servicing run is two HTTP
    /// requests minutes apart, and without a span per turn the dashboard shows two
    /// unrelated POSTs — which is exactly the shape that makes people believe the
    /// approval loop is one blocking call.
    /// </summary>
    public static Activity? Turn(string operation, string loanId, string? runId = null)
    {
        var activity = Source.StartActivity($"servicing_run.{operation}", ActivityKind.Internal);
        activity?.SetTag("cre.loan_id", loanId);

        if (runId is not null)
        {
            activity?.SetTag("cre.run_id", runId);
        }

        return activity;
    }

    /// <summary>
    /// One tool call, named per the OpenTelemetry GenAI semantic conventions so a
    /// dashboard groups these with the framework's own tool spans rather than
    /// beside them.
    /// </summary>
    public static Activity? Tool(string toolName, string? loanId = null)
    {
        var activity = Source.StartActivity($"execute_tool {toolName}", ActivityKind.Internal);
        activity?.SetTag("gen_ai.tool.name", toolName);

        if (!string.IsNullOrWhiteSpace(loanId))
        {
            activity?.SetTag("cre.loan_id", loanId);
        }

        return activity;
    }

    /// <summary>
    /// A tool that refused to do what it was asked.
    ///
    /// Recorded as an error status because that is what it is from the caller's
    /// point of view — the model passed something the system would not accept —
    /// even though the run continues and the tool returned a perfectly ordinary
    /// string. A rejected filing that leaves no trace is the one you find out
    /// about from the operator rather than the dashboard.
    /// </summary>
    public static void Rejected(this Activity? activity, string reason)
    {
        activity?.SetTag("cre.outcome", "rejected");
        activity?.SetStatus(ActivityStatusCode.Error, reason);
    }

    /// <summary>
    /// One document's extraction.
    ///
    /// Without this the snapshot endpoint's trace is three anonymous model calls
    /// under one POST: you can see that something cost 4,000 tokens and not which
    /// document it was. Per-document is the unit the whole Cost/ folder is built
    /// around — "a rent roll costs about this much to read" is the figure that
    /// multiplies by a portfolio — and a trace that cannot express it is measuring
    /// the wrong thing.
    ///
    /// <paramref name="documentType"/> is a short stable slug rather than the
    /// extractor's class name, so the span names stay low-cardinality and readable
    /// in a dashboard: "extract rent-roll", not "extract RentRollExtractor".
    /// </summary>
    public static Activity? Extraction(string documentType, SourceDocument document)
    {
        var activity = Source.StartActivity($"extract {documentType}", ActivityKind.Internal);
        activity?.SetTag("cre.document_type", documentType);

        // The path and the size. Never the text — same line held everywhere else.
        activity?.SetTag("cre.document", document.RelativePath.Replace('\\', '/'));
        activity?.SetTag("cre.approximate_tokens", document.ApproximateTokens);

        return activity;
    }

    /// <summary>
    /// The parent of the four extractions: one borrower's package becoming one
    /// <see cref="Domain.FinancialSnapshot"/>.
    ///
    /// Worth its own span rather than letting the extractions hang off the HTTP
    /// request directly, because the endpoint does two other things either side of
    /// it — the hand-keyed comparison and two covenant evaluations — and the
    /// assembly is the only part that spends money.
    /// </summary>
    public static Activity? Assembly(string loanId)
    {
        var activity = Source.StartActivity("assemble_snapshot", ActivityKind.Internal);
        activity?.SetTag("cre.loan_id", loanId);
        return activity;
    }

    /// <summary>
    /// Grounding a run's findings in the loan agreement.
    ///
    /// The pair of counts this span carries — resolved against unresolved — is the
    /// only place a silently degrading index shows up. Citations failing to attach
    /// breaks nothing: the findings are unchanged, the run completes, the tests
    /// pass, and the exception report simply stops quoting the agreement. That is
    /// the shape of failure nobody notices from the outside, so it gets a number
    /// something can alert on.
    ///
    /// Emitted only when an index is actually configured. The free path resolves
    /// <see cref="Citations.NullClauseIndex"/> and produces no span at all, because
    /// a run that never looked for a citation has nothing to report about one.
    /// </summary>
    public static Activity? Citations(string loanId, int findingCount)
    {
        var activity = Source.StartActivity("resolve_citations", ActivityKind.Internal);
        activity?.SetTag("cre.loan_id", loanId);
        activity?.SetTag("cre.finding_count", findingCount);
        return activity;
    }

    /// <summary>
    /// Records that an extraction came back with no structured result at all.
    ///
    /// Distinct from a rejected tool call: nothing was refused, the model simply
    /// returned nothing usable, and the assembler is about to turn that into a 422.
    /// The endpoint's caller learns which field was missing; this is where the
    /// operator learns it happened at all.
    /// </summary>
    public static void NoResult(this Activity? activity)
    {
        activity?.SetTag("cre.outcome", "no-result");
        activity?.SetStatus(ActivityStatusCode.Error, "extraction returned no structured result");
    }

    /// <summary>
    /// Records that the model answered with something that is not the schema.
    ///
    /// <c>AgentResponse&lt;T&gt;.Result</c> parses lazily and throws rather than
    /// returning null, so this is the common shape of a bad extraction and
    /// <see cref="NoResult"/> is the rare one. Both are worth distinguishing in a
    /// dashboard: "the model wrote prose where JSON was required" and "the model
    /// wrote JSON that came out empty" have different fixes, and the first one is
    /// usually a prompt that stopped saying the word "JSON".
    /// </summary>
    public static void Unparseable(this Activity? activity, Exception exception)
    {
        activity?.SetTag("cre.outcome", "unparseable");
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    /// <summary>
    /// Token counts, under the names the GenAI conventions already define — the
    /// cached one included, and spelled exactly as the framework's own model
    /// spans spell it so the two line up in a dashboard rather than sitting under
    /// near-miss attribute names.
    ///
    /// Emitted even when zero. "Nothing was cached" and "caching was never
    /// measured" are different facts, and on the agent path the first is a
    /// finding: a resume that re-sends the whole conversation and gets no cache
    /// hit is paying full price for a prefix it just sent.
    /// </summary>
    public static void SetUsage(this Activity? activity, ModelUsage usage)
    {
        activity?.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
        activity?.SetTag("gen_ai.usage.cache_read.input_tokens", usage.EffectiveCachedInputTokens);
    }
}
