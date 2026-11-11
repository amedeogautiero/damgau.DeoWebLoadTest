#!/usr/bin/env bash
# Pubblica agente e CLI self-contained, file singolo, per ogni RID, e li impacchetta in artifacts/packages (+ SHA256SUMS).
#   build/publish.sh [versione]          es. build/publish.sh 0.2.0
#   ONLY="linux-x64 win-x64" build/publish.sh      solo alcuni RID
#   OUT=dist build/publish.sh                      cartella di uscita (default: artifacts)
# Serve il .NET SDK 10 e la rete (runtime pack e ASP.NET Core pack si scaricano al primo publish).
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${1:-0.1.0}"
OUT="${OUT:-artifacts}"
ONLY="${ONLY:-}"
# l'agente è leggero e gira ovunque; la CLI completa porta ASP.NET Core e SQLite nativo: niente linux-arm (32 bit)
AGENT_RIDS="win-x64 linux-x64 linux-arm64 linux-arm osx-arm64 osx-x64"
CLI_RIDS="win-x64 linux-x64 linux-arm64 osx-arm64 osx-x64"

wanted() { [ -z "$ONLY" ] || case " $ONLY " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }

sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$@"; else shasum -a 256 "$@"; fi; }

pack() { # pack <progetto> <nome> <rid>
  local project="$1" name="$2" rid="$3"
  local stage="$OUT/stage/$name-$rid" base="$name-$VERSION-$rid"
  echo "==> $name $rid"
  dotnet publish "$project" -c Release -r "$rid" -p:Version="$VERSION" -o "$stage"
  find "$stage" -name '*.pdb' -delete
  case "$rid" in
    win-*) (cd "$stage" && zip -qr "../../packages/$base.zip" .) ;;
    *)     tar -C "$stage" -czf "$OUT/packages/$base.tar.gz" . ;;   # il tar conserva il bit di esecuzione
  esac
}

rm -rf "$OUT"
mkdir -p "$OUT/stage" "$OUT/packages"

for rid in $AGENT_RIDS; do wanted "$rid" && pack src/LoadTest.Agent/LoadTest.Agent.csproj loadtest-agent "$rid"; done
for rid in $CLI_RIDS;   do wanted "$rid" && pack src/LoadTest.Cli/LoadTest.Cli.csproj loadtest "$rid"; done

(cd "$OUT/packages" && sha $(ls | sort)) > "$OUT/SHA256SUMS"
rm -rf "$OUT/stage"
echo
echo "Pacchetti in $OUT/packages:"; ls -lh "$OUT/packages"
echo "Somme di controllo: $OUT/SHA256SUMS"
