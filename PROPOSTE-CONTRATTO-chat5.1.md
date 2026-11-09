# Proposte di modifica al CONTRATTO v0.1 — dalla chat "Plugin" (componente 5)

Da portare alla chat **Architettura**. Nulla è stato applicato a CONTRATTO.md, né a Sdk/Engine. Il codice della chat 5 è scritto sulle bozze sotto: se una proposta cambia, cambio il codice.

## Consegna fase 1 (non compilata: nell'ambiente non c'è dotnet)

NUOVI: `src/LoadTest.Plugins/{LoadTest.Plugins.csproj, PluginManifest.cs, PluginPackage.cs, PluginLoader.cs}`, `tests/LoadTest.Plugins.Tests/{...csproj, PluginsTests.cs}`.
DA FARE A MANO: `dotnet sln add src/LoadTest.Plugins/LoadTest.Plugins.csproj tests/LoadTest.Plugins.Tests/LoadTest.Plugins.Tests.csproj` (LoadTest.sln non ha ancora i due progetti).
NON toccati: CONTRATTO.md, Sdk, Engine, Metrics, Reports.
Dopo la copia: `dotnet restore && dotnet build && dotnet test`.

## A. Modifiche richieste

**P1 — Come arrivano i `params` ai plugin (sez. 4 e 6).** Il web test dichiara `plugins.webTest: [{ name, params }]`, ma `IWebTestPlugin`/`IRequestPlugin` non hanno `Initialize` né parametri (solo `ILoadTestPlugin` li riceve). Bozza implementata: costruttore pubblico senza parametri, oppure con un solo `IReadOnlyDictionary<string,string>`. Alternativa: interfaccia opzionale `IConfigurablePlugin { void Configure(IReadOnlyDictionary<string,string> args); }` nell'SDK.

**P2 — Cosa sta in un `.csx` (sez. 1 e 6).** Propongo: il `.csx` contiene classi pubbliche che implementano le interfacce SDK (come una DLL scritta in script), compilate dal controller con Roslyn in un assembly; nessuna istruzione top-level come punto d'ingresso. Il `plugin.yaml` resta obbligatorio per i pacchetti. Per `code: plugins/Custom.csx` (sez. 4), che non ha manifest: il file singolo è trattato come pacchetto implicito `kinds: [codedTest]`, `sdk` = quello corrente.

**P3 — `ICodedTest` non può fare richieste (sez. 6).** `RunAsync(TestContext ctx)` non dà accesso a un esecutore HTTP, quindi un test in codice (e la riga di sez. 10 "generazione del codice dal test dichiarativo") non può inviare richieste né registrare misure. Proposta: aggiungere in SDK un'interfaccia `IWebTestHost` (Send di una richiesta con regole, transazioni, misure) raggiungibile da `TestContext`, oppure passarla come secondo parametro. Serve la decisione con la chat 2.

**P4 — Dove vive `ICodeStepHost` (sez. 3).** Oggi è in `LoadTest.Engine.Runtime`; `Plugins` dipenderebbe da `Engine` (la sez. 2 dà solo dipendenza dalla chat 1/SDK). Proposta: spostare `ICodeStepHost` in `LoadTest.Sdk` oppure far dipendere `Plugins` da `Engine` (aggiornando la sez. 3).

**P5 — Come un nome di plugin diventa un oggetto (sez. 4, 5, 6).** Riferimenti per nome: `plugins.webTest[].name`, `loadTest[].name`, `loadPattern.plugin`, regole `type: custom` con arg `rule`. Proposta: il nome è quello del `plugin.yaml`; se il pacchetto contiene più tipi dello stesso kind, nel riferimento si usa `Pacchetto/NomeTipo` e, per le regole `custom`, `rule` = `NomeTipo` (o `Pacchetto/NomeTipo`). Senza ambiguità, basta il nome del pacchetto. Va deciso dove stanno i pacchetti sul disco (proposta: cartella `plugins/` accanto al `.webtest.yaml`/`.loadtest.yaml`, una sottocartella per pacchetto).

**P6 — `dataSource` plugin (sez. 4 e 6).** `kinds` ammette `dataSource`, ma `data.<nome>.type` elenca solo csv|xml|database|excel. Proposta: aggiungere `type: custom` con `plugin: NomeTipo`.

**P7 — Hash del pacchetto (sez. 1 e 7).** "Hash SHA-256" non dice di cosa. Implementato: SHA-256 dell'elenco ordinato di righe `percorso\0sha256-del-file\n` per `plugin.yaml`, `entry` e `files`. Nel manifest di `PrepareRun` (sez. 7) la chat 6 userebbe questo hash. Per i `.csx`, l'hash è quello del pacchetto **compilato** (assembly prodotto dal controller), così l'agente verifica ciò che esegue.

**P8 — Versione dell'SDK (sez. 6).** `sdk: ">=1.0 <2.0"` richiede una versione SDK dell'host, ma non esiste una costante. Uso la versione dell'assembly `LoadTest.Sdk`; propongo di fissarla nel contratto (e nel `Hello.sdk_version` di sez. 7) con un `<Version>1.0.0</Version>` nel csproj dell'Sdk.

**P9 — Riferimenti consentiti in compilazione (sez. 6).** "Il controller verifica i riferimenti consentiti" ma l'elenco non c'è. Proposta iniziale: BCL di `net10.0` senza API Windows-only (`System.Runtime`, `System.Collections`, `System.Linq`, `System.Net.Http`, `System.Text.Json`, `System.Text.RegularExpressions`, `System.Xml*`, `System.IO`, `System.Threading*`), più `LoadTest.Sdk`; niente `System.Diagnostics.Process`, `Microsoft.Win32.*`, `System.Runtime.InteropServices`/P/Invoke né DLL native. Da approvare.

**P10 — Firma (sez. 7).** "Opzionalmente, firma": propongo firma Ed25519 del solo hash, con chiave pubblica nella configurazione dell'agente. Fuori dalla fase 1.

## B. Punti aperti con altre chat

1. **Chat 2:** gli elenchi `EngineServices.WebTestPlugins/RequestPlugins` sono globali; il web test dichiara plugin propri. Chi li istanzia per test e passa i `params`? Io fornisco `LoadedPlugin.Create<T>(kind, args)`.
2. **Chat 3:** `LoadTestRunnerOptions.Plugins` e `CustomProfileResolver` sono già i punti d'aggancio; li alimento io.
3. **Chat 6:** il caricamento nell'agente usa `PluginCache` (hash → cartella); `Missing()` produce i `PackageRequest`.

## C. Sez. 10 (copertura)
- «Plugin web test e request»: TODO → **WIP** (manifest, hash, cache, caricatore DLL; manca l'integrazione con l'Engine).
- «Plugin load test e parametri di contesto»: TODO → **WIP**.
- «Test in codice e generazione del codice»: resta TODO (dipende da P3).
