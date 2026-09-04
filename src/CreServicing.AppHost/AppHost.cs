// The dashboard half of item 4. Run this project — not CreServicing.Api — when
// you want to watch a servicing run rather than call one.
//
//   dotnet run --project src/CreServicing.AppHost
//
// That starts the API, starts the Aspire dashboard, and wires the first to the
// second. The dashboard URL is printed on startup with a one-time login token in
// the query string; it is the whole login, so copy the whole line.
//
// ── Why there is no telemetry configuration in this file ─────────────────────
//
// Aspire injects OTEL_EXPORTER_OTLP_ENDPOINT (and the service name, and the
// protocol) into every project it launches. Api/Telemetry.cs already registers
// the OTLP exporter exactly when that variable is present and skips it when it is
// not — which is why standing up the dashboard required no change to the API at
// all, and why running the API directly still works with no collector and no
// background exporter retrying a connection nobody asked for.
//
// The same seam is why the integration tests are unaffected: WebApplicationFactory
// boots the API without this project, the variable is absent, and the exporter
// never registers.
//
// ── What is deliberately not here ────────────────────────────────────────────
//
// No AZURE_OPENAI_* passthrough. The API reads those from the environment through
// the PostConfigure fallback in ServiceRegistration, and a child process inherits
// this one's environment, so restating them here would be a second place for the
// endpoint to be configured and a second place for it to be wrong. If the model
// endpoints answer 503 under the dashboard, the variable is missing from the shell
// that launched this — /health will say so in as many words.
//
// No database, no cache, no container resources. There is one service. An AppHost
// that orchestrates a single project is a dashboard launcher, and pretending
// otherwise is how this file grows a Redis nobody uses.
//
// ── Why the default launch profile is https ──────────────────────────────────
//
// Properties/launchSettings.json lists https first, which is what `dotnet run`
// picks. That is not ceremony: the dashboard authenticates with a token carried
// in the URL, so plain http would put the credential on the wire in the clear.
// Aspire refuses to start over http for exactly that reason and makes you say
// ASPIRE_ALLOW_UNSECURED_TRANSPORT=true to override it.
//
// The http profile below it does set that flag, and is there for a machine with
// no trusted dev certificate — `dotnet dev-certs https --trust` is the better fix
// and takes a few seconds. Reach for the http profile when that is not an option,
// not to skip the prompt.
//
// The ports (17280 / 21330 / 23430 / 22240) are picked clear of section 5's
// MinimalAgent AppHost so both can run at once without a port fight.

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.CreServicing_Api>("cre-servicing-api")
    .WithHttpHealthCheck("/health")
    .WithUrlForEndpoint("http", url => url.DisplayText = "API")
    .WithUrls(context =>
    {
        // The OpenAPI document is mapped in Development only, which is exactly
        // the environment this launches in. A link on the resource row saves
        // guessing the path when you are trying to remember what the approval
        // sub-resource is called.
        if (context.Urls.FirstOrDefault() is { } baseUrl)
        {
            context.Urls.Add(new()
            {
                Url = baseUrl.Url.TrimEnd('/') + "/openapi/v1.json",
                DisplayText = "OpenAPI document"
            });
        }
    });

builder.Build().Run();
