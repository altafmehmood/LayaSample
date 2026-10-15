# 0001-internal-transport-security

Branch: `0001-internal-transport-security`. Tick the task boxes as each task finishes (commit the tick with the work). The post-build page `docs/internal-transport-security.md` (per CLAUDE.md) links back here.

## Why

Every hop inside DocIntake is plaintext, and the only thing between services is a NetworkPolicy (threat model 4.1/4.2: "No shared secret or mTLS"). The document service sees every uploaded loan document, so any pod that can reach it can read borrower data. The goal is that all internal communication happens over secure channels.

| Hop | Today |
|---|---|
| Cilium Gateway -> API | HTTP 8080 (`httproute.yaml`) |
| API -> document / extraction / decisions | `http://<release>-<svc>:8080` (`api-deployment.yaml:55-69`) |
| API -> ClamAV | raw clamd TCP 3310 (`ClamdMalwareScanner`) |
| services -> OTel collector | HTTP 4318 (`_helpers.tpl:80`) |
| collector -> Honeycomb | HTTPS already |
| PVC | encrypted at rest by the platform (stated by the owner; not verified here) |

The nodes read 101-105% memory (3.0-3.2 GiB each) before this change, so memory is the binding constraint. Removing the decisions pod (2Gi requested / 3Gi limit) pays for the new security components.

## What

Decisions: **Dapr** for service-to-service; **ClamAV wrapped in a C# service**; **self-signed cert** so Gateway -> pod is encrypted; **OTLP hop encrypted**; **decisions (Laya) service removed**; nodes are now 2 x (2 vCPU / 4 GB).

End state:
- API -> document / extraction / scan: Dapr sidecars, mTLS with auto-rotated certs (Sentry), plus a deny-by-default allow-list so only the API's app-id may call each service. No Dapr SDK and no new NuGet package: calls go to `http://localhost:3500/v1.0/invoke/<app-id>/method`, so only the configured base URLs change.
- ClamAV: new `DocIntake.ScanService` runs as a second container in the ClamAV pod and talks to clamd over pod loopback; the API calls it through Dapr. clamd's 3310 is never exposed (GPL-2.0 exception unchanged; nothing linked in).
- Gateway -> API: a cert-manager private CA (self-signed) issues the API's cert; Kestrel serves HTTPS; Cilium validates it with a `BackendTLSPolicy`.
- Services -> OTel collector: the collector's own OTLP TLS receiver, certs from the same private CA. No nginx (extra pod, plaintext localhost hop); a per-pod collector sidecar only moves the problem; the .NET OTLP exporter cannot route via Dapr. Server-auth TLS is the baseline; mutual TLS only if the exporter supports a client cert.
- Decisions service deleted; `/api/feedback/analyze` stays routed and returns 503 "not available" until the OpenAI replacement (keeps the contract).

Costs and risks: Dapr control plane (operator, sentry, injector; placement and scheduler off) plus a sidecar on api, document, extraction and scan, bounded with `dapr.io/sidecar-memory-limit` and measured with `kubectl top`. daprd's default request cap is 4 MB while uploads go to 20 MB, so set `dapr.io/http-max-request-size` and verify streaming. Cilium WireGuard (no sidecars, no identity) was the alternative and stays an optional later layer. Dapr is Apache-2.0 infrastructure, not a package.

Not doing: docker-compose keeps plain HTTP on its isolated internal network (dev-only, documented); Dapr-in-compose; PVC encryption (platform); new NuGet packages.

Unknowns to settle: BackendTLSPolicy support in the cluster's Cilium (fallback: Cilium WireGuard for that hop, recorded as such; do not claim "all encrypted" until checked); Kestrel cert reload on renewal; daprd streaming of 20 MB bodies; .NET OTLP exporter client-cert support.

## Dapr in plain English

**What it is.** Dapr is a small helper program (a "sidecar") that runs next to each of our services in the same pod. Our code talks to the helper on the same machine, and the helpers talk to each other across the cluster.

**What it solves for us now**
- **Encryption between services.** The helpers encrypt every call to each other. We don't write TLS code or manage service certificates; Dapr issues short-lived ones and renews them itself.
- **Knowing who is calling.** Each helper proves its identity with its certificate, so the document service can tell "this is really the API" instead of trusting any pod that reaches it.
- **Deciding who may call what.** A deny-by-default list says only the API may call document, extraction and scan. Everything else is refused.
- **Almost no code change.** Our services keep serving normal HTTP; we only change the addresses the API calls.

