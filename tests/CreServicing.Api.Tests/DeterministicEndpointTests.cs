using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CreServicing.Api.Tests;

/// <summary>
/// Stage B: the half of the API that needs no model, tested in the state it needs
/// to work in — with no Azure configuration at all.
///
/// This is the repo's oldest invariant and the one most likely to rot. Every
/// version of this project has claimed the covenant path costs nothing and
/// requires no credential; putting it behind HTTP is exactly the change that would
/// quietly break that, because the obvious way to host it — validate the model
/// configuration at startup, like the CLI does — takes the free endpoints down
/// with the paid ones.
///
/// So the factory here boots with an empty configuration on purpose. If someone
/// later adds ValidateOnStart back to the API's composition, every test in this
/// file fails at startup, which is the correct outcome.
/// </summary>
public class DeterministicEndpointTests
{
    [Fact]
    public async Task The_api_starts_and_serves_the_deterministic_paths_with_no_configuration()
    {
        using var factory = ServicingApiFactory.Unconfigured();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("available", body.RootElement.GetProperty("deterministicEndpoints").GetString());

        // The honest half: it says so rather than pretending.
        Assert.Equal("unconfigured", body.RootElement.GetProperty("modelEndpoints").GetString());
    }

    [Fact]
    public async Task The_loan_list_comes_from_the_system_of_record()
    {
        using var factory = ServicingApiFactory.Unconfigured();
        using var client = factory.CreateClient();

        var loans = await client.GetFromJsonAsync<List<LoanSummaryResponse>>("/loans");

        Assert.NotNull(loans);
        Assert.NotEmpty(loans);
        Assert.Contains(loans, loan => loan.LoanId == "CRE-2019-0447");

        // Covenant thresholds come from the loan agreement, never from a document,
        // so they must be present and non-zero on every loan.
        Assert.All(loans, loan =>
        {
            Assert.True(loan.MinimumDscr > 0);
            Assert.False(string.IsNullOrWhiteSpace(loan.BorrowerName));
        });
    }

    [Fact]
    public async Task A_covenant_review_returns_findings_computed_in_csharp()
    {
        using var factory = ServicingApiFactory.Unconfigured();
        using var client = factory.CreateClient();

        var review = await client.GetFromJsonAsync<CovenantReviewResponse>("/loans/CRE-2019-0447/covenant-review");

        Assert.NotNull(review);
        Assert.Equal("CRE-2019-0447", review.LoanId);
        Assert.Equal("hand-keyed", review.Source);
        Assert.NotEmpty(review.Findings);

        // Every finding carries its arithmetic. An exception without evidence is
        // an assertion, not an audit record — the same property the engine tests
        // pin, enforced again at the wire.
        Assert.All(review.Findings, finding =>
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.Code));
            Assert.False(string.IsNullOrWhiteSpace(finding.Evidence));
        });
    }

    [Fact]
    public async Task The_same_review_twice_returns_the_same_findings()
    {
        // The property the whole project is arranged around, asserted through the
        // transport rather than against the engine. Same inputs, same findings,
        // every call — which is what makes this the audit-grade path and the
        // reason it is not the model's job.
        using var factory = ServicingApiFactory.Unconfigured();
        using var client = factory.CreateClient();

        var first = await client.GetStringAsync("/loans/CRE-2019-0447/covenant-review");
        var second = await client.GetStringAsync("/loans/CRE-2019-0447/covenant-review");

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task An_unknown_loan_is_a_404_that_names_the_known_ones()
    {
        using var factory = ServicingApiFactory.Unconfigured();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/loans/CRE-9999-0000/covenant-review");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // An operator who mistyped a loan id should not have to go read the source
        // to find out what a valid one looks like.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CRE-2019-0447", body);
    }

    [Fact]
    public async Task The_package_listing_returns_names_and_sizes_but_never_content()
    {
        using var factory = ServicingApiFactory.Unconfigured();
        using var client = factory.CreateClient();

        var package = await client.GetFromJsonAsync<PackageResponse>("/loans/CRE-2019-0447/documents");

        Assert.NotNull(package);
        Assert.Equal(package.DocumentCount, package.Documents.Count);
        Assert.NotEmpty(package.Documents);
        Assert.All(package.Documents, document =>
        {
            Assert.True(document.ApproximateTokens > 0);
            // Forward slashes on every platform: the model is told to pass these
            // back verbatim, and a Windows-shaped path in a JSON response would
            // fail against a fixture root built with the other separator.
            Assert.DoesNotContain('\\', document.RelativePath);
        });
    }

    [Fact]
    public async Task A_loan_with_no_documents_on_file_is_an_empty_package_rather_than_an_error()
    {
        // CRE-2018-0233 has no fixture package by design — it demonstrates the
        // "no documents on file" path, which is the ordinary state of a borrower
        // who has not reported yet, not a fault.
        using var factory = ServicingApiFactory.Unconfigured();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/loans/CRE-2018-0233/documents");
        response.EnsureSuccessStatusCode();

        var package = await response.Content.ReadFromJsonAsync<PackageResponse>();
        Assert.NotNull(package);
        Assert.Equal(0, package.DocumentCount);
    }

    // ── The model-backed routes, in the state where they cannot work ─────────

    [Fact]
    public async Task Starting_a_run_without_a_model_is_a_503_that_names_the_missing_setting()
    {
        using var factory = ServicingApiFactory.Unconfigured();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/servicing-runs", new StartRunRequest("CRE-2019-0447", "test-operator"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("AzureOpenAI:Endpoint", body);

        // And it says what still works, because the alternative is an operator
        // concluding the whole service is down.
        Assert.Contains("covenant-review", body);
    }

    [Fact]
    public async Task Runs_can_still_be_listed_on_a_host_that_cannot_call_a_model()
    {
        // Looking at a run is a storage question with no model in it. Routing the
        // read through a type that depends on an IChatClient made listing runs
        // fail on exactly the host where you most want to see what happened — and
        // it failed from inside the container, as a 500, because minimal APIs
        // resolve handler parameters before the handler runs.
        using var factory = ServicingApiFactory.Unconfigured();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/servicing-runs");
        response.EnsureSuccessStatusCode();
        Assert.Equal("[]", (await response.Content.ReadAsStringAsync()).Trim());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/servicing-runs/nope")).StatusCode);
    }

    [Fact]
    public async Task Assembling_a_snapshot_without_a_model_is_a_503_and_charges_nothing()
    {
        using var factory = ServicingApiFactory.Unconfigured();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/loans/CRE-2019-0447/financial-snapshot", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("nothing was charged", await response.Content.ReadAsStringAsync());
    }
}
