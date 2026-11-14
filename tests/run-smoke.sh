#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if ! command -v dotnet >/dev/null 2>&1; then
  echo 'ERROR: .NET 8 SDK is required; no tests were run.' >&2
  exit 127
fi
dotnet --version
dotnet run --project tests/Emanager.FileOps.Smoke/Emanager.FileOps.Smoke.csproj -c Release
