# VSCode-LoadTest: documento di contratto (v0.1)

Questo file è la fonte unica di verità per tutte le chat del progetto. Ogni chat di componente lo legge prima di iniziare. **Le modifiche al contratto si decidono solo nella chat "Architettura"** e fanno salire la versione (vedi sezione 9).

## 1. Principi

- Piattaforma: **.NET 10 (LTS)**, `net10.0`, C# con nullable abilitato.
- Il motore gira da CLI; VS Code lo pilota tramite un'estensione TypeScript che parla con il controller (HTTP/JSON locale o gRPC).
- Gli agenti girano su `win-x64`, `linux-x64`, `linux-arm64`, `linux-arm` (da confermare), `osx-arm64`, `osx-x64`. Publish self-contained e single-file.
- L'agente apre solo connessioni **in uscita** verso il controller (stream gRPC bidirezionale). Nessun Kestrel sull'agente.
- Plugin: **DLL** oppure **`.csx`**. Il `.csx` viene compilato **una volta dal controller** con Roslyn contro l'SDK e distribuito come assembly (IL portabile). Gli agenti non contengono Roslyn.
- Distribuzione per **hash SHA-256** con cache locale sull'agente: manifest, richiesta dei soli pacchetti mancanti, stream compresso.
- Metriche come **istogrammi HdrHistogram** fusi dal controller. Mai medie di percentili.
- Repository dei risultati: **SQLite** predefinito.
- Definizione di "pagina" identica al vecchio tool: richiesta di primo livello più le sue richieste dipendenti.

## 2. Componenti e chat

| # | Chat | Responsabilità | Dipende da |
|---|---|---|---|
| 1 | Architettura e contratti | questo documento, decisioni, versioni | - |
| 2 | Engine e web test | esecuzione richieste, regole, data source, condizioni, transazioni, dipendenti | 1 |
| 3 | Load test locale | scenari, mix, profili, run settings, raccolta metriche | 2 |
| 4 | Report e analisi | HTML, CSV/Excel, SQLite, confronto tra run | 3 |
| 5 | Plugin | caricatore DLL/`.csx`, Roslyn, cache, SDK | 1 |
| 6 | Controller e agenti | gRPC, handshake, distribuzione, servizi OS, publish | 3, 5 |
| 7 | Monitoraggio | counter set, soglie, goal-based, collector | 3 |
| 8 | Estensione VS Code | editor, Run/Stop, webview, comandi | 6 |
| 9 | Registratore e import HAR | conversione HAR, promozione parametri dinamici | 1 |

## 3. Struttura della solution

```
LoadTest.sln
  src/LoadTest.Sdk/         interfacce plugin e modelli (net10.0, nessuna dipendenza di piattaforma)
  src/LoadTest.Engine/      web test, utenti virtuali, regole, data source
  src/LoadTest.Metrics/     istogrammi, contatori, serializzazione
  src/LoadTest.Plugins/     caricatore, Roslyn, cache
  src/LoadTest.Controller/  gRPC server, orchestrazione
  src/LoadTest.Agent/       worker service, publish per RID
  src/LoadTest.Protocol/    file .proto condivisi
  src/LoadTest.Cli/         comando `loadtest`
  vscode-extension/         TypeScript
  tests/
```

## 4. Formato web test (YAML, `*.webtest.yaml`)

