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
