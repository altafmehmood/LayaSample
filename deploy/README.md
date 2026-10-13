# Deploying to DigitalOcean Kubernetes

The Helm chart in [`helm/docintake`](helm/docintake) runs the API (the gateway), the isolated renderer (now the document
service: it classifies and prepares every upload) and the extraction service (the only pod with the model provider's key), and
publishes the API through the Gateway API.

```
internet -> DigitalOcean load balancer -> Gateway (cilium, TLS) -> HTTPRoute -> api Service -> api pod
                                                                                   |
                                                       document Service <-----------+  (NetworkPolicy: only the API may call it)
                                                       extraction Service <--------+  (only the API may call it; it may reach 443, DNS, collector)
                                                       decisions Service <---------+  (only the API may call it; it may reach only the collector)
```

The API, the renderer, the extraction service and the decisions service fit **one node with 1 vCPU and 2 GB of memory** by
their requests (the extraction pod asks for 192 MiB and is limited to 768 MiB; the decisions pod asks for 256 MiB and is
limited to 1 GiB, as the API pod was while it carried Laya): one replica of each, one document analysed at a
time, short queues, no CPU limits, and a rolling update that replaces the pod in place (a second copy would not fit).
Expect slow OCR and brief downtime on upgrades. **The malware scanner changes that:** the ClamAV pod (on by default) needs
about 4 GB of its own, so use a node with 8 GB, or set `malware.clamd.enabled: false` (see
[Malware scanning](#malware-scanning-clamav)). For real traffic use a bigger node and raise the values under `api.config`,
`document.config`, `extraction.maxConcurrent` and `*.resources`. The API pod's limits (256 MiB requested, 1 GiB limit) were set
while it carried the model; it carries none now, and I did not lower them without measuring.

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

4. Pick the image tag CI pushed for the commit you want: `sha-<commit>` (see the package list on GitHub). The images are
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

The gateway is ready in seconds: it loads no model. The decisions service is the slow one on a small node (its image carries
the 840 MB Laya model and loading takes minutes); its startup probe allows ten minutes, and until it is ready the gateway
reports `decisions-service` as `Degraded` and feedback analysis answers 503. Documents and extraction are not affected.

### First deploy of the decisions image: make the package public

CI pushes the decisions image to `ghcr.io/<owner>/docintake-decisions`. **A package that is new to GitHub starts private**, and
the cluster pulls images without credentials, so on the first deploy the decisions pod sits in `ImagePullBackOff` (and
`helm upgrade --wait` times out) until the package is made public: GitHub, your profile, Packages, `docintake-decisions`,
Package settings, Change visibility. The extraction image (`docintake-extraction`) needs the same step once; see
[AI extraction key](#ai-extraction-key). Do the same for any image package a later phase renames.

## AI extraction key

`POST /api/documents/extract` needs a model provider API key, and **only the extraction service gets it**: the gateway's pod
has no key, no model setting and no OpenAI client. Create the key as a Secret, outside Helm, so it never enters the release
history, and point the chart at it (`extraction.existingSecret: docintake-llm`, already in the example values):

```sh
kubectl create namespace docintake --dry-run=client -o yaml | kubectl apply -f -
kubectl -n docintake create secret generic docintake-llm --from-literal=api-key="$OPENAI_API_KEY"
```

The Secret reference is `optional`: without it the pod still starts and extraction answers 503. Environment variables are
read at start, so after changing the key run `kubectl rollout restart deploy/docintake-extraction -n docintake`. The CI deploy job does
this for you: it stores the `OPENAI_API_KEY` GitHub secret in `docintake-llm` and passes a short hash of the key as
`extraction.keyChecksum`, which rolls the extraction pods when the key changes. Models are `extraction.visionModel` and
`textModel`; tune them with the evaluation job (Actions, Run workflow, tick the evaluation box).

**Values moved:** `api.extraction.*` is now `extraction.*` (and `extraction.replicas`, `extraction.resources`,
`extraction.startupProbe` and `extraction.extraEnv` are new). A values file that still sets `api.extraction` fails schema
validation, which is the point: it would otherwise silently leave extraction without a key.

The extraction pod's NetworkPolicy lets only the API pod reach it, and lets it reach only DNS, the collector and TCP 443
anywhere (the provider's addresses are not fixed). The API pod has no NetworkPolicy, so it is not blocked from the internet
either; the threat model's open item 4 is still open. The documents' pages leave the cluster for OpenAI; see the privacy note
in the main README.

### First deploy of the extraction image: make the package public

CI pushes the extraction image to `ghcr.io/<owner>/docintake-extraction`. **A package that is new to GitHub starts
private**, and the cluster pulls images without credentials, so on the first deploy the extraction pod sits in
`ImagePullBackOff` (and `helm upgrade --wait` times out) until the package is made public: GitHub, your profile, Packages,
`docintake-extraction`, Package settings, Change visibility. After that the pod pulls on its own, or re-run the deploy.
Do the same for any image package a later phase renames.

## API access key

`POST /api/documents/extract` spends OpenAI money, so it requires an `X-Api-Key` header; everything else stays open.
Without a configured key it answers 503 (it fails closed). Create the key as a Secret, outside Helm, like the OpenAI key
(`api.auth.existingSecret: docintake-api-key` is already in the example values):

```sh
kubectl -n docintake create secret generic docintake-api-key --from-literal=api-key="$(openssl rand -base64 32)"
```

The CI deploy job does this from the **`DOCINTAKE_API_KEY`** (the old `LAYA_API_KEY` still works until the new one is added) GitHub secret and passes a short hash of it as
`api.auth.keyChecksum`, so pods restart when it changes. Add the GitHub secret before the next push to `main`;
without it the deploy succeeds but extraction answers 503. Callers then send the same value:

```sh
curl -H "X-Api-Key: $DOCINTAKE_API_KEY" -F "file=@document.pdf" https://api.mehmood.us/docintake/api/documents/extract
```

**Rotate without downtime.** Add the new key to the Secret under a second entry, list both entries in the chart
(`api.auth.secretKeys: [api-key, api-key-next]`), deploy, move callers to the new key, then remove the old entry and
deploy again.

**Cap the spend.** The access key decides who may call; it does not limit how much they spend. Give this service's
OpenAI key its own OpenAI project and set a monthly budget there; that is the only limit that still holds if the access
key leaks.

## Malware scanning (ClamAV)

The chart runs a ClamAV pod (`malware.clamd`, on by default) and the API streams every upload to it before anything parses
the file; an infected upload gets 422. It is the largest pod in the release, so plan the node around it:

- **Memory.** ClamAV keeps its signature database in memory: about 1 GiB measured when idle, and its documentation advises
  3 to 4 GiB because a database reload can briefly double that. The chart's default node (1 vCPU, 2 GB) cannot hold it
  together with the API and the renderer. Resize the DigitalOcean node pool **before** merging: otherwise the ClamAV pod
  stays `Pending` or is evicted, `helm upgrade --wait` times out and the deploy job fails.
- **No requests or limits.** By choice, the pod has none (and its volumes have no size limit). It is therefore `BestEffort`:
  under memory pressure the kubelet evicts it first, and while it is down uploads answer 503 (`FailMode: closed`). To change
  that, add a `resources` block to `templates/clamav.yaml`.
- **Start-up.** The pod runs ClamAV's non-root entrypoint on an empty volume, so its first start downloads the signature
  database (a minute or two; the startup probe allows 15). The database is downloaded again whenever the pod is replaced,
  and `freshclam` keeps it current. The pod is the only one besides the telemetry collector with internet egress (HTTPS
  and DNS), and only the API can reach it (port 3310).
- **Failure mode.** `malware.clamd.failMode` is `closed` (refuse uploads while there is no verdict, 503 with `Retry-After`)
  or `open` (accept them unscanned, with a warning in the log). Readiness shows the scanner as `Degraded` while it does not
  answer; the API stays in rotation.
- **Switch it off** with `malware.clamd.enabled: false`, which sets `Security__Malware__Provider=none`.
- **File size.** clamd's default stream limit is 25 MB, above the 20 MB upload limit. If you raise
  `api.config.maxFileBytes` past that, raise clamd's `StreamMaxLength` too (mount a `clamd.conf` at `/etc/clamav`).

Check it after a deploy: `kubectl -n docintake get pods` shows `docintake-clamav` Ready, and uploading the
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

## Cutting over from the `laya` names

Until the cutover, the live cluster uses the old names: namespace and release `laya`, services `laya-api`, `laya-renderer`, ...,
Secrets `laya-api-key`, `laya-llm`, `laya-otel`, claim `laya-usage`, Gateway and load balancer `laya`, URL prefix `/laya`,
images `ghcr.io/<owner>/laya-sample-*`. The chart and CI now create everything under **`docintake`** (the document
service replaces `renderer`, images are `docintake-api`, `-document`, `-extraction`, `-decisions`). Because selector labels
and names are immutable, this is a **second release in a second namespace** that runs beside the old one, and then you move
traffic. The old stack is not touched by CI, so it keeps serving until you remove it.

Do these in order. Nothing here is automatic past step 2.

1. **Make room.** Two full stacks do not fit the node (ClamAV alone needs about 4 GB, and the decisions pod loads an
   850 MB model). Resize the DigitalOcean node pool to at least twice what runs now, until step 8, or accept downtime by
   scaling the old release to zero before step 2.
2. **Merge.** CI pushes the four new images, applies the cert-manager issuers (they now point at Gateway `docintake` in
   namespace `docintake`), creates the namespace and the three Secrets from the GitHub secrets (`DOCINTAKE_API_KEY`, or
   the old `LAYA_API_KEY` if you have not added it yet; `OPENAI_API_KEY`; `HONEYCOMB_API_KEY`) and installs release `docintake`.
3. **Make the four new GHCR packages public** (profile, Packages, each of `docintake-api`, `docintake-document`,
   `docintake-extraction`, `docintake-decisions`, Package settings, Change visibility). A package that is new to GitHub starts
   private, and the pods stay in `ImagePullBackOff` until it is public; the deploy job waits 10 minutes, so either do this at
   once or re-run the deploy job afterwards.
4. **Check the new stack before moving anything.** `kubectl -n docintake get pods` shows every pod Ready, and
   `kubectl get gateway docintake -n docintake` shows a new ADDRESS (a new load balancer, a new IP).
   Try it with the host header, which skips DNS:
   `curl -k --resolve api.mehmood.us:443:<new address> https://api.mehmood.us/docintake/health/ready`.
5. **Carry over the TLS certificate** so HTTPS works the moment DNS moves, instead of after Let's Encrypt has validated the
   new Gateway:
   ```sh
   kubectl get secret laya-tls -n laya -o yaml \
     | sed -e 's/namespace: laya/namespace: docintake/' -e 's/name: laya-tls/name: docintake-tls/' \
           -e '/resourceVersion:/d' -e '/uid:/d' -e '/creationTimestamp:/d' | kubectl apply -f -
   ```
   cert-manager sees a valid certificate for the host and keeps it; it renews it later from the new Gateway. Skip this step and
   there is a few minutes of HTTPS errors after step 6.
6. **Point DNS** (`api.mehmood.us`, A record) at the new address. Old callers keep working: the new route also answers the old
   prefix (`gateway.legacyPathPrefixes: [/laya]` in `values-prod.yaml`), so `https://api.mehmood.us/laya/...` and
   `.../docintake/...` reach the same API. Wait for the record's TTL to pass.
7. **Move the usage history**, after traffic has stopped reaching the old pod, so nothing is written in between:
   ```sh
   for f in usage.db usage.db-wal usage.db-shm; do
     kubectl -n laya exec deploy/laya-api -- cat /data/$f 2>/dev/null > /tmp/$f || true
   done
   POD=$(kubectl -n docintake get pod -l app.kubernetes.io/component=api -o name | head -1)
   for f in usage.db usage.db-wal usage.db-shm; do [ -s /tmp/$f ] && kubectl -n docintake cp /tmp/$f ${POD#pod/}:/data/$f; done
   kubectl -n docintake rollout restart deploy/docintake-api
   ```
   Then check `GET /api/usage/summary` shows the old totals. Until then the new database is empty; keep the old claim until you have.
8. **Remove the old stack** once step 7 checks out and the node pool can shrink:
   `helm uninstall laya -n laya`, then `kubectl delete namespace laya` (this also deletes `laya-usage`: do it last). Delete the
   old GHCR packages (`laya-sample-*`) and the `LAYA_API_KEY` GitHub secret whenever you like. Later, once nobody calls
   `/laya`, remove `legacyPathPrefixes` from `values-prod.yaml`.

**Rollback** before step 8: point DNS back at the old address. The old stack was never changed. The ClusterIssuer solver now
names the new Gateway, so a certificate **renewal** for the old one would fail until you re-apply the old issuer
(`kubectl apply` with `name: laya` / `namespace: laya`); certificates last 90 days, so this is only a concern if you stay on
the old stack for weeks.

**What breaks, for how long:** with steps 4 to 6 in this order, nothing breaks for callers except the short DNS propagation
window (both stacks answer, since both run). Anything that scraped the old Kubernetes names (dashboards on the `laya`
namespace, `kubectl` aliases) must be updated; telemetry `service.name` values did not change in this step.

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
- The renderer parses untrusted files with native code. A NetworkPolicy allows only the API to reach it and blocks all
  its egress; both pods run as UID 1654 with a read-only root filesystem, no capabilities and a default seccomp profile.
  The NetworkPolicy relies on the cluster's Cilium enforcing it, which DOKS does.

## Troubleshooting

| Symptom | Look at |
|---|---|
| Gateway has no ADDRESS | `kubectl describe gateway docintake -n docintake`; the load balancer can take a few minutes. |
| Certificate stays `False` | `kubectl describe certificate,order,challenge -n docintake`. The DNS A record must resolve to the Gateway, and port 80 must reach it. |
| Pods `Pending` | `kubectl describe pod -n docintake`: not enough memory on the node. Check `kubectl describe node`. |
| API pod `OOMKilled` or renderer restarts on big scans or big spreadsheets (the renderer now also loads Office files and every PDF's structure) | Raise `document.resources.limits.memory` (a five-page scan peaks around 550 MiB) and give the node more memory. |
| 502 from the API | The document service is down or was OOM-killed; `kubectl logs deploy/docintake-document -n docintake`. |
| 503 with `Retry-After` | Queues are full (one document at a time by default). Raise `api.config.maxQueuedAnalyses` or add capacity. |
| 504 | `api.config.requestTimeoutSeconds` elapsed; the route timeout (`gateway.requestTimeout`) must stay above it. |
