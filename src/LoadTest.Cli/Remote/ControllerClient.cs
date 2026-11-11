using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LoadTest.Controller.Api;
using LoadTest.Controller.Runs;

namespace LoadTest.Cli.Remote;

/// <summary>Client dell'API JSON del controller (G18/G22). Ogni errore di rete o di protocollo diventa una <see cref="CliException"/> con codice 3.</summary>
public sealed class ControllerClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ControllerClient Create(string baseUrl, string? token)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new CliException(ExitCodes.Usage, $"URL del controller non valido: '{baseUrl}'");
        var client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromMinutes(30) };
        if (!string.IsNullOrEmpty(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new ControllerClient(client);
    }

    public async Task<IReadOnlyList<AgentDto>> GetAgentsAsync(CancellationToken ct) =>
        await Read<List<AgentDto>>(await Send(() => http.GetAsync("api/agents", ct))) ?? [];

    public async Task<string> UploadProjectAsync(string zipPath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(zipPath);
        using var content = new StreamContent(fs);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var resp = await Send(() => http.PostAsync("api/projects", content, ct));
        return await ReadString(resp, "projectId");
    }

    public async Task<string> StartRunAsync(StartRunBody body, CancellationToken ct)
    {
        using var resp = await Send(() => http.PostAsJsonAsync("api/runs", body, Json, ct));
        return await ReadString(resp, "runId");
    }

    public async Task<RunSnapshot?> GetRunAsync(string runId, CancellationToken ct)
    {
        using var resp = await Send(() => http.GetAsync($"api/runs/{Uri.EscapeDataString(runId)}", ct), allowNotFound: true);
        return resp.StatusCode == HttpStatusCode.NotFound ? null : await Read<RunSnapshot>(resp);
    }

    public async Task<bool> StopRunAsync(string runId, bool abort, string reason, CancellationToken ct)
    {
        var url = $"api/runs/{Uri.EscapeDataString(runId)}/stop?mode={(abort ? "abort" : "graceful")}&reason={Uri.EscapeDataString(reason)}";
        using var resp = await Send(() => http.PostAsync(url, null, ct), allowNotFound: true);
        return resp.StatusCode != HttpStatusCode.NotFound;
    }

    public async Task<RunSummaryDto?> GetSummaryAsync(string runId, CancellationToken ct)
    {
        using var resp = await Send(() => http.GetAsync($"api/runs/{Uri.EscapeDataString(runId)}/summary", ct), allowNotFound: true);
        return resp.StatusCode == HttpStatusCode.NotFound ? null : await Read<RunSummaryDto>(resp);
    }

    public async Task<string> GetSummaryCsvAsync(string runId, CancellationToken ct)
    {
        using var resp = await Send(() => http.GetAsync($"api/runs/{Uri.EscapeDataString(runId)}/summary.csv", ct));
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // ---------- interni ----------

    private static async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> call, bool allowNotFound = false)
    {
        HttpResponseMessage resp;
        try { resp = await call(); }
        catch (HttpRequestException ex) { throw new CliException(ExitCodes.Infrastructure, "controller non raggiungibile: " + ex.Message); }
        catch (TaskCanceledException) { throw new CliException(ExitCodes.Infrastructure, "il controller non ha risposto in tempo"); }

        if (resp.IsSuccessStatusCode || (allowNotFound && resp.StatusCode == HttpStatusCode.NotFound)) return resp;
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new CliException(ExitCodes.Infrastructure, "non autorizzato: token mancante o errato (--token o LOADTEST_TOKEN)");
            var errors = await TryErrors(resp);
            throw new CliException(ExitCodes.Infrastructure,
                errors.Count > 0 ? "richiesta rifiutata dal controller:\n  - " + string.Join("\n  - ", errors) : $"il controller ha risposto {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
    }

    private static async Task<List<string>> TryErrors(HttpResponseMessage resp)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array)
                return e.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
        }
        catch (JsonException) { }
        return [];
    }

    private static async Task<T?> Read<T>(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadFromJsonAsync<T>(Json); }
        catch (JsonException ex) { throw new CliException(ExitCodes.Infrastructure, "risposta del controller non valida: " + ex.Message); }
    }

    private static async Task<string> ReadString(HttpResponseMessage resp, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty(property).GetString() ?? throw new JsonException($"'{property}' nullo");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CliException(ExitCodes.Infrastructure, "risposta del controller non valida: " + ex.Message);
        }
    }
}
