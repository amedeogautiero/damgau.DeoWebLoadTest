# Consegna chat "Report e analisi" — dove va ogni file

Percorsi relativi alla radice del repo (la cartella con `LoadTest.sln`). Lo zip ha già questa struttura: estrailo sopra la radice.

## NUOVI

| Percorso | Contenuto |
|---|---|
| src/LoadTest.Reports/LoadTest.Reports.csproj | progetto (Microsoft.Data.Sqlite 10.0.0, riferimento a Metrics) |
| src/LoadTest.Reports/ResultsModels.cs | record e `IResultsRepository` |
| src/LoadTest.Reports/Storage/SqliteSchema.cs | schema SQLite v1 |
| src/LoadTest.Reports/Storage/HistogramCodec.cs | serializzazione degli istogrammi (BLOB) |
| src/LoadTest.Reports/Storage/SqliteResultsRepository.cs | repository SQLite |
| src/LoadTest.Reports/Analysis/RunAnalyzer.cs | fusione degli istogrammi e riassunto per serie |
| src/LoadTest.Reports/Export/CsvExporter.cs | export CSV del riassunto |
| tests/LoadTest.Reports.Tests/LoadTest.Reports.Tests.csproj | progetto di test |
| tests/LoadTest.Reports.Tests/ReportsTests.cs | 7 test |

## MODIFICATI

- `LoadTest.sln`: unica differenza, i progetti `LoadTest.Reports` ({55555555-...}) e `LoadTest.Reports.Tests` ({66666666-...}) con le righe Debug/Release. In alternativa:
  `dotnet sln add src/LoadTest.Reports/LoadTest.Reports.csproj tests/LoadTest.Reports.Tests/LoadTest.Reports.Tests.csproj`

## NON toccati
CONTRATTO.md, Sdk, Engine, Metrics: invariati.

## Dopo la copia
`dotnet restore && dotnet build && dotnet test`

Non compilato da me (nell'ambiente non c'è dotnet). Verificato solo lo schema SQL (eseguito con SQLite: creazione idempotente, cascade, UNIQUE, query di lettura).
Revisione 2: tolti `GetMean()`/`GetMaxValue()` (non esistono in HdrHistogram 2.5.0; media e massimo ora ricavati da `RecordedValues()`); aggiunto il riferimento diretto a `SQLitePCLRaw.lib.e_sqlite3` 3.50.3 per l'avviso GHSA-2m69-gcr7-jv3q.
Ancora da verificare al build: `RecordedValues()`, `RecordValueWithCount()`, `GetValueAtPercentile()` di HdrHistogram 2.5.0 (usati in HistogramCodec e RunAnalyzer).