```yaml
version: 1
name: Login e ricerca
description: Flusso di esempio
baseUrl: https://esempio.it
defaults:
  timeoutSec: 60
  followRedirects: true
  httpVersion: "1.1"            # 1.0 | 1.1 | 2
  encoding: utf-8
  parseDependentRequests: true  # scarica immagini, css, js
  cache: simulated              # simulated | none
  headers: { Accept-Language: it }

context:                        # parametri di contesto
  lingua: it

data:                           # data source
  utenti:
    type: csv                   # csv | xml | database | excel
    path: data/utenti.csv
    access: sequential          # sequential | random | unique
    # database: { provider: sqlite|sqlserver|postgres, connection: "...", query: "..." }

credentials:                    # opzionale
  default: { type: basic, user: "{{utenti.username}}", password: "{{utenti.password}}" }  # basic | ntlm | negotiate
clientCertificate: { path: certs/client.pfx, password: "{{env.CERT_PWD}}" }
proxy: { url: "http://proxy:8080", bypass: [ "localhost" ] }

plugins:
  webTest: [ { name: MioPlugin, params: { x: 1 } } ]
  request: [ ]

steps:
  - name: Home
    id: home
    request:
      method: GET
      url: /
      headers: { }
      dependentRequests: auto   # auto | none
    thinkTimeMs: 2000
    expectedStatus: 200
    breakpoint: true       # sospende prima dell'invio, solo in modalità Debug
    extract:
      - { name: csrf, type: regex, pattern: 'name="csrf" value="(.+?)"', group: 1, required: true }
      # altri type: text (delimitatori), attribute, hidden, header, formField, custom
    validate:
      - { type: status, expected: 200 }
      - { type: textContains, value: "Benvenuto", ignoreCase: true }
      - { type: maxTimeMs, value: 2000 }
      # altri type: formField, tagRequired, attributeRequired, responseUrl, responseTimeGoal, custom
      # ogni regola ha `level: low|medium|high` per il validation level

  - transaction: Autenticazione  # raggruppa più richieste e misura il tempo totale
    steps:
      - name: Login
        request:
          method: POST
          url: /login
          body:
            form: { user: "{{utenti.username}}", pass: "{{utenti.password}}", csrf: "{{csrf}}" }
            # alternative: raw: "...", json: {...}, xml: "...", file: path, multipart: [...]
        expectedStatus: 302

  - if: "{{csrf}} != ''"        # condizioni
    then:
      - { name: Profilo, request: { method: GET, url: /profilo } }
    else: [ ]

  - loop:                       # cicli: for | while | foreach
      for: 3
    steps:
      - { name: Ricerca, request: { method: GET, url: "/search?q=test" } }

  - call: altro.webtest.yaml    # richiama un altro web test
    params: { lingua: en }

  - code: plugins/Custom.csx    # passo scritto in codice
```

Variabili predefinite: `$WebTestIteration`, `$LoadTestUserContext`, `$AgentId`, `$AgentName`, `$ControllerName`, `$UserId`, `$ScenarioName`, `$TestName`. Sintassi di sostituzione: `{{nome}}`, `{{sorgente.colonna}}`, `{{env.NOME}}`, `{{$Predefinita}}`.

## 5. Formato load test (YAML, `*.loadtest.yaml`)

```yaml
version: 1
name: Carico giornaliero
runSettings:
  warmupSec: 60
  durationMin: 30             # in alternativa: iterations: N
  coolDownSec: 30
  samplingSec: 5
  connectionModel: pool       # perUser | pool
  connectionPoolSize: 100
  validationLevel: high       # low | medium | high
  timingDetail: statistics    # none | statistics | all
  maxErrorDetails: 1000
  stopOnErrors: 500
  saveLogOnFailure: true
  sqlTracing: false
  ipSwitching: { enabled: false, addresses: [ ] }

scenarios:
  - name: Utenti anonimi
    thinkProfile: { mode: normal, avgMs: 2000, stdDevMs: 500, scale: 100 }  # recorded | normal | off
    testMix:
      model: users            # tests | users | pace | sequential
      tests:
        - { test: ricerca.webtest.yaml, percent: 70 }
        - { test: catalogo.webtest.yaml, percent: 30 }
        # per model: pace -> testsPerUserPerHour: 12
    loadPattern:
      type: step              # constant | step | goal | custom
      start: 10
      max: 200
      step: 20
      stepSec: 60
      # constant: users
      # goal: { counter: "...", target: 70, tolerance: 5, minUsers: 10, maxUsers: 500, adjustment: 10, intervalSec: 30 }
      # custom: { plugin: MioProfilo }
    newUsersPercent: 40
    browserMix:
      - { browser: chrome, percent: 80 }
      - { browser: firefox, percent: 20 }
    networkMix:
      - { network: dsl, percent: 100 }   # lan | dsl | 3g | custom {downKbps, upKbps, latencyMs}

counterSets: [ ]              # vedi componente 7
thresholds: [ ]               # { counter, warn, critical, compareTo: constant|counter }
monitoredMachines: [ ]

rig:
  agents:
    - { name: agent-linux-1, weight: 2, attributes: { os: linux }, maxUsers: 500 }
    - { name: pi-1, weight: 1, maxUsers: 40 }
  deployment: [ { path: data/utenti.csv, partition: true } ]
  setupScript: scripts/setup.sh
  cleanupScript: scripts/cleanup.sh

plugins:
  loadTest: [ { name: MioLoadPlugin } ]
```

