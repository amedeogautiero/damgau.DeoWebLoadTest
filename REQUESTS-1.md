# DeoLoadTest – Elenco e confronto delle esecuzioni

## Contesto

Stiamo sviluppando DeoLoadTest, un tool di Web Load Testing in .NET/C#, progettato per recuperare e ampliare le funzionalità del precedente Microsoft Visual Studio Web Load Test.

Repository: `amedeogautiero/damgau.DeoWebLoadTest`

La soluzione contiene già un sistema di esecuzione dei test e di persistenza dei risultati tramite SQLite.

Attualmente il comando `report` permette di generare un report relativo a una singola esecuzione. Se non viene specificato un identificativo, viene selezionata automaticamente l'esecuzione più recente.

Il database è progettato per conservare più esecuzioni, identificate tramite `run_id`.

Il repository SQLite sembra già esporre un metodo `ListRuns()`, che dovrà essere verificato e riutilizzato.

## Obiettivo

Implementare due nuove funzionalità nella CLI di DeoLoadTest:

1. Elenco delle esecuzioni presenti nel database.
2. Confronto delle metriche tra due esecuzioni differenti.

Prima di modificare il codice, analizza l'implementazione attuale e individua le classi e i servizi già disponibili. Riutilizza il più possibile il codice esistente.

Non introdurre nuove dipendenze se non strettamente necessarie.

---

# 1. Elenco delle esecuzioni

Aggiungere un nuovo comando CLI:

```powershell
dotnet run --project src/LoadTest.Cli -- runs --db results/primo-test.db
```

Il comando deve mostrare tutte le esecuzioni presenti nel database.

Esempio di output desiderato:

```text
DeoLoadTest - Test Runs
Database: results/primo-test.db

ID          NAME                 STARTED              STATUS     DURATION
--------------------------------------------------------------------------------
a81f32c1    Primo test            2026-11-11 10:30:00  Completed  00:03:00
b72e41d2    Test autenticazione  2026-11-11 11:15:00  Completed  00:05:00
c93f52e3    Test API              2026-11-11 12:00:00  Failed     00:01:23

Total runs: 3
```

Le informazioni visualizzate devono essere ricavate dai dati realmente disponibili nel database.

Requisiti:

- Elencare tutte le esecuzioni.
- Ordinare dalla più recente alla più vecchia.
- Mostrare almeno:
  - Run ID.
  - Nome del test.
  - Data e ora di avvio.
  - Stato dell'esecuzione.
  - Durata, se disponibile.
- Gestire correttamente database inesistenti o senza esecuzioni.
- Non interrompere l'applicazione con eccezioni non gestite.
- Supportare, se coerente con l'architettura CLI esistente, un'opzione per limitare il numero di risultati.

Esempio:

```powershell
dotnet run --project src/LoadTest.Cli -- runs --db results/primo-test.db --limit 10
```

---

# 2. Confronto tra due esecuzioni

Aggiungere un nuovo comando CLI:

```powershell
dotnet run --project src/LoadTest.Cli -- compare --db results/primo-test.db --run1 ID_RUN_1 --run2 ID_RUN_2
```

Il comando deve confrontare due esecuzioni dello stesso test o di test differenti.

L'obiettivo è consentire di capire rapidamente se una modifica all'applicazione, all'infrastruttura o alla configurazione del test ha migliorato oppure peggiorato le prestazioni.

## Metriche da confrontare

Utilizzare le metriche già persistite nel database.

Verificare quali sono effettivamente disponibili e confrontare almeno quelle seguenti, quando presenti:

| Metrica | Descrizione |
|---|---|
| Total Requests | Numero totale di richieste |
| Successful Requests | Richieste completate correttamente |
| Failed Requests | Richieste fallite |
| Requests/sec | Throughput medio |
| Average Response Time | Tempo medio di risposta |
| Minimum Response Time | Tempo minimo |
| Maximum Response Time | Tempo massimo |
| P50 | 50° percentile |
| P90 | 90° percentile |
| P95 | 95° percentile |
| P99 | 99° percentile |
| Error Rate | Percentuale di errori |

Non inventare metriche mancanti: se una metrica non è disponibile, indicarlo chiaramente.

## Output desiderato

Esempio:

