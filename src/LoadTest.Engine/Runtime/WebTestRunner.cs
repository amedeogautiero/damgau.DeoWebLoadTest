using System.Diagnostics;
using System.Globalization;
using LoadTest.Engine.Model;
using LoadTest.Sdk;

namespace LoadTest.Engine.Runtime;

/// <summary>
/// Esegue un web test per un utente virtuale. Ordine degli hook di richiesta (PROPOSTA, il contratto non lo specifica):
/// PreRequestDataBinding -> sostituzione variabili -> PreRequest -> invio -> PostRequest -> validazioni -> estrazioni -> PostRequestDataBinding.
/// Il test si ferma alla prima richiesta fallita.
/// </summary>
public sealed class WebTestRunner(EngineServices services)
{
    private sealed class Frame
    {
        public required WebTestDefinition Def { get; init; }
        public required WebTestContext Ctx { get; init; }
        public required List<string> Failures { get; init; }
        public string? Transaction { get; set; }
        public int Depth { get; init; }
        public string BaseDir => Path.GetDirectoryName(Def.SourcePath) ?? Directory.GetCurrentDirectory();
    }

    public async Task<WebTestResult> RunAsync(WebTestDefinition def, WebTestContext ctx, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();
        var frame = new Frame { Def = def, Ctx = ctx, Failures = failures };
        foreach (var kv in def.Context) ctx.Parameters.TryAdd(kv.Key, kv.Value);   // i valori già presenti (es. da `call`) hanno la precedenza

        var ok = false;
        try
        {
            foreach (var p in services.WebTestPlugins) p.PreWebTest(ctx);
            EmitContextSnapshot(ctx);
            ok = await ExecuteStepsAsync(def.Steps, frame, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            failures.Add($"Eccezione: {ex.Message}");
        }
        finally
        {
            try { foreach (var p in services.WebTestPlugins) p.PostWebTest(ctx); }
            catch (Exception ex) { failures.Add($"Eccezione in PostWebTest: {ex.Message}"); ok = false; }
        }

        sw.Stop();
        ok = ok && failures.Count == 0;
        var elapsedMicros = Micros(sw);
        services.Sink.TestCompleted(new TestMeasurement(ctx.User.ScenarioName, ctx.TestName, elapsedMicros, ok));
        Emit(new ExecutionEvent
        {
            EventType = "TestCompleted",
            ScenarioName = ctx.User.ScenarioName,
            TestName = ctx.TestName,
            UserId = ctx.User.UserId,
            Iteration = ctx.Iteration,
            ElapsedMicros = elapsedMicros,
            Success = ok,
            Failures = failures.ToArray()
        });
        return new WebTestResult(ok, failures, sw.Elapsed);
    }

    private async Task<bool> ExecuteStepsAsync(IReadOnlyList<StepDef> steps, Frame f, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            bool ok;
            switch (step)
            {
                case RequestStep r: ok = await ExecuteRequestAsync(r, f, ct); break;
                case TransactionStep t: ok = await ExecuteTransactionAsync(t, f, ct); break;
                case IfStep i:
                    ok = await ExecuteStepsAsync(ConditionEvaluator.Evaluate(i.Condition, f.Ctx) ? i.Then : i.Else, f, ct);
                    break;
                case LoopStep l: ok = await ExecuteLoopAsync(l, f, ct); break;
                case CallStep c: ok = await ExecuteCallAsync(c, f, ct); break;
                case CodeStep code: ok = await ExecuteCodeAsync(code, f, ct); break;
                default: throw new NotSupportedException($"Passo non supportato: {step.GetType().Name}");
            }
            EmitContextSnapshot(f.Ctx);
            if (!ok) return false;
        }
        return true;
    }

    private void EmitContextSnapshot(WebTestContext ctx)
    {
        Emit(new ExecutionEvent
        {
            EventType = "ContextSnapshot",
            ScenarioName = ctx.User.ScenarioName,
            TestName = ctx.TestName,
            UserId = ctx.User.UserId,
            Iteration = ctx.Iteration,
            ContextSnapshot = CaptureContextSnapshot(ctx)
        });
    }

