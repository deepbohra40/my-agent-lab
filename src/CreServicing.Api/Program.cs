using System.Globalization;
using CreServicing.Api;
using CreServicing.Core.Configuration;
using CreServicing.Core.Data;

// CRE post-close document intake and covenant compliance, behind HTTP.
//
// This is the HOST entry in the roadmap at the bottom of
// ../CreServicing.Cli/Program.cs, built in two stages. The first put the paths
// with no approval step behind an API. The second put the one with an approval
// step behind it too, which was the actual design problem: the console loop
// worked because Console.ReadLine() blocked and held the run in memory, and
// there is no equivalent here. See Core/Runs/ServicingRun.cs.
//
// The API adds no domain logic. Every endpoint resolves something out of
// CreServicing.Core and shapes the result for the wire — if a rule lived here that
// was not in Core, the CLI and the API could disagree about it, and one of the two
// would be wrong about whether a covenant was breached.
//
// Everything is fabricated. See the header comment in MockServicingSystem.

var builder = WebApplication.CreateBuilder(args);

// These are US dollars regardless of where the machine is, and ISO dates
// regardless of the host's locale. CovenantEngine pins its own formatting for the
// same reason; this covers response serialisation and the display strings the
// endpoints build.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

// validateOnStart: false is the one deliberate difference from the CLI's
// composition. The reasoning is on the parameter itself in ServiceRegistration and
// on ModelAvailability — in short, the deterministic endpoints are the ones that
// must never be unavailable, so a missing Azure endpoint degrades this service
// rather than stopping it.
builder.Services.AddCreServicing(builder.Configuration, validateOnStart: false);
builder.Services.AddSingleton<ModelAvailability>();

// Item 4. Registered unconditionally and exported only when a collector is
// configured — see Telemetry.cs for why that asymmetry is the point rather than a
// half-measure. A servicing run is two HTTP requests minutes apart, and this is
// what makes it legible as one operation instead of two unrelated POSTs.
builder.AddCreServicingTelemetry();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", (ModelAvailability availability) => Results.Ok(new
    {
        status = "ok",
        // Reported rather than acted on. "Deterministic endpoints work, model
        // endpoints do not" is a real and useful state for this service to be in,
        // and a health check that called it unhealthy would have a load balancer
        // pull an instance that is serving the audit-grade path perfectly well.
        deterministicEndpoints = "available",
        modelEndpoints = availability.IsConfigured() ? "available" : "unconfigured",
        deployment = availability.Deployment,
        loansOnFile = MockServicingSystem.LoanIds.Count,
        fixturePackages = DocumentStore.ListPackages().Count
    }))
    .WithTags("Diagnostics")
    .WithSummary("Liveness, plus whether this instance can reach a model.");

app.MapLoanEndpoints();
app.MapServicingRunEndpoints();

app.Run();

/// <summary>
/// Named so the integration tests can drive the real pipeline through
/// WebApplicationFactory rather than calling handler methods directly. Testing the
/// endpoints without the pipeline would skip model binding, the problem-details
/// mapping and the status codes — which is most of what stage B actually added.
/// </summary>
public partial class Program;
