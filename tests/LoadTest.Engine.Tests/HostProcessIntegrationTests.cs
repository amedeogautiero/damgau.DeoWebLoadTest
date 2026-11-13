using System.Diagnostics;
using System.Reflection.Metadata;
using System.Text.Json;

namespace LoadTest.Engine.Tests;

public sealed class HostProcessIntegrationTests
{
    [Fact]
    public async Task Host_process_runs_calculate_value_webtest_in_debug_mode()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "CalculateValue.csx"));
        Assert.True(File.Exists(fixturePath), $"Fixture non trovata: {fixturePath}");
        Assert.False(string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(fixturePath)),
            "La fixture CalculateValue.csx deve contenere il codice reale.");

        var hostAssemblyPath = FindHostAssembly();
        var tempDirectory = Directory.CreateTempSubdirectory("deoloadtest-host-integration-");
        var webTestPath = Path.Combine(tempDirectory.FullName, "HostIntegration.webtest.yaml");
        var sourcePath = Path.Combine(tempDirectory.FullName, "CalculateValue.csx");
        File.Copy(fixturePath, sourcePath);

        await File.WriteAllTextAsync(webTestPath, """
            version: 1
            name: Host process integration
            steps:
              - code: CalculateValue.csx
            """);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(hostAssemblyPath)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(hostAssemblyPath);

        // Il test usa un lock isolato: può essere eseguito anche mentre l'Host
        // dell'estensione VS Code è attivo, senza alterare il lock predefinito.
        process.StartInfo.Environment["DEOLOADTEST_HOST_INSTANCE_ID"] =
            "integration-" + Guid.NewGuid().ToString("N");

        try
        {
            Assert.True(process.Start(), "Impossibile avviare LoadTest.VSCode.Host.");
            var stderrTask = process.StandardError.ReadToEndAsync();

            using (var ready = await ReadUntilAsync(
                process.StandardOutput,
                message => IsNotification(message, "host.ready"),
                timeout.Token))
            {
                Assert.Equal("LoadTest.VSCode.Host",
                    ready.RootElement.GetProperty("params").GetProperty("serverName").GetString());
            }

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { }
            });

            using (var initialized = await ReadUntilAsync(
                process.StandardOutput,
                message => HasId(message, 1),
                timeout.Token))
            {
                Assert.False(initialized.RootElement.TryGetProperty("error", out var error),
                    error.ValueKind == JsonValueKind.Undefined ? null : error.GetProperty("message").GetString());
                Assert.Equal("1.0",
                    initialized.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
            }

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "webtest.run",
                @params = new
                {
                    webTestFile = webTestPath,
                    debugMode = true
                }
            });

            using (var started = await ReadUntilAsync(
                process.StandardOutput,
                message => HasId(message, 2),
                timeout.Token))
            {
                Assert.False(started.RootElement.TryGetProperty("error", out var error),
                    error.ValueKind == JsonValueKind.Undefined ? null : error.GetProperty("message").GetString());
                Assert.False(string.IsNullOrWhiteSpace(
                    started.RootElement.GetProperty("result").GetProperty("runId").GetString()));
            }

            using (var completed = await ReadUntilAsync(
                process.StandardOutput,
                message => IsNotification(message, "webtest.completed") ||
                           IsNotification(message, "webtest.failed"),
                timeout.Token))
            {
                var method = completed.RootElement.GetProperty("method").GetString();
                Assert.Equal("webtest.completed", method);
                var result = completed.RootElement.GetProperty("params").GetProperty("result");
                Assert.True(result.GetProperty("success").GetBoolean(),
                    string.Join(" | ", result.GetProperty("failures").EnumerateArray()
                        .Select(failure => failure.GetString())));
            }

            Assert.True(HostPdbContainsSource(sourcePath),
                $"L'Host non ha prodotto nel PluginCache un PDB riferito al sorgente {sourcePath}.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(stopTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }

            tempDirectory.Delete(recursive: true);
        }
    }

    private static string FindHostAssembly()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var projectDirectory = Path.Combine(
                directory.FullName, "src", "LoadTest.VSCode.Host");
            var candidate = Path.Combine(
                projectDirectory, "bin", configuration, "net10.0", "LoadTest.VSCode.Host.dll");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "LoadTest.VSCode.Host.dll non trovato. Il riferimento al progetto Host deve essere compilato insieme ai test.");
    }

    private static async Task SendAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonDocument> ReadUntilAsync(
        StreamReader output,
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (await output.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var message = JsonDocument.Parse(line);
            if (predicate(message.RootElement))
                return message;

            message.Dispose();
        }

        throw new EndOfStreamException("Il processo Host ha chiuso stdout prima della risposta attesa.");
    }

    private static bool HasId(JsonElement message, int id) =>
        message.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number &&
        value.GetInt32() == id;

    private static bool IsNotification(JsonElement message, string method) =>
        message.TryGetProperty("method", out var value) &&
        string.Equals(value.GetString(), method, StringComparison.Ordinal);

    private static bool HostPdbContainsSource(string sourcePath)
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(), "DeoLoadTest", "VSCode", "PluginCache");
        if (!Directory.Exists(cacheRoot))
            return false;

        foreach (var pdbPath in Directory.EnumerateFiles(
                     cacheRoot, "CalculateValue.pdb", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.OpenRead(pdbPath);
                using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
                var reader = provider.GetMetadataReader();
                if (reader.Documents.Any(handle => string.Equals(
                        reader.GetString(reader.GetDocument(handle).Name),
                        sourcePath,
                        StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch (BadImageFormatException)
            {
                // Ignora eventuali file omonimi non Portable PDB.
            }
        }

        return false;
    }
}
