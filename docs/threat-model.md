# Threat model

Written 2026-10-11, against commit `6fe4128`. The service takes an uploaded document from a caller, scans and parses it,
sends its pages to OpenAI and returns structured fields. This page asks what could go wrong at each place where data
crosses a trust boundary, and says what already stops it and what does not. The diagram is
`.archify/current/laya-architecture.html`.

It is a desk review: the code, charts and CI were read, and nothing here was attacked or tested. A row marked
**covered** means a control exists in the code, not that it was proven to work. The garak red-team baseline in
[`redteam/README.md`](../redteam/README.md) is still empty, so no claim about prompt injection is measured.

**Update 2026-10-12:** the gateway no longer reads documents. Classification and preparation, with PdfPig and the Office
libraries, moved into the document service (the old renderer), so finding 2.3 below is now mitigated: those parsers run only
in the service that has no egress, and a test checks that the gateway's build contains none of them. The row is left as it
was written; the text after "Update" says what changed. This is a design and test result, not an attack: nothing was run
against the parsers. Finding 4.2 matters more now, because the document service sees every upload and still trusts any pod
that reaches it.

**Update 2026-10-12 (extraction service):** the model provider's key moved out of the gateway into the extraction service,
which parses nothing and is reachable only from the gateway. A compromised parser or gateway request handler no longer has
the key in its process or its environment, and a test checks that the gateway's code, build and Deployment never mention it.
The extraction service trusts any pod that reaches it (the same open item 4.2), and it has no authentication of its own.
The gateway's pod still has no NetworkPolicy (open item 4), but it no longer needs egress to the model provider.

**Update 2026-10-12 (decisions service):** Laya moved into its own service, so the gateway runs no model and carries no native
library at all (ONNX Runtime was the last one; a test checks its build). The decisions service is reachable only from the
gateway and may reach nothing but the telemetry collector, because its model is in the image. It has no authentication of its
own (the same open item 4.2). The feedback text it receives is not recorded in logs or usage.

**Update 2026-10-13 (slim gateway):** the rules for what counts as an instruction-like text are no longer in the gateway at all:
the document service finds them in what it prepares and the extraction service finds them in the answer, and the gateway only applies
the policy (`Security:OnFinding`). Where rows 2.5 and 3.1 name `RiskScanner`, read `RiskPolicy` for the decision and the services'
`TextRisks` and `AnswerRisks` for the detection. Nothing a request can do changed.

**Update 2026-10-14 (internal transport security):** the hops inside the cluster are now encrypted, and the services behind the
API authenticate their caller. The rows below are left as written; this is what changed (design and a local kind cluster, not the
live cluster, see [`internal-transport-security.md`](internal-transport-security.md) for what was verified):

