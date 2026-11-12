namespace LoadTest.VSCode.Host;

internal static class Program
{
    private const string ReadyMarker = "DEOLOADTEST_HOST_READY";

    private static async Task<int> Main()
    {
        try
        {
            var server = new StdioJsonRpcServer(Console.In, Console.Out, Console.Error);
            var serverTask = server.RunAsync();
            Console.Error.WriteLine(ReadyMarker);
            await serverTask;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LoadTest.VSCode.Host: " + ex);
            return 1;
        }
    }
}
