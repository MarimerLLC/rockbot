#!/usr/bin/env bash
# Rebuilds the Drive tools' path-prefix argument guards on the agent's "graph-mail" MCP server
# entry from every alias 06-scope-drive.sh has recorded, and restarts graph-mcp to pick up the
# Graph__Drives__* config. The guards are what actually decide read-only vs read-write vs no
# access per alias (see design/mcp-arg-guards.md); run this again after any 06-scope-drive.sh
# call that added, removed or changed an alias's --access. Safe to re-run.
#
# Usage: 07-enable-drives.sh
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[[ "${1:-}" != "-h" && "${1:-}" != "--help" ]] || { sed -n '2,9p' "$0"; exit 0; }

require docker jq

read_tools='["list_drive_items","get_drive_item","download_drive_file","search_drive_items"]'
write_tools='["upload_drive_file","create_drive_folder","delete_drive_item"]'
readable=() writable=()
for n in 0 1 2 3 4; do
  a="$(env_get "GRAPH_DRIVE_${n}_ALIAS")"
  [[ -n "$a" ]] || continue
  access="$(env_get "GRAPH_DRIVE_${n}_ACCESS")"
  readable+=("/$a")
  [[ "$access" == "write" ]] && writable+=("/$a")
done

if [[ ${#readable[@]} -eq 0 ]]; then
  log "No drive aliases recorded in $ENV_FILE yet; run 06-scope-drive.sh first. Nothing to enable."
  exit 0
fi

readable_json="$(printf '%s\n' "${readable[@]}" | jq -R . | jq -s .)"
if [[ ${#writable[@]} -gt 0 ]]; then
  writable_json="$(printf '%s\n' "${writable[@]}" | jq -R . | jq -s .)"
  guards="$(jq -n --argjson rt "$read_tools" --argjson wt "$write_tools" --argjson ro "$readable_json" --argjson rw "$writable_json" '
    [ {handler: "path-prefix", tools: $rt, options: {args: ["path"], allowedPrefixes: $ro, requireArgs: true}},
      {handler: "path-prefix", tools: $wt, options: {args: ["path"], allowedPrefixes: $rw, requireArgs: true}} ]')"
else
  guards="$(jq -n --argjson rt "$read_tools" --argjson ro "$readable_json" '
    [ {handler: "path-prefix", tools: $rt, options: {args: ["path"], allowedPrefixes: $ro, requireArgs: true}} ]')"
fi

log "Restarting graph-mcp with ${#readable[@]} drive alias(es) (${#writable[@]} read-write)"
"${COMPOSE[@]}" up -d --build graph-mcp

# register_mcp_server merges top-level keys, so this REPLACES the whole argGuards array with the
# complete, current set every time — it must never be called with a partial list.
register_mcp_server graph-mail "$(jq -n --argjson g "$guards" '{argGuards: $g}')"
log "Read-only aliases: ${readable[*]:-(none)}"
log "Read-write aliases: ${writable[*]:-(none)}"
