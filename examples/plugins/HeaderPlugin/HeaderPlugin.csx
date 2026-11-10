// Plugin di esempio: aggiunge un header a ogni richiesta.
// Uso in un *.webtest.yaml:
//   plugins:
//     request: [ { name: HeaderPlugin, params: { name: X-Prova, value: "9" } } ]
//
// Using impliciti: System, System.Collections.Generic, System.Linq, System.Threading, System.Threading.Tasks, LoadTest.Sdk.
// Solo classi pubbliche: niente istruzioni top-level.
// Le istanze sono condivise fra gli utenti virtuali: non tenere stato modificabile nei campi.
public sealed class AddHeader : IRequestPlugin
{
    private readonly string _name;
    private readonly string _value;

    // Costruttore con i `params` del YAML (PROPOSTA P1). In alternativa, costruttore senza parametri.
    public AddHeader(IReadOnlyDictionary<string, string> args)
    {
        _name = args.TryGetValue("name", out var n) ? n : "X-Load-Test";
        _value = args.TryGetValue("value", out var v) ? v : "1";
    }

    public void PreRequestDataBinding(RequestContext ctx) { }
    public void PreRequest(RequestContext ctx) => ctx.Headers[_name] = _value;
    public void PostRequest(RequestContext ctx, ResponseContext resp) { }
    public void PostRequestDataBinding(RequestContext ctx, ResponseContext resp) { }
}