## 6. Interfacce SDK dei plugin (C#, namespace `LoadTest.Sdk`)

```csharp
public interface IWebTestPlugin
{
    void PreWebTest(WebTestContext ctx);
    void PostWebTest(WebTestContext ctx);
    void PreTransaction(WebTestContext ctx, string name);
    void PostTransaction(WebTestContext ctx, string name);
}

public interface IRequestPlugin
{
    void PreRequestDataBinding(RequestContext ctx);
    void PreRequest(RequestContext ctx);
    void PostRequest(RequestContext ctx, ResponseContext resp);
    void PostRequestDataBinding(RequestContext ctx, ResponseContext resp);
}

public interface IExtractionRule { ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args); }
public interface IValidationRule { ValidationResult Validate(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args); }

public interface IDataSource : IAsyncDisposable
{
    Task OpenAsync(DataSourceConfig cfg, PartitionInfo partition);
    ValueTask<IReadOnlyDictionary<string, string>?> NextAsync(UserContext user);
}

public interface ILoadTestPlugin
{
    void Initialize(ILoadTestEvents events, IReadOnlyDictionary<string, string> args);
}

public interface ILoadTestEvents
{
    event Action<LoadTestInfo> LoadTestStarting;
    event Action<LoadTestInfo> LoadTestFinished;
    event Action WarmupComplete;
    event Action<TestEventArgs> TestStarting;
    event Action<TestEventArgs> TestFinished;
    event Action<TestSelectedArgs> TestSelected;      // permette di cambiare il test scelto
    event Action<HeartbeatArgs> Heartbeat;            // ogni secondo
    event Action<ThresholdArgs> ThresholdExceeded;
    event Action<string> Aborted;
}

public interface ILoadProfile { int GetUserCount(TimeSpan elapsed, LoadState state); }

public interface ICodedTest { Task RunAsync(TestContext ctx); }  // test in codice, equivalente ai coded web test
```

Regole: i plugin non usano API solo Windows né librerie native; il controller verifica i riferimenti consentiti in compilazione. Accesso a `ctx` per leggere e scrivere i parametri di contesto, modificare la richiesta, aggiungere regole.

Manifest del pacchetto plugin (`plugin.yaml`):

```yaml
name: MioPlugin
version: 1.0.0
sdk: ">=1.0 <2.0"
kinds: [ webTest, request ]       # webTest | request | extraction | validation | dataSource | loadTest | loadProfile | codedTest
entry: MioPlugin.dll              # oppure MioPlugin.csx
files: [ ]
```

## 7. Protocollo controller-agente (gRPC, `LoadTest.Protocol`)

```proto
service AgentLink { rpc Connect (stream AgentMsg) returns (stream ControllerMsg); }

message AgentMsg {
  oneof m {
    Hello hello = 1;                 // agent_id, name, protocol_version, sdk_version, rid, os, cores, ram_mb, max_users, utc_now
    PackageRequest need = 2;         // hash dei pacchetti mancanti
    Ready ready = 3;                 // run preparato
    MetricsBatch metrics = 4;        // istogrammi + contatori per intervallo
    ErrorBatch errors = 5;
    Heartbeat hb = 6;                // cpu%, ram, utenti attivi, offset orologio
    Finished finished = 7;
  }
}
message ControllerMsg {
  oneof m {
    Welcome welcome = 1;             // accettazione o rifiuto, offset orologio stimato
    PrepareRun prepare = 2;          // run_id, manifest (nome, versione, sha256, size), file dati e partizione
    PackageChunk chunk = 3;          // stream compresso
    StartRun start = 4;              // start_at_utc sincronizzato, quota utenti, seed
    UpdateLoad update = 5;           // nuovi utenti target (profilo goal e a gradini)
    Stop stop = 6;                   // graceful | abort
  }
}
```

Handshake: il controller rifiuta agenti con `protocol_version` o `sdk_version` incompatibili. L'agente segnala la propria CPU; sopra l'80% il report marca le misure di quell'agente come "agente saturo". Autenticazione: mTLS o token; i pacchetti plugin hanno hash e, opzionalmente, firma.

