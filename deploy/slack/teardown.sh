#!/usr/bin/env bash
# Disconnects one Slack workspace, or all of them. The token is revoked with auth.revoke, so it
# stops working even if a copy survives elsewhere; the Slack app itself stays until you delete it
# at https://api.slack.com/apps. Safe to re-run.
#
# Usage: teardown.sh --workspace NAME | --all
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
SLACK_ENV="$COMPOSE_DIR/slack.env"

workspace=""
all=false
case "${1:-}" in
  --workspace) workspace="${2:-}" ;;
  --all) all=true ;;
  -h|--help|"") sed -n '2,6p' "$0"; exit 0 ;;
  *) die "unknown argument: $1" ;;
esac

require curl jq docker
key_prefix() { printf 'SLACK_%s_' "$(tr 'a-z-' 'A-Z_' <<<"$1")"; }

remove_workspace() {
  local name="$1" prefix token
  prefix="$(key_prefix "$name")"
  token="$(env_get "${prefix}TOKEN")"
  if [[ -n "$token" ]]; then
    printf 'Authorization: Bearer %s\n' "$token" | curl -sS -o /dev/null -H @- -X POST https://slack.com/api/auth.revoke || true
    log "Revoked the $name token"
  fi
  remove_env "${prefix}TOKEN"
  remove_env "${prefix}TEAM_ID"
}

names="$(env_get SLACK_WORKSPACES)"
IFS=',' read -r -a list <<<"$names"
if $all; then
  for name in "${list[@]}"; do [[ -z "$name" ]] || remove_workspace "$name"; done
  remaining=""
else
  [[ ",$names," == *",$workspace,"* ]] || die "no workspace '$workspace' in SLACK_WORKSPACES ($names)"
  remove_workspace "$workspace"
  remaining="$(tr ',' '\n' <<<"$names" | grep -vx "$workspace" | paste -sd, - || true)"
fi

if [[ -n "$remaining" ]]; then
  upsert_env SLACK_WORKSPACES "$remaining"
  "$(dirname "${BASH_SOURCE[0]}")/setup.sh" --render-only
else
  unregister_mcp_server slack
  "${COMPOSE[@]}" --profile slack rm -sf slack-mcp >/dev/null 2>&1 || true
  remove_env SLACK_WORKSPACES
  rm -f "$SLACK_ENV"
  disable_profile slack
  log "Removed every Slack workspace"
fi
