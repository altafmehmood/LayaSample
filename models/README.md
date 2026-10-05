# Local models

Model files used by `dotnet run` and copied into the Docker images. Everything here except this README is ignored
by git.

```
models/
  elbruno_laya-onnx/          Laya (~850 MB): config.json, rl_agent_config.json, model.onnx, model.onnx.data,
    tokenizer/                tokenizer/tokenizer.json, tokenizer/tokenizer_config.json
  ocr/v6/                     Optional PP-OCRv6 files (PP-OCRv5 Latin ships in the RapidOcrNet NuGet)
```

How files get here:

- `dotnet run --project src/LayaSample.Api` downloads Laya into `elbruno_laya-onnx/` on first start.
- `scripts/download-models.sh laya` and `scripts/download-models.sh ocr <OcrModel>` download the pinned versions.
- Or copy the folders from a machine that already has them.

`docker build` / `docker compose build` use these files when their SHA-256 matches the pinned version and download
only what is missing or different. A machine that cannot download (a proxy, TLS inspection, no internet) can build
offline once the files are here.
