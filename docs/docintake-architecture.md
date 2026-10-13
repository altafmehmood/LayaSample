# DocIntake architecture: gateway, services, logs and telemetry

Started 2026-10-11. This page tracks a plan delivered in phases: a rename, then splitting the API into a gateway and
three services, then better logs and OpenTelemetry. Each phase adds its section here when it lands. The README keeps
the short versions.

## Why

An architectural review of the code as of commit `174247c` found these problems:

- **The production API ships native parsers.** The API references the native rendering library, so its image carries
  PDFium, ImageMagick, Skia and ONNX even though rendering happens in the renderer.
- **One process holds everything risky.** The API parses untrusted bytes in managed code (PdfPig, ClosedXML, OpenXml),
  holds the model-provider key, has internet access and loads a local language model.
- **HTTP leaks into the core.** HTTP status codes live inside domain exceptions and services. `Program.cs` mixes wiring,
  endpoint handlers and failure mapping.
- **Logs are thin.** No EventIds, no scopes, and no trace ids in console output.
- **Telemetry data has gaps.**
  - Duration histograms use the SDK's default buckets, which suit milliseconds, not seconds.
  - Metric keys are bare (`kind`, `model`).
  - Attributes like `page_contents` are joined strings that can't be grouped or filtered.
  - Classify spans don't record their own failures.

The project had also outgrown its name. "Laya" is the local decisions model behind feedback analysis, and it is now one
capability among several: OCR, classification, extraction, risk checks and agents.

Target, in later phases:

| Service | Role |
|---|---|
| `docintake-gateway` | Auth, rate limits, malware scan, risk policy, orchestration, usage history |
| `docintake-document` | Classify, prepare, render and OCR. No internet access. |
| `docintake-extraction` | The model agents. The only service with internet access and the model key. |
| `docintake-decisions` | The Laya model |

## Phase 7: deployment cutover to the `docintake` names (2026-10-13)

**Why.** Phases 1 to 6 left the live cluster on the old names (namespace and release `laya`, `laya-*` Secrets and claim, the
`laya-sample-*` images, the `/laya` path, a component called `renderer`), because Kubernetes selector labels and the names
of a running release are immutable. The user chose the full cutover, so the repository now creates everything under
`docintake`, and moving the live traffic is a documented manual runbook.

### What it does

- **Renames in the chart, CI, Compose and Dockerfile** (no application code changed):

  | Before | After |
  |---|---|
  | namespace and release `laya`, `nameOverride: laya-sample` | namespace and release `docintake` (no overrides in `values-prod.yaml`) |
  | `laya-api`, `laya-renderer`, `laya-extraction`, `laya-decisions`, `laya-clamav`, `laya-otel-collector` | `docintake-api`, `docintake-document`, `docintake-extraction`, `docintake-decisions`, `docintake-clamav`, `docintake-otel-collector` |
  | Secrets `laya-api-key`, `laya-llm`, `laya-otel`; claim `laya-usage` | `docintake-api-key`, `docintake-llm`, `docintake-otel`; `docintake-usage` |
  | Gateway and load balancer `laya`; certificate Secret `laya-tls` | `docintake`; `docintake-tls` |
  | values key `renderer`, templates `renderer-*.yaml`, Dockerfile target and Compose service `renderer` | `document`, `document-*.yaml`, `document` |
  | images `ghcr.io/<owner>/laya-sample-{api,renderer,extraction,decisions}` | `ghcr.io/<owner>/docintake-{api,document,extraction,decisions}` |
  | public path `/laya` | `/docintake`; `/laya` still routed (`gateway.legacyPathPrefixes`) |
  | GitHub secret `LAYA_API_KEY` | `DOCINTAKE_API_KEY` (CI falls back to the old one) |

- **A second release beside the old one.** The deploy job installs release `docintake` in namespace `docintake` and does not
  touch the `laya` release, so the old stack keeps serving until it is removed by hand.
- **`gateway.legacyPathPrefixes`** (chart value, schema-validated): extra path prefixes on the same HTTPRoute rule, so callers
  on `https://api.mehmood.us/laya/...` keep working after DNS moves.
