using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace LoadTest.Controller.Rig;

public sealed class RigAgentDef
{
    public string Name { get; set; } = "";
    public double Weight { get; set; } = 1;
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int? MaxUsers { get; set; }
}

public sealed class RigDeploymentDef
{
    public string Path { get; set; } = "";
    public bool Partition { get; set; }
}

/// <summary>Sezione `rig` di *.loadtest.yaml (CONTRATTO sez. 5): agenti con peso, attributi e maxUsers, deployment, setup e cleanup.</summary>
public sealed class RigDefinition
{
    public List<RigAgentDef> Agents { get; } = [];
    public List<RigDeploymentDef> Deployment { get; } = [];
    public string? SetupScript { get; set; }
    public string? CleanupScript { get; set; }
}

public sealed class RigLoadException(string origin, IReadOnlyList<string> errors) : Exception($"{origin}: {string.Join("; ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class RigLoader
{
    public static RigDefinition LoadFile(string path)
    {
        using var reader = File.OpenText(path);
        return Load(reader, path);
    }

    public static RigDefinition Load(TextReader reader, string origin = "<memoria>")
    {
        var stream = new YamlStream();
        try { stream.Load(reader); }
        catch (YamlException ex) { throw new RigLoadException(origin, [$"YAML non valido ({ex.Message})"]); }

        var rig = new RigDefinition();
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return rig;
        if (Child(root, "rig") is not YamlMappingNode m) return rig;

        var errors = new List<string>();
        rig.SetupScript = Str(m, "setupScript");
        rig.CleanupScript = Str(m, "cleanupScript");

        if (Child(m, "agents") is YamlSequenceNode agents)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in agents.Children.OfType<YamlMappingNode>())
            {
                var a = new RigAgentDef { Name = Str(item, "name") ?? "" };
                if (string.IsNullOrWhiteSpace(a.Name)) errors.Add("rig.agents: voce senza 'name'");
                else if (!names.Add(a.Name)) errors.Add($"rig.agents: nome duplicato '{a.Name}'");
                if (Str(item, "weight") is { } w)
                {
                    if (double.TryParse(w, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wd) && wd > 0) a.Weight = wd;
                    else errors.Add($"rig.agents '{a.Name}': weight '{w}' deve essere un numero > 0");
                }
                if (Str(item, "maxUsers") is { } mu)
                {
                    if (int.TryParse(mu, out var mi) && mi >= 0) a.MaxUsers = mi;
                    else errors.Add($"rig.agents '{a.Name}': maxUsers '{mu}' non valido");
                }
                if (Child(item, "attributes") is YamlMappingNode attrs)
                    foreach (var (k, v) in attrs.Children)
                        a.Attributes[((YamlScalarNode)k).Value ?? ""] = (v as YamlScalarNode)?.Value ?? "";
                rig.Agents.Add(a);
            }
        }

        if (Child(m, "deployment") is YamlSequenceNode dep)
            foreach (var item in dep.Children.OfType<YamlMappingNode>())
            {
                var d = new RigDeploymentDef { Path = Str(item, "path") ?? "" };
                if (string.IsNullOrWhiteSpace(d.Path)) { errors.Add("rig.deployment: voce senza 'path'"); continue; }
                d.Partition = bool.TryParse(Str(item, "partition"), out var p) && p;
                rig.Deployment.Add(d);
            }

        if (errors.Count > 0) throw new RigLoadException(origin, errors);
        return rig;
    }

    private static YamlNode? Child(YamlMappingNode m, string key) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? Str(YamlMappingNode m, string key) => Child(m, key) is YamlScalarNode s ? s.Value : null;
}
