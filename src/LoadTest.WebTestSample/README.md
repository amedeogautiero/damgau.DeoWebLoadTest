# LoadTest.WebTestSample

Questo è un unico progetto .NET che rappresenta il contenitore completo del WebTest.

## Contenuto

- `LoadTest.WebTestSample.csproj`: progetto unico, con riferimenti a `LoadTest.Sdk` e strumenti xUnit.
- `Scripts/CalcolaValore.cs`: Code Step C# ordinario che implementa `ICodedTest`.
- `UnitTests/CalcolaValoreTests.cs`: unit test che invoca direttamente il Code Step.
- `Sample.webtest.yaml`: definizione del WebTest.

Nello stesso progetto saranno ospitati anche `.loadtest.yaml`, file CSV, dati e gli altri asset del WebTest.

## Apertura e test

Aprire la cartella `src/LoadTest.WebTestSample` in VS Code.

Dalla root del repository:

```powershell
dotnet test .\src\LoadTest.WebTestSample\LoadTest.WebTestSample.csproj
```

È possibile mettere un breakpoint in `Scripts/CalcolaValore.cs` e avviare il test
`RunAsync_calcola_il_totale_e_aggiorna_il_contesto` dal Test Explorer.

## Stato dell'integrazione

Questo progetto dimostra il modello di sviluppo C# e unit test nello stesso progetto.
Il collegamento tra le classi compilate e l'esecuzione/packaging del WebTest da parte
dell'estensione sarà implementato nelle fasi successive. Il file YAML di esempio
non richiama ancora `CalcolaValore`.