## 8. Metriche e risultati

- Chiave di misura: `(scenario, test, pagina, richiesta, transazione)`.
- Per intervallo di campionamento: istogramma dei tempi (HdrHistogram, microsecondi), conteggi (richieste, pagine, test, errori per tipo), byte inviati e ricevuti, utenti attivi, per agente.
- Database SQLite: tabelle `runs`, `intervals`, `histograms`, `errors`, `thresholds`, `counters`, `machines`.
- Esportazioni: report HTML autonomo, CSV, Excel.

## 9. Regole di governo del contratto

- Versione del documento nel titolo. Ogni modifica incrementa la versione e riporta nel registro qui sotto cosa è cambiato.
- Le chat di componente non cambiano questo file. Se serve una modifica, la propongono alla chat Architettura.
- I formati YAML hanno un campo `version`. Variazioni compatibili aumentano il numero minore, le altre il maggiore.

### Registro modifiche

- v0.1: prima stesura.

## 10. Copertura delle funzioni del vecchio tool

Stato: `TODO`, `WIP`, `OK`. Chat: numero della tabella della sezione 2.

| Area | Funzione | Chat | Stato |
|---|---|---|---|
| Web test | Richieste (metodi, header, cookie, body, redirect, timeout, encoding, HTTP version) | 2 | TODO |
| Web test | Richieste dipendenti e cache simulata del browser | 2 | TODO |
| Web test | Credenziali (Basic, NTLM, Negotiate), certificati client, proxy | 2 | TODO |
| Web test | Transazioni, condizioni, cicli, chiamata di altri test | 2 | TODO |
| Web test | Parametri di contesto e predefiniti | 2 | TODO |
| Web test | Data source CSV, XML, database, Excel (sequential, random, unique) | 2 | TODO |
| Web test | Regole di estrazione e validazione, validation level, custom | 2 | TODO |
| Web test | Think time per richiesta | 2 | TODO |
| Web test | Plugin web test e request | 5 | TODO |
| Web test | Test in codice e generazione del codice dal test dichiarativo | 5 | TODO |
| Web test | Registratore con promozione parametri dinamici | 9 | TODO |
| Web test | Visualizzatore di playback | 8 | TODO |
| Load test | Più scenari, test mix (4 modelli), browser mix, network mix | 3 | TODO |
| Load test | Profili costante, a gradini, goal-based, custom | 3, 7 | TODO |
| Load test | Percentuale di nuovi utenti, think profile | 3 | TODO |
| Load test | Run settings completi, stop on error, log al fallimento | 3 | TODO |
| Load test | Modello di connessione (per utente, pool) | 3 | TODO |
| Load test | Unit test nel mix (test in codice) | 3 | TODO |
| Load test | Counter set, soglie, macchine monitorate | 7 | TODO |
| Load test | Plugin load test e parametri di contesto | 5 | TODO |
| Load test | Rig: agenti con peso, attributi, deployment items, setup e cleanup | 6 | TODO |
| Load test | IP switching | 6 | TODO |
| Load test | Emulazione di rete lato client | 3 | TODO |
| Load test | SQL tracing | 7 | TODO (bassa priorità) |
| Risultati | Analisi: grafici, tabelle, percentili, annotazioni, errori, soglie | 4 | TODO |
| Risultati | Confronto tra run, trend, export Excel | 4 | TODO |
| Risultati | Repository SQLite (opzione database) | 4 | TODO |
| Esecuzione | CLI e integrazione CI | 6 | TODO |
| Esecuzione | Estensione VS Code | 8 | TODO |
| Nuove | Agenti multipiattaforma (Linux, macOS, Raspberry), cache plugin per hash | 6 | TODO |

## 11. Unit-Test
tutti i test devono essere messi nel progetto .tests\LoadTest.Engine.Tests

## 12 Rilasci odegli zip
tutti i rilasci dei file tramite zip decono ricostruire la struttura .\src\{nome_progetto}
    - .\src\LoadTest.Engine
    - .\src\LoadTest.Metrics
    - .\src\LoadTest.Plugins
    - .\src\LoadTest.Plugins.Compilation
    - .\src\LoadTest.Reports
    - .\src\LoadTest.Sdk