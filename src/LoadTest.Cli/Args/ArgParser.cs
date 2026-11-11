using System.Globalization;

namespace LoadTest.Cli.Args;

public sealed record OptionSpec(string Name, bool TakesValue = true);

public sealed class ParsedArgs
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];
    /// <summary>Opzioni `--Sezione:Chiave=valore` passate tali e quali alla configurazione di agente/controller.</summary>
    public List<string> Raw { get; } = [];

    internal void AddValue(string name, string value)
    {
        if (!_values.TryGetValue(name, out var l)) _values[name] = l = [];
        l.Add(value);
    }

    internal void AddFlag(string name) => _flags.Add(name);

    public string? Get(string name) => _values.TryGetValue(name, out var l) ? l[^1] : null;
    public IReadOnlyList<string> GetAll(string name) => _values.TryGetValue(name, out var l) ? l : [];
    public bool Flag(string name) => _flags.Contains(name);

    public double? GetDouble(string name) =>
        Get(name) is { } v
            ? (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new CliException(ExitCodes.Usage, $"--{name}: '{v}' non è un numero"))
            : null;

    public long? GetLong(string name) =>
        Get(name) is { } v
            ? (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new CliException(ExitCodes.Usage, $"--{name}: '{v}' non è un intero"))
            : null;
}

public static class ArgParser
{
    /// <summary>Opzioni `--nome valore`, `--nome=valore`, flag `--nome`. Opzioni sconosciute = errore (evita refusi silenziosi in CI).</summary>
    public static ParsedArgs Parse(IReadOnlyList<string> args, IReadOnlyList<OptionSpec> specs, bool allowRaw = false)
    {
        var result = new ParsedArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (token == "--")
            {
                for (i++; i < args.Count; i++) result.Positional.Add(args[i]);
                break;
            }
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positional.Add(token);
                continue;
            }

            var body = token[2..];
            string? inlineValue = null;
            var eq = body.IndexOf('=');
            if (eq >= 0) { inlineValue = body[(eq + 1)..]; body = body[..eq]; }

            if (allowRaw && body.Contains(':'))
            {
                if (inlineValue is null) throw new CliException(ExitCodes.Usage, $"{token}: usare --Chiave:Sezione=valore");
                result.Raw.Add($"--{body}={inlineValue}");
                continue;
            }

            var spec = specs.FirstOrDefault(s => string.Equals(s.Name, body, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliException(ExitCodes.Usage, $"opzione sconosciuta '--{body}'");
            if (spec.TakesValue)
            {
                var value = inlineValue ?? (i + 1 < args.Count ? args[++i] : throw new CliException(ExitCodes.Usage, $"--{body}: manca il valore"));
                result.AddValue(spec.Name, value);
            }
            else
            {
                if (inlineValue is not null && !bool.TryParse(inlineValue, out var on))
                    throw new CliException(ExitCodes.Usage, $"--{body}: valore '{inlineValue}' non valido (true|false)");
                else if (inlineValue is null || bool.Parse(inlineValue)) result.AddFlag(spec.Name);
            }
        }
        return result;
    }
}
