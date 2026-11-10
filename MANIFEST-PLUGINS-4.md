# Consegna chat "Plugin" — fase 4 — dove va ogni file

Percorsi relativi alla radice del repo. Estrai lo zip sopra la radice (dopo le fasi 1-3).

## NUOVI
| Percorso | Contenuto |
|---|---|
| src/LoadTest.Plugins/PluginSignature.cs | firma ECDSA P-256 dei pacchetti, politica di fiducia |
| tests/LoadTest.Engine.Tests/PluginSigningTests.cs | 7 test (firma 6, esempio 1) |
| examples/plugins/HeaderPlugin/{plugin.yaml, HeaderPlugin.csx} | plugin di esempio |
| docs/PLUGIN-GUIDA.md | guida ai plugin |

## MODIFICATI (sostituire)
- `src/LoadTest.Plugins/PluginCatalog.cs`: `FromCache` accetta un `PluginTrust` opzionale.
- `src/LoadTest.Plugins.Compilation/ScriptCompiler.cs`: `Compile` e `CompileSingleFile` accettano `signingPrivateKey` opzionale.

## NON toccati
CONTRATTO.md, Sdk, Engine, Metrics, Reports e tutti gli altri file delle fasi 1-3.
Nota: `examples/` e `docs/` non sono sotto `src/` (sez. 12 elenca solo i progetti): se preferisci un'altra posizione dimmelo.

## Dopo la copia
`dotnet build && dotnet test` — non compilato da me (nell'ambiente non c'è dotnet).
