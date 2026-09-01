using CreServicing.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreServicing.Api.Tests;

/// <summary>
/// Boots the real API in memory.
///
/// Two configurations, because the API has two meaningfully different states and
/// both are worth testing:
///
///   - <see cref="Unconfigured"/>: no Azure OpenAI settings at all. This is the
///     state the repo has always claimed the deterministic paths work in, and it
///     is the one a reviewer will actually try — clone, run, hit an endpoint,
///     with no credential. If that stops working, the claim in the README is a
///     lie and nobody will read far enough to find out why.
///
///   - <see cref="WithScriptedModel"/>: configured, with the model replaced by a
///     script. Lets the whole approval loop be driven over HTTP — start a run,
///     get the question back, POST the answer, watch it resume — with no Azure
///     call and no cost.
/// </summary>
internal sealed class ServicingApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly IChatClient? _chatClient;

    private ServicingApiFactory(Dictionary<string, string?> settings, IChatClient? chatClient)
    {
        _settings = settings;
        _chatClient = chatClient;
    }

    /// <summary>No credential, no endpoint, nothing. The free path must still work.</summary>
    public static ServicingApiFactory Unconfigured() => new([], null);

    public static ServicingApiFactory WithScriptedModel(IChatClient chatClient) => new(
        new Dictionary<string, string?>
        {
            ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com/",
            ["AzureOpenAI:Deployment"] = "gpt-5-mini"
        },
        chatClient);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            // Added last so it wins over anything the host picked up. A developer
            // machine with AZURE_OPENAI_ENDPOINT exported would otherwise turn the
            // "unconfigured" tests green for the wrong reason — and those are
            // exactly the tests whose value depends on the variable being absent.
            configuration.AddInMemoryCollection(_settings);
        });

        builder.ConfigureServices(services =>
        {
            // The legacy AZURE_OPENAI_* fallback in ServiceRegistration reads the
            // environment directly rather than through IConfiguration, so clearing
            // configuration is not enough to guarantee an unconfigured host.
            // Neutralised here for the lifetime of the test run.
            if (_settings.Count == 0)
            {
                Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
                Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME", null);
            }

            if (_chatClient is not null)
            {
                // Replace, not add. Appending would leave the real registration in
                // the container and make which one wins a property of ordering.
                services.RemoveAll<IChatClient>();
                services.AddSingleton(_chatClient);
            }
        });
    }
}
