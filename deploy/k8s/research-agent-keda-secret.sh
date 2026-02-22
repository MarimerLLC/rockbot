#!/usr/bin/env bash
# Creates the KEDA RabbitMQ authentication secret for the ResearchAgent ScaledJob.
# Reads credentials from existing cluster secrets/configmaps — no passwords are
# embedded in any YAML file.
#
# Usage: bash deploy/k8s/research-agent-keda-secret.sh
# Requires: kubectl configured and pointing at the target cluster.

set -euo pipefail

NAMESPACE="rockbot"

echo "Reading RabbitMQ connection details from cluster..."

RABBIT_USER=$(kubectl get configmap rockbot-config -n "$NAMESPACE" \
  -o jsonpath='{.data.RabbitMq__UserName}')

RABBIT_PASS=$(kubectl get secret rockbot-secrets -n "$NAMESPACE" \
  -o jsonpath='{.data.RabbitMq__Password}' | base64 -d)

RABBIT_HOST=$(kubectl get configmap rockbot-config -n "$NAMESPACE" \
  -o jsonpath='{.data.RabbitMq__HostName}')

RABBIT_PORT=$(kubectl get configmap rockbot-config -n "$NAMESPACE" \
  -o jsonpath='{.data.RabbitMq__Port}')

# URL-encode user and password so special characters (^, $, @, etc.) don't
# break URI parsing in KEDA's RabbitMQ scaler.
RABBIT_USER_ENC=$(python3 -c "import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1],safe=''))" "$RABBIT_USER")
RABBIT_PASS_ENC=$(python3 -c "import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1],safe=''))" "$RABBIT_PASS")

RABBIT_URI="amqp://${RABBIT_USER_ENC}:${RABBIT_PASS_ENC}@${RABBIT_HOST}:${RABBIT_PORT}/"

echo "Creating/updating secret rockbot-keda-rabbitmq in namespace $NAMESPACE..."

kubectl create secret generic rockbot-keda-rabbitmq -n "$NAMESPACE" \
  --from-literal=uri="$RABBIT_URI" \
  --dry-run=client -o yaml | kubectl apply -f -

echo "Secret rockbot-keda-rabbitmq created/updated."
