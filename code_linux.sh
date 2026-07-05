#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SDK_VERSION="$(grep -Po '"version"\s*:\s*"\K[^"]+' "$ROOT/global.json" | head -1)"
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.pi/dotnet}"
DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$HOME/.pi/dotnet-home}"
NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.pi/nuget/packages}"

mkdir -p "$DOTNET_ROOT" "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

if [[ ! -x "$DOTNET_ROOT/dotnet" ]] || ! "$DOTNET_ROOT/dotnet" --list-sdks 2>/dev/null | grep -q "^$SDK_VERSION "; then
  echo "[Info] .NET SDK $SDK_VERSION fehlt in $DOTNET_ROOT; installiere/build mit setup_linux.sh ..."
  "$ROOT/setup_linux.sh"
fi

export DOTNET_ROOT DOTNET_CLI_HOME NUGET_PACKAGES
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

WORKSPACE_DIR="$ROOT/artifacts/linux-vscode"
WORKSPACE="$WORKSPACE_DIR/SuperCalcBenchmark-linux.code-workspace"
mkdir -p "$WORKSPACE_DIR"
python3 - "$ROOT" "$DOTNET_ROOT/dotnet" "$WORKSPACE" <<'PY'
import json
import sys
root, dotnet, workspace = sys.argv[1:]
data = {
    "folders": [{"path": root}],
    "settings": {
        "dotnetAcquisitionExtension.existingDotnetPath": [
            {"extensionId": "ms-dotnettools.csharp", "path": dotnet},
            {"extensionId": "ms-dotnettools.csdevkit", "path": dotnet},
        ]
    },
}
with open(workspace, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2)
    f.write("\n")
PY

cd "$ROOT"
exec code "$WORKSPACE"
