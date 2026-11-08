using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace LoadTest.Engine.Rules;

internal static class ArgExtensions
{
    public static string? Arg(this IReadOnlyDictionary<string, string> a, string name) => a.TryGetValue(name, out var v) ? v : null;
    public static int ArgInt(this IReadOnlyDictionary<string, string> a, string name, int def) =>
        a.TryGetValue(name, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : def;
    public static bool ArgBool(this IReadOnlyDictionary<string, string> a, string name, bool def = false) =>
        a.TryGetValue(name, out var v) && bool.TryParse(v, out var b) ? b : def;
}

internal static class RegexCache
{
    private static readonly ConcurrentDictionary<(string, RegexOptions), Regex> Cache = new();
    public static Regex Get(string pattern, RegexOptions options) =>
        Cache.GetOrAdd((pattern, options), k => new Regex(k.Item1, k.Item2 | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)));
}

internal static class HtmlTags
{
    private static readonly Regex AttrRx = new(@"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", RegexOptions.Compiled);

    /// <summary>Elenca i tag `tag` con i loro attributi (parser a regex: non gestisce '&gt;' dentro i valori degli attributi).</summary>
    public static IEnumerable<Dictionary<string, string>> Find(string html, string tag)
    {
        var rx = RegexCache.Get($@"<{Regex.Escape(tag)}\b([^>]*)>", RegexOptions.IgnoreCase);
        foreach (Match m in rx.Matches(html))
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in AttrRx.Matches(m.Groups[1].Value))
            {
                var raw = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                attrs[a.Groups[1].Value] = WebUtility.HtmlDecode(raw);
            }
            yield return attrs;
        }
    }
}
