#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SDK_VERSION="$(grep -Po '"version"\s*:\s*"\K[^"]+' "$ROOT/global.json" | head -1)"
CONFIGURATION="${CONFIGURATION:-Release}"
DOTNET_INSTALL_DIR="${DOTNET_INSTALL_DIR:-$HOME/.pi/dotnet}"
DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$HOME/.pi/dotnet-home}"
NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.pi/nuget/packages}"
PUBLISH_DIR="${PUBLISH_DIR:-$ROOT/artifacts/linux-wine/SuperCalcBenchmark.App-win-x64}"

if [[ -z "$SDK_VERSION" ]]; then
  echo "[Fehler] Konnte SDK-Version nicht aus global.json lesen." >&2
  exit 1
fi

mkdir -p "$DOTNET_INSTALL_DIR" "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"
export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
export DOTNET_CLI_HOME
export NUGET_PACKAGES
export PATH="$DOTNET_ROOT:$PATH"

if [[ ! -x "$DOTNET_ROOT/dotnet" ]] || ! "$DOTNET_ROOT/dotnet" --list-sdks 2>/dev/null | grep -q "^$SDK_VERSION "; then
  echo "[1/4] Installiere .NET SDK $SDK_VERSION nach $DOTNET_ROOT ..."
  tmp="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp"
  bash "$tmp" --version "$SDK_VERSION" --install-dir "$DOTNET_ROOT" --architecture x64 --no-path
  rm -f "$tmp"
else
  echo "[1/4] .NET SDK $SDK_VERSION ist vorhanden: $DOTNET_ROOT/dotnet"
fi

cd "$ROOT"
echo "[2/4] .NET Info"
"$DOTNET_ROOT/dotnet" --info | sed -n '1,35p'

echo "[3/4] Build $CONFIGURATION ..."
"$DOTNET_ROOT/dotnet" build SuperCalcBenchmark.slnx --configuration "$CONFIGURATION"

echo "[4/4] Publish Windows/WPF App fuer Wine (self-contained win-x64) ..."
"$DOTNET_ROOT/dotnet" publish src/SuperCalcBenchmark.App/SuperCalcBenchmark.App.csproj \
  --configuration "$CONFIGURATION" \
  --runtime win-x64 \
  --self-contained true \
  -p:PublishSingleFile=false \
  -o "$PUBLISH_DIR"

cat <<EOF

Fertig.
- Native .NET fuer CLI/Tests: $DOTNET_ROOT/dotnet
- Wine-App: $PUBLISH_DIR/SuperCalcBenchmark.App.exe

Start GUI unter Ubuntu:
  ./start_linux.sh

VS Code mit richtigem .NET SDK starten:
  ./code_linux.sh
EOF
