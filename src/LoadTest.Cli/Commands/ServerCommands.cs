using LoadTest.Agent;
using LoadTest.Cli.Args;
using LoadTest.Controller;

namespace LoadTest.Cli.Commands;

/// <summary>`loadtest agent` e `loadtest controller`: traducono le opzioni in configurazione e avviano l'host (si fermano con Ctrl+C).</summary>
public static class ServerCommands
{
    private static readonly (string Option, string Key)[] AgentMap =
    [
        ("controller", "Agent:ControllerUrl"), ("token", "Agent:Token"), ("name", "Agent:Name"), ("agent-id", "Agent:AgentId"),
        ("work-dir", "Agent:WorkDirectory"), ("max-users", "Agent:MaxUsers"),
        ("client-cert", "Agent:ClientCertificatePath"), ("client-cert-password", "Agent:ClientCertificatePassword")
    ];

    private static readonly (string Option, string Key)[] ControllerMap =
    [
        ("listen", "Controller:ListenUrl"), ("api", "Controller:ApiUrl"), ("api-token", "Controller:ApiToken"),
        ("cert", "Controller:CertificatePath"), ("cert-password", "Controller:CertificatePassword"),
        ("work-dir", "Controller:WorkDirectory"), ("results-db", "Controller:ResultsDatabase"), ("name", "Controller:Name")
    ];

    public static async Task<int> AgentAsync(string[] args, CliContext ctx)
    {
        var spec = AgentMap.Select(m => new OptionSpec(m.Option)).Append(new OptionSpec("insecure", false)).ToList();
        var p = ArgParser.Parse(args, spec, allowRaw: true);
        if (p.Positional.Count > 0) throw new CliException(ExitCodes.Usage, $"argomento inatteso '{p.Positional[0]}'");
        var host = Translate(p, AgentMap);
        if (p.Flag("insecure")) host.Add("--Agent:AllowInvalidServerCertificate=true");
        host.AddRange(p.Raw);
        await AgentHost.RunAsync([.. host], ctx.Stop);
        return ExitCodes.Success;
    }

    public static async Task<int> ControllerAsync(string[] args, CliContext ctx)
    {
        var spec = ControllerMap.Select(m => new OptionSpec(m.Option))
            .Concat([new OptionSpec("agent-token"), new OptionSpec("trusted-client-cert"),
                     new OptionSpec("require-client-cert", false), new OptionSpec("allow-anonymous-agents", false), new OptionSpec("allow-partial-rig", false)])
            .ToList();
        var p = ArgParser.Parse(args, spec, allowRaw: true);
        if (p.Positional.Count > 0) throw new CliException(ExitCodes.Usage, $"argomento inatteso '{p.Positional[0]}'");
        var host = Translate(p, ControllerMap);
        var tokens = p.GetAll("agent-token");
        for (var i = 0; i < tokens.Count; i++) host.Add($"--Controller:AgentTokens:{i}={tokens[i]}");
        var thumbs = p.GetAll("trusted-client-cert");
        for (var i = 0; i < thumbs.Count; i++) host.Add($"--Controller:TrustedClientCertificateThumbprints:{i}={thumbs[i]}");
        if (p.Flag("require-client-cert")) host.Add("--Controller:RequireClientCertificate=true");
        if (p.Flag("allow-anonymous-agents")) host.Add("--Controller:AllowAnonymousAgents=true");
        if (p.Flag("allow-partial-rig")) host.Add("--Controller:AllowPartialRig=true");
        host.AddRange(p.Raw);
        await ControllerHost.RunAsync([.. host], ctx.Stop);
        return ExitCodes.Success;
    }

    private static List<string> Translate(ParsedArgs p, (string Option, string Key)[] map)
    {
        var list = new List<string>();
        foreach (var (option, key) in map)
            if (p.Get(option) is { } v) list.Add($"--{key}={v}");
        return list;
    }
}
