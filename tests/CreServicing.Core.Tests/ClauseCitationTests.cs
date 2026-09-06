using CreServicing.Core.Citations;
using CreServicing.Core.Domain;

namespace CreServicing.Core.Tests;

/// <summary>
/// The rule that makes retrieval safe to put on a borrower's file: a clause is
/// attached only when it declares the same finding code, so a plausible-but-wrong
/// hit produces no citation rather than a wrong one.
///
/// None of these tests use an embedding model or a vector store. The verification
/// rule is the part that can be wrong in a way that matters, and it is entirely
/// deterministic — so it is tested against a scripted index, in the free suite,
/// on every push. What a real index actually retrieves is a separate question,
/// and it belongs in the eval project where live calls live.
/// </summary>
public class ClauseCitationTests
{
    private const string Loan = Given.LoanId;

    /// <summary>An index that returns exactly what a test tells it to.</summary>
    private sealed class ScriptedIndex(params ClauseHit[] hits) : IClauseIndex
    {
        public bool IsAvailable => true;

        public int Searches { get; private set; }

        public Task<IReadOnlyList<ClauseHit>> SearchAsync(
            string loanId, string query, int take, CancellationToken cancellationToken = default)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<ClauseHit>>(hits.Take(take).ToList());
        }
    }

    private static ClauseHit Hit(string clauseId, string code, double score = 0.9)
        => new(clauseId, code, "Heading", $"Operative text of {clauseId}.", score);

    private static ServicingException Finding(string code)
        => new(Loan, code, ExceptionSeverity.Breach, $"{code} summary", "evidence");

    // ── The happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_hit_whose_code_matches_is_attached_with_its_clause_number()
    {
        var resolver = new ClauseCitationResolver(new ScriptedIndex(Hit("7.3(b)", "DSCR-MIN")));

        var result = await resolver.ResolveAsync(Loan, [Finding("DSCR-MIN")]);

        var citation = Assert.Single(result.Findings).ClauseCitation;
        Assert.NotNull(citation);
        Assert.Contains("§7.3(b)", citation);
        Assert.Contains("Operative text of 7.3(b).", citation);
        Assert.Empty(result.Uncited);
    }

    [Fact]
    public async Task The_right_clause_ranked_second_is_still_used()
    {
        // The ordinary case, not an edge case. Occupancy and insurance clauses
        // share a lot of vocabulary with each other, and similarity ordering is
        // not correctness ordering — which is the entire reason more than one
        // candidate is considered.
        var resolver = new ClauseCitationResolver(new ScriptedIndex(
            Hit("8.1(a)", "INS-COVERAGE", 0.91),
            Hit("7.3(d)", "OCC-MIN", 0.88)));

        var result = await resolver.ResolveAsync(Loan, [Finding("OCC-MIN")]);

        Assert.Contains("§7.3(d)", Assert.Single(result.Findings).ClauseCitation);
    }

    // ── Failing closed ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_confident_hit_under_the_wrong_code_is_refused()
    {
        // The test this whole design exists for. A near-perfect similarity score
        // on the wrong clause must produce nothing — putting the insurance
        // covenant on a coverage-ratio breach would be a citation that reads as
        // authoritative and is simply false.
        var resolver = new ClauseCitationResolver(new ScriptedIndex(Hit("8.1(a)", "INS-COVERAGE", 0.99)));

        var result = await resolver.ResolveAsync(Loan, [Finding("DSCR-MIN")]);

        var dscr = result.Findings.Single(f => f.Code == "DSCR-MIN");
        Assert.Null(dscr.ClauseCitation);
        Assert.Equal("DSCR-MIN", Assert.Single(result.Uncited));
    }

    [Fact]
    public async Task An_index_that_returns_nothing_leaves_the_finding_intact()
    {
        var resolver = new ClauseCitationResolver(new ScriptedIndex());

        var result = await resolver.ResolveAsync(Loan, [Finding("DSCR-MIN")]);

        var dscr = result.Findings.Single(f => f.Code == "DSCR-MIN");
        Assert.Equal("DSCR-MIN summary", dscr.Summary);
        Assert.Equal(ExceptionSeverity.Breach, dscr.Severity);
        Assert.Null(dscr.ClauseCitation);
    }

    [Fact]
    public async Task Codes_are_matched_ordinally_not_loosely()
    {
        // "DSCR-MIN" is an identifier. A case-insensitive or prefix match here
        // would let "dscr-min" or "DSCR-MINIMUM" through, and a vocabulary that
        // accepts near-misses is not a closed vocabulary.
        var resolver = new ClauseCitationResolver(new ScriptedIndex(Hit("7.3(b)", "dscr-min")));

        var result = await resolver.ResolveAsync(Loan, [Finding("DSCR-MIN")]);

        Assert.Null(result.Findings.Single(f => f.Code == "DSCR-MIN").ClauseCitation);
    }

    // ── Telling "did not look" from "looked and missed" ──────────────────────

    [Fact]
    public async Task The_null_index_cites_nothing_and_complains_about_nothing()
    {
        // The free path. Findings come back exactly as the engine produced them,
        // with no citation and — crucially — no CITATION-UNRESOLVED. Not looking
        // is not a failure to find, and a zero-cost run must not start reporting
        // a problem it was never configured to solve.
        var resolver = new ClauseCitationResolver(new NullClauseIndex());
        var findings = new[] { Finding("DSCR-MIN"), Finding("OCC-MIN") };

        var result = await resolver.ResolveAsync(Loan, findings);

        Assert.False(result.Attempted);
        Assert.Empty(result.Uncited);
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Null(f.ClauseCitation));
    }

    [Fact]
    public async Task A_configured_index_that_misses_says_so_once_on_the_record()
    {
        // An index was configured, so a reviewer is entitled to read a citation's
        // absence as meaningful. One aggregate finding rather than one per miss:
        // three uncited findings are a single problem with the index.
        var resolver = new ClauseCitationResolver(new ScriptedIndex(Hit("7.3(b)", "DSCR-MIN")));
        var findings = new[] { Finding("DSCR-MIN"), Finding("OCC-MIN"), Finding("INS-EXPIRY") };

        var result = await resolver.ResolveAsync(Loan, findings);

        var unresolved = result.Findings.Single(f => f.Code == "CITATION-UNRESOLVED");
        Assert.Equal(ExceptionSeverity.Informational, unresolved.Severity);
        Assert.Contains("OCC-MIN", unresolved.Evidence);
        Assert.Contains("INS-EXPIRY", unresolved.Evidence);
        Assert.DoesNotContain("DSCR-MIN", unresolved.Evidence);
        Assert.Equal(4, result.Findings.Count);
    }

    [Fact]
    public async Task Every_code_the_resolver_can_emit_is_declared_by_the_engine()
    {
        // CITATION-UNRESOLVED reaches an exception report but is produced outside
        // CovenantEngine, so nothing else would catch it falling out of the write
        // tool's allowlist.
        var resolver = new ClauseCitationResolver(new ScriptedIndex());

        var result = await resolver.ResolveAsync(Loan, [Finding("DSCR-MIN")]);

        Assert.All(result.Findings, f => Assert.Contains(f.Code, CovenantEngine.KnownCodes));
    }

    // ── Shape guarantees the callers rely on ─────────────────────────────────

    [Fact]
    public async Task Findings_keep_their_order_and_none_are_dropped()
    {
        var resolver = new ClauseCitationResolver(new ScriptedIndex(Hit("7.3(b)", "DSCR-MIN")));
        var findings = new[] { Finding("OCC-MIN"), Finding("DSCR-MIN"), Finding("INS-EXPIRY") };

        var result = await resolver.ResolveAsync(Loan, findings);

        Assert.Equal(
            ["OCC-MIN", "DSCR-MIN", "INS-EXPIRY"],
            result.Findings.Where(f => f.Code != "CITATION-UNRESOLVED").Select(f => f.Code));
    }

    [Fact]
    public async Task A_compliant_loan_costs_no_searches_at_all()
    {
        var index = new ScriptedIndex(Hit("7.3(b)", "DSCR-MIN"));

        var result = await new ClauseCitationResolver(index).ResolveAsync(Loan, []);

        Assert.Empty(result.Findings);
        Assert.Equal(0, index.Searches);
    }

    [Fact]
    public async Task A_citation_never_changes_the_finding_it_decorates()
    {
        // The boundary restated as an assertion. Citation runs after the engine
        // and may only add a quote — if it can alter a severity or a summary, a
        // retrieval result has become able to change a verdict.
        var original = Finding("DSCR-MIN");
        var resolver = new ClauseCitationResolver(new ScriptedIndex(Hit("7.3(b)", "DSCR-MIN")));

        var cited = (await resolver.ResolveAsync(Loan, [original])).Findings.Single();

        Assert.Equal(original with { ClauseCitation = cited.ClauseCitation }, cited);
    }
}

