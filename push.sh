#!/usr/bin/env bash
set -euo pipefail

if [ $# -lt 1 ]; then
  echo "Usage: $0 <nuget-api-key>"
  exit 1
fi

API_KEY="$1"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NUPKG_DIR="$SCRIPT_DIR/nupkgs"

if [ ! -d "$NUPKG_DIR" ] || [ -z "$(ls -A "$NUPKG_DIR"/*.nupkg 2>/dev/null)" ]; then
  echo "No packages found in $NUPKG_DIR. Run pack.sh first."
  exit 1
fi

for pkg in "$NUPKG_DIR"/*.nupkg; do
  echo "Pushing $(basename "$pkg")..."
  dotnet nuget push "$pkg" \
    --api-key "$API_KEY" \
    --source https://api.nuget.org/v3/index.json \
    --skip-duplicate
done

echo ""
echo "All packages pushed."
