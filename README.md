# LayaSample

Lending-document intelligence on .NET 10. Upload a loan application, UCC-1, UCC-3 or loan and security agreement as a
PDF, image or Office file. The service works out how to read it (text layer, rendered page image, OCR for scans and
faxes), prepares it for an AI agent, and extracts the borrower, lender, collateral and every box on the UCC forms with
OpenAI vision and text models.

- **`POST /api/documents/analyze`** classifies a document and prepares it for an agent: page images, markdown, OCR text,
  form values. No AI model, no cost.
- **`POST /api/documents/extract`** reads the document with an OpenAI model and returns structured data with page
  citations. Needs an API key.
- **A 39-PDF lending corpus** with ground truth, in every rendering a bank sees: digital, fillable form, scan, fax and
  mixed.
- **Helm chart and CI** that deploy the API and an isolated renderer to Kubernetes behind the Gateway API.
- `POST /api/feedback/analyze` is the original sample: hotel feedback sentiment with the Laya decision model.

## Quick start

You need the .NET 10 SDK. Steps 1 to 3 need no Docker, API key or cluster.

1. **Start the API.** The first run downloads the Laya model (~850 MB, several minutes); the OCR models ship with the app.

       dotnet run --project src/LayaSample.Api

2. **Wait until it is ready.** `503` means the models are still loading, `200` means ready.

       curl -i localhost:5091/health/ready

