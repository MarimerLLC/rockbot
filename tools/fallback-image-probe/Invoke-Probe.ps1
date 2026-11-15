#Requires -Version 7
<#
.SYNOPSIS
    Runs probe.cs with provider keys read from a live RockBot deployment's LLM secret.

.DESCRIPTION
    Finds the OpenRouter key and an OpenAI-compatible (e.g. Azure AI Foundry) key and endpoint
    among the secret's LLM__BalancedModels__<n>__* entries by matching the endpoint, so it does
    not depend on the chain's order. The values go into environment variables for the probe
    process only; nothing is printed or written to disk.

.EXAMPLE
    ./tools/fallback-image-probe/Invoke-Probe.ps1

.EXAMPLE
    ./tools/fallback-image-probe/Invoke-Probe.ps1 -Namespace rockbot -Secret rockbot-secrets -TextOnlyModel gpt-5.6-terra
#>
param(
    [string] $Namespace = 'rockbot',
    [string] $Secret = 'rockbot-secrets',
    # Must be served by the OpenAI-compatible entry the key and endpoint are taken from.
    [string] $TextOnlyModel = 'gpt-5.6-terra'
)
$ErrorActionPreference = 'Stop'

$data = (kubectl -n $Namespace get secret $Secret -o json | ConvertFrom-Json).data
if ($LASTEXITCODE) { throw "Could not read secret $Namespace/$Secret" }

function Decode([string] $key) {
    $value = $data.$key
    if (-not $value) { return $null }
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($value))
}

$openRouterKey = $null; $textOnlyKey = $null; $textOnlyEndpoint = $null
for ($i = 0; $null -ne (Decode "LLM__BalancedModels__${i}__ModelId"); $i++) {
    $endpoint = Decode "LLM__BalancedModels__${i}__Endpoint"
    $provider = Decode "LLM__BalancedModels__${i}__Provider"
    if (-not $openRouterKey -and $endpoint -like 'https://openrouter.ai/*') {
        $openRouterKey = Decode "LLM__BalancedModels__${i}__ApiKey"
    }
    elseif (-not $textOnlyKey -and $endpoint -and $endpoint -notlike 'https://openrouter.ai/*' -and $provider -notin 'Anthropic', 'Copilot') {
        $textOnlyKey = Decode "LLM__BalancedModels__${i}__ApiKey"
        $textOnlyEndpoint = $endpoint
    }
}
if (-not $openRouterKey) { throw 'No OpenRouter entry found in LLM__BalancedModels' }
if (-not $textOnlyKey) { throw 'No OpenAI-compatible (non-OpenRouter) entry found in LLM__BalancedModels' }

$env:PROBE_OPENROUTER_KEY = $openRouterKey
$env:PROBE_TEXTONLY_KEY = $textOnlyKey
$env:PROBE_TEXTONLY_ENDPOINT = $textOnlyEndpoint
$env:PROBE_TEXTONLY_MODEL = $TextOnlyModel
try {
    dotnet run (Join-Path $PSScriptRoot 'probe.cs')
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:PROBE_OPENROUTER_KEY, Env:PROBE_TEXTONLY_KEY, Env:PROBE_TEXTONLY_ENDPOINT, Env:PROBE_TEXTONLY_MODEL -ErrorAction SilentlyContinue
}