**What it does not solve.** It doesn't protect the traffic coming in from the internet (the Gateway cert in task D does that), doesn't cover the OTLP telemetry hop (task F does), and doesn't know about end users (the API keys still do). It also isn't free: it costs memory per pod, which is why we removed the decisions service first.

**What it could offer later, if we want it (not in this plan)**
- **Retries, timeouts and circuit breakers** on service calls, set in config instead of code (Dapr "resiliency").
- **Secrets store.** Read the OpenAI key from Kubernetes Secrets or a vault through one API, with per-service rules on which secrets each service can read.
- **Pub/sub and queues.** Process a large upload in the background instead of holding the request open; swap the broker (Redis, Kafka, a cloud queue) without code changes.
- **State store.** Replace the SQLite usage database on a PVC with a managed store without rewriting the usage code.
- **Bindings and workflows.** Call out to or react to external systems, or run multi-step document pipelines with retries.
- **The future OpenAI decisions service** can be added as one more app-id with its own allow-list entry.

**Observability.** Dapr has built-in observability and no extra extension is needed:
- **Tracing.** Calls through Dapr carry the W3C `traceparent` header, so the API's span and the document service's span stay in one trace. Each sidecar can also export its own spans over OTLP (set in the Dapr `Configuration` under `tracing`: sampling rate and OTLP endpoint). That fits our collector, but the collector's receiver will be TLS (task F), so the sidecars would need `isSecure` and the private CA, or a short-lived plaintext exception. Check this in the spike.
- **Metrics.** Each sidecar serves Prometheus metrics on port 9090 (calls, latency, errors, mTLS cert expiry). Our collector has no Prometheus receiver today; enabling that is optional and not in this plan.
- **Logs.** Sidecar logs are JSON or plain text; `enable-api-logging` records every Dapr API call, which we leave off by default so document names don't land in logs.
- **Dashboards.** Dapr ships a Grafana dashboard set; we use Honeycomb, so we would rely on traces first.
- Traces will show the extra hop (app -> sidecar -> sidecar -> app) as added spans and latency, which is expected.

## How (tasks)

**A. Remove decisions**
- [x] A1. Delete `src/DocIntake.DecisionsService`, the Dockerfile `decisions` target and model stage, `DocIntake.slnx`, CI entries, compose service.
- [x] A2. Delete chart templates (`decisions-deployment/-service`, its NetworkPolicy), values, `values.schema.json`, prod values.
- [x] A3. Remove `Services/Decisions/*`, `AddDecisionsServiceClient`, the `decisions-service` health check, tests; make `/api/feedback/analyze` return 503; update `ArchitectureTests`.

**B. ClamAV scan service**
- [x] B1. New `src/DocIntake.ScanService` (shaped like `DocumentService`, uses `ServiceDefaults`): `POST /scan` streams the body to clamd INSTREAM, `GET /health/ready` pings clamd. Move `ClamdMalwareScanner` and its tests here.
- [x] B2. API: `ScanServiceMalwareScanner : IMalwareScanner` (streams, fail-closed 503 as today), `Provider` `scan-service`/`none`, client and readiness check in `ServiceClients.cs`.
- [x] B3. slnx, Dockerfile `scan` target, CI, `packages.lock.json`; the clamav pod gets the second container and no clamd Service port.
- [x] B4. Tests: fake clamd socket (clean, infected, oversize, no verdict); client fail-closed.

**C. Private CA and certificates**
- [x] C1. `templates/internal-ca.yaml`: cert-manager selfSigned Issuer -> CA Certificate -> CA Issuer; `ca.crt` ConfigMap. **Done:** private CA made by the chart (Helm `genCA`, reused through `lookup`, kept in a Secret) because BackendTLSPolicy needs the CA in a ConfigMap and cert-manager cannot publish one; a cert-manager CA Issuer signs the leaves.
- [x] C2. Leaf Certificates for the API Service and the collector, mounted read-only. **Done:** API and collector leaf Certificates (ECDSA P-256, 90 days, renewed 30 days before).

