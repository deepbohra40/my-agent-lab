using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using CreServicing.Agent.Agents;
using CreServicing.Agent.Extraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace CreServicing.Agent.Configuration;

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
    public static IServiceCollection AddCreServicing(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
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
            .ValidateDataAnnotations()
            // Fail at startup, not on the first model call. By the time an
            // extractor discovers the endpoint is missing, the process has
            // printed a banner and may already have spent money on earlier
            // documents in the same package.
            .ValidateOnStart();

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
            return provider.GetRequiredService<AzureOpenAIClient>()
                .GetChatClient(options.Deployment)
                .AsIChatClient();
        });

        // The extractors and the agent host. Singletons because each builds an
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
        services.AddSingleton<ServicingAgentHost>();

        return services;
    }
}
