#!/usr/bin/env bash
# Limits the app to Mail.ReadWrite, Calendars.ReadWrite and Contacts.ReadWrite on the configured
# mailbox only, using Exchange RBAC for Applications. Runs scope-mailbox.ps1 in the PowerShell
# container, so pwsh is not needed on the host. Needs an Exchange admin to complete the
# device-code sign-in. Safe to re-run.
#
# Usage: 02-scope-mailbox.sh
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[[ "${1:-}" != "-h" && "${1:-}" != "--help" ]] || { sed -n '2,7p' "$0"; exit 0; }

require docker
app_id="$(env_get GRAPH_CLIENT_ID)"
sp_object_id="$(env_get GRAPH_SP_OBJECT_ID)"
mailbox="$(env_get GRAPH_MAILBOX)"
app_name="$(env_get GRAPH_APP_NAME)"
require_value GRAPH_CLIENT_ID "$app_id"
require_value GRAPH_SP_OBJECT_ID "$sp_object_id"
require_value GRAPH_MAILBOX "$mailbox"

tty_flags=(-i)
[[ -t 1 ]] && tty_flags+=(-t)

log "Scoping app $app_id to mailbox $mailbox"
docker run --rm "${tty_flags[@]}" \
  -e ACTION=apply \
  -e APP_ID="$app_id" \
  -e SP_OBJECT_ID="$sp_object_id" \
  -e MAILBOX="$mailbox" \
  -e APP_NAME="${app_name:-$DEFAULT_APP_NAME}" \
  -v rockbot-pwsh-modules:/root/.local/share/powershell/Modules \
  -v "$GRAPH_DIR/scope-mailbox.ps1:/work/scope-mailbox.ps1:ro" \
  "${PWSH_IMAGE:-mcr.microsoft.com/powershell:7.5-ubuntu-24.04}" \
  pwsh -NoProfile -File /work/scope-mailbox.ps1
