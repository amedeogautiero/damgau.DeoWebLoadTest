# DeoLoadTest – Revisione e gestione errori

# Task: Revisione e correzione della gestione degli errori nell'architettura distribuita di DeoLoadTest

## Contesto

DeoLoadTest è un sistema di Web Load Testing composto da diversi progetti:

- `LoadTest.Cli`
- `LoadTest.Controller`
- `LoadTest.Agent`
- `LoadTest.Engine`
- `LoadTest.Protocol`
- altri progetti di supporto.

Il sistema supporta due modalità di esecuzione:

1. **Locale:** la CLI esegue direttamente il load test.
2. **Distribuita:** la CLI comunica con il Controller attraverso API JSON HTTP, mentre il Controller coordina uno o più Agent attraverso gRPC.

Attualmente è stata riscontrata un'incoerenza nella gestione degli errori: in alcune circostanze il comando CLI termina senza fornire informazioni sufficienti all'utente.

Vogliamo rendere la gestione degli errori coerente, affidabile e uniforme in tutta l'architettura.

## 1. Obiettivo generale

Analizzare e correggere la propagazione degli errori lungo tutta la catena:

`CLI → Controller → Agent → Controller → CLI`

Nessun comando CLI deve terminare silenziosamente in presenza di un errore.

Ogni errore deve:
- essere rilevato dal componente responsabile;
- essere rappresentato in modo strutturato;
- essere propagato al componente chiamante;
- arrivare alla CLI con un messaggio comprensibile;
- produrre un codice di uscita coerente.

Non vogliamo semplicemente aggiungere messaggi di log: vogliamo correggere il meccanismo di comunicazione degli errori.

## 2. Distinguere le responsabilità dei componenti

È importante non demandare all'Agent controlli che devono essere effettuati dalla CLI o dal Controller.

### CLI

La CLI è responsabile dei controlli sui file locali prima di iniziare la comunicazione remota.

Esempi:
- file YAML inesistente;
- file non leggibile;
- directory del progetto inesistente;
- impossibilità di creare lo ZIP;
- dimensione dello ZIP superiore al limite consentito;
- parametri CLI non validi.

In questi casi deve produrre immediatamente un messaggio esplicito, senza coinvolgere Controller e Agent.

### Controller

Il Controller è responsabile della validazione delle informazioni ricevute dalla CLI e dell'orchestrazione della run.

Esempi:
- progetto caricato ma archivio non valido;
- file YAML non presente nel progetto caricato;
- YAML non valido;
- riferimenti a file inesistenti;
- configurazione del rig non valida;
- nessun Agent disponibile;
- impossibilità di assegnare il lavoro agli Agent;
- timeout durante l'avvio;
- errore durante l'orchestrazione;
- impossibilità di recuperare i risultati.

Il Controller deve comunicare gli errori alla CLI attraverso le API JSON.

### Agent

L'Agent è responsabile degli errori che si verificano durante la preparazione e l'esecuzione del lavoro assegnato.

Esempi:
- impossibilità di scaricare o estrarre il progetto;
- file richiesto non disponibile nell'ambiente di lavoro;
- plugin non valido o non caricabile;
- impossibilità di inizializzare il motore;
- errore durante l'esecuzione del test;
- perdita della connessione con il Controller;
- impossibilità di inviare metriche o risultati.

L'Agent deve comunicare gli errori al Controller attraverso gRPC.

Il Controller deve raccogliere queste informazioni e renderle disponibili alla CLI.

## 3. Propagazione degli errori

Analizzare l'attuale implementazione di:

- `CliApp`
- `RunCommand`
- `RemoteRunner`
- `ControllerClient`
- API JSON del Controller
- `RunManager`
- `RunOrchestrator`
- servizi gRPC
- protocolli e messaggi gRPC
- Agent e relativo ciclo di esecuzione.

Verificare che tutte le eccezioni e gli errori applicativi vengano gestiti correttamente.

In particolare:

**A. Errori sincroni**

Quando un'operazione fallisce durante una chiamata HTTP o gRPC, il componente chiamante deve ricevere un errore strutturato con:
- codice identificativo;
- messaggio comprensibile;
- componente che ha generato l'errore;
- eventuali dettagli tecnici;
- eventuale identificativo della run.

**B. Errori asincroni**

Quando una run è già stata avviata e successivamente si verifica un errore nell'Agent:

1. L'Agent comunica l'errore al Controller.
2. Il Controller aggiorna lo stato della run.
3. Il Controller conserva il dettaglio dell'errore.
4. La CLI recupera lo stato aggiornato attraverso il polling.
5. La CLI visualizza il messaggio di errore.
6. La CLI termina con un codice di uscita coerente.

Non deve essere sufficiente comunicare genericamente `Failed`: vogliamo conoscere la causa effettiva del fallimento.

## 4. Modello uniforme degli errori

Verificare se esiste già un modello condiviso per rappresentare gli errori.

Se non esiste, progettare un modello coerente, possibilmente condiviso tra i progetti attraverso `LoadTest.Contracts` o il progetto appropriato.

Il modello dovrebbe prevedere almeno:

- `Code`
- `Message`
- `Source`
- `Details`
- `RunId`
- `Timestamp`

