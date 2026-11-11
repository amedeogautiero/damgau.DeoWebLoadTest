using LoadTest.Cli;

// Ctrl+C: la prima volta chiede uno stop ordinato (Stop), la seconda un'interruzione immediata (Abort).
using var stop = new CancellationTokenSource();
using var abort = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (!stop.IsCancellationRequested) stop.Cancel();
    else abort.Cancel();
};

return await CliApp.RunAsync(args, new CliContext
{
    Out = Console.Out, Err = Console.Error, Stop = stop.Token, Abort = abort.Token
});
