using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LoadTest.Plugins;

/// <summary>Off: nessun controllo. IfPresent: se c'è una firma deve essere valida. Required: serve una firma valida di una chiave fidata.</summary>
public enum SignaturePolicy { Off, IfPresent, Required }

/// <summary>
/// Firma di un pacchetto (PROPOSTA P10 bis): ECDSA P-256 sull'hash SHA-256 del pacchetto (P7), scritto in esadecimale minuscolo.
/// Sta nel file `plugin.sig` della cartella del pacchetto; il file NON entra nell'hash. Chiavi in base64 (PKCS#8 la privata, SPKI la pubblica).
/// </summary>
public sealed record PluginSignature(string KeyId, string Signature)
{
    public const string FileName = "plugin.sig";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PluginSignature? ReadFrom(string packageDirectory)
    {
        var path = Path.Combine(packageDirectory, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<PluginSignature>(File.ReadAllText(path), Json) : null;
    }
}

public static class PluginSigning
{
    private const DSASignatureFormat Format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        using var e = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(e.ExportPkcs8PrivateKey()), Convert.ToBase64String(e.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Identificativo breve di una chiave pubblica: i primi 16 caratteri dello SHA-256 (esadecimale) della sua forma SPKI.</summary>
    public static string KeyIdOf(string publicKeyBase64) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKeyBase64))).ToLowerInvariant()[..16];

    public static PluginSignature Sign(string packageSha256, string privateKeyBase64)
    {
        using var e = ECDsa.Create();
        e.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        var sig = e.SignData(Data(packageSha256), HashAlgorithmName.SHA256, Format);
        return new PluginSignature(KeyIdOf(Convert.ToBase64String(e.ExportSubjectPublicKeyInfo())), Convert.ToBase64String(sig));
    }

    /// <summary>Firma il pacchetto e scrive `plugin.sig` nella sua cartella (per un .csx: il pacchetto compilato in cache).</summary>
    public static PluginSignature SignPackage(PluginPackage pkg, string privateKeyBase64)
    {
        var s = Sign(pkg.Sha256, privateKeyBase64);
        File.WriteAllText(Path.Combine(pkg.Directory, PluginSignature.FileName), System.Text.Json.JsonSerializer.Serialize(s, PluginSignature.Json));
        return s;
    }

    internal static byte[] Data(string sha256) => Encoding.UTF8.GetBytes(sha256.ToLowerInvariant());

    internal static bool Verify(string packageSha256, PluginSignature sig, string publicKeyBase64)
    {
        try
        {
            using var e = ECDsa.Create();
            e.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return e.VerifyData(Data(packageSha256), Convert.FromBase64String(sig.Signature), HashAlgorithmName.SHA256, Format);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }
}

/// <summary>Chiavi pubbliche fidate (configurazione dell'agente/controller) e politica di verifica.</summary>
public sealed class PluginTrust
{
    private readonly Dictionary<string, string> _keys;
    public SignaturePolicy Policy { get; }

    public PluginTrust(IEnumerable<string> trustedPublicKeysBase64, SignaturePolicy policy)
    {
        Policy = policy;
        _keys = trustedPublicKeysBase64.ToDictionary(PluginSigning.KeyIdOf, k => k, StringComparer.Ordinal);
    }

    /// <summary>Lancia PluginLoadException se il pacchetto non rispetta la politica. Senza `sig` legge plugin.sig dalla cartella.</summary>
    public void Check(PluginPackage pkg, PluginSignature? sig = null)
    {
        if (Policy == SignaturePolicy.Off) return;
        sig ??= PluginSignature.ReadFrom(pkg.Directory);
        var name = pkg.Manifest.Name;
        if (sig is null)
        {
            if (Policy == SignaturePolicy.Required) throw new PluginLoadException($"'{name}': pacchetto non firmato");
            return;
        }
        if (!_keys.TryGetValue(sig.KeyId, out var pub)) throw new PluginLoadException($"'{name}': firmato con una chiave non fidata ({sig.KeyId})");
        if (!PluginSigning.Verify(pkg.Sha256, sig, pub)) throw new PluginLoadException($"'{name}': firma non valida per l'hash {pkg.Sha256}");
    }
}
