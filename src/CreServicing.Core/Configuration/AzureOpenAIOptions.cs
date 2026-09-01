using System.ComponentModel.DataAnnotations;

namespace CreServicing.Core.Configuration;

/// <summary>
/// Where the model lives and which deployment to call.
///
/// ── Why this replaced Environment.GetEnvironmentVariable ─────────────────────
///
/// The same two variables were read in five places — four extractors and the
/// agent host — each with its own <c>?? throw</c> or <c>?? "gpt-5-mini"</c>
/// fallback. Five copies of a default is five chances for them to disagree, and
/// the failure mode is the quiet one: a run that silently uses a different
/// deployment than the one you thought you were measuring, which makes every
/// cost figure in Cost/ wrong without anything looking broken.
///
/// Binding once and validating at startup also moves the failure to the right
/// moment. Reading an unset endpoint inside <c>ExtractAsync</c> throws after the
/// process has started, printed a banner, and possibly already spent money on
/// earlier documents in the package. <c>ValidateOnStart</c> throws before any of
/// that, with a message naming the setting rather than the symptom.
/// </summary>
public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>
    /// The resource endpoint, e.g. <c>https://maf-course-db26.openai.azure.com/</c>.
    /// No default: a wrong endpoint is not something to guess at.
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage =
        "AzureOpenAI:Endpoint is not configured. Set AZURE_OPENAI_ENDPOINT or the "
        + "AzureOpenAI:Endpoint configuration key.")]
    [Url(ErrorMessage = "AzureOpenAI:Endpoint must be an absolute URL.")]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The deployment name. Defaulted, because every cost figure this project
    /// prints is priced against a specific deployment and the pricing table in
    /// <see cref="Cost.ModelPricing"/> has to agree with whatever this says.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Deployment { get; set; } = "gpt-5-mini";

    /// <summary>
    /// How long to let one model call run before giving up. Generous, because a
    /// reasoning model working through a rent roll is legitimately slow — this
    /// exists to stop a hung socket holding a servicing run open forever, not to
    /// second-guess the model.
    /// </summary>
    [Range(5, 600)]
    public int TimeoutSeconds { get; set; } = 120;
}