    private static object CaptureContextSnapshot(WebTestContext ctx) => new
    {
        variables = new Dictionary<string, string>(ctx.Parameters, StringComparer.Ordinal),
        dataRows = ctx.DataRows.ToDictionary(
            row => row.Key,
            row => row.Value.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal),
            StringComparer.Ordinal),
        predefined = new
        {
            UserId = ctx.User.UserId,
            TestName = ctx.TestName,
            ScenarioName = ctx.User.ScenarioName,
            WebTestIteration = ctx.Iteration,
            LoadTestUserContext = ctx.User.UserContextId
        },
        user = new
        {
            userId = ctx.User.UserId,
            userContextId = ctx.User.UserContextId,
            agentId = ctx.User.AgentId,
            agentName = ctx.User.AgentName,
            controllerName = ctx.User.ControllerName,
            scenarioName = ctx.User.ScenarioName,
            isNewUser = ctx.User.IsNewUser
        }
    };

    private async Task<bool> ExecuteTransactionAsync(TransactionStep t, Frame f, CancellationToken ct)
    {
        var previous = f.Transaction;
        f.Transaction = t.Name;
        foreach (var p in services.WebTestPlugins) p.PreTransaction(f.Ctx, t.Name);
        var sw = Stopwatch.StartNew();   // tempo di parete: include i think time dei passi interni (PROPOSTA)
        var ok = false;
        try { ok = await ExecuteStepsAsync(t.Steps, f, ct); }
        finally
        {
            sw.Stop();
            f.Transaction = previous;
            foreach (var p in services.WebTestPlugins) p.PostTransaction(f.Ctx, t.Name);
            services.Sink.TransactionCompleted(new TransactionMeasurement(f.Ctx.User.ScenarioName, f.Ctx.TestName, t.Name, Micros(sw), ok));
        }
        return ok;
    }

    private async Task<bool> ExecuteLoopAsync(LoopStep l, Frame f, CancellationToken ct)
    {
        var text = VariableResolver.Resolve(l.For, f.Ctx);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
        {
            f.Failures.Add($"loop: valore 'for' non valido: '{text}'");
            return false;
        }
        for (var i = 0; i < count; i++)
            if (!await ExecuteStepsAsync(l.Steps, f, ct)) return false;
        return true;
    }

    private async Task<bool> ExecuteCallAsync(CallStep c, Frame f, CancellationToken ct)
    {
        if (f.Depth >= services.MaxCallDepth)
        {
            f.Failures.Add($"call: profondità massima ({services.MaxCallDepth}) superata su '{c.Path}'");
            return false;
        }
        var path = VariableResolver.Resolve(c.Path, f.Ctx);
        var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(f.BaseDir, path));
        var child = services.LoadTest(full);

        // Semantica scelta (PROPOSTA): il test chiamato riceve una COPIA dei parametri del chiamante più i `params`;
        // le modifiche non tornano indietro. Cookie e sessione HTTP restano condivisi (stesso executor).
        var childCtx = new WebTestContext { TestName = f.Ctx.TestName, User = f.Ctx.User, Iteration = f.Ctx.Iteration, CancellationToken = ct };
        foreach (var kv in f.Ctx.Parameters) childCtx.Parameters[kv.Key] = kv.Value;
        foreach (var kv in f.Ctx.DataRows) childCtx.DataRows[kv.Key] = kv.Value;
        foreach (var kv in c.Params) childCtx.Parameters[kv.Key] = VariableResolver.Resolve(kv.Value, f.Ctx);
        foreach (var kv in child.Context) childCtx.Parameters.TryAdd(kv.Key, kv.Value);

        var childFrame = new Frame { Def = child, Ctx = childCtx, Failures = f.Failures, Transaction = f.Transaction, Depth = f.Depth + 1 };
        return await ExecuteStepsAsync(child.Steps, childFrame, ct);
    }

    private async Task<bool> ExecuteCodeAsync(CodeStep c, Frame f, CancellationToken ct)
    {
        if (services.CodeSteps is null)
        {
            f.Failures.Add($"code: nessun host per i passi in codice configurato ('{c.Path}')");
            return false;
        }
        var path = VariableResolver.Resolve(c.Path, f.Ctx);
        var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(f.BaseDir, path));
        EmitStepEvent("StepStarted", "Code", path, "Started", f, new { path });
        try
        {
            await services.CodeSteps.RunAsync(full, f.Ctx, ct);
            EmitStepEvent("StepCompleted", "Code", path, "Succeeded", f, new { path });
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            EmitStepEvent("StepCompleted", "Code", path, "Failed", f, new { path, error = ex.Message });
            throw;
        }
    }

    private async Task<bool> ExecuteRequestAsync(RequestStep step, Frame f, CancellationToken ct)
    {
        var d = f.Def.Defaults;
        var req = new RequestContext
        {
            WebTest = f.Ctx, StepName = step.Name, Method = step.Method, Url = step.Url,
            TimeoutSec = d.TimeoutSec, FollowRedirects = d.FollowRedirects, HttpVersion = d.HttpVersion, Encoding = d.Encoding,
            Body = ToBody(step.Body)
        };
        foreach (var h in d.Headers) req.Headers[h.Key] = h.Value;
        foreach (var h in step.Headers) req.Headers[h.Key] = h.Value;

        ResponseContext? resp = null;
        string? errorType = null, errorMessage = null, exceptionDetails = null;
        long requestElapsedMicros = 0;
        var failures = new List<string>();

        try
        {
            foreach (var p in services.RequestPlugins) p.PreRequestDataBinding(req);
            Bind(req, f);
            foreach (var p in services.RequestPlugins) p.PreRequest(req);

            Emit(new ExecutionEvent
            {
                EventType = "RequestStarted",
                StepType = "Request",
                StepName = step.Name,
                StepStatus = "Started",
                ScenarioName = f.Ctx.User.ScenarioName,
                TestName = f.Ctx.TestName,
                UserId = f.Ctx.User.UserId,
                Iteration = f.Ctx.Iteration,
                RequestName = step.Name,
                Method = req.Method,
                Url = req.Url,
                RequestHeaders = new Dictionary<string, string>(req.Headers, StringComparer.OrdinalIgnoreCase),
                RequestBody = req.Body,
                ContextSnapshot = CaptureContextSnapshot(f.Ctx)
            });

            var requestStopwatch = Stopwatch.StartNew();
            try { resp = await services.Http.SendAsync(req, ct); }
            catch (TimeoutException ex) { errorType = "Timeout"; errorMessage = ex.Message; exceptionDetails = FormatExceptionChain(ex); }
            catch (HttpRequestException ex) { errorType = "Http"; errorMessage = ex.Message; exceptionDetails = FormatExceptionChain(ex); }
            finally { requestStopwatch.Stop(); requestElapsedMicros = Micros(requestStopwatch); }

            if (resp is not null)
            {
                foreach (var p in services.RequestPlugins) p.PostRequest(req, resp);
                CheckStatus(step, resp, failures);
                RunValidations(step, req, resp, f, failures);
                if (failures.Count == 0) RunExtractions(step, req, resp, f, failures);
                foreach (var p in services.RequestPlugins) p.PostRequestDataBinding(req, resp);
                if (failures.Count > 0) { errorType = "Validation"; errorMessage = string.Join("; ", failures); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            errorType = ex is UnresolvedVariableException ? "Binding" : "Exception";
            errorMessage = ex.Message;
            exceptionDetails = FormatExceptionChain(ex);
        }

        var success = errorType is null;
        services.Sink.RequestCompleted(new RequestMeasurement(
            f.Ctx.User.ScenarioName, f.Ctx.TestName, step.Name, req.Url, f.Transaction,
            resp?.ElapsedMicros ?? 0, resp?.BytesSent ?? 0, resp?.Body.Length ?? 0, resp?.StatusCode ?? 0,
            success, errorType, errorMessage));

        Emit(new ExecutionEvent
        {
            EventType = success ? "RequestCompleted" : "RequestFailed",
            StepType = "Request",
            StepName = step.Name,
            StepStatus = success ? "Succeeded" : "Failed",
            ScenarioName = f.Ctx.User.ScenarioName,
            TestName = f.Ctx.TestName,
            UserId = f.Ctx.User.UserId,
            Iteration = f.Ctx.Iteration,
            RequestName = step.Name,
            Method = req.Method,
            Url = req.Url,
            RequestHeaders = new Dictionary<string, string>(req.Headers, StringComparer.OrdinalIgnoreCase),
            RequestBody = req.Body,
            StatusCode = resp?.StatusCode,
            ReasonPhrase = resp?.ReasonPhrase,
            ResponseHeaders = resp?.Headers,
            ResponseBody = resp?.BodyText,
            ElapsedMicros = resp?.ElapsedMicros ?? requestElapsedMicros,
            ErrorType = errorType,
            ErrorMessage = errorMessage,
            ExceptionDetails = exceptionDetails,
            ContextSnapshot = CaptureContextSnapshot(f.Ctx),
            RequestDefinition = new
            {
                method = step.Method,
                url = step.Url,
                headers = step.Headers,
                body = step.Body
            },
            Success = success
        });

        if (!success)
        {
            f.Failures.Add($"[{step.Name}] {errorType}: {errorMessage}");
            return false;
        }

        if (step.ThinkTimeMs is { } think) await services.ThinkTime.DelayAsync(think, f.Ctx, ct);
        return true;
    }

    // Richiesta riuscita se lo status atteso compare in uno degli hop (redirect inclusi);
    // senza expectedStatus l'ultimo status deve essere < 400. (PROPOSTA: il contratto non specifica)
    private static void CheckStatus(RequestStep step, ResponseContext resp, List<string> failures)
    {
        if (step.ExpectedStatus is int expected)
        {
            if (!resp.StatusHistory.Contains(expected) && resp.StatusCode != expected)
                failures.Add($"status {resp.StatusCode}, atteso {expected}");
        }
        else if (resp.StatusCode >= 400)
            failures.Add($"status {resp.StatusCode}");
    }

    private void RunValidations(RequestStep step, RequestContext req, ResponseContext resp, Frame f, List<string> failures)
    {
        void Run(IValidationRule rule, IReadOnlyDictionary<string, string> args, ValidationLevel level, string label)
        {
            if (level > services.ValidationLevel) return;
            EmitStepEvent("StepStarted", "Validator", label, "Started", f, new { requestName = step.Name, arguments = args });
            var r = rule.Validate(req, resp, args);
            EmitStepEvent("StepCompleted", "Validator", label, r.Passed ? "Succeeded" : "Failed", f,
                new { requestName = step.Name, message = r.Message });
            if (!r.Passed) failures.Add($"{label}: {r.Message}");
        }

        foreach (var v in step.Validate)
        {
            var args = ResolveArgs(v.Args, f.Ctx);
            Run(services.Rules.GetValidation(v.Type, args), args, v.Level, v.Type);
        }
        foreach (var b in req.ValidationRules) Run(b.Rule, b.Args, b.Level, b.Rule.GetType().Name);
    }

    private void RunExtractions(RequestStep step, RequestContext req, ResponseContext resp, Frame f, List<string> failures)
    {
        void Run(string name, IExtractionRule rule, IReadOnlyDictionary<string, string> args, bool required)
        {
            EmitStepEvent("StepStarted", "Extractor", name, "Started", f, new { requestName = step.Name, arguments = args });
            var r = rule.Extract(req, resp, args);
            EmitStepEvent("StepCompleted", "Extractor", name,
                r.Success && r.Value is not null ? "Succeeded" : required ? "Failed" : "Succeeded", f,
                new { requestName = step.Name, success = r.Success, message = r.Message, value = r.Value });
            if (r.Success && r.Value is not null)
            {
                f.Ctx.Parameters[name] = r.Value;
                Emit(new ExecutionEvent
                {
                    EventType = "VariablesUpdated",
                    ScenarioName = f.Ctx.User.ScenarioName,
                    TestName = f.Ctx.TestName,
                    UserId = f.Ctx.User.UserId,
                    Iteration = f.Ctx.Iteration,
                    RequestName = step.Name,
                    VariableName = name,
                    VariableValue = r.Value
                });
            }
            else if (required) failures.Add($"estrazione '{name}': {r.Message}");
        }

        foreach (var e in step.Extract)
        {
            var args = ResolveArgs(e.Args, f.Ctx);
            Run(e.Name!, services.Rules.GetExtraction(e.Type, args), args, e.Required);
        }
        foreach (var b in req.ExtractionRules) Run(b.ParameterName, b.Rule, b.Args, b.Required);
    }

    private void EmitStepEvent(string eventType, string stepType, string stepName, string status, Frame f, object? details = null)
    {
        Emit(new ExecutionEvent
        {
            EventType = eventType,
            StepType = stepType,
            StepName = stepName,
            StepStatus = status,
            ScenarioName = f.Ctx.User.ScenarioName,
            TestName = f.Ctx.TestName,
            UserId = f.Ctx.User.UserId,
            Iteration = f.Ctx.Iteration,
            RequestName = f.Transaction,
            StepDetails = details
        });
    }

    private void Emit(ExecutionEvent executionEvent)
    {
        try { services.OnExecutionEvent?.Invoke(executionEvent); }
        catch (Exception ex) { services.OnPluginError?.Invoke(ex); }
    }

    private static Dictionary<string, string> ResolveArgs(Dictionary<string, string> args, TestContext ctx)
    {
        var resolved = new Dictionary<string, string>(args.Count, StringComparer.Ordinal);
        foreach (var kv in args) resolved[kv.Key] = VariableResolver.Resolve(kv.Value, ctx);
        return resolved;
    }

    private static RequestBody? ToBody(BodyDef? b) => b is null ? null : new RequestBody
    {
        Kind = b.Kind, Text = b.Text, FilePath = b.FilePath,
        Form = [.. b.Form], Parts = [.. b.Parts]
    };

    /// <summary>Sostituisce le variabili in URL, header e body e rende assoluto l'URL (baseUrl + url, per concatenazione).</summary>
    private static void Bind(RequestContext req, Frame f)
    {
        string R(string? s) => VariableResolver.Resolve(s, f.Ctx);

        req.Url = Combine(f.Def.BaseUrl, R(req.Url));
        foreach (var key in req.Headers.Keys.ToList()) req.Headers[key] = R(req.Headers[key]);

        if (req.Body is not { } body) return;
        body.Text = body.Text is null ? null : R(body.Text);
        if (body.FilePath is not null) body.FilePath = AbsPath(R(body.FilePath), f.BaseDir);
        body.Form = body.Form.Select(kv => KeyValuePair.Create(R(kv.Key), R(kv.Value))).ToList();
        body.Parts = body.Parts.Select(p => new MultipartPart(
            R(p.Name),
            p.Value is null ? null : R(p.Value),
            p.FilePath is null ? null : AbsPath(R(p.FilePath), f.BaseDir),
            p.FileName is null ? null : R(p.FileName),
            p.ContentType)).ToList();
    }

    private static string AbsPath(string p, string baseDir) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p));

    private static string Combine(string? baseUrl, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && (abs.Scheme is "http" or "https")) return url;
        if (string.IsNullOrEmpty(baseUrl)) throw new InvalidOperationException($"URL relativo '{url}' senza baseUrl");
        return baseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
    }

    private static string FormatExceptionChain(Exception exception)
    {
        var lines = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            lines.Add($"{current.GetType().FullName}: {current.Message}");
        return string.Join(Environment.NewLine + " ---> ", lines);
    }

    private static long Micros(Stopwatch sw) => sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
}