| Row | Was | Now |
|---|---|---|
| 4.2 Spoofing: something other than the API calls the document, extraction (and scan) service | NetworkPolicy only; "no shared secret or mTLS" | **mitigated.** Dapr sidecars with mutual TLS; each callee's `Configuration` is deny-by-default and allows only the API's app id and only the operations it uses. A pod with another app id got 403 naming its SPIFFE id on kind, even with the NetworkPolicies removed. The plaintext port 8080 is no longer open to other pods (NetworkPolicy), and Dapr's 50002 refuses a client without a certificate |
| 4.1 Sideways movement | the API pod has no NetworkPolicy | **partial, unchanged.** Callees now accept only Dapr's port from the API. The API pod still has no NetworkPolicy: the Gateway's traffic arrives with Cilium's ingress identity, which a plain NetworkPolicy cannot select (a `CiliumNetworkPolicy` with `fromEntities: ingress` could). Any pod can reach its HTTPS port |
| 1.7 Eavesdropping, Gateway to the API pod | TLS ended at the Gateway; HTTP to the pod | **covered, not verified live.** The API serves HTTPS only with a certificate from a private CA; the Gateway checks it with a `BackendTLSPolicy`. Whether DOKS's Cilium enforces that policy is only known after a deploy |
| Boundary 3, API to ClamAV | raw clamd TCP over the cluster network | **covered.** The API talks to the scan service through Dapr; the scan service talks to clamd on the pod's loopback. clamd still binds all interfaces of its pod, and only the NetworkPolicy keeps other pods off 3310 (confirmed on kind) |
| Boundary 6, services to the collector | HTTP 4318 | **covered, not verified against Honeycomb.** TLS with the private CA's certificate, checked by the exporter. Client certificates are not used: callers are limited by the NetworkPolicy |
| 4.3 / new | n/a | **new risk.** The private CA's key is in the Helm release record and a Secret in the namespace; whoever can read Secrets there can mint certificates. Anyone who can edit Dapr `Configuration` resources or pod annotations in the namespace can change who may call whom |
| 4.5 Availability | ClamAV memory | **worse and better.** The decisions pod (2 to 3 GiB) is gone; Dapr adds about 240 MiB of control plane and sidecars (the API's peaks near 190 MiB under a 19 MB upload and was OOM-killed at 192 MiB). Untested on the live nodes |

The decisions service no longer exists, so its mentions in the updates above are history.

## What is protected

| Asset | Why it matters |
|---|---|
| Uploaded documents (loan applications: names, amounts, signatures) | Personal and financial data. Pages go to OpenAI and may be kept up to 30 days for abuse monitoring ([README](../README.md), Privacy) |
| The OpenAI key and the spend it allows | A leaked key or a flood of requests costs money |
| API keys (`X-Api-Key`) | They decide who may spend that money and read usage |
| The Honeycomb ingest key | Held only by the collector |
| Usage history (SQLite on a PVC) | Who called, which file names, cost |
| Extraction results | Downstream systems may trust them |
| Availability | Parsing, rendering and OCR are CPU and memory heavy |

## Trust boundaries

1. **Internet to gateway.** Callers are untrusted. Their documents are hostile until scanned.
2. **Gateway to API.** The API is the only public workload; the HTTP route sends everything under the prefix to it
   (`deploy/helm/docintake/templates/httproute.yaml:24-26`).
3. **API to ClamAV.** Documents go out over TCP; a verdict comes back.
4. **API to renderer.** The renderer runs native code (PDFium, ImageMagick) on untrusted bytes, so it is the most exposed
   component after the API itself.
5. **API to OpenAI.** Document content leaves the cluster; the model's answer comes back and is untrusted.
6. **API and renderer to collector to Honeycomb.** Telemetry leaves the cluster, and carries prompts and answers.
7. **CI to cluster.** A pipeline holds the kubeconfig and the secrets.

## Threats by boundary (STRIDE)

Status: **covered** (a control exists), **partial** (reduced but not closed), **open** (nothing stops it).

### 1. Internet to API

| # | Threat | What exists | Status |
|---|---|---|---|
| 1.1 | **Spoofing.** An unauthenticated caller uses extraction and spends OpenAI money | Keys checked first, in constant time, before the body is read; fails closed with 503 when no key is set outside Development (`Services/ApiKeyAuthentication.cs:70-128`, `Program.cs:340`) | covered |
| 1.2 | **Spoofing.** A key leaks and is used for ever | Keys are a list, so one can be rotated out; the usage history shows spend per key fingerprint. No expiry, no per-key quota | partial |
| 1.3 | **Elevation.** `/api/documents/analyze` and `/api/feedback/analyze` need no key. Analyze parses the upload, calls the renderer and runs OCR, so an anonymous caller reaches the same native-code surface as an authenticated one | The scan, size limit and concurrency limit still apply (`Program.cs:215-279`), but there is no `RequireApiKey` on them | **open** |
| 1.4 | **Information disclosure.** `/scalar` and `/openapi/v1.json` are public, and so are `/health` (check names and descriptions) | They describe the API but hold no data (`Program.cs:172-173,191`) | partial, low |
| 1.5 | **Denial of service.** Large or many uploads | 20 MB body limit, 20 page limit, concurrency limits with a bounded queue, per-request timeout, 503 when full (`Program.cs:121-140,277`; `DocumentAnalysisOptions.cs:14,45,91-95`). Limits are per process, not per caller, and there is no per-IP limit. The API key is the only per-caller control, and it exists only on extract | partial |
| 1.6 | **Repudiation.** A caller denies an upload | Every request is traced and every model call recorded with the key fingerprint and file name (`Services/Usage/UsageStore.cs`). Logs are not tamper-proof | partial |
| 1.7 | **Tampering and eavesdropping.** Traffic read or changed on the way in | The Gateway terminates TLS with a cert-manager certificate (`templates/gateway.yaml:36-41`) | covered |
| 1.8 | **Information disclosure.** A key supplied twice or in the wrong header | Exactly one header value is accepted | covered |

### 2. Hostile documents (API process)

| # | Threat | What exists | Status |
|---|---|---|---|
| 2.1 | **Malware** in an upload | Scanned with ClamAV before any parser; infected gets 422; no verdict gets 503 (fail closed) (`DocumentPipeline.cs:50-56,98-125`, `Security/SecurityOptions.cs:35-37`). ClamAV only knows known signatures | covered, with the limit that unknown malware passes |
| 2.2 | **Scanner switched off.** `Provider: none` in `appsettings.Development.json`, or `FailMode: open` | Compose and Helm set clamd on; the default is clamd. A local `dotnet run` does not scan (found today when EICAR returned 415, not 422). Nothing stops a production deploy from setting `none` | partial |
| 2.3 | **Elevation.** A crafted PDF, spreadsheet or Office file exploits a parser (PdfPig, ClosedXML, OpenXML) and runs code in the API process, which holds the OpenAI key and API keys | Parsers are managed code. The pod is non-root, read-only root filesystem, capabilities dropped, no service account token (`templates/_helpers.tpl:64-75`, `api-deployment.yaml:36`). The API pod can still reach OpenAI and the collector. **Update 2026-10-12:** PdfPig, ClosedXML and OpenXML now run in the document service, not the API; the gateway has none of them (`tests/DocIntake.Tests/ArchitectureTests.cs`) | mitigated by the split; the document service has no authentication of its own (4.2) |
| 2.2b | **Elevation.** The same, against PDFium or ImageMagick | These run only in the renderer, which has no egress except the collector and DNS, accepts calls only from the API pod and has the same container hardening (`templates/networkpolicy.yaml:1-47`, `document-deployment.yaml:22-63`). The renderer has no authentication of its own, so any pod that reaches it is trusted (see 4.2) | partial |
| 2.4 | **Denial of service.** Decompression bombs, huge page sizes, deep nesting | Page cap, bitmap cap, per-request timeout, spreadsheet row cap (`DocumentAnalysisOptions.cs`, `LocalPageRasterizer.cs:135-141`). No memory limit is set on the API pod by default | partial |
| 2.5 | **Active content.** Macros, embedded executables, JavaScript, hidden text | Detected from the file's structure before rendering and refused with 422 by default (`Security/RiskScanner.cs:29-35`, `Security/OfficeInspector.cs`, `Security/ExecutableSniffer.cs`) | covered |
| 2.6 | **File type confusion.** A file labelled one type, parsed as another | The type comes from the bytes, not the name or content type; anything else is 415 (`DocumentClassifier.cs`) | covered |

### 3. Prompt injection (API to OpenAI and back)

| # | Threat | What exists | Status |
|---|---|---|---|
| 3.1 | **Tampering.** Text in the document tells the model to change fields, ignore its rules or leak its prompt | Pattern checks on the text the model will read (OCR included) and on its answer, refused with 422 by default (`Security/RiskScanner.cs:37-110`, `Security/InjectionPatterns.cs`); the output is a fixed JSON schema. The patterns are a list, so a new phrasing or a non-English attack passes. **No measured pass rate yet** | partial |
| 3.2 | **Tampering.** The injection shows up as a plausible wrong value (a changed amount), not as instruction text | No control can see this: the answer is the model's reading of the page. A human or a downstream check must verify values that matter | **open** |
| 3.3 | **Information disclosure.** Document content goes to a third party | Documented, `store` is false, and a dedicated OpenAI project with a budget is advised (README, Privacy). Retention up to 30 days unless Zero Data Retention. Needs sign-off before borrower data | partial |
| 3.4 | **Denial of service and spend.** A document that makes the model produce long output or many retries | `MaxOutputTokens` 4000, `MaxRetries` 2, `MaxImagePages` 10, extraction concurrency 4 with a queue of 10 (`ExtractionOptions.cs:28-50`). The hard stop on spend is the OpenAI project budget, not this service | partial |
| 3.5 | **Information disclosure.** The system prompt or schema leaks | Neither is secret by design; the model has no tools and no other data to leak | covered |

### 4. Inside the cluster

| # | Threat | What exists | Status |
|---|---|---|---|
| 4.1 | **Elevation.** A compromised pod moves sideways | Renderer and ClamAV have ingress and egress policies; ClamAV may reach only DNS and port 443 for signature updates (`templates/clamav.yaml:93-125`). **The API pod has no NetworkPolicy**, so any pod in the cluster can call it, and it can call anything | partial |
| 4.2 | **Spoofing.** Something other than the API calls the renderer | Only the API pod may reach it, enforced by the NetworkPolicy, which depends on the CNI enforcing it (Cilium here). No shared secret or mTLS | partial |
| 4.3 | **Information disclosure.** Secrets in the pod | Keys come from Kubernetes Secrets by `existingSecret` (`api-deployment.yaml:77-101`), not from values or the image. Anyone who can read Secrets or exec into the pod gets them | partial |
| 4.4 | **Information disclosure.** The usage database holds API key fingerprints and uploaded file names | Fingerprints are a short hash prefix; usage reads are scoped to the caller's own fingerprint unless the key is an admin key (`Services/Usage/UsageEndpoints.cs:145-148`). The file names are personal data if they contain names. The PVC is kept on uninstall and has no encryption setting of its own | partial |
| 4.5 | **Denial of service.** ClamAV's memory (about 1 GiB, more during a signature reload) starves the node | Deliberately no limits (project decision). A reload on a small node can cause an eviction and then 503s while the scanner is down | partial, accepted |

### 5. Telemetry (API and renderer to Honeycomb)

| # | Threat | What exists | Status |
|---|---|---|---|
| 5.1 | **Information disclosure.** Prompts, document text and extraction results are recorded on spans, up to 32 KB each | The code comment calls this a demonstrator choice (`Extraction/DocumentExtractor.cs:157-181`, `Telemetry.cs:13,23`). With real borrower data this puts personal data into Honeycomb, with Honeycomb's retention and access, and into any test-environment export | **open** |
| 5.2 | **Information disclosure.** The Honeycomb key leaks | Only the collector holds it; the apps send to the in-cluster collector (`templates/collector-config.yaml:24`) | covered |
| 5.3 | **Tampering.** Someone injects false telemetry | The collector accepts OTLP only from the API and renderer pods, and sends only to HTTPS and DNS (`templates/collector.yaml:79-107`). False data needs a compromised API or renderer pod | covered |
| 5.4 | **Log forging and leakage.** Document text in logs | Findings log kinds and counts, never the text; the scan signature stays in the log and trace, not the response (`DocumentPipeline.cs:103-112`) | covered |

### 6. Build and deploy

| # | Threat | What exists | Status |
|---|---|---|---|
| 6.1 | **Tampering.** A vulnerable or malicious package or base image | Licence check in CI (`.github/workflows/ci.yml:51`), central version pinning and locked restores, a permissive-licence policy. **No vulnerability scan** of NuGet packages or images, no Dependabot or equivalent, no image pinning by digest. PDFium and ImageMagick are the highest-risk native dependencies | **open** |
| 6.2 | **Elevation.** A pull request from a fork reaches secrets | Workflow default is `contents: read` (`ci.yml:22-23`); the paid jobs run only by manual dispatch and use secrets. Deploy runs on a push to main only | covered |
| 6.3 | **Elevation.** The deploy credential is too broad | `DO_KUBECONFIG` is a cluster credential in a GitHub secret. Its scope was not checked | unverified |
| 6.4 | **Information disclosure.** Secrets in the image or repo | Keys are read at run time; `.env` files are not committed (checked: nothing sensitive in the commit) | covered |

## Open items, in the order I would fix them

1. **Put `RequireApiKey` on `/api/documents/analyze`** (1.3), or switch it off in production. It is the one place an
   anonymous caller reaches the parsers, the renderer and OCR. This is a one-line change, but it changes who can call the
   endpoint, so it needs your decision.
2. **Stop recording document text in telemetry before real data** (5.1): turn the prompt and result tags off by
   default, or redact them, and keep them behind a setting for debugging.
3. **Add vulnerability scanning to CI** (6.1): a package audit (`dotnet list package --vulnerable`) and an image scan,
   plus automated dependency updates. The renderer's native libraries matter most.
4. **Add a NetworkPolicy for the API pod** (4.1): ingress from the gateway only; egress to the renderer, ClamAV, the
   collector, DNS and OpenAI.
5. **Run the garak baseline** (3.1) and write the result into `redteam/README.md`, so the injection claim is a number.
6. **Make a production deploy fail if scanning is off** (2.2): refuse `Provider: none` and `FailMode: open` when the
   environment is not Development.
7. Decide on 3.2 (a wrong but plausible value): that is a process question, such as human review of key fields, and
   not something code here can close.

## What this does not cover

- No penetration test, dynamic scan or fuzzing was done.
- The cluster itself (node hardening, who has `kubectl`, Secret encryption at rest, the DigitalOcean account) is out of
  scope; the Helm chart's assumptions about it are listed under 4.
- Compliance (data residency, retention, sign-off for borrower data) is noted at 3.3 but not assessed.
- The extraction agents behind `/api/documents/analyze` are stubs, so there is no downstream-agent threat model yet.
- Only the production path was modelled. Local runs (`dotnet run`, Compose) differ: scanning and keys are optional in
  Development.

## Verified and not verified

Verified by reading: the controls cited above exist at those lines at commit `6fe4128`. Not verified: that any control
works against a real attack, that the NetworkPolicies are enforced on the cluster, that the Secrets and the kubeconfig
are scoped as assumed, and the scan and injection pass rates.

## Before relying on this

- [ ] Decide on 1.3 (key on analyze) and 5.1 (telemetry content) before any real borrower data is sent.
- [ ] Check the scope of `DO_KUBECONFIG` (6.3).
- [ ] Confirm the cluster's CNI enforces NetworkPolicy (4.1, 4.2).
- [ ] Run the red-team baseline (3.1).
