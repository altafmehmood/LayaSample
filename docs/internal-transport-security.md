# Internal transport security

Written 2026-10-14. The plan this page implements is [`Plans/0001-internal-transport-security.md`](../Plans/0001-internal-transport-security.md)
(branch `0001-internal-transport-security`). The deploy steps are in [`deploy/README.md`](../deploy/README.md); the threat-model
rows this changes are in [`threat-model.md`](threat-model.md) (update of 2026-10-14).

## Why this was done

Every hop inside DocIntake was plaintext HTTP or a raw socket, and the only thing between the services was a NetworkPolicy
(threat model rows 4.1 and 4.2: "no shared secret or mTLS"). The document service sees every uploaded loan document, so any pod
that could reach it could read borrower data, and nothing checked who was calling. The aim: all internal communication over secure
channels, each caller identified, and each service accepting only the callers it should.

| Hop | Before | Now (Kubernetes) |
|---|---|---|
| Cilium Gateway to API | HTTP 8080 | HTTPS 8443, certificate from the release's private CA, checked by a `BackendTLSPolicy` |
| API to document / extraction | `http://<svc>:8080` | Dapr sidecars, mutual TLS, only the API's app id, only the operations it uses |
| API to ClamAV | raw clamd TCP 3310 | API to the scan service through Dapr; the scan service to clamd on the pod's loopback |
| Services to OTel collector | HTTP 4318 | HTTPS 4318, certificate from the private CA, checked by the exporter |
| Collector to Honeycomb | HTTPS | unchanged |
| Usage database (PVC) | encrypted at rest by the platform (stated by the owner; not checked here) | unchanged |

docker-compose is **not** changed in this respect: it keeps plain HTTP on its isolated internal network (a development
setting; the same URL settings switch it). Only the scan service was added there.

## Decisions service removed

**What it does now.** `POST /api/feedback/analyze` still exists, still validates its input (400 for empty or over 4000
characters) and answers `503` "feedback analysis is not available". The local model (Laya), its service, image, chart
templates, health check, tests and the `ElBruno.LocalLLMs.Decisions` package are gone. The OpenAI-based replacement is not built.

**Why.** The decisions pod asked for 2 GiB and was limited to 3 GiB; the nodes read 101 to 105% memory, and the new security
components (Dapr, scan service) needed the room.

**Key files.** `src/DocIntake.Api/Endpoints/FeedbackEndpoints.cs`, `Directory.Packages.props`, `Dockerfile`, `docker-compose.yml`,
`deploy/helm/docintake` (templates and values removed). **Does not:** analyse feedback. `docintake-decisions` images and the GHCR
package are no longer built and can be deleted by hand.

## Scan service in front of ClamAV

**What it does.** `src/DocIntake.ScanService` is a small ASP.NET service in the same pod as ClamAV. The gateway streams a document to
`POST /scan` and gets `{"outcome":"clean|infected|unavailable"}`; the service passes the bytes to clamd's `INSTREAM` over the pod's
loopback in 64 KB chunks (never in memory or on disk). `GET /health/ready` is unhealthy until clamd answers a `PING`.

**How it works.** `ClamdClient` is the old `ClamdMalwareScanner`, moved unchanged in behaviour. The gateway's
`ScanServiceMalwareScanner` (`src/DocIntake.Api/Services/Security/MalwareScanner.cs`) sends the stream (through `LeaveOpenStream`,
because `StreamContent` disposes what it is given and the next stage still reads the document) and turns anything that is not a
clean or infected verdict (HTTP error, timeout, unreadable or unknown outcome) into "no verdict", which `Security:Malware:FailMode`
(`closed` by default) turns into 503. Shared contract: `src/DocIntake.Contracts/ScanModels.cs`.

