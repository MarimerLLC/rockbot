// ============================================================
// RockBot — Azure Container Apps Deployment
// ============================================================
// Mirrors the docker-compose stack:
//   rabbitmq, agent (with init), introspection-mcp, blazor, scripts-manager
//
// Notable ACA differences from docker-compose:
//   - No startup ordering between container apps; services must tolerate
//     peers being temporarily unavailable (retry logic in messaging layer).
//   - RabbitMQ management UI (port 15672) is not exposed — ACA TCP ingress
//     supports a single port. Use `az containerapp exec` for debugging.
//   - Scripts manager runs but cannot execute scripts — ACA has no Docker
//     socket. A future ACI-based provider would restore this capability.
//   - Azure Files replaces Docker named volumes for persistent storage.
//     POSIX permissions are not enforced, so init containers run as the
//     default image user (no root required).
//
// Usage:
//   az deployment group create -g <rg> --template-file main.bicep \
//     --parameters llmApiKey='...' braveApiKey='...' rabbitmqPassword='...'
// ============================================================

// ── Parameters ──────────────────────────────────────────────

@description('Azure region for all resources')
param location string = resourceGroup().location

@minLength(3)
@description('Name prefix for all resources (lowercase, no spaces)')
param namePrefix string = 'rockbot'

// -- LLM --
@description('OpenAI-compatible LLM endpoint')
param llmEndpoint string = 'https://openrouter.ai/api/v1'

@secure()
@description('LLM API key (required)')
param llmApiKey string

@description('LLM model ID')
param llmModelId string = 'anthropic/claude-haiku-4.5'

// -- Web search --
@secure()
@description('Brave Search API key (required)')
param braveApiKey string

// -- RabbitMQ --
@description('RabbitMQ username')
param rabbitmqUser string = 'rockbot'

@secure()
@description('RabbitMQ password (required)')
param rabbitmqPassword string

// -- Agent --
@description('Agent IANA timezone (e.g. America/Chicago, Europe/London)')
param agentTimezone string = 'America/Chicago'

// -- Container images --
param agentImage string = 'rockylhotka/rockbot-agent:latest'
param blazorImage string = 'rockylhotka/rockbot-blazor:latest'
param introspectionMcpImage string = 'rockylhotka/rockbot-introspection-mcp:latest'
param scriptsManagerImage string = 'rockylhotka/rockbot-scripts-manager:latest'
param rabbitmqImage string = 'rabbitmq:4-management'

// ── Variables ───────────────────────────────────────────────

// Storage account names: 3-24 chars, lowercase alphanumeric only
var storageAccountName = take(toLower(replace('${namePrefix}aca${uniqueString(resourceGroup().id)}', '-', '')), 24)

// Internal hostnames — used by other container apps for service discovery
var rabbitmqHost = '${namePrefix}-rabbitmq'
var introspectionMcpHost = '${namePrefix}-introspection-mcp'

// ── Log Analytics ───────────────────────────────────────────

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ── Storage (Azure Files) ───────────────────────────────────

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource agentDataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileService
  name: 'agent-data'
  properties: {
    shareQuota: 10
  }
}

resource rabbitmqDataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileService
  name: 'rabbitmq-data'
  properties: {
    shareQuota: 5
  }
}

// ── Container Apps Environment ──────────────────────────────

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// Mount Azure Files shares into the environment so container apps can reference them
resource agentDataStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: environment
  name: 'agentdata'
  properties: {
    azureFile: {
      accountName: storageAccount.name
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: agentDataShare.name
      accessMode: 'ReadWrite'
    }
  }
}

resource rabbitmqDataStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: environment
  name: 'rabbitmqdata'
  properties: {
    azureFile: {
      accountName: storageAccount.name
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: rabbitmqDataShare.name
      accessMode: 'ReadWrite'
    }
  }
}

// ── RabbitMQ ────────────────────────────────────────────────
// Internal TCP ingress on 5672 (AMQP). Management UI (15672) is not exposed —
// ACA TCP ingress supports a single port. Use `az containerapp exec` to debug.

