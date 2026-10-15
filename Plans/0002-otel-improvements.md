# 0002-otel-improvements

Branch: `0002-otel-improvements`. Tick the task boxes as each task finishes (commit the tick with the work). The post-build page is
`docs/otel-observability.md` (per CLAUDE.md).

## Why

After plan 0001 the services reach Honeycomb through a TLS collector (seen working on 2026-10-15: spans from the gateway, document,
scan and extraction services arrive). Reading a trace was still harder than it should be:

- every HTTP client span was named just `POST`, so a trace did not say which service a call went to;
- the Dapr sidecar hop (the API to a callee, mTLS included) was invisible, because sidecar spans were switched off;
- the scan service's time in clamd was inside one span, and the page span did not say whether OCR ran;
- the collector log showed `TLS handshake error ... EOF` every ten seconds (its readiness probe was a bare TCP connect to the TLS port),
  which hides real errors.

## What

1. Client spans named after the service and operation: `POST docintake-document /documents/prepare`.
2. A `scan.clamd` span in the scan service (bytes sent, verdict, failures recorded) and an `intake.render.ocr` tag on the page span.
3. The Dapr sidecars export their own spans to the collector over TLS (sampling `"1"`, off with `"0"`).
4. The collector's readiness probe is an HTTP check on its `health_check` extension instead of a TCP connect to the TLS port.
5. Left for later (written down so they are not forgotten): collector self-metrics, Honeycomb alerts, the trace id in error responses, an
   unexplained root `GET` span per service.

## How (tasks)

**A. Span names and detail**
- [x] A1. `IntakeTelemetry.ClientSpanName` renames HTTP client spans (Dapr invoke URLs become `<METHOD> <app id> <operation>`; other calls `<METHOD> <host> <path>`).
- [x] A2. `scan.clamd` span in `ClamdClient` (client kind, `peer.service`, bytes sent, result; failure recorded) and `intake.render.ocr` on `document.render.page`.
- [x] A3. Tests: `TraceNamingTests` (names for Dapr and plain URLs, no address, the clamd span's kind and tags).

**B. Sidecar spans**
- [x] B1. `docintake.daprTracing`: the `tracing.otel` block (collector address, `protocol: http`, `isSecure` when the collector has TLS), used by every Dapr `Configuration`.
- [x] B2. Sidecar annotations: mount the private CA at `/ca` and set `SSL_CERT_FILE` (only when internal TLS is on).
- [x] B3. Run it on kind with the real images, with a debug exporter on the collector: check sidecar spans arrive over TLS and what they look like.
  **Done:** see "Verified" in `docs/otel-observability.md`.

**C. Collector readiness**
- [x] C1. `health_check` extension on 13133 (plain HTTP), `httpGet` readiness probe, container port.
- [x] C2. Check on kind that the probe works and the handshake error is gone. **Done:** a pod with the extension went ready and logged no `TLS handshake error` (before: one every ten seconds); see the docs page.

**D. Docs and checks**
- [x] D1. `docs/otel-observability.md`; README dated entry and link; update note in `docs/internal-transport-security.md`.
- [x] D2. `dotnet test`, `helm lint`, `deploy/check-chart.py`.
- [x] D3. After a commit, regenerate the archify diagram from HEAD. **Done:** pinned to `6af5186`; citations re-anchored (collector-config, ClamdClient, LocalPageRasterizer moved by a few lines).

## Operator quickstart

- [ ] **P1. Merge, let CI deploy, then look in Honeycomb.** Expect new spans named `POST docintake-document /documents/prepare` in `docintake-gateway`,
  and a new dataset `docintake-api` holding the API sidecar's `CallLocal/...` spans (the sidecar's service name is its Dapr app id).
  Record: one trace id, and whether `docintake-api` is acceptable as a separate dataset.
- [ ] **P2. Check the collector log is quiet.** `kubectl logs -n docintake deploy/docintake-otel-collector --tail=50` should show no `TLS handshake error`. Record: the line count of that message (expected 0).
- [ ] **P3. Check the cost.** Sidecar spans add a few spans per request; look at the Honeycomb usage page after a day and set `dapr.tracing.samplingRate` lower than `"1"` if needed.

## Not done / open

- Collector self-metrics (`service.telemetry.metrics: none` hides export failures), Honeycomb triggers (5xx rate, scan unavailable, sidecar restarts),
  the trace id in problem responses, and a root `GET` span per service with no route: each needs its own look.
