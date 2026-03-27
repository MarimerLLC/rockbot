#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="$SCRIPT_DIR/nupkgs"

# Clean previous output
rm -rf "$OUTPUT_DIR"

# Pack all packable projects in Release configuration
dotnet pack "$SCRIPT_DIR/RockBot.slnx" \
  --configuration Release \
  --output "$OUTPUT_DIR"

echo ""
echo "Packages written to $OUTPUT_DIR:"
ls -1 "$OUTPUT_DIR"/*.nupkg