3. **Analyse a sample document.** This is a fax-quality UCC-1 from the [lending corpus](#lending-corpus). The response
   reports `classification.kind` (`FaxPdf`) and the page image and OCR text prepared for an agent. `dispatch=false`
   skips the (stub) agent and `includeData=false` leaves the page images out of the output.

       curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf" \
         "localhost:5091/api/documents/analyze?dispatch=false&includeData=false"

   Or open Scalar at http://localhost:5091/scalar/v1 and use "Try it" on any endpoint.

4. **Extract data with AI (optional).** This sends the document to OpenAI and costs money, so it needs your own key.
   Store it once as a [user secret](https://learn.microsoft.com/aspnet/core/security/app-secrets) (kept in your user
   profile, outside the repository, and read only when running in Development), then restart the API:

       dotnet user-secrets set "Extraction:ApiKey" "sk-..." --project src/LayaSample.Api
       dotnet run --project src/LayaSample.Api
       curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf" localhost:5091/api/documents/extract

   Locally the endpoint needs no access key; see [Authentication](#authentication) for deployments.

   Remove it with `dotnet user-secrets remove "Extraction:ApiKey" --project src/LayaSample.Api`. The `OPENAI_API_KEY`
   environment variable works too (a configured `Extraction:ApiKey` wins). Without a key the endpoint answers `503` and
   nothing else changes. The models are `Extraction:VisionModel` and `Extraction:TextModel` in `appsettings.json`;
   override them with the `Extraction__VisionModel` and `Extraction__TextModel` environment variables.

5. **Run the tests.** They are offline: OCR and the model provider are faked.

       dotnet run --project tests/LayaSample.Tests -c Release

   CI runs them with `dotnet test`. The AI accuracy evaluation is separate and opt-in; see [AI extraction](#ai-extraction).

To run the API with the isolated renderer, as in production, use `docker compose up --build` (http://localhost:8080);
see [Docker](#docker).

## API

| Endpoint | Purpose |
|---|---|
| `POST /api/documents/analyze` | Multipart `file`. Classify and prepare a document for an agent. Query: `dispatch` (default true), `includeData` (default true). |
| `POST /api/documents/extract` | Multipart `file`. Extract structured data with OpenAI. **Needs the `X-Api-Key` header** (see [Authentication](#authentication)); 503 until a model key is configured. |
| `POST /api/feedback/analyze` | JSON `{ "text": "..." }`. Hotel feedback sentiment (the original sample). |
| `GET /health/live`, `/health/ready` (also `/health`) | Liveness; readiness (503 while models load, 200 `Degraded` if one failed for good or the renderer is unreachable). |
| `GET /scalar/v1`, `/openapi/v1.json` | Interactive API docs and the OpenAPI document. |

Errors are problem details (`application/problem+json`).

### Authentication

Only `POST /api/documents/extract` needs a key, because it is the endpoint that spends money (OpenAI bills per call);
the other endpoints are open, so put the service behind an authenticating gateway before exposing those to an
untrusted network. Callers send the key in a header:

    curl -H "X-Api-Key: $LAYA_API_KEY" -F "file=@document.pdf" https://api.example.com/api/documents/extract

- **Configure it** with `Auth:ApiKeys` (an array, so a key can be rotated without downtime): the environment variables
  `Auth__ApiKeys__0`, `Auth__ApiKeys__1`, a user secret, or in Kubernetes a Secret (see [deploy/README.md](deploy/README.md)).
  Each key must be at least 24 characters with no spaces, or the app refuses to start. Generate one with
  `openssl rand -base64 32`.
- **It fails closed.** In any environment except Development, extraction answers `503` until a key is configured. In
  Development it is open when no key is set, so the quick start needs no setup; set one to try it locally:
  `dotnet user-secrets set "Auth:ApiKeys:0" "<key>" --project src/LayaSample.Api`.
- **The check runs first.** A request with a missing or wrong key gets `401` before its upload is read, before it can
  take a slot in the queue and before anything reaches OpenAI. Keys are compared in constant time and never logged;
  rejections are counted on the `laya.auth.rejections` metric.
- **Scalar** shows an authentication field for `extract` (the docs page itself stays public). Enter the key there and
  "Try it" sends the header.
- **A shared key is a gate, not a budget.** Anyone holding it can spend until OpenAI stops them, so also put this
  service's OpenAI key in its own OpenAI project with a monthly budget; that is the only limit that holds if a key leaks.

## Document analysis

The API decides how to prepare each page from its content; there is no strategy parameter. It reports the choice in
`classification.strategy` (`PerPage` when pages differ) and `classification.pages[].strategy`:

| Page content | Prepared as |
|---|---|
| Usable text layer | Markdown |
| Forms, annotations | Page image, plus the field values as JSON |
| Scans, faxes, broken text | Page image plus OCR text |
| Layout-heavy pages (tables, drawings) | Page image plus the text layer |
| Dynamic XFA forms | The machine-readable form data only |

Supported inputs are PDF (including signed and tampered ones), Excel, Word, TIFF/fax, PNG and JPEG. Each classified
document is routed (`classification.route`) to an agent: `form-agent`, `vision-agent`, `text-agent` or
`spreadsheet-agent`. The analyze endpoint's agents are still stubs; extraction is a separate endpoint. See
[docs/samples](docs/samples/README.md) for small samples of every path and their expected results.

## AI extraction

`POST /api/documents/extract` reads a loan application, UCC-1, UCC-3 or loan and security agreement and returns:

- **Parties:** `borrower` (the debtor on a UCC filing: name, address, EIN) and `lender` (the secured party).
- **Document fields:** application number, date, amount and term; UCC file number, filing date, filing office and
  jurisdiction; collateral; agreement date, principal, loan type and governing law.
- **Every box on the UCC-1 and UCC-3 national forms as its own attribute.** UCC-1: the debtor's organization type,
  jurisdiction and ID, the additional debtor (2), the alternative designation (5), the real estate records (6) and
  search report (7) boxes, the filer reference (8), and the contact and acknowledgment block. UCC-3: the real estate
  records box (1b); the termination (2), assignment (3), continuation (4) and party information change (5) boxes; the
  assignee, record party and changed party names; the collateral change (8); the authorizing party (9).
- **`otherFields`:** anything else printed on the document, as generic `{ name, value, page }` pairs.

Every field carries the `page` it came from, a short verbatim `evidence` quote and a `confidence`. Checkboxes are
`true`, `false`, or `null` when they cannot be told; absent values are `null`. Abridged response (values illustrative):

    {
      "fileName": "redwood-mechanical-ucc1-fax.pdf",
      "documentType": "Ucc1", "detectedBy": "keywords", "agent": "ucc1-agent", "model": "...",
      "extraction": {
        "borrower": { "name": { "value": "Redwood Mechanical Supply, LLC", "page": 1, "evidence": "...", "confidence": "High" }, ... },
        "lender":   { "name": { "value": "Cascade Harbor Bank, N.A.", ... }, ... },
        "ucc1": {
          "fileNumber": { "value": "2026-004417-8", "normalized": "2026-004417-8", ... },
          "filingDate": { "value": "02/12/2026", "normalized": "2026-02-12", ... },
          "realEstateRecords": { "checked": false, "page": 1, ... },
          "searchReport": { "allDebtors": { "checked": true, ... }, "debtor1": { "checked": false, ... }, ... },
          ...
        },
        "otherFields": [ { "name": "County", "value": "Multnomah", "page": 1 } ]
      },
      "usage": { "inputTokens": 3120, "outputTokens": 640 },
      "warnings": []
    }

**How it works.** The text already extracted from the first page picks an agent: loan application, UCC-1, UCC-3,
security agreement, or a generic one that also reports the document type (`detectedBy` says which). Matching ignores
case, spacing and punctuation, so OCR noise rarely breaks it. If a type agent finds almost nothing, the generic agent
tries once and a warning says so. Each agent is a prompt plus a strict JSON schema in code, run as one stateless Chat
Completions call; nothing is configured on OpenAI's side. Documents with page images (scans, faxes, forms, complex
layouts) go to `Extraction:VisionModel`, text-only documents to `Extraction:TextModel`. After the call, page numbers
are checked against the document and quotes against the page text; quotes that cannot be found raise a warning.

The code depends only on `IChatClient` (Microsoft.Extensions.AI), so another provider means a new client in
`ExtractionClientFactory` and an error mapper for its exceptions.

**Settings** (the `Extraction` section; in Kubernetes see [deploy/README.md](deploy/README.md)):

| Setting | Default | Effect |
|---|---|---|
| `ApiKey` | none | Falls back to `OPENAI_API_KEY`. Without one the endpoint answers 503. |
| `VisionModel`, `TextModel` | in `appsettings.json` | OpenAI models for documents sent as page images / as text |
| `MaxImagePages` | 10 | Page images sent per document; later pages go as text only, with a warning |
| `ImageDetail` | `high` | OpenAI image detail: `low`, `high` or `auto` |
| `TimeoutSeconds`, `MaxRetries` | 120, 2 | One provider call; the SDK retries (honouring `Retry-After`) |
| `MaxOutputTokens`, `MaxTextChars` | 4000, 200000 | Output cap; text cap per document |
| `MaxConcurrentExtractions`, `MaxQueuedExtractions` | 4, 10 | Extractions at once and waiting; beyond that, 503 with `Retry-After` |

Provider errors map to HTTP statuses: rate limiting is 503 with `Retry-After`, a rejected key is 503, a document the
model cannot take is 422, a failed or unreadable answer is 502, a provider timeout is 504. Logs hold counts, kinds,
models and token usage, never file names, values or quotes, and not the provider's error text, which can echo part of
the API key. Metrics are on the `LayaSample.Documents` meter.

**Privacy.** The document's pages (images and text) are sent to OpenAI. Under OpenAI's API terms the data is not used
for training and requests are not stored (`store` is false), but abuse monitoring may retain them for up to 30 days
unless the organisation has Zero Data Retention. Use a dedicated OpenAI project for this service with a monthly budget
(the hard stop on spend; the API key above only decides who may call), and get sign-off before sending borrower data.

**Accuracy evaluation.** This scores the model against the corpus manifest and prints accuracy by field and by
rendering, the keyword detector's hit rate, and every miss. It calls the paid API for 39 documents and skips itself
without a key (`OPENAI_API_KEY` or the `Extraction:ApiKey` user secret):

    dotnet run --project tests/LayaSample.Tests -c Release -- -trait "Category=Evaluation" -showLiveOutput

Optional environment variables: `EVAL_VISION_MODEL`, `EVAL_TEXT_MODEL` (to compare models), `EVAL_FILTER` (a substring
of the file name, e.g. `-fax`), `EVAL_MIN_ACCURACY` (default 0.9). In CI it is the manual `extraction-eval` job.

It scores every fact in the manifest: the parties, the key fields and every UCC-1 and UCC-3 box. A box printed empty
must come back `null` (a value invented for an empty box fails), and a checkbox must be exactly `true` or `false`
(`null`, "could not tell", counts as a miss). Each manifest fact has a scorer in `ExtractionScoring`, and
`ExtractionScoringTests` fails if one does not, so adding a fact to the generator cannot go unscored.

## Lending corpus

`docs/samples/pdf/corpus/` holds 39 PDFs (99 pages, 4.8 MB) of lending documents for three fictional deals, to test
classification, OCR and extraction on realistic inputs. Everything is invented: the borrowers (Redwood Mechanical
Supply, Blue Heron Dental Partners, Sunbelt Freight Logistics) and the lender, EINs (unissued `00-` prefix), phone
numbers (reserved 555-01xx range) and signatures.

| Document | Files | Pages | Notes |
|---|---|---|---|
| Commercial loan application | 12 | 2 | |
| UCC-1 financing statement | 12 | 1 | Follows the national form (rev. 05/22/02), redrawn |
| UCC-3 amendment | 3 | 1 | National amendment form (rev. 07/01/23): an assignment, a termination and a collateral amendment, one per deal |
| Loan and security agreement | 12 | 5 | Running text with a schedule and signature pages |

Each file is named `<deal>-<document>-<rendering>.pdf`, and the rendering decides how the API has to read it:

| Rendering | Files | What it is | Expected `classification.kind` |
|---|---|---|---|
| `digital` | 9 | Vector PDF with a text layer | `TextPdf` (agreement), `LayoutPdf` (forms) |
| `form` | 7 | Fillable form; the values are in fields, not in the text | `FormPdf` |
| `scan` | 10 | Image-only pages with skew, blur, shading and dust | `ScannedPdf` |
| `fax` | 10 | 1-bit CCITT G4 at 204 dpi with a fax header and dropped lines | `FaxPdf` |
| `mixed` | 3 | Agreement with digital first pages and scanned signature pages | `MixedPdf` |

`manifest.json` in the same folder lists every file with its rendering, expected kind, page count and the facts it
contains, which is the ground truth for checking extraction. Facts by document type: all have `debtorName` and
`lender`; applications add `ein`, `amountRequested`, `termMonths`, `applicationDate`, `applicationNo`; agreements add
`agreementDate`, `principal`, `loanType`, `governingLaw`. UCC filings carry every box on the form:

- **UCC-1:** `fileNumber`, `filingDate`, `filingOffice`, `collateral`; `debtorEntityType`, `debtorJurisdiction`,
  `debtorOrganizationId`; `additionalDebtorName`, `alternativeDesignation`; the checkboxes `realEstateRecords`,
  `searchAllDebtors`, `searchDebtor1`, `searchDebtor2`; `filerReference`, `contactName`, `acknowledgmentTo`.
- **UCC-3:** `initialFileNumber`, `fileNumber`, `filingDate`, `action`; the checkboxes `realEstateRecords`,
  `termination`, `assignment`, `continuation`, `partyInformationChange`, `authorizedByDebtor`; `partyChangeAffects`,
  `partyChangeType`, `assigneeName`, `recordPartyName`, `changedPartyName`, `collateralChange`,
  `collateralChangeDescription`, `authorizingPartyName`, `filerReference`, `contactName`, `contactEmail`,
  `acknowledgmentTo`.

Checkboxes are `"true"` / `"false"`, and `""` means the box is printed empty (the additional debtor, for example, is
empty on every UCC-1). An entry:

    {
      "File": "redwood-mechanical-ucc1-digital.pdf", "DocumentType": "ucc1", "Rendering": "digital",
      "ExpectedKind": "LayoutPdf", "Pages": 1,
      "Facts": { "debtorName": "Redwood Mechanical Supply, LLC", "lender": "Cascade Harbor Bank, N.A.",
                 "fileNumber": "2026-004417-8", "filingDate": "02/12/2026",
                 "filingOffice": "Oregon Secretary of State, UCC Division", "collateral": "Equipment, Inventory, Accounts" }
    }

To check that the API classifies every file as the manifest expects (with the API running), print the kind next to the
file name and compare it with `ExpectedKind`:

    for f in docs/samples/pdf/corpus/*.pdf; do
      printf '%s: ' "$(basename "$f")"
      curl -s -F "file=@$f" "localhost:5091/api/documents/analyze?dispatch=false&includeData=false" \
        | python3 -c "import json,sys; print(json.load(sys.stdin)['classification']['kind'])"
    done

The files are generated. The content is deterministic (each file seeds its scan noise from its name), so regenerating
gives the same pages and the same manifest; only the fax PDFs differ byte for byte, because ImageMagick stamps them
with a creation time. Restore them with `git checkout` if you only changed the manifest:

    dotnet run docs/samples/generate-corpus.cs

Details and the other, smaller samples are in [docs/samples](docs/samples/README.md).

## Architecture

Untrusted PDFs and images are parsed by native C/C++ libraries (PDFium, ImageMagick, Skia, ONNX Runtime for OCR).
Those run only in a separate **renderer** service, so a malicious or malformed file that crashes or takes over a
parser cannot take down the API or reach its data:

| | `LayaSample.Api` | `LayaSample.Renderer` |
|---|---|---|
| Does | File-type detection, PDF classification and text (PdfPig, managed), Office conversion, form data, signatures, AI extraction, Laya, routing | Page rendering (PDFium), image decoding (ImageMagick), PNG encoding, OCR |
| Sees untrusted bytes in native code | No | Yes, isolated: no network egress, read-only file system, no capabilities, own memory/CPU limits |
| Talks to | The renderer, and OpenAI for extraction | Nothing outside; only the API may reach it |

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
| `RequestTimeoutSeconds` | 120 | Waiting plus analysis (and extraction); then 504 |

Invalid settings stop the app at startup. `?includeData=false` leaves base64 page images out of the response.

Durations per stage and documents by kind and outcome are published on the `LayaSample.Documents` meter
(`dotnet-counters monitor -n LayaSample.Api --counters LayaSample.Documents`).

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

    scripts/download-models.sh ocr PPOCRv6Small     # into models/ocr/v6; the build copies them next to the binary
    DocumentAnalysis__OcrModel=PPOCRv6Small dotnet run --project src/LayaSample.Api

Downloads are pinned and SHA-256 verified. If the configured models are missing, documents that need OCR get a 503
rather than being reported as unreadable. Local models live in [`models/`](models/README.md) (ignored by git).

## Docker

`docker-compose.yml` runs the API and the hardened renderer. Both images bake in their models (Laya in the API, OCR
in the renderer), so containers never download anything and run offline. The build itself takes models from the local
[`models/`](models/README.md) folder when their SHA-256 matches the pinned version and downloads only what is missing,
so a machine that cannot download (a proxy or TLS inspection blocking curl) can build once the files are there: run
the API once with `dotnet run`, or copy `models/` from another machine.

    docker compose up --build                                  # http://localhost:8080
    OCR_MODEL=PPOCRv6Small docker compose up --build           # multilingual OCR
    OPENAI_API_KEY=sk-... LAYA_API_KEY=$(openssl rand -base64 32) docker compose up --build   # enables /api/documents/extract

Containers run in Production, where user secrets are not read, so the keys come from the environment, and extraction
answers 503 without `LAYA_API_KEY` (callers send it as `X-Api-Key`). The renderer
is on an internal network with no route out, has a read-only file system (`/tmp` is a tmpfs), drops all Linux
capabilities, and has its own memory, CPU and process limits; it restarts automatically after a crash. Both run as
non-root users and have a `HEALTHCHECK` on `/health/ready`. The images are built from one `Dockerfile`
(`--target api` / `--target renderer`); model downloads use a BuildKit cache, so rebuilds don't fetch them again.

## Kubernetes

[`deploy/`](deploy/README.md) has a Helm chart that deploys both services to a DigitalOcean Kubernetes cluster: the API
is public through the Gateway API with a Let's Encrypt certificate, the renderer is reachable only from the API and
has no egress. The CI `deploy` job runs it on every push to `main`, with the cluster credentials, the Let's Encrypt
e-mail, the OpenAI key and the API access key (`LAYA_API_KEY`) taken from GitHub secrets. The chart, the secrets and
the cluster setup are described in [deploy/README.md](deploy/README.md).

## Telemetry (Honeycomb)

Both services send traces, metrics and logs over OpenTelemetry (OTLP) when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; unset,
they export nothing. In Kubernetes and in compose the renderer has no route out, so both services send to a small
collector that alone talks to Honeycomb (see below), and the renderer's spans join the API's trace.

**What you get.** One extract request is a single trace: the HTTP request (tagged `laya.file.bytes`, `laya.document.kind`,
`laya.document.strategy`, `laya.document.pages`, `laya.outcome` = `ok` / `rejected` / `renderer_unavailable` / `rate_limited` /
`auth_invalid` / `extraction_failed`...) with children `laya.buffer`, `laya.classify`, `laya.prepare`, `laya.render` (the
call to the renderer, with `renderer.render` / `renderer.page` / `renderer.ocr` beneath it), `laya.extraction` and one
`laya.extract` per model call. `laya.extract` carries the system prompt, the document text sent (page images appear only
as `[image ..., N bytes]`), the model's answer (`laya.extraction.result`), token counts, and retry events. This is a
technology demonstrator, so prompts and answers are recorded; do not keep that for real documents. Failures are recorded
on the span (`exception.*`, `laya.failure.kind`) and logged. Metrics are on the `LayaSample.Documents` and
`LayaSample.Rendering` meters (document size and pages, queue wait, extraction duration and in-flight, fields found,
warnings, LLM retries, rate-limit rejections, render/OCR duration, model load times) plus ASP.NET Core, Kestrel,
HttpClient and .NET runtime metrics. Startup logs one line with the version, renderer, models and limits.

**Test locally.** Create an ingest key in Honeycomb (Environment settings, API Keys). With `dotnet run` the settings can
live in user secrets, next to the other keys (read in Development only, so they never reach git or a container):

```sh
dotnet user-secrets set "OTEL_EXPORTER_OTLP_ENDPOINT" "https://api.honeycomb.io" --project src/LayaSample.Api
dotnet user-secrets set "OTEL_EXPORTER_OTLP_HEADERS" "x-honeycomb-team=<ingest key>" --project src/LayaSample.Api
dotnet user-secrets set "OTEL_EXPORTER_OTLP_PROTOCOL" "http/protobuf" --project src/LayaSample.Api
dotnet run --project src/LayaSample.Api
curl -F "file=@docs/samples/pdf/text.pdf" http://localhost:5091/api/documents/analyze
```

The secret names are the standard OpenTelemetry variable names, exactly as above. The protocol line is needed for
collectors that only take OTLP over HTTP (the SDK defaults to gRPC); Honeycomb accepts either. Remove them with
`dotnet user-secrets remove <name> --project src/LayaSample.Api`.

Containers run in Production, where user secrets are not read, so with `docker compose` use environment variables.
The renderer sits on a network with no route out, so run the bundled collector, which both services then send to:

```sh
LAYA_OTLP_ENDPOINT=http://collector:4318 HONEYCOMB_API_KEY=<ingest key> \
  docker compose --profile telemetry up --build        # then use port 8080
```

Without the profile, set `OTEL_EXPORTER_OTLP_ENDPOINT=https://api.honeycomb.io` and
`OTEL_EXPORTER_OTLP_HEADERS="x-honeycomb-team=<ingest key>"` to export from the API only (the renderer stays silent).

Within a minute a `laya-sample-api` dataset appears in Honeycomb with a `POST` root span and `laya.classify` /
`laya.prepare` children (plus `renderer.*` spans for rendered pages, from the `laya-sample-renderer` service). Add `-H "X-Api-Key: $LAYA_API_KEY"` and the `extract` endpoint to see `laya.extract` with the
model and token counts. To see the spans without a Honeycomb account, point the endpoint at a local OpenTelemetry
Collector (`http://localhost:4318`, protocol `http/protobuf`).

**On DOKS.** Store the key as a Secret and set two chart values; see
[Telemetry in deploy/README.md](deploy/README.md#telemetry-opentelemetry). Check it with
`kubectl -n laya rollout status deploy/<release>-api`, send a request through the gateway, then look for the same dataset.

## Feedback sentiment

`POST /api/feedback/analyze` is the original sample: hotel guest feedback to dissatisfaction, using the open-weight
[Laya](https://huggingface.co/elbruno/laya-onnx) "System 1" decision model on ONNX Runtime, in-process via the
`ElBruno.LocalLLMs.Decisions` package.

    curl -X POST localhost:5091/api/feedback/analyze -H 'content-type: application/json' \
      -d '{"text":"Room was filthy and the staff were rude."}'

The response has `dissatisfied`, `dissatisfactionScore` (P(dissatisfied), 0 to 1), `rating` (1 to 5 dissatisfaction),
`level` (None when not dissatisfied, otherwise Mild/Moderate/Severe from the rating), `primaryDriver` (null when unclear
or not dissatisfied) and `confidence`. Confidences are **uncalibrated**; tune thresholds on your own labeled feedback
before relying on them. Model settings go under the `Laya` section of `appsettings.json` (`ModelRepository`,
`ModelPath`, `CacheDirectory`, `IntraOpNumThreads`, `DecisionThreshold`); `ModelPath` loads a local copy and skips the
download.

## Test and CI

    dotnet run --project tests/LayaSample.Tests -c Release

CI (`.github/workflows/ci.yml`) restores in locked mode (`packages.lock.json`), builds with warnings as errors, runs the
tests, fails on vulnerable packages or licences outside `.github/allowed-licenses.json`, builds both Docker images,
lints and validates the Helm chart against the Kubernetes and Gateway API schemas, and deploys on `main`. After changing
a package, run `dotnet restore` and commit the updated lock files.