**D. Gateway -> API TLS**
- [x] D1. Kestrel HTTPS 8443 from the mounted cert (verify reload on renewal, else checksum-annotation restart); probes `scheme: HTTPS`; Service exposes 8443. **Done:** `Tls__*` settings, `ReloadingCertificate` (re-reads the files every 30 s), HTTPS-only listener, HTTPS probes, Service on 8443. Tested in-process.
- [ ] D2. `BackendTLSPolicy` and `httproute.yaml` backend port 8443; verify support on the cluster, else WireGuard fallback. **Partly done, left open:** `BackendTLSPolicy` and the HTTPRoute port 8443 are written; Cilium 1.20.2 and the v1 CRD are on the DOKS cluster and a server-side dry run accepted the object, but whether Envoy enforces it is only known after a deploy.

**E. Dapr**
- [x] E1. Document Dapr as a cluster prerequisite (`helm install dapr`, mTLS on, placement/scheduler off), like cert-manager. **Done:** `deploy/README.md`, section "Dapr" (pinned 1.18.7). Correction: placement and the scheduler cannot be turned off in this chart version; the control plane measured about 240 MiB.
- [x] E2. Pod annotations via `_helpers.tpl` (app-id, app-port, max request size, sidecar limits, health path).
- [x] E3. `templates/dapr-config.yaml`: mTLS on, `accessControl.defaultAction: deny`, per-callee policy allowing only the API app-id. **Done:** one `Configuration` per callee, deny by default, only the API app id and only the operations it uses.
- [x] E4. Switch the API's service URLs to the Dapr invoke form.
- [x] E6. Observability: confirm one trace spans API -> sidecars -> document in Honeycomb; set the Dapr `Configuration` `tracing` block (sampling, OTLP endpoint, TLS/CA) or leave sidecar spans off and rely on trace-context propagation; decide on metrics scraping (default: off). Add `dapr.io/config` to every pod and a chart test that fails when a callee lacks it. **Done:** trace context travels through the sidecars (a request's trace id was in the API's, the scan service's and the document service's logs on kind); sidecar spans and metrics are off by choice; every callee carries `dapr.io/config`, which `deploy/check-chart.py` asserts.
- [x] E5. `networkpolicy.yaml`: callees accept only the API on Dapr port 50002 (not 8080); egress to `dapr-system`; add the missing API-pod policy; drop the 3310 rule. **Done:** callees accept only Dapr's 50002 from the API pod; egress to `dapr-system`. **Not done:** a NetworkPolicy for the API pod, because the Gateway's traffic arrives with Cilium's ingress identity, which a plain NetworkPolicy cannot select (it would need a CiliumNetworkPolicy); left open and recorded in the threat model.

**F. OTLP over TLS**
- [x] F1. Collector config: `tls` on the OTLP receiver (`client_ca_file` only if F2 passes). **Done:** the receiver has `tls` with `reload_interval`; services use `https://` and `OTEL_EXPORTER_OTLP_CERTIFICATE`.
- [x] F2. Spike: exporter client-cert support in the current OpenTelemetry package; services use `https://...:4318` and trust the CA via `SSL_CERT_FILE`. **Done:** spike result: the OTLP exporter 1.19.1 supports `OTEL_EXPORTER_OTLP_CERTIFICATE`, `_CLIENT_CERTIFICATE` and `_CLIENT_KEY`. Only server-authenticated TLS is enabled: client certificate files are read once at start, so a 90-day client certificate would silently stop exporting in a long-lived pod. Verified in-process with a private CA (`TelemetryTlsTests`).

**G. Tests and CI**
- [x] G1. Chart tests: no `http://<release>-*:8080` left, callee policies don't open 8080, new templates render. `helm lint`/`template`/kubeconform (Dapr CRDs may need a skip or catalog entry). **Done:** `deploy/check-chart.py` (27 checks, run by the CI `helm` job), kubeconform with the CI schema commit, `ArchitectureTests` (the gateway has no raw clamd client), `TlsTests`, `TelemetryTlsTests`, sidecar-error tests.
- [x] G2. Opt-in (kind + Docker, free): Dapr end-to-end upload; a foreign app-id gets 403; plaintext to callee:8080 refused; `curl --cacert` to API 8443 works. **Done:** run by hand on a local kind cluster (not scripted into CI): a foreign app id got 403 with its SPIFFE id; plaintext to 8080 refused by the NetworkPolicy and reachable without it; plaintext to 50002 refused; `curl --cacert` to the API verified. `BackendTLSPolicy` could not be tried (no Cilium on kind).
- [ ] G3. Opt-in, paid: real extraction through Dapr (spends OpenAI tokens). **Partly done, left open:** not run: it spends OpenAI tokens, and the extraction sidecar's memory limit (384Mi) is therefore a guess.