```text
DeoLoadTest - Run Comparison

RUN 1
Name: Test API
ID: a81f32c1
Started: 2026-11-11 10:30:00

RUN 2
Name: Test API
ID: b72e41d2
Started: 2026-11-11 11:15:00

METRIC                     RUN 1       RUN 2       DIFFERENCE
----------------------------------------------------------------
Total Requests             10,000      12,000      +20.00%
Requests/sec               100.00      120.00      +20.00%
Average Response Time      120 ms      95 ms       -20.83%
P95 Response Time          250 ms      180 ms      -28.00%
P99 Response Time          500 ms      350 ms      -30.00%
Failed Requests            100         20          -80.00%
Error Rate                 1.00%       0.17%       -83.00%

OVERALL RESULT

The second execution shows improved performance.

- Throughput: improved
- Average response time: improved
- P95: improved
- Error rate: improved
```

Le percentuali devono essere calcolate correttamente.

Per le metriche in cui un valore inferiore è migliore, come tempi di risposta ed error rate, una diminuzione deve essere interpretata come miglioramento.

Per il throughput, normalmente un aumento è positivo.

Per il numero di richieste completate, non esprimere automaticamente un giudizio positivo o negativo: il valore dipende anche dalla durata e dalla configurazione del test.

Attenzione: confrontare esecuzioni con durate o carichi differenti può produrre conclusioni fuorvianti. Mostrare sempre i parametri principali delle due run e segnalare eventuali differenze significative.

---

# 3. Esportazione CSV

Valutare la possibilità di esportare il confronto in CSV:

```powershell
dotnet run --project src/LoadTest.Cli -- compare --db results/primo-test.db --run1 ID_RUN_1 --run2 ID_RUN_2 --csv results/confronto.csv
```

Il CSV deve contenere:

- Nome metrica.
- Valore Run 1.
- Valore Run 2.
- Differenza assoluta.
- Differenza percentuale.

Utilizzare le convenzioni già presenti nel progetto per l'esportazione CSV, se disponibili.

---

# 4. Architettura e qualità del codice

Rispettare rigorosamente le convenzioni architetturali già presenti.

In particolare:

- La CLI deve occuparsi solamente di parsing degli argomenti e presentazione dei risultati.
- La logica di confronto non deve essere implementata direttamente dentro `Program.cs` o nel comando CLI.
- Riutilizzare `SqliteResultsRepository` e gli eventuali servizi di analisi già presenti.
- Se necessario, introdurre un servizio dedicato al confronto, collocandolo nel progetto architetturalmente più appropriato.
- Evitare duplicazioni di query SQL.
- Non modificare inutilmente lo schema SQLite.
- Mantenere la retrocompatibilità con i comandi esistenti.

Prima di introdurre nuove classi, verificare che non esistano già componenti equivalenti.

---

# 5. Test automatici

Aggiungere test unitari per entrambe le funzionalità.

## Test elenco esecuzioni

Verificare:

- Database con più esecuzioni.
- Ordinamento corretto.
- Database vuoto.
- Database inesistente.
- Limitazione del numero di risultati.

## Test confronto

Verificare:

- Confronto corretto di due esecuzioni.
- Calcolo delle differenze assolute.
- Calcolo delle differenze percentuali.
- Interpretazione corretta dei miglioramenti e peggioramenti.
- Metriche mancanti.
- Valori nulli.
- Valori pari a zero, evitando divisioni per zero.
- ID di esecuzione inesistenti.
- Confronto di esecuzioni con durate differenti.

Utilizzare test deterministici e database SQLite temporanei, seguendo le convenzioni già adottate in `LoadTest.Engine.Tests` e negli altri progetti di test.

---

# 6. Documentazione

Aggiornare la documentazione CLI e gli eventuali README.

Documentare:

- Comando `runs`.
- Comando `compare`.
- Parametri disponibili.
- Esempi pratici.
- Significato delle metriche.
- Interpretazione delle differenze.

---

# 7. Modalità di lavoro richiesta

Procedi in questo ordine:

1. Analizza l'implementazione attuale di `LoadTest.Cli`.
2. Analizza `SqliteResultsRepository` e lo schema SQLite.
3. Individua le metriche effettivamente persistite.
4. Definisci la soluzione tecnica più semplice e coerente con l'architettura esistente.
5. Implementa il comando `runs`.
6. Implementa il comando `compare`.
7. Implementa i test automatici.
8. Aggiorna la documentazione.
9. Esegui tutti i test della soluzione e correggi eventuali regressioni.

Non limitarti a produrre una proposta teorica: implementa effettivamente le funzionalità.

Al termine, fornisci un riepilogo con:

- File creati.
- File modificati.
- Funzionalità implementate.
- Test eseguiti e relativo risultato.
- Eventuali limitazioni.
- Esempi di comandi pronti da copiare e utilizzare.

**Importante:** non modificare componenti non pertinenti e non introdurre refactoring generali della soluzione. L'obiettivo è aggiungere queste due funzionalità mantenendo il progetto semplice, coerente e facilmente manutenibile.