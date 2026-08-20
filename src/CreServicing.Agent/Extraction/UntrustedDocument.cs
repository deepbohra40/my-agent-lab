using CreServicing.Agent.Data;

namespace CreServicing.Agent.Extraction;

/// <summary>
/// Fences a document's text as untrusted data for a model prompt, and defuses any
/// attempt to close the fence early and continue as instruction.
///
/// Every extractor in this project asks the same question — how do you hand a
/// borrower-supplied document to a model without its text reading as
/// instructions? — and four independently typed answers would drift the moment
/// one of them got patched. This is the one answer.
///
/// The delimiter is the cheap half of injection defence: it gives the model a
/// boundary to reason about, which measurably helps and never guarantees. It
/// does not make the text safe. The actual guarantee in this project is
/// structural — <see cref="Domain.CovenantEngine"/> decides from typed numbers,
/// and no sentence in a document can reach that code path.
/// </summary>
public static class UntrustedDocument
{
    private const string Open = "<<<BEGIN UNTRUSTED DOCUMENT>>>";
    private const string Close = "<<<END UNTRUSTED DOCUMENT>>>";

    /// <summary>
    /// <paramref name="taskLine"/> is the one sentence describing what to do with
    /// the document below the fence, e.g. "Extract the rent roll below."
    /// </summary>
    public static string Wrap(SourceDocument document, string taskLine)
    {
        var text = document.Text;

        // A document containing our own markers is either a formatting accident
        // or someone trying to close the fence early and continue as instruction.
        // Neutralise it and say so, rather than trusting the boundary to hold.
        var tampered = text.Contains(Open, StringComparison.OrdinalIgnoreCase)
                       || text.Contains(Close, StringComparison.OrdinalIgnoreCase);
        if (tampered)
        {
            text = text
                .Replace(Open, "[REDACTED MARKER]", StringComparison.OrdinalIgnoreCase)
                .Replace(Close, "[REDACTED MARKER]", StringComparison.OrdinalIgnoreCase);

            Console.WriteLine("  ! Document contained the fence markers. Redacted before sending.");
            Console.WriteLine();
        }

        return $"""
                {taskLine} Its file name is {document.FileName}.

                {Open}
                {text}
                {Close}
                """;
    }
}
