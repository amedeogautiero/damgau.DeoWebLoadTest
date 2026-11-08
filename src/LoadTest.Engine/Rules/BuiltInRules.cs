using System.Text.RegularExpressions;
using LoadTest.Sdk;

namespace LoadTest.Engine.Rules;

// ---------------- Estrazione ----------------

/// <summary>args: pattern, group (default 0), ignoreCase.</summary>
public sealed class RegexExtractionRule : IExtractionRule
{
    public ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var pattern = args.Arg("pattern");
        if (pattern is null) return ExtractionResult.Fail("parametro 'pattern' mancante");
        var rx = RegexCache.Get(pattern, args.ArgBool("ignoreCase") ? RegexOptions.IgnoreCase : RegexOptions.None);
        var m = rx.Match(resp.BodyText);
        if (!m.Success) return ExtractionResult.Fail($"nessuna corrispondenza per /{pattern}/");
        var g = m.Groups[args.ArgInt("group", 0)];
        return g.Success ? ExtractionResult.Ok(g.Value) : ExtractionResult.Fail("gruppo non valorizzato");
    }
}

/// <summary>Estrazione fra delimitatori. args: start, end, ignoreCase. (nomi degli argomenti = PROPOSTA)</summary>
public sealed class TextExtractionRule : IExtractionRule
{
    public ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var start = args.Arg("start"); var end = args.Arg("end");
        if (string.IsNullOrEmpty(start) || string.IsNullOrEmpty(end)) return ExtractionResult.Fail("servono 'start' e 'end'");
        var cmp = args.ArgBool("ignoreCase") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var s = resp.BodyText.IndexOf(start, cmp);
        if (s < 0) return ExtractionResult.Fail("delimitatore iniziale non trovato");
        s += start.Length;
        var e = resp.BodyText.IndexOf(end, s, cmp);
        return e < 0 ? ExtractionResult.Fail("delimitatore finale non trovato") : ExtractionResult.Ok(resp.BodyText[s..e]);
    }
}

/// <summary>Valore di un attributo HTML. args: tag, attribute, matchAttribute?, matchValue?, index (default 0).</summary>
public sealed class AttributeExtractionRule : IExtractionRule
{
    public ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var tag = args.Arg("tag"); var attr = args.Arg("attribute");
        if (tag is null || attr is null) return ExtractionResult.Fail("servono 'tag' e 'attribute'");
        return Find(resp.BodyText, tag, attr, args.Arg("matchAttribute"), args.Arg("matchValue"), args.ArgInt("index", 0));
    }

    internal static ExtractionResult Find(string html, string tag, string attr, string? matchAttr, string? matchValue, int index)
    {
        var n = 0;
        foreach (var attrs in HtmlTags.Find(html, tag))
        {
            if (matchAttr is not null && (!attrs.TryGetValue(matchAttr, out var mv) || (matchValue is not null && !string.Equals(mv, matchValue, StringComparison.OrdinalIgnoreCase))))
                continue;
            if (!attrs.TryGetValue(attr, out var value)) continue;
            if (n++ == index) return ExtractionResult.Ok(value);
        }
        return ExtractionResult.Fail($"<{tag} {attr}> non trovato");
    }
}

/// <summary>Campo input nascosto. args: field.</summary>
public sealed class HiddenFieldExtractionRule : IExtractionRule
{
    public ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var field = args.Arg("field");
        if (field is null) return ExtractionResult.Fail("parametro 'field' mancante");
        foreach (var a in HtmlTags.Find(resp.BodyText, "input"))
            if (a.TryGetValue("type", out var t) && t.Equals("hidden", StringComparison.OrdinalIgnoreCase)
                && a.TryGetValue("name", out var n) && n == field)
                return ExtractionResult.Ok(a.GetValueOrDefault("value", ""));
        return ExtractionResult.Fail($"campo nascosto '{field}' non trovato");
    }
}

/// <summary>Valore di un campo di form (input name=...). args: field.</summary>
public sealed class FormFieldExtractionRule : IExtractionRule
{
    public ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var field = args.Arg("field");
        if (field is null) return ExtractionResult.Fail("parametro 'field' mancante");
        foreach (var a in HtmlTags.Find(resp.BodyText, "input"))
            if (a.TryGetValue("name", out var n) && n == field)
                return ExtractionResult.Ok(a.GetValueOrDefault("value", ""));
        return ExtractionResult.Fail($"campo '{field}' non trovato");
    }
}

