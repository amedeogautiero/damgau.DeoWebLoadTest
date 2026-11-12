namespace LoadTest.VSCode.Host;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            using var instanceLock = HostInstanceLock.Acquire();
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