**Settings.** Gateway: `Security:Malware:Provider` (`scan-service` or `none`), `ServiceUrl` (required with `scan-service`),
`TimeoutSeconds`, `FailMode`. The old `Host` and `Port` are gone. Scan service: `Scan:ClamdHost` (127.0.0.1), `ClamdPort` (3310),
`TimeoutSeconds` (60), `MaxDocumentBytes` (25 MB, clamd's own limit). Chart: `malware.clamd.scan.image` and `.resources`.

**Try it.** `docker compose up --build`, then upload the [EICAR file](https://www.eicar.org/download-anti-malware-testfile/) to
`localhost:8080/api/documents/analyze` (422 `malware detected`) and a sample PDF (200).

**Does not.** Make clamd listen only on loopback: it still binds all interfaces of the pod, and only the NetworkPolicy keeps other pods
off 3310 (shown on kind: reachable without the policy, refused with it). Scan anything it cannot read; ClamAV only knows known
signatures. clamd stays GPL-2.0 in its own container; nothing of it is linked into the service (approved exception in CLAUDE.md).

**Tests.** `ClamdClientTests` (fake clamd: chunks, hash, infected, size limit, bad reply, dead daemon, timeout, cancel, ping),
`MalwareScannerTests` (gateway client end to end against the in-memory scan service and the fake clamd; HTTP errors, connection
failures, garbage and unknown verdicts, timeout, cancel, settings validation, the pipeline's 422/503/open paths), all offline.
`ClamdIntegrationTests` (real ClamAV, `CLAMD_HOST`) are **opt-in** and still skipped by default.

## Dapr: encrypted, authenticated, authorised service calls

**What it does.** The API calls document, extraction and scan through its own Dapr sidecar (`http://localhost:3500/v1.0/invoke/<app-id>/method`).
The sidecars talk to each other over mutual TLS with certificates from Dapr's Sentry (SPIFFE ids such as
`spiffe://public/ns/docintake/<app-id>`). Each callee's `Configuration` is deny-by-default and allows only the API's app id and only the
operations the API uses: `/documents/classify`, `/documents/prepare`, `/extract`, `/extraction/status`, `/scan` and `/health/ready`.
No Dapr SDK and no new NuGet package: the typed HTTP clients only got new base URLs. Dapr is Apache-2.0 cluster software, not a package.

**How it works.** `templates/_helpers.tpl` (`docintake.daprAnnotations`, `docintake.serviceUrl`), `templates/dapr-config.yaml`,
annotations on the api, document, extraction and clamav pod templates, `templates/networkpolicy.yaml` (callees accept only the API
pod on Dapr's port 50002, no longer 8080; egress to `dapr-system`). When the sidecar itself cannot complete a call (callee gone, or
403 from the policy) it answers with its own JSON error; `src/DocIntake.Api/Services/DaprErrors.cs` recognises it so the gateway answers
503 with `Retry-After` and never leaks the sidecar's text (it names internal services) instead of calling it a 502 crash.

**Settings.** `dapr.enabled`, `trustDomain`, `controlPlaneTrustDomain`, `namespace`, `maxBodySize` (32Mi), `tracing.samplingRate` ("1"; spans go to the collector, see the 2026-10-14 update),
`sidecar.resources` and per-service `sidecar.overrides` (api: 512Mi limit, extraction: 384Mi). Install: `deploy/README.md`, "Dapr".

**Try it.** After the deploy: `kubectl get pods -n docintake` (2/2, ClamAV 3/3), upload a sample through the Gateway, and from a pod
with another app id call `localhost:3500/v1.0/invoke/docintake-document/method/documents/classify` (403).

**Findings from running it on kind (Dapr 1.18.7, arm64), which the first draft got wrong:**
- The sidecar crashed at start (`stat /.kube/config: no such file`): the pods mount no service account token, and Dapr's built-in
  Kubernetes secret store needs one. Fixed with `dapr.io/disable-builtin-k8s-secret-store: "true"`.
- The API's sidecar was **OOM-killed at a 192 MiB limit on the second 19 MB upload in a row** (it holds the body while forwarding). With 512 MiB:
  three sequential and two concurrent 19 MB uploads all answered 200 with no restart, sampled peak about 190 MiB.
- The injector only injects pods made by a controller (Deployment and so on), not a bare Pod.
- The chart cannot turn placement or the scheduler off, and the scheduler runs three replicas: control plane about 240 MiB in total.
- The Dapr `Configuration` CRD requires `mtls.controlPlaneTrustDomain` and `mtls.sentryAddress` (kubeconform caught it).

**Does not.** Protect the Gateway's hop (that is the private CA below); authenticate end users (API keys still do); export Prometheus
metrics (off: `dapr.io/enable-metrics: "false"`) or the sidecars' own spans (sampling 0: the trace context still travels, shown below);
cover docker-compose; add a NetworkPolicy for the API pod (see "Not covered"). Anyone who can edit a Dapr `Configuration` or pod annotations in
the namespace can change who may call whom.

**Tests.** `deploy/check-chart.py` (27 checks of the rendered chart; run by the CI `helm` job, needs only helm and Python): no plain
Service URL left, a sidecar and policy on every callee, only the API allowed, callees reachable only on 50002, no 3310 in the Service.
`GatewayDocumentServiceTests` and `GatewayExtractionTests` (sidecar errors become 503, no leak). **Opt-in, free, needs Docker and kind
(done by hand here, not scripted):** the cluster checks under "What was verified".

## Private CA and TLS from the Gateway to the API

**What it does.** The chart makes a private CA (`templates/internal-ca.yaml`) and cert-manager issues the API's serving certificate from it
(ECDSA P-256, 90 days, renewed 30 days early). The API serves **HTTPS on 8443 only** (`src/DocIntake.Api/TlsSetup.cs`); probes use HTTPS; the
Service exposes 8443; a `BackendTLSPolicy` (`templates/backendtlspolicy.yaml`) makes the Gateway check the certificate against the CA
and the name `<release>-api.<namespace>.svc`.

**How it works.** `ReloadingCertificate` re-reads the mounted certificate and key when their files change (checked every 30 s; a half-written
renewal keeps the last good certificate), because Kestrel alone keeps the certificate it loaded at start. The CA is created by Helm
(`genCA`) once and reused through `lookup` on every upgrade, because a `BackendTLSPolicy` can only read the CA from a ConfigMap and
cert-manager cannot publish one. Its key is therefore in the Helm release record and in a Secret in the namespace (the cost is stated in
`deploy/README.md`). The public certificate on the load balancer is still Let's Encrypt.

**Settings.** `internalTls.enabled` (true), `caValidityDays` (3650), `certDuration` (2160h), `certRenewBefore` (720h); at run time `Tls:CertificatePath`,
`Tls:KeyPath`, `Tls:Port`. Without a certificate path (compose, `dotnet run`) the API serves plain HTTP on 8080.

**Try it.** `kubectl get certificate,backendtlspolicy -n docintake`; from a pod, `curl --cacert ca.crt https://docintake-api.docintake.svc:8443/health/live`.

**Does not.** Rotate the CA by itself (delete the Secret and ConfigMap, then upgrade), or restart pods on a CA change. **Not verified:** that
the DOKS Cilium (1.20.2, with the `BackendTLSPolicy` v1 CRD from Gateway API 1.6.1) enforces the policy. The API server accepted the object in a
server-side dry run; whether Envoy re-encrypts with validation is only known after a deploy (`Accepted` and `ResolvedRefs` conditions).

**Tests.** `TlsTests` (the listener is HTTPS only and presents the mounted certificate, plain HTTP is refused, a renewed certificate is served
without a restart, a half-written file keeps the old one), all offline.

## Services to the OTel collector over TLS

**What it does.** The collector's OTLP receiver terminates TLS with a certificate from the private CA (`reload_interval: 1h`); the services
use `https://<release>-otel-collector:4318` and trust the CA only for the exporter (`OTEL_EXPORTER_OTLP_CERTIFICATE`). No nginx: it would add a
pod and a plaintext hop on localhost; a collector sidecar per pod only moves the problem; the .NET exporter cannot route through Dapr.

**Mutual TLS is available but not enabled.** OpenTelemetry exporter 1.19.1 supports `OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE` and `_CLIENT_KEY`.
Client certificate files are read once at start, so a 90-day certificate would silently stop exporting from a long-lived pod. Callers are
limited to the app pods by a NetworkPolicy instead.

**Tests.** `TelemetryTlsTests` (an HTTPS collector stand-in with a private-CA certificate: the export arrives when the CA is given and not otherwise).
Real Honeycomb delivery was not run.

## What was verified, and what was not

Run on this machine on 2026-10-14:
- `dotnet build` and `dotnet test`: all offline tests pass (486 passed, 6 opt-in skipped).
- `helm lint`, `helm template` for every `ci/` values file and `deploy/values-prod.yaml`, kubeconform (strict, the CI schema commit) including the
  Dapr, cert-manager and `BackendTLSPolicy` kinds; `deploy/check-chart.py`; a server-side dry run of the `BackendTLSPolicy` against the DOKS API server
  (nothing created).
- `docker compose up --build`: EICAR upload 422, sample PDF 200, readiness lists scanner, document and extraction.
- **On a local kind cluster** (cert-manager, Dapr 1.18.7, the chart with real images and real ClamAV; no Cilium, no Gateway): the API served HTTPS only
  and the private CA plus the Service name verified (a wrong name and the system trust store were refused); EICAR 422 and a clean PDF 200 through the
  sidecars; **a pod with another app id got 403 `PermissionDenied` naming its SPIFFE id** on document, scan and the health route even with the
  NetworkPolicies removed; with the policies removed a plain pod *could* reach the document pod's port 8080 and clamd's 3310 (so the policies matter),
  with them it could not; plain HTTP to Dapr's 50002 got no answer; 19 MB uploads (about 32 s each through `kubectl port-forward`) passed scan, classify and
  prepare; the trace id of a request appeared in the scan service's and the document service's logs (the context travels through the sidecars). Measured memory:
  Dapr control plane about 240 MiB; sidecars 30 to 70 MiB at rest and about 190 MiB peak for the API's; ClamAV 970 MiB.
- Re-installing the chart three times kept the same CA (the leaf certificate still verified against the ConfigMap).

**Not verified:**
- Cilium enforcing the `BackendTLSPolicy`, the Gateway reaching the API on 8443, and any of this on the DOKS cluster (nothing was deployed or changed there).
- Real memory on the DOKS nodes, and the extraction sidecar under real page-image bodies (needs the paid OpenAI path; the 384Mi limit is a guess).
- Certificate renewal in a running cluster (the reload is tested with rewritten files, not with cert-manager).
- Honeycomb receiving data over the TLS collector; the CI workflow itself (the new precheck and `check-chart.py` steps never ran on GitHub).
- A paid end-to-end extraction through Dapr (opt-in, spends tokens): not run.

## Not covered

- **The API pod has no NetworkPolicy.** The Gateway's traffic reaches it with Cilium's ingress identity, which a plain NetworkPolicy cannot select;
  a `CiliumNetworkPolicy` with `fromEntities: ingress` would. Until then any pod can reach the API on 8443 (it still needs an API key for extraction).
- clamd binds all interfaces of its pod (above). Dapr's metrics are off (sidecar spans are on, see [otel-observability.md](otel-observability.md)). docker-compose is plaintext.
- Where the old releases' decisions Deployment, Service and NetworkPolicy exist, `helm upgrade` removes them.

## Before you deploy

The same steps, with exact commands and a place to record each as done, are in the plan's [Operator quickstart](../Plans/0001-internal-transport-security.md#operator-quickstart-tasks-only-a-person-with-cluster-github-or-billing-access-can-do).

- [ ] Resize check: the nodes are 2 x (2 vCPU / 4 GB) and read 101 to 105% memory before this. Removing the decisions pod frees 2 to 3 GiB, Dapr adds about 240 MiB plus sidecars (up to about 1.3 GiB at the limits). Look at `kubectl top nodes` first.
- [ ] Install Dapr 1.18.7 in `dapr-system` (`deploy/README.md`, "Dapr") **before merging**. The CI deploy job now stops with a clear error if the Dapr, cert-manager or `BackendTLSPolicy` CRDs or Dapr's control plane are missing. Without sidecars every upload would fail.
- [ ] After the first push, make the `docintake-scan` GHCR package public; the first deploy waits on it (`ImagePullBackOff`) and the job times out, so re-run it.
- [ ] After the deploy: pods 2/2 (ClamAV 3/3); `kubectl get backendtlspolicy -n docintake -o yaml` shows `Accepted`; `curl https://<host>/<prefix>/health/ready`; EICAR 422; a clean PDF 200; a pod with another app id gets 403.
- [ ] If the Gateway answers 502 or 503 after the deploy, the `BackendTLSPolicy` is not working: `helm upgrade ... --set internalTls.enabled=false` returns the API to plain HTTP 8080 (the rest stays on).
- [ ] Record `kubectl top pods --containers -n docintake` and set the sidecar limits in `deploy/values-prod.yaml` from it; run a real extraction and look at the extraction sidecar.
- [ ] Delete the old `docintake-decisions` package and the `Decisions` settings anywhere else you keep them.

## Update 2026-10-14: sidecar spans reach the collector

Moved to [otel-observability.md](otel-observability.md) (plan 0002), where it is written up and checked on kind. The default of `dapr.tracing.samplingRate` is now `"1"`; `"0"` turns it off.
