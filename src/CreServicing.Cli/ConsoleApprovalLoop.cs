using CreServicing.Core.Cost;
using CreServicing.Core.Data;
using CreServicing.Core.Runs;
using CreServicing.Core.Tools;

namespace CreServicing.Cli;

/// <summary>
/// The terminal's half of the approval loop.
///
/// Everything in here is presentation. The run itself — which tools exist, which
/// are gated, when to pause, what a decision authorises — belongs to
/// <see cref="Core.Agents.ServicingRunner"/>, and this file has no opinion about
/// any of it. That split is the whole of stage C: the same run state renders as a
/// console prompt here and as JSON in the API, and neither host can drift from the
/// other on a safety property because neither host owns one.
///
/// What the terminal still owns is the thing only a terminal has: a person sitting
/// in front of it. <see cref="Console.ReadLine"/> is still a blocking read — that
/// was never the bug. The bug was that the blocking read was holding the run.
///
/// Three outputs, printed in this order and worth reading in it: the tool trace
/// (what the agent did), the answer (what it says it did), and the ledger (what
/// actually landed on the loan file). The third is the only one that is evidence.
///
/// The trace is also printed incrementally, at each pause, ahead of the question.
/// A gate that shows the operator only the arguments of the write is asking them
/// to authorise the agent's reading of documents they have not seen.
/// </summary>
public static class ConsoleApprovalLoop
{
    /// <summary>Who is sitting at the console approving filings. Recorded on every exception.</summary>
    private const string Approver = "console-operator";

    public static async Task RunAsync(
        ServicingRunService service,
        string loanId,
        string deployment,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("SERVICING AGENT — COVENANT REVIEW");
        Console.WriteLine($"Loan       {loanId}");
        Console.WriteLine($"Deployment {deployment}");
        Console.WriteLine("Tools      GetLoanTerms, ListPackageDocuments, GetDocumentText, EvaluateCovenants");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine();

        Console.WriteLine($"TASK  Review the quarterly reporting package for loan {loanId}, report whether "
                          + "it is in covenant compliance, and file a servicing exception for each finding.");
        Console.WriteLine();

        var run = await service.StartAsync(loanId, Approver, cancellationToken);

        Console.WriteLine($"RUN   {run.RunId}");
        Console.WriteLine();

        // `shown` tracks how much of the trace the operator has already been
        // walked through, so each pause shows what the agent did since the last
        // one rather than reprinting the whole run.
        var shown = 0;

        // A loop, not a single check. The agent files one exception per finding,
        // so a package with three breaches pauses three times — and the framework
        // may batch or stagger those requests. The instructor's example handles
        // the first request in one round, which is enough to demonstrate the API
        // and not enough to run this workflow.
        while (run.Status == ServicingRunStatus.AwaitingApproval)
        {
            // Before the question, the context for it. An operator asked to
            // authorise a filing on the strength of its five arguments alone is
            // being asked to trust the agent's reading of documents they have not
            // been shown — which is ceremony, not control.
            shown = PrintCallsSince(run.Trace, shown, $"WHAT THE AGENT DID BEFORE ASKING (round {run.Round})");

            foreach (var swept in run.Suspended.Where(pending => !pending.RequiresHumanDecision))
            {
                Console.WriteLine(
                    $"  auto-approved  {swept.ToolName} — a read, swept into this round by tool-call batching.");
            }

            var decisions = run.AwaitingHuman
                .Select(pending => Ask(pending, run.Round))
                .ToList();

            run = await service.SubmitApprovalsAsync(run.RunId, decisions, cancellationToken);
        }

        PrintCallsSince(run.Trace, 0, $"TOOL TRACE — {run.Trace.Count} call(s)");

        if (run.Error is not null)
        {
            Console.WriteLine("RUN DID NOT COMPLETE");
            Console.WriteLine(new string('-', 78));
            Console.WriteLine($"  {run.Error}");
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("ANSWER");
            Console.WriteLine(new string('-', 78));
            Console.WriteLine(run.Answer);
            Console.WriteLine();
        }

        PrintLedger(run.Filed);

        CostReport.PrintAgentRun(run.Usage, run.ModelCalls, run.Trace.Count, run.Deployment);
    }

    /// <summary>
    /// The pause. Everything the human needs in order to say no is on screen —
    /// the tool, every argument, and nothing else.
    ///
    /// Worth being honest about what this is: the last point in the pipeline where
    /// a wrong number can be stopped, and it is a person reading a console at the
    /// end of a long run. Show the arguments in full, never truncated. A summary
    /// the operator has to trust is not a control.
    /// </summary>
    private static ApprovalDecisionInput Ask(SuspendedApproval pending, int round)
    {
        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"APPROVAL REQUIRED  (round {round})");
        Console.WriteLine($"  Tool  {pending.ToolName}");

