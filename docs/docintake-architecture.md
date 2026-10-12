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
