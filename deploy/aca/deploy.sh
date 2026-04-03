#!/usr/bin/env bash
# ============================================================
# RockBot — Deploy to Azure Container Apps
# ============================================================
# Reads configuration from .env (same directory), creates the
# resource group if needed, and deploys the Bicep template.
#
# Prerequisites:
#   - Azure CLI (`az`) with Bicep support
#   - Logged in: `az login`
#   - Copy .env.example to .env and fill in required values
#
# Usage:
#   cd deploy/aca
#   cp .env.example .env   # edit .env with your values
#   ./deploy.sh
# ============================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ── Load .env ────────────────────────────────────────────────
if [ -f "$SCRIPT_DIR/.env" ]; then
  set -a
  # shellcheck disable=SC1091
  source "$SCRIPT_DIR/.env"
  set +a
else
  echo "ERROR: $SCRIPT_DIR/.env not found. Copy .env.example to .env and fill in required values."
  exit 1
fi

# ── Validate required variables ──────────────────────────────
: "${LLM_API_KEY:?LLM_API_KEY is required in .env}"
: "${BRAVE_API_KEY:?BRAVE_API_KEY is required in .env}"
: "${RABBITMQ_PASSWORD:?RABBITMQ_PASSWORD is required in .env}"

# ── Defaults ─────────────────────────────────────────────────
RESOURCE_GROUP="${RESOURCE_GROUP:-rockbot-rg}"
LOCATION="${LOCATION:-eastus}"
NAME_PREFIX="${NAME_PREFIX:-rockbot}"

echo "=== RockBot ACA Deployment ==="
echo "  Resource Group : $RESOURCE_GROUP"
echo "  Location       : $LOCATION"
echo "  Name Prefix    : $NAME_PREFIX"
echo ""

# ── Create resource group ────────────────────────────────────
echo "Creating resource group '$RESOURCE_GROUP' in '$LOCATION'..."
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --output none

# ── Deploy Bicep ─────────────────────────────────────────────
echo "Deploying Bicep template..."
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$SCRIPT_DIR/main.bicep" \
  --parameters \
    namePrefix="$NAME_PREFIX" \
    llmApiKey="$LLM_API_KEY" \
    llmEndpoint="${LLM_ENDPOINT:-https://openrouter.ai/api/v1}" \
    llmModelId="${LLM_MODEL_ID:-anthropic/claude-haiku-4.5}" \
    braveApiKey="$BRAVE_API_KEY" \
    rabbitmqUser="${RABBITMQ_USER:-rockbot}" \
    rabbitmqPassword="$RABBITMQ_PASSWORD" \
    agentTimezone="${AGENT_TIMEZONE:-America/Chicago}" \
  --output json

# ── Print outputs ────────────────────────────────────────────
echo ""
echo "=== Deployment Complete ==="
BLAZOR_URL=$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name main \
  --query 'properties.outputs.blazorUrl.value' \
  --output tsv 2>/dev/null || echo "(check Azure Portal)")
echo "  Blazor UI: $BLAZOR_URL"
echo ""
echo "Useful commands:"
echo "  az containerapp logs show -n ${NAME_PREFIX}-agent -g $RESOURCE_GROUP --follow"
echo "  az containerapp exec -n ${NAME_PREFIX}-rabbitmq -g $RESOURCE_GROUP --command /bin/bash"
