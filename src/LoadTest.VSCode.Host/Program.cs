namespace LoadTest.VSCode.Host;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            var server = new StdioJsonRpcServer(Console.In, Console.Out, Console.Error);
            await server.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LoadTest.VSCode.Host: " + ex);
            return 1;
        }
    }
}
