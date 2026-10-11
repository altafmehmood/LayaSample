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

## Upgrade, roll back, remove

```sh
helm upgrade laya deploy/helm/laya-sample -n laya -f my-values.yaml --set image.tag=sha-<new commit>
helm rollback laya -n laya
helm uninstall laya -n laya
```

## Security notes

- **The API has no authentication.** Anyone who knows the hostname can submit documents and read `/scalar/v1`; only the
  built-in rate limiting and request limits apply. Add API-key authentication or restrict source addresses before
  sharing the name.
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
