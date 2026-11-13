using System.Text.Json;

namespace LoadTest.VSCode.Host;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            // Istanza isolata: non collide con l'Host eventualmente avviato dall'estensione.
            Environment.SetEnvironmentVariable("DEOLOADTEST_HOST_INSTANCE_ID", "program-debug");
            using var instanceLock = HostInstanceLock.Acquire();

            const string sourcePath = @"C:\temp\webtests\CalculateValue.csx";
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Script diagnostico non trovato.", sourcePath);

            var webTestDirectory = Directory.CreateTempSubdirectory("deoloadtest-host-debug-");
            var webTestPath = Path.Combine(webTestDirectory.FullName, "HostDebug.webtest.yaml");
            var yaml = $$"""
                version: 1
                name: Host debugger diagnostic
                steps:
                  - code: "{{sourcePath.Replace("\\", "/")}}"
                """;
            await File.WriteAllTextAsync(webTestPath, yaml);

            // Inietta nel server gli stessi messaggi JSON-RPC che invierebbe l'estensione.
            // Dopo i due messaggi, il reader resta in attesa: l'Host non termina al termine
            // del WebTest e possiamo ispezionare il processo con il debugger.
            var messages = new[]
            {
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new { }
                }),
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "webtest.run",
                    @params = new
                    {
                        webTestFile = webTestPath,
                        debugMode = true
                    }
                })
            };

            Console.Error.WriteLine($"Host diagnostic WebTest: {webTestPath}");
            Console.Error.WriteLine($"Host diagnostic script:  {sourcePath}");
            Console.Error.WriteLine("Il processo resterà vivo dopo la run. Arrestare con Stop Debugging.");

            var input = new PrefixTextReader(messages);
            var server = new StdioJsonRpcServer(input, Console.Out, Console.Error);
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

    private sealed class PrefixTextReader(IEnumerable<string> prefix) : TextReader
    {
        private readonly Queue<string> _lines = new(prefix);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lines.TryDequeue(out var line))
                return line;

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }
}