- **The cutover runbook** in [deploy/README.md](../deploy/README.md#cutting-over-from-the-laya-names): make room on the node,
  merge, make four GHCR packages public, check the new stack with `curl --resolve`, carry over the TLS Secret, move DNS, copy the
  usage database, remove the old stack. Rollback is pointing DNS back.

Key files: `deploy/helm/docintake/**`, `deploy/values-prod.yaml`, `deploy/cluster-issuer.yaml`, `.github/workflows/ci.yml`,
`Dockerfile`, `docker-compose.yml`.

### Settings

`gateway.legacyPathPrefixes` (default empty; `[/laya]` in `values-prod.yaml`). `gateway.pathPrefix` is now `/docintake` in prod.

### How to try it

    helm lint deploy/helm/docintake -f deploy/helm/docintake/ci/values.yaml
    helm template docintake deploy/helm/docintake -f deploy/values-prod.yaml --set image.tag=sha-abc1234 | grep -n 'name: docintake\|value: "/'
    docker compose config -q

### What it does not do

- It does not move traffic, the certificate or the usage history for you; those are runbook steps.
- It does not rename telemetry: `service.name` is still `docintake-gateway` and so on, and the `renderer.*` span names are
  Phase 9.
- It does not rename the GitHub repository (the user's job) or delete the old `laya` namespace, GHCR packages or secret.
- Two full stacks do not fit one small node; the runbook starts with resizing the node pool.

### Tests

No new test. The 485 existing tests pass unchanged (6 opt-in skipped); the architecture tests that read the chart (the model key
is not in the gateway's Deployment) still pass against the renamed templates.

### Verified, and not

- Verified: `dotnet build`, the full test run, `helm lint` for the three CI values files, `helm template` with
  `values-prod.yaml` (names, both path prefixes, the PVC and Gateway names), `docker compose config`.
- **Not verified:** `kubeconform` (not installed here; CI runs it), the Docker image builds under the new target names, a real
  deploy, the certificate copy, the usage-database copy, and DNS. The runbook commands were written, not run.

### Before deploying

- [ ] Resize the node pool (or accept downtime by scaling `laya` to zero first).
- [ ] Add the `DOCINTAKE_API_KEY` GitHub secret (optional; the old name is used until then).
- [ ] After the first push, make the four `docintake-*` GHCR packages public, or the pods stay in `ImagePullBackOff`.
- [ ] Update any Honeycomb trigger, board or script that mentions the `laya` namespace, `laya-*` pod names or `/laya`.
- [ ] Follow the runbook; remove the old stack only after the usage totals match.

## Phase 6: the slim gateway (2026-10-13)

**What it does.** No new service and no deployment change: this phase tidies the gateway so that what it does is easy to see.
`Program.cs` went from 378 lines to **83**, all wiring. The endpoints, the upload checks, the rate limits, the OpenAPI setup and
the service clients each have their own file, the gateway answers failures in one place that is a real exception handler, and it
no longer looks for findings: the services find, the gateway decides.

**What moved where.**

| New file (`DocIntake.Api`) | What it holds |
|---|---|
| `Endpoints/DocumentEndpoints.cs`, `FeedbackEndpoints.cs`, `HealthEndpoints.cs` | The handlers and their OpenAPI metadata. A handler does the work and throws when it fails. `UsageEndpoints` was already its own file |
| `Endpoints/UploadFilters.cs` | `UploadValidationFilter` (a file is there: 400; not too big: 413; then tags the span, records the queue wait and starts the request's `DocumentRequest`) and `RequireExtractionFilter` (asks the extraction service first) |
| `RateLimiting.cs`, `OpenApiSetup.cs`, `ServiceClients.cs` | The rate-limit policies, the OpenAPI document, and the options, HTTP client, typed client and readiness check for each of the three services |
| `Services/Documents/DocumentRequest.cs` | What the failure handler needs to know about an upload that has started |
| `Services/Security/RiskPolicy.cs` | Was `RiskScanner`: records findings and decides, never detects |

**Failures are answered by an exception handler.** `ProblemMapping` now implements `IExceptionHandler` (the plan from Phase 2, which
had deferred it). Endpoints and filters throw; the handler maps the failure to the status, `Retry-After`, the outcome and kind on the
span, the log line and, for an upload, the document metric. It knows an upload's size and kind from the `DocumentRequest` the upload
filter put on the request; any other failure (feedback, the extraction pre-check) is answered without them. Anything it does not know
(a bug, a cancelled request) is left to the default handler, so a real 500 is still logged as one.

**The services find, the gateway decides.** Finding instruction-like text in what the model will read moved from the gateway to the
document service (`TextRisks`), next to where the structure findings already were. `POST /documents/prepare` now answers a
`PrepareResponse(Document, Risks)`, the same shape as `ClassifyResponse`. The gateway's `RiskPolicy` has three methods,
`EnforceStructure`, `EnforceText` and `EnforceAnswer`, each taking findings; it uses no detection rule, and a test checks that
`InjectionPatterns` is not referenced anywhere in the gateway.

**Behaviour changes.**
- **A rolling deploy needs the gateway and the document service replaced together.** `/documents/prepare` changed shape (a bare
  document before, `{document, risks}` now), so an old gateway reading a new service, or a new gateway reading an old one, gets an
  answer it cannot use and returns a 502 until both are on the new version. The same `helm upgrade` replaces both, so expect a short
  burst of 502s on document requests during the rollout.
- A feedback failure's outcome on the span and in the log is **`feedback_failed`**, not `extraction_failed`. That label was written
  for failed model calls and the decisions service reused the same failure kinds in Phase 5; a real run of this phase showed it.
- A failure of the extraction pre-check (the service unreachable) is no longer counted in the document metric
  `intake.document.analyzed` and has no document kind, because no document was involved. The status, `Retry-After` and text are unchanged.
- The order of the checks is unchanged: an unconfigured extraction answers 503 **before** the upload is validated (a request with no
  file at all gets 503, not 400), and a configured one validates before any work.
- Nothing else a caller or an operator sees changes: same routes, statuses, bodies, headers, log levels (a bad file is Information, a
  failing provider Warning) and span attributes. I checked the log levels against a real run: the exception middleware does **not**
  log handled failures as errors.

**Deviations from the plan.**
- The plan's Phase 6 also listed the gateway's readiness as `Degraded` when a dependency is down. That was already done, one service
  at a time, in Phases 3 to 5.
- `DocumentPipeline` still takes an `IFormFile`; it was not changed to a plain upload type.

**Key files.** `src/DocIntake.Api/Program.cs`, `Endpoints/*`, `Services/ProblemMapping.cs`, `Services/Security/RiskPolicy.cs`;
`src/DocIntake.DocumentService/TextRisks.cs`; `src/DocIntake.Contracts/DocumentModels.cs` (`PrepareResponse`).

**How to try it.** Nothing new to run: `dotnet run` the services as before. A PDF whose attachment says "ignore all previous
instructions" is refused with 422 `document rejected: it contains instructions addressed to an AI model` by both
`/api/documents/analyze` and `/extract` (with `Security:OnFinding=flag` it is reported in `risks` instead).

**What it does not do.** It does not change what any endpoint accepts or answers, and it adds no deployment.

**Tests.** 485 tests, 479 passed and 6 skipped (the opt-in ones from Phase 1). Every existing endpoint test passes through the new
filters and handler unchanged. New or changed:
- `ProblemMappingTests` (40): the handler answers what it knows and leaves a bug and a cancellation to the default; an upload gets
  its size and kind in the log and a non-document request does not; **a feedback failure is `feedback_failed`**, with the test
  mimicking the middleware (no endpoint on the context, the original on the exception feature) after the first version of the fix
  passed its unit test and failed in a real run.
- `DocumentServiceTests`: findings about the text arrive with the prepared document, are not in its JSON, and are not acted on by the service.
- `GatewayExtractionTests`: an unconfigured extraction is 503 before validation (no file at all), a configured one is 400 and nothing is called.
- `RiskTests`: detection is tested on `TextRisks`, the decision on `RiskPolicy`.
- `ArchitectureTests`: `Program.cs` has no endpoints, limiter or OpenAPI setup and is under 100 lines; nothing under `Endpoints` has a
  `catch` clause (I planted one and the test failed); the gateway never references `InjectionPatterns`.

### Verified (2026-10-13)

- `dotnet build`, `dotnet test` (above), the licence check (no package changed).
- **The real stack as separate processes** (document, extraction and gateway; the extraction service pointed at a local fake of the
  provider): a fax PDF extracts (200); no file is 400 `file is required`; a PDF with an injection phrase in an attachment is 422 on
  both endpoints; a text file is 415; with the decisions service down feedback is 503 with `Retry-After`; the provider was asked once,
  only for the fax PDF.
- The gateway and document service images build (377 MB and 538 MB, unchanged in size).
- **Log levels:** no Error or Critical line for any of those; a bad file logs at Information and the unreachable service at Warning.
- **Honeycomb** (the gateway's spans): the hostile PDF is `422 / rejected / TextPdf`, the text file `415 / rejected / unclassified`,
  and the feedback failure `feedback_failed`.

### Not verified

- A deploy to Kubernetes, and the rolling-deploy burst of 502s described above.
- `docker compose up` (no compose or Dockerfile change was made; the two affected images were built, below).

### Before deploying (Phase 6)

- [ ] The gateway and the document service change their wire contract together: expect a short burst of 502s on document requests
      during the rollout (the same `helm upgrade` replaces both).
- [ ] A trigger or board on `intake.outcome` = `extraction_failed` for `/api/feedback/analyze` will see `feedback_failed` instead.
- [ ] Nothing to do with images, packages, secrets or values.

## Phase 5: the decisions service (2026-10-12)

**What it does.** Laya, the local model that scores hotel-guest feedback, moved out of the gateway into
`DocIntake.DecisionsService`. The gateway now **loads no model and carries no native library at all** (ONNX Runtime was the
last one), so it is ready in seconds instead of waiting for an 850 MB model. `POST /api/feedback/analyze` keeps its validation
and its answers.

**How it works.** The gateway checks the text at the edge (required, at most `FeedbackLimits.MaxTextLength` = 4000 characters:
400 otherwise, without calling anything) and calls the service, `POST /decisions/feedback`. The service checks the text again,
answers `503` with the kind `DependencyUnavailable` while the model loads (`model is still loading`) or after it failed for good
(`model failed to load`), and otherwise returns the same `AnalyzeResponse` as before. The gateway passes the 503's text
through, so a client sees exactly the answers it saw when Laya was in-process. A service that cannot be reached is a `503` with
`Retry-After: 10` and the text `the decisions service is unreachable; retry later`; one that dies on the request is a `502`.

**What moved where.**

| Now in `DocIntake.DecisionsService` | Still in `DocIntake.Api` (gateway) | Shared |
|---|---|---|
| `DissatisfactionAnalyzer`, `ModelWarmup` (with a new `IModelStatus` seam), the `Laya` settings, `DecisionsMetrics`, the `ElBruno.LocalLLMs.Decisions` package | `DecisionsServiceClient`, `DecisionsGatewayOptions`, the feedback endpoint's validation, `ProblemMapping.ToServiceResult` (the answer for a failure that is not about a document) | `Contracts`: `FeedbackLimits` |

**Settings.**

| Setting | Before | Now |
|---|---|---|
| `Laya:*` (`ModelPath`, `CacheDirectory`, `ModelRepository`, `IntraOpNumThreads`, `DecisionThreshold`), and `Laya__ModelPath` in the image | gateway | **Decisions service** (the Docker `decisions` target sets `Laya__ModelPath=/models/laya`) |
| `Decisions:ServiceUrl` | none | **Gateway.** Optional: empty means feedback analysis answers `503` "not configured" and the gateway still starts (an empty environment variable is fine; a non-http value refuses to start) |

**Deployment, pulled forward again.** The same reason as Phase 4: merging deploys. `Dockerfile` has a `decisions` target that
carries the model (and the `api` target no longer does); `docker-compose.yml` has a `decisions` service on the **internal**
network (no route out: the model is in the image); Helm has `decisions-deployment.yaml`, `decisions-service.yaml` and a
NetworkPolicy (ingress from the API only; egress only to the collector and DNS, like the renderer); the collector accepts its
telemetry; CI builds, pushes and checks its rollout (15 minutes). The API's startup probe shrank from ten minutes to two,
because the gateway no longer loads a model; the decisions pod keeps the ten-minute one.

**Measured.** The gateway image is **377 MB** (the deploy README said about 2 GB while it carried the model) and its only
native library is SQLite's, for the usage history; the decisions image is 2.03 GB. As containers, the gateway was live in about
1 second. In a read-only, no-capability, no-network decisions container the real model loaded in **1.5 s** and answered at +5 s.

**Behaviour changes.**
- **The gateway's readiness is no longer 503 while Laya loads.** It is `200` `Degraded`, and `decisions-service` says why. In
  Kubernetes the gateway takes traffic sooner, and feedback requests during the model load get the same 503 as before.
- **Feedback analysis needs the decisions service running**: a fourth process locally (README quick start).
- The gateway's `laya` health check is gone; the decisions service has it, and the gateway's `decisions-service` check is `Degraded`
  when the service is unreachable or still loading, and `Healthy` ("not configured") with no URL.
- The model-load metric (`intake.model.load.duration`) moves to the meter `DocIntake.Decisions`, and the `intake.model.load` span
  and the feedback request's spans arrive in the dataset **`docintake-decisions`**.
- The gateway's startup log names the decisions service's address.
- An unreachable service answers feedback with a `Retry-After`, which "still loading" never had.
- **Compose:** the gateway's `mem_limit` went from 3g to 2g. It is a guess, not a measurement.

**Deviations from the plan.**
- **Deployment pulled forward** (above), so Phase 7 has less to do.
- `ModelWarmup` gained the `IModelStatus` interface (and lost its `Ready` property): it is the seam that lets tests say "ready"
  without loading an 850 MB model.

**Key files.**
- `src/DocIntake.DecisionsService/Program.cs` (the endpoint), `ModelWarmup.cs`, `DissatisfactionAnalyzer.cs`
- `src/DocIntake.Api/Services/Decisions/DecisionsServiceClient.cs`, `DecisionsGatewayOptions.cs`; `HealthChecks.cs`
- `deploy/helm/docintake/templates/decisions-*.yaml`, `networkpolicy.yaml`; `Dockerfile` (the `decisions` target)

**How to try it.** Free: the model is open-weight and runs on your machine.

    dotnet run --project src/DocIntake.DecisionsService         # terminal 1, http://localhost:5094 (downloads the model on the first run)
    dotnet run --project src/DocIntake.Api                      # terminal 2, http://localhost:5091, ready in seconds
    curl -s localhost:5091/health/ready
    curl -s -H 'Content-Type: application/json' -d '{"text":"The room was dirty"}' localhost:5091/api/feedback/analyze

**What it does not do.**
- The decisions service has no authentication: any pod that can reach it is trusted (threat model 4.2).
- It does not scale as one model: each replica loads its own copy (about 1 GiB) and has its own warm-up.
- The feedback text is not recorded in logs or usage by either service. It is not in the traces either: only the URL is.

**Tests.** 475 tests, 469 passed and 6 skipped (the opt-in ones from Phase 1). New:
- `DecisionsServiceTests` (10): the service over HTTP with a fixed model state and a fake analyzer: a ready model answers; a
  loading or failed model is a 503 with its kind and text and the analyzer is not asked; the service checks the text too;
  readiness follows the model (loading is 503 `Unhealthy`, failed is 200 `Degraded`); liveness does not wait.
- `GatewayDecisionsTests` (9): the gateway as deployed. A ready model answers through the gateway unchanged; loading and failed
  are 503 with the same text and no retry hint; bad text is refused at the gateway **without calling the service**; an
  unreachable service is a 503 with a retry hint and the rest of the gateway still works (`Degraded`); a service dying on the
  request is a 502; no URL means 503 and a gateway that starts; **an empty URL** (what an empty environment variable binds as)
  does not stop it starting; a non-http URL does.
- `AnalyzeEndpointTests`: the old readiness test, which expected the gateway to be unready while Laya loaded, now asserts the
  new behaviour on purpose.
- `ArchitectureTests`: the gateway's build has **no ONNX Runtime and no Laya library**; the decisions service has them and no
  document parser, no provider client, no database and no reference to the other services; its source mentions no `StatusCodes`
  outside its entry point.

### Verified (2026-10-12)

- `dotnet build`, `dotnet test` (above), the licence check (no package was added: the Laya package moved), `helm lint` on the
  three CI values files, `docker compose config`, both images build.
- **The rendered chart:** the decisions Deployment has no Secret, no extraction setting and a read-only root filesystem; the API
  has `Decisions__ServiceUrl` and the short startup probe.
- **The real model, as separate processes** (no fake): the gateway up in 4 s; the decisions service restarted while the gateway
  was polled: `503 the decisions service is unreachable` at +0 s, `503 model is still loading` at +1 s, answers at +3 s; a
  guest complaining about a dirty room scored `dissatisfied` with `Cleanliness`, a delighted one `None`.
- **The images as containers**, decisions read-only with no capabilities and on a network with no route out ("Network is
  unreachable" to 1.1.1.1): ready in seconds, answers, and stopping it gives `503` with `Retry-After: 10`.
- **Honeycomb:** the dataset `docintake-decisions` has the model loads and the request spans, as children of the gateway's.

### Not verified

- `docker compose up` of the quartet, Kubernetes, and a rolling deploy with old and new pods together.
- Laya's behaviour under load or with many replicas, and the memory the decisions pod really needs: its 1 GiB limit is what the
  API pod had while it carried Laya, not a new measurement.
- Whether the gateway needs less memory now (its limits are unchanged in Helm).

### Before deploying (Phase 5)

- [ ] **Make the `laya-sample-decisions` package public after CI first pushes it** (deploy/README.md). A package new to GitHub
      starts private and the cluster pulls without credentials, so the first deploy leaves the decisions pod in
      `ImagePullBackOff` and `helm upgrade --wait` fails until you do. (If you have not yet done the same for
      `laya-sample-extraction`, do both.) Re-run the deploy afterwards.
- [ ] **Rollout window:** the API and the new decisions Deployment come up separately, and the model takes minutes on a small
      node, so feedback analysis answers 503 until it has loaded (the same 503 as before). Documents are not affected.
- [ ] **The node must fit one more pod** (256 MiB requested, 1 GiB limit). Together with ClamAV (about 4 GB) the node needs 8 GB.
- [ ] Honeycomb: the model-load span and metric, and the feedback request's child spans, move to `docintake-decisions` and the
      meter `DocIntake.Decisions`. A trigger on the gateway's readiness being 503 while Laya loads will stop firing.
- [ ] Anything that set `Laya__*` on the API Deployment must set it on the decisions Deployment (`decisions.extraEnv`); nothing in
      this repository does.

## Phase 4: the extraction service (2026-10-12)

**What it does.** AI extraction moved out of the gateway into `DocIntake.ExtractionService`. It is now the only process that
holds the model provider's key and reaches the internet. The gateway no longer has the key, the models, the prompts or an
OpenAI client. Callers see the same routes, answers and status codes.

**How it works.** For `POST /api/documents/extract` the gateway scans, classifies and prepares the upload as before, then:

1. Before any of that, it asks the service `GET /extraction/status` ("do you have a key?"), remembered for 5 seconds, so a
   deployment without a key still answers `503 "extraction is not configured"` before anything is read, scanned or rendered.
   An unreachable service is the same 503 with `Retry-After: 10`.
2. It sends `POST /extract` with the classification and the prepared document, and **nothing else**: not the file's name, not the
   caller, not the API keys. The service picks the agent, calls the model (page images when it has them, text otherwise),
   validates the answer and replies with an `ExtractAnswer`: the data, the usage, the warnings, the **findings about the
   answer**, and **the model calls it made**.
3. The gateway records one usage row per call (`UsageRecord` = the call + who asked + what was uploaded), then applies the
   risk policy to the findings (`Security:OnFinding`): the service finds, the gateway decides. Recording comes first, so a
   refused answer, which was paid for, is still counted.
4. On failure the service answers with problem details carrying the failure's `kind`, a `retryAfterSeconds` hint and the
   `calls` made before it failed. The gateway's client rebuilds the same `IntakeFailureException`, so statuses, `Retry-After`
   and the recording of a billed failed call are unchanged.

**The wire contract** (`DocIntake.Contracts/ExtractionWire.cs`): `ExtractRequest(Classification, Document)`,
`ModelCall(At, Agent, Model, Attempt, Images, Usage?, PriceKey?, Outcome, DurationMs)`, `ExtractAnswer(...)`,
`ExtractionStatus(Configured)`. `IntakeFailureException` gained `Calls`, and `FailureKind` gained `DependencyUnavailable`
(503), used when the extraction service cannot be reached or is saturated. A call's `Outcome` is unchanged: `ok`, `timeout`,
`cancelled`, or `http_<status>`, so existing usage rows and queries keep working.

**What moved where.**

| Now in `DocIntake.ExtractionService` | Still in `DocIntake.Api` (gateway) | New shared |
|---|---|---|
| `ExtractionOrchestrator` (was `DocumentExtractor`), the agents and prompts, `ExtractionMessageBuilder`, the provider client, retry policy and error mapper, `ExtractionValidator`, `LendingTypeDetector`, `AnswerRisks`, `ModelPricing`, `ExtractionMetrics`, `ExtractionOptions` | `DocumentExtractor` (calls the service, records usage, applies the policy), `ExtractionServiceClient`, `ExtractionGatewayOptions`, `RiskScanner.EnforceAnswer`, usage storage and endpoints | `Contracts`: the wire types, `InjectionPatterns` (the gateway checks the document's text with it, the service neutralises prompts and checks answers). `ServiceDefaults`: the problem answer now carries `retryAfterSeconds` and `calls` |

**Settings.** Same section (`Extraction`), split by who reads them, as for `DocumentAnalysis`:

| Setting | Before | Now |
|---|---|---|
| `ApiKey` (or `OPENAI_API_KEY`), `Provider`, `Endpoint`, `VisionModel`, `TextModel`, `MaxImagePages`, `ImageDetail`, `TimeoutSeconds`, `MaxRetries`, `MaxOutputTokens`, `MaxTextChars`, `Pricing` | gateway | **Extraction service only** |
| `MaxRequestBytes` (default 256 MB) | none | Extraction service: the largest request it accepts, page images included |
| `MaxConcurrentExtractions`, `MaxQueuedExtractions` | gateway | Both: the gateway's limit (503 with `Retry-After`) and the service's own, since the gateway can be replicated |
| `ServiceUrl` | none | **Gateway.** Optional: empty means extraction answers `503` "not configured" and the gateway still starts |

Local development: the key is now an extraction-service user secret (`--project src/DocIntake.ExtractionService`); the old one
in the gateway's secrets is not found. The two services have their own user-secrets IDs (telemetry settings too).

**Deployment, pulled forward from Phase 7.** The service cannot wait for the cutover, because merging deploys: `Dockerfile`
has an `extraction` target; `docker-compose.yml` an `extraction` service (public network only: it needs the internet for the
provider; read-only, no capabilities, 1 GB, 256 pids); Helm has `extraction-deployment.yaml`, `extraction-service.yaml` and a
NetworkPolicy (ingress from the API only; egress to DNS, the collector and TCP 443); the collector accepts its telemetry; CI
builds and pushes the image and checks its rollout. **`api.extraction.*` values are now `extraction.*`**, and the chart's
schema rejects the old shape, so a stale values file fails loudly instead of leaving extraction without a key. The rendered
chart gives the `laya-llm` Secret to the extraction Deployment and to no other.

**Behaviour changes.**
- **Where the key lives**, and a **third process** to run locally for extraction (README quick start).
- **An extraction service outage** fails extraction with 503 and `Retry-After`; analysis, feedback and everything else keep working,
  and the gateway's readiness shows `extraction-service` as `Degraded`.
- **A request the caller abandoned** (a disconnect, or the gateway's own request timeout) no longer leaves a `cancelled` usage
  row: the service cannot report a call to a gateway that has hung up. Those rows never had tokens or a cost.
- **A refused answer is counted by the service's metrics** (`intake.extraction.documents`, `.fields`, `.warnings`), because the
  service does not know the gateway will refuse it. The usage row and the 422 are unchanged.
- The extraction metrics moved to their own meter, **`DocIntake.Extraction`** (same metric names), and the spans
  `intake.extraction` / `intake.extract` and the gen_ai spans now arrive in the dataset **`docintake-extraction`**.
- A usage row's timestamp is the service's clock when the call ended.
- The startup log line names the extraction service's address instead of the models.
- `intake.failure.cause` and the log lines for extraction failures are written by the gateway and, separately, by the service.

**Deviations from the plan.**
- **Deployment was pulled forward** (above), so Phase 7 has less to do.
- `ExtractionOrchestrator` was not split into an orchestrator and a `ModelCallRecorder`. The real separation is that usage
  recording left the service altogether; what remains is a private method that builds a `ModelCall`.
- `InjectionPatterns` went to Contracts (the plan put the answer detection in the service and left the text detection in the
  gateway, but both use the same rules).

**A production bug found and fixed.** The service crashed with a stack overflow the first time the fake provider answered 429.
`RecordingRetryPolicy` overrode both `ShouldRetry` and `ShouldRetryAsync`, each calling the other; the SDK's base async method
calls the sync one, so the first retryable response (a 429, a 5xx or a timeout) recursed until the process died. **The same
code is on `main`, so today it would take down the API pod.** The old tests missed it because the fake chat client bypasses the
SDK's retry pipeline. Fixed here and, separately and ahead of this change, in
[#9](https://github.com/altafmehmood/LayaSample/pull/9). `RetryPolicyTests` now send through the real pipeline (async and sync):
a 429 is retried and counted once, a provider that keeps refusing is given up on after `MaxRetries`, and a 400 is not retried.
On the old code the test host dies with `Stack overflow.`

**Key files.**
- `src/DocIntake.ExtractionService/Program.cs` (the endpoints), `ExtractionOrchestrator.cs`, `ExtractionClients.cs`
- `src/DocIntake.Api/Services/Documents/Extraction/DocumentExtractor.cs`, `ExtractionServiceClient.cs`
- `src/DocIntake.Contracts/ExtractionWire.cs`, `Failures.cs`
- `deploy/helm/docintake/templates/extraction-*.yaml`, `networkpolicy.yaml`

**How to try it.** Needs your own key (the extract call is **paid**). Without a key everything but the extraction itself works.

    dotnet user-secrets set "Extraction:ApiKey" "sk-..." --project src/DocIntake.ExtractionService
    dotnet run --project src/DocIntake.DocumentService          # terminal 1
    dotnet run --project src/DocIntake.ExtractionService        # terminal 2
    dotnet run --project src/DocIntake.Api                      # terminal 3
    curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf" localhost:5091/api/documents/extract   # (paid)
    curl localhost:5091/api/usage

To try it for free, point the extraction service at any OpenAI-compatible endpoint (`Extraction__Endpoint=http://127.0.0.1:5099/v1`
and `Extraction__ApiKey=anything`): that is how the cases below were run.

**What it does not do.**
- The extraction service has no authentication: any pod that can reach it is trusted (threat model 4.2).
- The gateway still hosts Laya; it moves in Phase 5. The gateway's pod still has no NetworkPolicy.
- Usage is not recorded by the service, so a gateway that is gone when the answer arrives records nothing.
- The service buffers the request (the prepared document, images included) in memory.

**Tests.** 454 tests, 448 passed and 6 skipped (the opt-in ones from Phase 1). Changed: the tests that built an extractor now
drive the service's orchestrator and record usage from the calls it reports; the factories use `UseExtractionService`, which
attaches an in-memory extraction service with the test's fake chat client, and the test handler now carries trace context like a
real connection (so a request is one trace across the services). New:
- `ExtractionServiceTests` (18): the service over HTTP through the gateway's client. The answer with the calls made and their
  price; page images reaching the model; findings reported but not acted on; every provider failure keeping its kind, retry hint
  and the call that failed; a cut-off answer reporting its billed tokens; the generic agent's second call; the status; the
  client's reading of unreachable, dying, saturated and malformed services.
- `GatewayExtractionTests` (6): the gateway as deployed. An unreachable service fails extraction but not analysis, and shows
  `Degraded`; no URL means `503` and a gateway that starts; **a service without a key turns the upload away before the document
  service is called once**; **the request to the service carries neither the file's name nor the caller**.
- `UsageTests` (3 new, end to end through the gateway): a cut-off call is recorded with its tokens and cost; a refused answer
  is recorded; a rate-limited call is recorded without tokens.
- `RetryPolicyTests` (5): the real SDK pipeline (above).
- `ArchitectureTests`: the gateway's build holds no model-provider library and no extraction service; the extraction service has
  them and no document parser and no reference to the gateway; no source in it mentions `StatusCodes` outside its entry point; and the
  key appears in no gateway source, no gateway Deployment and no gateway compose block.
- `ProblemMappingTests`: the new kind in the status, outcome and log-level tables.

### Verified (2026-10-12)

- `dotnet build`, `dotnet test` (above), the licence check (no package was added: the AI packages moved), `helm lint` on the
  three CI values files, `docker compose config`.
- **The rendered chart:** the `laya-llm` Secret is referenced by the extraction Deployment and by no other; the API has
  `Extraction__ServiceUrl` and no key or model setting.
- **Three separate processes with real OCR, the extraction service pointed at a local fake of the provider** (the real SDK,
  real HTTP between all three, a real SQLite usage database): a success (vision model, one page image, the key sent, cost
  $0.01452 by hand and by the service); an instruction-like answer (`422`); a cut-off answer (`502`); a rate limit (`503` with
  the provider's `Retry-After: 7` after the SDK's three attempts). **All four calls are in `/api/usage`**, with the caller and
  the file name the service never saw.
- **One trace across three Honeycomb datasets** (`docintake-gateway`, `docintake-document`, `docintake-extraction`).
- The gateway survived the extraction service dying mid-request (the stack overflow above) and answered 502.

### Not verified

- The real model provider. The extract call costs money, so only a local fake was used: the real provider's own error bodies,
  streaming and rate-limit headers are untested.
- `docker compose up` of the trio, Kubernetes, and a rolling deploy with old and new pods together. The three images build
  (below), but were not run together.
- Memory and latency under load: the request, with its page images, now crosses the network twice more and is buffered in the
  service.

### Before deploying (Phase 4)

- [ ] **Make the `laya-sample-extraction` package public after CI first pushes it** (deploy/README.md, "First deploy of the
      extraction image"). A package new to GitHub starts private and the cluster pulls without credentials, so the first deploy
      leaves the extraction pod in `ImagePullBackOff` and `helm upgrade --wait` fails until you do. Re-run the deploy afterwards.
- [ ] **Merge [#9](https://github.com/altafmehmood/LayaSample/pull/9) first** (the stack overflow is on `main` today).
- [ ] **Your local key moved**: set it again for the extraction service (README quick start).
- [ ] Any values file that sets `api.extraction.*` must use `extraction.*`; the schema rejects the old keys.
- [ ] **Rollout window**: the API and the new extraction Deployment come up separately, so for a moment an API without a service
      to call answers extraction with 503. Analysis is not affected.
- [ ] Honeycomb: extraction spans and metrics change dataset (`docintake-extraction`), and the metrics meter is
      `DocIntake.Extraction`. Boards that read them from `docintake-gateway` go quiet.
- [ ] The node must fit one more pod (192 MiB requested, 768 MiB limit).

## Phase 3: the document service (2026-10-12)

**What it does.** The renderer grew into the **document service**. Classification and preparation moved into it, with
PdfPig, the Office libraries and the native libraries it already had. The gateway (`DocIntake.Api`) no longer contains
any code that reads a document: no PDF, Office, image or OCR library, and no reference to `DocIntake.Rendering`. Callers
see the same routes, answers and status codes.

**How it works.** Per upload the gateway calls the service twice, over HTTP, each time sending the file itself (streamed
from the spooled upload, never copied into memory):

1. `POST /documents/classify` returns a `ClassifyResponse`: the classification and the findings about the file's structure.
   The gateway's risk policy then decides, so with `Security:OnFinding=reject` a document with active content or hidden
   text is refused **before** anything is prepared (rendered, OCRed).
2. `POST /documents/prepare` (the file plus the classification as a JSON part) returns the `PreparedDocument`: page images,
   page text, OCR text and structured data. The gateway then checks the text for instructions aimed at the model.

The service keeps nothing between the calls. A failure comes back as problem details with a `kind` extension, and the
gateway's `DocumentServiceClient` raises the same `IntakeFailureException` kind on its side (so `Unsupported` is still 415,
`OverLimit` still 422). A service that cannot be reached or is saturated is a `RendererUnavailableException` (503 with
`Retry-After: 10`); one that dies on the document is a `RenderingFailedException` (502). Nothing is retried: a document that
crashed the service would crash it again.

**What moved where.**

| Now in `DocIntake.DocumentService` | Still in `DocIntake.Api` (gateway) | New shared |
|---|---|---|
| `DocumentClassifier`, `DocumentPreparer`, `DocumentSniffer`, `Pdf/*`, `OfficeInspector`, `ExecutableSniffer`, the classification and preparation limits | `DocumentPipeline`, `RiskScanner`, `DocumentRouter`, malware scan, extraction, usage, Laya, `ProblemMapping`, `DocumentServiceClient`, `DocumentGatewayOptions` | `Contracts`: `ClassifyResponse`, the exceptions for a bad or unavailable service, `IDocumentClassifier`, `IDocumentPreparer`, `DocumentRoutes`, `UsageFileName`, `IntakeJson`. `ServiceDefaults`: `ServiceProblems` (status table and the problem answer), `LeaveOpenStream` |

`RemotePageRasterizer`, the renderer's `/render` and `/inspect` endpoints and in-process rendering are gone.

**Settings.** Same section (`DocumentAnalysis`), split by who reads them:

| Setting | Before | Now |
|---|---|---|
| `DocumentServiceUrl` | `RendererUrl` (or `AllowInProcessRendering`) | **Required** by the gateway; it refuses to start without it. `appsettings.Development.json` sets `http://localhost:5092` |
| `AllowInProcessRendering` | development mode | Removed |
| `MaxFileBytes`, `MaxConcurrentAnalyses`, `MaxQueuedAnalyses`, `RequestTimeoutSeconds` | API | Gateway, unchanged |
| `MinCharsPerPage`, `PngDpi`, `MaxPages`, `MaxPdfPages`, `MaxImageFrames`, `MaxOfficeUncompressedBytes`, `MaxSpreadsheetRows`, `MaxAttachments`, `EnableOcr` and the rest of the classification limits | API | Document service, same names. The gateway no longer reads them |
| `MaxDocumentBytes`, `MaxConcurrentJobs`, `MaxQueuedJobs`, `OcrModel`, `OcrMaxSide` | Renderer | Document service, unchanged. The limiter now counts classify and prepare calls |
| `MaxPagesPerJob` | Renderer (`/render`) | Removed |

Helm and compose pass `DocumentAnalysis__DocumentServiceUrl` to the API; the Dockerfile publishes the document service from
`src/DocIntake.DocumentService` (the `renderer` target and Deployment keep their names until Phase 7).

**Behaviour changes.**
- **A document service outage now fails every upload**, text PDFs and Office files included. Before, only documents that
  needed rendering failed, because classification ran in the API. Feedback analysis still works, and readiness reports
  `document-service` as `Degraded` (200).
- **You run two processes locally** (`dotnet run` for each). The README's quick start says so.
- The two messages for an unavailable or crashed service now say "document processing is unavailable; retry later" and
  "document could not be processed". They said "rendering" and "rendered", which is wrong for a text PDF.
- Each upload crosses the network twice, so a 20 MB file is sent twice to the service. This is the cost of refusing a
  document before it is prepared; it is within the cluster.
- The health check is named `document-service` (it was `renderer`), and the gateway has no `ocr` check any more.
- The library detail of why a file was refused (the inner exception) is now logged by the document service, not the gateway.
  The gateway logs the kind, the reason and `intake.failure.cause` = `none`.
- The service's spans are children of the gateway's request: `docintake-document` shows `POST /documents/classify` and
  `/prepare` with the `renderer.*` spans beneath them, in the same trace as `docintake-gateway`.

**Deviations from the plan.**
- The plan had one `POST /documents/prepare` returning the findings. Classify and prepare are two calls, so a refused
  document is never prepared (the plan's single call would have rendered it first).
- `DocumentClassification.Risks` is deliberately not serialised (the public responses report findings in their own list),
  so the service sends them next to it in a `ClassifyResponse`. A first version without that lost every structure finding
  over the wire, and three `RiskTests` caught it.
- The plan said the gateway image would lose its native libraries. It loses every document library (PDFium, ImageMagick,
  Skia, OCR); **ONNX Runtime stays** until Phase 5, because the Laya feedback model runs on it.
- The gateway has no in-process fallback. The plan said to retire it; the cost is the two terminals above.

**Key files.**
- `src/DocIntake.DocumentService/Program.cs` (endpoints), `Classification/`, `Preparation/`, `Pdf/`, `Inspection/`
- `src/DocIntake.Api/Services/Documents/DocumentServiceClient.cs`, `DocumentGatewayOptions.cs`
- `src/DocIntake.ServiceDefaults/ServiceProblems.cs`
- `src/DocIntake.Contracts/DocumentServices.cs`, `RenderingFailures.cs`, `IntakeJson.cs`

**How to try it.**

    dotnet run --project src/DocIntake.DocumentService          # terminal 1, http://localhost:5092
    dotnet run --project src/DocIntake.Api                      # terminal 2, http://localhost:5091
    curl -s localhost:5091/health/ready
    curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf" "localhost:5091/api/documents/analyze?dispatch=false&includeData=false"
    curl -i -F "file=@docs/samples/unsupported/legacy.doc" localhost:5091/api/documents/analyze

Then stop the document service and repeat the second command: `503` with `Retry-After: 10`, readiness `Degraded`, and
`curl -H 'Content-Type: application/json' -d '{"text":"The room was dirty"}' localhost:5091/api/feedback/analyze` still
answers. Or run the two images (`docker build --target api` and `--target renderer`) on one network.

**What it does not do.**
- The gateway still hosts extraction, usage and Laya. They move in Phases 4 and 5.
- The document service has no authentication: any pod that can reach it is trusted (threat model 4.2). It matters more now,
  because the service sees every upload.
- `RendererUnavailableException`, `RenderingFailedException` and `OcrUnavailableException` are not folded into
  `FailureKind`, and their outcome strings (`renderer_unavailable`, `render_failed`) are unchanged.
- Nothing was measured under load, so the cost of sending each file twice is not known beyond the single requests below.

**Tests.** 414 tests, 408 passed and 6 skipped (the opt-in ones from Phase 1). The existing endpoint tests pass through an
in-memory document service: a shared helper, `UseDocumentService`, attaches one to any gateway factory and takes the OCR
fake as before, so the files that built a gateway needed one changed line each. New or rewritten:
- `DocumentServiceTests` (replaces `RendererTests`): the service over HTTP through the gateway's client. Classify and
  prepare for text, scan and fax documents; greyscale fax pages and OCR text; the findings arriving with the classification;
  the contract surviving JSON; each failure kind surviving the boundary (unsupported, unreadable, over a limit); missing OCR
  models; oversized and malformed requests; unreachable, dying and error-mapping cases for the client.
- `GatewayDocumentServiceTests` (replaces `RemoteRenderingEndpointTests`): the gateway as deployed. An unreachable service
  fails every upload with `Retry-After` and readiness `Degraded`; a crashing one is a 502; production without a
  `DocumentServiceUrl` refuses to start.
- `ArchitectureTests`: the gateway's dependency manifest has no document parser, no native imaging or OCR library and no
  reference to `DocIntake.Rendering`; the document service's manifest has them; and no source file under the classification,
  preparation, PDF or inspection folders mentions `StatusCodes`.

### Verified (2026-10-12)

- `dotnet build`, `dotnet test` (above), the licence check (no package was added), `helm lint` on the three CI values
  files, `docker compose config`.
- Both Docker targets build. The gateway image holds no PdfPig, ClosedXML, OpenXML, Magick.NET, PDFium, Skia or OCR file,
  only ONNX Runtime; the document service image holds them.
- **The two services as separate processes**, real OCR, 12 sample documents (text, scan and fax PDFs, a form, signed, XFA
  and e-invoice PDFs, an Excel file, a Word file, a fax TIFF, and the two unsupported files): the kinds, strategies and
  parts match Phase 1's, and the fax PDF with OCR took 0.68 s.
- **The two images together** on a private network, the document service with a read-only filesystem, no capabilities and a
  2 GB limit: the same results; with the service stopped, text and scan uploads answered 503 with `Retry-After: 10`,
  feedback analysis still answered 200 and readiness was `Degraded`; after a restart uploads worked again.
- **One trace across both services** in Honeycomb: the gateway request, `intake.classify`, the HTTP client span and the
  document service's `POST /documents/classify` as a chain.

### Not verified

- A deploy to DOKS, and `docker compose up` of the pair with ClamAV (the images were run directly, without the scanner).
- Extraction against the real model provider: it costs money, and the extract path was only exercised for files that fail
  before the model.
- Latency and memory under load. The document service now also loads Office files and every PDF's structure, in a pod
  limited to 1 GiB in `deploy/values-prod.yaml`.
- A rolling deploy with old and new pods side by side.

### Before deploying (Phase 3)

- [ ] **Rollout window.** `helm upgrade` replaces the API and the renderer Deployments separately, so for a moment an old
      API may call a new document service (404 on `/render`) or the reverse. Expect a few 502/503s during the rollout.
- [ ] **Memory.** Watch the renderer pod for `OOMKilled` after the deploy and raise `renderer.resources.limits.memory`
      (1 GiB in `values-prod.yaml`) if big spreadsheets or scans restart it. The API pod should need less than before.
- [ ] Honeycomb: the `docintake-document` dataset now has `POST /documents/classify` and `/prepare` request spans and
      `intake.render`; anything built on the renderer's `POST /render` span will go quiet.
- [ ] Anything that set `DocumentAnalysis__RendererUrl` on the API must set `DocumentAnalysis__DocumentServiceUrl` instead
      (compose and Helm in this repository do).
- [ ] Anything that matched `document rendering is unavailable` or `document could not be rendered` needs the new text.

## Phase 2: shared projects and one failure type (2026-10-12)

**What it does.** Two new projects take the code every service will share, and one exception type replaces the two that
carried HTTP status codes. HTTP statuses are now decided in exactly one place. Public routes, status codes, Retry-After
headers and response bodies are unchanged.

| Project | Holds | Depends on |
|---|---|---|
| `DocIntake.Contracts` | The models (documents, extraction, risk findings, feedback) and the failure types | Nothing but the runtime |
| `DocIntake.ServiceDefaults` | `IntakeTelemetry` (OpenTelemetry setup, `RecordFailure`), `HealthReporting`, `WarmupState`, and the OpenTelemetry packages | ASP.NET Core, OpenTelemetry |

`DocIntake.Rendering` lost its OpenTelemetry packages and now references ServiceDefaults. `OcrHealthCheck` stays in
Rendering because it needs `OcrWarmup`. `AddIntakeTelemetry` no longer names the rendering meter: each service passes its
own (`DocumentMetrics.MeterName` and `RenderingMetrics.MeterName` for the API; the renderer passes its own).

**Failures.** `DocumentException` and `ExtractionException` are gone. Everything below the endpoints throws
`IntakeFailureException(FailureKind, message, retryAfterSeconds?, inner?)`:

| `FailureKind` | Status | Thrown for |
|---|---|---|
| `Unsupported` | 415 | Unknown type, legacy Office, plain text |
| `Unreadable` | 422 | Corrupt or password-protected file, Word file with no body, PDF with no machine-readable data |
| `OverLimit` | 422 | Too many PDF pages or image frames, a file that unpacks past the limit |
| `Malicious` | 422 | The malware scanner found something |
| `PolicyRejected` | 422 | `Security:OnFinding=reject` refused the document or the model's answer |
| `Declined` | 422 | The model's content filter, or the provider calling the document too large or unsupported |
| `Busy` | 503 | The provider rate-limited (carries Retry-After, 30 s if the provider gave none) |
| `Misconfigured` | 503 | The provider rejected our credentials |
| `Timeout` | 504 | The provider took too long |
| `DependencyFailed` | 502 | The provider failed, or its answer was cut off or unreadable |

The rendering and scanner exceptions (`OcrUnavailableException`, `RendererUnavailableException`,
`RenderingFailedException`, `MalwareScannerUnavailableException`) are unchanged. They answer 503, 503, 502 and 503 with
fixed messages, and Retry-After 10 for the renderer and the scanner.

**`ProblemMapping`** (`src/DocIntake.Api/Services/ProblemMapping.cs`) replaces `DocumentProblems` and the inline
`catch (ExtractionException)` in `Program.cs`. For every failure it sets, in one place:
- the status and the problem body (the exception's message, or the fixed message above)
- the Retry-After header
- `intake.outcome`, `intake.document.kind` and `intake.failure.cause` on the request span, and the failure on the span
- one log line, at Information for a bad file, Warning for a busy, slow or failing provider, Error for bad credentials or
  a broken renderer
- the `intake.document.analyzed` metric

A bad file's log carries the inner exception, which says why a library rejected it. A provider failure never does: only
the inner exception's type is logged, because provider error text can echo the API key or the document. For bad
credentials the log line says to check the API key.

**Behaviour changes.** All are in telemetry and logs; none change what a caller sees.
- `PolicyRejected` for the model's answer now has `intake.outcome` = `rejected` (it was `extraction_failed`), like every
  other refusal by our rules.
- `intake.failure.kind` on the request span is now the outcome (for example `extraction_failed`). For extraction
  failures it used to be the fixed word `extraction`.
- `intake.failure.cause` is now set for every failure, not only extraction ones.
- The per-case log messages became one message. The usage history still records `http_<status>` as a call's outcome
  (`http_503`, `http_502`), so existing rows and queries keep working. Phase 4 revisits that label.

**Deviation from the plan.** The plan called `ProblemMapping` an `IExceptionHandler`. It is a plain class that both
endpoints call from one `catch`, because a global handler would lose the request's classification and upload size, which
the metric, the span and the log need. It moves to a handler in Phase 6, when the endpoints are restructured. The kind
`TooLarge` is called `OverLimit`, because the status is 422 and the endpoint's own 413 is a separate check.

**Key files.**
- `src/DocIntake.Contracts/Failures.cs`
- `src/DocIntake.Api/Services/ProblemMapping.cs`
- `src/DocIntake.ServiceDefaults/IntakeTelemetry.cs`, `HealthReporting.cs`
- `Dockerfile` (the restore layer copies the two new projects)

**Settings.** None.

**How to try it.** Neither command spends money: both upload files that fail before any model call.

    dotnet run --project src/DocIntake.Api
    curl -i -F "file=@docs/samples/unsupported/legacy.doc" localhost:5091/api/documents/analyze
    curl -i -F "file=@docs/samples/unsupported/notes.txt" localhost:5091/api/documents/extract

Both answer `415` with the same problem body as before. With the OTLP user secrets set, the request span in
`docintake-gateway` has `intake.outcome` = `rejected`, `intake.failure.cause` = `none` and the failure recorded on it.

**What it does not do.**
- The services still run in one process. This phase prepares the split; it does not make it.
- `IFormFile` is still in `DocumentPipeline`'s signature.
- The rendering and scanner exceptions are not folded into `FailureKind`.
- No Honeycomb names change in this phase beyond the ones above.

**Tests.** 399 tests, 393 passed and 6 skipped (the opt-in ones listed under Phase 1). The existing endpoint tests pass
unchanged. The unit tests that read an exception's `StatusCode` now read `ProblemMapping.StatusOf(ex.Kind)`, so they
still assert the same statuses. New:
- `ProblemMappingTests` (33 tests): a status for every kind, with a test that fails if a kind is added without one; the
  outcome for every kind; Retry-After; the rendering and scanner answers; the log level for each kind; the inner
  exception logged for a bad file and never for a provider; and the "check the API key" hint. I confirmed the
  provider-text tests fail (3 of them) when the inner exception is logged for every kind.
- `ArchitectureTests`: `Contracts` references only the runtime, and nothing under `Services/Documents` or
  `Services/Security` mentions `StatusCodes`.

### Verified (2026-10-12)

- `dotnet build`, and `dotnet test` (above).
- The licence check passes. No package was added; the OpenTelemetry packages moved from Rendering to ServiceDefaults.
- `docker build --target api` and `--target renderer` both succeed.
- A local run: `analyze` and `extract` on `legacy.doc` and `notes.txt` answer 415 with the unchanged messages, and the
  spans reached Honeycomb with the attributes listed above.

### Not verified

- Every failure path against the real provider (rate limit, bad key, timeout). The tests cover them with a fake client.
  Extraction was not called, because it costs money.
- The native crash seen once in the first full test run of this phase (`libc++abi: terminating due to uncaught
  exception ... recursive_mutex lock failed`, reported after all tests had passed). Three further full runs were
  clean. **Update 2026-10-12:** it is not from these changes. `main` as of the Phase 1 merge, with a rebuild before each
  run, crashed the same way in 2 of 8 runs. It is a flake in the test host's native teardown (it happens after every test
  has passed), and its cause is unknown.

### Before deploying (Phase 2)

- [ ] On Honeycomb boards or triggers, queries on `intake.outcome` = `extraction_failed` no longer count answers
      rejected for injected text; those are `rejected` now.
- [ ] Anything that matched the old log messages ("Extraction failed with", "Rejected ... document with", "Renderer
      failed on a") needs the new message, which starts with the outcome.

## Phase 1: rename LayaSample → DocIntake (2026-10-11)

**What it does.** Renames the project everywhere it is not tied to a live deployment. Behaviour is unchanged.

| Before | After |
|---|---|
| `LayaSample.slnx`, `src/LayaSample.{Api,Renderer,Rendering}`, `tests/LayaSample.Tests` | `DocIntake.slnx`, `src/DocIntake.{Api,Renderer,Rendering}`, `tests/DocIntake.Tests` |
| Namespaces `LayaSample.*` | `DocIntake.*` |
| `LayaTelemetry`, `AddLayaTelemetry` | `IntakeTelemetry`, `AddIntakeTelemetry` |
| `service.name` `laya-sample-api` / `laya-sample-renderer` (Honeycomb datasets) | `docintake-gateway` / `docintake-document` |
| ActivitySources `LayaSample`, `LayaSample.GenAI` | `DocIntake`, `DocIntake.GenAI` |
| Meters `LayaSample.Documents`, `LayaSample.Rendering` | `DocIntake.Documents`, `DocIntake.Rendering` |
| Span names and attributes `laya.*` (e.g. `laya.classify`, `laya.document.kind`) | `intake.*` (e.g. `intake.classify`, `intake.document.kind`) |
| Metric names `laya.*` (e.g. `laya.llm.cost`) | `intake.*` (e.g. `intake.llm.cost`) |
| Resource attribute `laya.image.tag` (Helm) | `intake.image.tag` |
| Problem type `urn:laya-sample:ocr-unavailable` | `urn:docintake:ocr-unavailable` |
| Usage CSV files `laya-usage*.csv` | `docintake-usage*.csv` |
| Compose project `laya-sample`, local images `laya-sample-api` / `-renderer` | `docintake`, `docintake-api` / `docintake-renderer` |
| Compose and red-team variables `LAYA_API_KEY`, `LAYA_ON_FINDING`, `LAYA_OTLP_ENDPOINT` | `DOCINTAKE_API_KEY`, `DOCINTAKE_ON_FINDING`, `DOCINTAKE_OTLP_ENDPOINT` |
| Red-team user agent `laya-garak/<run>` | `docintake-garak/<run>` |
| Helm chart `deploy/helm/laya-sample` (name `laya-sample`) | `deploy/helm/docintake` (name `docintake`) |

The renderer's own spans (`renderer.*`, `ocr.model.load`) are renamed in Phase 9. Two names are deliberately set to
where they will end up, so the Honeycomb datasets move only once:
- `service.name` is `docintake-gateway` for the API.
- `service.name` is `docintake-document` for the renderer.

**Kept on purpose:**

- **The Laya model's own names.** The `Laya:` settings section, `ElBruno.LocalLLMs.Decisions`, the `laya-model` Docker
  stage, `scripts/download-models.sh` and `models/elbruno_laya-onnx` all name the model, not the project.
- **The live cluster.** These stay until the services cutover replaces the Deployments:
  - namespace and Helm release `laya`, and `fullnameOverride: laya`
  - Secrets `laya-llm`, `laya-api-key` and `laya-otel`
  - the `LAYA_API_KEY` GitHub secret
  - the public path `/laya`
  - the GHCR images `laya-sample-api` / `laya-sample-renderer`

  The reasons:
  - Deployment selector labels (`app.kubernetes.io/name`) are immutable, so `deploy/values-prod.yaml` pins
    `nameOverride: laya-sample`.
  - GHCR creates new user-owned packages as private, and the cluster pulls without credentials.
- **`UserSecretsId`.** Your existing `dotnet user-secrets` (model key, API keys, OTLP settings) keep working.
- **Older dated entries** in the README's "Quickstarts by date". They still say `LayaSample`. Read `src/LayaSample.Api` as
  `src/DocIntake.Api`.
- **The sample PDFs** in `docs/samples/pdf/corpus` still carry `LayaSample corpus generator` as their PDF Producer.
  The generator now writes `DocIntake`, but the committed files were not regenerated.

**Key files.**
- `DocIntake.slnx`
- `src/DocIntake.Rendering/IntakeTelemetry.cs`
- `deploy/helm/docintake/`
- `deploy/values-prod.yaml` (the `nameOverride` pin)
- `.github/workflows/ci.yml` (chart and test paths)
- `docker-compose.yml`

**How to try it.**

    dotnet run --project src/DocIntake.Api
    curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf" "localhost:5091/api/documents/analyze?includeData=false"

You should see `"kind": "FaxPdf"` with strategy `Hybrid`. With the OTLP user secrets set, the trace arrives in a
`docintake-gateway` dataset with `intake.classify`, `intake.prepare` and `intake.render` spans and `intake.*` attributes.

**What it does not do.**
- It doesn't rename the GitHub repository. That is yours to do.
- It doesn't move the live deployment's names.
- It doesn't migrate Honeycomb boards or triggers, which still query `laya.*` in the old datasets.
- It doesn't copy local Docker volumes. The compose project name changed, so `docker compose` starts with fresh `usage`
  and ClamAV signature volumes, and the old `laya-sample_*` volumes stay until you remove them.

**Tests.** The whole suite passed unchanged apart from names: 363 tests, 357 passed and 6 skipped. The skipped ones are
opt-in:
- `ClamdIntegrationTests` needs a ClamAV daemon (`CLAMD_HOST`).
- `RealOcrRiskTests` needs `REAL_OCR=1`.
- `ExtractionEvaluationTests` is paid (`RUN_EXTRACTION_EVAL=1`).

### Verified (2026-10-11)

- `dotnet build` and `dotnet test` (above).
- The licence check passed.
- `docker compose config` is valid.
- `docker build --target api` and `--target renderer` both succeeded.
- `helm lint` passed for every `ci/*.yaml`.
- **Prod render is safe.** `helm template` of the production values was diffed against `main`. The only differences
  are the `helm.sh/chart` label, the `intake.image.tag` resource attribute and the collector's config checksum, which
  restarts the collector once. Names, selectors and images are unchanged.
- **Local run.** `dotnet run` plus one analyze call returned `FaxPdf`/`Hybrid`. The trace reached Honeycomb as
  `docintake-gateway` with `intake.*` attributes.

### Not verified

- A CI deploy to DOKS.
- `docker compose up` of the renamed stack.
- The red-team run, which is paid.

### Before deploying

- [ ] Update Honeycomb boards, triggers and saved queries:
  - datasets `laya-sample-api` → `docintake-gateway`, `laya-sample-renderer` → `docintake-document`
  - the `laya.` prefix → `intake.` on spans, attributes and metrics
  - meters `LayaSample.*` → `DocIntake.*`
- [ ] Expect one restart of the OTel collector pod (config checksum).
- [ ] After merge, regenerate the architecture diagram from HEAD.
- [ ] Optionally rename the GitHub repository. GitHub redirects the old URL.
