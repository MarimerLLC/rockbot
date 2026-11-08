#!/usr/bin/env bash
# Disconnects one Gmail account, or all of them. Each refresh token is revoked with Google, so it
# stops working even if a copy survives elsewhere. Safe to re-run.
#
# Usage: teardown.sh --account NAME | --all
#   --all also removes the stored OAuth client (GMAIL_CLIENT_*); delete the client itself in
#   Google Cloud console > APIs & Services > Credentials if you no longer need it.
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
GMAIL_ENV="$COMPOSE_DIR/gmail.env"

account=""
all=false
case "${1:-}" in
  --account) account="${2:-}" ;;
  --all) all=true ;;
  -h|--help|"") sed -n '2,8p' "$0"; exit 0 ;;
  *) die "unknown argument: $1" ;;
esac

require curl docker jq
key_prefix() { printf 'GMAIL_%s_' "$(tr 'a-z-' 'A-Z_' <<<"$1")"; }

remove_account() {
  local name="$1" prefix token
  prefix="$(key_prefix "$name")"
  token="$(env_get "${prefix}REFRESH_TOKEN")"
  if [[ -n "$token" ]]; then
    printf 'token=%s' "$token" | curl -sS -o /dev/null -d @- https://oauth2.googleapis.com/revoke || true
    log "Revoked Google access for $name"
  fi
  remove_env "${prefix}REFRESH_TOKEN"
  remove_env "${prefix}EMAIL"
}

names="$(env_get GMAIL_ACCOUNTS)"
IFS=',' read -r -a list <<<"$names"
if $all; then
  for name in "${list[@]}"; do [[ -z "$name" ]] || remove_account "$name"; done
  remaining=""
else
  [[ ",$names," == *",$account,"* ]] || die "no account '$account' in GMAIL_ACCOUNTS ($names)"
  remove_account "$account"
  remaining="$(tr ',' '\n' <<<"$names" | grep -vx "$account" | paste -sd, - || true)"
fi

if [[ -n "$remaining" ]]; then
  upsert_env GMAIL_ACCOUNTS "$remaining"
  "$(dirname "${BASH_SOURCE[0]}")/setup.sh" --render-only
else
  unregister_mcp_server gmail
  "${COMPOSE[@]}" --profile gmail rm -sf gmail-mcp >/dev/null 2>&1 || true
  remove_env GMAIL_ACCOUNTS
  if $all; then
    remove_env GMAIL_CLIENT_ID
    remove_env GMAIL_CLIENT_SECRET
  fi
  rm -f "$GMAIL_ENV"
  disable_profile gmail
  log "Removed every Gmail account"
fi
