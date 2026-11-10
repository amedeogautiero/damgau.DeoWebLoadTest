using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace LoadTest.Controller;

public static class KestrelSetup
{
    /// <summary>Due endpoint: gRPC per gli agenti (HTTP/2, https con CertificatePath, opzionalmente mTLS) e API JSON (HTTP/1.1).</summary>
    public static void Configure(KestrelServerOptions k, ControllerOptions o)
    {
        Listen(k, new Uri(o.ListenUrl), HttpProtocols.Http2, o, withClientCertificates: true);
        Listen(k, new Uri(o.ApiUrl), HttpProtocols.Http1, o, withClientCertificates: false);
    }

    private static void Listen(KestrelServerOptions k, Uri url, HttpProtocols protocols, ControllerOptions o, bool withClientCertificates)
    {
        void Configure(ListenOptions l)
        {
            l.Protocols = protocols;
            if (url.Scheme != Uri.UriSchemeHttps) return;
            if (string.IsNullOrEmpty(o.CertificatePath))
                throw new InvalidOperationException($"{url}: l'endpoint https richiede Controller:CertificatePath (per lo sviluppo usare http)");
            var cert = X509CertificateLoader.LoadPkcs12FromFile(o.CertificatePath, string.IsNullOrEmpty(o.CertificatePassword) ? null : o.CertificatePassword);
            l.UseHttps(https =>
            {
                https.ServerCertificate = cert;
                if (!withClientCertificates || !o.RequireClientCertificate) return;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (c, _, errors) =>
                    o.TrustedClientCertificateThumbprints.Count > 0
                        ? o.TrustedClientCertificateThumbprints.Contains(c.Thumbprint, StringComparer.OrdinalIgnoreCase)
                        : errors == System.Net.Security.SslPolicyErrors.None;
            });
        }

        var host = url.Host;
        if (host is "*" or "+" or "0.0.0.0") k.ListenAnyIP(url.Port, Configure);
        else if (host == "localhost") k.ListenLocalhost(url.Port, Configure);
        else if (IPAddress.TryParse(host.Trim('[', ']'), out var ip)) k.Listen(ip, url.Port, Configure);
        else k.ListenAnyIP(url.Port, Configure);
    }
}
