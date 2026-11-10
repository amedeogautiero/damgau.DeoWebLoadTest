# Proposte di modifica al CONTRATTO v0.1 — dalla chat "Controller e agenti" (componente 6), parte 1: Protocollo

Da portare alla chat **Architettura**. Nulla è applicato a CONTRATTO.md, né a Sdk/Engine/Metrics/Reports/Plugins. Il codice è scritto sulle bozze sotto: se una proposta cambia, cambio il codice.
Il protocollo rispetta alla lettera la sez. 7: servizio `AgentLink.Connect`, i due `oneof m` con gli stessi nomi e numeri di campo.

## A. Modifiche richieste

**G1 — Sez. 3 e 12 (progetti).** `LoadTest.Protocol` non contiene solo `.proto`: ha anche `MetricsWire` (conversione `IntervalSnapshot` ⇄ `MetricsBatch`), `Handshake` e `WireHistogram`, quindi **dipende da `LoadTest.Metrics`** (e da Google.Protobuf / Grpc.Core.Api / Grpc.Tools). Nella sez. 12 l'elenco dei progetti da rilasciare non cita `Protocol`, `Controller`, `Agent`, `Cli`: vanno aggiunti, altrimenti gli zip non possono contenerli. Mancano anche `LoadTest.Reports` e `LoadTest.Plugins.Compilation` nella sez. 3.

**G2 — Sez. 7 (campi interni dei messaggi).** Il contratto nomina i messaggi ma non i campi. Bozza in `Protos/agent_link.proto` (v1), in sintesi:
- `Hello`: i campi del contratto (`agent_id, name, protocol_version, sdk_version, rid, os, cores, ram_mb, max_users, utc_now`), nessuno in più.
- `Welcome`: `accepted, reject_reason, controller_name, protocol_version, sdk_version, clock_offset_ms, utc_now`. `clock_offset_ms = ora_controller − ora_agente` stimato alla ricezione di `Hello` (non compensa la latenza).
- `PrepareRun`: `run_id, run_name, controller_name, manifest[], load_test_path, partition_index, partition_count`. Ogni `ManifestEntry` ha `kind (plugin|load_test|web_test|data|deployment), name, version, sha256, size, path, encoding, partitioned`. Il manifest porta quindi **anche** i `*.loadtest.yaml` / `*.webtest.yaml` e i deployment items, non solo i plugin.
- `PackageRequest{run_id, sha256[]}`, `Ready{run_id, ok, error}`.
- `PackageChunk{run_id, sha256, sequence, data, last}`; chunk da 64 KiB.
- `StartRun{run_id, start_at_utc, user_share, max_users, seed, user_id_base}`. «Quota utenti» = frazione 0..1 (dal `weight` del rig) più tetto `max_users`. `start_at_utc` è sull'orologio del **controller**: l'agente applica `clock_offset_ms` di `Welcome`.
- `UpdateLoad{run_id, targets[{scenario, users}]}`: utenti **finali** dell'agente per scenario (il controller ha già applicato la quota).
- `Stop{run_id, mode: graceful|abort, reason}`.
- `MetricsBatch{run_id, interval_index, start_utc, end_utc, active_users, series[], counters[]}`; `SeriesSample` = chiave + conteggi + byte + `errors_by_type` + istogramma. Le parti assenti della chiave (`page/request/transaction`) sono campi `optional` non impostati, distinti dalla stringa vuota. `counters[]` è provvisorio (chat 7).
- `ErrorBatch{run_id, errors[]}`, `Heartbeat{run_id, cpu_percent, ram_used_mb, active_users, utc_now, clock_offset_ms}`, `Finished{run_id, status, abort_reason, tests_completed, tests_failed, request_errors, warnings[]}`.

**G3 — Sez. 7 (autenticazione).** Token nel metadato gRPC `authorization: Bearer <token>` (nessun campo in `Hello`); in alternativa mTLS. Il controller valida l'autenticazione prima di leggere `Hello`.

**G4 — Sez. 1 e 7 (cosa viaggia e con quale hash).** Plugin: la cartella del pacchetto è impacchettata in zip, `sha256` = hash del pacchetto (P7 della chat 5, per i `.csx` quello del pacchetto compilato). File singoli (test, dati, deployment): gzip, `sha256` = SHA-256 del file non compresso. Per i dati con `partition: true` il controller **taglia fisicamente** la fetta di ogni agente (hash per agente, `partitioned = true`); `partition_index/count` restano comunque in `PrepareRun` per `IDataSource.OpenAsync`.

**G5 — Sez. 7 (regole dell'handshake).** `protocol_version` uguale; SDK con lo stesso major e minor dell'agente ≥ minor del controller (i `.csx` sono compilati dal controller contro il suo SDK). Rifiuto = `Welcome.accepted = false` con `reject_reason`, poi il controller chiude lo stream. Richiede P8 (versione fissa dell'SDK, oggi è la versione dell'assembly).

**G6 — Sez. 8 e C3 della chat 4 (istogrammi sul filo).** `SeriesSample.hist` usa `HISTOGRAM_FORMAT_VARINT_DEFLATE_V1`, byte-identico al `HistogramCodec` v1 di Reports; il valore `HDR_V2_COMPRESSED` è riservato (oggi lancia `NotSupportedException`). Il codec è **duplicato** in `WireHistogram` per non far dipendere Protocol da Reports: propongo di spostarlo in `LoadTest.Metrics` (chat 3/4) e usarlo da entrambi. Il decoder tratta i dati come non fidati (limite di coppie, conteggi validi).

**G7 — Stato `Failed` (sez. 8 / chat 4).** `Finished.status` ha anche `FAILED` (preparazione o esecuzione non riuscita sull'agente). `runs.status` di Reports prevede solo Running|Completed|Aborted|Cancelled: va aggiunto `Failed`.

**G8 — Costanti (sez. 7).** `ProtocolVersion = 1`, chunk 64 KiB, `MaxMessageBytes = 16 MiB` (da impostare su server e client), soglia di saturazione 80% come da contratto.

## B. Richieste ad altre chat (servono alla parte 2, Agente)

1. **Chat 2/3 (Engine, `LoadTestRunner`):** oggi `Adjust` usa solo `ILoadProfile.GetUserCount`. Per l'agente servono: (a) un fattore di quota e un tetto sugli utenti (`user_share`, `max_users`); (b) un modo per **sovrascrivere dall'esterno** il target per scenario (`UpdateLoad`, profili goal e a gradini calcolati dal controller); (c) un `UserId` iniziale (`user_id_base`, oggi parte da 1 su ogni agente e gli id collidono); (d) il seme per agente. Posso proporre io l'API minima (opzioni in `LoadTestRunnerOptions`) quando arrivo alla parte 2.
2. **Chat 3:** `LoadTestRunnerOptions.OnInterval` è l'aggancio per `MetricsWire.ToBatch`; `AgentId` e saturazione CPU non viaggiano nell'`IntervalSnapshot` (punto B2 della chat 4): il controller li aggiunge al momento di `WriteInterval(runId, agentId, interval, agentSaturated)`.
3. **Chat 5:** P4 (`ICodeStepHost`), P12 (reference assembly) e il percorso di `LoadTest.Sdk` nel publish single-file restano da decidere prima della parte 3.

## C. Sez. 10 (copertura)
- «Rig: agenti con peso, attributi, deployment items, setup e cleanup»: resta TODO.
- «Agenti multipiattaforma, cache plugin per hash»: TODO → **WIP** (protocollo e handshake scritti; non ancora compilati).
