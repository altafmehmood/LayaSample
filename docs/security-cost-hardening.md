# Security and cost hardening

Built 2026-10-11 (plan: track token usage and cost; scan uploads for malware, hidden content and prompt injection; stream
the document end to end; add a manual garak red-team run). This page says what each feature does, how it works, how to try
it, and what it does not do. The README has the short versions: the Features table and "Quickstarts by date".

## Why

- Two extractions on DOKS showed about $3 on the OpenAI dashboard and nothing in the repository could explain it. (The cause
  was most likely the opt-in evaluation test, which called the paid API whenever a key was on the machine; see feature 7.)
- Uploads are untrusted documents that reach parsers and a language model. Nothing scanned them, looked for content a reader
  cannot see, or looked for text aimed at the model.
- The document was copied into memory several times per request.

## 1. Usage and cost tracking

**What.** One SQLite row per model call: time, trace id, API-key fingerprint, file name, file size, kind, pages, images,
agent, model, attempt (1, or 2 for the generic-agent retry), input / cached / reasoning / output tokens, list-price cost,
price key, outcome, duration. A call that failed before the provider answered has no tokens; a cut-off or refused answer is
recorded with the tokens it was billed for. The extract response also returns `usage.cachedInputTokens`,
`reasoningTokens` and `costUsd`.

**API.** `GET /api/usage` (filters `from`, `to`, `file`, `model`, `apiKey` for admin keys; `limit` up to 10,000; `before` to
page) and `GET /api/usage/summary?groupBy=day|model|key`. `format=csv` or `Accept: text/csv` downloads a CSV with a byte
order mark; cells that start with `=`, `+`, `-` or `@` get a leading apostrophe because file names come from the caller.
A key sees only its own rows; keys in `Auth:AdminKeys` see all and are also accepted for extraction.

**How.**