        if (pending.Arguments.Count > 0)
        {
            foreach (var argument in pending.Arguments)
            {
                Console.WriteLine($"  {argument.Name,-10}  {argument.Value}");
            }
        }
        else
        {
            Console.WriteLine("  (no arguments)");
        }

        Console.WriteLine();
        Console.Write($"  Approve this {Describe(pending.ToolName)}? [y/N]: ");

        // Timed from the moment the question is on screen. Recorded on the ledger
        // entry, because "approved by console-operator" and "approved by
        // console-operator in 400ms" are not the same audit record, and approval
        // fatigue is the failure mode of every HITL system ever built.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var input = Console.ReadLine();
        var timeToDecision = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        var approved = string.Equals(input?.Trim(), "y", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine(approved
            ? $"  APPROVED — filing. (decided in {timeToDecision.TotalSeconds:F1}s)"
            : "  REJECTED — nothing written.");
        Console.WriteLine();

        return new ApprovalDecisionInput(pending.RequestId, approved, timeToDecision);
    }

    /// <summary>
    /// Wording derived from the tool rather than hardcoded, so gating a sixth tool
    /// later cannot silently produce a prompt that describes the wrong action.
    /// </summary>
    private static string Describe(string toolName)
        => toolName == nameof(ServicingTools.CreateServicingException)
            ? "filing"
            : $"call to {toolName}";

    /// <summary>
    /// What actually landed on the loan file, printed from the ledger rather than
    /// from the agent's summary of itself.
    ///
    /// The distinction is the point. The agent's closing paragraph is a claim
    /// about what it did; this is the record. When they disagree, the record wins,
    /// and noticing the disagreement is the reason to print both.
    /// </summary>
    private static void PrintLedger(IReadOnlyList<FiledException> filed)
    {
        Console.WriteLine("EXCEPTION LEDGER — what was actually written");
        Console.WriteLine(new string('-', 78));

        if (filed.Count == 0)
        {
            Console.WriteLine("  Nothing filed.");
            Console.WriteLine();
            return;
        }

        foreach (var entry in filed)
        {
            Console.WriteLine($"  {entry.ReferenceNumber}  [{entry.Exception.Severity.ToString().ToUpperInvariant()}] "
                              + $"{entry.Exception.Code}  {entry.Exception.LoanId}");
            Console.WriteLine($"    {entry.Exception.Summary}");
            Console.WriteLine($"    Evidence:    {entry.Exception.Evidence}");

            var decision = entry.TimeToDecision is { } elapsed
                ? $" (decided in {elapsed.TotalSeconds:F1}s)"
                : string.Empty;

            Console.WriteLine($"    Approved by: {entry.ApprovedBy}{decision} at {entry.FiledAt:yyyy-MM-dd HH:mm:ss}Z");

            if (!entry.IsAttributed)
            {
                // Should be unreachable while the gate holds. Printed rather than
                // asserted because the ledger is the record of what happened, and
                // a write that arrived without an approval is exactly the thing
                // the record exists to make visible.
                Console.WriteLine("    ** no approval was recorded against this filing **");
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// Prints the calls from <paramref name="from"/> onward under
    /// <paramref name="heading"/>, and returns the new watermark.
    ///
    /// Used twice, for two different jobs: incrementally before each approval
    /// prompt, so the operator sees the reads a filing rests on, and once from
    /// zero at the end as the record of the whole run.
    /// </summary>
    private static int PrintCallsSince(IReadOnlyList<RecordedCall> calls, int from, string heading)
    {
        Console.WriteLine(heading);
        Console.WriteLine(new string('-', 78));

        if (calls.Count == 0)
        {
            Console.WriteLine("  The agent answered without calling a tool. That is a finding in");
            Console.WriteLine("  itself — it means the answer came from the model's priors, not");
            Console.WriteLine("  from the servicing system.");
            Console.WriteLine();
            return 0;
        }

        if (from >= calls.Count)
        {
            Console.WriteLine("  (nothing new since the last prompt)");
            Console.WriteLine();
            return from;
        }

        for (var index = from; index < calls.Count; index++)
        {
            var call = calls[index];
            var arguments = call.Arguments.Count == 0
                ? "(none)"
                : string.Join(", ", call.Arguments.Select(pair => $"{pair.Name}={Abbreviate(pair.Value)}"));

            Console.WriteLine($"  {index + 1,2}. {call.ToolName}({arguments})");
        }

        Console.WriteLine();
        return calls.Count;
    }

    private static string Abbreviate(string value)
        => value.Length <= 60 ? value : value[..57] + "...";
}
