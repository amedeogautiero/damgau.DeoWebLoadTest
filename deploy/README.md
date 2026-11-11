# Pubblicazione e installazione — agente e CLI

Due eseguibili, entrambi **self-contained** (nessun .NET da installare), **file singolo** compresso e senza trimming:

| Eseguibile | Cosa contiene | RID |
|---|---|---|
| `loadtest-agent` | solo l'agente (leggero, senza ASP.NET Core né SQLite) | win-x64 · linux-x64 · linux-arm64 · linux-arm · osx-arm64 · osx-x64 |
| `loadtest` | CLI completa: `run`, `validate`, `report`, `controller`, `agent`, … | win-x64 · linux-x64 · linux-arm64 · osx-arm64 · osx-x64 |

`linux-arm` (Raspberry a 32 bit) è solo agente: la CLI porta ASP.NET Core e SQLite nativo. `InvariantGlobalization` è attivo: non serve ICU (la sua assenza fa fallire l'avvio su Linux minimali).

## 1. Produrre i pacchetti

```bash
build/publish.sh 0.2.0                      # tutti i RID  → artifacts/packages + artifacts/SHA256SUMS
ONLY="linux-x64 win-x64" build/publish.sh   # solo alcuni RID
```
```powershell
build\publish.ps1 -Version 0.2.0 [-Only linux-x64,win-x64]
```
Serve il .NET SDK 10 e la rete al primo publish (runtime pack). Windows → `.zip`; Linux e macOS → `.tar.gz` (conserva il bit di esecuzione). Verifica con `SHA256SUMS`.

## 2. Installare l'agente come servizio

Estrai il pacchetto per la tua piattaforma e lancia, **come root/amministratore**:

```bash
# Linux e macOS
sudo LOADTEST_AGENT_TOKEN='<token>' ./loadtest-agent install --controller https://controller.interno:5001 --name pi-1
```
```powershell
# Windows (terminale come amministratore)
$env:LOADTEST_AGENT_TOKEN = '<token>'
.\loadtest-agent.exe install --controller https://controller.interno:5001 --name pi-1
```
Il token meglio dalla variabile `LOADTEST_AGENT_TOKEN` che da `--token`: non resta nella cronologia della shell né in `ps`.
`--dry-run` mostra tutti i passi senza eseguirne nessuno. Rilanciare `install` **aggiorna** binario e configurazione (il servizio viene fermato e riavviato).

| | Linux | macOS | Windows |
|---|---|---|---|
| Servizio | unit systemd `loadtest-agent` (`Type=notify`, `Restart=always`) | LaunchDaemon `com.loadtest.agent` (`KeepAlive`) | servizio `LoadTestAgent`, avvio automatico, riavvio dopo i guasti |
| Utente | `loadtest` (creato, senza login) | `_loadtest` (creato, nascosto) | `NT AUTHORITY\LocalService` |
| Eseguibile | `/opt/loadtest-agent/` | `/usr/local/lib/loadtest-agent/` | `%ProgramFiles%\LoadTest Agent\` |
| Dati (cache, run) | `/var/lib/loadtest-agent` | `/var/lib/loadtest-agent` | `%ProgramData%\LoadTest Agent` |
| Configurazione | `<eseguibile>/appsettings.json`, modo **600**, dell'utente del servizio | idem | idem, ACL: solo SYSTEM, Administrators e l'account del servizio |
| Stato | `systemctl status loadtest-agent` | `sudo launchctl print system/com.loadtest.agent` | `sc.exe query LoadTestAgent` |
| Log | `journalctl -u loadtest-agent -f` | `/var/log/loadtest-agent/agent.log` | Visualizzatore eventi → Applicazione |

Il token sta **solo** in `appsettings.json` (non nell'unit/plist, che sono leggibili da tutti). Limiti già alzati: Linux `LimitNOFILE=1048576`, `TasksMax=infinity`; macOS 65536 file aperti. Sicurezza dell'unit: `NoNewPrivileges`, `ProtectSystem=strict` (scrive solo nella cartella dati), `ProtectHome`, `PrivateTmp`.

Opzioni: `--install-dir`, `--work-dir`, `--user`, `--agent-id`, `--max-users`, `--client-cert <pfx> --client-cert-password` (mTLS: il file deve essere leggibile dall'utente del servizio), `--insecure` (solo sviluppo, accetta qualsiasi certificato), `--no-start`.

### macOS
I binari scaricati hanno la quarantena e su Apple Silicon devono essere firmati: `install` toglie la quarantena e firma «ad hoc». Se estrai a mano: `chmod +x loadtest-agent`.

### Disinstallare
```bash
sudo ./loadtest-agent uninstall            # ferma e rimuove servizio ed eseguibile, tiene cache e dati
sudo ./loadtest-agent uninstall --purge    # elimina anche la cartella dati
```
Su Windows esegui `uninstall` da una **copia** dell'eseguibile fuori dalla cartella di installazione (un programma in uso non può cancellarsi: il passo segnala un avviso e va rimosso a mano).

## 3. Configurazione dell'agente (sezione `Agent`)

`ControllerUrl`, `Token`, `Name`, `AgentId`, `MaxUsers`, `WorkDirectory`, `ClientCertificatePath`, `ClientCertificatePassword`, `AllowInvalidServerCertificate`, `HeartbeatSec`, `ReconnectMinSec`, `ReconnectMaxSec`, `MaxEntryBytes`, `MaxRunBytes`, `CacheMaxBytes` (2 GiB), `CacheMaxAgeDays` (14), `KeepRunFolders`. Stesse chiavi come variabili `Agent__Chiave` o argomenti `--Agent:Chiave=valore`.

**Cache per hash:** all'inizio di ogni preparazione (solo ad agente libero) si segnano come usate le voci del manifest e si eliminano quelle non usate da più di `CacheMaxAgeDays` giorni; se resta sopra `CacheMaxBytes`, le meno recenti. Le voci della run in preparazione non si toccano mai. `0` = nessun limite.

## 4. Taratura per generare carico

- **Linux:** i limiti per processo sono già nell'unit. Per molte connessioni in uscita allarga le porte effimere: `sysctl -w net.ipv4.ip_local_port_range="10240 65535"` e, se serve, `net.ipv4.tcp_tw_reuse=1`.
- **Windows:** le porte effimere sono ~16.000: per carichi grandi `netsh int ipv4 set dynamicport tcp start=10000 num=55000` e valuta `TcpTimedWaitDelay` (30 s).
- **Raspberry (linux-arm / linux-arm64):** `max_users` basso nel rig (es. 40) e controlla `cpuPercent` con `loadtest agents`: oltre l'80% le misure risultano «agente saturo».
- Firewall: l'agente apre **solo connessioni in uscita** verso il controller (porta `ListenUrl`, di default 5001): nessuna porta in ingresso.

## 5. Controller come servizio

`loadtest controller` (CLI completa) si integra con systemd (`Type=notify`) e con il Service Control Manager. Esempio Linux, `/etc/systemd/system/loadtest-controller.service`:

```ini
[Unit]
Description=LoadTest controller
Wants=network-online.target
After=network-online.target

[Service]
Type=notify
ExecStart=/opt/loadtest/loadtest controller --listen https://0.0.0.0:5001 --cert /etc/loadtest/server.pfx --work-dir /var/lib/loadtest-controller
# segreti fuori dalla riga di comando: chmod 600, proprietario root
EnvironmentFile=/etc/loadtest/controller.env
User=loadtest
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```
con `/etc/loadtest/controller.env`:
```
Controller__CertificatePassword=...
Controller__AgentTokens__0=<token-agente>
Controller__ApiToken=<token-api>
```
(`install` per il controller non c'è: la configurazione del servizio è questa; su Windows `sc.exe create LoadTestController binPath= "\"C:\...\loadtest.exe\" controller --listen ..."`.)

## 6. Risoluzione dei problemi

| Sintomo | Causa probabile |
|---|---|
| `errore: servono privilegi` | lancia con `sudo` / terminale come amministratore |
| l'agente si riconnette ogni 30 s | token errato o certificato del controller non valido (`journalctl`/log: «rifiutato», «Unauthenticated») |
| `agent_id già …`/sostituzioni continue | due macchine con lo stesso `AgentId` (clonate): togli `AgentId` e cancella `agent-id` nella cartella dati |
| il servizio parte e si ferma (Windows) | l'account del servizio non può leggere `ClientCertificatePath` o scrivere nella cartella dati |
| SmartScreen avvisa all'apertura | gli eseguibili non sono firmati (vedi limiti in `PROPOSTE-CONTRATTO-chat6-parte5.md`) |
