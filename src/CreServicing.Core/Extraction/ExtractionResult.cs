using CreServicing.Core.Cost;
using Microsoft.Agents.AI;

namespace CreServicing.Core.Extraction;

/// <summary>
/// What an extractor returns: the structured record, and what the call cost.
///
/// ── Why this exists rather than an ambient cost ledger ───────────────────────
///
/// The obvious cheaper move was a static accumulator in the shape of
/// <see cref="Data.ExceptionLedger"/> — extractors write usage into it, the demo
/// reads the total, no signature changes anywhere. It was rejected. This project
/// already carries two pieces of static mutable state that are documented as
/// shortcuts and are a data race the moment anything is hosted behind HTTP, and
/// adding a third to save a dozen call-site edits would be spending the exact
/// debt the hosting work is meant to pay down.
///
/// Cost is a property of a call, so it is returned by the call. The result is that
/// concurrent extraction — the S9 fan-out — needs no coordination to account
/// correctly, because nothing is shared.
/// </summary>
/// <param name="Value">
/// The extracted record, or null when the model returned nothing usable. Null is
/// not an error path to be swallowed: <see cref="FinancialSnapshotAssembler"/>
/// throws on it rather than assembling a snapshot with a hole in it.
/// </param>
/// <param name="Usage">
/// Tokens billed for this call. <see cref="ModelUsage.None"/> when the SDK
/// reported no usage, which is a reporting gap rather than a free call — the
/// display says so rather than showing a confident zero.
/// </param>
public sealed record ExtractionResult<T>(T? Value, ModelUsage Usage) where T : class;

/// <summary>
/// Maps the SDK's usage report onto <see cref="ModelUsage"/>. Kept here rather
/// than in <c>Cost/</c> so that folder stays free of any SDK reference — its
/// arithmetic is testable with no package dependency at all, which is what lets
/// the cost tests run in the free, credential-free CI job.
/// </summary>
internal static class UsageMapping
{
    public static ModelUsage ToModelUsage(this AgentResponse response)
        => response.Usage is { } usage
            ? new ModelUsage(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0)
            : ModelUsage.None;
}
