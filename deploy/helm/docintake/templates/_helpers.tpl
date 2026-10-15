{{/* Chart name, release-scoped full name, and the labels every object carries. */}}
{{- define "docintake.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "docintake.fullname" -}}
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

{{/* Selector labels. Takes a dict: root (the chart context) and component (api | document). */}}
{{- define "docintake.selectorLabels" -}}
app.kubernetes.io/name: {{ include "docintake.name" .root }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{- define "docintake.labels" -}}
{{ include "docintake.selectorLabels" . }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
app.kubernetes.io/version: {{ .root.Values.image.tag | quote }}
{{- end -}}

{{/* Image reference for a component. Takes root and image (the image name). */}}
{{- define "docintake.image" -}}
{{- printf "%s/%s:%s" .root.Values.image.registry .image (required "image.tag is required: pin the sha-<commit> tag CI pushes" .root.Values.image.tag) -}}
{{- end -}}

{{/* Name of the Gateway this chart creates, or the existing one it attaches to. */}}
{{- define "docintake.gatewayName" -}}
{{- if .Values.gateway.create -}}
{{- default (include "docintake.fullname" .) .Values.gateway.name -}}
{{- else -}}
{{- required "gateway.existing.name is required when gateway.create is false" .Values.gateway.existing.name -}}
{{- end -}}
{{- end -}}

{{/* One parentRef for an HTTPRoute. Takes root and listener (http | https). */}}
{{- define "docintake.parentRef" -}}
- group: gateway.networking.k8s.io
  kind: Gateway
  name: {{ include "docintake.gatewayName" .root }}
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
{{- define "docintake.podSecurityContext" -}}
runAsNonRoot: true
runAsUser: 1654
runAsGroup: 1654
seccompProfile:
  type: RuntimeDefault
{{- end -}}

{{- define "docintake.containerSecurityContext" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop: [ALL]
{{- end -}}

{{/* In-cluster OTLP endpoint of the collector, when the chart runs one. */}}
{{- define "docintake.collectorEndpoint" -}}
{{- printf "%s://%s-otel-collector:4318" (ternary "https" "http" (not (empty (include "docintake.otlpTls" .)))) (include "docintake.fullname" .) -}}
{{- end -}}

{{/* Host and port of the collector's OTLP/HTTP receiver, without a scheme (what Dapr's tracing configuration takes). */}}
{{- define "docintake.collectorAddress" -}}
{{- printf "%s-otel-collector.%s.svc.cluster.local:4318" (include "docintake.fullname" .) .Release.Namespace -}}
{{- end -}}

{{/* Env shared by every service: where telemetry goes and which pod it came from. Takes the chart context. */}}
{{- define "docintake.telemetryEnv" -}}
- name: POD_NAME
  valueFrom:
    fieldRef: { fieldPath: metadata.name }
- name: POD_NAMESPACE
  valueFrom:
    fieldRef: { fieldPath: metadata.namespace }
- name: NODE_NAME
  valueFrom:
    fieldRef: { fieldPath: spec.nodeName }
- name: OTEL_RESOURCE_ATTRIBUTES
  value: {{ printf "k8s.pod.name=$(POD_NAME),k8s.namespace.name=$(POD_NAMESPACE),k8s.node.name=$(NODE_NAME),intake.image.tag=%s" .Values.image.tag | quote }}
{{- if .Values.telemetry.collector.enabled }}
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: {{ include "docintake.collectorEndpoint" . | quote }}
- name: OTEL_EXPORTER_OTLP_PROTOCOL
  value: http/protobuf
{{- if include "docintake.otlpTls" . }}
# The collector's certificate is signed by the release's private CA, which only the exporter trusts for this.
- name: OTEL_EXPORTER_OTLP_CERTIFICATE
  value: /ca/ca.crt
{{- end }}
{{- end }}
{{- end -}}

{{/*
Where the API sends a call to one of the services behind it. With Dapr, the API's own sidecar (localhost:3500) carries it to
the callee's sidecar over mutual TLS and the callee only accepts the API's app id. Without, the Service name, in plain HTTP.
Takes root and name (document | extraction | clamav; the scan service answers on the clamav Service and is app id "scan").
*/}}
{{- define "docintake.serviceUrl" -}}
{{- $fullname := include "docintake.fullname" .root -}}
{{- if .root.Values.dapr.enabled -}}
{{- printf "http://localhost:3500/v1.0/invoke/%s-%s/method" $fullname (default .name .appId) -}}
{{- else -}}
{{- printf "http://%s-%s:8080" $fullname .name -}}
{{- end -}}
{{- end -}}

{{/*
Pod annotations that put a Dapr sidecar next to a service. Takes root, name (the app id suffix) and, for a service the API
calls, appPort. The sidecar's identity is its app id; what it accepts is the Configuration of the same name (dapr-config.yaml).
*/}}
{{- define "docintake.daprAnnotations" -}}
{{- if .root.Values.dapr.enabled }}
dapr.io/enabled: "true"
dapr.io/app-id: {{ printf "%s-%s" (include "docintake.fullname" .root) .name | quote }}
dapr.io/config: {{ printf "%s-dapr-%s" (include "docintake.fullname" .root) .name | quote }}
{{- if .appPort }}
dapr.io/app-port: {{ .appPort | quote }}
dapr.io/enable-app-health-check: "true"
dapr.io/app-health-check-path: "/health/live"
{{- end }}
# Documents go up to 25 MB; the sidecar's default request limit is 4 MB.
dapr.io/max-body-size: {{ .root.Values.dapr.maxBodySize | quote }}
# The pods mount no service account token (automountServiceAccountToken: false), and the sidecar's built-in Kubernetes secret
# store would need one to start. Nothing here uses Dapr secrets, so it is off. (Found by running the chart on kind.)
dapr.io/disable-builtin-k8s-secret-store: "true"
dapr.io/enable-api-logging: "false"
dapr.io/log-as-json: "true"
# Traces only: the sidecar's Prometheus endpoint is off (nothing scrapes it).
dapr.io/enable-metrics: "false"
{{- if include "docintake.otlpTls" .root }}
# The sidecar sends its spans to the collector over TLS (dapr-config.yaml); the collector's certificate is signed by the private
# CA, so the sidecar gets the CA mounted and uses it instead of the system roots (it talks to nothing else over public TLS).
dapr.io/volume-mounts: "internal-ca:/ca"
dapr.io/env: "SSL_CERT_FILE=/ca/ca.crt"
{{- end }}
{{- $resources := .root.Values.dapr.sidecar.resources }}
{{- with (get .root.Values.dapr.sidecar.overrides .name) }}{{ $resources = .resources }}{{ end }}
dapr.io/sidecar-cpu-request: {{ $resources.requests.cpu | quote }}
dapr.io/sidecar-memory-request: {{ $resources.requests.memory | quote }}
dapr.io/sidecar-cpu-limit: {{ $resources.limits.cpu | quote }}
dapr.io/sidecar-memory-limit: {{ $resources.limits.memory | quote }}
{{- end }}
{{- end -}}

{{/* The port a service the API calls accepts the call on: Dapr's internal gRPC port (sidecar to sidecar, mutual TLS), else its own. */}}
{{- define "docintake.calleePort" -}}
{{- ternary 50002 8080 .Values.dapr.enabled -}}
{{- end -}}

{{/* Egress rule to the Dapr control plane (Sentry for certificates, the operator for configuration), when Dapr is on. */}}
{{- define "docintake.daprEgress" -}}
{{- if .Values.dapr.enabled -}}
- to:
    - namespaceSelector:
        matchLabels:
          kubernetes.io/metadata.name: {{ .Values.dapr.namespace }}
{{- end -}}
{{- end -}}

{{/* Do the services reach the collector over TLS? When the chart runs the collector and the private CA exists. */}}
{{- define "docintake.otlpTls" -}}
{{- if and .Values.telemetry.collector.enabled .Values.internalTls.enabled -}}true{{- end -}}
{{- end -}}

{{/* The private CA's certificate, for the services that send telemetry to the collector. Empty when the collector is plain HTTP. */}}
{{- define "docintake.caMount" -}}
{{- if include "docintake.otlpTls" . -}}
- name: internal-ca
  mountPath: /ca
  readOnly: true
{{- end -}}
{{- end -}}

{{- define "docintake.caVolume" -}}
{{- if include "docintake.otlpTls" . -}}
- name: internal-ca
  configMap:
    name: {{ include "docintake.fullname" . }}-internal-ca
{{- end -}}
{{- end -}}

{{/*
The sidecar's tracing: its sampling rate and, when the chart runs the collector, where it sends its spans (OTLP over HTTP, TLS
when the collector has it). Without the collector the sidecar only propagates trace context.
*/}}
{{- define "docintake.daprTracing" -}}
tracing:
  samplingRate: {{ .Values.dapr.tracing.samplingRate | quote }}
  {{- if .Values.telemetry.collector.enabled }}
  otel:
    endpointAddress: {{ include "docintake.collectorAddress" . | quote }}
    protocol: http
    isSecure: {{ not (empty (include "docintake.otlpTls" .)) }}
  {{- end }}
{{- end -}}
