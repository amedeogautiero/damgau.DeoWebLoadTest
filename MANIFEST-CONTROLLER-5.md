# Consegna chat "Controller e agenti", parte 5 (Publish e servizi OS) — dove va ogni file

Percorsi relativi alla radice del repo (la cartella con `LoadTest.sln`). Lo zip ha già questa struttura: estrailo sopra la radice.

## NUOVI

| Percorso | Contenuto |
|---|---|
| src/LoadTest.Agent/Service/ServiceModel.cs | tipi del piano di installazione (passi, percorsi, opzioni) |
| src/LoadTest.Agent/Service/ServicePlanner.cs | piano puro per Linux (systemd), macOS (launchd), Windows (SCM); contenuti di unit, plist, appsettings.json |
| src/LoadTest.Agent/Service/PlanExecutor.cs | esecuzione del piano, `ICommandRunner` |
| src/LoadTest.Agent/Service/ServiceCommands.cs | `loadtest-agent install | uninstall` |
| build/publish.sh, build/publish.ps1 | publish self-contained/file singolo per RID, pacchetti e SHA256SUMS |
| deploy/README.md | guida: pacchetti, installazione, configurazione, taratura, controller come servizio, problemi |
| tests/LoadTest.Engine.Tests/ServiceTests.cs | 13 test: piani per i tre sistemi, validazione, comandi, esecutore, eviction della cache (sez. 11) |
| PROPOSTE-CONTRATTO-chat6-parte5.md | proposte G25–G29, limiti, stato della componente 6 |

## MODIFICATI (sostituire)

- `src/LoadTest.Agent/LoadTest.Agent.csproj`: `AssemblyName` = `loadtest-agent`, `InvariantGlobalization`, proprietà di publish per RID, pacchetti `Microsoft.Extensions.Hosting.Systemd` e `.WindowsServices` 10.0.0.
- `src/LoadTest.Agent/Program.cs`: instrada `install`/`uninstall`, altrimenti avvia l'agente.
- `src/LoadTest.Agent/AgentHost.cs`: radice del contenuto = cartella dell'eseguibile; integrazione con systemd e SCM.
- `src/LoadTest.Agent/AgentOptions.cs`: aggiunte `CacheMaxBytes`, `CacheMaxAgeDays`.
- `src/LoadTest.Agent/AgentCaches.cs`: aggiunti `Touch` e `Evict`.
- `src/LoadTest.Agent/Transfer/ContentCache.cs`: aggiunta la proprietà `Root`.
- `src/LoadTest.Agent/AgentSession.cs`: in `OnPrepareAsync` chiama `MaintainCache(entries)` (segna come usate ed elimina).
- `src/LoadTest.Controller/ControllerHost.cs`: radice del contenuto, integrazione con systemd e SCM.
- `src/LoadTest.Controller/LoadTest.Controller.csproj`: pacchetti Systemd e WindowsServices.
- `src/LoadTest.Cli/LoadTest.Cli.csproj`: `InvariantGlobalization` e proprietà di publish per RID.
- `LoadTest.sln`: nessuna modifica rispetto alla parte 4.

## NON toccati
CONTRATTO.md, Sdk, Engine, Metrics, Reports, Plugins, Plugins.Compilation, Protocol; `tests/LoadTest.Engine.Tests/LoadTest.Engine.Tests.csproj` (invariato dalla parte 4).

## Dopo la copia
`dotnet restore && dotnet build && dotnet test`
Pacchetti: `build/publish.sh 0.1.0` (o `build\publish.ps1`). Prova dell'installazione senza effetti:
`./loadtest-agent install --controller https://controller:5001 --dry-run`

Non compilato da me (nell'ambiente non c'è dotnet): né il codice né gli script sono stati eseguiti.
Da verificare al restore: Microsoft.Extensions.Hosting.Systemd e .WindowsServices 10.0.0 (scelti a memoria).
Da verificare al build: `services.AddWindowsService(...)` / `AddSystemd()` su `HostApplicationBuilder` e `WebApplicationBuilder`, `FileStreamOptions.UnixCreateMode`, `[GeneratedRegex]` in una classe `static partial`, raw string `$"""` con `{p.Campo}` nell'unit.
