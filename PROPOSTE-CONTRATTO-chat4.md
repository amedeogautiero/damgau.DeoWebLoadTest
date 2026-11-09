# Proposte di modifica al CONTRATTO v0.1 — dalla chat "Report e analisi" (componente 4)

Da portare alla chat **Architettura**. Nulla di quanto sotto è stato applicato a CONTRATTO.md: il codice della chat 4 è già scritto sulle bozze proposte, quindi se una proposta cambia, cambio il codice.
Riferimenti: sezioni del CONTRATTO v0.1; codice nello zip `loadtest-reports-delivery.zip`.

## A. Modifiche richieste

**C1 — Sezione 3 (struttura della solution).** Aggiungere:
```
src/LoadTest.Reports/       repository SQLite, analisi (fusione istogrammi), esportazioni CSV/HTML/Excel, confronto tra run
tests/LoadTest.Reports.Tests/
```
`Reports` dipende solo da `Metrics` (non da Engine né da Controller): chi chiama mappa `LoadTestResult` sui record di Reports.

**C2 — Sezione 8 (schema SQLite).** Il contratto elenca le tabelle ma non le colonne. Bozza v1 (file `SqliteSchema.cs`), in sintesi:
- `meta(key, value)`: `schema_version`. Un repository con versione maggiore di quella supportata viene rifiutato.
- `runs(run_id, name, started_utc, ended_utc, status, abort_reason, definition_yaml, tests_completed, tests_failed, request_errors, warnings_json)`. `definition_yaml` = copia del `*.loadtest.yaml` usato. `status` = Running | Completed | Aborted | Cancelled.
- `series(series_id, run_id, scenario, test, page, request, transaction_name, kind)`: una riga per chiave di misura (sez. 8); parti assenti = `''`. `kind` = Test | Transaction | Request. (La colonna non si chiama `transaction` perché è parola riservata in SQLite.)
- `intervals(run_id, idx, agent_id, start_utc, end_utc, active_users, agent_saturated)`: un intervallo **per agente**.
- `histograms(run_id, idx, agent_id, series_id, count, failed, bytes_sent, bytes_received, hist BLOB)`: oltre all'istogramma porta i conteggi della serie.
- `error_counts(run_id, idx, agent_id, series_id, error_type, count)` — **tabella nuova**: gli «errori per tipo» della sez. 8.
- `errors(...)`: dettaglio degli errori (limitato da `maxErrorDetails`).
- `counters`, `thresholds`, `machines`: colonne provvisorie, da fissare con la chat 7.
- `annotations(annotation_id, run_id, at_utc, text, created_utc)` — **tabella nuova**: la sez. 10 cita le «annotazioni».
- Date in ISO 8601 UTC (testo).

**C3 — Sezione 8 (istogrammi).** Fissare nel contratto: HdrHistogram in **microsecondi, massimo 1 ora (3.600.000.000 µs), 3 cifre significative** (come già in `Metrics.HistogramSettings`, che la chat 3 ha marcato PROPOSTA). Serve per poter fondere istogrammi di agenti diversi.
Serializzazione nel repository: formato v1 mio (1 byte di versione + Deflate di coppie valore/conteggio in varint), isolato in `HistogramCodec`. Non è l'encoding V2 compresso della libreria: l'ho scelto perché non potevo compilare e provare l'API di encoding. Va deciso con la chat 6 se lo stesso formato debba viaggiare nel protocollo agente–controller (`MetricsBatch`) o se lì si usi l'encoding V2; in tal caso cambio solo il codec.

**C4 — Interfaccia di scrittura/lettura risultati.** Proposta: `IResultsRepository` (in `LoadTest.Reports`) come unico punto di accesso al database. Chi esegue la run (CLI locale o controller) chiama `BeginRun`, `WriteInterval(runId, agentId, interval, agentSaturated)` a ogni campione (aggancio naturale: `LoadTestRunnerOptions.OnInterval`), `WriteErrorDetails`, `CompleteRun`. Da confermare con le chat 3 e 6.

**C5 — Dipendenze.** SQLite: `Microsoft.Data.Sqlite` (include i binari nativi per win/linux/osx x64 e arm, compatibili con il publish single-file). Excel (da fare): propongo `ClosedXML` (MIT); escludo EPPlus per la licenza. HTML autonomo (da fare): grafici in SVG/JS incorporati nel file, senza CDN.

**C6 — Tipi di errore.** Fissare l'elenco in sez. 8. Oggi circolano: `Timeout`, `Http`, `Validation`, `Binding`, `Exception` (Engine), `Failed` (runner, per test e transazioni), `Unknown` (Metrics). Proposta: questi sette, come enumerazione contrattuale, più eventuali tipi di plugin con prefisso `plugin:`.

## B. Punti da decidere con le altre chat

1. **Cos'è la «richiesta» nella chiave di misura (chat 2 e 3).** Il runner usa l'URL completo, query string inclusa (`/search?q=...`): con parametri variabili le serie esplodono e il confronto tra run diventa inutile. Proposta: la richiesta è identificata dal nome del passo (o `id`) più, per le dipendenti, il percorso senza query. Finché non si decide, nei report le serie per URL sono sconsigliate.
2. **Misure per agente (chat 3 e 6).** La sez. 8 le prevede, ma `IntervalSnapshot` non porta l'agente né la CPU. Reports lo gestisce già (parametro `agentId`, flag `agent_saturated`); `Metrics` dovrà fornire entrambi quando arriva il controller.
3. **Intervallo parziale a fine run.** Il runner emette l'ultimo intervallo più corto: i report pesano per durata reale (`end - start`), non per indice. Da dichiarare nel contratto.
4. **Contatori, soglie, macchine (chat 7).** Ho bisogno della forma dei dati per fissare le colonne di `counters`, `thresholds`, `machines`.
5. **`timingDetail: none`.** Gli istogrammi restano vuoti: i report mostrano solo conteggi e byte (tempi vuoti). Da confermare come comportamento voluto.

## C. Aggiornamento sez. 10 (copertura)
- «Repository SQLite (opzione database)»: TODO → **WIP** (repository, schema e test scritti; non ancora compilati).
- «Analisi: grafici, tabelle, percentili, annotazioni, errori, soglie»: TODO → **WIP** (percentili da istogrammi fusi e CSV; mancano HTML, grafici, soglie).
- «Confronto tra run, trend, export Excel»: resta TODO.
