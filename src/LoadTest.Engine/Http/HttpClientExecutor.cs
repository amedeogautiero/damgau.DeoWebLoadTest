using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LoadTest.Engine.Runtime;
using LoadTest.Sdk;

namespace LoadTest.Engine.Http;

public sealed class HttpClientOptions
{
    public IWebProxy? Proxy { get; init; }
    public ICredentials? Credentials { get; init; }                 // TODO: mappare credentials: basic | ntlm | negotiate
    public X509Certificate2? ClientCertificate { get; init; }
}

/// <summary>
/// Esecutore HTTP basato su SocketsHttpHandler. Un'istanza = una sessione (cookie container proprio):
/// il modello di connessione per utente / pool lo decide la chat Load test (quante istanze creare e condividere).
/// I redirect sono seguiti a mano per misurare l'intera catena e per poter asserire uno status 3xx.
/// </summary>
public sealed class HttpClientExecutor : IHttpExecutor, IDisposable
{
    private const int MaxRedirects = 10;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _client;

    public CookieContainer Cookies { get; } = new();

    public HttpClientExecutor(HttpClientOptions? options = null)
    {
        _handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = Cookies,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        if (options?.Proxy is { } proxy) { _handler.Proxy = proxy; _handler.UseProxy = true; }
        if (options?.Credentials is { } cred) { _handler.Credentials = cred; _handler.PreAuthenticate = true; }
        if (options?.ClientCertificate is { } cert)
            _handler.SslOptions.ClientCertificates = new X509CertificateCollection { cert };
        _client = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ResponseContext> SendAsync(RequestContext request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSec));

        var sw = Stopwatch.StartNew();
        var uri = new Uri(request.Url, UriKind.Absolute);
        var method = new HttpMethod(request.Method);
        var sendBody = true;
        var history = new List<int>();
        long sent = 0;

        try
        {
            for (var hop = 0; ; hop++)
            {
                using var msg = BuildMessage(request, uri, method, sendBody);
                if (msg.Content?.Headers.ContentLength is { } len) sent += len;   // solo body: gli header non sono contati

                using var response = await _client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var status = (int)response.StatusCode;
                history.Add(status);

                if (request.FollowRedirects && status is 301 or 302 or 303 or 307 or 308
                    && response.Headers.Location is { } location && hop < MaxRedirects)
                {
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (status is 301 or 302 or 303 && method != HttpMethod.Get && method != HttpMethod.Head)
                    {
                        method = HttpMethod.Get;
                        sendBody = false;
                    }
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                sw.Stop();

                var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in response.Headers.Concat(response.Content.Headers)) headers[h.Key] = h.Value.ToList();

                return new ResponseContext
                {
                    StatusCode = status,
                    ReasonPhrase = response.ReasonPhrase,
                    StatusHistory = history,
                    Headers = headers,
                    Body = bytes,
                    BodyText = Decode(bytes, response.Content.Headers.ContentType?.CharSet, request.Encoding),
                    FinalUrl = uri.ToString(),
                    ElapsedMicros = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency,
                    BytesSent = sent
                };
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout di {request.TimeoutSec}s su {request.Url}");
        }
    }

    private static HttpRequestMessage BuildMessage(RequestContext r, Uri uri, HttpMethod method, bool sendBody)
    {
        var msg = new HttpRequestMessage(method, uri)
        {
            Version = r.HttpVersion switch { "1.0" => HttpVersion.Version10, "2" or "2.0" => HttpVersion.Version20, _ => HttpVersion.Version11 },
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        if (sendBody && r.Body is { Kind: not BodyKind.None } body) msg.Content = BuildContent(r, body);

        foreach (var (key, value) in r.Headers)
        {
            if (msg.Content is not null && key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                msg.Content.Headers.Remove(key);
                msg.Content.Headers.TryAddWithoutValidation(key, value);
            }
            else msg.Headers.TryAddWithoutValidation(key, value);
        }
        return msg;
    }

    private static HttpContent BuildContent(RequestContext r, RequestBody body)
    {
        var enc = GetEncoding(r.Encoding);
        switch (body.Kind)
        {
            case BodyKind.Form: return new FormUrlEncodedContent(body.Form);
            case BodyKind.Raw: return new StringContent(body.Text ?? "", enc, "text/plain");
            case BodyKind.Json: return new StringContent(body.Text ?? "", enc, "application/json");
            case BodyKind.Xml: return new StringContent(body.Text ?? "", enc, "application/xml");
            case BodyKind.File:
            {
                var c = new ByteArrayContent(File.ReadAllBytes(body.FilePath!));
                c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return c;
            }
            case BodyKind.Multipart:
            {
                var mp = new MultipartFormDataContent();
                foreach (var p in body.Parts)
                {
                    if (p.FilePath is not null)
                    {
                        var fc = new ByteArrayContent(File.ReadAllBytes(p.FilePath));
                        fc.Headers.ContentType = MediaTypeHeaderValue.Parse(p.ContentType ?? "application/octet-stream");
                        mp.Add(fc, p.Name, p.FileName ?? Path.GetFileName(p.FilePath));
                    }
                    else mp.Add(new StringContent(p.Value ?? "", enc), p.Name);
                }
                return mp;
            }
            default: throw new NotSupportedException($"Body {body.Kind} non supportato");
        }
    }

    private static string Decode(byte[] bytes, string? charset, string fallback)
    {
        var enc = GetEncoding(charset ?? fallback);
        return enc.GetString(bytes);
    }

    private static Encoding GetEncoding(string name)
    {
        try { return Encoding.GetEncoding(name.Trim('"')); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }
}
