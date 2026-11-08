#!/usr/bin/env bash
# Grants the app the Microsoft Graph "Sites.Selected" application permission and admin-consents
# it. This only switches the mechanism on; it grants no access to any site or OneDrive by itself —
# each one still needs its own grant from 06-scope-drive.sh. Needs a Global Administrator or
# Privileged Role Administrator for the consent step (device-code sign-in via the Azure CLI).
# Run once, before the first 06-scope-drive.sh. Safe to re-run.
#
# Usage: 05-grant-sites-permission.sh
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[[ "${1:-}" != "-h" && "${1:-}" != "--help" ]] || { sed -n '2,9p' "$0"; exit 0; }

require az jq
ensure_az_login
app_id="$(env_get GRAPH_CLIENT_ID)"
sp_object_id="$(env_get GRAPH_SP_OBJECT_ID)"
require_value GRAPH_CLIENT_ID "$app_id"
require_value GRAPH_SP_OBJECT_ID "$sp_object_id"

graph_sp_id="$(az ad sp show --id "$GRAPH_API_APP_ID" --query id -o tsv)"
role_id="$(az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$graph_sp_id" \
  --query "appRoles[?value=='Sites.Selected'].id" -o tsv)"
[[ -n "$role_id" ]] || die "Microsoft Graph's service principal has no 'Sites.Selected' app role"

existing="$(az rest --method GET \
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/$sp_object_id/appRoleAssignments" \
  --query "value[?resourceId=='$graph_sp_id' && appRoleId=='$role_id'].id" -o tsv)"
if [[ -n "$existing" ]]; then
  log "Sites.Selected is already granted and consented on $app_id"
  exit 0
fi

body="$(jq -n --arg sp "$sp_object_id" --arg res "$graph_sp_id" --arg role "$role_id" \
  '{principalId: $sp, resourceId: $res, appRoleId: $role}')"
az rest --method POST \
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/$sp_object_id/appRoleAssignments" \
  --headers "Content-Type=application/json" --body "$body" >/dev/null
log "Granted and consented Sites.Selected (application) on $app_id"
log "Next: deploy/graph/06-scope-drive.sh --alias NAME (--site-id ID | --user UPN) --access read|write"
