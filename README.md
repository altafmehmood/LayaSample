# LayaSample

Hotel guest feedback → dissatisfaction sentiment, using the open-weight
[Laya](https://huggingface.co/elbruno/laya-onnx) "System 1" decision model on ONNX Runtime, in-process via the
`ElBruno.LocalLLMs.Decisions` NuGet package (.NET 10).

## Run
    dotnet run --project src/LayaSample.Api --urls http://localhost:5080

The first start downloads the model (can take several minutes); `GET /health` returns 503 until it is loaded.

    curl -X POST localhost:5080/api/feedback/analyze -H 'content-type: application/json' \
      -d '{"text":"Room was filthy and the staff were rude."}'

Interactive API docs (Development only): http://localhost:5091/scalar/v1 (OpenAPI JSON at `/openapi/v1.json`).

Response: `dissatisfied`, `dissatisfactionScore` (P(dissatisfied), 0–1), `rating` (1–5 dissatisfaction),
`level` (None/Mild/Moderate/Severe), `primaryDriver` (null when unclear or not dissatisfied), `confidence`.

## Notes
- Model settings go under the `Laya` section of `appsettings.json` (`ModelRepository`, `CacheDirectory`, `IntraOpNumThreads`, `DecisionThreshold`).
- Confidences are **uncalibrated**; tune thresholds on your own labeled feedback before relying on them.

## Test
    dotnet test