| Piece | File |
|---|---|
| Records, options, pricing, fingerprint, file-name cleaning | `src/LayaSample.Api/Services/Usage/UsageModels.cs` |
| SQLite store (WAL, micro-dollar costs, filters, summaries) | `Services/Usage/UsageStore.cs` |
| Background writer (bounded queue, batches, drains on shutdown) | `Services/Usage/UsageWriter.cs` |
| Endpoints and CSV | `Services/Usage/UsageEndpoints.cs` |
| Recording each call, failed or billed | `Services/Documents/Extraction/DocumentExtractor.cs` (`RunAsync`) |
| Caller fingerprint (first 12 hex of the key's SHA-256, never the key) | `Services/ApiKeyAuthentication.cs` |

A store failure is logged and counted (`laya.usage.write_failures`) but never fails an extraction. Spend is on the
`laya.llm.cost` and `laya.extraction.tokens` metrics.

**Cost.** `Extraction:Pricing` in `appsettings.json` holds per-model tiers (USD per million tokens) and the date they were
read; cost is computed when the call is recorded, so a later price change does not rewrite history. Cached tokens are billed
at the cached rate out of the input count; the higher tier starts above 272,000 input tokens (the OpenAI page says "272K";
this reads it as 272,000). It is list price, not the invoice. A model with no price gets a null cost and counts on
`laya.usage.unpriced`.

| Setting | Default | |
|---|---|---|
| `Usage:DatabasePath` | `data/usage.db` (`/data/usage.db` in containers) | Empty switches tracking off; the endpoints answer 503 |
| `Auth:AdminKeys` | none | Keys that may read every caller's rows |
| `Extraction:Pricing` | the two configured models, as of 2026-10-11 | Update when OpenAI changes prices |

**Kubernetes.** `api.usage.persistence` creates a 1 GiB PVC (`laya-usage`, `helm.sh/resource-policy: keep`) mounted at
`/data`, with `fsGroup: 1654` so the non-root user can write. The Deployment already replaces its pod in place, so the
ReadWriteOnce volume is free when the new pod starts. Keep `api.replicas` at 1.

**Try it.**

    dotnet user-secrets set "Extraction:ApiKey" "sk-..." --project src/LayaSample.Api       # (paid)
    dotnet run --project src/LayaSample.Api
    curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf" localhost:5091/api/documents/extract
    curl "localhost:5091/api/usage?format=csv" -o usage.csv
    curl "localhost:5091/api/usage/summary?groupBy=model"

**Not done.** Retries inside the OpenAI SDK are invisible (only the final response reports tokens). Reasoning and image
tokens are as the provider reports them; compare one call with the dashboard before trusting the totals. There is no
retention policy.

**Tests.** `UsageTests` (store, pricing, CSV, endpoints, scoping, writer), and the recording tests in `ExtractionTests`.

**Updated 2026-10-12.** The writer lost queued records when it was stopped before its loop first ran. On .NET 10 the host
schedules `ExecuteAsync` rather than running it inside `StartAsync`, so `StopAsync` could cancel it before it started (its task
ends `Canceled`) and the records waiting in the queue were never written. CI caught it as
`The_writer_stores_queued_records_on_shutdown` failing with "stored 0 of 250" on one run and passing on the next of the same
commit. `UsageWriter.StopAsync` now drains the queue itself when the loop never ran. Regression test:
`Records_queued_before_the_writer_loop_first_runs_are_still_stored_on_shutdown` (fails on the old code, 50 start/stop cycles).

## 2. Malware scanning (ClamAV)

**What.** Before anything parses an upload it is streamed to a ClamAV daemon. Infected: 422 `document rejected: malware
detected` (the signature goes to the log and the trace, not the response). No verdict (daemon down, timeout, size refused):
503 with `Retry-After` while `Security:Malware:FailMode` is `closed` (the default); accepted unscanned with a warning when
`open`. Readiness reports the scanner `Degraded` while it does not answer.

**How.** `Services/Security/MalwareScanner.cs`: a small clamd client speaking `zINSTREAM` (length-prefixed 64 KB chunks, so
memory per scan is one chunk, whatever the document's size). The pipeline calls it first, in `DocumentPipeline.PrepareAsync`.
ClamAV runs as its own container (`clamav/clamav:1.5.4_base-debian13-slim`, multi-arch), non-root (uid 1000) with a read-only
root file system: compose service `clamav`, chart template `templates/clamav.yaml`. ClamAV is GPL-2.0; the exception is
recorded in `CLAUDE.md` (separate process, not linked or bundled).

| Setting | Default | |
|---|---|---|
| `Security:Malware:Provider` | `clamd` | `none` in Development (no daemon with `dotnet run`) |
| `Security:Malware:Host`, `Port` | `clamav`, 3310 | |
| `Security:Malware:FailMode` | `closed` | `open` lets uploads through while there is no verdict |
| `Security:Malware:TimeoutSeconds` | 60 | |
| `malware.clamd.*` (chart) | enabled | image, tag, `failMode`; **no `resources`** |

**Before deploying.** ClamAV used about 1 GiB idle here; its documentation advises 3 to 4 GiB because a reload can double
it. The pod has no requests or limits by choice, so it is `BestEffort` and is evicted first under pressure (uploads then
answer 503). The default 1 vCPU / 2 GB node cannot hold it: resize the node pool first, or `helm --wait` fails the deploy.
Its first start downloads the signature database (a minute or two, 15 minutes allowed) and again whenever the pod is
replaced. clamd's default stream limit is 25 MB, above the 20 MB upload limit.

**Try it.**

    docker compose up -d clamav
    printf 'X5O!P%%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*' > eicar.txt
    Security__Malware__Provider=clamd Security__Malware__Host=localhost dotnet run --project src/LayaSample.Api
    curl -F "file=@eicar.txt" localhost:5091/api/documents/analyze          # 422
    docker compose stop clamav                                              # uploads now answer 503

**Not done.** It finds known malware by signature; it does not detect an exploit aimed at a parser. That risk is limited by
isolating PDFium, ImageMagick and OCR in the renderer.

**Tests.** `MalwareScannerTests` (a fake clamd server speaking the protocol, plus the pipeline); `ClamdIntegrationTests`
against a real daemon (**opt-in**: `CLAMD_HOST=localhost`, then `-trait "Category=Integration"`).

## 3. Active content, hidden text and prompt-injection checks

**What.** Findings are `RiskFinding(kind, severity, page, detail)`, listed in the `risks` field of the analyze and extract
responses. `Block` findings refuse the request with 422 while `Security:OnFinding` is `reject` (the default); with `flag` they
are reported and processing goes on. `Info` findings are only reported. The detail never repeats the document's text.

| Where it looks | Finds | Severity |
|---|---|---|
| PDF structure (`Pdf/PdfRiskDetectors.cs`) | Launch actions; RichMedia and 3D annotations; JavaScript and form scripts; embedded programs (`Pdf/DocumentDetectors.cs`) | Launch, rich media, programs: Block. JavaScript: Info (fillable forms use it) |
| PDF pages | Hidden text: 20 or more characters that are under 1.5 pt, near-white, or off the page | Block. Skipped on a scan with a text layer over a full-page image |
| Office packages (`Services/Security/OfficeInspector.cs`) | VBA macros, ActiveX, embedded programs, linked templates and objects; embedded objects and workbook links | Macros, ActiveX, programs, linked template/object: Block. Embedded object, workbook link: Info |
| Text the model will read (`Services/Security/InjectionPatterns.cs`, `RiskScanner.cs`) | Phrases aimed at the model: "ignore the previous instructions", "you are now an AI", system-prompt requests, chat-template tokens, role markers followed by an instruction | Block. A literal `<document>` tag: Info |
| The model's answer (`RiskScanner.CheckAnswer`) | The same phrases echoed into a field, its quote or `otherFields` | Block (422 `extraction rejected`) |

Structure findings are enforced after classification and before preparation, so a file with active content or hidden text is
refused without going to the renderer; text findings after preparation (OCR included), before the model call. The
`<document>` block that wraps the text for the model has its closing tag made inert inside the text
(`InjectionPatterns.Neutralise`), so a document cannot close the block early.

The phrases are tuned for precision, because a hit rejects the upload: "System: LoanPro", "Developer: Acme Homes" and
"disregard my previous instructions regarding the wire" pass. All sample documents have no blocking finding, including the
scans and faxes read by the real OCR.

| Setting | Default | |
|---|---|---|
| `Security:OnFinding` | `reject` | `flag` reports and carries on; also how the red team tests the model itself |

**Try it.**

    dotnet run --project tests/LayaSample.Tests -c Release -- -class LayaSample.Tests.RiskTests
    Security__OnFinding=flag dotnet run --project src/LayaSample.Api
    REAL_OCR=1 dotnet run --project tests/LayaSample.Tests -c Release -- -class LayaSample.Tests.RealOcrRiskTests   # (opt-in)

No hostile sample files are checked in; the tests build them (`DocumentFixtures.PdfWithHiddenText` and friends).

**Not done.** The text check is a phrase list: a rephrased attack passes it. A scan's invisible text layer is not compared
with what the image shows. Page-level `/AA` actions are not inspected (PdfPig does not expose them); the catalog's open
action and the actions on annotations and widgets are. Treat extracted values as untrusted data downstream.

**Tests.** `RiskTests` (phrases, each detector on generated PDFs and Office packages, the policy, the endpoints, the sample
corpus), `RealOcrRiskTests` (**opt-in**).

## 4. End-to-end streaming

**What.** The upload is one seekable stream, read in place by the scan, the classifier, the preparer and the hand-off to the
renderer. It is never copied into memory. ASP.NET already spools a multipart file above 64 KB to a temporary file (`/tmp`,
a writable volume in both containers).

**How.** `DocumentPipeline` uses `IFormFile.OpenReadStream()`; `IPageRasterizer` and `RenderJob` take a `Stream`;
`RemotePageRasterizer` sends `StreamContent`; the renderer renders from the form file's stream and spools `/inspect` bodies to
a temporary file; `LeaveOpenStream` stops libraries that dispose their input from closing it; PdfPig and PDFtoImage read the
stream directly. `StreamBytes` is gone.

**Still in memory.** Files embedded in a PDF (bounded by `MaxAttachments`), rendered page images (at most `MaxPages`) and
their text, the libraries' own structures, and the signed ranges of a signed PDF.

**Tests.** `StreamingTests` classifies and prepares a 19 MB PDF and asserts the thread allocates under a quarter of its size
(PdfPig allocates about 30 MB once on first use, so the test warms it up first).

## 5. Manual red team with garak

**What.** [garak](https://github.com/NVIDIA/garak) 0.17.0 attacks the whole extraction agent. `redteam/GarakShim` turns each
garak prompt into a PDF (the remarks of a loan application), uploads it to `/api/documents/extract` with the API key (only the
shim holds it) and returns `HTTP <status> <body>` as the answer, so a 422 counts as "attack blocked". Run it twice: with the
defences on, and with `LAYA_ON_FINDING=flag` to test the model alone.

**Files.** `redteam/run.sh`, `GarakShim/` (a project in the solution, covered by `GarakShimTests`), `garak-rest.json`,
`garak-smoke.yaml`, `garak-full.yaml`, `requirements.txt` (pinned), `README.md`. Manual CI job: `redteam` (Actions > CI > Run
workflow).

**Guard rails.** The shim refuses a non-local target without `--allow-remote`, stops after `--max-requests`, and sends
`User-Agent: laya-garak/<run id>` so a run can be found in Honeycomb.

**Try it.**

    OPENAI_API_KEY=sk-... LAYA_API_KEY=$(openssl rand -base64 32) docker compose up -d --build      # (paid)
    export LAYA_API_KEY=<the same key>
    redteam/run.sh --smoke

**Verified.** The whole chain ran against local stubs (an echoing stub failed at 100% attack success, a refusing stub passed
5 of 5 on two probe families). It has **not** been run against the real API.

**Updated 2026-10-11.** The default probe list named `probes.xss`, which garak 0.17.0 no longer has (it matches no probes, so
the markdown-exfiltration attacks were never run); it is now `probes.web_injection`. Prompt counts, measured without calling
the model: about 7,470 for the default list, over the shim's 5000 `--max-requests`, so use `--max-requests 8000` for a
complete run. Estimated model cost of a complete run: $4 to $8 (an estimate from list prices, not a measurement).

**Not done.** garak's detectors are string matches on the answer, so a pass is not proof of safety and a fail can be the
model quoting the injected text as data. The shim writes Latin-1 text PDFs only; no image or scan channel yet. No pass/fail
gate: a threshold needs a baseline from the first real runs (a table is waiting in `redteam/README.md`).

## 6. Production telemetry through the in-cluster collector

`deploy/values-prod.yaml` now sets `telemetry.endpoint`, `existingSecret: laya-otel` and `collector.enabled: true`, and the
CI deploy job creates the `laya-otel` Secret from the `HONEYCOMB_API_KEY` GitHub secret. The API and the renderer send OTLP
to the collector, which alone holds the key; the renderer's NetworkPolicy lets it reach only the collector and DNS. Not yet
confirmed on the cluster: the collector image pull, and traces arriving in Honeycomb.

## 7. The paid evaluation is opt-in

`ExtractionEvaluationTests` calls the OpenAI API for 39 documents. It is skipped unless `RUN_EXTRACTION_EVAL=1`, whatever key
is on the machine, so a plain test run never spends money (the suite takes about 10 seconds). The manual `extraction-eval` CI
job sets the flag.

    RUN_EXTRACTION_EVAL=1 dotnet run --project tests/LayaSample.Tests -c Release -- -trait "Category=Evaluation"     # (paid)

## Checklist before merging to `main`

1. Resize the DigitalOcean node pool for ClamAV (8 GB is comfortable), or set `malware.clamd.enabled: false`.
2. Replace the revoked OpenAI key: the `OPENAI_API_KEY` GitHub secret (the deploy job rewrites the cluster Secret) and the
   local user secret. Prefer a dedicated OpenAI project with a monthly budget.
3. Add the `HONEYCOMB_API_KEY` GitHub secret if it is not set.
4. After the deploy: `kubectl -n laya get pods` shows `laya-clamav`, `laya-otel-collector`, the API and the renderer Ready;
   an EICAR upload answers 422; one extraction appears in `GET /api/usage`; compare its cost with the OpenAI dashboard.

## Verification summary

Full suite: 363 tests, 6 skipped (the paid evaluation, four ClamAV integration tests, the real-OCR check). Built with
`-warnaserror`; `helm lint` on the default, prod and CI value sets; `docker compose config`; the licence check. ClamAV was run
for real (non-root, read-only) and tested through the real API: EICAR 422, clean PDF 200, daemon stopped 503 with
`Retry-After`. Not run: a Kubernetes install, the DOKS deploy, a PVC on DigitalOcean, garak against the real API, and any
comparison of recorded cost with the dashboard.