resource rabbitmq 'Microsoft.App/containerApps@2024-03-01' = {
  name: rabbitmqHost
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      secrets: [
        { name: 'rabbitmq-password', value: rabbitmqPassword }
      ]
      ingress: {
        external: false
        targetPort: 5672
        exposedPort: 5672
        transport: 'tcp'
      }
    }
    template: {
      containers: [
        {
          name: 'rabbitmq'
          image: rabbitmqImage
          env: [
            { name: 'RABBITMQ_DEFAULT_USER', value: rabbitmqUser }
            { name: 'RABBITMQ_DEFAULT_PASS', secretRef: 'rabbitmq-password' }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          volumeMounts: [
            {
              volumeName: 'rabbitmq-data'
              mountPath: '/var/lib/rabbitmq'
            }
          ]
          probes: [
            {
              type: 'liveness'
              tcpSocket: {
                port: 5672
              }
              initialDelaySeconds: 30
              periodSeconds: 10
            }
            {
              type: 'readiness'
              tcpSocket: {
                port: 5672
              }
              initialDelaySeconds: 10
              periodSeconds: 5
            }
          ]
        }
      ]
      volumes: [
        {
          name: 'rabbitmq-data'
          storageName: rabbitmqDataStorage.name
          storageType: 'AzureFile'
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

// ── Introspection MCP ───────────────────────────────────────
// Internal HTTP service that exposes agent introspection tools.
// Shares the agent-data Azure Files volume.

resource introspectionMcp 'Microsoft.App/containerApps@2024-03-01' = {
  name: introspectionMcpHost
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
      }
    }
    template: {
      containers: [
        {
          name: 'introspection-mcp'
          image: introspectionMcpImage
          env: [
            { name: 'AgentName__Path', value: '/data/agent/agent-name.md' }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          volumeMounts: [
            {
              volumeName: 'agent-data'
              mountPath: '/data/agent'
            }
          ]
        }
      ]
      volumes: [
        {
          name: 'agent-data'
          storageName: agentDataStorage.name
          storageType: 'AzureFile'
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

// ── Agent ───────────────────────────────────────────────────
// The primary RockBot agent. Single-replica, stateful.
// Init container seeds the Azure Files volume with default profile documents
// on first run (no-clobber — existing customisations are preserved).

resource agent 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-agent'
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      secrets: [
        { name: 'llm-api-key', value: llmApiKey }
        { name: 'brave-api-key', value: braveApiKey }
        { name: 'rabbitmq-password', value: rabbitmqPassword }
      ]
      // Agent is a pure consumer — no ingress needed
    }
    template: {
      initContainers: [
        {
          name: 'init-agent-data'
          image: agentImage
          env: [
            { name: 'INTROSPECTION_MCP_HOST', value: introspectionMcpHost }
          ]
          command: [ '/bin/sh', '-c' ]
          args: [
            '''
            set -e
            echo "Seeding agent data volume..."
            for f in soul.md directives.md subagent-directives.md style.md memory-rules.md \
                     dream.md skill-dream.md common-directives.md session-evaluator.md \
                     session-start.md heartbeat-patrol.md skill-optimize.md \
                     dlq-dream.md routing-dream.md; do
              src="/app/agent/$f"
              dst="/data/agent/$f"
              if [ -f "$src" ] && [ ! -s "$dst" ]; then
                echo "  Copying $f"
                cp "$src" "$dst"
              fi
            done
            if [ -f /app/agent/well-known-agents.json ] && [ ! -s /data/agent/well-known-agents.json ]; then
              cp /app/agent/well-known-agents.json /data/agent/well-known-agents.json
            fi
            # Seed mcp.json with ACA-internal MCP server URLs
            if [ ! -f /data/agent/mcp.json ]; then
              printf '{"mcpServers":{"introspection":{"type":"sse","url":"http://%s/"}}}' "$INTROSPECTION_MCP_HOST" > /data/agent/mcp.json
            fi
            # Per-model behavior files
            for model_dir in /app/model-behaviors/*/; do
              [ -d "$model_dir" ] || continue
              model_name=$(basename "$model_dir")
              mkdir -p "/data/agent/model-behaviors/$model_name"
              for src in "$model_dir"*; do
                [ -f "$src" ] || continue
                dst="/data/agent/model-behaviors/$model_name/$(basename "$src")"
                if [ ! -s "$dst" ]; then
                  cp "$src" "$dst"
                fi
              done
            done
            mkdir -p /data/agent/memory /data/agent/skills /data/agent/conversations /data/agent/feedback
            echo "Agent data volume ready."
            '''
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          volumeMounts: [
            {
              volumeName: 'agent-data'
              mountPath: '/data/agent'
            }
          ]
        }
      ]
      containers: [
        {
          name: 'agent'
          image: agentImage
          env: [
            // RabbitMQ
            { name: 'RabbitMq__HostName', value: rabbitmqHost }
            { name: 'RabbitMq__Port', value: '5672' }
            { name: 'RabbitMq__UserName', value: rabbitmqUser }
            { name: 'RabbitMq__Password', secretRef: 'rabbitmq-password' }
            { name: 'RabbitMq__VirtualHost', value: '/' }
            // LLM (Balanced tier)
            { name: 'LLM__Balanced__Endpoint', value: llmEndpoint }
            { name: 'LLM__Balanced__ApiKey', secretRef: 'llm-api-key' }
            { name: 'LLM__Balanced__ModelId', value: llmModelId }
            // Web search
            { name: 'WebTools__ApiKey', secretRef: 'brave-api-key' }
            // Agent data paths
            { name: 'AgentProfile__BasePath', value: '/data/agent' }
            { name: 'Memory__BasePath', value: '/data/agent/memory' }
            { name: 'Skill__BasePath', value: '/data/agent/skills' }
            { name: 'McpBridge__ConfigPath', value: '/data/agent/mcp.json' }
            { name: 'ModelBehaviors__BasePath', value: '/data/agent/model-behaviors' }
            // Timezone
            { name: 'Agent__Timezone', value: agentTimezone }
          ]
          resources: {
            cpu: json('1')
            memory: '2Gi'
          }
          volumeMounts: [
            {
              volumeName: 'agent-data'
              mountPath: '/data/agent'
            }
          ]
        }
      ]
      volumes: [
        {
          name: 'agent-data'
          storageName: agentDataStorage.name
          storageType: 'AzureFile'
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [ rabbitmq, introspectionMcp ]
}

// ── Scripts Manager ─────────────────────────────────────────
// In docker-compose this pod uses the Docker socket to spawn ephemeral
// Python containers. ACA does not expose a Docker socket, so the container
// will start and connect to RabbitMQ but script execution requests will fail.
// To restore this capability, implement an ACI-based script provider or
// use Azure Container Apps Jobs as the execution backend.

resource scriptsManager 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-scripts-manager'
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      secrets: [
        { name: 'rabbitmq-password', value: rabbitmqPassword }
      ]
      // No ingress — scripts-manager is a pure consumer
    }
    template: {
      containers: [
        {
          name: 'scripts-manager'
          image: scriptsManagerImage
          env: [
            { name: 'RabbitMq__HostName', value: rabbitmqHost }
            { name: 'RabbitMq__Port', value: '5672' }
            { name: 'RabbitMq__UserName', value: rabbitmqUser }
            { name: 'RabbitMq__Password', secretRef: 'rabbitmq-password' }
            { name: 'RabbitMq__VirtualHost', value: '/' }
            // Docker provider config — will not function in ACA (no Docker socket)
            // Replace with an ACI-based provider when available
            { name: 'Scripts__Provider', value: 'Docker' }
            { name: 'Scripts__Docker__Image', value: 'python:3.12-slim' }
            { name: 'Scripts__Docker__CpuLimit', value: '500m' }
            { name: 'Scripts__Docker__MemoryLimit', value: '256Mi' }
            { name: 'Scripts__Docker__NetworkMode', value: 'bridge' }
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [ rabbitmq ]
}

// ── Blazor UI ───────────────────────────────────────────────
// External-facing web UI. The only service with public ingress.

resource blazor 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-blazor'
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      secrets: [
        { name: 'rabbitmq-password', value: rabbitmqPassword }
      ]
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
    }
    template: {
      containers: [
        {
          name: 'blazor'
          image: blazorImage
          env: [
            { name: 'RabbitMq__HostName', value: rabbitmqHost }
            { name: 'RabbitMq__Port', value: '5672' }
            { name: 'RabbitMq__UserName', value: rabbitmqUser }
            { name: 'RabbitMq__Password', secretRef: 'rabbitmq-password' }
            { name: 'RabbitMq__VirtualHost', value: '/' }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          probes: [
            {
              type: 'liveness'
              httpGet: {
                path: '/'
                port: 8080
              }
              initialDelaySeconds: 30
              periodSeconds: 15
              failureThreshold: 3
            }
            {
              type: 'readiness'
              httpGet: {
                path: '/'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 5
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [ rabbitmq ]
}

// ── Outputs ─────────────────────────────────────────────────

@description('Blazor UI URL')
output blazorUrl string = 'https://${blazor.properties.configuration.ingress.fqdn}'

@description('Container Apps Environment name')
output environmentName string = environment.name

@description('Storage account name (for Azure Files management)')
output storageAccountName string = storageAccount.name
