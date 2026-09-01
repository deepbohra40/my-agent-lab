using CreServicing.Core.Configuration;
using Microsoft.Extensions.Options;

namespace CreServicing.Api;

/// <summary>
/// Answers one question: can this instance call a model right now?
///
/// The CLI validates its configuration at startup and exits 1 if it cannot,
/// because a console process exists to do the one thing it was invoked for. A web
/// host is different. Most of this API — covenant review, loan terms, document
/// listings — is deterministic C# that needs no credential, and it is the part
/// that must never be down, because it is the audit-grade path. Refusing to start
/// because extraction is unconfigured would take those endpoints offline to
/// protect endpoints nobody called.
///
/// So the host starts, and the routes that need a model check here first and
/// answer 503 naming the missing setting. A 503 an operator can act on beats a
/// container that crash-loops with the same information in a log nobody is
/// tailing.
///
/// Resolved per request rather than cached because <see cref="IOptionsMonitor{T}"/>
/// reflects configuration reloads, and an instance that had its endpoint supplied
/// after start should begin working without a restart.
/// </summary>
public sealed class ModelAvailability(IOptionsMonitor<AzureOpenAIOptions> options)
{
    /// <summary>
    /// The configured deployment, or a placeholder when configuration is invalid.
    /// Only read on paths that have already passed <see cref="Unavailable"/>,
    /// except in cost reporting, where naming an unknown deployment is better than
    /// throwing on the way to a price.
    /// </summary>
    public string Deployment
    {
        get
        {
            try
            {
                return options.CurrentValue.Deployment;
            }
            catch (OptionsValidationException)
            {
                return "(unconfigured)";
            }
        }
    }

    /// <summary>
    /// Null when a model call is possible; a 503 problem response naming every
    /// failed setting when it is not.
    /// </summary>
    public IResult? Unavailable()
    {
        try
        {
            _ = options.CurrentValue;
            return null;
        }
        catch (OptionsValidationException ex)
        {
            return Results.Problem(
                title: "This endpoint needs a model and none is configured.",
                detail: string.Join(" ", ex.Failures)
                        + " Nothing was run and nothing was charged. The deterministic endpoints "
                        + "— /loans and /loans/{loanId}/covenant-review — need none of this and still work.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>For /health, which reports the fact rather than acting on it.</summary>
    public bool IsConfigured() => Unavailable() is null;
}
