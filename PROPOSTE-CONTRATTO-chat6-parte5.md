# Proposte di modifica al CONTRATTO v0.1 — dalla chat "Controller e agenti" (componente 6), parte 5: Publish e servizi OS

Da portare alla chat **Architettura**. Nulla è applicato a CONTRATTO.md, né a Sdk/Engine/Metrics/Reports/Plugins/Protocol. Seguono G1–G24 delle parti precedenti. Chiude la componente 6.

## A. Modifiche richieste

**G25 — Sez. 1 e 6 (servizi del sistema operativo).** `loadtest-agent install | uninstall` registra l'agente come servizio, senza script per sistema: il piano è calcolato in modo puro (`ServicePlanner`), mostrabile con `--dry-run`, ed eseguito da `PlanExecutor`. Layout: Linux unit systemd `loadtest-agent` (`Type=notify`, `Restart=always`, utente `loadtest`, `/opt/loadtest-agent`, dati in `/var/lib/loadtest-agent`); macOS LaunchDaemon `com.loadtest.agent` (utente nascosto `_loadtest`, firma ad hoc e rimozione della quarantena); Windows servizio `LoadTestAgent` (LocalService, ripartenza automatica 5 s/5 s/30 s). Il servizio funziona anche da terminale: nessuna dipendenza dal gestore di servizi. Nomi di utente e percorsi sono validati prima di finire in comandi o file. Dettaglio e tabella nel `deploy/README.md`.

**G26 — Sez. 1 (publish per RID; chiude G24).** Due binari: `loadtest-agent` (leggero, tutti e sei i RID, compreso `linux-arm`) e `loadtest` (CLI completa con controller, cinque RID: **non** `linux-arm`, per ASP.NET Core e SQLite nativo). Self-contained, file singolo compresso, **senza trimming** (reflection, YAML, plugin), `InvariantGlobalization` (niente ICU). `linux-arm` passa da «da confermare» a «supportato per l'agente, da provare su hardware reale».

**G27 — Sez. 7 (configurazione e segreti dell'agente).** Il token e la password del certificato stanno solo in `appsettings.json` accanto all'eseguibile: creato con permessi 600 alla creazione (mai un istante leggibile da altri), proprietario l'utente del servizio; su Windows ACL ristretta. Mai nell'unit/plist. `install` legge il token anche da `LOADTEST_AGENT_TOKEN`. L'agente legge la configurazione dalla cartella dell'eseguibile (radice del contenuto), non dalla directory corrente (un servizio Windows parte da System32).

**G28 — Sez. 1 (cache per hash: eliminazione; chiude G13).** All'inizio di ogni `PrepareRun`, solo ad agente libero: le voci del manifest vengono segnate come usate (ultima modifica) e poi si eliminano le voci non usate da più di `CacheMaxAgeDays` (14) e, sopra `CacheMaxBytes` (2 GiB), le meno recenti. Le voci del manifest in preparazione non si toccano mai; i pacchetti plugin di una run in corso non si possono eliminare perché la run non c'è (l'agente è libero).

**G29 — Sez. 3 e 12 (nomi e cartelle).** L'assembly dell'agente si chiama `loadtest-agent` (la CLI resta `loadtest`). Fuori da `src/` ci sono ora `build/` (script di publish) e `deploy/` (guida): la sez. 12 parla solo di `src/{progetto}`, va estesa. L'elenco di sez. 12 deve includere `LoadTest.Protocol`, `LoadTest.Agent`, `LoadTest.Controller`, `LoadTest.Cli` (G1, G9, G21, G24).

## B. Cosa NON c'è (e perché)
1. **Firma del codice:** gli eseguibili non sono firmati. Windows: SmartScreen avvisa; macOS: solo firma ad hoc (niente notarizzazione); servono certificati che non ho.
2. **Pacchetti nativi** (MSI, .deb, .rpm, .pkg): `install` copre il caso d'uso senza dipendenze; i pacchetti si possono costruire sopra gli stessi artefatti se servono a un'azienda.
3. **Aggiornamento automatico dell'agente:** si aggiorna rilanciando `install` (anche da Ansible/SSH). Un auto-update dal controller (con verifica dell'hash) è una funzione a parte.
4. **Installazione del controller come servizio:** niente `install` dedicato; unit di esempio e variabili d'ambiente nel README (il controller è uno solo, di solito sotto un orchestratore).
5. **Prova su sistemi reali:** il codice non è mai stato eseguito (nessun dotnet nell'ambiente), nemmeno gli script e i comandi di sistema (`systemctl`, `launchctl`, `sc.exe`, `dscl`, `icacls`). I test coprono il piano (passi, contenuti, permessi, ordine) e l'esecutore con comandi simulati. **Prima di usarlo su una flotta: prova su una macchina per sistema operativo con `--dry-run` e poi senza.**
6. **Compilazione `.csx` sul controller (G16)** e **`ICodeStepHost`/`user_id_base` (G11)** restano aperti: dipendono dalle chat 5 e 2/3.

## C. Sez. 10 (copertura)
- «Agenti multipiattaforma (Linux, macOS, Raspberry), cache plugin per hash»: WIP → **OK a codice, da provare su hardware reale** (eviction della cache fatta; mancano le prove sui sei RID).
- «Esecuzione: CLI e integrazione CI»: WIP (invariato).
- «Rig»: invariato (IP switching e script sugli agenti non fatti).

## D. Stato della componente 6 (le cinque parti)
| Parte | Contenuto | Note aperte |
|---|---|---|
| 1 Protocollo | `.proto`, handshake, metriche sul filo | G1–G8 |
| 2 Agente | worker, cache, esecuzione, profili | G9–G14; richieste all'Engine (G11) |
| 3 Controller | rig, pacchetti, run distribuita, API | G15–G21; adattatore `.csx` (G16) |
| 4 CLI e CI | `loadtest`, soglie, JUnit/JSON/CSV, upload progetto | G22–G24 |
| 5 Publish e servizi | RID, `install`/`uninstall`, eviction | G25–G29 |
Tutto il codice è **non compilato**: il primo `dotnet build` è la vera verifica; i punti incerti noti sono elencati in fondo a ogni `MANIFEST-CONTROLLER-n.md`.
