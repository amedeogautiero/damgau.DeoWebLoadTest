using LoadTest.Controller.Agents;
using LoadTest.Controller.Runs;
using LoadTest.Protocol;
using Microsoft.Extensions.Options;

namespace LoadTest.Controller.Api;

public sealed record StartRunBody(string LoadTestPath, string? Name, int? Seed);

/// <summary>
/// API JSON del controller per CLI ed estensione VS Code (CONTRATTO sez. 1: "HTTP/JSON locale o gRPC"). Forma PROPOSTA (G18).
/// Accesso: con ApiToken configurato serve "Authorization: Bearer ..."; senza, solo richieste da loopback.
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

        api.MapGet("/agents", (AgentRegistry registry) => registry.Snapshot().Select(a => new
        {
            id = a.AgentId, name = a.Name, rid = a.Hello.Rid, os = a.Hello.Os, cores = a.Hello.Cores, ramMb = a.Hello.RamMb,
            maxUsers = a.Hello.MaxUsers, busy = a.ReservedBy is not null, cpuPercent = a.CpuPercent,
            activeUsers = a.ActiveUsers, lastHeartbeatUtc = a.LastHeartbeatUtc
        }));

        api.MapPost("/runs", async (StartRunBody body, RunManager runs) =>
        {
            try
            {
                var id = await runs.StartAsync(new RunRequest { LoadTestPath = body.LoadTestPath, RunName = body.Name, Seed = body.Seed });
                return Results.Accepted($"/api/runs/{id}", new { runId = id });
            }
            catch (RunPlanException ex) { return Results.BadRequest(new { errors = ex.Errors }); }
        });

        api.MapGet("/runs", (RunManager runs) => runs.List());
        api.MapGet("/runs/{id}", (string id, RunManager runs) => runs.Get(id) is { } s ? Results.Ok(s) : Results.NotFound());
        api.MapPost("/runs/{id}/stop", (string id, string? mode, string? reason, RunManager runs) =>
        {
            var m = string.Equals(mode, "abort", StringComparison.OrdinalIgnoreCase) ? StopMode.Abort : StopMode.Graceful;
            return runs.Stop(id, m, reason ?? "stop richiesto dall'API") ? Results.Accepted() : Results.NotFound();
        });
    }
}
