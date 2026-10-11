{{/* Chart name, release-scoped full name, and the labels every object carries. */}}
{{- define "laya.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "laya.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* Selector labels. Takes a dict: root (the chart context) and component (api | renderer). */}}
{{- define "laya.selectorLabels" -}}
app.kubernetes.io/name: {{ include "laya.name" .root }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{- define "laya.labels" -}}
{{ include "laya.selectorLabels" . }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
app.kubernetes.io/version: {{ .root.Values.image.tag | quote }}
{{- end -}}

{{/* Image reference for a component. Takes root and image (the image name). */}}
{{- define "laya.image" -}}
{{- printf "%s/%s:%s" .root.Values.image.registry .image (required "image.tag is required: pin the sha-<commit> tag CI pushes" .root.Values.image.tag) -}}
{{- end -}}

{{/* Name of the Gateway this chart creates, or the existing one it attaches to. */}}
{{- define "laya.gatewayName" -}}
{{- if .Values.gateway.create -}}
{{- default (include "laya.fullname" .) .Values.gateway.name -}}
{{- else -}}
{{- required "gateway.existing.name is required when gateway.create is false" .Values.gateway.existing.name -}}
{{- end -}}
{{- end -}}

{{/* One parentRef for an HTTPRoute. Takes root and listener (http | https). */}}
{{- define "laya.parentRef" -}}
- group: gateway.networking.k8s.io
  kind: Gateway
  name: {{ include "laya.gatewayName" .root }}
  {{- if not .root.Values.gateway.create }}
  {{- with .root.Values.gateway.existing.namespace }}
  namespace: {{ . }}
  {{- end }}
  sectionName: {{ ternary .root.Values.gateway.existing.httpsSectionName .root.Values.gateway.existing.httpSectionName (eq .listener "https") }}
  {{- else }}
  sectionName: {{ .listener }}
  {{- end }}
{{- end -}}

{{/* Hardened pod and container settings shared by both services (mirrors docker-compose.yml). */}}
{{- define "laya.podSecurityContext" -}}
runAsNonRoot: true
runAsUser: 1654
runAsGroup: 1654
seccompProfile:
  type: RuntimeDefault
{{- end -}}

{{- define "laya.containerSecurityContext" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop: [ALL]
{{- end -}}
