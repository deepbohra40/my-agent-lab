using CreServicing.Core.Diagnostics;
using CreServicing.Core.Domain;

namespace CreServicing.Core.Citations;

/// <summary>
/// Attaches loan-agreement clauses to covenant findings — and refuses to attach
/// the wrong one.
///
/// ── Why this is not inside CovenantEngine ────────────────────────────────────
///
/// <see cref="CovenantEngine"/> is pure: same terms plus same snapshot, same
/// findings, forever, with no I/O and no model. Retrieval is asynchronous,
/// network-bound and non-deterministic. Putting it inside the engine would trade
/// away the one property the whole project is arranged to protect, so this runs
/// after the engine instead and takes its output as input. The verdict is
/// already fixed by the time a citation is looked for; a citation can decorate a
/// finding, and can never create, suppress or change one.
///
/// ── The verification rule ────────────────────────────────────────────────────
///
/// Retrieval proposes, this class disposes. Every clause carries the finding
/// code it governs, so a hit is only usable when that code equals the finding's
/// own. A search for "occupancy below the covenant minimum" that ranks the
/// insurance clause first is not a disaster here — it is simply rejected, and
/// the finding goes out uncited. The failure mode of a similarity search is
/// "plausible but wrong", which is precisely the failure a borrower's file must
/// never carry, so the design makes it fail closed.
///
/// Note what is deliberately absent: a score threshold. "We cited it because it
/// scored above 0.8" is not a defensible sentence in a review, and a threshold
/// is the knob that gets widened when citations stop appearing. The code match
/// is the gate. The score is carried for humans reading a trace.
/// </summary>
public sealed class ClauseCitationResolver(IClauseIndex index)
{
    /// <summary>
    /// How many candidates to consider. More than one because the best match by
    /// similarity is not always the right clause by code — the correct clause
    /// ranking second is a normal outcome and worth catching. Small, because if
    /// the right clause is not in the top few the index is the problem and
    /// digging deeper only converts a miss into a coincidence.
    /// </summary>
    private const int Candidates = 4;

    /// <summary>
    /// Returns the findings with citations attached where one could be verified.
    ///
    /// The input list is never reordered and never filtered. Callers that
    /// already rendered or persisted findings can hand them here and get back a
    /// list that matches position for position.
    /// </summary>
    public async Task<CitationResult> ResolveAsync(
        string loanId,
        IReadOnlyList<ServicingException> findings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loanId);
        ArgumentNullException.ThrowIfNull(findings);

        if (!index.IsAvailable || findings.Count == 0)
        {
            // No index configured: hand the findings straight back, uncited and
            // unflagged. This is the free path, and it must stay indistinguishable
            // from how the project behaved before citations existed.
            return new CitationResult(findings, Attempted: false, Uncited: []);
        }

        using var activity = ServicingTelemetry.Citations(loanId, findings.Count);

        var cited = new List<ServicingException>(findings.Count);
        var uncited = new List<string>();

        foreach (var finding in findings)
        {
            var hits = await index.SearchAsync(loanId, QueryFor(finding), Candidates, cancellationToken);

            // First hit whose governing code matches. Ordinal, because a finding
            // code is an identifier and not prose.
            var match = hits.FirstOrDefault(hit => string.Equals(hit.Code, finding.Code, StringComparison.Ordinal));

            if (match is null)
            {
                uncited.Add(finding.Code);
                cited.Add(finding);
                continue;
            }

            cited.Add(finding with { ClauseCitation = $"§{match.ClauseId} {match.Heading}: {match.Text}" });
        }

        activity?.SetTag("cre.citations_resolved", cited.Count - uncited.Count);
        activity?.SetTag("cre.citations_unresolved", uncited.Count);

        if (uncited.Count > 0)
        {
            // Recorded on the report, not just in a log. An index was configured,
            // so a reviewer is entitled to assume findings are grounded in the
            // agreement — and silence would let them assume it about a finding
            // that is not. Same argument as LTV-UNTESTED: the omission has to be
            // visible in the record rather than inferred from an absence.
            //
            // One aggregate finding, not one per miss. Six uncited findings are a
            // single problem with the index, and six identical entries on a
            // borrower's file would be noise that trains a reviewer to skim.
            cited.Add(new ServicingException(
                loanId,
                "CITATION-UNRESOLVED",
                ExceptionSeverity.Informational,
                $"{uncited.Count} finding(s) could not be grounded in the loan agreement.",
                $"No clause governing {string.Join(", ", uncited)} was matched in the indexed agreement "
                + $"for {loanId}. The findings themselves are unaffected; only the citation is missing."));
        }

        return new CitationResult(cited, Attempted: true, Uncited: uncited);
    }

    /// <summary>
    /// What to search for. The summary carries the covenant's own vocabulary —
    /// "Debt Service Coverage Ratio", "physical occupancy", "property insurance"
    /// — which is the language the agreement uses too, so it makes a better query
    /// than the code. The code is an internal identifier and appears nowhere in
    /// the document.
    ///
    /// The heading-style prefix is included because a bare summary carries the
    /// borrower's figures, and dollar amounts are noise in a similarity search
    /// against legal prose.
    /// </summary>
    private static string QueryFor(ServicingException finding)
        => $"{finding.Code}: {finding.Summary}";
}

/// <summary>
/// The findings after citation, and enough context to say what happened.
///
/// <paramref name="Attempted"/> distinguishes "no index configured" from "index
/// configured and found nothing", which read identically if you only look at the
/// citations. The free path reports false here forever, and that is correct.
/// </summary>
public sealed record CitationResult(
    IReadOnlyList<ServicingException> Findings,
    bool Attempted,
    IReadOnlyList<string> Uncited);
