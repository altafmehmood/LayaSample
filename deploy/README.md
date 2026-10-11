# Deploying to DigitalOcean Kubernetes

The Helm chart in [`helm/laya-sample`](helm/laya-sample) runs the API and the isolated renderer and publishes the API
through the Gateway API.

```
internet -> DigitalOcean load balancer -> Gateway (cilium, TLS) -> HTTPRoute -> api Service -> api pod
                                                                                   |
                                                       renderer Service <----------+  (NetworkPolicy: only the API may call it)
```

The defaults fit **one node with 1 vCPU and 2 GB of memory**: one replica of each, one document analysed at a time,
short queues, no CPU limits, and a rolling update that replaces the pod in place (a second copy would not fit).
Expect slow OCR and brief downtime on upgrades. For real traffic use a bigger node and raise the values under
`api.config`, `renderer.config` and `*.resources`.

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
helm upgrade --install laya deploy/helm/laya-sample -n laya --create-namespace -f my-values.yaml
```

Then create the DNS record. The load balancer address appears once DigitalOcean has provisioned it (a minute or two):

```sh
kubectl get gateway laya -n laya          # ADDRESS column
```

Point an **A record** for your hostname at it. cert-manager can only issue the certificate once the name resolves:

```sh
kubectl get certificate -n laya -w        # READY becomes True
```

Start with `letsencrypt-staging` (the example values do). When the staging certificate is issued, switch:

```sh
helm upgrade laya deploy/helm/laya-sample -n laya -f my-values.yaml --set gateway.tls.clusterIssuer=letsencrypt-prod
```

(Delete the old certificate Secret `laya-tls` first if cert-manager keeps the staging one: `kubectl delete secret laya-tls -n laya`.)

## Check it

```sh
curl https://<hostname>/health/ready             # {"status":"Healthy", ...}
open https://<hostname>/scalar/v1                # API reference
curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-loan-application-scan.pdf" \
  "https://<hostname>/api/documents/analyze?dispatch=false&includeData=false"
```

The first start is slow on a small node: the API image is 2 GB and it loads an 840 MB model. The startup probes allow
ten minutes.

## AI extraction key

`POST /api/documents/extract` needs a model provider API key. Create it as a Secret, outside Helm, so the key never
enters the release history, and point the chart at it (`api.extraction.existingSecret: laya-llm`, already in the example values):

```sh
kubectl create namespace laya --dry-run=client -o yaml | kubectl apply -f -
kubectl -n laya create secret generic laya-llm --from-literal=api-key="$OPENAI_API_KEY"
```

The Secret reference is `optional`: without it the pod still starts and the endpoint answers 503. Environment
variables are read at start, so after changing the key run `kubectl rollout restart deploy/laya-api -n laya`. The CI
deploy job does this for you: it stores the `OPENAI_API_KEY` GitHub secret in `laya-llm` and passes a short hash of the
key as `api.extraction.keyChecksum`, which rolls the pods when the key changes. Models are `api.extraction.visionModel`
and `textModel`; tune them with the evaluation job (Actions, Run workflow, tick the evaluation box).

The API pod needs outbound HTTPS to `api.openai.com`; there is no NetworkPolicy on it, so nothing blocks that. The
documents' pages leave the cluster for OpenAI; see the privacy note in the main README.

## API access key

`POST /api/documents/extract` spends OpenAI money, so it requires an `X-Api-Key` header; everything else stays open.
Without a configured key it answers 503 (it fails closed). Create the key as a Secret, outside Helm, like the OpenAI key
(`api.auth.existingSecret: laya-api-key` is already in the example values):

```sh
kubectl -n laya create secret generic laya-api-key --from-literal=api-key="$(openssl rand -base64 32)"
```

The CI deploy job does this from the **`LAYA_API_KEY`** GitHub secret and passes a short hash of it as
`api.auth.keyChecksum`, so pods restart when it changes. Add the GitHub secret before the next push to `main`;
without it the deploy succeeds but extraction answers 503. Callers then send the same value:

```sh
curl -H "X-Api-Key: $LAYA_API_KEY" -F "file=@document.pdf" https://api.mehmood.us/laya/api/documents/extract
```

**Rotate without downtime.** Add the new key to the Secret under a second entry, list both entries in the chart
(`api.auth.secretKeys: [api-key, api-key-next]`), deploy, move callers to the new key, then remove the old entry and
deploy again.

**Cap the spend.** The access key decides who may call; it does not limit how much they spend. Give this service's
OpenAI key its own OpenAI project and set a monthly budget there; that is the only limit that still holds if the access
key leaks.

## Upgrade, roll back, remove

```sh
helm upgrade laya deploy/helm/laya-sample -n laya -f my-values.yaml --set image.tag=sha-<new commit>
helm rollback laya -n laya
helm uninstall laya -n laya
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
| Gateway has no ADDRESS | `kubectl describe gateway laya -n laya`; the load balancer can take a few minutes. |
| Certificate stays `False` | `kubectl describe certificate,order,challenge -n laya`. The DNS A record must resolve to the Gateway, and port 80 must reach it. |
| Pods `Pending` | `kubectl describe pod -n laya`: not enough memory on the node. Check `kubectl describe node`. |
| API pod `OOMKilled` or renderer restarts on big scans | Raise `renderer.resources.limits.memory` (a five-page scan peaks around 550 MiB) and give the node more memory. |
| 502 from the API | The renderer is down or was OOM-killed; `kubectl logs deploy/laya-renderer -n laya`. |
| 503 with `Retry-After` | Queues are full (one document at a time by default). Raise `api.config.maxQueuedAnalyses` or add capacity. |
| 504 | `api.config.requestTimeoutSeconds` elapsed; the route timeout (`gateway.requestTimeout`) must stay above it. |
