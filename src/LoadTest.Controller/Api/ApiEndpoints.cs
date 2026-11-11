using LoadTest.Controller.Agents;
using LoadTest.Controller.Runs;
using LoadTest.Protocol;
using LoadTest.Reports;
using LoadTest.Reports.Analysis;
using LoadTest.Reports.Export;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace LoadTest.Controller.Api;

/// <param name="LoadTestPath">Percorso visto dal controller; con ProjectId è relativo al progetto caricato.</param>
public sealed record StartRunBody(string LoadTestPath, string? Name, int? Seed, string? ProjectId = null);

/// <summary>
/// API JSON del controller per CLI ed estensione VS Code (CONTRATTO sez. 1: "HTTP/JSON locale o gRPC"). Forma PROPOSTA (G18, G22).
/// Accesso: con ApiToken serve "Authorization: Bearer ..."; senza, solo richieste da loopback.
/// </summary>
public static class ApiEndpoints
{
    public static void MapControllerApi(this IEndpointRouteBuilder app, string? requireHost = null)
    {
        var api = app.MapGroup("/api");
        if (requireHost is not null) api.RequireHost(requireHost);
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var o = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<ControllerOptions>>().Value;
            var allowed = string.IsNullOrEmpty(o.ApiToken)
                ? System.Net.IPAddress.IsLoopback(ctx.HttpContext.Connection.RemoteIpAddress ?? System.Net.IPAddress.None)
                : ControllerAuth.TryBearer(ctx.HttpContext.Request.Headers.Authorization.ToString(), out var t) && ControllerAuth.Matches(t, [o.ApiToken]);
            return allowed ? await next(ctx) : Results.Unauthorized();
        });

        api.MapGet("/agents", (AgentRegistry registry) => registry.Snapshot().Select(a => new AgentDto(
            a.AgentId, a.Name, a.Hello.Rid, a.Hello.Os, a.Hello.Cores, a.Hello.RamMb, a.Hello.MaxUsers,
            a.ReservedBy is not null, a.CpuPercent, a.ActiveUsers, a.LastHeartbeatUtc)));

        // zip della cartella del load test (CI → controller remoto)
        api.MapPost("/projects", async (HttpRequest req, ProjectStore store, IOptions<ControllerOptions> o) =>
        {
            var limit = req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = o.Value.MaxProjectUploadBytes;
            try
            {
                var id = await store.SaveZipAsync(req.Body, req.HttpContext.RequestAborted);
                return Results.Ok(new { projectId = id });
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { errors = new[] { ex.Message } }); }
        });

        api.MapPost("/runs", async (StartRunBody body, RunManager runs, ProjectStore store) =>
        {
            try
            {
                var path = body.ProjectId is { } pid ? store.ResolveLoadTest(pid, body.LoadTestPath) : body.LoadTestPath;
                var id = await runs.StartAsync(new RunRequest { LoadTestPath = path, RunName = body.Name, Seed = body.Seed });
                return Results.Accepted($"/api/runs/{id}", new { runId = id });
            }
            catch (RunPlanException ex) { return Results.BadRequest(new { errors = ex.Errors }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { errors = new[] { ex.Message } }); }
        });

        api.MapGet("/runs", (RunManager runs) => runs.List());
        api.MapGet("/runs/{id}", (string id, RunManager runs) => runs.Get(id) is { } s ? Results.Ok(s) : Results.NotFound());
        api.MapPost("/runs/{id}/stop", (string id, string? mode, string? reason, RunManager runs) =>
        {
            var m = string.Equals(mode, "abort", StringComparison.OrdinalIgnoreCase) ? StopMode.Abort : StopMode.Graceful;
            return runs.Stop(id, m, reason ?? "stop richiesto dall'API") ? Results.Accepted() : Results.NotFound();
        });

        // riassunto dai risultati (SQLite): percentili da istogrammi fusi (mai medie di percentili)
        api.MapGet("/runs/{id}/summary", (string id, IResultsRepository repo) =>
            repo.GetRun(id) is { } run ? Results.Ok(SummaryMapper.From(run, RunAnalyzer.Summarize(repo.ReadSamples(id)))) : Results.NotFound());

        api.MapGet("/runs/{id}/summary.csv", (string id, IResultsRepository repo) =>
        {
            if (repo.GetRun(id) is null) return Results.NotFound();
            var w = new StringWriter();
            CsvExporter.WriteSummary(w, RunAnalyzer.Summarize(repo.ReadSamples(id)));
            return Results.Text(w.ToString(), "text/csv");
        });
    }
}
