#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_EXE="${APP_EXE:-$ROOT/artifacts/linux-wine/SuperCalcBenchmark.App-win-x64/SuperCalcBenchmark.App.exe}"
WINEPREFIX="${WINEPREFIX:-$HOME/.pi/wine/supercalc}"
XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-$HOME/.pi/runtime}"
XDG_CACHE_HOME="${XDG_CACHE_HOME:-$HOME/.pi/cache}"

if ! command -v wine >/dev/null 2>&1; then
  echo "[Fehler] wine wurde nicht gefunden. Bitte wine installieren." >&2
  exit 1
fi

if [[ ! -f "$APP_EXE" ]]; then
  echo "[Fehler] Keine publizierte Wine-App gefunden:" >&2
  echo "  $APP_EXE" >&2
  echo "Bitte zuerst ausfuehren:" >&2
  echo "  ./setup_linux.sh" >&2
  exit 1
fi

mkdir -p "$WINEPREFIX" "$XDG_RUNTIME_DIR" "$XDG_CACHE_HOME"
chmod 700 "$WINEPREFIX" "$XDG_RUNTIME_DIR" 2>/dev/null || true

export WINEPREFIX XDG_RUNTIME_DIR XDG_CACHE_HOME
# Linux .NET darf Wine/.NET nicht auf eine ELF-Installation lenken.
unset DOTNET_ROOT DOTNET_ROOT_X64 DOTNET_MULTILEVEL_LOOKUP

if command -v winepath >/dev/null 2>&1; then
  export SUPERCALC_REPOSITORY_ROOT="$(winepath -w "$ROOT")"
else
  export SUPERCALC_REPOSITORY_ROOT="$ROOT"
fi

cd "$ROOT"
echo "Starte SuperCalc Benchmark via Wine..."
echo "Repo: $ROOT"
echo "Archive: $ROOT/archive"
wine "$APP_EXE"
