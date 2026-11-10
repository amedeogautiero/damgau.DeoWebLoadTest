# Proposte di modifica al CONTRATTO — dalla chat "Plugin" (componente 5), consegna fase 4

Si aggiunge a 5.2 e 5.3. Nulla è applicato a CONTRATTO.md, Sdk o Engine. Codice non compilato (nell'ambiente non c'è dotnet).

## Modifiche richieste

**P10 bis — Firma dei pacchetti (sez. 7; sostituisce P10).** P10 proponeva Ed25519, ma la libreria standard di .NET non lo offre: avrebbe richiesto un pacchetto esterno (managed) o libsodium (nativa, problematica su `linux-arm`). Implementato **ECDSA P-256** (BCL, nessuna dipendenza):
- si firma l'hash SHA-256 del pacchetto (P7), in esadecimale minuscolo; formato IEEE P1363, base64;
- la firma sta in `plugin.sig` (JSON `{ keyId, signature }`) nella cartella del pacchetto e **non entra nell'hash**;
- `keyId` = primi 16 caratteri dello SHA-256 della chiave pubblica (SPKI);
- politica: `Off | IfPresent | Required`; chiavi pubbliche fidate nella configurazione di agente e controller;
- per i `.csx` si firma il pacchetto **compilato** (quello di cui l'agente verifica l'hash).
Per la chat 6: la firma deve viaggiare insieme al manifest di `PrepareRun` (sez. 7) e alla fine dello stream `PackageChunk`, oppure come campo `signature` accanto a `sha256`. La politica va in configurazione dell'agente.

**P1 (confermata dall'esempio).** Il costruttore con `IReadOnlyDictionary<string,string>` funziona per i `params`; resta da decidere se standardizzare questa convenzione o introdurre `IConfigurablePlugin` nell'SDK.

## Non fatto: generazione del codice dal test dichiarativo
Sez. 10 («Test in codice e generazione del codice dal test dichiarativo»). Dipende da **P3**: `ICodedTest.RunAsync(TestContext)` non dà accesso a un esecutore HTTP né alle misure, quindi il codice generato non potrebbe inviare richieste. Servono una decisione su `IWebTestHost` (SDK) con la chat 2 e l'Architettura. Quando c'è, il generatore produce un `.csx` con un `ICodedTest` che replica passi, transazioni, `extract` e `validate`.

## Sez. 10 (copertura)
- «Plugin web test e request», «Plugin load test e parametri di contesto»: WIP → **OK** quando la run reale (chat 6) avrà provato caricamento e firma sull'agente; per ora restano WIP.
- «Test in codice e generazione del codice»: TODO (P3).
