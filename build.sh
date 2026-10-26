#!/usr/bin/env bash
# Builds the Valence CLI and the bundled probes into dist/.
#   dist/valence.dll            the CLI  (dotnet dist/valence.dll analyze <repo>)
#   dist/probes/csharp/         the C# probe (found automatically next to the CLI)
# The Godot viewer looks for dist/valence.dll to power "Analyze folder…".
set -euo pipefail
cd "$(dirname "$0")"

dotnet publish src/Valence.Cli/Valence.Cli.csproj -c Release -o dist
dotnet publish probes/csharp/Valence.Probe.CSharp.csproj -c Release -o dist/probes/csharp

echo
echo "Built:"
echo "  dist/valence.dll"
echo "  dist/probes/csharp/"
echo "Try: dotnet dist/valence.dll analyze <path-to-a-csharp-repo>"
