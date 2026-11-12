using System.Text.Json;
using System.Text.Json.Serialization;
using LoadTest.Engine.Http;
using LoadTest.Execution;
using LoadTest.Engine.Runtime;

namespace LoadTest.VSCode.Host;

/// <summary>
/// JSON-RPC 2.0 over newline-delimited JSON (one JSON message per stdin/stdout line).
/// stdout is reserved exclusively for protocol messages; diagnostics go to stderr.
/// </summary>
internal sealed class StdioJsonRpcServer(TextReader input, TextWriter output, TextWriter error)
{
    private sealed class ActiveRun(string runId)
    {
        public string RunId { get; } = runId;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed class StartRunParams
    {
        public StartRunParams() { }

        public string LoadTestFile { get; set; } = "";
        public string? ResultsDatabasePath { get; set; }
        public string? RunName { get; set; }
        public int? Seed { get; set; }
        public bool DebugMode { get; set; }
    }

    private sealed class CancelRunParams
    {
        public CancelRunParams() { }

        public string RunId { get; set; } = "";
    }

    private sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly object _writeGate = new();
    private readonly object _runGate = new();
    private ActiveRun? _activeRun;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (await input.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                WriteError(null, -32700, "Parse error");
                continue;
            }

            using (document)
                await HandleMessageAsync(document.RootElement);
        }

        ActiveRun? active;
        lock (_runGate)
        {
            active = _activeRun;
            active?.Cancellation.Cancel();
        }

        if (active is not null)
            await active.Task;
    }

    private Task HandleMessageAsync(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("jsonrpc", out var version) ||
            version.ValueKind != JsonValueKind.String ||
            version.GetString() != "2.0" ||
            !message.TryGetProperty("method", out var methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
        {
            if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("id", out var invalidId))
                WriteError(invalidId.Clone(), -32600, "Invalid Request");
            return Task.CompletedTask;
        }

        // The host currently exposes request/response methods only. Notifications from
        // the client are ignored because every state-changing method needs an acknowledgement.
        if (!message.TryGetProperty("id", out var idElement))
            return Task.CompletedTask;

        var id = idElement.Clone();
        var method = methodElement.GetString();

        try
        {
            var parameters = message.TryGetProperty("params", out var paramsElement)
                ? paramsElement
                : default;

            switch (method)
            {
                case "initialize":
                    WriteResult(id, new
                    {
                        protocolVersion = "1.0",
                        serverName = "LoadTest.VSCode.Host",
                        capabilities = new
                        {
                            localLoadTest = true,
                            progressNotifications = true,
                            executionEvents = true,
                            cancellation = true,
                            concurrentRuns = false
                        }
                    });
                    break;

                case "run.start":
                {
                    var request = parameters.ValueKind == JsonValueKind.Undefined
                        ? new StartRunParams()
                        : JsonSerializer.Deserialize<StartRunParams>(parameters.GetRawText(), JsonOptions)
                            ?? throw new RpcException(-32602, "Invalid params");

                    if (string.IsNullOrWhiteSpace(request.LoadTestFile))
                        throw new RpcException(-32602, "loadTestFile is required");

                    var active = ReserveRun();
                    WriteResult(id, new { runId = active.RunId });
                    LaunchRun(active, request);
                    break;
                }

                case "run.cancel":
                {
                    var request = parameters.ValueKind == JsonValueKind.Undefined
                        ? new CancelRunParams()
                        : JsonSerializer.Deserialize<CancelRunParams>(parameters.GetRawText(), JsonOptions)
                            ?? throw new RpcException(-32602, "Invalid params");

                    if (string.IsNullOrWhiteSpace(request.RunId))
                        throw new RpcException(-32602, "runId is required");

                    CancelRun(request.RunId);
                    WriteResult(id, new { accepted = true, runId = request.RunId });
                    break;
                }

                default:
                    WriteError(id, -32601, $"Method not found: {method}");
                    break;
            }
        }
        catch (RpcException ex)
        {
            WriteError(id, ex.Code, ex.Message);
        }
        catch (JsonException ex)
        {
            WriteError(id, -32602, "Invalid params: " + ex.Message);
        }
        catch (Exception ex)
        {
            WriteError(id, -32603, ex.Message);
        }

        return Task.CompletedTask;
    }

    private ActiveRun ReserveRun()
    {
        lock (_runGate)
        {
            if (_activeRun is not null)
                throw new RpcException(-32001, "A local load test is already running.");

            var active = new ActiveRun(Guid.NewGuid().ToString("N"));
            _activeRun = active;
            return active;
        }
    }

    private void LaunchRun(ActiveRun active, StartRunParams request)
    {
        active.Task = Task.Run(() => ExecuteRunAsync(active, request));
    }

    private void CancelRun(string runId)
    {
        lock (_runGate)
        {
            if (_activeRun is null || !string.Equals(_activeRun.RunId, runId, StringComparison.Ordinal))
                throw new RpcException(-32002, $"No active run found with id '{runId}'.");

            _activeRun.Cancellation.Cancel();
        }
    }

    private async Task ExecuteRunAsync(ActiveRun active, StartRunParams request)
    {
        try
        {
            var result = await LocalLoadTestExecutor.RunAsync(new LocalLoadTestOptions
            {
                RunId = active.RunId,
                LoadTestFile = request.LoadTestFile,
                ResultsDatabasePath = request.ResultsDatabasePath ?? "loadtest-results.db",
                RunName = request.RunName,
                Seed = request.Seed,
                HttpFactory = () => new HttpClientExecutor(),
                OnProgress = snapshot => WriteNotification("run.progress", new
                {
                    runId = active.RunId,
                    snapshot
                }),
                OnExecutionEvent = request.DebugMode ? executionEvent =>
                {
                    var notification = executionEvent.EventType switch
                    {
                        "RequestStarted" => "debug.requestStarted",
                        "RequestCompleted" => "debug.requestCompleted",
                        "RequestFailed" => "debug.requestFailed",
                        "VariablesUpdated" => "debug.variablesUpdated",
                        "TestCompleted" => "debug.testCompleted",
                        _ => null
                    };
                    if (notification is not null)
                        WriteNotification(notification, new { runId = active.RunId, @event = executionEvent });
                } : null,
                OnObserverError = ex => error.WriteLine("observer warning: " + ex.Message)
            }, active.Cancellation.Token);

            WriteNotification("run.completed", new
            {
                runId = active.RunId,
                result = new
                {
                    run = result.Run,
                    summary = result.Summary,
                    csv = result.Csv
                }
            });
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
            WriteNotification("run.cancelled", new { runId = active.RunId });
        }
        catch (Exception ex)
        {
            WriteNotification("run.failed", new
            {
                runId = active.RunId,
                error = new
                {
                    code = "execution_failed",
                    message = ex.Message,
                    details = ex.InnerException?.Message
                }
            });
            error.WriteLine($"run {active.RunId} failed: {ex}");
        }
        finally
        {
            lock (_runGate)
            {
                if (ReferenceEquals(_activeRun, active))
                    _activeRun = null;
            }

            active.Cancellation.Dispose();
        }
    }

    private void WriteResult(JsonElement id, object result) =>
        WriteMessage(new { jsonrpc = "2.0", id, result });

    private void WriteError(JsonElement? id, int code, string message) =>
        WriteMessage(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message }
        });

    private void WriteNotification(string method, object parameters) =>
        WriteMessage(new { jsonrpc = "2.0", method, @params = parameters });

    private void WriteMessage(object message)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        lock (_writeGate)
        {
            output.WriteLine(json);
            output.Flush();
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
