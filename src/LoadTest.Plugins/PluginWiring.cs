using LoadTest.Engine.LoadTesting;
using LoadTest.Engine.Model;
using LoadTest.Engine.Rules;
using LoadTest.Engine.Runtime;
using LoadTest.Sdk;

namespace LoadTest.Plugins;

/// <summary>
/// Collega i plugin all'Engine senza modificarlo: costruisce ciò che EngineServices e LoadTestRunnerOptions si aspettano.
/// Limiti (PROPOSTA P13): il runner usa elenchi plugin GLOBALI, quindi i plugin dichiarati dai singoli web test vengono
/// uniti (senza duplicati) e valgono per tutti; le istanze sono condivise fra gli utenti virtuali e devono essere senza stato.
/// I test chiamati con `call:` vanno passati anch'essi in `tests`, altrimenti i loro plugin non vengono caricati.
/// </summary>
public sealed class PluginWiring
{
    public IReadOnlyList<IWebTestPlugin> WebTestPlugins { get; init; } = [];
    public IReadOnlyList<IRequestPlugin> RequestPlugins { get; init; } = [];
    public required RuleRegistry Rules { get; init; }
    public required ICodeStepHost CodeSteps { get; init; }
    /// <summary>Per LoadTestRunnerOptions.Plugins.</summary>
    public IReadOnlyList<(ILoadTestPlugin Plugin, IReadOnlyDictionary<string, string> Args)> LoadTestPlugins { get; init; } = [];
    /// <summary>Per LoadTestRunnerOptions.CustomProfileResolver.</summary>
    public required Func<string, ILoadProfile?> CustomProfileResolver { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <param name="compileSingleScript">Percorso .csx → pacchetto compilato nel processo che prepara l'esecuzione (Controller o VS Code Host).</param>
    public static PluginWiring Build(PluginHost host, IEnumerable<WebTestDefinition> tests,
        LoadTestDefinition? loadTest = null, Func<string, PluginPackage>? compileSingleScript = null)
    {
        var warnings = new List<string>();
        var defs = tests.ToList();

        var web = Distinct(defs.SelectMany(d => d.WebTestPlugins))
            .SelectMany(r => host.Create<IWebTestPlugin>(PluginKind.WebTest, r)).ToList();
        var req = Distinct(defs.SelectMany(d => d.RequestPlugins))
            .SelectMany(r => host.Create<IRequestPlugin>(PluginKind.Request, r)).ToList();

        var lt = new List<(ILoadTestPlugin, IReadOnlyDictionary<string, string>)>();
        foreach (var r in loadTest?.LoadTestPlugins ?? [])
            foreach (var p in host.Create<ILoadTestPlugin>(PluginKind.LoadTest, r)) lt.Add((p, r.Params));

        var rules = RuleRegistry.CreateDefault();
        RegisterRules(host, rules, warnings);

        ILoadProfile? ResolveProfile(string name)
        {
            if (!host.Catalog.Contains(PluginCatalog.ParseReference(name).Package)) return null;
            var found = host.Create<ILoadProfile>(PluginKind.LoadProfile, new PluginRef { Name = name });
            return found.Count == 1 ? found[0]
                : throw new PluginLoadException($"profilo '{name}' ambiguo: usare 'Pacchetto/Tipo' ({found.Count} tipi)");
        }

        return new PluginWiring
        {
            WebTestPlugins = web, RequestPlugins = req, Rules = rules, LoadTestPlugins = lt,
            CustomProfileResolver = ResolveProfile, Warnings = warnings,
            CodeSteps = new CodeStepHost(compileSingleScript
                ?? (p => throw new PluginLoadException($"'{p}': nessun compilatore .csx configurato (solo controller)")))
        };
    }

    public EngineServices CreateServices(IHttpExecutor http, ValidationLevel level = ValidationLevel.High,
        Action<ExecutionEvent>? onExecutionEvent = null, bool debugMode = false,
        Func<ExecutionEvent, CancellationToken, Task>? waitForContinue = null) => new()
    {
        Http = http, WebTestPlugins = WebTestPlugins, RequestPlugins = RequestPlugins,
        Rules = Rules, CodeSteps = CodeSteps, ValidationLevel = level,
        OnExecutionEvent = onExecutionEvent, DebugMode = debugMode, WaitForContinue = waitForContinue
    };

    private static IEnumerable<PluginRef> Distinct(IEnumerable<PluginRef> refs) =>
        refs.GroupBy(r => r.Name + "|" + string.Join(",", r.Params.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value)))
            .Select(g => g.First());

    /// <summary>
    /// Registra le regole dei plugin sotto "Pacchetto/Tipo", sotto "Tipo" (se unico) e sotto "Pacchetto" (se è l'unico tipo del pacchetto per quel kind).
    /// Non sovrascrive mai regole già registrate (built-in comprese): il conflitto diventa un avviso.
    /// Le istanze sono condivise: le regole ricevono gli argomenti a ogni chiamata e non devono tenere stato.
    /// </summary>
    private static void RegisterRules(PluginHost host, RuleRegistry rules, List<string> warnings)
    {
        var ext = new List<(string Pkg, PluginType T)>();
        var val = new List<(string Pkg, PluginType T)>();
        foreach (var pkg in host.Catalog.Packages)
        {
            var kinds = pkg.Manifest.Kinds;
            if (!kinds.Contains(PluginKind.Extraction) && !kinds.Contains(PluginKind.Validation)) continue;
            foreach (var t in host.Load(pkg.Manifest.Name).Types)
            {
                if (t.Kind == PluginKind.Extraction) ext.Add((pkg.Manifest.Name, t));
                else if (t.Kind == PluginKind.Validation) val.Add((pkg.Manifest.Name, t));
            }
        }

        var none = new Dictionary<string, string>();
        bool ExtExists(string k) { try { rules.GetExtraction(k, none); return true; } catch (InvalidOperationException) { return false; } }
        bool ValExists(string k) { try { rules.GetValidation(k, none); return true; } catch (InvalidOperationException) { return false; } }

        RegisterAll(ext, ExtExists, (k, o) => rules.Register(k, (IExtractionRule)o), "regola di estrazione", warnings);
        RegisterAll(val, ValExists, (k, o) => rules.Register(k, (IValidationRule)o), "regola di validazione", warnings);
    }

    private static void RegisterAll(List<(string Pkg, PluginType T)> items, Func<string, bool> exists,
        Action<string, object> register, string what, List<string> warnings)
    {
        var shortCount = items.GroupBy(i => i.T.Type.Name).ToDictionary(g => g.Key, g => g.Count());
        var perPackage = items.GroupBy(i => i.Pkg).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (pkg, t) in items)
        {
            var instance = PluginLoader.Instantiate(t.Type, null);
            var keys = new List<string> { $"{pkg}/{t.Type.Name}" };
            if (shortCount[t.Type.Name] == 1) keys.Add(t.Type.Name);
            else warnings.Add($"{what} '{t.Type.Name}' presente in più pacchetti: usare 'Pacchetto/{t.Type.Name}'");
            if (perPackage[pkg] == 1) keys.Add(pkg);
            foreach (var k in keys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (exists(k)) warnings.Add($"{what} '{k}' già registrata: il plugin '{pkg}' non la sostituisce");
                else register(k, instance);
            }
        }
    }
}
