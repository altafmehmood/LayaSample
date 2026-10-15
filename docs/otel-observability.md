# OpenTelemetry: trace readability, sidecar spans, collector readiness

Date: 2026-10-14 (built), 2026-10-15 (checked on kind). Plan: [Plans/0002-otel-improvements.md](../Plans/0002-otel-improvements.md).

## Why this was done

After plan 0001 the services reach Honeycomb through a collector with a TLS receiver, and spans from the gateway, document, scan and
extraction services arrived (seen in Honeycomb on 2026-10-15). A trace was still hard to read: every HTTP client span was named `POST`,
the Dapr sidecar hop was invisible, the scan service's clamd time was hidden inside one span, and the collector's log showed a `TLS
handshake error` every ten seconds because its readiness probe opened a bare TCP connection to the TLS port.

## What it does

### Client spans name the service and the operation
- **How:** `IntakeTelemetry.ClientSpanName` (`src/DocIntake.ServiceDefaults/IntakeTelemetry.cs`) renames each HTTP client span when it
  starts. A Dapr invoke URL (`/v1.0/invoke/<app id>/method/<operation>`) becomes `POST docintake-document /documents/prepare`; any other
  call becomes `<METHOD> <host> <path>`, for example `POST api.openai.com /v1/chat/completions`.
- **Try:** `dotnet test --filter "FullyQualifiedName~TraceNaming"` (ran, passes), or run the stack and open a trace.
- **Does not:** rename server spans or add ids to names. It assumes the paths are fixed routes; a client that puts an id in a path would
  make a high-cardinality name.

### A span for the clamd round trip, and an OCR tag
- **How:** `ClamdClient.ScanAsync` (`src/DocIntake.ScanService/ClamdClient.cs`) wraps the call in `scan.clamd` (client kind,
  `peer.service`, `intake.scan.bytes`, `intake.scan.result`; a failure is recorded with its exception). `document.render.page` now
  carries `intake.render.ocr` (`LocalPageRasterizer.cs`).
- **Does not:** make OCR faster. OCR is serialised and already uses all cores, so rendering pages in parallel would not help.

### Dapr sidecars export their own spans
- **How:** `docintake.daprTracing` (`templates/_helpers.tpl`) adds a `tracing.otel` block to every Dapr `Configuration`
  (`templates/dapr-config.yaml`): the collector's address, `protocol: http`, and `isSecure` when the collector has TLS. The sidecar
  mounts the private CA at `/ca` and sets `SSL_CERT_FILE` (`dapr.io/volume-mounts`, `dapr.io/env`), only when internal TLS is on.
- **Settings:** `dapr.tracing.samplingRate`, default `"1"` (`"0"` sends none; the trace context still travels). Nothing is exported
  unless `telemetry.collector.enabled`.
- **Look:** the API's sidecar exports under the service name of its Dapr app id, `docintake-api`, so its spans land in their own
  Honeycomb dataset beside `docintake-gateway`. A callee's sidecar shares the callee's dataset (same name). They are spans named
  `CallLocal/<app id>/<operation>`, scope `dapr-diagnostics`.
- **Does not:** turn on the sidecars' metrics (still off).

### Collector readiness
- **How:** the collector has the `health_check` extension on 13133 (plain HTTP; status only) and the readiness probe is an HTTP GET
  there (`templates/collector.yaml`, `templates/collector-config.yaml`). Before, the probe was a TCP connect to the TLS port.

## Tests
- `TraceNamingTests` (4 name cases, a request without an address, the `scan.clamd` span's kind and tags).
- `deploy/check-chart.py` still passes. No test asserts the `tracing.otel` block; the kind run below does.

## Verified, and not
Verified on a throwaway kind cluster (cert-manager, Dapr 1.18.7, the real images rebuilt from this branch, a debug exporter added to
the collector for the run, sampling `"1"`, scanning off, one upload with `dispatch=false`):
- the API's sidecar sent `CallLocal/docintake-document/documents/classify` and `.../prepare` client spans (service name
  `docintake-api`, scope `dapr-diagnostics`) to the collector over TLS, and the document service's sidecar sent the matching server spans;
- the gateway's client spans were named `POST docintake-document /documents/classify` and `/prepare`;
- the collector's `health_check` probe worked: a pod started without the extension stayed `0/1` (probe refused), a pod with it went
  ready, and the log carried no `TLS handshake error` afterwards (before: one every ten seconds, also on kind).

Not verified: the `scan.clamd` span in a running pod (covered by the unit test only; scanning was off on kind), the extraction and scan
sidecars' spans, anything on the DOKS cluster, Honeycomb receiving the sidecar spans, and Honeycomb cost with sampling `"1"`.

## Before you deploy
- [ ] After the deploy, look for `POST docintake-document /documents/prepare` in `docintake-gateway` and a dataset `docintake-api`.
- [ ] `kubectl logs -n docintake deploy/docintake-otel-collector --tail=50` has no `TLS handshake error`.
- [ ] Watch the Honeycomb usage page for a day; lower `dapr.tracing.samplingRate` if the sidecar spans cost too much.

## Not done (open)
Collector self-metrics (`service.telemetry.metrics: none` hides failed exports), Honeycomb triggers (5xx rate, scan unavailable,
sidecar restarts), the trace id in problem responses, and one unexplained root `GET` span per service.
