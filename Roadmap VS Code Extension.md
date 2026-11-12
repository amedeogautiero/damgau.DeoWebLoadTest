Roadmap VS Code Extension

Fase 1 – Infrastruttura .NET
Prima di sviluppare l'interfaccia VS Code, dobbiamo predisporre il collegamento con il motore.
| # | Componente | Descrizione |
|---|---         |          ---|
| 1 | `LoadTest.VSCode.Host`   | Nuovo processo .NET che ospita il motore e comunica con VS Code. |
| 2 | Protocollo di comunicazione | Definizione dei messaggi JSON-RPC tra estensione e Host. |
| 3 | Execution API | API per avviare, interrompere e monitorare un'esecuzione. |
| 4 | Execution Events | Eventi strutturati: RequestStarted, RequestCompleted, RequestFailed, VariablesUpdated, TestCompleted. |

Fase 2 – Struttura dell'estensione
| # | Componente | Descrizione |
|---|---|---|
| 5 | Extension Bootstrap | Struttura iniziale dell'estensione TypeScript. |
| 6 | Host Manager | Avvio, gestione e terminazione del processo .NET. |
| 7 | Communication Client | Client che comunica con il protocollo JSON-RPC. |
| 8 | Command Registry | Registrazione dei comandi disponibili in VS Code. |

Fase 3 – Gestione progetto
| # | Componente | Descrizione |
|---|---|---|
| 9 | New Project | Creazione della struttura di cartelle convenzionale. |
| 10 | Project Explorer | Navigazione dei WebTest, LoadTest e Package del progetto. |

Fase 4 – Editor WebTest
| # | Componente | Descrizione |
|---|---|---|
| 11 | YAML Schema | Definizione dello schema per `.webtest.yaml`. |
| 12 | Syntax Highlighting | Evidenziazione della sintassi. |
| 13 | IntelliSense | Autocompletamento e suggerimenti. |
| 14 | Validation | Segnalazione degli errori di struttura e configurazione. |

Fase 5 – Importazione HAR
| # | Componente | Descrizione |
|---|---|---|
| 15 | HAR Importer | Importazione di un file `.har`. |
| 16 | HAR Converter | Conversione in un singolo `.webtest.yaml`. |
| 17 | Save WebTest | Salvataggio del risultato nella cartella `webtests`. |


Fase 6 – WebTest Debugger
| # | Componente | Descrizione |
|---|---|---|
| 18 | WebTest Runner | Esecuzione di una singola iterazione. |
| 19 | Debug Panel | Interfaccia dedicata al debug. |
| 20 | Request Explorer | Elenco delle richieste e stato di esecuzione. |
| 21 | Request Inspector | Dettaglio Request e Response. |
| 22 | Timing Inspector | Tempi di esecuzione delle richieste. |
| 23 | Context Inspector | Variabili disponibili, estratte e aggiornate. |
| 24 | Execution Control | Avvio, pausa/interruzione e ripresa, dove supportata dal motore. |

Fase 7 – Debugger .NET
| # | Componente | Descrizione |
|---|---|---|
| 25 | .NET Debug Integration | Integrazione con il debugger C# di VS Code. |
| 26 | Code Step Debugging | Breakpoint nelle richieste `type: code`. |
| 27 | Plugin Debugging | Debug dei plugin Roslyn. |
| 28 | Variables / Watch | Ispezione delle variabili durante l'esecuzione. |

Fase 8 – Packaging
| # | Componente | Descrizione |
|---|---|---|
| 29 | Package Builder | Generazione del `.webtestpkg`. |
| 30 | Dependency Resolver | Individuazione dei WebTest e dei plugin referenziati. |
| 31 | Package Validator | Controllo delle dipendenze mancanti. |
| 32 | Package Output | Produzione del pacchetto autonomo. |

Fase 9 – Rifinitura
| # | Componente | Descrizione |
|---|---|---|
| 33 | Error Management | Gestione uniforme degli errori. |
| 34 | Output Panel | Log e messaggi dell'esecuzione. |
| 35 | Configuration | Configurazioni dell'estensione. |
| 36 | Extension Packaging | Generazione e pubblicazione del pacchetto VSIX. |

Ordine effettivo di sviluppo
| Priorità | Attività | Risultato |
|---:|---|---|
| 1 | Host .NET | Motore utilizzabile dall'estensione |
| 2 | Protocollo di comunicazione | Comunicazione bidirezionale |
| 3 | Scheletro estensione VS Code | Estensione installabile |
| 4 | New Project | Creazione struttura cartelle |
| 5 | Editor YAML | Editing assistito |
| 6 | Import HAR | Conversione automatica |
| 7 | WebTest Runner | Esecuzione singola |
| 8 | Debug Panel | Debug visuale delle richieste |
| 9 | Debugger .NET | Debug Code e plugin Roslyn |
| 10 | Package Builder | Generazione `.webtestpkg` |
| 11 | Distribuzione VSIX | Estensione pronta all'utilizzo |




-----------------
Una precisazione: responseTimeGoal è citata nel contratto, ma non è ancora implementata. Non l'ho quindi inserita tra le regole disponibili nello schema. (per intellisense dello yaml di un webtest)

------------------
possibilità di entrare anche nelle richieste positive e vederne i dati di contesto ecc....

------------------
'errore di una richiesta non vuol dire che tutto il test si deve farmare...