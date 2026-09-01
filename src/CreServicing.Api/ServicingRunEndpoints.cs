using CreServicing.Core.Agents;
using CreServicing.Core.Data;
using CreServicing.Core.Runs;

namespace CreServicing.Api;

/// <summary>
/// Stage C: the approval loop, over HTTP.
///
/// ── The shape, and why it is this shape ──────────────────────────────────────
///
/// A servicing review is a long-running operation that stops to ask a human a
/// question. Modelling it as one request that blocks until someone answers would
/// hold a connection open for however long the operator takes — minutes, or until
/// they go to lunch — and lose the run entirely if anything in between times out.
///
/// So the run is a resource. POST creates one and returns whatever state it
/// reached, which is normally "suspended, and here is what I am asking". The
/// answers come back as a POST to a sub-resource, and the response is the run's
/// new state — possibly another question. The client loops until the run is
/// terminal, and the loop is the same one the console does, with the same states,
/// because both are driving <see cref="ServicingRunner"/>.
///
/// Two properties worth noticing because they are the ones a review would probe:
///
///   - The POST that starts a run returns 201 even when the run has suspended.
///     The resource exists and is addressable; "not finished" is its state, not a
///     failure to create it.
///
///   - Submitting approvals is not idempotent and does not pretend to be, but it
///     is safe against the failure that matters. A duplicate submission finds the
///     request ids no longer outstanding and gets a 400 rather than filing the
///     exception twice. See ServicingRunService for the lock that makes the
///     ordering deterministic.
/// </summary>
public static class ServicingRunEndpoints
{
    /// <summary>
    /// The fallback when the caller does not say who they are. This project has no
    /// authentication; in a real deployment the approver comes from the
    /// authenticated principal and this parameter would not exist, because the
    /// entire value of the field is that the person approving cannot choose what
    /// it says about them.
    /// </summary>
    private const string DefaultApprover = "api-operator";

    public static void MapServicingRunEndpoints(this WebApplication app)
    {
        var runs = app.MapGroup("/servicing-runs").WithTags("Servicing runs");

        // IServiceProvider rather than ServicingRunService, for the reason spelled
        // out on the snapshot endpoint in LoanEndpoints: handler parameters are
        // resolved from DI before the body runs, and ServicingRunService pulls in
        // an IChatClient. Taking it as a parameter made an unconfigured host
        // answer 500 from inside the container instead of the 503 below.
        runs.MapPost("/", async (
                StartRunRequest request,
                IServiceProvider services,
                ModelAvailability availability,
                CancellationToken cancellationToken) =>
            {
                if (availability.Unavailable() is { } problem)
                {
                    return problem;
                }

                if (string.IsNullOrWhiteSpace(request.LoanId))
                {
                    return Results.Problem(
                        title: "loanId is required.",
                        detail: "Known loans: "
                                + string.Join(", ", MockServicingSystem.LoanIds.OrderBy(id => id, StringComparer.Ordinal)),
                        statusCode: StatusCodes.Status400BadRequest);
                }

                if (!MockServicingSystem.TryGetLoanTerms(request.LoanId, out _))
                {
                    return Results.Problem(
                        title: "No such loan.",
                        detail: $"No loan '{request.LoanId}' in the servicing system.",
                        statusCode: StatusCodes.Status404NotFound);
                }

                var approver = string.IsNullOrWhiteSpace(request.Approver)
                    ? DefaultApprover
                    : request.Approver.Trim();

                var service = services.GetRequiredService<ServicingRunService>();
                var run = await service.StartAsync(request.LoanId, approver, cancellationToken);

                return Results.Created($"/servicing-runs/{run.RunId}", RunResponse.From(run));
            })
            .WithSummary("Starts a covenant review. Returns as soon as the agent finishes or asks for approval.");

        // The reads go straight to the store rather than through the service.
        // Looking at a run is a storage question with no model in it, and routing
        // it through a type that depends on an IChatClient would make listing runs
        // fail on a host that cannot call a model — which is exactly the host on
        // which you most want to see what happened.
        runs.MapGet("/", async (IRunStore store, CancellationToken cancellationToken) =>
                Results.Ok((await store.ListAsync(cancellationToken)).Select(RunResponse.From).ToList()))
            .WithSummary("Every run this instance is holding, most recently updated first.");

        runs.MapGet("/{runId}", async (
                string runId, IRunStore store, CancellationToken cancellationToken) =>
            {
                var run = await store.GetAsync(runId, cancellationToken);
                return run is null ? NotFoundRun(runId) : Results.Ok(RunResponse.From(run));
            })
            .WithSummary("The current state of one run, including what it is waiting for.");

        // ── The resume ───────────────────────────────────────────────────────
        //
        // This is the endpoint the whole stage exists for. Everything needed to
        // continue — the agent's conversation, the tool trace, the filings already
        // made, the approvals already granted — was written down when the previous
        // request returned, so this one can pick the run up without having held
        // anything open in between.
        runs.MapPost("/{runId}/approvals", async (
                string runId,
                SubmitApprovalsRequest request,
                IServiceProvider services,
                CancellationToken cancellationToken) =>
            {
                var decisions = (request.Decisions ?? [])
                    .Select(decision => new ApprovalDecisionInput(
                        decision.RequestId,
                        decision.Approved,
                        // Negative or absurd client-reported times are clamped
                        // rather than rejected. The field is an audit signal, not
                        // a control input, and refusing a filing because a clock
                        // was skewed would block real work over telemetry.
                        TimeSpan.FromSeconds(Math.Clamp(decision.TimeToDecisionSeconds, 0, 86_400))))
                    .ToList();

                try
                {
                    var service = services.GetRequiredService<ServicingRunService>();
                    var run = await service.SubmitApprovalsAsync(runId, decisions, cancellationToken);
                    return Results.Ok(RunResponse.From(run));
                }
                catch (RunNotFoundException)
                {
                    return NotFoundRun(runId);
                }
                catch (RunOperationException ex)
                {
                    // Answering a run that is not waiting, answering a request it
                    // is not holding, or answering only some of them. All caller
                    // errors, all worth naming precisely — an operator who gets
                    // "bad request" with no detail will resubmit, and resubmitting
                    // is the thing we are trying to make safe.
                    return Results.Problem(
                        title: "That submission does not match what the run is waiting for.",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status400BadRequest);
                }
            })
            .WithSummary("Answers every approval the run is waiting on and resumes it.");

        runs.MapGet("/{runId}/ledger", async (
                string runId, IRunStore store, CancellationToken cancellationToken) =>
            {
                var run = await store.GetAsync(runId, cancellationToken);

                // Deliberately its own endpoint as well as a field on the run. The
                // ledger is the audit record — what actually landed on the loan
                // file, as against the agent's account of itself — and it is the
                // thing a compliance reviewer wants to fetch without wading
                // through a tool trace.
                return run is null
                    ? NotFoundRun(runId)
                    : Results.Ok(run.Filed.Select(FiledExceptionResponse.From).ToList());
            })
            .WithSummary("What actually landed on the loan file, with who approved each filing and how long they took.");
    }

    private static IResult NotFoundRun(string runId) => Results.Problem(
        title: "No such run.",
        detail: $"No servicing run '{runId}'. Runs live in memory and do not survive a restart of this process.",
        statusCode: StatusCodes.Status404NotFound);
}
