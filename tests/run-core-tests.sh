#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build tests/Emanager.Core.Tests/Emanager.Core.Tests.csproj -c Release
dotnet run --project tests/Emanager.Core.Runner/Emanager.Core.Runner.csproj -c Release -- "$(pwd)/tests/Emanager.Core.Tests/bin/Release/net8.0/Emanager.Core.Tests.dll"
