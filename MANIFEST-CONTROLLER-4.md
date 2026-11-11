# Consegna chat "Controller e agenti", parte 4 (CLI e CI) — dove va ogni file

Percorsi relativi alla radice del repo (la cartella con `LoadTest.sln`). Lo zip ha già questa struttura: estrailo sopra la radice.

## NUOVI

| Percorso | Contenuto |
|---|---|
| src/LoadTest.Cli/LoadTest.Cli.csproj | progetto Exe, `AssemblyName` = `loadtest` (riferimenti a Agent, Controller, Engine, Metrics, Plugins, Reports) |
| src/LoadTest.Cli/Program.cs, CliApp.cs | ingresso, dispatch, codici di uscita, aiuto |
| src/LoadTest.Cli/Args/ArgParser.cs | parsing delle opzioni |
| src/LoadTest.Cli/Commands/*.cs | run, validate, report, agent/controller, agents/status/stop |
| src/LoadTest.Cli/Local/LocalRunner.cs | run locale con `LoadTestRunner` e SQLite |
| src/LoadTest.Cli/Remote/*.cs | client dell'API, run remota, zip del progetto |
| src/LoadTest.Cli/Ci/*.cs | soglie, tabella, JSON, JUnit |
| src/LoadTest.Agent/AgentHost.cs | costruzione/avvio dell'host dell'agente |
| src/LoadTest.Controller/ControllerHost.cs | costruzione/avvio dell'host del controller |
| src/LoadTest.Controller/Api/SummaryDto.cs, ProjectStore.cs | JSON del riassunto, progetti caricati |
| tests/LoadTest.Engine.Tests/CliTests.cs | 12 test (sez. 11) |
| PROPOSTE-CONTRATTO-chat6-parte4.md | proposte G22–G24, esempio CI |

## MODIFICATI (sostituire)

- `src/LoadTest.Agent/Program.cs`: ridotto a `await AgentHost.RunAsync(args);`.
- `src/LoadTest.Controller/Program.cs`: ridotto a `await ControllerHost.RunAsync(args);` (la costruzione è in `ControllerHost.cs`).
- `src/LoadTest.Controller/ControllerOptions.cs`: aggiunte `MaxProjectUploadBytes`, `ProjectRetentionHours`.
- `src/LoadTest.Controller/Api/ApiEndpoints.cs`: aggiunti `/projects`, `projectId` in `/runs`, `/runs/{id}/summary(.csv)`; `/agents` restituisce `AgentDto`.
- `tests/LoadTest.Engine.Tests/LoadTest.Engine.Tests.csproj`: riga aggiunta `<ProjectReference Include="..\..\src\LoadTest.Cli\LoadTest.Cli.csproj" />`.
- `LoadTest.sln`: non fornito. Eseguire `dotnet sln add src/LoadTest.Cli/LoadTest.Cli.csproj`
  (e, se non fatto, quelli di Protocol, Agent e Controller).

## NON toccati
CONTRATTO.md, Sdk, Engine, Metrics, Reports, Plugins, Plugins.Compilation, Protocol.

## Dopo la copia
`dotnet restore && dotnet build && dotnet test`
Prova rapida: `dotnet run --project src/LoadTest.Cli -- validate percorso/x.loadtest.yaml` e
`dotnet run --project src/LoadTest.Cli -- run percorso/x.loadtest.yaml --db r.db`

Non compilato da me (nell'ambiente non c'è dotnet). Nessun pacchetto nuovo.
Da verificare al build: `FrameworkReference Microsoft.AspNetCore.App` in un progetto Exe che referenzia un progetto Web, `[GeneratedRegex]` in `ProjectStore`, `JsonContent.Create(..., options:)` nei test, le collection expression passate a `Concat`/`Parse` (C# 12).
