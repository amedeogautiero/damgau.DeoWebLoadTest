# Proposte di modifica al CONTRATTO v0.1 — dalla chat "Controller e agenti" (componente 6), parte 3: Controller

Da portare alla chat **Architettura**. Nulla è applicato a CONTRATTO.md, né a Sdk/Engine/Metrics/Reports/Plugins. Seguono G1–G14 delle parti 1 e 2.
In questa parte ho toccato due file miei delle parti precedenti (vedi G15): `agent_link.proto` e, nell'Agente, `AgentSession.cs` e `RunExecutor.cs`.

## A. Modifiche richieste

**G15 — Sez. 5 e 7 (semantica distribuita di `iterations` e `stopOnErrors`).** Il contratto non dice cosa significano con più agenti.
- `iterations: N` = N test **in totale**. Il controller divide N fra gli agenti con il resto maggiore (pesi del rig) e lo manda in `StartRun.iterations` (campo nuovo, n. 7, 0 = valore del file). Se N è minore del numero di agenti, o un agente ne avrebbe 0, la run non parte.
- `stopOnErrors: N` = N errori di richiesta **in totale**. Il controller somma i `failed` delle serie di tipo richiesta nei `MetricsBatch` e manda `Stop(abort)` a tutti; l'agente ignora il valore locale (lo azzera in `Prepare`). Stato finale `Aborted` con motivo.

**G16 — Sez. 1 e 6 (compilazione `.csx` e posizione dei pacchetti).** Il controller cerca i pacchetti in `plugins/<cartella>/plugin.yaml` accanto al load test (P5 della chat 5) e li risolve per nome; riferimenti `Pacchetto/Tipo` usano il solo nome del pacchetto. I pacchetti con entry `.csx` richiedono un compilatore: ho definito `IPluginCompiler.Compile(PluginPackage, string outputDir)` nel controller, **senza adattatore** perché non ho le firme di `LoadTest.Plugins.Compilation` (chat 5). Finché non c'è, un pacchetto `.csx` fa fallire la pianificazione con un messaggio esplicito. Servono da chat 5: nome della classe/metodo di compilazione e dove sono i reference assembly nel publish single-file.

**G17 — Sez. 5 (`rig`).**
- `rig.agents[].name` si confronta con il nome dell'agente (in alternativa con il suo `agent_id`). Attributi verificabili: `os`, `rid`, `arch`, `name`; gli altri (etichette libere) non sono nell'`Hello` e sono ignorati con un avviso. Se vuoi etichette libere servono nel `Hello` (o in configurazione dell'agente): chiedo conferma.
- Rig rigido di default: se un agente elencato non è connesso la run non parte; `Controller:AllowPartialRig=true` la fa partire con gli agenti disponibili (avviso). Senza `rig.agents` si usano tutti gli agenti liberi con peso 1.
- `maxUsers` effettivo = minimo fra `rig.agents[].maxUsers` e il `max_users` dichiarato dall'agente.
- `rig.deployment`: file o cartelle (relativi alla cartella del load test); `partition: true` solo per `.csv` (intestazione a tutti, righe ripartite per peso, record con a capo tra virgolette integri). Le fette hanno hash per agente.
- `setupScript` / `cleanupScript` girano **sul controller** (`.sh` con /bin/sh, `.ps1` con pwsh/powershell, `.cmd/.bat`, o eseguibile), con tempo massimo e dentro la cartella del load test. Il protocollo non ha un campo per eseguirli sugli agenti: se serve, proposta di due messaggi `RunScript`/`ScriptResult`. Setup fallito = run `Failed`; cleanup fallito = avviso.
- `ipSwitching`: **non implementato** (avviso). Resta in sez. 10 come TODO.

**G18 — Sez. 1 (API JSON del controller).** Per CLI ed estensione VS Code: `GET /api/agents`, `POST /api/runs {loadTestPath, name?, seed?}` (202 `{runId}` oppure 400 `{errors[]}` con tutti i motivi), `GET /api/runs`, `GET /api/runs/{id}`, `POST /api/runs/{id}/stop?mode=graceful|abort`. Accesso: con `Controller:ApiToken` serve `Authorization: Bearer`; senza, solo loopback. Endpoint separati: gRPC agenti su `ListenUrl` (HTTP/2, https con certificato; mTLS opzionale con elenco di thumbprint), API su `ApiUrl` (default `http://127.0.0.1:5000`). Gli agenti si autenticano con `AgentTokens` o mTLS; senza nessuno dei due nessun agente è ammesso (salvo `AllowAnonymousAgents`, solo sviluppo). I risultati si leggono dal file SQLite (`ResultsDatabase`, default `WorkDirectory/results.db`) con `LoadTest.Reports`: nessuna API di lettura per ora (chat 4/8).

**G19 — Sez. 8 (stato finale della run e `runs.status`).** Valori scritti dal controller: `Completed`, `Aborted` (stopOnErrors, oppure un agente non ha completato/si è perso), `Cancelled` (stop richiesto dall'utente), `Failed` (pianificazione riuscita ma preparazione/setup/avvio falliti, o nessun agente ha concluso). `Failed` si aggiunge ai tre del contratto (G7). Gli avvisi degli agenti entrano in `runs.warnings_json` con il prefisso `[nome agente]`.

**G20 — Sez. 5 e 7 (profili di carico e contatori).** Il controller guida costante, a gradini e goal-based: ogni secondo calcola il totale per scenario e lo ripartisce **esattamente** fra gli agenti (resto maggiore, tetti per agente) mandando `UpdateLoad` solo quando cambia: questo risolve G12 (arrotondamento). I profili `custom` restano all'agente (quota applicata). Contatori per il goal, provvisori: `users.active`, `agents.cpu.avg`, `agents.cpu.max`; i counter set veri, le soglie e le macchine monitorate sono della chat 7 (punto di aggancio: la funzione `Counter` in `RunOrchestrator`).

**G21 — Sez. 3 e 12 (progetto Controller).** `LoadTest.Controller` è un eseguibile ASP.NET Core (`Microsoft.NET.Sdk.Web`, Grpc.AspNetCore) che dipende da Engine, Metrics, Plugins, Protocol e Reports. Va in sez. 12. Dato che Engine.Tests (sez. 11) lo referenzia, il progetto di test porta il framework ASP.NET Core in transitivo.

## B. Richieste ad altre chat e limiti noti
1. **Chat 4 (Reports):** accettare `Failed` in `runs.status` (G7/G19); l'allineamento degli intervalli fra agenti si basa su `interval_index` (ogni agente conta dal proprio fine-warmup; l'avvio sincronizzato li tiene vicini, ma non c'è un riallineamento a posteriori).
2. **Chat 5:** adattatore per `IPluginCompiler` (G16).
3. **Chat 2/3:** `user_id_base` resta non applicabile nell'agente (G11); il controller lo assegna comunque (`indice × 1.000.000`).
4. **Limiti:** le run concluse stanno solo in memoria (si perdono al riavvio del controller; i risultati restano su SQLite); nessun limite di retention; i CSV partizionati si leggono interi in memoria; una run alla volta per agente, più run in parallelo solo su agenti diversi.

## C. Sez. 10 (copertura)
- «Rig: agenti con peso, attributi, deployment items, setup e cleanup»: TODO → **WIP** (manca IP switching e l'esecuzione degli script sugli agenti).
- «Esecuzione: CLI e integrazione CI»: resta TODO (parte 4; l'API del controller è pronta).
- «Profili costante, a gradini, goal-based, custom»: il lato controller dei profili goal è fatto; contatori reali dalla chat 7.
