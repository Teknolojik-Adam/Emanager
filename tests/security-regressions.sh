#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet run --project tests/Emanager.Security.Audit/Emanager.Security.Audit.csproj -c Release
./tests/disk-safety-audit.sh
