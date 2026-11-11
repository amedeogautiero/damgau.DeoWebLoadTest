using LoadTest.Cli.Args;
using LoadTest.Controller.Packaging;
using LoadTest.Controller.Rig;
using LoadTest.Controller.Runs;
using LoadTest.Engine.LoadTesting;
using LoadTest.Plugins;

namespace LoadTest.Cli.Commands;

public static class ValidateCommand
{
    /// <summary>In validazione i pacchetti `.csx` non si compilano: si controlla solo che ci siano (il compilatore sta sul controller).</summary>
    private sealed class PassThroughCompiler : IPluginCompiler
    {
        public PluginPackage Compile(PluginPackage scriptPackage, string outputDirectory) => scriptPackage;
    }

    public static int Execute(string[] args, CliContext ctx)
    {
        var p = ArgParser.Parse(args, []);
        if (p.Positional.Count == 0) throw new CliException(ExitCodes.Usage, "indicare almeno un file *.loadtest.yaml");

        var invalid = 0;
        foreach (var file in p.Positional)
        {
            var (errors, warnings, files) = Check(file);
            if (errors.Count == 0)
            {
                ctx.Out.WriteLine($"OK {file} ({files} file" + (warnings.Count > 0 ? $", {warnings.Count} avvisi" : "") + ")");
                foreach (var w in warnings) ctx.Out.WriteLine("  avviso: " + w);
            }
            else
            {
                invalid++;
                ctx.Err.WriteLine($"NON VALIDO {file}");
                foreach (var e in errors) ctx.Err.WriteLine("  - " + e);
                foreach (var w in warnings) ctx.Err.WriteLine("  avviso: " + w);
            }
        }
        return invalid == 0 ? ExitCodes.Success : ExitCodes.Usage;
    }

    public static (List<string> Errors, List<string> Warnings, int Files) Check(string file)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) return ([$"file non trovato: {path}"], warnings, 0);

        LoadTestDefinition? def = null;
        RigDefinition? rig = null;
        try { def = LoadTestLoader.LoadFile(path); }
        catch (LoadTestLoadException ex) { errors.AddRange(ex.Errors); }
        try { rig = RigLoader.LoadFile(path); }
        catch (RigLoadException ex) { errors.AddRange(ex.Errors); }
        if (def is null || rig is null) return (errors, warnings, 0);

        var work = Directory.CreateTempSubdirectory("lt-validate").FullName;
        try
        {
            var set = RunPackager.Build(path, def, rig, [1.0], work, new PassThroughCompiler());
            warnings.AddRange(set.Warnings);
            return (errors, warnings, set.Common.Count + set.PerAgent[0].Count);
        }
        catch (RunPlanException ex) { errors.AddRange(ex.Errors); }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
        return (errors, warnings, 0);
    }
}
