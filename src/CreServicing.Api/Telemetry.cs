using CreServicing.Core.Diagnostics;
// For UseOtlpExporter, which is an extension on OpenTelemetryBuilder in the root
// namespace rather than beside the signal-specific builders below.
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CreServicing.Api;

/// <summary>
/// Item 4, the hosting half: where the spans go.
///
/// ── Why this is not a copy of Aspire's ServiceDefaults ───────────────────────
///
/// The template in <c>section5-getting-started/MinimalAgent/ServiceDefaults</c> is
/// the obvious thing to lift, and most of it is right. Two parts are not, for this
/// service:
///
///   - <c>AddStandardResilienceHandler</c>. It would wrap every HttpClient in
///     retries, including the one underneath AzureOpenAIClient, which already
///     retries and already honours Retry-After on a 429. Two retry budgets
///     multiply — three outer attempts over three inner ones is nine calls to a
///     deployment that is telling you to slow down. The reasoning is in
///     ServiceRegistration; this file's job is not to quietly undo it.
///
///   - <c>MapDefaultEndpoints</c>. /health here is not a health check probe, it
///     reports whether this instance can reach a model and says so without
///     calling itself unhealthy. Mapping the template's version over it would
///     replace a deliberate answer with a generic one.
///
/// What is worth taking is the OpenTelemetry wiring, and that is all this is.
///
/// ── Why the exporter is conditional ──────────────────────────────────────────
///
/// Instrumentation is registered unconditionally; the OTLP exporter is registered
/// only when there is somewhere to export to. That asymmetry is what lets the
/// integration tests run the real pipeline — WebApplicationFactory boots this
/// exact composition — without a collector to talk to and without a background
/// exporter retrying a connection nobody asked for.
/// </summary>
public static class Telemetry
{
    /// <summary>
    /// The name this service reports itself as. Pinned rather than defaulted to
    /// the assembly name so the dashboard keeps saying the same thing if the
    /// project is ever renamed — which it already was once, when
    /// CreServicing.Agent became Core/Cli/Api.
    /// </summary>
    private const string ServiceName = "cre-servicing-api";

    /// <summary>
    /// Polled constantly by whatever is watching the process, and interesting
    /// approximately never. Left out of traces so a run's spans are not buried
    /// under liveness checks.
    /// </summary>
    private const string HealthPath = "/health";

    public static IHostApplicationBuilder AddCreServicingTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                // The domain source: agent turns and tool calls from
                // CreServicing.Core, plus — because ServiceRegistration hands this
                // same name to UseOpenTelemetry — the model calls themselves.
                // Without this line the trace has HTTP requests and nothing
                // underneath them.
                .AddSource(ServicingTelemetry.ActivitySourceName)
                // The framework's own spans, in case a future version of MAF emits
                // agent or function-invocation activities of its own. Wildcards
                // rather than exact names because the source names have moved
                // between preview versions and a missing span is silent.
                .AddSource("Microsoft.Extensions.AI*")
                .AddSource("Microsoft.Agents.AI*")
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments(HealthPath))
                // The outbound leg to Azure OpenAI. Worth having beside the GenAI
                // spans rather than instead of them: this is where a retry, a 429
                // or a network timeout shows up, and none of those are visible in
                // the token counts.
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }
}
