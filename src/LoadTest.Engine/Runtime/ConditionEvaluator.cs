using System.Globalization;
using LoadTest.Sdk;

namespace LoadTest.Engine.Runtime;

/// <summary>
/// Espressione di `if:` (grammatica NON definita dal contratto v0.1 -> PROPOSTA):
///   &lt;lhs&gt; (==|!=|&lt;|&lt;=|&gt;|&gt;=) &lt;rhs&gt;   oppure solo &lt;valore&gt; (vero se non vuoto, non "false", non "0").
/// L'espressione viene spezzata PRIMA della sostituzione delle variabili, così il contenuto delle variabili non può alterarla.
/// Variabili non risolte valgono stringa vuota. Confronto numerico se entrambi i lati sono numeri, altrimenti ordinale.
/// </summary>
public static class ConditionEvaluator
{
    private static readonly string[] Ops = ["==", "!=", ">=", "<=", ">", "<"];

    public static bool Evaluate(string expression, TestContext ctx)
    {
        var (lhs, op, rhs) = Split(expression);
        if (op is null) return Truthy(Unquote(VariableResolver.Resolve(expression, ctx, strict: false)));

        var l = Unquote(VariableResolver.Resolve(lhs, ctx, strict: false));
        var r = Unquote(VariableResolver.Resolve(rhs, ctx, strict: false));
        var cmp = Compare(l, r);
        return op switch
        {
            "==" => cmp == 0,
            "!=" => cmp != 0,
            ">" => cmp > 0,
            "<" => cmp < 0,
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            _ => false
        };
    }

    private static (string lhs, string? op, string rhs) Split(string expr)
    {
        char quote = '\0';
        var inBraces = false;
        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (inBraces)
            {
                if (c == '}' && i + 1 < expr.Length && expr[i + 1] == '}') { inBraces = false; i++; }
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '{' && i + 1 < expr.Length && expr[i + 1] == '{') { inBraces = true; i++; continue; }
            foreach (var op in Ops)
                if (string.CompareOrdinal(expr, i, op, 0, op.Length) == 0)
                    return (expr[..i], op, expr[(i + op.Length)..]);
        }
        return (expr, null, "");
    }

    private static int Compare(string l, string r)
    {
        if (double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return a.CompareTo(b);
        return string.CompareOrdinal(l, r);
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && (s[0] == '\'' || s[0] == '"') && s[^1] == s[0] ? s[1..^1] : s;
    }

    private static bool Truthy(string s) =>
        !(s.Length == 0 || s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0");
}
