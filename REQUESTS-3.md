# DeoLoadTest – Gestione HTTP Session

Devi implementare nel progetto `damgau.DeoWebLoadTest` una nuova funzionalità per configurare il ciclo di vita della HTTP Session durante un Load Test.

## 1. Obiettivo

Attualmente il progetto crea un `HttpClientExecutor` per ogni Virtual User e lo riutilizza nelle iterazioni successive.

Questo comporta che il `CookieContainer` venga mantenuto tra un'iterazione e l'altra.

Vogliamo rendere configurabile questo comportamento attraverso una nuova proprietà `session.scope`, definita nel file `.loadtest.yaml`, a livello di scenario.

La sessione HTTP deve avere un ciclo di vita indipendente da quello del Virtual User.

## 2. Nuova configurazione YAML

Introdurre la seguente configurazione:

```yaml
scenarios:
  - name: NavigazioneWeb

    session:
      scope: iteration

```

Sono previsti due valori:

### iteration

Viene creata una nuova HTTP Session all'inizio di ogni iterazione del Virtual User.

La sessione comprende almeno:
- HttpClientExecutor.
- CookieContainer.
- Connessioni HTTP e relativo stato gestito dall'executor.

La sessione deve essere utilizzata da tutte le richieste HTTP appartenenti alla stessa iterazione.

Al termine dell'iterazione, la sessione deve essere rilasciata correttamente.

Alla successiva iterazione deve essere creata una nuova sessione, senza riutilizzare i cookie precedenti.

### virtualUser

La HTTP Session viene creata quando nasce il Virtual User e viene riutilizzata per tutte le sue iterazioni.

I cookie e lo stato HTTP vengono quindi mantenuti tra un'iterazione e l'altra.

Questa modalità deve conservare il comportamento attualmente implementato.

## 3. Compatibilità

Se la proprietà `session.scope` non viene specificata, il comportamento deve rimanere quello attuale:

```yaml
session:
  scope: virtualUser
```

Non devono essere introdotte regressioni nei test esistenti.

Non modificare il significato di `newUsersPercent`: deve continuare a governare la creazione di nuovi Virtual User secondo la logica attuale.

Il ciclo di vita della HTTP Session deve essere indipendente da `newUsersPercent`.

## 4. Condivisione della sessione durante Call

È fondamentale mantenere il comportamento attuale delle chiamate `Call`.

Esempio:

Il test principale esegue:

1. GET `/Home`.
2. Call al test secondario `Login`.
3. GET `/Home`.

Il test secondario esegue:

1. GET `/Login`.
2. POST `/Login`, ricevendo il cookie di autenticazione.

La terza richiesta del test principale deve utilizzare lo stesso CookieContainer del test secondario.

Quindi:
- Una singola iterazione deve avere una sola HTTP Session.
- Tutti i WebTest eseguiti attraverso `Call` durante quella iterazione devono condividere la stessa HTTP Session.
- Una nuova iterazione con `scope: iteration` deve ricevere una nuova HTTP Session.

Non creare un nuovo executor quando viene eseguito un `Call`.

## 5. Separazione delle responsabilità

Verifica attentamente l'attuale implementazione di:

- LoadTestRunner.
- WebTestRunner.
- HttpClientExecutor.
- LoadTestRunnerOptions.
- ScenarioDefinition e relative classi di configurazione YAML.

La nuova implementazione deve separare chiaramente:

- Vita del Virtual User.
- Vita del contesto del Virtual User.
- Vita della HTTP Session.
- Vita della singola iterazione.

Non confondere il contesto del Virtual User con il CookieContainer.

Le variabili e i dati associati al Virtual User devono continuare a funzionare secondo la semantica attuale.

La nuova HTTP Session deve essere condivisa da tutti i WebTest appartenenti alla stessa iterazione, comprese le chiamate `Call`.

## 6. Gestione delle risorse

Verifica che gli executor e le relative risorse vengano rilasciati correttamente.

In particolare:
- Con `scope: iteration`, ogni iterazione deve rilasciare il proprio executor al termine.
- Con `scope: virtualUser`, l'executor deve essere rilasciato quando termina il Virtual User.
- Gestire correttamente cancellazioni, eccezioni e terminazioni anticipate.
- Evitare leak di connessioni o risorse.

## 7. Test automatici

Aggiungi test automatici per verificare almeno questi casi.

### Test A - Sessione persistente

Configurazione:

```yaml
session:
  scope: virtualUser
```

Un Virtual User esegue tre iterazioni.

La prima richiesta di ogni iterazione deve verificare se esiste un cookie precedentemente impostato.

Risultato atteso:
- Iterazione 1: cookie assente.
- Iterazione 2: cookie presente.
- Iterazione 3: cookie presente.

### Test B - Sessione per iterazione

Configurazione:

```yaml
session:
  scope: iteration
```

Un Virtual User esegue tre iterazioni.

Risultato atteso:
- Iterazione 1: cookie assente.
- Iterazione 2: cookie assente.
- Iterazione 3: cookie assente.

Ogni iterazione deve ricevere una nuova HTTP Session.

### Test C - Condivisione sessione con Call

Configurazione:

```yaml
session:
  scope: iteration
```

Il test principale richiama un test secondario che effettua il login e riceve un cookie.

La successiva richiesta del test principale deve ricevere lo stesso cookie.

Verificare che il CookieContainer sia condiviso tra test principale e secondario.

### Test D - Isolamento tra Virtual User

Con almeno due Virtual User, verificare che i cookie non vengano condivisi tra utenti differenti.

### Test E - Default

Senza specificare `session.scope`, verificare che venga mantenuto il comportamento attuale (`virtualUser`).

### Test F - Configurazione non valida

Un valore non riconosciuto, ad esempio:

```yaml
session:
  scope: invalid
```

deve generare un errore di configurazione chiaro, non essere ignorato silenziosamente.

## 8. Documentazione

Aggiorna la documentazione del formato `.loadtest.yaml`, inserendo:
- Descrizione di `session.scope`.
- Valori ammessi.
- Valore predefinito.
- Esempi YAML.
- Differenza tra `iteration` e `virtualUser`.
- Relazione con `newUsersPercent`.

## 9. Modalità di lavoro

Prima di modificare il codice:
1. Analizza l'architettura attuale.
2. Individua esattamente dove viene creato e mantenuto l'HttpClientExecutor.
3. Individua come viene gestito il ciclo di vita delle iterazioni.
4. Individua come `Call` condivide attualmente l'EngineServices e l'executor.

Poi implementa la soluzione rispettando l'architettura esistente.

Non limitarti a modificare i test per farli passare: il comportamento deve essere effettivamente verificato.

Al termine:
- Esegui tutti i test automatici.
- Segnala i file modificati.
- Descrivi le modifiche architetturali.
- Riporta eventuali problemi o decisioni progettuali che richiedono una mia valutazione.