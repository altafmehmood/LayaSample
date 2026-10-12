# DocIntake

Formerly LayaSample; Laya is now the name of the feedback model only (see [docs/docintake-architecture.md](docs/docintake-architecture.md)).

Lending-document intelligence on .NET 10. Upload a loan application, UCC-1, UCC-3 or loan and security agreement as a
PDF, image or Office file. The service works out how to read it (text layer, rendered page image, OCR for scans and
faxes), prepares it for an AI agent, and extracts the borrower, lender, collateral and every box on the UCC forms with
OpenAI vision and text models.

## Features

| Feature | What it gives you | Run it |
|---|---|---|
| [Document analysis](#document-analysis) | Classifies a PDF, image or Office file and prepares it for an agent: page images, markdown, OCR text, form values. No model, no cost | `curl -F file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf localhost:5091/api/documents/analyze` |
| [AI extraction](#ai-extraction) | Borrower, lender, collateral and every UCC box as structured data with page citations (OpenAI vision and text) | `dotnet user-secrets set "Extraction:ApiKey" "sk-..." --project src/DocIntake.Api`, then `POST /api/documents/extract` |
| [Usage and cost](#ai-extraction) | Every model call recorded (API key fingerprint, file name, input, cached, reasoning, output tokens, list-price cost) and served as JSON or CSV | `curl -H "X-Api-Key: $KEY" "localhost:5091/api/usage?format=csv"` |
| [Security layers](#security) | Malware scan (ClamAV), active-content, hidden-text and prompt-injection checks, all reported in `risks` and refused with 422 by default | `docker compose up -d clamav`, then upload the EICAR test file |
| [Authentication](#authentication) | `X-Api-Key` on the endpoints that spend money; key fingerprints in the usage history; optional admin keys | `dotnet user-secrets set "Auth:ApiKeys:0" "$(openssl rand -base64 32)" --project src/DocIntake.Api` |
| [Document service](#architecture) | Classification, preparation, PDFium, ImageMagick and OCR in a separate container: no network, read-only file system. The gateway reads no document | `docker compose up --build` |
| [Telemetry](#telemetry-honeycomb) | Traces, metrics and logs to Honeycomb, including prompts, answers, tokens and cost | set the `OTEL_EXPORTER_OTLP_*` user secrets, see [Telemetry](#telemetry-honeycomb) |
| [Lending corpus and evaluation](#lending-corpus) | 39 synthetic lending PDFs with ground truth, and an opt-in accuracy score for the model | `RUN_EXTRACTION_EVAL=1 dotnet run --project tests/DocIntake.Tests -c Release -- -trait "Category=Evaluation"` (paid) |
| [Red team](redteam/README.md) | garak attacks the extraction API through a shim, with the defences on and in `flag` mode | `redteam/run.sh --smoke` (paid) |
| [Kubernetes deploy](#kubernetes) | Helm chart and CI deploy to DOKS: Gateway API, TLS, ClamAV, usage PVC, collector | see [deploy/README.md](deploy/README.md) |
| [Feedback sentiment](#feedback-sentiment) | The original sample: hotel feedback sentiment with the Laya decision model | `curl -H 'Content-Type: application/json' -d '{"text":"The room was dirty"}' localhost:5091/api/feedback/analyze` |

Longer notes on how each feature set works, its settings, limits and what was verified:
[docs/security-cost-hardening.md](docs/security-cost-hardening.md) (usage tracking, ClamAV, content checks, streaming, red team) and
[docs/threat-model.md](docs/threat-model.md) (what could go wrong at each trust boundary, and what is still open) and
[docs/docintake-architecture.md](docs/docintake-architecture.md) (the DocIntake rename and the move to a gateway and
services, with the full list of telemetry renames).

The README is kept in step with the code: a new feature gets a row here, a section below, and a line in the quick start
or its own section if it needs setup.

## Quick start

You need the .NET 10 SDK. Steps 1 to 3 need no Docker, API key or cluster.

1. **Start the two services.** The gateway (the API) reads no document itself: every upload is classified and prepared by
   the document service, so run both, each in its own terminal. The first run of the gateway downloads the Laya model
   (~850 MB, several minutes); the OCR models ship with the document service.

       dotnet run --project src/DocIntake.DocumentService        # http://localhost:5092
       dotnet run --project src/DocIntake.Api                    # http://localhost:5091 (finds the service at 5092)

2. **Wait until they are ready.** `503` means a model is still loading, `200` means ready. The gateway's answer lists the
   document service as `document-service`.

       curl -i localhost:5092/health/ready
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

       dotnet user-secrets set "Extraction:ApiKey" "sk-..." --project src/DocIntake.Api
       dotnet run --project src/DocIntake.Api
       curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf" localhost:5091/api/documents/extract

   Locally the endpoint needs no access key; see [Authentication](#authentication) for deployments.

   Remove it with `dotnet user-secrets remove "Extraction:ApiKey" --project src/DocIntake.Api`. The `OPENAI_API_KEY`
   environment variable works too (a configured `Extraction:ApiKey` wins). Without a key the endpoint answers `503` and
   nothing else changes. The models are `Extraction:VisionModel` and `Extraction:TextModel` in `appsettings.json`;
   override them with the `Extraction__VisionModel` and `Extraction__TextModel` environment variables.

5. **Run the tests.** They are offline: OCR, the model provider and the malware scanner are faked.

       dotnet run --project tests/DocIntake.Tests -c Release

   CI runs them with `dotnet test`. Three kinds of test never run unless you ask: the AI accuracy evaluation (it calls the
   paid OpenAI API; it needs `RUN_EXTRACTION_EVAL=1`, even when a key is set, see [AI extraction](#ai-extraction)), the
   ClamAV tests (`CLAMD_HOST`) and the real-OCR check (`REAL_OCR=1`), both described under [Security](#security).

Without the document service the gateway still starts and serves feedback analysis, but every document upload answers `503`.
`dotnet run` does not scan uploads for malware (Development sets `Security:Malware:Provider` to `none`, because there is no
ClamAV daemon). To run with the isolated document service and the scanner, as in production, use
`docker compose up --build` (http://localhost:8080); see [Docker](#docker).

## Quickstarts by date

One entry per day's work, newest first: what was added, the commands to try it and what you should see. Steps marked
**(paid)** call the OpenAI API; **(opt-in)** tests do not run unless you ask. Ports: `dotnet run` listens on 5091, Docker
Compose on 8080.

### 2026-10-12

**Document service: the gateway no longer reads documents.** `DocIntake.Renderer` became `DocIntake.DocumentService`, and
classification and preparation moved into it, with PdfPig and the Office libraries. The gateway streams each upload to it
twice (`POST /documents/classify`, then `/prepare`) and keeps the scan, the risk policy, extraction and usage. Callers see
the same answers. What changed for you: **run two processes locally**, a document service outage now fails **every**
upload (it used to fail only the ones that needed rendering), and `DocumentAnalysis:RendererUrl` and
`AllowInProcessRendering` are replaced by `DocumentAnalysis:DocumentServiceUrl`. See
[docs/docintake-architecture.md](docs/docintake-architecture.md), Phase 3.

    dotnet run --project src/DocIntake.DocumentService          # terminal 1, http://localhost:5092
    dotnet run --project src/DocIntake.Api                      # terminal 2, http://localhost:5091
    curl -s localhost:5091/health/ready                          # lists laya, scanner and document-service
    curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf" "localhost:5091/api/documents/analyze?dispatch=false&includeData=false"
    curl -i -F "file=@docs/samples/unsupported/legacy.doc" localhost:5091/api/documents/analyze

You should see the fax PDF classified as `FaxPdf` with strategy `Hybrid` and a page image plus OCR text, and `415` for the
legacy `.doc`. In Honeycomb one request is one trace across `docintake-gateway` and `docintake-document`. Nothing here is
paid. Run on the machine: both services, 12 sample documents, real OCR. Not run: Kubernetes, `docker compose up` of the
new pair, extraction against the real model provider.

**One failure type, one place that turns it into HTTP.** The code every service will share is now in two projects
(`DocIntake.Contracts` and `DocIntake.ServiceDefaults`), and failures below the endpoints are an
`IntakeFailureException` with a kind, not a status code. `ProblemMapping` turns each kind into the status, Retry-After,
span outcome, log line and metric. Callers see no change; telemetry and log text change slightly (the new
`intake.outcome` for a refused model answer is `rejected`, and `intake.failure.cause` is set for every failure). See
[docs/docintake-architecture.md](docs/docintake-architecture.md), Phase 2.

    dotnet run --project src/DocIntake.Api
    curl -i -F "file=@docs/samples/unsupported/legacy.doc" localhost:5091/api/documents/analyze
    curl -i -F "file=@docs/samples/unsupported/notes.txt" localhost:5091/api/documents/extract

You should see `415` with the same problem body as before, from both endpoints. Neither command is paid: the files fail
before any model call. With the OTLP user secrets set, the request span has `intake.outcome` = `rejected`. The failure
paths against the real model provider (rate limit, bad key, timeout) were tested with a fake client only.

### 2026-10-11

**Renamed LayaSample → DocIntake.** The project, solution, namespaces, Helm chart, local images and telemetry now use
the DocIntake name. Laya is now only the feedback model's name. Behaviour is unchanged, but **telemetry names
changed**:
- Honeycomb datasets: `laya-sample-api` is now `docintake-gateway`, and `laya-sample-renderer` is now
  `docintake-document`.
- Span, attribute and metric names: the `laya.` prefix is now `intake.`.
- Meters: `LayaSample.*` is now `DocIntake.*`.

Entries below this one still say `src/LayaSample.Api`; read it as `src/DocIntake.Api`. The live cluster keeps its names
(namespace `laya`, `laya-*` secrets, GHCR images) until the services cutover. The full rename table is in
[docs/docintake-architecture.md](docs/docintake-architecture.md).

    dotnet run --project src/DocIntake.Api
    curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf" "localhost:5091/api/documents/analyze?includeData=false"

You should see `"kind": "FaxPdf"` with strategy `Hybrid`. With the OTLP user secrets set, the trace shows up in the
`docintake-gateway` dataset with `intake.classify` and `intake.prepare` spans. Nothing here is paid. With Docker Compose,
the project is now `docintake`, so it starts with fresh volumes and the old `laya-sample_*` volumes stay until you
remove them.

**Threat model.** A STRIDE review of each trust boundary: what already stops each threat, what does not, and the order
to fix the open ones. A desk review of commit `6fe4128`; nothing was attacked or tested. See
[docs/threat-model.md](docs/threat-model.md). Nothing to run and nothing paid.

**Red-team probe list fixed.** `redteam/run.sh` named `probes.xss`, which garak 0.17.0 no longer has; it now runs
`probes.web_injection`. A full run is about 7,470 prompts, more than the default 5000-upload cap. See
[docs/security-cost-hardening.md](docs/security-cost-hardening.md) and `redteam/README.md`.

    redteam/run.sh --smoke                          # (paid) a few prompts per probe
    redteam/run.sh --max-requests 8000              # (paid) the complete run, about $4 to $8 at list price (estimate)

Counting the prompts, which is free, was run; the paid runs were not.

**Usage and cost tracking.** Every model call is recorded and served as JSON or CSV. See [AI extraction](#ai-extraction).

    dotnet user-secrets set "Extraction:ApiKey" "sk-..." --project src/LayaSample.Api       # (paid)
    dotnet run --project src/LayaSample.Api
    curl -F "file=@docs/samples/pdf/corpus/redwood-mechanical-ucc1-fax.pdf" localhost:5091/api/documents/extract
    curl "localhost:5091/api/usage"                                   # one row per model call, newest first
    curl "localhost:5091/api/usage?format=csv" -o usage.csv           # the same as a CSV download
    curl "localhost:5091/api/usage/summary?groupBy=model"             # tokens and cost per model (or day, key)

You should see `usage` in the extract response with `cachedInputTokens`, `reasoningTokens` and `costUsd`, and a row per call
in the history (`src/LayaSample.Api/data/usage.db`, ignored by git). With a key configured (anywhere but Development) send
`-H "X-Api-Key: $KEY"`; a key sees its own rows. On Kubernetes the history is on a PVC (`api.usage.persistence`).

**Malware scanning with ClamAV.** Every upload is scanned before anything parses it. See [Security](#security).

    docker compose up -d clamav                                       # healthy in about 25 s
    printf 'X5O!P%%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*' > eicar.txt
    Security__Malware__Provider=clamd Security__Malware__Host=localhost dotnet run --project src/LayaSample.Api
    curl -F "file=@eicar.txt" localhost:5091/api/documents/analyze    # 422 "document rejected: malware detected"
    docker compose stop clamav                                        # now any upload answers 503 with Retry-After
    CLAMD_HOST=localhost dotnet run --project tests/LayaSample.Tests -c Release -- -trait "Category=Integration"   # (opt-in)

`dotnet run` alone does not scan (Development sets the provider to `none`). On Kubernetes the node needs the memory for
ClamAV: [deploy/README.md](deploy/README.md#malware-scanning-clamav).

**Active content, hidden text and prompt-injection checks.** Findings are refused with 422, or reported in `risks` with
`flag`. See [Security](#security).

    dotnet run --project tests/LayaSample.Tests -c Release -- -class LayaSample.Tests.RiskTests
    Security__OnFinding=flag dotnet run --project src/LayaSample.Api      # report findings in `risks` instead of refusing

There are no hostile sample files in the repository: the tests generate them (`DocumentFixtures.PdfWithHiddenText`,
`PdfWithLinkAction`, `PdfWithAttachment`). The sample corpus has no blocking findings; check the real-OCR scans and faxes with
`REAL_OCR=1 dotnet run --project tests/LayaSample.Tests -c Release -- -class LayaSample.Tests.RealOcrRiskTests` **(opt-in)**.

**Streaming uploads.** Nothing to switch on: the document is read in place from the upload's own stream, never copied into
memory. See [Security](#security) (Memory).

    dotnet run --project tests/LayaSample.Tests -c Release -- -class LayaSample.Tests.StreamingTests

**Red-teaming with garak.** See [redteam/README.md](redteam/README.md).

    OPENAI_API_KEY=sk-... LAYA_API_KEY=$(openssl rand -base64 32) docker compose up -d --build      # (paid)
    export LAYA_API_KEY=<the same key>
    redteam/run.sh --smoke                                            # a few prompts per probe; reports in redteam/reports/
    LAYA_ON_FINDING=flag docker compose up -d --force-recreate api    # then rerun to test the model without the filters

Or run the manual `redteam` job from the Actions tab. It spends the `OPENAI_API_KEY` secret.

**The paid evaluation is opt-in.** A plain test run never calls OpenAI, even with a key on the machine.

    RUN_EXTRACTION_EVAL=1 dotnet run --project tests/LayaSample.Tests -c Release -- -trait "Category=Evaluation"   # (paid)

**Telemetry in production (Honeycomb through the in-cluster collector).** Add the `HONEYCOMB_API_KEY` GitHub secret, then
merge to `main`; the deploy job creates the `laya-otel` Secret and the collector.

    kubectl -n laya get pods                       # laya-otel-collector next to the API and the renderer
    # Honeycomb: datasets laya-sample-api and laya-sample-renderer

See [Telemetry](#telemetry-honeycomb) and [deploy/README.md](deploy/README.md).

## API

| Endpoint | Purpose |
|---|---|
| `POST /api/documents/analyze` | Multipart `file`. Classify and prepare a document for an agent. Query: `dispatch` (default true), `includeData` (default true). |
| `POST /api/documents/extract` | Multipart `file`. Extract structured data with OpenAI. **Needs the `X-Api-Key` header** (see [Authentication](#authentication)); 503 until a model key is configured. |
| `GET /api/usage` | Model calls with tokens and list-price cost, newest first; filters, paging, `format=csv`. **Needs `X-Api-Key`**; a key sees its own rows (see [AI extraction](#ai-extraction)). |
| `GET /api/usage/summary` | The same, totalled per day, model or key. **Needs `X-Api-Key`.** |
| `POST /api/feedback/analyze` | JSON `{ "text": "..." }`. Hotel feedback sentiment (the original sample). |
| `GET /health/live`, `/health/ready` (also `/health`) | Liveness; readiness (503 while models load, 200 `Degraded` if one failed for good or the document service is unreachable). |
| `GET /scalar/v1`, `/openapi/v1.json` | Interactive API docs and the OpenAPI document. |

Errors are problem details (`application/problem+json`). The analyze and extract responses carry a `risks` list of
findings about the upload (see [Security](#security)).

### Authentication

Only `POST /api/documents/extract` needs a key, because it is the endpoint that spends money (OpenAI bills per call);
the other endpoints are open, so put the service behind an authenticating gateway before exposing those to an
untrusted network. Callers send the key in a header:

    curl -H "X-Api-Key: $DOCINTAKE_API_KEY" -F "file=@document.pdf" https://api.example.com/api/documents/extract

- **Configure it** with `Auth:ApiKeys` (an array, so a key can be rotated without downtime): the environment variables
  `Auth__ApiKeys__0`, `Auth__ApiKeys__1`, a user secret, or in Kubernetes a Secret (see [deploy/README.md](deploy/README.md)).
  Each key must be at least 24 characters with no spaces, or the app refuses to start. Generate one with
  `openssl rand -base64 32`.
- **It fails closed.** In any environment except Development, extraction answers `503` until a key is configured. In
  Development it is open when no key is set, so the quick start needs no setup; set one to try it locally:
  `dotnet user-secrets set "Auth:ApiKeys:0" "<key>" --project src/DocIntake.Api`.
- **The check runs first.** A request with a missing or wrong key gets `401` before its upload is read, before it can
  take a slot in the queue and before anything reaches OpenAI. Keys are compared in constant time and never logged;
  rejections are counted on the `intake.auth.rejections` metric.
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
the API key. Metrics are on the `DocIntake.Documents` meter.

**Privacy.** The document's pages (images and text) are sent to OpenAI. Under OpenAI's API terms the data is not used
for training and requests are not stored (`store` is false), but abuse monitoring may retain them for up to 30 days
unless the organisation has Zero Data Retention. Use a dedicated OpenAI project for this service with a monthly budget
(the hard stop on spend; the API key above only decides who may call), and get sign-off before sending borrower data.

**Usage and cost.** Every model call is recorded, one row each: the calling API key (a short hash fingerprint, never the
key), the file name, input, cached, reasoning and output tokens, and the cost at OpenAI's list price. Because a row is a
call, the generic-agent retry and an answer that was cut off or refused (still billed) show up on their own, and a call
that failed before OpenAI answered has no tokens. The extract response also returns the totals in `usage`
(`cachedInputTokens`, `reasoningTokens`, `costUsd`).

    curl -H "X-Api-Key: $DOCINTAKE_API_KEY" "http://localhost:8080/api/usage?from=2026-10-01&format=csv" -o usage.csv
    curl -H "X-Api-Key: $DOCINTAKE_API_KEY" "http://localhost:8080/api/usage/summary?groupBy=model"

| Endpoint | Returns |
|---|---|
| `GET /api/usage` | Rows, newest first. Filters `from`, `to` (UTC, `to` exclusive), `file` (contains, any case), `model`, `apiKey` (admin keys only); `limit` (default 1000, at most 10000) and `before` (a row id) to page. JSON carries `nextBefore` when more rows exist; CSV carries it in the `X-Next-Before` header. |
| `GET /api/usage/summary` | Calls, tokens and cost per `groupBy=day` (default), `model` or `key`, with a total; same filters. `unpriced` counts calls whose model has no price, which are missing from the cost. |

`format=csv` (or `Accept: text/csv`) downloads a CSV with a byte order mark so Excel reads it as UTF-8. File names come
from the caller, so a CSV cell that starts with `=`, `+`, `-` or `@` gets a leading apostrophe and cannot run as a
formula. Both endpoints need an `X-Api-Key`, and a key sees only its own rows; keys listed under `Auth:AdminKeys`
(none by default) see everyone's and are also accepted for extraction.

The history is a SQLite file at `Usage:DatabasePath` (`src/DocIntake.Api/data/usage.db` for `dotnet run`, `/data/usage.db`
in the container, on the `usage-data` volume in compose and a PersistentVolumeClaim in Kubernetes). An empty path
switches tracking off and the endpoints answer 503. Writing happens in the background, so a storage failure is logged and
counted (`intake.usage.write_failures`) but never fails an extraction. Spend also goes to the `intake.llm.cost` and
`intake.extraction.tokens` metrics.

**The cost is a list-price estimate, not the invoice.** `Extraction:Pricing` holds the per-model prices (per million
tokens, with a higher tier above 272,000 input tokens) and the date they were read; update it when OpenAI changes them.
It assumes the input count includes the cached tokens and the output count includes the reasoning tokens, which is how
OpenAI reports them; check one call against the OpenAI dashboard. Batch, flex and discounts are not modelled, and the
OpenAI SDK's own retries of a failed request are invisible here. Use a dedicated OpenAI project for this service so the
dashboard total is comparable with the history.

**Accuracy evaluation.** This scores the model against the corpus manifest and prints accuracy by field and by
rendering, the keyword detector's hit rate, and every miss. It calls the paid API for 39 documents and skips itself
unless you set `RUN_EXTRACTION_EVAL=1` (and a key: `OPENAI_API_KEY` or the `Extraction:ApiKey` user secret). A key alone does
not run it, so a plain `dotnet test` never spends money:

    RUN_EXTRACTION_EVAL=1 dotnet run --project tests/DocIntake.Tests -c Release -- -trait "Category=Evaluation" -showLiveOutput

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

Two services. The **gateway** (`DocIntake.Api`) is the public API: access keys, rate limits, the malware scan, the risk
policy, AI extraction, the usage history and Laya. The **document service** (`DocIntake.DocumentService`) reads every
upload: it classifies and prepares it with PdfPig, the Office libraries and the native C/C++ libraries (PDFium,
ImageMagick, Skia, ONNX Runtime for OCR). A malicious or malformed file that crashes or takes over a parser therefore
cannot take down the gateway or reach its data:

| | `DocIntake.Api` (gateway) | `DocIntake.DocumentService` |
|---|---|---|
| Does | Auth, scanning, risk policy, routing, AI extraction, usage, Laya | File-type detection, PDF classification and text, Office conversion, form data, signatures, page rendering, image decoding, OCR |
| Reads untrusted document bytes | No: it streams the upload on, and has no parser in its build | Yes, isolated: no network egress, read-only file system, no capabilities, own memory/CPU limits |
| Talks to | The document service, ClamAV, and OpenAI for extraction | Nothing outside; only the gateway may reach it |

The gateway asks the document service twice per upload over HTTP: `POST /documents/classify`, then, if the risk policy lets
the document through, `POST /documents/prepare`. Each call carries the file; the service keeps nothing between them. The
service's address is `DocumentAnalysis:DocumentServiceUrl`, and without it the gateway refuses to start. If the service dies
on a document, that request gets 502 and the service restarts. If it is down, **every** document upload gets 503 with
`Retry-After` (classification happens there too), while feedback analysis keeps working, and the gateway's readiness reports
`document-service` as `Degraded`. Shared types are in `src/DocIntake.Contracts` and the telemetry setup in
`src/DocIntake.ServiceDefaults`; the rendering code the document service uses is in `src/DocIntake.Rendering`.

There is no in-process mode any more: `DocumentAnalysis:AllowInProcessRendering` is gone, and `appsettings.Development.json`
points the gateway at `http://localhost:5092`. See [Quick start](#quick-start) for running both.

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

Durations per stage and documents by kind and outcome are published on the `DocIntake.Documents` meter
(`dotnet-counters monitor -n DocIntake.Api --counters DocIntake.Documents`).

## Security

An upload goes through these layers. The scan, the policy decisions, the prompt and the checks of the text and the answer run in the gateway; finding the file's structure, preparing it and every parser run only in the document service.

| Layer | What it does | Refuses with |
|---|---|---|
| **Malware scan** | Before anything parses the file, it is streamed to ClamAV (its own container, clamd's `INSTREAM`), 64 KB at a time. | 422 `malware detected`. While the scanner gives no verdict: 503 with `Retry-After` (`Security:Malware:FailMode`, `closed` by default) |
| **File structure** | PDF: Launch actions and RichMedia/3D (block), JavaScript and form scripts (reported), embedded programs (block), hidden text: tiny, near-white or off-page (block). Office: macros, ActiveX, embedded programs and linked templates or objects (block), embedded objects and external workbook links (reported). | 422 for the blocking ones |
| **Text** | The text the model will read (text layer, OCR, form values, attachments) is checked for phrases aimed at the model: "ignore the previous instructions", "you are now an AI", a system-prompt request, chat-template tokens. | 422 `instructions addressed to an AI model` |
| **Prompt** | The document sits in a `<document>` block that the system prompt declares to be data. A `<document>` tag inside the text is made inert, so it cannot close the block. The model has no tools and answers in a fixed JSON schema. | |
| **Answer** | The answer is checked for the same phrases, which an injection that got through can echo into a field or its quote. | 422 `extraction rejected` |
| **Isolation** | Every parser (PdfPig, the Office libraries, PDFium, ImageMagick, OCR) runs only in the document service: no network, read-only file system, no capabilities. | |

Every finding is in the `risks` list of the analyze and extract responses (kind, severity, page, detail), and counted on
the `intake.risk.findings` metric. The detail names the kind of thing and where; it never repeats the document's text, and
the malware signature stays in the log and the trace.

| Setting | Default | Effect |
|---|---|---|
| `Security:OnFinding` | `reject` | `reject` refuses a request with a blocking finding (422). `flag` reports it and carries on, which is also how to test the model without the filters in front of it |
| `Security:Malware:Provider` | `clamd` | `none` switches scanning off (Development sets it, since `dotnet run` has no ClamAV) |
| `Security:Malware:Host`, `Port` | `clamav`, 3310 | Where the clamd daemon is |
| `Security:Malware:FailMode` | `closed` | While there is no verdict: `closed` answers 503, `open` accepts the upload unscanned (and logs a warning) |
| `Security:Malware:TimeoutSeconds` | 60 | Connecting, sending and waiting for the verdict |

**What this does not do.** The text check is a list of phrases, so a rephrased attack gets past it; it is one layer next
to the prompt and the schema, not a guarantee, and a document may need to be refused even when no phrase matches. The
hidden-text check skips a page that is a scan with a text layer over the image (that layer is invisible by design), so
it does not yet compare the layer with what the image shows. Page-level additional actions in a PDF are not inspected
(PdfPig does not expose them); the catalog's open action and the actions on annotations and form widgets are. The
phrases are tuned to leave ordinary lending paperwork alone: every sample document under `docs/samples` has no blocking
finding, including the scans and faxes read by the real OCR. Treat the extracted values as untrusted data downstream.

ClamAV is GPL-2.0 and runs as its own container, which the API talks to over TCP; it is not part of the application (see
`CLAUDE.md`). Run the tests that use a real daemon with `docker compose up -d clamav` and then
`CLAMD_HOST=localhost dotnet run --project tests/DocIntake.Tests -c Release -- -trait "Category=Integration"`; the real-OCR
check of the sample scans runs with `REAL_OCR=1` in the same command.

**Red team.** [redteam/](redteam/README.md) attacks the API with [garak](https://github.com/NVIDIA/garak), by hand or with the manual
`redteam` CI job, once with these filters on and once in `flag` mode to test the model itself.

**Memory.** The upload is one seekable stream: ASP.NET already spools a multipart file above 64 KB to a temporary file
(`/tmp`, a writable volume in the containers), and the scan, the classify call and the prepare call to the document service
all read that stream in place (the gateway sends it twice, once per call). The document is never copied into memory in the gateway. What still is: files embedded in a PDF (a few,
bounded by `MaxAttachments`), the rendered page images (at most `MaxPages`) and their text, the libraries' own structures
(PdfPig, ClosedXML, OpenXML, PDFium, ImageMagick), and the signed ranges of a signed PDF.

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
    DocumentAnalysis__OcrModel=PPOCRv6Small dotnet run --project src/DocIntake.Api

Downloads are pinned and SHA-256 verified. If the configured models are missing, documents that need OCR get a 503
rather than being reported as unreadable. Local models live in [`models/`](models/README.md) (ignored by git).

## Docker

`docker-compose.yml` runs the gateway and the hardened document service (the `renderer` service and image target keep their
old name for now). Both images bake in their models (Laya in the gateway, OCR in the document service), so containers never download anything and run offline. The build itself takes models from the local
[`models/`](models/README.md) folder when their SHA-256 matches the pinned version and downloads only what is missing,
so a machine that cannot download (a proxy or TLS inspection blocking curl) can build once the files are there: run
the API once with `dotnet run`, or copy `models/` from another machine.

    docker compose up --build                                  # http://localhost:8080
    OCR_MODEL=PPOCRv6Small docker compose up --build           # multilingual OCR
    OPENAI_API_KEY=sk-... DOCINTAKE_API_KEY=$(openssl rand -base64 32) docker compose up --build   # enables /api/documents/extract

A third container, `clamav`, scans every upload for malware; see [Security](#security). Its first start downloads the
signature database (a minute or two), and the API starts once it reports healthy. It has no memory or CPU limit and uses
about 1 GiB when idle.

Containers run in Production, where user secrets are not read, so the keys come from the environment, and extraction
answers 503 without `DOCINTAKE_API_KEY` (callers send it as `X-Api-Key`). The renderer
is on an internal network with no route out, has a read-only file system (`/tmp` is a tmpfs), drops all Linux
capabilities, and has its own memory, CPU and process limits; it restarts automatically after a crash. Both run as
non-root users and have a `HEALTHCHECK` on `/health/ready`. The images are built from one `Dockerfile`
(`--target api` / `--target renderer`); model downloads use a BuildKit cache, so rebuilds don't fetch them again.

## Kubernetes

[`deploy/`](deploy/README.md) has a Helm chart that deploys both services to a DigitalOcean Kubernetes cluster: the API
is public through the Gateway API with a Let's Encrypt certificate, the renderer (now the document service) is reachable only
from the API and has no egress. The CI `deploy` job runs it on every push to `main`, with the cluster credentials, the Let's Encrypt
e-mail, the OpenAI key and the API access key (`LAYA_API_KEY`) taken from GitHub secrets. The chart also runs a ClamAV
pod that scans uploads, and keeps the usage history on a PersistentVolumeClaim. ClamAV has no resource requests or
limits, so the cluster's node needs the memory for it (see [deploy/README.md](deploy/README.md#malware-scanning-clamav)).
The chart, the secrets and the cluster setup are described in [deploy/README.md](deploy/README.md).

## Telemetry (Honeycomb)

Both services send traces, metrics and logs over OpenTelemetry (OTLP) when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; unset,
they export nothing. In Kubernetes and in compose the renderer has no route out, so both services send to a small
collector that alone talks to Honeycomb (see below), and the renderer's spans join the API's trace.

**What you get.** One extract request is a single trace: the HTTP request (tagged `intake.file.bytes`, `intake.document.kind`,
`intake.document.strategy`, `intake.document.pages`, `intake.outcome` = `ok` / `rejected` / `renderer_unavailable` / `rate_limited` /
`auth_invalid` / `extraction_failed`...) with children `intake.buffer`, `intake.classify`, `intake.prepare`, `intake.render` (the
call to the renderer, with `renderer.render` / `renderer.page` / `renderer.ocr` beneath it), `intake.extraction` and one
`intake.extract` per model call. `intake.extract` carries the system prompt, the document text sent (page images appear only
as `[image ..., N bytes]`), the model's answer (`intake.extraction.result`), token counts, and retry events. This is a
technology demonstrator, so prompts and answers are recorded; do not keep that for real documents. Failures are recorded
on the span (`exception.*`, `intake.failure.kind`) and logged. Metrics are on the `DocIntake.Documents` and
`DocIntake.Rendering` meters (document size and pages, queue wait, extraction duration and in-flight, fields found,
warnings, LLM retries, rate-limit rejections, render/OCR duration, model load times) plus ASP.NET Core, Kestrel,
HttpClient and .NET runtime metrics. Startup logs one line with the version, renderer, models and limits.

**Test locally.** Create an ingest key in Honeycomb (Environment settings, API Keys). With `dotnet run` the settings can
live in user secrets, next to the other keys (read in Development only, so they never reach git or a container):

```sh
dotnet user-secrets set "OTEL_EXPORTER_OTLP_ENDPOINT" "https://api.honeycomb.io" --project src/DocIntake.Api
dotnet user-secrets set "OTEL_EXPORTER_OTLP_HEADERS" "x-honeycomb-team=<ingest key>" --project src/DocIntake.Api
dotnet user-secrets set "OTEL_EXPORTER_OTLP_PROTOCOL" "http/protobuf" --project src/DocIntake.Api
dotnet run --project src/DocIntake.Api
curl -F "file=@docs/samples/pdf/text.pdf" http://localhost:5091/api/documents/analyze
```

The secret names are the standard OpenTelemetry variable names, exactly as above. The protocol line is needed for
collectors that only take OTLP over HTTP (the SDK defaults to gRPC); Honeycomb accepts either. Remove them with
`dotnet user-secrets remove <name> --project src/DocIntake.Api`.

Containers run in Production, where user secrets are not read, so with `docker compose` use environment variables.
The renderer sits on a network with no route out, so run the bundled collector, which both services then send to:

```sh
DOCINTAKE_OTLP_ENDPOINT=http://collector:4318 HONEYCOMB_API_KEY=<ingest key> \
  docker compose --profile telemetry up --build        # then use port 8080
```

Without the profile, set `OTEL_EXPORTER_OTLP_ENDPOINT=https://api.honeycomb.io` and
`OTEL_EXPORTER_OTLP_HEADERS="x-honeycomb-team=<ingest key>"` to export from the API only (the renderer stays silent).

Within a minute a `docintake-gateway` dataset appears in Honeycomb with a `POST` root span and `intake.classify` /
`intake.prepare` children (plus `renderer.*` spans for rendered pages, from the `docintake-document` service). Add `-H "X-Api-Key: $DOCINTAKE_API_KEY"` and the `extract` endpoint to see `intake.extract` with the
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

    dotnet run --project tests/DocIntake.Tests -c Release

CI (`.github/workflows/ci.yml`) restores in locked mode (`packages.lock.json`), builds with warnings as errors, runs the
tests, fails on vulnerable packages or licences outside `.github/allowed-licenses.json`, builds both Docker images,
lints and validates the Helm chart against the Kubernetes and Gateway API schemas, and deploys on `main`. After changing
a package, run `dotnet restore` and commit the updated lock files.
