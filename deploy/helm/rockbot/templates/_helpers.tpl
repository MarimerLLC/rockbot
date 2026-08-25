{{/*
Expand the chart name.
*/}}
{{- define "rockbot.name" -}}
{{- .Chart.Name | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Full release name (chart name, since we don't support name overrides for simplicity).
*/}}
{{- define "rockbot.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- .Chart.Name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}

{{/*
Common labels applied to every resource.
*/}}
{{- define "rockbot.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}

{{/*
Selector labels (stable, used in matchLabels — never add mutable fields here).
*/}}
{{- define "rockbot.selectorLabels" -}}
app.kubernetes.io/name: {{ include "rockbot.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Name of the secret to use — either Helm-managed or pre-existing.
*/}}
{{- define "rockbot.secretName" -}}
{{- if .Values.secrets.create }}
{{- include "rockbot.fullname" . }}-secrets
{{- else }}
{{- required "secrets.existingSecretName is required when secrets.create=false" .Values.secrets.existingSecretName }}
{{- end }}
{{- end }}

{{/*
Name of the shared ConfigMap.
*/}}
{{- define "rockbot.configmapName" -}}
{{- include "rockbot.fullname" . }}-config
{{- end }}

{{/*
Name of the agent ServiceAccount.
*/}}
{{- define "rockbot.agentServiceAccountName" -}}
rockbot-agent
{{- end }}

{{/*
Name of the agent PVC.
*/}}
{{- define "rockbot.agentPvcName" -}}
{{- include "rockbot.fullname" . }}-agent-data
{{- end }}

{{/*
Name of the shared volume PVC.
*/}}
{{- define "rockbot.sharedPvcName" -}}
{{- include "rockbot.fullname" . }}-shared
{{- end }}

{{/*
find(1) exclusion clauses for shared.protectedPaths, one pair of lines per entry.

Each entry is stripped of surrounding slashes first: a trailing one would render
'.../notes//*', which fnmatch cannot match against '.../notes/x.md', silently
disabling the protection the operator asked for. Two clauses per entry so a
protected leaf file is covered as well as a directory's contents.

Emitted without indentation — callers apply `nindent` and must guard on the result
being empty, since a blank continuation line would break the shell command.
*/}}
{{- define "rockbot.sharedProtectedFindClauses" -}}
{{- range .Values.shared.protectedPaths }}
{{- $prefix := . | trimPrefix "/" | trimSuffix "/" }}
{{- if $prefix }}
! -path '/rockbot/shared/{{ $prefix }}' \
! -path '/rockbot/shared/{{ $prefix }}/*' \
{{- end }}
{{- end }}
{{- end }}

{{/*
Comma-joined Tailscale tag list for the Blazor proxy device, or "" when unset.

The operator reads tailscale.com/tags on both the layer-3 Service and the layer-7
Ingress, and expects a comma-separated list. Tags are what the tailnet ACL matches
on: with none set the operator falls back to its own PROXY_TAGS default, which every
proxy it manages in the cluster shares — so no ACL rule can single out one endpoint.
Set this when a deployment needs its own access rule.

Validated here rather than at apply time because the operator rejects an unprefixed
tag by leaving the proxy Pod unregistered, with nothing useful surfaced on the
Kubernetes side.
*/}}
{{- define "rockbot.blazor.tailscaleTags" -}}
{{- $tags := .Values.blazor.tailscale.tags | default list }}
{{- range $t := $tags }}
{{- if not (hasPrefix "tag:" $t) }}
{{- fail (printf "blazor.tailscale.tags: %q is not a valid tag — every entry must start with 'tag:' (e.g. tag:trees)" $t) }}
{{- end }}
{{- end }}
{{- join "," $tags }}
{{- end }}
