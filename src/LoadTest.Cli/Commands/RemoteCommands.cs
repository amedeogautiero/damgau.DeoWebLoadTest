using LoadTest.Cli.Args;
using LoadTest.Cli.Remote;

namespace LoadTest.Cli.Commands;

/// <summary>`loadtest agents | status | stop`: interrogano un controller (URL da --controller o LOADTEST_CONTROLLER).</summary>
public static class RemoteCommands
{
    private static ControllerClient Client(ParsedArgs p, CliContext ctx)
    {
        var url = CommonOptions.ControllerUrl(p, allowEnv: true)
            ?? throw new CliException(ExitCodes.Usage, "manca --controller <url> (o la variabile LOADTEST_CONTROLLER)");
        return ctx.Services.ClientFactory(url, CommonOptions.Token(p));
    }

    public static async Task<int> AgentsAsync(string[] args, CliContext ctx)
    {
        var p = ArgParser.Parse(args, Specs.Remote);
        var agents = await Client(p, ctx).GetAgentsAsync(CancellationToken.None);
        if (agents.Count == 0) { ctx.Out.WriteLine("nessun agente connesso"); return ExitCodes.Success; }
        ctx.Out.WriteLine($"{"nome",-24}{"id",-34}{"rid",-14}{"core",6}{"maxUtenti",11}{"cpu%",7}{"utenti",8}  stato");
        foreach (var a in agents)
            ctx.Out.WriteLine($"{a.Name,-24}{a.Id,-34}{a.Rid,-14}{a.Cores,6}{a.MaxUsers,11}{a.CpuPercent,7:0}{a.ActiveUsers,8}  {(a.Busy ? "occupato" : "libero")}");
        return ExitCodes.Success;
    }

    public static async Task<int> StatusAsync(string[] args, CliContext ctx)
    {
        var p = ArgParser.Parse(args, Specs.Remote);
        if (p.Positional.Count != 1) throw new CliException(ExitCodes.Usage, "indicare l'id della run");
        var s = await Client(p, ctx).GetRunAsync(p.Positional[0], CancellationToken.None)
            ?? throw new CliException(ExitCodes.Usage, $"run '{p.Positional[0]}' sconosciuta");
        ctx.Out.WriteLine($"{s.Name} ({s.RunId}): {s.State}" + (s.Status is null ? "" : $" → {s.Status}" + (string.IsNullOrEmpty(s.Reason) ? "" : $" ({s.Reason})")));
        ctx.Out.WriteLine($"utenti {s.ActiveUsers} · richieste {s.Requests} · errori {s.Errors}");
        foreach (var a in s.Agents) ctx.Out.WriteLine($"  agente {a.Name}: {a.State}");
        foreach (var w in s.Warnings) ctx.Out.WriteLine("  avviso: " + w);
        return ExitCodes.Success;
    }

    public static async Task<int> StopAsync(string[] args, CliContext ctx)
    {
        var p = ArgParser.Parse(args, [.. Specs.Remote, new OptionSpec("abort", false), new OptionSpec("reason")]);
        if (p.Positional.Count != 1) throw new CliException(ExitCodes.Usage, "indicare l'id della run");
        var ok = await Client(p, ctx).StopRunAsync(p.Positional[0], p.Flag("abort"), p.Get("reason") ?? "stop richiesto dalla CLI", CancellationToken.None);
        if (!ok) throw new CliException(ExitCodes.Usage, $"run '{p.Positional[0]}' sconosciuta");
        ctx.Out.WriteLine(p.Flag("abort") ? "abort richiesto" : "stop ordinato richiesto");
        return ExitCodes.Success;
    }
}
