#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="$SCRIPT_DIR/nupkgs"
VERSION="${1:-}"

# Clean previous output
rm -rf "$OUTPUT_DIR"

# Build the version argument if provided
VERSION_ARG=""
if [ -n "$VERSION" ]; then
  VERSION_ARG="-p:Version=$VERSION"
  echo "Packing version $VERSION..."
fi

# Pack all packable projects in Release configuration
dotnet pack "$SCRIPT_DIR/RockBot.slnx" \
  --configuration Release \
  --output "$OUTPUT_DIR" \
  $VERSION_ARG

echo ""
echo "Packages written to $OUTPUT_DIR:"
ls -1 "$OUTPUT_DIR"/*.nupkg
