#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_EXE="${APP_EXE:-$ROOT/artifacts/linux-wine/SuperCalcBenchmark.App-win-x64/SuperCalcBenchmark.App.exe}"
WINEPREFIX="${WINEPREFIX:-$HOME/.pi/wine/supercalc}"
XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-$HOME/.pi/runtime}"
XDG_CACHE_HOME="${XDG_CACHE_HOME:-$HOME/.pi/cache}"
XDG_DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
DATA_ROOT="${SUPERCALC_NATIVE_DATA_ROOT:-$XDG_DATA_HOME/SuperCalcBenchmark}"

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

mkdir -p "$WINEPREFIX" "$XDG_RUNTIME_DIR" "$XDG_CACHE_HOME" "$DATA_ROOT"
chmod 700 "$WINEPREFIX" "$XDG_RUNTIME_DIR" 2>/dev/null || true

export WINEPREFIX XDG_RUNTIME_DIR XDG_CACHE_HOME XDG_DATA_HOME
# Linux .NET darf Wine/.NET nicht auf eine ELF-Installation lenken.
unset DOTNET_ROOT DOTNET_ROOT_X64 DOTNET_MULTILEVEL_LOOKUP

if command -v winepath >/dev/null 2>&1; then
  export SUPERCALC_ASSET_ROOT="$(winepath -w "$ROOT")"
  export SUPERCALC_DATA_ROOT="$(winepath -w "$DATA_ROOT")"
else
  export SUPERCALC_ASSET_ROOT="$ROOT"
  export SUPERCALC_DATA_ROOT="$DATA_ROOT"
fi

cd "$ROOT"
echo "Starte SuperCalc Benchmark via Wine..."
echo "Assets: $ROOT"
echo "Gemeinsamer Datenpool: $DATA_ROOT"
wine "$APP_EXE"
