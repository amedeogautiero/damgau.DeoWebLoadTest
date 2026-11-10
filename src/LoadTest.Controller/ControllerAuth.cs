using System.Security.Cryptography;
using System.Text;

namespace LoadTest.Controller;

/// <summary>Autenticazione degli agenti (CONTRATTO sez. 7: mTLS o token). Il confronto dei token è a tempo costante.</summary>
public static class ControllerAuth
{
    public static bool IsAuthorized(ControllerOptions o, string? authorizationHeader, bool hasClientCertificate)
    {
        if (hasClientCertificate && o.RequireClientCertificate) return true;   // già validato da Kestrel
        if (o.AgentTokens.Count > 0 && TryBearer(authorizationHeader, out var token) && Matches(token, o.AgentTokens)) return true;
        return o.AllowAnonymousAgents;
    }

    public static bool TryBearer(string? header, out string token)
    {
        token = "";
        if (string.IsNullOrWhiteSpace(header)) return false;
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        token = header[prefix.Length..].Trim();
        return token.Length > 0;
    }

    public static bool Matches(string presented, IEnumerable<string> allowed)
    {
        var p = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var ok = false;
        foreach (var t in allowed)
            ok |= CryptographicOperations.FixedTimeEquals(p, SHA256.HashData(Encoding.UTF8.GetBytes(t)));
        return ok;
    }
}
