namespace CreServicing.Cli;

/// <summary>
/// Turns Ctrl+C into a <see cref="CancellationToken"/> instead of an abrupt exit.
///
/// Worth more here than in most console apps. A servicing run can be halfway
/// through filing approved exceptions when the operator loses patience, and the
/// default Ctrl+C behaviour — terminate the process — leaves the ledger holding
/// some filings and the borrower's file holding others, with nothing recording
/// that the run was abandoned rather than completed. Cancelling instead lets the
/// in-flight model call unwind and the run report what it managed to do.
///
/// The second Ctrl+C is deliberately left alone: if the first one did not get
/// the operator out, they should not have to fight the program.
/// </summary>
public sealed class ConsoleLifetime : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public ConsoleLifetime() => Console.CancelKeyPress += OnCancelKeyPress;

    public CancellationToken Token => _cts.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (_cts.IsCancellationRequested)
        {
            // Already asked once. Let the runtime kill it.
            return;
        }

        // Cancel the run rather than the process.
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine("Cancelling — finishing the call in flight. Ctrl+C again to abandon.");
        _cts.Cancel();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        _cts.Dispose();
    }
}
