# Proposte di modifica al CONTRATTO v0.1 — dalla chat "Controller e agenti" (componente 6), parte 4: CLI e CI

Da portare alla chat **Architettura**. Nulla è applicato a CONTRATTO.md, né a Sdk/Engine/Metrics/Reports/Plugins/Protocol. Seguono G1–G21 delle parti precedenti.
Toccati in questa parte (miei): `ControllerOptions`, `Program.cs` e API del controller, `Program.cs` dell'agente (vedi G22 e G24).

## A. Modifiche richieste

**G22 — Sez. 1 (API JSON del controller, aggiunte a G18).**
- `POST /api/projects` (corpo `application/zip`): la CLI invia la cartella del load test quando il controller non vede il suo filesystem (tipico in CI). Risposta `{projectId}`. Estrazione difensiva (zip slip, tetto `MaxProjectUploadBytes` = 512 MB, massimo 20.000 voci); i progetti scadono dopo `ProjectRetentionHours` = 24 h.
- `POST /api/runs` accetta `projectId`: in quel caso `loadTestPath` è relativo al progetto.
- `GET /api/runs/{id}/summary` (JSON) e `/summary.csv`: riassunto per serie con percentili dagli istogrammi fusi, dal database SQLite del controller (valgono anche dopo un riavvio). `GET /api/agents` ora ha un tipo stabile (`AgentDto`).
- Le forme JSON (`RunSummaryDto`, `SeriesDto`) sono la superficie stabile per CLI, CI ed estensione VS Code: tempi in millisecondi, nomi camelCase.

**G23 — Sez. 1 e 10 (CLI `loadtest`).**
- Comandi: `run`, `validate`, `report`, `controller`, `agent`, `agents`, `status`, `stop`, `help`, `version`. Opzioni sconosciute = errore (nessun refuso silenzioso in CI).
- **Codici di uscita:** 0 ok · 1 run non riuscita o soglia non rispettata · 2 argomenti o file non validi · 3 infrastruttura (controller non raggiungibile o non autorizzato, run non avviabile) · 130 interrotto con Ctrl+C.
- **Verifiche per la CI:** sempre presenti «run completata» e «almeno una richiesta misurata» (una run vuota non diventa verde); a richiesta `--max-error-rate`, `--max-p95-ms`, `--max-p99-ms`, `--min-rps`, `--min-requests`, `--max-failed-tests`. Le soglie sui tempi falliscono se i tempi mancano (`timingDetail: none`): meglio un falso rosso di un falso verde. Si valutano sulle serie di tipo richiesta.
- **Uscite:** tabella a console, `--json <file|->`, `--csv`, `--junit` (un testcase per verifica); `report --db` le riproduce da una run già salvata.
- **Run locale** (senza `--controller`): un `LoadTestRunner` nel processo, risultati in SQLite (`--db`, default `./loadtest-results.db`); la sezione `rig` è ignorata; plugin `loadTest`/`loadProfile` da `plugins/` accanto al load test; contatori del goal con gli stessi nomi del controller (G20). **Run remota** (`--controller url`): `--upload`/`--no-upload` (default: upload se l'host non è locale), `--remote-path`, Ctrl+C = stop ordinato, due volte = abort. Il token si legge anche da `LOADTEST_TOKEN`; `LOADTEST_CONTROLLER` vale per `agents/status/stop` ma **non** per `run` (una run non deve diventare remota per una variabile d'ambiente).
- `validate` riusa il packager del controller: controlla load test, rig, web test (anche `call:`), data source, file di body, certificati e plugin; i `.csx` non si compilano in validazione.
- `controller` e `agent` avviano gli host con le opzioni tradotte in configurazione (qualsiasi chiave con `--Agent:Chiave=valore`, `--Controller:Chiave=valore`).

**G24 — Sez. 3 e 12 (progetto Cli e host riusabili).** `LoadTest.Cli` produce l'eseguibile `loadtest` (`AssemblyName`). Per poterlo ospitare, `Program.cs` di Agent e Controller sono ridotti a una riga e la costruzione dell'host sta in `AgentHost` e `ControllerHost`. La CLI dipende da Agent e Controller (quindi dal framework ASP.NET Core): **per la parte 5 va deciso** se pubblicare un solo binario `loadtest` (pesante: porta ASP.NET Core in ogni RID, anche per chi vuole solo l'agente) oppure binari separati `loadtest-agent` leggero e `loadtest` completo. Il progetto va in sez. 12.

## B. Richieste ad altre chat e limiti noti
1. **Chat 7:** la sezione `thresholds` del load test (soglie sui contatori, con warn/critical) non è valutata dalla CLI: le soglie CI sono sui risultati finali. Quando i counter set esistono, l'esito delle `thresholds` può diventare un'altra verifica nel JSON.
2. **Chat 4:** nessun HTML né Excel dalla CLI finché non esistono (la CLI scrive CSV e JSON); `report` li userà.
3. **Chat 8 (VS Code):** può usare l'API di G18/G22 così com'è.
4. **Limiti:** nella run remota la CLI non vede l'avanzamento per scenario, solo i totali; il polling è ogni 2 s; la CLI non rilancia una run già partita se perde il controller (esce con 3 dopo tre errori consecutivi; la run sul controller continua e si può riprendere con `status`/`report`).

## C. Sez. 10 (copertura)
- «Esecuzione: CLI e integrazione CI»: TODO → **WIP** (comandi, soglie, JUnit/JSON/CSV; non compilato).

## D. Esempio di integrazione CI (GitHub Actions; con Azure DevOps o GitLab cambia solo la sintassi)
```yaml
- name: Load test
  run: >
    loadtest run tests/carico/run.loadtest.yaml
    --controller https://loadtest.interno:5000 --token ${{ secrets.LOADTEST_TOKEN }}
    --max-error-rate 1 --max-p95-ms 800 --min-requests 1000
    --junit results/loadtest.xml --json results/loadtest.json --csv results/loadtest.csv --quiet
- uses: actions/upload-artifact@v4
  if: always()
  with: { name: loadtest, path: results/ }
```
Senza controller (prova su un solo nodo): stesso comando senza `--controller`; i risultati restano in `loadtest-results.db`.
