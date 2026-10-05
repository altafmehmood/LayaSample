# LayaSample

Hotel guest feedback → dissatisfaction sentiment, using the open-weight
[Laya](https://huggingface.co/elbruno/laya-onnx) "System 1" decision model on ONNX Runtime, in-process via the
`ElBruno.LocalLLMs.Decisions` NuGet package (.NET 10). It also classifies uploaded documents (PDF, Excel, Word,
TIFF/fax, PNG, JPEG), prepares them for an AI agent (page images, markdown, OCR text, form data) and routes them.

## Run
    dotnet run --project src/LayaSample.Api

The API listens on http://localhost:5091. The first start downloads the Laya model (~850 MB, can take several
minutes); `GET /health` returns 503 until it is loaded.

    curl -X POST localhost:5091/api/feedback/analyze -H 'content-type: application/json' \
      -d '{"text":"Room was filthy and the staff were rude."}'

Interactive API docs (Development only): http://localhost:5091/scalar/v1 (OpenAPI JSON at `/openapi/v1.json`).

Response: `dissatisfied`, `dissatisfactionScore` (P(dissatisfied), 0–1), `rating` (1–5 dissatisfaction),
`level` (None/Mild/Moderate/Severe), `primaryDriver` (null when unclear or not dissatisfied), `confidence`.

Documents go to `POST /api/documents/analyze`; see [docs/samples](docs/samples/README.md) for sample files and the
expected results.

## Notes
- Model settings go under the `Laya` section of `appsettings.json` (`ModelRepository`, `ModelPath`, `CacheDirectory`, `IntraOpNumThreads`, `DecisionThreshold`). `ModelPath` loads a local copy and skips the download.
- Confidences are **uncalibrated**; tune thresholds on your own labeled feedback before relying on them.

## OCR models
Scanned pages, faxes and images are read with [RapidOcrNet](https://github.com/BobLd/RapidOcrNet), which runs
PaddleOCR models on ONNX Runtime. Each model set has three networks: a detector that finds text lines, a classifier
that flips upside-down lines, and a recognizer that reads characters from a dictionary file. Pick one with
`DocumentAnalysis:OcrModel`:

| `OcrModel` | Languages | Size | Source |
|---|---|---|---|
| `PPOCRv5Latin` | Latin script | ~14 MB | Bundled in the NuGet package |
| `PPOCRv6Tiny` | Multilingual (Latin, CJK and more) | ~6 MB | `scripts/download-models.sh` |
| `PPOCRv6Small` | Multilingual | ~31 MB | `scripts/download-models.sh` |
| `PPOCRv6Medium` (default) | Multilingual, most accurate, slowest (~4x v5 per page) | ~138 MB | `scripts/download-models.sh` |

    scripts/download-models.sh ocr PPOCRv6Medium    # once, before the first run; the build copies them
    DocumentAnalysis__OcrModel=PPOCRv5Latin dotnet run --project src/LayaSample.Api   # or skip it: bundled, faster

Downloads are pinned and SHA-256 verified. If the configured models are missing, documents that need OCR get a 503
rather than being reported as unreadable.

## Docker
The image bakes in every model, so the container never downloads anything and can run offline.

    docker build -t laya-sample .                                       # PP-OCRv6 medium OCR (multilingual)
    docker build -t laya-sample --build-arg OCR_MODEL=PPOCRv5Latin .    # smaller and faster, Latin only
    docker run --rm -p 8080:8080 laya-sample

The image is about 1.3 GB of content (850 MB of it is the Laya weights, 140 MB the OCR models; Docker Desktop
reports roughly double, counting compressed and unpacked layers). It runs as a non-root user in the Production environment, so Scalar is off.
Model downloads use a BuildKit cache, so rebuilds don't fetch them again.

## Test
    dotnet test
