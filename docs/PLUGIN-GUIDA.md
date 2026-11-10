# Guida ai plugin (bozza della chat Plugin)

Valida per CONTRATTO v0.1 più le proposte P1–P13 non ancora approvate: dove una regola è una proposta è indicato.

## 1. Pacchetto
Una cartella con `plugin.yaml`, il file di ingresso e gli eventuali `files`:

```yaml
name: HeaderPlugin
version: 1.0.0
sdk: ">=1.0 <2.0"       # confronto su major.minor
kinds: [ request ]      # webTest | request | extraction | validation | dataSource | loadTest | loadProfile | codedTest
entry: HeaderPlugin.csx # oppure .dll
files: [ ]
```
Percorsi relativi, senza `..`. Posizione (P5): `plugins/<cartella>/` accanto al `.webtest.yaml` o `.loadtest.yaml`.

## 2. Che cosa implementare
| kind | interfaccia SDK |
|---|---|
| webTest | `IWebTestPlugin` |
| request | `IRequestPlugin` |
| extraction | `IExtractionRule` |
| validation | `IValidationRule` |
| dataSource | `IDataSource` (non ancora integrato) |
| loadTest | `ILoadTestPlugin` |
| loadProfile | `ILoadProfile` |
| codedTest | `ICodedTest` |

Ogni kind dichiarato deve avere almeno un tipo pubblico non astratto che implementa l'interfaccia.
Costruttore: senza parametri, oppure con un solo `IReadOnlyDictionary<string,string>` che riceve i `params` del YAML (P1).

## 3. Scrivere un `.csx` (P2)
- Solo `using` e classi pubbliche. Le istruzioni top-level sono un errore di compilazione.
- Using impliciti: `System`, `System.Collections.Generic`, `System.Linq`, `System.Threading`, `System.Threading.Tasks`, `LoadTest.Sdk`.
- Più `.csx` in `files` vengono compilati insieme. Gli altri `files` sono copiati.
- Un `.csx` usato da `code: percorso.csx` senza `plugin.yaml` è un pacchetto implicito `codedTest` e deve contenere esattamente un `ICodedTest`.
- La compilazione avviene una sola volta sul controller; gli agenti ricevono l'assembly.

Riferimenti consentiti (P9): `System.Runtime`, `System.Collections*`, `System.Linq`, `System.IO`, `System.Memory`, `System.Net.Http`, `System.Text.Json`, `System.Text.RegularExpressions`, `System.Threading*`, `System.Xml*`, `System.Globalization`, `LoadTest.Sdk`.
Vietati: `Microsoft.Win32`, `System.Runtime.InteropServices` (P/Invoke, `Marshal`), `System.Reflection.Emit`, `System.Runtime.Loader`, `Process`/`ProcessStartInfo`, metodi `extern`, `unsafe`.

## 4. Riferire un plugin dal YAML (P5)
- `plugins.webTest` / `plugins.request`: `name: Pacchetto` oppure `Pacchetto/Tipo`.
- Regole `type: custom`: `rule: Pacchetto/Tipo`, `Tipo` (se unico) o `Pacchetto` (se ha un solo tipo di quel kind). Le regole built-in non vengono mai sostituite.
- `loadPattern.plugin`: stessa sintassi.

## 5. Vincoli di esecuzione (P13)
Le istanze dei plugin sono **condivise fra tutti gli utenti virtuali**: niente stato modificabile nei campi, oppure sincronizzarlo.
I plugin dichiarati da un test valgono per tutti i test della run. Un `ICodedTest` oggi non può inviare richieste (P3).

## 6. Firma (P10 bis)
ECDSA P-256 sull'hash SHA-256 del pacchetto; la firma sta in `plugin.sig` e non entra nell'hash.

```csharp
var (priv, pub) = PluginSigning.GenerateKeyPair();          // una volta; custodire `priv` sul controller
ScriptPackageCompiler.Compile(pkg, cache, signingPrivateKey: priv);   // compila e firma
var trust = new PluginTrust([pub], SignaturePolicy.Required);          // agente: chiavi pubbliche fidate
var catalog = PluginCatalog.FromCache(cache, hashes, trust);           // rifiuta pacchetti non firmati o con firma errata
```
Politiche: `Off`, `IfPresent` (se c'è, deve essere valida), `Required`.

## 7. Errori frequenti
- «nessun tipo pubblico che implementi X»: il kind è dichiarato ma manca la classe.
- «API non consentita»: vedi l'elenco dei vietati.
- «richiede SDK '>=2.0'»: la versione dell'SDK dell'host è fuori dall'intervallo del manifest.
- «firma non valida» / «chiave non fidata»: il pacchetto è stato modificato dopo la firma, oppure la chiave pubblica non è nell'elenco fidato.