Non duplicare inutilmente modelli equivalenti nei diversi progetti.

Utilizzare i meccanismi HTTP e gRPC appropriati per trasportare queste informazioni.

Per HTTP valutare l'utilizzo di Problem Details, mantenendo la compatibilità con l'API esistente.

Per gRPC utilizzare status code e dettagli strutturati, evitando di affidarsi esclusivamente al testo delle eccezioni.

## 5. Codici di uscita della CLI

Mantenere una classificazione coerente con quella già prevista:

| Codice | Significato |
|---|---|
| 0 | Operazione completata |
| 1 | Run fallita o soglie non rispettate |
| 2 | Argomenti, configurazione o file non validi |
| 3 | Errore infrastrutturale o di comunicazione |
| 130 | Operazione interrotta dall'utente |

Verificare che i codici siano applicati coerentemente in tutti i comandi.

Non trasformare indistintamente tutti gli errori in codice 3.

## 6. Messaggi CLI

I messaggi devono essere chiari e immediatamente comprensibili.

Esempi desiderati:

```text
errore: file YAML non trovato:
C:\...\primo-test.loadtest.yaml
```

```text
errore: il progetto è stato caricato, ma il file YAML
primo-test.loadtest.yaml non è presente nell'archivio.
```

```text
errore: nessun Agent disponibile per eseguire il load test.
```

```text
errore: Agent HW003897: impossibile caricare il plugin MyPlugin.
```

```text
errore: timeout durante l'avvio della run.
```

Durante una run distribuita, la CLI deve mostrare:
- identificativo della run;
- stato corrente;
- eventuali errori;
- motivo del fallimento al termine.

Non nascondere gli errori anche quando è attiva la modalità `--quiet`, salvo che il comportamento sia esplicitamente richiesto per output machine-readable. In tal caso gli errori devono comunque essere restituiti tramite il canale di errore o nel formato strutturato previsto.

## 7. Test automatici richiesti

Aggiungere o aggiornare i test unitari e di integrazione.

### Test CLI

1. File YAML inesistente.
2. File YAML non valido.
3. Directory del progetto inesistente.
4. Errore durante la creazione dello ZIP.
5. Controller non raggiungibile.
6. Risposta HTTP di errore.
7. Errore durante il polling.
8. Run fallita sul Controller.
9. Run fallita su Agent.
10. Interruzione tramite Ctrl+C.

### Test Controller

1. Upload di archivio non valido.
2. File YAML non presente nel progetto caricato.
3. YAML non valido.
4. Nessun Agent disponibile.
5. Timeout durante l'avvio.
6. Errore comunicato da un Agent.
7. Perdita di connessione con un Agent.
8. Recupero del dettaglio dell'errore attraverso le API.

### Test Agent

1. Progetto non disponibile.
2. File richiesto non trovato.
3. Plugin non caricabile.
4. Errore di inizializzazione del motore.
5. Errore durante l'esecuzione.
6. Impossibilità di comunicare l'errore al Controller.

### Test end-to-end

Realizzare almeno un test che simuli questa sequenza:

```text
CLI
  |
  | Upload progetto valido
  v
Controller
  |
  | Assegnazione lavoro
  v
Agent
  |
  | Errore durante l'esecuzione
  v
Controller
  |
  | Stato Failed + dettaglio errore
  v
CLI
  |
  | Visualizzazione errore
  v
Exit code coerente
```

Verificare che il messaggio originale dell'Agent arrivi effettivamente alla CLI.

## 8. Vincoli di implementazione

- Non modificare il comportamento funzionale dei load test.
- Non alterare il protocollo di esecuzione se non necessario.
- Mantenere la retrocompatibilità delle API, ove possibile.
- Non introdurre dipendenze circolari tra progetti.
- Evitare duplicazioni di modelli e logiche di gestione degli errori.
- Non utilizzare eccezioni generiche per nascondere errori applicativi.
- Non perdere lo stack trace nei log diagnostici.
- Non esporre informazioni sensibili nei messaggi restituiti alla CLI.
- Mantenere compatibilità con la modalità locale e distribuita.

## 9. Modalità di lavoro richiesta

Prima di modificare il codice:

1. Analizza l'architettura attuale.
2. Ricostruisci il percorso degli errori nei diversi componenti.
3. Individua i punti in cui gli errori vengono persi, trasformati o non propagati.
4. Individua eventuali differenze tra errori sincroni e asincroni.
5. Presenta una breve relazione con i problemi individuati e la soluzione proposta.

Successivamente procedi con l'implementazione.

Al termine:

1. Esegui `dotnet build` della soluzione.
2. Esegui tutti i test esistenti.
3. Esegui i nuovi test.
4. Correggi eventuali regressioni.
5. Riporta l'elenco dei file modificati.
6. Descrivi le modifiche architetturali effettuate.
7. Riporta i risultati di build e test.
8. Indica eventuali problemi rimasti aperti.

**Importante:** non limitarti a rendere più verbosi i messaggi della CLI. L'obiettivo è garantire una propagazione affidabile degli errori dall'origine fino al chiamante, in tutta l'architettura distribuita di DeoLoadTest.