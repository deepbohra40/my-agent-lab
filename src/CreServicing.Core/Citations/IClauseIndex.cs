namespace CreServicing.Core.Citations;

/// <summary>
/// One covenant clause from a loan agreement, and the finding code it governs.
///
/// <paramref name="Code"/> is the field that makes retrieval safe to put on an
/// audit record. Retrieval is a similarity search and similarity is not
/// correctness — a query about occupancy can plausibly rank an insurance clause
/// second. Carrying the governing code as data means the resolver can check the
/// hit rather than trust it, so a bad retrieval yields no citation instead of a
/// wrong one.
///
/// <paramref name="Score"/> is reported but deliberately not used as a
/// threshold. A cutoff is a number nobody can defend in a review — "we cited it
/// because it scored 0.83" is not an argument. The code match is the gate; the
/// score exists so a human reading a trace can see how close the runner-up was.
/// </summary>
public sealed record ClauseHit(
    string ClauseId,
    string Code,
    string Heading,
    string Text,
    double Score);

/// <summary>
/// Finds the clause of a loan agreement that a finding is asserted under.
///
/// An interface for the same reason <c>IRunStore</c> is one: the real
/// implementation needs an embedding model and a credential, and CI is
/// deliberately offline and credential-free. The default path resolves
/// <see cref="NullClauseIndex"/> and behaves exactly as the project did before
/// citations existed.
///
/// Nothing here decides whether a hit is usable. That is
/// <see cref="ClauseCitationResolver"/>'s job, and keeping the two apart is what
/// lets the verification rule be tested without any index at all.
/// </summary>
public interface IClauseIndex
{
    /// <summary>
    /// True when this index can actually answer. False for the null
    /// implementation, which is how the resolver tells "we did not look" apart
    /// from "we looked and found nothing" — a distinction the exception report
    /// depends on.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The clauses most similar to <paramref name="query"/>, best first. An
    /// empty result is a legitimate answer, not an error.
    /// </summary>
    Task<IReadOnlyList<ClauseHit>> SearchAsync(
        string loanId, string query, int take, CancellationToken cancellationToken = default);
}

/// <summary>
/// The index that isn't one. Every search returns nothing and
/// <see cref="IsAvailable"/> is false.
///
/// This is what the free <c>dotnet run</c> resolves, and it is why that path
/// still needs no credential, makes no call and costs nothing after citations
/// were added. Findings come back with a null <c>ClauseCitation</c>, which is
/// exactly what they did before — and because <see cref="IsAvailable"/> is
/// false, no "could not be cited" finding is raised either. Not looking is not
/// a failure to find.
/// </summary>
public sealed class NullClauseIndex : IClauseIndex
{
    public bool IsAvailable => false;

    public Task<IReadOnlyList<ClauseHit>> SearchAsync(
        string loanId, string query, int take, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ClauseHit>>([]);
}
