#!/usr/bin/env bash
# Grants the app access to exactly one SharePoint document library or OneDrive via Sites.Selected
# (05-grant-sites-permission.sh must have run first), and records the alias in .env for
# 07-enable-drives.sh to wire into the container and the agent's argGuards. Safe to re-run with the
# same --alias to change its --access level; the underlying site grant is updated, not duplicated.
#
# A personal OneDrive is addressed as its own site collection, resolved from --user via
# https://learn.microsoft.com/graph/api/resources/sharepoint — a request to
# /sites/{tenant}-my.sharepoint.com:/personal/{mangled-upn}:. Verify this resolves correctly in
# your tenant before relying on it; if it doesn't, pass --site-id with the id from
# `az rest --method GET --url "https://graph.microsoft.com/v1.0/users/UPN/drive?\$select=sharePointIds"`
# instead.
#
# Usage: 06-scope-drive.sh --alias NAME (--site-id ID | --user UPN) --access read|write
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

alias=""
site_id=""
user=""
access=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --alias) alias="$2"; shift 2 ;;
    --site-id) site_id="$2"; shift 2 ;;
    --user) user="$2"; shift 2 ;;
    --access) access="$2"; shift 2 ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

[[ "$alias" =~ ^[a-z0-9][a-z0-9-]{0,63}$ ]] || die "--alias must be lowercase letters, digits and hyphens (matches McpServer.Graph's alias rule)"
[[ "$access" == "read" || "$access" == "write" ]] || die "--access must be 'read' or 'write'"
[[ -n "$site_id" || -n "$user" ]] || die "pass exactly one of --site-id or --user"
[[ -z "$site_id" || -z "$user" ]] || die "pass exactly one of --site-id or --user, not both"

require az jq
ensure_az_login
sp_object_id="$(env_get GRAPH_SP_OBJECT_ID)"
app_name="$(env_get GRAPH_APP_NAME)"
require_value GRAPH_SP_OBJECT_ID "$sp_object_id"

if [[ -n "$user" ]]; then
  root_host="$(az rest --method GET --url "https://graph.microsoft.com/v1.0/sites/root?\$select=webUrl" --query webUrl -o tsv)"
  root_host="${root_host#https://}"; root_host="${root_host%%/*}"
  my_host="${root_host%.sharepoint.com}-my.sharepoint.com"
  mangled="${user//./_}"; mangled="${mangled//@/_}"
  site_id="$(az rest --method GET --url "https://graph.microsoft.com/v1.0/sites/$my_host:/personal/$mangled:?\$select=id" --query id -o tsv)"
  [[ -n "$site_id" ]] || die "could not resolve the site behind $user's OneDrive (tried $my_host:/personal/$mangled:); pass --site-id instead"
  log "Resolved $user's OneDrive to site $site_id"
fi

role="read"; [[ "$access" == "write" ]] && role="write"
existing_id="$(az rest --method GET --url "https://graph.microsoft.com/v1.0/sites/$site_id/permissions" \
  --query "value[?grantedToIdentities[0].application.id=='$(env_get GRAPH_CLIENT_ID)'].id" -o tsv)"

if [[ -n "$existing_id" ]]; then
  az rest --method PATCH --url "https://graph.microsoft.com/v1.0/sites/$site_id/permissions/$existing_id" \
    --headers "Content-Type=application/json" --body "$(jq -n --arg r "$role" '{roles: [$r]}')" >/dev/null
  log "Updated the grant on site $site_id to '$role'"
else
  body="$(jq -n --arg r "$role" --arg app "$(env_get GRAPH_CLIENT_ID)" --arg name "${app_name:-$DEFAULT_APP_NAME}" \
    '{roles: [$r], grantedToIdentities: [{application: {id: $app, displayName: $name}}]}')"
  az rest --method POST --url "https://graph.microsoft.com/v1.0/sites/$site_id/permissions" \
    --headers "Content-Type=application/json" --body "$body" >/dev/null
  log "Granted '$role' access to site $site_id"
fi

slot=""
for n in 0 1 2 3 4; do
  existing_alias="$(env_get "GRAPH_DRIVE_${n}_ALIAS")"
  if [[ -z "$existing_alias" || "$existing_alias" == "$alias" ]]; then
    slot="$n"
    break
  fi
done
[[ -n "$slot" ]] || die "all 5 drive slots are in use; remove one in $ENV_FILE or raise the limit in docker-compose.yml"

upsert_env "GRAPH_DRIVE_${slot}_ALIAS" "$alias"
upsert_env "GRAPH_DRIVE_${slot}_SITE_ID" "$site_id"
remove_env "GRAPH_DRIVE_${slot}_USER"
upsert_env "GRAPH_DRIVE_${slot}_ACCESS" "$access"
log "Recorded alias '$alias' (site $site_id, $access) as drive slot $slot in $ENV_FILE"
log "Next: deploy/graph/07-enable-drives.sh (and 'docker compose up -d graph-mcp' to pick up the config)"
