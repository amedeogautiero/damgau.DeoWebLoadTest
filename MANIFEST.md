# Consegna chat "Load test locale" — dove va ogni file

Percorsi relativi alla radice del repo (la cartella con `LoadTest.sln`).
Lo zip `loadtest-locale-delivery.zip` ha già questa struttura: estrailo sopra la radice del repo.

## NUOVI (non esistono nel repo, si aggiungono)

| Percorso | Progetto |
|---|---|
| src/LoadTest.Metrics/LoadTest.Metrics.csproj | LoadTest.Metrics (nuovo progetto) |
| src/LoadTest.Metrics/MetricKey.cs | LoadTest.Metrics |
| src/LoadTest.Metrics/MetricsRecorder.cs | LoadTest.Metrics |
| src/LoadTest.Engine/LoadTesting/LoadTestDefinition.cs | LoadTest.Engine |
| src/LoadTest.Engine/LoadTesting/LoadTestLoader.cs | LoadTest.Engine |
| src/LoadTest.Engine/LoadTesting/LoadProfiles.cs | LoadTest.Engine |
| src/LoadTest.Engine/LoadTesting/TestMixSelector.cs | LoadTest.Engine |
| src/LoadTest.Engine/LoadTesting/ThinkProfileProvider.cs | LoadTest.Engine |
| src/LoadTest.Engine/LoadTesting/BrowserUserAgentPlugin.cs | LoadTest.Engine |
| src/LoadTest.Engine/LoadTesting/LoadTestEvents.cs | LoadTest.Engine |
| src/LoadTest.Engine/LoadTesting/LoadTestRunner.cs | LoadTest.Engine |
| tests/LoadTest.Engine.Tests/LoadTestingTests.cs | LoadTest.Engine.Tests |

## MODIFICATI (esistono già: sostituire, o applicare la sola modifica indicata)

1. `src/LoadTest.Engine/LoadTest.Engine.csproj` — unica differenza: riga aggiunta nell'ItemGroup
   `<ProjectReference Include="..\LoadTest.Metrics\LoadTest.Metrics.csproj" />`
2. `LoadTest.sln` — uniche differenze: progetto `LoadTest.Metrics` (GUID {44444444-4444-4444-4444-444444444444})
   con le 4 righe di configurazione Debug/Release. In alternativa al file:
   `dotnet sln add src/LoadTest.Metrics/LoadTest.Metrics.csproj`

## NON toccati
Tutto il resto di Sdk ed Engine (HttpClientExecutor, Rules, Model, Runtime, ecc.) è invariato.

## Dopo la copia
dotnet restore && dotnet build && dotnet test
(Non compilato da me: nell'ambiente non c'è dotnet. Il pacchetto HdrHistogram 2.5.0 è da verificare al restore.)
