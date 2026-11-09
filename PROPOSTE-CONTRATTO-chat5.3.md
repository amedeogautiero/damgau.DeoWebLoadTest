# Proposte di modifica al CONTRATTO — dalla chat "Plugin" (componente 5), consegna fase 3

Si aggiunge a `PROPOSTE-CONTRATTO-chat5.2.md` (P1–P12). Nulla è applicato a CONTRATTO.md, Sdk o Engine. Codice non compilato (nell'ambiente non c'è dotnet).

## Correzioni alle consegne precedenti (sez. 11 e 12 del CONTRATTO)
- Sez. 11: i test vanno tutti in `tests/LoadTest.Engine.Tests`. Il progetto `tests/LoadTest.Plugins.Tests` della fase 2 era un mio errore: **non va creato**. `ScriptCompilerTests.cs` è ora in Engine.Tests (namespace `LoadTest.Engine.Tests`); `PluginsTests.cs` era già lì.
- Sez. 12: da questa consegna il rilascio è uno zip con la struttura `src/{progetto}`.

## Nuove proposte

**P4 (risolta in questa bozza) — dipendenza Plugins → Engine (sez. 2 e 3).** L'integrazione ha bisogno di `ICodeStepHost`, `EngineServices`, `RuleRegistry`, `WebTestDefinition`, `LoadTestDefinition`. Per non toccare Sdk ed Engine ho aggiunto in `LoadTest.Plugins.csproj` un riferimento a `LoadTest.Engine` (nessun ciclo: Engine non conosce Plugins). Alternativa: spostare `ICodeStepHost` in Sdk e tenere l'integrazione in un progetto a parte. Va deciso, e la sez. 2 ("dipende da 1") va aggiornata.

**P13 — Ambito dei plugin e condivisione delle istanze (sez. 4 e 6; chat 2 e 3).** `WebTestRunner` usa gli elenchi globali `EngineServices.WebTestPlugins/RequestPlugins` e ignora `WebTestDefinition.WebTestPlugins/RequestPlugins`. `PluginWiring.Build` unisce i riferimenti di tutti i test (senza duplicati): un plugin dichiarato da un test vale per tutti. Le istanze sono **condivise fra gli utenti virtuali** e quindi devono essere senza stato (stesso vale per le regole custom). Se si vuole l'ambito per test o un'istanza per utente, serve una modifica all'Engine (chat 2). I test richiamati con `call:` vanno passati a `Build` dal chiamante.

**P5 (implementata) — risoluzione dei nomi.** `Pacchetto` | `Pacchetto/Tipo`. Regole `custom`: `rule` = `Pacchetto/Tipo`, `Tipo` (se unico fra tutti i pacchetti) o `Pacchetto` (se ha un solo tipo di quel kind). Le regole dei plugin non sostituiscono mai quelle built-in: il conflitto è un avviso in `PluginWiring.Warnings`. Profili `custom`: stessa sintassi in `loadPattern.plugin`; se ambiguo, errore. Pacchetti su disco: `plugins/<cartella>/plugin.yaml` accanto al file YAML.

**P2 (implementata) — `code:`.** `CodeStepHost` esegue il file indicato, compilato dal controller (`CompileSingleFile`), e richiede esattamente un `ICodedTest`. Resta il blocco **P3**: `ICodedTest` non può inviare richieste, quindi oggi un passo in codice può solo leggere/scrivere il contesto.

**P6 — data source plugin.** Non integrato: l'Engine non ha ancora un runtime per i data source. Resta la proposta `type: custom` + `plugin`.

## Sez. 10 (copertura)
- «Plugin web test e request»: WIP (integrazione scritta, non compilata).
- «Plugin load test e parametri di contesto»: WIP.
- «Test in codice e generazione del codice»: resta TODO (dipende da P3).
