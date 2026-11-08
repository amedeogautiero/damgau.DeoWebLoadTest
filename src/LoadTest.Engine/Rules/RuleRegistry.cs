using LoadTest.Sdk;

namespace LoadTest.Engine.Rules;

/// <summary>Regole per `type`. Il tipo `custom` usa l'argomento `rule` per scegliere una regola registrata (es. da plugin) -> PROPOSTA.</summary>
public sealed class RuleRegistry
{
    private readonly Dictionary<string, IExtractionRule> _extraction = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IValidationRule> _validation = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string type, IExtractionRule rule) => _extraction[type] = rule;
    public void Register(string type, IValidationRule rule) => _validation[type] = rule;

    public IExtractionRule GetExtraction(string type, IReadOnlyDictionary<string, string> args)
    {
        var key = type.Equals("custom", StringComparison.OrdinalIgnoreCase) ? args.Arg("rule") ?? "" : type;
        return _extraction.TryGetValue(key, out var r) ? r
            : throw new InvalidOperationException($"Regola di estrazione '{key}' non registrata");
    }

    public IValidationRule GetValidation(string type, IReadOnlyDictionary<string, string> args)
    {
        var key = type.Equals("custom", StringComparison.OrdinalIgnoreCase) ? args.Arg("rule") ?? "" : type;
        return _validation.TryGetValue(key, out var r) ? r
            : throw new InvalidOperationException($"Regola di validazione '{key}' non registrata");
    }

    public static RuleRegistry CreateDefault()
    {
        var r = new RuleRegistry();
        r.Register("regex", new RegexExtractionRule());
        r.Register("text", new TextExtractionRule());
        r.Register("attribute", new AttributeExtractionRule());
        r.Register("hidden", new HiddenFieldExtractionRule());
        r.Register("formField", new FormFieldExtractionRule());
        r.Register("header", new HeaderExtractionRule());

        r.Register("status", new StatusValidationRule());
        r.Register("textContains", new TextContainsValidationRule());
        r.Register("maxTimeMs", new MaxTimeValidationRule());
        r.Register("responseUrl", new ResponseUrlValidationRule());
        r.Register("formField", new FormFieldValidationRule());
        r.Register("tagRequired", new TagRequiredValidationRule());
        r.Register("attributeRequired", new AttributeRequiredValidationRule());
        // responseTimeGoal: non implementata (semantica da definire, richiede percentili aggregati dal controller)
        return r;
    }
}
