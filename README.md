# LayaSample

Hotel guest feedback → dissatisfaction sentiment, using the open-weight
[Laya](https://huggingface.co/elbruno/laya-onnx) "System 1" decision model on ONNX Runtime, in-process via the
`ElBruno.LocalLLMs.Decisions` NuGet package (.NET 10). It also classifies uploaded documents (PDF, Excel, Word,
TIFF/fax, PNG, JPEG), prepares them for an AI agent (page images, markdown, OCR text, form data) and routes them.

## Run
    dotnet run --project src/LayaSample.Api

The API listens on http://localhost:5091 and renders documents in-process (development only; see Architecture). The
first start downloads the Laya model (~850 MB, can take several minutes). `GET /health/live` answers as soon as the
process is up; `GET /health/ready` (also `/health`) returns 503 until the models are loaded, and 200 `Degraded` if one
failed to load for good or the renderer is unreachable, since the rest of the service still works.

    curl -X POST localhost:5091/api/feedback/analyze -H 'content-type: application/json' \
      -d '{"text":"Room was filthy and the staff were rude."}'

Interactive API docs (Development only): http://localhost:5091/scalar/v1 (OpenAPI JSON at `/openapi/v1.json`).

Response: `dissatisfied`, `dissatisfactionScore` (P(dissatisfied), 0–1), `rating` (1–5 dissatisfaction),
`level` (None when not dissatisfied, otherwise Mild/Moderate/Severe from the rating), `primaryDriver` (null when
unclear or not dissatisfied), `confidence`. Errors are problem details (`application/problem+json`).

Documents go to `POST /api/documents/analyze`; see [docs/samples](docs/samples/README.md) for sample files and the
expected results.

## Architecture
Untrusted PDFs and images are parsed by native C/C++ libraries (PDFium, ImageMagick, Skia, ONNX Runtime for OCR).
Those run only in a separate **renderer** service, so a malicious or malformed file that crashes or takes over a
parser cannot take down the API or reach its data:

| | `LayaSample.Api` | `LayaSample.Renderer` |
|---|---|---|
| Does | File-type detection, PDF classification and text (PdfPig, managed), Office conversion, form data, signatures, Laya, routing | Page rendering (PDFium), image decoding (ImageMagick), PNG encoding, OCR |
| Sees untrusted bytes in native code | No | Yes, isolated: no network egress, read-only file system, no capabilities, own memory/CPU limits |

The API calls the renderer over HTTP (`DocumentAnalysis:RendererUrl`). If the renderer dies on a document, that
request gets 502 and the renderer restarts; if it is down, documents that need it get 503 with `Retry-After` while
text PDFs, Office files and feedback analysis keep working, and the API's readiness reports the renderer as
`Degraded`. Shared code is in `src/LayaSample.Rendering`.

In development (`dotnet run`) the API renders in-process, because `appsettings.Development.json` sets
`DocumentAnalysis:AllowInProcessRendering`. Without that setting or a `RendererUrl`, the API refuses to start, so a
production deployment cannot fall back to in-process rendering by accident. To run the renderer locally as well:

    dotnet run --project src/LayaSample.Renderer                  # http://localhost:5092
    DocumentAnalysis__RendererUrl=http://localhost:5092 dotnet run --project src/LayaSample.Api

## Limits
Document analysis is CPU-bound, so each request runs within limits set under `DocumentAnalysis`:

| Setting | Default | Effect |
|---|---|---|
| `MaxFileBytes` | 20 MB | Larger uploads get 413 |
| `MaxPages` | 20 | Page renders per request, embedded documents included |
| `MaxPdfPages` / `MaxImageFrames` | 2000 / 200 | Longer PDFs / TIFFs get 422 |
| `MaxOfficeUncompressedBytes` | 256 MB | .xlsx/.docx that unpack larger (zip bombs) get 422 |
| `MaxConcurrentAnalyses` | half the cores | Analyses running at once |
| `MaxQueuedAnalyses` | 20 | Requests waiting for a slot; beyond that, 503 with `Retry-After` |
| `RequestTimeoutSeconds` | 120 | Waiting plus analysis; then 504 |

Invalid settings stop the app at startup. `?includeData=false` leaves base64 page images out of the response.

Durations per stage and documents by kind and outcome are published on the `LayaSample.Documents` meter
(`dotnet-counters monitor -n LayaSample.Api --counters LayaSample.Documents`).

## Notes
- The endpoints have no authentication. Put the service behind an authenticating gateway, or add authentication,
  before exposing it beyond a trusted network.
- Model settings go under the `Laya` section of `appsettings.json` (`ModelRepository`, `ModelPath`, `CacheDirectory`, `IntraOpNumThreads`, `DecisionThreshold`). `ModelPath` loads a local copy and skips the download.
- Confidences are **uncalibrated**; tune thresholds on your own labeled feedback before relying on them.

## OCR models
Scanned pages, faxes and images are read with [RapidOcrNet](https://github.com/BobLd/RapidOcrNet), which runs
PaddleOCR models on ONNX Runtime. Each model set has three networks: a detector that finds text lines, a classifier
that flips upside-down lines, and a recognizer that reads characters from a dictionary file. Pick one with
`DocumentAnalysis:OcrModel`:

| `OcrModel` | Languages | Size | Source |
|---|---|---|---|
| `PPOCRv5Latin` (default) | Latin script (English and Western European languages) | ~14 MB | Bundled in the NuGet package |
| `PPOCRv6Tiny` | Multilingual (Latin, CJK and more) | ~6 MB | `scripts/download-models.sh` |
| `PPOCRv6Small` | Multilingual | ~31 MB | `scripts/download-models.sh` |
| `PPOCRv6Medium` | Multilingual, slowest (~4x v5 per page) | ~138 MB | `scripts/download-models.sh` |

    scripts/download-models.sh ocr PPOCRv6Small     # into src/LayaSample.Rendering/ocr-models; the build copies them
    DocumentAnalysis__OcrModel=PPOCRv6Small dotnet run --project src/LayaSample.Api

Downloads are pinned and SHA-256 verified. If the configured models are missing, documents that need OCR get a 503
rather than being reported as unreadable.

## Docker
`docker-compose.yml` runs the API and the hardened renderer. Both images bake in their models (Laya in the API, OCR
in the renderer), so containers never download anything and run offline.

    docker compose up --build                                  # http://localhost:8080
    OCR_MODEL=PPOCRv6Small docker compose up --build           # multilingual OCR

The renderer is on an internal network with no route out, has a read-only file system (`/tmp` is a tmpfs), drops
all Linux capabilities, and has its own memory, CPU and process limits; it restarts automatically after a crash.
Both run as non-root users in the Production environment (so Scalar is off) and have a `HEALTHCHECK` on
`/health/ready`. The images are built from one `Dockerfile` (`--target api` / `--target renderer`); model downloads
use a BuildKit cache, so rebuilds don't fetch them again.

On Kubernetes, run the renderer as its own Deployment and Service (or as a sidecar the API reaches on localhost), with
a NetworkPolicy that denies its egress, `readOnlyRootFilesystem`, `allowPrivilegeEscalation: false`, dropped
capabilities and resource limits; set `DocumentAnalysis__RendererUrl` on the API.

## Test
    dotnet test

CI (`.github/workflows/ci.yml`) restores in locked mode (`packages.lock.json`), builds with warnings as errors,
runs the tests, fails on vulnerable packages or licences outside `.github/allowed-licenses.json`, and builds the
Docker image. After changing a package, run `dotnet restore` and commit the updated lock files.
