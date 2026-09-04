using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using CreServicing.Core.Agents;
using CreServicing.Core.Diagnostics;
using CreServicing.Core.Extraction;
using CreServicing.Core.Runs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace CreServicing.Core.Configuration;

/// <summary>
/// The composition root. Everything that was previously newed up inline lives
/// here instead, registered once and injected.
///
/// This is the whole of the "host it properly" argument in one file: before it,
/// <c>new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())</c>
/// appeared in five places — the four extractors and the agent host — each
/// re-reading the same environment variables and each hardcoding a credential
/// that works on a developer's laptop and nowhere else.
/// </summary>
public static class ServiceRegistration
{
    /// <param name="validateOnStart">
    /// Whether a missing or malformed <c>AzureOpenAI</c> section should stop the
    /// host from starting.
    ///
    /// True for the CLI, where the process exists to do one model-backed thing and
    /// failing before it prints a banner is strictly better.
    ///
    /// False for the API, and that is a deliberate asymmetry rather than an
    /// oversight. Most of the surface — the covenant engine, the loan terms, the
    /// document listing — needs no credential and no model, and it is the part
    /// that must never be unavailable, because it is the audit-grade path. A web
    /// host that crash-loops because extraction is unconfigured takes the
    /// deterministic endpoints down with it to protect endpoints nobody called.
    /// The API instead answers 503 on exactly the routes that need a model, naming
    /// the setting that is missing.
    /// </param>
    public static IServiceCollection AddCreServicing(
        this IServiceCollection services, IConfiguration configuration, bool validateOnStart = true)
    {
        var options = services
            .AddOptions<AzureOpenAIOptions>()
            .Bind(configuration.GetSection(AzureOpenAIOptions.SectionName))
            // The two bare environment variables this project has always used are
            // honoured as a fallback, so an existing machine keeps working without
            // being reconfigured. PostConfigure rather than a configuration source
            // because it must fill gaps, never override: AzureOpenAI:Endpoint set
            // explicitly has to win over an AZURE_OPENAI_ENDPOINT left over in the
            // user's environment from something else.
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.Endpoint))
                {
                    options.Endpoint =
                        Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? string.Empty;
                }

                if (Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME") is { Length: > 0 } fromEnv
                    && configuration[$"{AzureOpenAIOptions.SectionName}:Deployment"] is null)
                {
                    options.Deployment = fromEnv;
                }
            })
            .ValidateDataAnnotations();

        // Fail at startup, not on the first model call. By the time an extractor
        // discovers the endpoint is missing, the process has printed a banner and
        // may already have spent money on earlier documents in the same package.
        if (validateOnStart)
        {
            options.ValidateOnStart();
        }

        // ── Credential ───────────────────────────────────────────────────────
        //
        // DefaultAzureCredential rather than AzureCliCredential. The chain still
        // picks up `az login` locally — nothing about the developer loop changes
        // — but the same binary now authenticates with a managed identity in
        // Azure, which AzureCliCredential can never do because there is no Azure
        // CLI on an App Service instance. That single substitution is the
        // difference between code that runs on one laptop and code that deploys.
        //
        // Registered as a singleton because the credential caches tokens; a new
        // one per call would re-authenticate on every request.
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
            var credential = provider.GetRequiredService<TokenCredential>();

            // ── Resilience ───────────────────────────────────────────────────
            //
            // Worth being precise here rather than reaching for Polly by reflex.
            // AzureOpenAIClient is built on System.ClientModel, whose pipeline
            // already retries transient failures and already honours Retry-After
            // on a 429 — which is the failure that actually matters against a
            // TPM-capped deployment. Stacking Microsoft.Extensions.Http.Resilience
            // on top of that does not add a behaviour, it adds a second
            // uncoordinated retry budget, and two retry policies multiply: three
            // outer attempts over three inner ones is nine calls to a service
            // that is already telling you to slow down.
            //
            // So the pipeline's own policy is configured rather than replaced.
            // What was actually missing was not retries but *bounds* — the
            // defaults were never stated, so nothing in this repo said how long
            // a hung call could hold a servicing run open.
            var clientOptions = new AzureOpenAIClientOptions
            {
                NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 3)
            };

            return new AzureOpenAIClient(new Uri(options.Endpoint), credential, clientOptions);
        });

        // IChatClient, not the Azure client, is what the rest of the code depends
        // on. That is pillar one of the framework and it is not decoration: every
        // extractor and the agent host now name a provider-neutral abstraction,
        // so swapping model provider is a change to this file and nothing else.
        services.AddSingleton<IChatClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;

            var client = provider.GetRequiredService<AzureOpenAIClient>()
                .GetChatClient(options.Deployment)
                .AsIChatClient();

            // ── Why the model calls are wrapped rather than just subscribed to ──
            //
            // Turning on the OpenTelemetry SDK in the host is not enough to see
            // model calls: a bare IChatClient emits nothing. The GenAI spans —
            // model name, token counts, finish reason, duration — come from this
            // decorator, and without it the dashboard shows a servicing run whose
            // agent turns contain no evidence that a model was involved at all.
            //
            // Registered here rather than in the API's composition because the CLI
            // deserves the same spans, and because a decorator that only some
            // hosts apply is a decorator that reports different numbers depending
            // on who is asking.
            //
            // EnableSensitiveData is deliberately NOT set. It puts prompts and
            // completions on the spans, which here means whole borrower rent rolls
            // and operating statements leaving the process for a collector. The
            // /documents endpoint returns names and sizes but never content and
            // there is a test pinning that; this is the same boundary, and it is
            // the one that would be crossed by accident.
            return client
                .AsBuilder()
                .UseOpenTelemetry(sourceName: ServicingTelemetry.ActivitySourceName)
                .Build(provider);
        });

        // The extractors and the runner. Singletons because each builds an
        // AIAgent over the injected IChatClient in its constructor, and rebuilding
        // that per resolve would be pure waste — the agent is a stateless wrapper
        // around a client that is itself designed to be shared.
        //
        // These being registered at all is the point of the exercise. Before this,
        // each one reached out to the environment for its own configuration and
        // constructed its own credential, which is why swapping provider meant
        // editing five files and why none of them could be tested with a stub.
        services.AddSingleton<RentRollExtractor>();
        services.AddSingleton<OperatingStatementExtractor>();
        services.AddSingleton<InsuranceCertificateExtractor>();
        services.AddSingleton<TaxBillExtractor>();
        services.AddSingleton<FinancialSnapshotAssembler>();

        // ── The approval loop's state ────────────────────────────────────────
        //
        // ServicingRunner is a singleton and holds no run state: it builds a fresh
        // agent, ledger and tool set for every call, which is what allows one
        // instance to serve concurrent reviews. Everything that varies per run
        // lives in the ServicingRun record and therefore in the store.
        //
        // The store is the seam a real deployment replaces. Registered by its
        // interface for that reason and no other — see IRunStore for what the
        // in-memory implementation does and does not promise.
        services.AddSingleton<ServicingRunner>();
        services.AddSingleton<IRunStore, InMemoryRunStore>();
        services.AddSingleton<ServicingRunService>();

        return services;
    }
}
