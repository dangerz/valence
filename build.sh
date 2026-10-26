#!/usr/bin/env bash
# Optional: builds the command-line tools into dist/. The Godot viewer doesn't need this;
# "Open source folder…" runs the analysis in-process.
#   dist/valence.dll            the CLI, with the C# probe built in (dotnet dist/valence.dll analyze <repo>)
#   dist/probes/csharp/         the C# probe as a standalone plugin (for other tools / reference)
set -euo pipefail
cd "$(dirname "$0")"

dotnet publish src/Valence.Cli/Valence.Cli.csproj -c Release -o dist
dotnet publish probes/csharp/Valence.Probe.CSharp.csproj -c Release -o dist/probes/csharp

echo
echo "Built:"
echo "  dist/valence.dll"
echo "  dist/probes/csharp/"
echo "Try: dotnet dist/valence.dll analyze <path-to-a-csharp-repo>"