**H. Docs (per CLAUDE.md) and verification**
- [x] H1. `docs/internal-transport-security.md` (date, why, per-feature what/how/settings/try/not-done/tests, verified vs not, pre-deploy checklist: Dapr install, node size, sidecar memory, 20 MB test). **Done:** `docs/internal-transport-security.md`.
- [x] H2. README Features table, Quick start prerequisite, API table, dated entry at the top of Quickstarts by date (commands run first; unrunnable ones marked), remove the Feedback-sentiment and decisions text; `deploy/README.md` Dapr install and upgrade order; `docs/threat-model.md` dated update (4.1, 4.2, 2.2b, boundaries 3-4). **Done:** README (features row, quick start, architecture, security, Docker, Kubernetes, feedback and a new section, a dated 2026-10-14 entry), `deploy/README.md`, `docs/threat-model.md` (update), notes in the two older docs.
- [x] H3. Verify: `dotnet build`/`test`; `helm lint`/`template`; compose with the scan service (EICAR -> 422, clean passes); on the cluster check daprd mTLS logs, negative tests, `kubectl top`, record sidecar memory in `deploy/values-prod.yaml`. Report run vs not run. **Done:** 486 tests pass and 6 opt-in skip; helm lint/template/kubeconform; compose with real ClamAV (EICAR 422, sample 200, feedback 503); the kind run; the license check and a locked-mode restore pass. Not run: the DOKS cluster, Cilium's `BackendTLSPolicy`, Honeycomb over TLS, the GitHub workflow, a paid extraction.
- [x] H4. After commit, regenerate the archify diagram from HEAD (cite committed files only). **Done:** regenerated into `.archify/current/` (finalize and visual-check pass), last from `6af5186`: scan service, Dapr mTLS labels, the Dapr control plane in the cluster frame, decisions removed. The sidecars and the private CA are described in labels and cards, not drawn.

## Operator quickstart (tasks only a person with cluster, GitHub or billing access can do)

I (the agent) cannot do these: they touch the DOKS cluster, GitHub settings, or spend money. Do them in order. Tasks O1 to O4 must be
finished **before the branch is merged to `main`**, because CI deploys on every push to `main`. Commands assume the release
`docintake` in namespace `docintake`; replace `<hostname>` with your `gateway.hostname`. Details for each step are in
[`deploy/README.md`](../deploy/README.md) and [`docs/internal-transport-security.md`](../docs/internal-transport-security.md) ("Before you deploy").

### How to record that a task is done

For every task, when it is finished:

1. Tick its box below (`- [ ]` to `- [x]`) and add one line under it: `Done YYYY-MM-DD by <name>: <result>`.
2. Paste the output the task asks for into the **Evidence log** table at the end of this section (date, task, command, what you saw). Trim secrets;
   nothing in the output should contain keys or tokens.
3. If a result differs from "Expect", do **not** tick the box. Write what you saw under the task, mark it `BLOCKED`, and stop at that task.
4. When O7 is ticked, tick plan task D2; when O10 is ticked, tick G3. Update the "verified / not verified" lists in
   `docs/internal-transport-security.md` with the date, and commit the plan and docs together.

### Tasks

**Before merging to `main`**

