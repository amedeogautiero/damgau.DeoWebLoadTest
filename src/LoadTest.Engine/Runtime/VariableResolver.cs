using System.Globalization;
using System.Text.RegularExpressions;
using LoadTest.Sdk;

namespace LoadTest.Engine.Runtime;

public sealed class UnresolvedVariableException(string name) : Exception($"Variabile non risolta: {{{{{name}}}}}")
{
    public string VariableName { get; } = name;
}

/// <summary>Sostituzione {{nome}}, {{sorgente.colonna}}, {{env.NOME}}, {{$Predefinita}}.</summary>
public static class VariableResolver
{
    private static readonly Regex Token = new(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Resolve(string? template, TestContext ctx, bool strict = true)
    {
        if (string.IsNullOrEmpty(template)) return template ?? "";
        return Token.Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            if (TryLookup(name, ctx, out var v)) return v;
            return strict ? throw new UnresolvedVariableException(name) : "";
        });
    }

    public static bool TryLookup(string name, TestContext ctx, out string value)
    {
        value = "";
        if (name.StartsWith('$')) return TryPredefined(name, ctx, out value);

        if (name.StartsWith("env.", StringComparison.Ordinal))
        {
            var env = Environment.GetEnvironmentVariable(name[4..]);
            if (env is null) return false;
            value = env;
            return true;
        }

        var dot = name.IndexOf('.');
        if (dot > 0 && ctx is WebTestContext w
            && w.DataRows.TryGetValue(name[..dot], out var row)
            && row.TryGetValue(name[(dot + 1)..], out var cell))
        {
            value = cell;
            return true;
        }

        if (ctx.Parameters.TryGetValue(name, out var param))
        {
            value = param;
            return true;
        }
        return false;
    }

    private static bool TryPredefined(string name, TestContext ctx, out string value)
    {
        var inv = CultureInfo.InvariantCulture;
        string? v = name switch
        {
            "$WebTestIteration" => ctx.Iteration.ToString(inv),
            "$LoadTestUserContext" => ctx.User.UserContextId,
            "$AgentId" => ctx.User.AgentId,
            "$AgentName" => ctx.User.AgentName,
            "$ControllerName" => ctx.User.ControllerName,
            "$UserId" => ctx.User.UserId.ToString(inv),
            "$ScenarioName" => ctx.User.ScenarioName,
            "$TestName" => ctx.TestName,
            _ => null
        };
        value = v ?? "";
        return v is not null;
    }
}