/// <summary>
/// The hand-written clause manifests are ground truth, and ground truth that
/// drifts is worse than none. These read what is actually on disk.
/// </summary>
public class AgreementManifestTests
{
    [Fact]
    public void The_lakeview_agreement_is_indexed()
        => Assert.Contains("CRE-2019-0447", AgreementStore.ListAgreements());

    [Fact]
    public void Every_indexed_clause_governs_a_code_the_engine_can_emit()
    {
        // A clause tagged with a code no covenant test produces could never match
        // anything, and would sit in the index looking like coverage it is not.
        var clauses = AgreementStore.GetClauses("CRE-2019-0447");

        Assert.NotEmpty(clauses);
        Assert.All(clauses, clause => Assert.Contains(clause.Code, CovenantEngine.KnownCodes));
    }

    [Fact]
    public void No_two_clauses_claim_the_same_code()
    {
        // Two clauses for one code makes the resolver's answer depend on retrieval
        // order, which is exactly the non-determinism the code match exists to
        // remove. If a covenant genuinely spans two clauses, the manifest entry
        // should quote both rather than appear twice.
        var codes = AgreementStore.GetClauses("CRE-2019-0447").Select(c => c.Code).ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_clause_carries_a_number_a_heading_and_quotable_text()
    {
        Assert.All(AgreementStore.GetClauses("CRE-2019-0447"), clause =>
        {
            Assert.False(string.IsNullOrWhiteSpace(clause.ClauseId));
            Assert.False(string.IsNullOrWhiteSpace(clause.Heading));
            Assert.True(clause.Text.Length > 40, $"Clause {clause.ClauseId} text is too short to be a citation.");
        });
    }

    [Fact]
    public void Every_quoted_clause_actually_appears_in_the_agreement()
    {
        // The manifest is a hand-made derivative of the .txt, so nothing but a
        // test stops the two drifting apart — someone edits the agreement's
        // wording and the citations quietly start quoting text that is no longer
        // in the document they cite. Comparison ignores the line wrapping the
        // agreement uses for readability.
        var agreement = Squash(File.ReadAllText(
            Path.Combine(AgreementStore.Root, "CRE-2019-0447-loan-agreement.txt")));

        Assert.All(AgreementStore.GetClauses("CRE-2019-0447"), clause =>
            Assert.True(
                agreement.Contains(Squash(clause.Text), StringComparison.Ordinal),
                $"Clause {clause.ClauseId}'s quoted text is not present in the agreement."));
    }

    private static string Squash(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
