namespace LoadTest.Agent.Transfer;

/// <summary>Percorsi ricevuti dal controller: relativi, senza '..', senza radice. Difesa da path traversal (zip slip compreso).</summary>
public static class SafePath
{
    public static bool IsSafeRelative(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) return false;
        var s = path.Replace('\\', '/');
        if (s.StartsWith('/') || (s.Length >= 2 && s[1] == ':')) return false;
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..")) return false;
        normalized = string.Join('/', parts);
        return true;
    }

    public static string Combine(string root, string relative)
    {
        if (!IsSafeRelative(relative, out var n)) throw new InvalidDataException($"percorso non ammesso: '{relative}'");
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, n.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException($"percorso fuori dalla cartella: '{relative}'");
        return full;
    }
}
