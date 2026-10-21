#!/usr/bin/env bash
# Compiles the Unity scripts outside Unity (both Addressables branches) and runs the EditMode
# tests on plain .NET. Needs the .NET 10 SDK (set DOTNET to its dotnet executable if not on PATH).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dotnet="${DOTNET:-dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
artifacts="${ARTIFACTS:-$here/.artifacts}"

"$dotnet" build "$here/Runtime/SkyIslands.csproj" -nologo -v q --artifacts-path "$artifacts/plain"
"$dotnet" build "$here/Runtime/SkyIslands.csproj" -nologo -v q --artifacts-path "$artifacts/addressables" \
  -p:ExtraDefines=SKYISLANDS_ADDRESSABLES
"$dotnet" test "$here/Tests/SkyIslands.Tests.EditMode.csproj" -nologo -v q --artifacts-path "$artifacts/tests"
