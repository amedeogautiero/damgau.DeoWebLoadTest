using System.Globalization;
using LoadTest.Engine.Model;
using LoadTest.Sdk;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace LoadTest.Engine.LoadTesting;

public sealed class LoadTestLoadException(string origin, IReadOnlyList<string> errors)
    : Exception($"{origin}: {string.Join("; ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Legge e valida un file *.loadtest.yaml (CONTRATTO sez. 5, versione 1.x). Raccoglie tutti gli errori, non solo il primo.</summary>
public static class LoadTestLoader
{
    public const int SupportedMajorVersion = 1;

    public static LoadTestDefinition LoadFile(string path)
    {
        using var reader = File.OpenText(path);
        var def = Load(reader, path);
        def.SourcePath = Path.GetFullPath(path);
        return def;
    }

    public static LoadTestDefinition Load(TextReader reader, string origin = "<memoria>")
    {
        var stream = new YamlStream();
        try { stream.Load(reader); }
        catch (YamlException ex) { throw new LoadTestLoadException(origin, [$"YAML non valido ({ex.Message})"]); }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new LoadTestLoadException(origin, ["la radice del documento deve essere una mappa"]);

        var parser = new Parser();
        var def = parser.ParseRoot(root);
        if (parser.Errors.Count > 0) throw new LoadTestLoadException(origin, parser.Errors);
        return def;
    }

    private sealed class Parser
    {
        public List<string> Errors { get; } = [];

        private void Err(YamlNode? n, string msg) => Errors.Add(n is null ? msg : $"riga {n.Start.Line}: {msg}");

        private static YamlNode? Child(YamlMappingNode m, string key) =>
            m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

        private static string? Str(YamlMappingNode m, string key) => Child(m, key) is YamlScalarNode s ? s.Value : null;

        private int? Int(YamlMappingNode m, string key, string path)
        {
            var s = Str(m, key);
            if (s is null) return null;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            Err(Child(m, key), $"{path}.{key}: '{s}' non è un intero");
            return null;
        }

        private double? Dbl(YamlMappingNode m, string key, string path)
        {
            var s = Str(m, key);
            if (s is null) return null;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            Err(Child(m, key), $"{path}.{key}: '{s}' non è un numero");
            return null;
        }

        private bool? Bool(YamlMappingNode m, string key, string path)
        {
            var s = Str(m, key);
            if (s is null) return null;
            if (bool.TryParse(s, out var b)) return b;
            Err(Child(m, key), $"{path}.{key}: '{s}' non è true/false");
            return null;
        }

        private T Enum<T>(YamlMappingNode m, string key, string path, T def) where T : struct, System.Enum
        {
            var s = Str(m, key);
            if (s is null) return def;
            if (System.Enum.TryParse<T>(s, true, out var v) && System.Enum.IsDefined(v)) return v;
            var names = string.Join("|", System.Enum.GetNames<T>().Select(x => char.ToLowerInvariant(x[0]) + x[1..]));
            Err(Child(m, key), $"{path}.{key}: valore '{s}' non valido ({names})");
            return def;
        }

        public LoadTestDefinition ParseRoot(YamlMappingNode root)
        {
            var def = new LoadTestDefinition();

            var versionText = Str(root, "version");
            if (versionText is null) Err(root, "campo 'version' mancante");
            else
            {
                var major = int.TryParse(versionText.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mj) ? mj : -1;
                if (major != SupportedMajorVersion) Err(Child(root, "version"), $"versione formato '{versionText}' non supportata (attesa {SupportedMajorVersion}.x)");
                else def.Version = major;
            }

            def.Name = Str(root, "name") ?? "";

            if (Child(root, "runSettings") is YamlMappingNode rs) def.RunSettings = ParseRunSettings(rs);
            else Err(root, "'runSettings' mancante");

            if (Child(root, "scenarios") is YamlSequenceNode sc)
            {
                foreach (var item in sc.Children)
                {
                    if (item is YamlMappingNode sm) def.Scenarios.Add(ParseScenario(sm, def.Scenarios.Count));
                    else Err(item, "ogni scenario deve essere una mappa");
                }
            }
            if (def.Scenarios.Count == 0) Err(root, "'scenarios' deve contenere almeno uno scenario");
            var dup = def.Scenarios.GroupBy(s => s.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null) Err(root, $"nome scenario duplicato: '{dup.Key}'");

            if (Child(root, "plugins") is YamlMappingNode pl && Child(pl, "loadTest") is YamlSequenceNode lp)
                foreach (var p in lp.Children.OfType<YamlMappingNode>())
                {
                    var prm = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (Child(p, "params") is YamlMappingNode pm)
                        foreach (var (k, v) in pm.Children) prm[((YamlScalarNode)k).Value ?? ""] = (v as YamlScalarNode)?.Value ?? "";
                    def.LoadTestPlugins.Add(new PluginRef { Name = Str(p, "name") ?? "", Params = prm });
                }
            return def;
        }

        private RunSettingsDef ParseRunSettings(YamlMappingNode m)
        {
            const string P = "runSettings";
            var r = new RunSettingsDef
            {
                WarmupSec = Int(m, "warmupSec", P) ?? 0,
                DurationMin = Int(m, "durationMin", P),
                Iterations = Int(m, "iterations", P),
                CoolDownSec = Int(m, "coolDownSec", P) ?? 0,
                SamplingSec = Int(m, "samplingSec", P) ?? 5,
                ConnectionModel = Enum(m, "connectionModel", P, ConnectionModel.PerUser),
                ConnectionPoolSize = Int(m, "connectionPoolSize", P) ?? 100,
                ValidationLevel = Enum(m, "validationLevel", P, ValidationLevel.High),
                TimingDetail = Enum(m, "timingDetail", P, TimingDetail.Statistics),
                MaxErrorDetails = Int(m, "maxErrorDetails", P) ?? 1000,
                StopOnErrors = Int(m, "stopOnErrors", P),
                SaveLogOnFailure = Bool(m, "saveLogOnFailure", P) ?? false,
                SqlTracing = Bool(m, "sqlTracing", P) ?? false
            };
            if ((r.DurationMin is null) == (r.Iterations is null))
                Err(m, "runSettings: specificare esattamente uno fra 'durationMin' e 'iterations'");
            if (r.DurationMin is <= 0) Err(m, "runSettings.durationMin deve essere > 0");
            if (r.Iterations is <= 0) Err(m, "runSettings.iterations deve essere > 0");
            if (r.WarmupSec < 0) Err(m, "runSettings.warmupSec non può essere negativo");
            if (r.CoolDownSec < 0) Err(m, "runSettings.coolDownSec non può essere negativo");
            if (r.SamplingSec <= 0) Err(m, "runSettings.samplingSec deve essere > 0");
            if (r.ConnectionPoolSize <= 0) Err(m, "runSettings.connectionPoolSize deve essere > 0");
            if (r.MaxErrorDetails < 0) Err(m, "runSettings.maxErrorDetails non può essere negativo");
            if (r.StopOnErrors is <= 0) Err(m, "runSettings.stopOnErrors deve essere > 0");

            if (Child(m, "ipSwitching") is YamlMappingNode ip)
            {
                r.IpSwitching.Enabled = Bool(ip, "enabled", "runSettings.ipSwitching") ?? false;
                if (Child(ip, "addresses") is YamlSequenceNode a)
                    r.IpSwitching.Addresses = a.Children.OfType<YamlScalarNode>().Select(x => x.Value ?? "").ToList();
            }
            return r;
        }

        private ScenarioDef ParseScenario(YamlMappingNode m, int index)
        {
            var name = Str(m, "name");
            if (string.IsNullOrWhiteSpace(name)) Err(m, $"scenari[{index}]: 'name' mancante");
            var P = $"scenario '{name}'";
            var s = new ScenarioDef { Name = name ?? "", NewUsersPercent = Dbl(m, "newUsersPercent", P) ?? 100 };
            if (s.NewUsersPercent is < 0 or > 100) Err(m, $"{P}: newUsersPercent deve essere fra 0 e 100");

            if (Child(m, "thinkProfile") is YamlMappingNode tp)
            {
                s.ThinkProfile = new ThinkProfileDef
                {
                    Mode = Enum(tp, "mode", P + ".thinkProfile", ThinkMode.Recorded),
                    AvgMs = Dbl(tp, "avgMs", P + ".thinkProfile") ?? 0,
                    StdDevMs = Dbl(tp, "stdDevMs", P + ".thinkProfile") ?? 0,
                    Scale = Dbl(tp, "scale", P + ".thinkProfile") ?? 100
                };
                if (s.ThinkProfile.AvgMs < 0 || s.ThinkProfile.StdDevMs < 0) Err(tp, $"{P}.thinkProfile: avgMs e stdDevMs non possono essere negativi");
                if (s.ThinkProfile.Scale <= 0) Err(tp, $"{P}.thinkProfile: scale deve essere > 0");
            }

            if (Child(m, "testMix") is YamlMappingNode tm) s.TestMix = ParseMix(tm, P);
            else Err(m, $"{P}: 'testMix' mancante");

            if (Child(m, "loadPattern") is YamlMappingNode lp) s.LoadPattern = ParsePattern(lp, P);
            else Err(m, $"{P}: 'loadPattern' mancante");

            if (Child(m, "browserMix") is YamlSequenceNode bm)
            {
                foreach (var b in bm.Children.OfType<YamlMappingNode>())
                    s.BrowserMix.Add(new BrowserEntryDef { Browser = Str(b, "browser") ?? "", Percent = Dbl(b, "percent", P + ".browserMix") ?? 0 });
                CheckSum(bm, $"{P}.browserMix", s.BrowserMix.Select(x => x.Percent));
            }

            if (Child(m, "networkMix") is YamlSequenceNode nm)
            {
                foreach (var n in nm.Children.OfType<YamlMappingNode>())
                {
                    var e = new NetworkEntryDef
                    {
                        Network = (Str(n, "network") ?? "").ToLowerInvariant(),
                        Percent = Dbl(n, "percent", P + ".networkMix") ?? 0,
                        DownKbps = Int(n, "downKbps", P + ".networkMix"),
                        UpKbps = Int(n, "upKbps", P + ".networkMix"),
                        LatencyMs = Int(n, "latencyMs", P + ".networkMix")
                    };
                    if (e.Network is not ("lan" or "dsl" or "3g" or "custom"))
                        Err(n, $"{P}.networkMix: network '{e.Network}' non valido (lan|dsl|3g|custom)");
                    if (e.Network == "custom" && (e.DownKbps is not > 0 || e.UpKbps is not > 0 || e.LatencyMs is null or < 0))
                        Err(n, $"{P}.networkMix: 'custom' richiede downKbps > 0, upKbps > 0, latencyMs >= 0");
                    s.NetworkMix.Add(e);
                }
                CheckSum(nm, $"{P}.networkMix", s.NetworkMix.Select(x => x.Percent));
            }
            return s;
        }

        private TestMixDef ParseMix(YamlMappingNode m, string P)
        {
            var mix = new TestMixDef
            {
                Model = Enum(m, "model", P + ".testMix", MixModel.Tests),
                TestsPerUserPerHour = Dbl(m, "testsPerUserPerHour", P + ".testMix")
            };
            var testsNode = Child(m, "tests") as YamlSequenceNode;
            if (testsNode is not null)
                foreach (var t in testsNode.Children.OfType<YamlMappingNode>())
                {
                    var test = Str(t, "test");
                    if (string.IsNullOrWhiteSpace(test)) Err(t, $"{P}.testMix: voce senza 'test'");
                    mix.Tests.Add(new MixEntryDef { Test = test ?? "", Percent = Dbl(t, "percent", P + ".testMix") ?? 0 });
                }
            if (mix.Tests.Count == 0) Err((YamlNode?)testsNode ?? m, $"{P}.testMix: 'tests' vuoto o mancante");
            if (mix.Model != MixModel.Sequential) CheckSum((YamlNode?)testsNode ?? m, $"{P}.testMix.tests", mix.Tests.Select(x => x.Percent));
            if (mix.Model == MixModel.Pace && mix.TestsPerUserPerHour is not > 0)
                Err(m, $"{P}.testMix: il modello 'pace' richiede testsPerUserPerHour > 0");
            return mix;
        }

        private LoadPatternDef ParsePattern(YamlMappingNode m, string P)
        {
            var q = P + ".loadPattern";
            var lp = new LoadPatternDef { Type = Enum(m, "type", q, LoadPatternType.Constant) };
            switch (lp.Type)
            {
                case LoadPatternType.Constant:
                    if (Int(m, "users", q) is { } u && u >= 0) lp.Users = u;
                    else Err(m, $"{q}: 'users' mancante o negativo");
                    break;
                case LoadPatternType.Step:
                    lp.Start = Int(m, "start", q) ?? -1;
                    lp.Max = Int(m, "max", q) ?? -1;
                    lp.Step = Int(m, "step", q) ?? 0;
                    lp.StepSec = Int(m, "stepSec", q) ?? 0;
                    if (lp.Start < 0 || lp.Max < lp.Start) Err(m, $"{q}: servono start >= 0 e max >= start");
                    if (lp.Step <= 0 || lp.StepSec <= 0) Err(m, $"{q}: step e stepSec devono essere > 0");
                    break;
                case LoadPatternType.Goal:
                    // Il contratto riporta i campi goal solo in un commento: accetto sia `goal: {...}` annidato sia i campi direttamente nel loadPattern.
                    var g = Child(m, "goal") as YamlMappingNode ?? m;
                    lp.Goal = new GoalDef
                    {
                        Counter = Str(g, "counter") ?? "",
                        Target = Dbl(g, "target", q) ?? 0,
                        Tolerance = Dbl(g, "tolerance", q) ?? 0,
                        MinUsers = Int(g, "minUsers", q) ?? 1,
                        MaxUsers = Int(g, "maxUsers", q) ?? 1,
                        Adjustment = Int(g, "adjustment", q) ?? 1,
                        IntervalSec = Int(g, "intervalSec", q) ?? 30
                    };
                    if (string.IsNullOrWhiteSpace(lp.Goal.Counter)) Err(g, $"{q}: goal senza 'counter'");
                    if (lp.Goal.Tolerance < 0) Err(g, $"{q}: tolerance non può essere negativa");
                    if (lp.Goal.MinUsers < 0 || lp.Goal.MaxUsers < lp.Goal.MinUsers) Err(g, $"{q}: servono minUsers >= 0 e maxUsers >= minUsers");
                    if (lp.Goal.Adjustment <= 0 || lp.Goal.IntervalSec <= 0) Err(g, $"{q}: adjustment e intervalSec devono essere > 0");
                    break;
                case LoadPatternType.Custom:
                    lp.Plugin = Str(m, "plugin");
                    if (string.IsNullOrWhiteSpace(lp.Plugin)) Err(m, $"{q}: profilo custom senza 'plugin'");
                    break;
            }
            return lp;
        }

        private void CheckSum(YamlNode node, string what, IEnumerable<double> percents)
        {
            var list = percents.ToList();
            if (list.Count == 0) return;
            var sum = list.Sum();
            if (list.Any(p => p < 0) || Math.Abs(sum - 100) > 0.01)
                Err(node, $"{what}: le percentuali devono essere >= 0 e sommare 100 (somma attuale {sum.ToString(CultureInfo.InvariantCulture)})");
        }
    }
}
