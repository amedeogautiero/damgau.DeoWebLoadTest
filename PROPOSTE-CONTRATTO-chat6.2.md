# Proposte di modifica al CONTRATTO v0.1 — dalla chat "Controller e agenti" (componente 6), parte 2: Agente

Da portare alla chat **Architettura**. Nulla è applicato a CONTRATTO.md, né a Sdk/Engine/Metrics/Reports/Plugins/Protocol. Seguono G1–G8 della parte 1.

## A. Modifiche richieste

**G9 — Sez. 3 e 12 (progetto Agent).** `LoadTest.Agent` è un eseguibile (worker, nessun Kestrel) che dipende da Engine, Plugins (senza Roslyn) e Protocol, più `Microsoft.Extensions.Hosting` e `Grpc.Net.Client`. Va in sez. 12 per gli zip. Configurazione: sezione `Agent` di appsettings.json, variabili `Agent__ControllerUrl`, ecc.; chiavi in `AgentOptions` (ControllerUrl, AgentId, Name, MaxUsers, WorkDirectory, Token, ClientCertificatePath/Password, HeartbeatSec, ReconnectMinSec/MaxSec, MaxEntryBytes, MaxRunBytes, KeepRunFolders).

**G10 — Sez. 7 (comportamento dell'agente).** Regole implementate, da dichiarare:
- una sola run alla volta; un `PrepareRun` ricevuto mentre l'agente è occupato riceve `Ready{ok=false}`;
- `Ready{ok=false}` anche se il manifest non è valido, un hash non coincide, il load test non è valido (`LoadTestLoader` riporta tutti gli errori) o un plugin non carica: la run non parte;
- se lo stream cade durante una run, la run viene **annullata** (le metriche non avrebbero dove andare) e l'agente si riconnette con backoff (1 s → 30 s); un `Welcome` rifiutato attende il massimo;
- `StartRun.start_at_utc` è sull'orologio del controller: l'agente sottrae `clock_offset_ms` di `Welcome`; partenza in ritardo → avviso in `Finished.warnings`; più di 10 minuti nel futuro → `Finished{FAILED}`;
- `Finished` viene inviato dopo che l'agente è tornato libero (il controller può mandare subito un nuovo `PrepareRun`); gli `ErrorBatch` precedono `Finished`;
- `UpdateLoad` (utenti finali dell'agente, per scenario) prevale sul profilo locale; senza `UpdateLoad` vale profilo originale × `user_share` (arrotondato), con tetto `max_users` sul totale dell'agente (scenari ridotti in proporzione). Per i profili goal il controller deve mandare `UpdateLoad`: prima del primo, l'agente usa `minUsers × user_share`.

**G12 — Arrotondamento della quota.** Con più agenti la somma dei valori arrotondati può differire dal totale di ±(n agenti)/2 utenti. Soluzioni: il controller manda `UpdateLoad` anche per i profili a gradini (il contratto lo prevede), oppure aggiungo a `StartRun` un campo `share_start` per il riparto cumulativo.

**G13 — Sicurezza del trasferimento (sez. 7).** L'agente non si fida ciecamente del controller: `run_id` limitato a `[A-Za-z0-9_-]{1,64}`; percorsi relativi senza `..` né radici (zip slip incluso); tetti su dimensione per voce e per run; zip con massimo 10.000 voci e byte contati in lettura; hash e dimensione verificati prima di entrare in cache; chunk fuori sequenza o non richiesti = errore. Le cache (`cache/files`, `cache/plugins`) non hanno ancora eviction: da decidere (parte 5).

**G14 — CPU nell'Heartbeat.** Linux: CPU di macchina da `/proc/stat`. Windows/macOS: CPU del solo processo agente (approssimazione). La CPU di macchina cross-platform va concordata con la chat 7.

## B. Richieste ad altre chat

**G11 — Engine / chat 2 e 3** (la parte 2 funziona senza queste modifiche, con le approssimazioni indicate):
1. **Stop graceful**: `LoadTestRunner` non ha un'API per «nessun nuovo test, finiscono quelli in corso». Approssimazione attuale: target a zero e annullamento dopo `coolDownSec` (minimo 1 s). Proposta: `RequestStop()` nel runner.
2. **`user_id_base`**: gli id utente partono da 1 su ogni agente e collidono (`$UserId`). Il campo è in `StartRun` ma non applicabile oggi (avviso in `Finished.warnings`). Proposta: `FirstUserId` in `LoadTestRunnerOptions`.
3. **Seme**: l'agente usa `seed * 397 + partition_index` come `Seed` del runner (già supportato).
4. **`RequestMeasurement`** non porta l'istante: `ErrorEntry.at_utc` resta vuoto.
5. **Data source**: nessun componente popola `WebTestContext.DataRows` (né `IDataSource` è collegato): `partition_index/count` viaggiano ma non sono usati.
6. **Plugin webTest/request/extraction/validation/dataSource e `code:`** (chat 5, B1 e P4): l'agente carica e collega solo i plugin `loadTest` e `loadProfile`; gli altri kind restano non collegati finché l'Engine non decide chi li istanzia per test. `ICodeStepHost` non è fornito (i passi `code:` falliscono con messaggio chiaro).
7. **Profili**: l'agente sostituisce a runtime il `loadPattern` di ogni scenario con un profilo `custom` interno (`agent:<indice>`, via `CustomProfileResolver`, hook esistente). Nessuna modifica all'Engine, ma il `LoadTestRunner` non vede più i profili goal (niente `CounterProvider` sull'agente: il goal è del controller).

## C. Sez. 10 (copertura)
- «Agenti multipiattaforma, cache plugin per hash»: resta **WIP** (agente scritto; non compilato).
- «Modello di connessione (per utente, pool)»: invariato (warning dell'Engine inoltrato in `Finished.warnings`).
