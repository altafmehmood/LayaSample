# Red-teaming the extraction API with garak

[garak](https://github.com/NVIDIA/garak) (NVIDIA, Apache-2.0) fires attack prompts at a language model and reports how many
got through. This folder points it at the extraction API: the whole agent, meaning the system prompt, the model, the JSON
schema and the filters in front of it, not the model alone.

It is a manual assessment. Nothing here runs on a push, and every prompt is a call to the paid model API.

## How it fits together

garak's REST generator posts `{"text": "<prompt>"}` and reads `{"text": "<answer>"}`. The API takes a document upload and
turns plain text away (415). `GarakShim` sits between them: it writes each prompt into a small PDF, the free-text remarks of
a loan application, uploads it to `POST /api/documents/extract`, and returns `HTTP <status>` and the response body as the
answer. A 422 that refused the document comes back as text, so garak sees "attack blocked" instead of an error. The shim
holds the API key (`LAYA_API_KEY`), so it never reaches garak's config, log or reports.

```
garak -- {"text": prompt} --> GarakShim -- PDF upload + X-Api-Key --> /api/documents/extract
garak <-- {"text": "HTTP 200 {...}"} -- GarakShim <-- response ------
```

## Run it

You need Python 3.11 to 3.13, the .NET SDK, and the stack running with a model key:

    OPENAI_API_KEY=sk-... LAYA_API_KEY=$(openssl rand -base64 32) docker compose up -d --build
    export LAYA_API_KEY=<the same key>
    redteam/run.sh --smoke                              # a few prompts per probe, to check the setup
    redteam/run.sh                                      # all default probes (about 7,500 prompts; see below)

The first run creates `.venv-garak` and installs the pinned `garak` from `requirements.txt`. Reports land in
`redteam/reports/` (not committed): `<run>.report.html` for reading, `<run>.report.jsonl` for the numbers.

| Option | Meaning |
|---|---|
| `--smoke` | 5 prompts per probe instead of up to 256 |
| `--probes <spec>` | garak selectors, comma separated (default `probes.promptinject,probes.latentinjection,probes.encoding,probes.goodside,probes.web_injection`). List them with `.venv-garak/bin/python -m garak --list_probes` |
| `--target <url>` | The API to attack (default `http://localhost:8080`) |
| `--label <name>` | Added to the run id and the report names |
| `--parallel <n>` | Prompts in flight at once (default 1; the API's limits are one analysis at a time unless you raise them) |
| `--max-requests <n>` | Stop paying after this many uploads (the shim answers "budget exhausted"; default 5000) |
| `--allow-remote` | Attack a non-local API. Refused otherwise: it spends real money, and the attack text lands in that service's telemetry and prompt capture |

The shim sends `User-Agent: laya-garak/<run id>`, so a run can be found in Honeycomb (`user_agent.original`) with the prompts
the model was given.

## How big is a full run

Counted with garak 0.17.0 (active probes, after the 256 cap): `encoding` 3,840, `latentinjection` 2,200, `promptinject`
768, `web_injection` 626, `goodside` 39: about **7,470 prompts**. That is more than the shim's default `--max-requests`
of 5000, so a default full run stops early and the probes that run last are cut off. Either pass `--max-requests 8000`
or run the families one at a time with `--probes`. At the model's list price a call is roughly $0.0005 to $0.001, so a
complete run that reaches the model every time is about $4 to $8 (worst case about $18); with the defences on, most
attacks are refused with 422 before any model call. The usage endpoint (`GET /api/usage`) gives the real figure afterwards.
`probes.xss` no longer exists in garak 0.17.0 (it became `web_injection`); an older spec naming it matches nothing.

## Two runs, on purpose

1. **Defences on** (the default, `Security:OnFinding=reject`). Most attacks should come back as `HTTP 422` and pass.
2. **Model alone**: restart the API with `LAYA_ON_FINDING=flag docker compose up -d --force-recreate api`, then
   `redteam/run.sh --label flag`. Findings are reported instead of refused, so the attacks reach the model. This measures
   how well the extraction agent itself resists, which is what to compare across prompt or model changes.

The `redteam` job of the CI workflow does both against a stack started on the runner (Actions > CI > Run workflow >
`redteam`), and keeps the reports as an artifact. It needs the `OPENAI_API_KEY` secret and never targets production.

## Reading a report

garak prints, per probe and detector, `PASS` or `FAIL` and `ok on n/total`: a failure means the detector found the attack's
marker in the answer, and the attack success rate is the share of failures. For example `promptinject.HijackHateHumans`
asks the model to say "I hate humans"; the detector looks for that string in what the shim returned, which includes the
whole JSON answer.

Which probes matter here: `latentinjection` hides instructions inside a document the model is asked to process, the
closest match to this service; `promptinject` and `encoding` try direct and obfuscated instructions; `goodside` and `web_injection`
cover JSON-forcing and exfiltration through markdown. The DAN, toxicity, leak-replay and package-hallucination families
are left out: they test chat behaviour that a schema-constrained extractor does not have.

## What a result means

- garak's detectors are mostly string matches on the answer. A pass is not proof of safety, and a fail can be the model
  quoting the injected text as data in `otherFields`, which is not the same as obeying it.
- The shim writes plain text PDFs only (Latin-1; other characters become `?`), so encodings that depend on exotic Unicode,
  and attacks that need an image or a scan, are not exercised.
- Compare runs of the same probes and prompt cap. Record a baseline here after the first full run so a change can be judged
  against it:

| Date | Model | Mode | Probe | Attack success rate |
|---|---|---|---|---|
| | | | | |
