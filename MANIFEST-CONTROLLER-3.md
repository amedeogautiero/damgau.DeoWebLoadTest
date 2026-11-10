# Consegna chat "Controller e agenti", parte 3 (Controller) — dove va ogni file

Percorsi relativi alla radice del repo (la cartella con `LoadTest.sln`). Lo zip ha già questa struttura: estrailo sopra la radice.

## NUOVI

| Percorso | Contenuto |
|---|---|
| src/LoadTest.Controller/LoadTest.Controller.csproj | progetto Web (Grpc.AspNetCore; riferimenti a Engine, Metrics, Plugins, Protocol, Reports) |
| src/LoadTest.Controller/Program.cs, KestrelSetup.cs | host: gRPC agenti + API JSON, TLS/mTLS |
| src/LoadTest.Controller/ControllerOptions.cs, ControllerAuth.cs | configurazione (sezione `Controller`), autenticazione degli agenti |
| src/LoadTest.Controller/Agents/*.cs | `AgentConnection`, `AgentRegistry` |
| src/LoadTest.Controller/Grpc/*.cs | `ControllerHub` (handshake e inoltro), `AgentLinkService` (servizio gRPC) |
| src/LoadTest.Controller/Rig/*.cs | sezione `rig` del load test, pianificazione degli agenti, ripartizione intera |
| src/LoadTest.Controller/Packaging/*.cs | raccolta dei file e dei plugin, zip/gzip per hash, partizione CSV, `IPluginCompiler` |
| src/LoadTest.Controller/Runs/*.cs | `RunOrchestrator`, `RunManager`, `LoadDriver`, `ScriptRunner`, stato e controllo della run |
| src/LoadTest.Controller/Api/ApiEndpoints.cs | API JSON per CLI e VS Code |
| tests/LoadTest.Engine.Tests/ControllerTests.cs | 13 test, comprese due run distribuite in memoria con agenti veri (sez. 11) |
| PROPOSTE-CONTRATTO-chat6-parte3.md | proposte G15–G21 |

## MODIFICATI

- `src/LoadTest.Protocol/Protos/agent_link.proto`: unica differenza, campo `int32 iterations = 7;` in `StartRun`.
- `src/LoadTest.Agent/AgentSession.cs`: unica differenza, in `OnStart` applica `StartRun.iterations` a `RunSettings.Iterations` (se il file ha `iterations`).
- `src/LoadTest.Agent/RunExecutor.cs`: unica differenza, in `Prepare` pone `RunSettings.StopOnErrors = null` (lo valuta il controller).
- `tests/LoadTest.Engine.Tests/LoadTest.Engine.Tests.csproj`: riga aggiunta `<ProjectReference Include="..\..\src\LoadTest.Controller\LoadTest.Controller.csproj" />` (oltre a quelle di Protocol e Agent delle parti 1 e 2).
- `LoadTest.sln`: non fornito. Eseguire `dotnet sln add src/LoadTest.Controller/LoadTest.Controller.csproj`
  (e, se non fatto, quelli di `LoadTest.Protocol` e `LoadTest.Agent`).

## NON toccati
CONTRATTO.md, Sdk, Engine, Metrics, Reports, Plugins, Plugins.Compilation.

## Dopo la copia
`dotnet restore && dotnet build && dotnet test`
Avvio manuale (sviluppo, senza TLS):
`dotnet run --project src/LoadTest.Controller -- --Controller:ListenUrl=http://0.0.0.0:5001 --Controller:AllowAnonymousAgents=true`
poi un agente con `--Agent:ControllerUrl=http://localhost:5001` e
`curl -X POST http://127.0.0.1:5000/api/runs -H "Content-Type: application/json" -d '{"loadTestPath":"/percorso/x.loadtest.yaml"}'`

Non compilato da me (nell'ambiente non c'è dotnet). Da verificare al restore: Grpc.AspNetCore 2.71.0 (scelta a memoria).
Da verificare al build: `ServerCallContext.GetHttpContext()`, `MapGroup(...).AddEndpointFilter`, `RequireHost("*:porta")`, `X509CertificateLoader` (.NET 9+), l'implementazione di `IServerStreamWriter<T>` nei test (membri richiesti dall'interfaccia), il riferimento dal progetto di test a un progetto Web.
