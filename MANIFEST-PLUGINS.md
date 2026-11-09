# Consegna chat "Plugin" — fasi 1-3 — dove va ogni file

Percorsi relativi alla radice del repo (la cartella con `LoadTest.sln`). Estrai lo zip sopra la radice.

## NUOVI
| Percorso | Contenuto |
|---|---|
| src/LoadTest.Plugins/PluginCatalog.cs | catalogo pacchetti, riferimenti `Pacchetto/Tipo` |
| src/LoadTest.Plugins/PluginHost.cs | caricamento e istanziazione per kind |
| src/LoadTest.Plugins/CodeStepHost.cs | `ICodeStepHost` per i passi `code:` |
| src/LoadTest.Plugins/PluginWiring.cs | collegamento a EngineServices / LoadTestRunnerOptions |
| src/LoadTest.Plugins.Compilation/* | Roslyn, riferimenti e API vietate, compilazione dei pacchetti .csx (fase 2) |
| tests/LoadTest.Engine.Tests/ScriptCompilerTests.cs | 5 test (fase 2) |
| tests/LoadTest.Engine.Tests/PluginIntegrationTests.cs | 5 test (fase 3) |

## MODIFICATI (sostituire)
- `src/LoadTest.Plugins/LoadTest.Plugins.csproj`: aggiunto il riferimento a LoadTest.Engine (proposta P4).
- `tests/LoadTest.Engine.Tests/LoadTest.Engine.Tests.csproj`: aggiunto il riferimento a LoadTest.Plugins.Compilation.

## DA FARE A MANO
- `dotnet sln add src/LoadTest.Plugins.Compilation/LoadTest.Plugins.Compilation.csproj`
- NON creare `tests/LoadTest.Plugins.Tests` (era un errore della consegna precedente, sez. 11).

## NON toccati
CONTRATTO.md, Sdk, Engine, Metrics, Reports e i file della fase 1 (PluginManifest, PluginPackage, PluginLoader, PluginsTests).

## Dopo la copia
`dotnet restore && dotnet build && dotnet test` — non compilato da me (nell'ambiente non c'è dotnet).
Da verificare: versione di `Microsoft.CodeAnalysis.CSharp` (5.0.0 scelta a memoria).
