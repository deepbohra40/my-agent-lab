using System.Collections.Concurrent;
using System.Text.Json;

namespace CreServicing.Core.Runs;

/// <summary>
/// Where suspended runs live between the request that suspends one and the request
/// that resumes it.
///
/// An interface with one in-memory implementation is usually over-abstraction. It
/// earns its place here because this is the exact seam a real deployment has to
/// replace — a suspended run outliving a process restart or being resumed on a
/// different instance is a storage question, not an agent question, and separating
/// them means the approval loop above never has to know which it is talking to.
///
/// The honest limitation of the implementation below is named on it.
/// </summary>
public interface IRunStore
{
    Task<ServicingRun?> GetAsync(string runId, CancellationToken cancellationToken = default);

    Task SaveAsync(ServicingRun run, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Runs currently suspended, most recently updated first.</summary>
    Task<IReadOnlyList<ServicingRun>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The in-process store. Correct for one instance; wrong for two.
///
/// ── The deliberate part ──────────────────────────────────────────────────────
///
/// It serializes on save and deserializes on load rather than handing back the
/// object it was given. That is slower and it is the entire point: it makes the
/// store behave like a real one, so the "can this run actually be persisted?"
/// question is answered by every test that touches it instead of by a code review.
/// A <see cref="ServicingRun"/> that quietly held a live <c>AgentSession</c> would
/// pass every test against a store that returned the same reference and fail the
/// first time it met Redis. Here it fails immediately.
///
/// It also means callers get their own copy, so mutating a run they fetched cannot
/// corrupt what another request is reading.
///
/// What it does not solve: durability across restart, and two instances. Both are
/// the implementation's problem, not the caller's, which is what the interface is
/// for.
/// </summary>
public sealed class InMemoryRunStore : IRunStore
{
    private readonly ConcurrentDictionary<string, string> _runs = new(StringComparer.Ordinal);

    public Task<ServicingRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
        => Task.FromResult(_runs.TryGetValue(runId, out var json) ? Deserialize(json) : null);

    public Task SaveAsync(ServicingRun run, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(run.RunId);
        _runs[run.RunId] = JsonSerializer.Serialize(run, RunSerialization.Options);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default)
        => Task.FromResult(_runs.TryRemove(runId, out _));

    public Task<IReadOnlyList<ServicingRun>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ServicingRun> all = _runs.Values
            .Select(Deserialize)
            .OfType<ServicingRun>()
            .OrderByDescending(run => run.UpdatedAt)
            .ToList();

        return Task.FromResult(all);
    }

    private static ServicingRun? Deserialize(string json)
        => JsonSerializer.Deserialize<ServicingRun>(json, RunSerialization.Options);
}

/// <summary>
/// One JSON configuration for run state, shared by the store and the runner.
///
/// It has to be the framework's options rather than a fresh
/// <c>JsonSerializerOptions</c>: <see cref="ServicingRun.SessionState"/> and
/// <see cref="SuspendedApproval.Request"/> hold content the framework serialized,
/// including the polymorphic <c>$type</c> discriminators that turn a blob back
/// into a <c>ToolApprovalRequestContent</c>. Deserializing those with default
/// options loses the type and the run cannot be resumed.
/// </summary>
public static class RunSerialization
{
    public static JsonSerializerOptions Options { get; } =
        new(Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions)
        {
            PropertyNameCaseInsensitive = true
        };

    /// <summary>
    /// A JSON <c>null</c>, used as the initial value of
    /// <see cref="ServicingRun.SessionState"/>.
    ///
    /// Not decoration. The default <see cref="JsonElement"/> is
    /// <see cref="JsonValueKind.Undefined"/>, and System.Text.Json throws when
    /// asked to write one — so a run that failed before its first turn produced a
    /// session could not be saved at all. The visible symptom would be a 500 from
    /// the endpoint that starts a run, on exactly the path where something has
    /// already gone wrong and the record matters most: bad credentials, an
    /// unreachable endpoint, a model that rejected the request.
    ///
    /// Starting from a real JSON null means an empty session is representable, and
    /// the "was there a session?" question is answered by
    /// <see cref="JsonValueKind"/> rather than by whether serialization exploded.
    /// </summary>
    public static JsonElement NullElement { get; } = JsonSerializer.SerializeToElement<object?>(null);

    /// <summary>True when a run carries a session that can actually be resumed.</summary>
    public static bool HasSession(JsonElement state)
        => state.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
}
