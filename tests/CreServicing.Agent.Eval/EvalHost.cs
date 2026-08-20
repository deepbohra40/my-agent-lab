using CreServicing.Agent.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreServicing.Agent.Eval;

/// <summary>
/// The container the eval tests resolve extractors from.
///
/// Before the extractors took their dependencies by injection they were static
/// classes that reached into the environment and built their own credential, and
/// a test could not have supplied anything else if it wanted to. It now can —
/// which is what makes a recorded-response harness possible later without
/// touching a single extractor. This one still resolves the real thing, because
/// the point of the eval suite is to measure the real model.
///
/// Built once for the whole assembly rather than per test class: the container
/// owns a token-caching credential and an HTTP pipeline, and rebuilding it per
/// class would re-authenticate for no reason.
/// </summary>
internal static class EvalHost
{
    private static readonly Lazy<IServiceProvider> Instance = new(Build);

    public static T Resolve<T>() where T : notnull => Instance.Value.GetRequiredService<T>();

    private static IServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        return new ServiceCollection()
            .AddCreServicing(configuration)
            .BuildServiceProvider();
    }
}
