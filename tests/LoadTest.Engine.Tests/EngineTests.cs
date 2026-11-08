using LoadTest.Engine.Model;
using LoadTest.Engine.Runtime;
using LoadTest.Sdk;

namespace LoadTest.Engine.Tests;

internal sealed class FakeHttp : IHttpExecutor
{
    public List<RequestContext> Sent { get; } = [];
    public Func<RequestContext, (int status, string body)> Handler { get; set; } = _ => (200, "");

    public Task<ResponseContext> SendAsync(RequestContext request, CancellationToken ct)
    {
        Sent.Add(request);
        var (status, body) = Handler(request);
        return Task.FromResult(new ResponseContext
        {
            StatusCode = status, StatusHistory = [status], Body = System.Text.Encoding.UTF8.GetBytes(body),
            BodyText = body, FinalUrl = request.Url, ElapsedMicros = 1500
        });
    }
}

internal sealed class CollectingSink : IMeasurementSink
{
    public List<RequestMeasurement> Requests { get; } = [];
    public List<TransactionMeasurement> Transactions { get; } = [];
    public List<TestMeasurement> Tests { get; } = [];
    public void RequestCompleted(RequestMeasurement m) => Requests.Add(m);
    public void TransactionCompleted(TransactionMeasurement m) => Transactions.Add(m);
    public void TestCompleted(TestMeasurement m) => Tests.Add(m);
}

public class EngineTests
{
    private static WebTestContext NewCtx() => new()
    {
        TestName = "t", User = new UserContext { UserId = 7, ScenarioName = "s", AgentName = "a1" }
    };

    private const string Sample = """
        version: 1
        name: Login e ricerca
        baseUrl: https://esempio.it
        context: { lingua: it }
        steps:
          - name: Home
            request: { method: GET, url: / }
            expectedStatus: 200
            extract:
              - { name: csrf, type: regex, pattern: 'name="csrf" value="(.+?)"', group: 1, required: true }
            validate:
              - { type: textContains, value: "Benvenuto", ignoreCase: true }
          - transaction: Autenticazione
            steps:
              - name: Login
                request:
                  method: POST
                  url: /login
                  body:
                    form: { user: "u{{$UserId}}", csrf: "{{csrf}}", l: "{{lingua}}" }
                expectedStatus: 302
          - if: "{{csrf}} != ''"
            then:
              - { name: Profilo, request: { method: GET, url: /profilo } }
            else: [ ]
          - loop:
              for: 3
            steps:
              - { name: Ricerca, request: { method: GET, url: "/search?q=test" } }
        """;

    [Fact]
    public void Loader_parses_contract_sample()
    {
        var def = WebTestLoader.Load(new StringReader(Sample));
        Assert.Equal("https://esempio.it", def.BaseUrl);
        Assert.Equal(4, def.Steps.Count);
        Assert.IsType<TransactionStep>(def.Steps[1]);
        var home = Assert.IsType<RequestStep>(def.Steps[0]);
        Assert.Equal("csrf", home.Extract[0].Name);
        Assert.True(home.Extract[0].Required);
    }

    [Fact]
    public void Loader_rejects_unsupported_major_version()
    {
        Assert.Throws<WebTestLoadException>(() => WebTestLoader.Load(new StringReader("version: 2\nsteps: []")));
    }

    [Theory]
    [InlineData("{{a}} == 'x'", true)]
    [InlineData("{{a}} != 'x'", false)]
    [InlineData("{{n}} > 5", true)]
    [InlineData("{{missing}} == ''", true)]
    [InlineData("{{a}}", true)]
    public void Condition_evaluates(string expr, bool expected)
    {
        var ctx = NewCtx();
        ctx.Set("a", "x"); ctx.Set("n", "10");
        Assert.Equal(expected, ConditionEvaluator.Evaluate(expr, ctx));
    }

    [Fact]
    public void Resolver_handles_predefined_env_and_data()
    {
        var ctx = NewCtx();
        ctx.DataRows["utenti"] = new Dictionary<string, string> { ["username"] = "mario" };
        Assert.Equal("7-mario-s", VariableResolver.Resolve("{{$UserId}}-{{utenti.username}}-{{$ScenarioName}}", ctx));
        Assert.Throws<UnresolvedVariableException>(() => VariableResolver.Resolve("{{nope}}", ctx));
    }

    [Fact]
    public async Task Runner_executes_sample_flow()
    {
        var http = new FakeHttp
        {
            Handler = r => r.Url.EndsWith("/") ? (200, "<p>Benvenuto</p><input name=\"csrf\" value=\"abc\">")
                         : r.Url.EndsWith("/login") ? (302, "")
                         : (200, "ok")
        };
        // la regex del sample cerca name="csrf" value="..." in questo ordine: il fake lo rispetta
        http.Handler = r => r.Url.EndsWith("/") ? (200, "Benvenuto name=\"csrf\" value=\"abc\"")
                         : r.Url.EndsWith("/login") ? (302, "") : (200, "ok");

        var sink = new CollectingSink();
        var runner = new WebTestRunner(new EngineServices { Http = http, Sink = sink });
        var def = WebTestLoader.Load(new StringReader(Sample));
        var result = await runner.RunAsync(def, NewCtx());

        Assert.True(result.Success, string.Join(" | ", result.Failures));
        Assert.Equal(1 + 1 + 1 + 3, http.Sent.Count);
        var login = http.Sent[1];
        Assert.Equal("https://esempio.it/login", login.Url);
        Assert.Contains(new KeyValuePair<string, string>("csrf", "abc"), login.Body!.Form);
        Assert.Contains(new KeyValuePair<string, string>("user", "u7"), login.Body!.Form);
        Assert.Equal("Autenticazione", sink.Requests.Single(m => m.Page == "Login").Transaction);
        Assert.Single(sink.Transactions);
        Assert.Single(sink.Tests);
    }

    [Fact]
    public async Task Runner_stops_on_failed_validation()
    {
        var http = new FakeHttp { Handler = _ => (200, "niente") };
        var sink = new CollectingSink();
        var runner = new WebTestRunner(new EngineServices { Http = http, Sink = sink });
        var def = WebTestLoader.Load(new StringReader(Sample));
        var result = await runner.RunAsync(def, NewCtx());

        Assert.False(result.Success);
        Assert.Single(http.Sent);                 // si ferma alla prima richiesta fallita
        Assert.False(sink.Requests[0].Success);
    }

    [Fact]
    public async Task Validation_level_filters_rules()
    {
        const string yaml = """
            version: 1
            baseUrl: https://x.it
            steps:
              - name: A
                request: { method: GET, url: / }
                validate:
                  - { type: textContains, value: "assente", level: high }
            """;
        var runner = new WebTestRunner(new EngineServices { Http = new FakeHttp(), ValidationLevel = ValidationLevel.Low });
        var result = await runner.RunAsync(WebTestLoader.Load(new StringReader(yaml)), NewCtx());
        Assert.True(result.Success);
    }
}