/// <summary>Header di risposta. args: header (non 'name', già usato per il parametro di destinazione).</summary>
public sealed class HeaderExtractionRule : IExtractionRule
{
    public ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var h = args.Arg("header");
        if (h is null) return ExtractionResult.Fail("parametro 'header' mancante");
        var v = resp.GetHeader(h);
        return v is null ? ExtractionResult.Fail($"header '{h}' assente") : ExtractionResult.Ok(v);
    }
}

// ---------------- Validazione ----------------

/// <summary>args: expected.</summary>
public sealed class StatusValidationRule : IValidationRule
{
    public ValidationResult Validate(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var expected = args.ArgInt("expected", 200);
        return resp.StatusCode == expected ? ValidationResult.Ok() : ValidationResult.Fail($"status {resp.StatusCode}, atteso {expected}");
    }
}

/// <summary>args: value, ignoreCase.</summary>
public sealed class TextContainsValidationRule : IValidationRule
{
    public ValidationResult Validate(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var value = args.Arg("value");
        if (value is null) return ValidationResult.Fail("parametro 'value' mancante");
        var cmp = args.ArgBool("ignoreCase") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return resp.BodyText.Contains(value, cmp) ? ValidationResult.Ok() : ValidationResult.Fail($"testo '{value}' non trovato");
    }
}

/// <summary>args: value (millisecondi).</summary>
public sealed class MaxTimeValidationRule : IValidationRule
{
    public ValidationResult Validate(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var max = args.ArgInt("value", int.MaxValue);
        var ms = resp.ElapsedMicros / 1000;
        return ms <= max ? ValidationResult.Ok() : ValidationResult.Fail($"tempo {ms} ms oltre il limite di {max} ms");
    }
}

/// <summary>args: value (URL atteso, assoluto), ignoreQuery.</summary>
public sealed class ResponseUrlValidationRule : IValidationRule
{
    public ValidationResult Validate(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var expected = args.Arg("value");
        if (expected is null) return ValidationResult.Fail("parametro 'value' mancante");
        string Norm(string u) => args.ArgBool("ignoreQuery") && Uri.TryCreate(u, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path) : u;
        return string.Equals(Norm(resp.FinalUrl), Norm(expected), StringComparison.OrdinalIgnoreCase)
            ? ValidationResult.Ok() : ValidationResult.Fail($"URL finale '{resp.FinalUrl}', atteso '{expected}'");
    }
}

/// <summary>args: name, value? (se omesso basta l'esistenza del campo).</summary>
public sealed class FormFieldValidationRule : IValidationRule
{
    public ValidationResult Validate(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var name = args.Arg("name");
        if (name is null) return ValidationResult.Fail("parametro 'name' mancante");
        var expected = args.Arg("value");
        foreach (var a in HtmlTags.Find(resp.BodyText, "input"))
            if (a.TryGetValue("name", out var n) && n == name)
                return expected is null || a.GetValueOrDefault("value", "") == expected
                    ? ValidationResult.Ok() : ValidationResult.Fail($"campo '{name}' ha valore diverso da '{expected}'");
        return ValidationResult.Fail($"campo '{name}' non trovato");
    }
}

/// <summary>args: tag, minOccurrences (default 1).</summary>
public sealed class TagRequiredValidationRule : IValidationRule
{
    public ValidationResult Validate(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var tag = args.Arg("tag");
        if (tag is null) return ValidationResult.Fail("parametro 'tag' mancante");
        var min = args.ArgInt("minOccurrences", 1);
        var count = HtmlTags.Find(resp.BodyText, tag).Count();
        return count >= min ? ValidationResult.Ok() : ValidationResult.Fail($"<{tag}> trovato {count} volte, minimo {min}");
    }
}

/// <summary>args: tag, attribute, value?.</summary>
public sealed class AttributeRequiredValidationRule : IValidationRule
{
    public ValidationResult Validate(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
    {
        var tag = args.Arg("tag"); var attr = args.Arg("attribute");
        if (tag is null || attr is null) return ValidationResult.Fail("servono 'tag' e 'attribute'");
        var expected = args.Arg("value");
        foreach (var a in HtmlTags.Find(resp.BodyText, tag))
            if (a.TryGetValue(attr, out var v) && (expected is null || v == expected)) return ValidationResult.Ok();
        return ValidationResult.Fail($"<{tag} {attr}> non trovato");
    }
}
