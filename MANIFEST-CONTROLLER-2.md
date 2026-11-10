# Consegna chat "Controller e agenti", parte 2 (Agente) — dove va ogni file

Percorsi relativi alla radice del repo (la cartella con `LoadTest.sln`). Lo zip ha già questa struttura: estrailo sopra la radice.

## NUOVI

| Percorso | Contenuto |
|---|---|
| src/LoadTest.Agent/LoadTest.Agent.csproj | progetto Exe (Hosting, Grpc.Net.Client; riferimenti a Engine, Plugins, Protocol) |
| src/LoadTest.Agent/Program.cs | host del worker |
| src/LoadTest.Agent/AgentOptions.cs | configurazione, `AgentRejectedException` |
| src/LoadTest.Agent/AgentWorker.cs | connessione e riconnessione con backoff |
| src/LoadTest.Agent/IControllerLink.cs, GrpcControllerLink.cs | stream verso il controller (gRPC, token, mTLS) |
| src/LoadTest.Agent/AgentSession.cs | handshake, preparazione, avvio, Update/Stop, invio metriche, Heartbeat |
| src/LoadTest.Agent/RunWorkspace.cs | validazione del manifest, cartella di lavoro |
| src/LoadTest.Agent/RunExecutor.cs | caricamento/validazione del load test e esecuzione con `LoadTestRunner` |
| src/LoadTest.Agent/AgentLoadProfile.cs | `LoadCoordinator`: quota, tetto, UpdateLoad, stop graceful |
| src/LoadTest.Agent/AgentPlugins.cs | plugin loadTest/loadProfile dalla cache |
| src/LoadTest.Agent/AgentCaches.cs, Transfer/*.cs | cache per hash, ricezione chunk, zip sicuro, percorsi sicuri |
| src/LoadTest.Agent/SystemSampler.cs, PlatformInfo.cs | CPU/RAM, RID, id dell'agente |
| tests/LoadTest.Engine.Tests/AgentTests.cs | 13 metodi di test (un Theory con 5 casi), compresa una sessione completa in memoria con due run (sez. 11) |
| PROPOSTE-CONTRATTO-chat6-parte2.md | proposte G9–G14 e richieste all'Engine |

## MODIFICATI

- `tests/LoadTest.Engine.Tests/LoadTest.Engine.Tests.csproj`: unica differenza, riga aggiunta
  `<ProjectReference Include="..\..\src\LoadTest.Agent\LoadTest.Agent.csproj" />` (oltre a quella di Protocol, parte 1).
- `LoadTest.sln`: non fornito. Eseguire `dotnet sln add src/LoadTest.Agent/LoadTest.Agent.csproj`
  (e, se non fatto, `dotnet sln add src/LoadTest.Protocol/LoadTest.Protocol.csproj`).

## NON toccati
CONTRATTO.md, Sdk, Engine, Metrics, Reports, Plugins, Plugins.Compilation, Protocol.

## Dopo la copia
`dotnet restore && dotnet build && dotnet test`
Avvio manuale: `dotnet run --project src/LoadTest.Agent -- --Agent:ControllerUrl=https://host:5001 --Agent:Token=...`

Non compilato da me (nell'ambiente non c'è dotnet). Da verificare al restore: Microsoft.Extensions.Hosting 10.0.0, Grpc.Net.Client 2.71.0 (scelte a memoria).
Da verificare al build: `X509CertificateLoader.LoadPkcs12FromFile` (.NET 9+), `IClientStreamWriter.WriteAsync(msg, ct)`, `ReadAllAsync` di Grpc.Core, riferimento a un progetto Exe dal progetto di test.
