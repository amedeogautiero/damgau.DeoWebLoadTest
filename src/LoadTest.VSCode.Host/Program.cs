namespace LoadTest.VSCode.Host;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            using var instanceLock = HostInstanceLock.Acquire();

            // The Host communicates with the VS Code extension over
            // newline-delimited JSON-RPC on the standard input/output streams.
            // stdout is reserved for protocol messages; diagnostics go to stderr.
            var server = new StdioJsonRpcServer(Console.In, Console.Out, Console.Error);
            await server.RunAsync();
            return 0;
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Another DeoLoadTest Host instance", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("DeoLoadTest Host: " + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LoadTest.VSCode.Host: " + ex);
            return 1;
        }
    }
}