- [x] **O1. Check node headroom.** Why: the nodes (2 x 2 vCPU / 4 GB) read 101 to 105% memory before this change.
  Run: `kubectl top nodes`. Expect: after removing decisions the nodes should have about 2 to 3 GiB free in total; Dapr needs about 240 MiB plus
  sidecars. Record: the `kubectl top nodes` output. **Added 2026-10-14:** the cluster now has an `api` pool (2 vCPU / 4 GB, autoscale 1 to 2, label
  `role=api`) and a `tools` pool (1 vCPU / 2 GB, autoscale 1 to 5, `role=tools`). `deploy/values-prod.yaml` sets `nodeSelector: {role: api}` so DocIntake pods stay off the
  small nodes; also run `kubectl get pods -A -o wide` and check the Dapr control plane (`dapr-system`) landed where you want it (move it with the Dapr chart's `global.nodeSelector` if not).
  Pool bounds set by the operator on 2026-10-14: `pool-api` 1 to 3, `pool-tools` 1 to 5 (suggested 1 to 1: nothing on tools can use a second node, and each costs about $24).
  **Resized 2026-10-14:** `pool-api` is now 2 vCPU / 4 GB, autoscale 1 to 3 (about $24 per node, up to $72); `pool-tools` stays 2 vCPU / 4 GB. Re-run `kubectl top node` on the new api node and record it here as the baseline.
  New api node baseline (2026-10-14, Dapr installed, no DocIntake pods): `pool-api-v2` 879Mi / 29% (about 2.1 GiB free); `pool-tools` 1384Mi / 46% (about 1.6 GiB free; ClamAV would leave about 0.5 GiB). Both pools autoscale 1 to 3.
  Note: ClamAV had no memory request, so it could never make the autoscaler add a node; 2026-10-14: `malware.clamd.resources` added and `deploy/values-prod.yaml` requests 1200Mi (still no limit), so a full tools node shows as Pending and gets a new node. If a node is still above about 85%, stop and tell the agent; the sidecar limits need rethinking.
  Done 2026-10-14 by the operator (baseline, all DocIntake workloads scaled down): `pool-api` (s-1vcpu-2gb, `role=api`) 738Mi / 50% (so about 1.47 GiB usable, about 0.73 GiB free);
  `pool-tools` (s-2vcpu-4gb, `role=tools`) 847Mi / 28% (about 3 GiB usable, about 2.2 GiB free). ClamAV (about 1.1 GiB) plus the Dapr control plane (about 240 MiB) fit on tools with
  about 0.8 GiB spare for signature reloads. **Watch:** the api node's own overhead (738Mi) is more than the 0.5 GiB I assumed; the app needs about 0.5 GiB at rest and about 1 GiB
  during a 19 MB upload, so that node is likely too small. Re-measure at O9 and move to the 2 vCPU / 4 GB size if it reads above about 75%.
- [x] **O2. Install Dapr 1.18.7.** Run:
  `helm repo add dapr https://dapr.github.io/helm-charts && helm repo update && helm upgrade --install dapr dapr/dapr --version 1.18.7 --namespace dapr-system --create-namespace --set global.nodeSelector.role=tools --wait`
  Expect: `kubectl get pods -n dapr-system` shows operator, sentry, sidecar-injector, placement and 3 scheduler pods `Running`.
  Record: that pod list (with `-o wide`, to show they landed on the tools node) and `helm list -n dapr-system`.
  Done 2026-10-14 by the operator: `helm list -n dapr-system` shows `dapr` revision 1, `deployed`, chart and app 1.18.7. All 7 pods `Running` on `pool-tools-3jptvl`: operator, placement, 3 schedulers, sentry, sidecar-injector.
  `dapr-scheduler-server-1` restarted once about 16 s after start (the scheduler replicas form a quorum as they come up; expected, and it was stable afterwards). Re-check its restart count at O6.
- [x] **O3. Confirm the other prerequisites.** Run:
  `kubectl get gatewayclass cilium`, `kubectl get crd backendtlspolicies.gateway.networking.k8s.io`, `kubectl get pods -n cert-manager`, `kubectl get clusterissuer`.
  Expect: GatewayClass `ACCEPTED True`; the CRD exists; cert-manager pods `Running`; the issuers `READY True`. Record: the four outputs.
  Done 2026-10-14 by the operator: GatewayClass `cilium` ACCEPTED True; CRD `backendtlspolicies.gateway.networking.k8s.io` present (v1, storage); cert-manager's three pods Running; ClusterIssuers `letsencrypt-prod` and `letsencrypt-staging` READY True.
  Same time, `kubectl top node` with Dapr installed and no DocIntake pods: `pool-api` 1011Mi / 69% (was 738Mi / 50% about 3 minutes after the node was created), `pool-tools` 1250Mi / 41% (Dapr's control plane added about 400Mi, more than the 240Mi measured on kind).
  **Consequence:** the 1 vCPU / 2 GB api node has about 0.46 GiB free, less than the app's measured peak (about 650Mi); ClamAV on tools would leave about 0.55 GiB spare. See the node-size note under O1.
- [x] **O4. Make a rollback point.** Run: `helm history docintake -n docintake` and note the current revision; note the current image tag
  (`helm get values docintake -n docintake -o yaml | grep tag`). Record: both, so a rollback is `helm rollback docintake <revision> -n docintake`.
  Done 2026-10-14 by the operator: current release `docintake` is revision 5, `deployed` (revisions 2 to 4 superseded; 1 failed on an HTTPRoute `replacePrefixMatch` rule and was superseded), image tag `sha-e8e8482`.
  Rollback: `helm rollback docintake 5 -n docintake`. Keep the `docintake-decisions` GHCR package until O12, because a rollback to revision 5 needs that image.
  **The DocIntake workloads are currently scaled down by hand.** Helm's three-way merge may not restore a replica count that is unchanged between the old and new manifests, so after the first deploy
  run `kubectl get deploy -n docintake` and, if any shows 0 replicas, `kubectl scale deploy/<name> --replicas=1 -n docintake` (or `kubectl rollout restart`). Check this at O6.

**First deploy**

- [ ] **O5. Merge to `main`, then make the new package public.** After the first CI run pushes `ghcr.io/<owner>/docintake-scan`: GitHub, your profile,
  Packages, `docintake-scan`, Package settings, Change visibility, Public. Then re-run the failed deploy job (the first run waits on
  `ImagePullBackOff` and times out). Expect: the deploy job is green. Record: the workflow run URL and `kubectl get pods -n docintake`.
- [ ] **O6. Check the pods.** Run: `kubectl get pods -n docintake`. Expect: api, document and extraction `2/2 Running` (if a Deployment shows 0 replicas because it was scaled down by hand, scale it to 1 first); clamav `3/3 Running` (the first start
  downloads ClamAV's signatures, up to 15 minutes); no `docintake-decisions` pod. Record: the output.
- [ ] **O7. Check the Gateway to API TLS (closes plan task D2).** Run:
  `kubectl get backendtlspolicy -n docintake -o yaml` and `curl -i https://<hostname>/health/ready`.
  Expect: the policy's status has `Accepted: True` and `ResolvedRefs: True`; `/health/ready` answers 200. If the Gateway answers 502 or 503, the policy is
  not enforced: run `helm upgrade docintake deploy/helm/docintake -n docintake -f my-values.yaml --set internalTls.enabled=false`, mark O7 `BLOCKED`,
  and tell the agent (the "all internal traffic encrypted" claim must not be made while this is blocked). Record: the policy status block and the curl status line.
- [ ] **O8. Check the security behaviour.** Run (EICAR as in the README, Docker section):
  `printf 'X5O!P%%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*' > eicar.pdf`
  `curl -s -o /dev/null -w '%{http_code}\n' -F file=@eicar.pdf "https://<hostname>/api/documents/analyze?dispatch=false"` then the same with
  `docs/samples/pdf/corpus/redwood-mechanical-loan-application-scan.pdf`, and
  `curl -s -o /dev/null -w '%{http_code}\n' -XPOST -H 'content-type: application/json' -d '{"text":"fine"}' https://<hostname>/api/feedback/analyze`.
  (use `{"text":"fine"}` as the body for a valid feedback request). Expect: EICAR `422`, the sample `200` (`dispatch=false` is the free path), feedback `503`.
  The "a pod with another app id gets 403" check was run on kind (G2) and is not repeated here; it needs a throwaway annotated Deployment. Record: the three status codes.

**After the first deploy**

- [ ] **O9. Measure memory and write it down.** Run: `kubectl top pods --containers -n docintake` and `kubectl top nodes`. Then edit
  `deploy/values-prod.yaml` (it has no `dapr:` block yet: add `dapr.sidecar.resources` and `dapr.sidecar.overrides.<api|document|extraction|scan>.resources`,
  same shape as in `deploy/helm/docintake/values.yaml`) to the measured values plus headroom, as was done for decisions, and commit.
  Expect: sidecars 30 to 70 MiB at rest, ClamAV near 1 GiB. Record: both outputs.
- [ ] **O10. (Paid, opt-in) Run one real extraction (closes plan task G3).** This spends OpenAI tokens (a few cents for one page; the cost depends on the model
  in `extraction` settings). Only do it after the OpenAI key is set (`deploy/README.md`, "AI extraction key"). Run
  `curl -H "X-Api-Key: $KEY" -F "file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf" https://<hostname>/api/documents/extract` (the access key is
  needed on a deployment; see README "Authentication"), and while it runs, `kubectl top pods --containers -n docintake | grep extraction`.
  Expect: 200 with extracted data; the extraction sidecar (`daprd`) stays under its limit (384Mi is a guess). Record: the status code, the `top` line, and the
  approximate cost from the OpenAI usage page.
- [ ] **O11. Look at telemetry.** In Honeycomb, open one trace from the O8 upload. Expect: one trace id across the `api`, `scan` and `document` services, and spans arriving at all
  (this is the first proof that the collector's TLS receiver works with Honeycomb). If nothing arrives: `kubectl logs -n docintake deploy/docintake-otel-collector`
  and look for TLS handshake errors. Record: the trace id (not a link with keys in it).
- [ ] **O12. Clean up.** Delete the old GHCR package `docintake-decisions` and any `Decisions__*` settings kept elsewhere (secrets store, local `.env`). Record: done/not applicable.

### Evidence log

Add one row per task as you finish it. Keep each row short; link to a gist or the CI run for long output.

| Date | Task | Who | Command run | What you saw | Result (ok / BLOCKED) |
|---|---|---|---|---|---|
| 2026-10-14 | O1 | operator | `kubectl top node`, `kubectl get nodes --show-labels` | api 738Mi/50%, tools 847Mi/28%, labels `role=api`/`role=tools` | ok (baseline only) |
| 2026-10-14 | O2 | operator | `helm upgrade --install dapr ... --set global.nodeSelector.role=tools`, `kubectl get pods -o wide`, `helm list -n dapr-system` | 7 pods Running on pool-tools, scheduler-1 restarted once at start; release deployed 1.18.7 | ok |
| 2026-10-14 | O3 | operator | `kubectl get gatewayclass cilium`, `get crd backendtlspolicies...`, `get pods -n cert-manager`, `get clusterissuer`, `top node` | all present and ready; top node api 1011Mi/69%, tools 1250Mi/41% | ok |
| 2026-10-14 | O4 | operator | `helm history docintake -n docintake`, `helm get values ... \| grep tag` | revision 5 deployed; tag sha-e8e8482 | ok |

### After the operator tasks

The agent then: ticks D2 and G3 here from the log, records the dated results in `docs/internal-transport-security.md` ("What was verified, and what was not"),
adds a dated README entry if behaviour changed, and (once a commit exists) regenerates the archify diagram from HEAD (H4).

## Changes from the plan while building it

- **Dapr control plane:** placement and the scheduler cannot be switched off in the 1.18.7 chart (three scheduler replicas); about 240 MiB measured.
- **Sidecar limits:** 192Mi OOM-killed the API's sidecar on the second 19 MB upload; the API's sidecar now has 512Mi (`dapr.sidecar.overrides`).
- **Found only by running it:** the sidecar needs `dapr.io/disable-builtin-k8s-secret-store` (pods mount no service account token); the Dapr `Configuration` CRD wants `mtls.controlPlaneTrustDomain` and `sentryAddress`; the injector skips bare Pods.
- **API pod NetworkPolicy:** not added (Cilium's ingress identity is not selectable by a plain NetworkPolicy). Left open in the threat model.
- **Private CA:** made by Helm (`genCA` plus `lookup`) instead of cert-manager alone, because a `BackendTLSPolicy` reads the CA from a ConfigMap.
- **Deploy guard:** the CI deploy job now fails early when Dapr, cert-manager or the `BackendTLSPolicy` CRD is missing.
- **H4 left open:** the archify diagram is regenerated from HEAD after a commit; nothing has been committed.
