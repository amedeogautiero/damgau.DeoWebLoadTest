namespace LoadTest.Agent.Service;

public enum OsKind { Linux, MacOS, Windows }

/// <summary>Parametri di `install` / `uninstall` (G25).</summary>
public sealed class InstallOptions
{
    public string Controller { get; set; } = "";
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
    public string AgentId { get; set; } = "";
    public int MaxUsers { get; set; }
    public string? InstallDir { get; set; }
    public string? WorkDir { get; set; }
    /// <summary>Utente del servizio (Linux/macOS: lo crea se manca; Windows: account di servizio, default LocalService).</summary>
    public string? User { get; set; }
    /// <summary>Solo sviluppo: accetta qualsiasi certificato del server.</summary>
    public bool Insecure { get; set; }
    public string ClientCert { get; set; } = "";
    public string ClientCertPassword { get; set; } = "";
    public bool NoStart { get; set; }
    /// <summary>uninstall: elimina anche la cartella di lavoro (cache e dati dell'agente).</summary>
    public bool Purge { get; set; }
}

/// <summary>Percorsi e nomi risolti per un sistema operativo.</summary>
public sealed record ServicePaths(OsKind Os, string ServiceName, string InstallDir, string WorkDir, string User, string ExePath,
    string SettingsPath, string? DefinitionPath, string? LogDir);

public abstract record PlanStep
{
    public abstract string Describe();
}

public sealed record MkdirStep(string Path, UnixFileMode? Mode = null) : PlanStep
{
    public override string Describe() => $"crea cartella {Path}";
}

public sealed record CopyFileStep(string From, string To, UnixFileMode? Mode = null) : PlanStep
{
    public override string Describe() => $"copia {From} → {To}";
}

/// <summary>Scrive un file; i permessi Unix si applicano alla creazione (un file con segreti non passa mai da 644).</summary>
public sealed record WriteFileStep(string Path, string Content, UnixFileMode? Mode = null, bool Secret = false) : PlanStep
{
    public override string Describe() => $"scrive {Path}" + (Secret ? " (contenuto con segreti, non mostrato)" : "");
}

public sealed record RunStep(string File, IReadOnlyList<string> Args, bool IgnoreFailure = false) : PlanStep
{
    public override string Describe() => "esegue " + File + " " + string.Join(" ", Args.Select(a => a.Contains(' ') || a.Contains('"') ? $"'{a}'" : a)) + (IgnoreFailure ? "   (errori ignorati)" : "");
}

/// <summary>Esegue il comando solo se il controllo fallisce (es. crea l'utente se `id -u utente` non riesce).</summary>
public sealed record RunUnlessStep(string CheckFile, IReadOnlyList<string> CheckArgs, string File, IReadOnlyList<string> Args) : PlanStep
{
    public override string Describe() => $"se `{CheckFile} {string.Join(" ", CheckArgs)}` fallisce: {File} {string.Join(" ", Args.Select(a => a.Length > 60 ? a[..57] + "…" : a))}";
}

public sealed record DeleteStep(string Path, bool Recursive = false) : PlanStep
{
    public override string Describe() => $"elimina {Path}";
}
