using System.Collections.Concurrent;
using CreServicing.Core.Agents;

namespace CreServicing.Core.Runs;

/// <summary>
/// The run lifecycle: start one, look at it, answer what it is asking, list what
/// is outstanding. Everything a host needs, with no opinion about whether that
/// host is a terminal or a web API.
///
/// This sits between <see cref="ServicingRunner"/> (which knows how to advance an
/// agent) and <see cref="IRunStore"/> (which knows where state lives) because
/// neither of them should own the third concern: making sure one run is only
/// advanced by one caller at a time.
///
/// ── Why the lock is here and what it does not cover ──────────────────────────
///
/// Two POSTs of the same approval — an impatient operator, a retried request, a
/// double-clicked button — would otherwise both load the same suspended run and
/// both resume it, and the agent would execute the filing twice. That is the
/// worst available bug in this system: a duplicate covenant breach on a
/// borrower's file, authorised by one human decision.
///
/// The lock serialises them, and then the validation in
/// <see cref="ServicingRunner.ResumeAsync"/> finishes the job — the second caller
/// loads state in which those request ids are no longer outstanding and is
/// rejected rather than served. Order matters: the lock alone would only make the
/// double-file sequential.
///
/// What this does NOT cover is two instances, because a lock in one process means
/// nothing to another. The correct mechanism there is optimistic concurrency on
/// the stored record — an etag or version checked on write, so the loser of the
/// race is rejected by the store. That belongs in <see cref="IRunStore"/> when a
/// real store appears; putting it in now would be inventing an interface for a
/// database nobody has chosen.
/// </summary>
public sealed class ServicingRunService(ServicingRunner runner, IRunStore store)
{
    /// <summary>
    /// Bounded by the number of runs, and never cleaned up on purpose: removing a
    /// semaphore on completion opens the window where two callers hold two
    /// different semaphores for the same run, which is precisely the thing this
    /// exists to prevent. A process that accumulates enough runs for this to
    /// matter has already outgrown the in-memory store underneath it.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<ServicingRun> StartAsync(
        string loanId, string approver, CancellationToken cancellationToken = default)
    {
        var run = await runner.StartAsync(loanId, approver, cancellationToken);
        await store.SaveAsync(run, cancellationToken);
        return run;
    }

    public async Task<ServicingRun> SubmitApprovalsAsync(
        string runId,
        IReadOnlyList<ApprovalDecisionInput> decisions,
        CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(runId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var run = await store.GetAsync(runId, cancellationToken)
                      ?? throw new RunNotFoundException(runId);

            var resumed = await runner.ResumeAsync(run, decisions, cancellationToken);
            await store.SaveAsync(resumed, cancellationToken);
            return resumed;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<ServicingRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
        => store.GetAsync(runId, cancellationToken);

    public Task<IReadOnlyList<ServicingRun>> ListAsync(CancellationToken cancellationToken = default)
        => store.ListAsync(cancellationToken);
}

/// <summary>No run with that id. Callers map this to a 404.</summary>
public sealed class RunNotFoundException(string runId)
    : InvalidOperationException($"No servicing run '{runId}'.")
{
    public string RunId { get; } = runId;
}
