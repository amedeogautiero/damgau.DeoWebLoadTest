# Consegna chat "Controller e agenti", parte 1 (Protocollo) — dove va ogni file

Percorsi relativi alla radice del repo (la cartella con `LoadTest.sln`). Lo zip ha già questa struttura: estrailo sopra la radice.

## NUOVI

| Percorso | Contenuto |
|---|---|
| src/LoadTest.Protocol/LoadTest.Protocol.csproj | progetto (Google.Protobuf, Grpc.Core.Api, Grpc.Tools, riferimento a Metrics) |
| src/LoadTest.Protocol/Protos/agent_link.proto | servizio `AgentLink` e tutti i messaggi (sez. 7) |
| src/LoadTest.Protocol/ProtocolInfo.cs | costanti del protocollo |
| src/LoadTest.Protocol/Handshake.cs | decisione del controller sull'handshake |
| src/LoadTest.Protocol/WireHistogram.cs | serializzazione istogrammi sul filo |
| src/LoadTest.Protocol/MetricsWire.cs | `IntervalSnapshot` ⇄ `MetricsBatch` |
| tests/LoadTest.Engine.Tests/ProtocolTests.cs | 8 test (sez. 11: tutti i test in Engine.Tests) |
| PROPOSTE-CONTRATTO-chat6.md | proposte G1–G8 e richieste alle altre chat (da portare alla chat Architettura) |

## MODIFICATI

- `tests/LoadTest.Engine.Tests/LoadTest.Engine.Tests.csproj`: unica differenza, riga aggiunta nell'ItemGroup
  `<ProjectReference Include="..\..\src\LoadTest.Protocol\LoadTest.Protocol.csproj" />`
- `LoadTest.sln`: non fornito (per non sovrascrivere modifiche delle altre chat). Eseguire:
  `dotnet sln add src/LoadTest.Protocol/LoadTest.Protocol.csproj`

## NON toccati
CONTRATTO.md, Sdk, Engine, Metrics, Reports, Plugins, Plugins.Compilation.

## Dopo la copia
`dotnet restore && dotnet build && dotnet test`

Non compilato da me (nell'ambiente non c'è dotnet). Da verificare al restore: versioni di Google.Protobuf 3.31.1, Grpc.Core.Api 2.71.0, Grpc.Tools 2.72.0 (scelte a memoria).
Da verificare al build: `RecordedValues()`, `RecordValueWithCount()` di HdrHistogram 2.5.0 (stesse chiamate già usate in Reports); i nomi C# generati da protoc (`AgentMsg.MCase`, `AgentMsg.MOneofCase`, `HistogramFormat.VarintDeflateV1`, `HasPage` ecc.).
