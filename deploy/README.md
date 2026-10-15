# Deploying to DigitalOcean Kubernetes

The Helm chart in [`helm/docintake`](helm/docintake) runs the API (the gateway), the isolated document service (it classifies
and prepares every upload), the extraction service (the only pod with the model provider's key), the ClamAV pod (ClamAV and
the scan service in front of it) and an OpenTelemetry collector, and publishes the API through the Gateway API.

```
internet -> DigitalOcean load balancer -> Gateway (cilium, public TLS) -> HTTPRoute
              --TLS (private CA, BackendTLSPolicy)--> api pod (HTTPS 8443)
                  |  Dapr sidecar to sidecar, mutual TLS, only the API's app id allowed
                  +--> document pod        (classify, prepare)
                  +--> extraction pod      (model provider key; reaches 443)
                  +--> clamav pod -> scan container -> clamd on the pod's loopback
every service --TLS (private CA)--> OTel collector --HTTPS--> Honeycomb
```

What is encrypted and what is not, and how to check it, is in [`docs/internal-transport-security.md`](../docs/internal-transport-security.md).

Memory is the constraint. The nodes were resized to **two nodes of 2 vCPU / 4 GB** for this release. The ClamAV pod has no
requests or limits by choice and takes about 1 GiB at rest (3 to 4 GiB while a signature reload runs). Dapr adds its control
plane (about 240 MiB measured on kind, of which three scheduler pods are about 175 MiB; the chart of Dapr 1.18.7 has no switch
to turn the scheduler or placement off) and a sidecar per service (see [Dapr](#dapr)). Check `kubectl top nodes` after the
first deploy.

## Prerequisites

1. A DOKS cluster on Kubernetes **1.33 or later with VPC-native networking**. DigitalOcean then provides a managed
   Gateway API (Cilium) with the GatewayClass `cilium`; no controller to install. Check it:

   ```sh
   kubectl get gatewayclass cilium      # ACCEPTED must be True
   ```

2. The Gateway CRDs are present (they come with the managed Gateway API), then install **cert-manager** with Gateway
   API support on:

   ```sh
   helm upgrade --install cert-manager oci://quay.io/jetstack/charts/cert-manager \
     --namespace cert-manager --create-namespace \
     --set crds.enabled=true --set config.gatewayAPI.enabled=true
   ```

   If the Gateway CRDs appeared after cert-manager started, `kubectl rollout restart deployment cert-manager -n cert-manager`.

3. Create the Let's Encrypt issuers (edit the e-mail address first):

   ```sh
   kubectl apply -f deploy/cluster-issuer.yaml
   ```

4. Install **Dapr** (the chart's sidecars need it; the chart does not install it, like cert-manager). See [Dapr](#dapr).

5. Pick the image tag CI pushed for the commit you want: `sha-<commit>` (see the package list on GitHub). The images are
   public, so no pull secret is needed.

## Install

```sh
cp deploy/values-doks.example.yaml my-values.yaml     # set gateway.hostname and image.tag
helm upgrade --install docintake deploy/helm/docintake -n docintake --create-namespace -f my-values.yaml
```

Then create the DNS record. The load balancer address appears once DigitalOcean has provisioned it (a minute or two):

```sh
kubectl get gateway docintake -n docintake          # ADDRESS column
```

Point an **A record** for your hostname at it. cert-manager can only issue the certificate once the name resolves:

```sh
kubectl get certificate -n docintake -w        # READY becomes True
```

Start with `letsencrypt-staging` (the example values do). When the staging certificate is issued, switch:

```sh
helm upgrade docintake deploy/helm/docintake -n docintake -f my-values.yaml --set gateway.tls.clusterIssuer=letsencrypt-prod
```

(Delete the old certificate Secret `docintake-tls` first if cert-manager keeps the staging one: `kubectl delete secret docintake-tls -n docintake`.)

## Check it

```sh
curl https://<hostname>/health/ready             # {"status":"Healthy", ...}
open https://<hostname>/scalar/v1                # API reference
curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-loan-application-scan.pdf" \
  "https://<hostname>/api/documents/analyze?dispatch=false&includeData=false"
```

The gateway is ready in seconds: it loads no model. `/api/feedback/analyze` answers 503 "feedback analysis is not available":
the local model was removed and the OpenAI-based replacement is not built yet.

### First deploy of a new image: make the package public

CI pushes `ghcr.io/<owner>/docintake-api`, `-document`, `-extraction` and `-scan`. **A package that is new to GitHub starts
private**, and the cluster pulls images without credentials, so on the first deploy the pod sits in `ImagePullBackOff` (and
`helm upgrade --wait` times out) until the package is made public: GitHub, your profile, Packages, the package, Package
settings, Change visibility. The new one in this release is `docintake-scan`; `docintake-decisions` is no longer built and can
be deleted. See also [AI extraction key](#ai-extraction-key).

## Node pools

`nodeSelector` (values) pins the DocIntake pods to nodes with a label, and `malware.clamd.nodeSelector` overrides it for ClamAV.
`deploy/values-prod.yaml` puts ClamAV on `role: tools` and everything else on `role: api`. Dapr's and cert-manager's pods are
placed by their own charts (see [Dapr](#dapr) for the Dapr flag).

| Pool | Label | Droplet size | Autoscale | Holds |
|---|---|---|---|---|
| `pool-tools` | `role=tools` | 2 vCPU / 4 GB (about $24) | 1 to 3 (set 2026-10-14; 1 to 1 would cap the cost) | ClamAV pod (about 1.1 GiB, more while it reloads signatures), Dapr control plane (about 400 MiB measured on the pool) |
| `pool-api` | `role=api` | 2 vCPU / 4 GB (about $24) | 1 to 3 (2026-10-14; resized from 1 vCPU / 2 GB) | api, document, extraction, collector and their sidecars |

- Measured 2026-10-14 with DocIntake scaled down: the 1 vCPU / 2 GB api node read 738Mi, then 1011Mi (69%) a few minutes later, which left
  about 0.46 GiB for an app that peaks near 650Mi, so api moved to the 2 vCPU / 4 GB size. The tools node read 847Mi, then 1250Mi (41%) with
  Dapr's control plane installed (about 400Mi, more than the 240Mi measured on kind).
- The cluster autoscaler adds a node only for a pod that cannot be scheduled (by its requests), not for a node running out of
  memory. ClamAV therefore carries a memory request in `deploy/values-prod.yaml` (1200Mi; none by default): if the tools node cannot
  hold it the pod goes `Pending` and a node is added. One pod cannot use a second node, so a maximum above 2 only matters for that case (each node is about $24 a month).
- DOKS cannot resize a pool's droplet size: create a new pool with the label, move the workloads, delete the old one.

## Dapr

Dapr carries the API's calls to the document, extraction and scan services: the API talks to its own sidecar on
`localhost:3500`, and the sidecars talk to each other over mutual TLS with certificates from Dapr's Sentry (rotated every day
or so). Each service accepts only the API's app id and only the operations the API uses (`templates/dapr-config.yaml`); anything
else gets 403 from the callee's sidecar. The services use Dapr for service invocation only: no SDK, state, pub/sub or secrets.

Install it once per cluster, before the chart (pinned; the chart was tested against this version). `global.nodeSelector.role=tools` keeps the control plane on the tools pool; drop it if your pools are not labelled:

```sh
helm repo add dapr https://dapr.github.io/helm-charts && helm repo update
helm upgrade --install dapr dapr/dapr --version 1.18.7 --namespace dapr-system --create-namespace \
  --set global.nodeSelector.role=tools --wait
kubectl get pods -n dapr-system      # operator, sentry, sidecar-injector, placement, 3 x scheduler
```

- mTLS is on by default in that chart (`global.mtls.enabled`). If you changed the trust domain, set `dapr.trustDomain` and
  `dapr.controlPlaneTrustDomain` to match.
- **Memory.** Measured on kind (arm64), Dapr 1.18.7: control plane about 240 MiB in total (placement and scheduler cannot be
  turned off in this chart version, and the scheduler runs three replicas); a sidecar of about 30 to 60 MiB at rest, and the
  API's sidecar peaks near its 512 MiB limit while a 19 MB upload goes through three calls (scan, classify, prepare). A 192 MiB
  limit on it was OOM-killed on the second upload in a row, which is why the API's sidecar has an override
  (`dapr.sidecar.overrides.api`). The extraction sidecar carries page images and was **not measured**.
- **Large uploads.** The sidecar's default request limit is 4 MB; `dapr.maxBodySize` (32Mi) raises it above the 20 MB upload
  limit. If you raise `api.config.maxFileBytes`, raise it too.
- **No service account token.** The pods mount none (`automountServiceAccountToken: false`), and a sidecar with its built-in
  Kubernetes secret store would not start without one; the chart turns that store off (`dapr.io/disable-builtin-k8s-secret-store`).
- **Injection.** Dapr only injects pods created by a controller (Deployments, StatefulSets), not bare pods made with `kubectl apply`.
- **Who can change the policy.** Anyone who can edit a Dapr `Configuration` or a pod's annotations in the namespace can change who
  is allowed to call whom. Keep that to the deploy pipeline.
- **Turn it off** with `dapr.enabled: false`: the API then calls the Services over plain HTTP and the callees accept 8080 again.

Check it after a deploy: the `daprd` containers are `Running` (`kubectl get pods -n docintake` shows 2/2, and 3/3 for ClamAV), an
upload works, and a pod with another app id gets 403:
`kubectl exec <pod with a sidecar> -c <container> -- curl -s -XPOST localhost:3500/v1.0/invoke/docintake-document/method/documents/classify`.

## Internal TLS (private CA)

The chart makes a private certificate authority (`templates/internal-ca.yaml`) and has cert-manager issue the API's and the
collector's serving certificates from it (ECDSA P-256, 90 days, renewed 30 days before). The API serves HTTPS on 8443 only and
re-reads its certificate when the mounted file changes; the Gateway checks it with a `BackendTLSPolicy` against the CA's
ConfigMap and the Service's name; the services check the collector with `OTEL_EXPORTER_OTLP_CERTIFICATE`.

- **The CA key is in the Helm release record.** The chart creates the CA with Helm (once; later upgrades reuse the Secret
  `docintake-internal-ca`) because the Gateway's policy needs the CA certificate in a ConfigMap and cert-manager cannot publish one.
  Whoever can read Secrets in the namespace can already mint certificates; `helm get manifest` also shows the key. Rotate by
  deleting the Secret `docintake-internal-ca` and the ConfigMap of the same name, then upgrading: every certificate is reissued.
- **Needs** cert-manager (already required for the public certificate), the Gateway API `BackendTLSPolicy` CRD (present on DOKS
  with Cilium 1.20) and a Gateway controller that implements it. Check: `kubectl get backendtlspolicy -n docintake -o yaml` shows
  `Accepted` and `ResolvedRefs` conditions.
- **Off:** `internalTls.enabled: false` serves HTTP 8080 and sends telemetry over HTTP.
- Only server-authenticated TLS is used to the collector; mutual TLS is available in the exporter but not enabled (see the docs page).

## Malware scanning (ClamAV)

The chart runs one pod (`malware.clamd`, on by default) with three containers: ClamAV (`clamd`), the scan service
(`docintake-scan`, a small C# service) and Dapr's sidecar. The API streams every upload to the scan service through Dapr; the
scan service passes it to clamd over the pod's own loopback and answers with a verdict; an infected upload gets 422. clamd's
plaintext socket (3310) is not in the Service and the NetworkPolicy lets nothing but the pod itself near it. It is the largest
pod in the release, so plan the node around it:

- **Memory.** ClamAV keeps its signature database in memory: about 1 GiB measured when idle (970 MiB on kind), and its
  documentation advises 3 to 4 GiB because a database reload can briefly double that. Resize the DigitalOcean node pool
  **before** merging if the nodes cannot hold it: otherwise the ClamAV pod stays `Pending` or is evicted, `helm upgrade --wait`
  times out and the deploy job fails.
- **No limit on clamd, and a request only where you set one.** By choice the clamd container has no memory limit (a limit would kill it
  during a signature reload) and its volumes have no size limit. By default it has no request either (`malware.clamd.resources: {}`);
  `deploy/values-prod.yaml` sets 1200Mi so the scheduler and the cluster autoscaler can see it (2026-10-14). Without a request the pod is
  `BestEffort` unless the other containers set requests (they do: the scan container and the sidecar), which makes
  it `Burstable`; clamd can still be the first thing the kubelet reclaims memory from. While it is down uploads answer 503
  (`FailMode: closed`). The scan container asks for 64 MiB and is limited to 256 MiB (`malware.clamd.scan.resources`).
- **Start-up.** The pod runs ClamAV's non-root entrypoint on an empty volume, so its first start downloads the signature
  database (a minute or two; the startup probe allows 15). The database is downloaded again whenever the pod is replaced,
  and `freshclam` keeps it current. The scan container is not Ready until clamd answers a PING, so the Service sends no upload
  to a pod that cannot scan. The pod is the only one besides the telemetry collector with internet egress (HTTPS and DNS).
- **clamd is not bound to loopback yet.** clamd listens on all interfaces of the pod. Only the NetworkPolicy keeps other pods
  away from 3310 (verified on kind: reachable with the policy removed, refused with it). Binding it to 127.0.0.1 with a
  mounted `clamd.conf` is a further hardening that is not done.
- **Failure mode.** `malware.clamd.failMode` is `closed` (refuse uploads while there is no verdict, 503 with `Retry-After`)
  or `open` (accept them unscanned, with a warning in the log). Readiness shows the scanner as `Degraded` while it does not
  answer; the API stays in rotation.
- **Switch it off** with `malware.clamd.enabled: false`, which sets `Security__Malware__Provider=none`.
- **File size.** clamd's default stream limit is 25 MB, above the 20 MB upload limit, and the scan service refuses more than
  `Scan__MaxDocumentBytes` (the same 25 MB). If you raise `api.config.maxFileBytes` past that, raise both, and clamd's
  `StreamMaxLength` (mount a `clamd.conf` at `/etc/clamav`).

Check it after a deploy: `kubectl -n docintake get pods` shows `docintake-clamav` 3/3 Ready, and uploading the
[EICAR test file](https://www.eicar.org/download-anti-malware-testfile/) answers 422 `malware detected`.

## Usage history (PVC)

The API records every model call (API-key fingerprint, file name, tokens, list-price cost) in a SQLite file and serves it
at `GET /api/usage` and `/api/usage/summary` as JSON or CSV (see the [README](../README.md#ai-extraction)). On Kubernetes
the file lives on a PersistentVolumeClaim, so the history survives a pod restart or a deploy:

- `api.usage.persistence` creates the claim `docintake-usage` (1 GiB, ReadWriteOnce, the cluster's default storage class; on DOKS
  `do-block-storage`) and mounts it at `/data`. Set `enabled: false` to use an emptyDir instead, which loses the history
  whenever the pod is replaced, or `existingClaim` to bring your own volume.
- The claim is annotated `helm.sh/resource-policy: keep`: `helm uninstall` leaves it, because it is the record of what the
  model cost. Delete it with `kubectl -n docintake delete pvc docintake-usage` when you really want it gone.
- A ReadWriteOnce volume can only be attached to one pod. The API Deployment already replaces its pod in place
  (`maxSurge: 0`), so the old pod releases the volume before the new one starts; a deploy takes the same short outage as
  before. Do not raise `api.replicas` above 1 while it is on: a second pod could not mount the volume.
- The pod runs as UID 1654 with a read-only root file system, so the chart sets `fsGroup: 1654` to make the new volume
  writable for it.
- Back it up with a snapshot of the DigitalOcean volume (Volumes > the `pvc-...` volume > Snapshots).
- To let a key read everyone's usage, add an entry such as `admin-key` to the `docintake-api-key` Secret and list it in
  `api.auth.adminSecretKeys`. Without one, each key sees only its own rows.

Check it after a deploy: send one extraction, then
`curl -H "X-Api-Key: $DOCINTAKE_API_KEY" "https://api.mehmood.us/docintake/api/usage?format=csv"`. Costs use the prices in
`Extraction:Pricing`; to change them in the cluster, set the matching `Extraction__Pricing__...` environment variables with
`api.extraEnv`. For a total that matches the OpenAI dashboard, give this service its own OpenAI project.

## Telemetry (OpenTelemetry)

Traces, metrics and logs go over OTLP when `telemetry.endpoint` is set; with it empty nothing leaves the pods. Store the
Honeycomb ingest key outside Helm and point the chart at it:

```sh
kubectl -n docintake create secret generic docintake-otel \
  --from-literal=api-key="<ingest key>" --from-literal=headers="x-honeycomb-team=<ingest key>"
# values: telemetry.endpoint: https://api.honeycomb.io, telemetry.existingSecret: docintake-otel
```

**With the collector (`telemetry.collector.enabled: true`, recommended).** The chart runs an OpenTelemetry Collector
pod. The API and the renderer send OTLP to it inside the cluster; it alone holds the key (the `api-key` entry) and has
egress to the backend (HTTPS and DNS only). The renderer's NetworkPolicy then allows exactly two things beyond the API's
requests: the collector on port 4318 and DNS to kube-dns. Its isolation from the internet is unchanged, and its spans
(`document.render`, `document.render.page`, `document.render.ocr`) join the API's trace. Check the collector pod is up with
`kubectl -n docintake get pods -l app.kubernetes.io/component=otel-collector`.

**Without it** the API exports directly using the `headers` entry and the renderer exports nothing; its work shows as
the API's client span to it.

Spans cover each request (tagged with the document and the outcome), buffering, classification, preparation, the render
round trip with per-page renderer and OCR spans, each extraction and model call (agent, model, tokens, retries, prompts
and answers; page images are recorded only as sizes), and the calls to the renderer and the model provider. Prompts and
answers are captured because this is a demonstrator; remove that (`intake.prompt.*` and `intake.extraction.result` in
`DocumentExtractor`) before real documents flow through.

## First deploy after the move to the `docintake` names

The live cluster used to run as namespace and release `laya`. That namespace was deleted, so the first deploy of the `docintake`
names starts from nothing (the usage history in the old `laya-usage` claim went with it). Everything is created by the CI deploy
job; you do four things by hand:

1. After the `docker` job's first push, make the packages public (profile, Packages, `docintake-api`, `docintake-document`,
   `docintake-extraction`, `docintake-scan`, Package settings, Change visibility). A package that is new to GitHub starts
   private and the pods stay in `ImagePullBackOff`; the deploy job waits 10 minutes, so re-run it once they are public.
2. Point the DNS A record (`api.mehmood.us`) at the new load balancer: `kubectl get gateway docintake -n docintake`.
3. Wait for the certificate: `kubectl get certificate -n docintake -w` (needs the DNS record and port 80).
4. Check: `kubectl get pods -n docintake`, then `curl https://api.mehmood.us/docintake/health/ready`. The old `/laya` path is not served.

The GitHub secret `LAYA_API_KEY` is used until you add `DOCINTAKE_API_KEY`. The old `laya-sample-*` GHCR packages can be deleted.

## Upgrade, roll back, remove

```sh
helm upgrade docintake deploy/helm/docintake -n docintake -f my-values.yaml --set image.tag=sha-<new commit>
helm rollback docintake -n docintake
helm uninstall docintake -n docintake
```

## Security notes

- **Only `POST /api/documents/extract` requires a key** (see above). The other endpoints (`analyze`, feedback) and
  `/scalar/v1` are open: anyone who knows the hostname can submit documents for analysis, limited only by the built-in
  rate limiting and request limits. Restrict source addresses or add a key to those too before sharing the name widely.
- The document service parses untrusted files with native code. A NetworkPolicy allows only the API to reach it (on Dapr's
  port, over mutual TLS, and Dapr's own policy admits only the API's app id) and blocks all its egress except the collector and
  Dapr's control plane; the pods run as UID 1654 with a read-only root filesystem, no capabilities and a default seccomp
  profile. The NetworkPolicy relies on the cluster's Cilium enforcing it, which DOKS does.
- **Not covered by a NetworkPolicy: the API pod.** The Gateway's traffic reaches it with Cilium's ingress identity, which a plain
  NetworkPolicy cannot select, so the API pod can be called by any pod on its HTTPS port 8443 (and can call anything). A
  `CiliumNetworkPolicy` (`fromEntities: ingress`) would close that; it is not in the chart.

## Troubleshooting

| Symptom | Look at |
|---|---|
| Gateway has no ADDRESS | `kubectl describe gateway docintake -n docintake`; the load balancer can take a few minutes. |
| Certificate stays `False` | `kubectl describe certificate,order,challenge -n docintake`. The DNS A record must resolve to the Gateway, and port 80 must reach it. |
| Pods `Pending` | `kubectl describe pod -n docintake`: not enough memory on the node. Check `kubectl describe node`. |
| API pod `OOMKilled` or renderer restarts on big scans or big spreadsheets (the renderer now also loads Office files and every PDF's structure) | Raise `document.resources.limits.memory` (a five-page scan peaks around 550 MiB) and give the node more memory. |
| 502 from the API | The document service crashed on a document or was OOM-killed; `kubectl logs deploy/docintake-document -n docintake -c document`. |
| 503 on every upload, `daprd` restarts | `kubectl get pod -n docintake -o jsonpath='{.items[*].status.containerStatuses[*].lastState}'`: an `OOMKilled` sidecar needs a higher `dapr.sidecar.overrides.<service>.resources.limits.memory`. |
| 403 or `ERR_DIRECT_INVOKE` in a sidecar's log | Dapr's access policy refused a caller, or the callee's sidecar is not running. `kubectl get configuration -n docintake -o yaml`; the API must be the only allowed app id. |
| Pods have no `daprd` container | Dapr is not installed or its injector is down (`kubectl get pods -n dapr-system`). Install Dapr, then restart the Deployments. |
| `curl: (52)` or SSL errors to the API | The API serves HTTPS only when `internalTls.enabled`; through the Gateway check `kubectl get backendtlspolicy,certificate -n docintake`. |
| 503 with `Retry-After` | Queues are full (one document at a time by default). Raise `api.config.maxQueuedAnalyses` or add capacity. |
| 504 | `api.config.requestTimeoutSeconds` elapsed; the route timeout (`gateway.requestTimeout`) must stay above it. |
