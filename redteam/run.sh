#!/usr/bin/env bash
# Red-teams the extraction API with garak. See redteam/README.md.
#
#   redteam/run.sh --smoke                         # a few prompts per probe
#   redteam/run.sh --probes probes.latentinjection # one family, full size
#   redteam/run.sh --target http://localhost:8080 --label flag-mode
#
# Needs LAYA_API_KEY when the target asks for an X-Api-Key. Every prompt is a paid model call.
set -euo pipefail
cd "$(dirname "$0")/.."

TARGET="http://localhost:8080"
SPEC="probes.promptinject,probes.latentinjection,probes.encoding,probes.goodside,probes.web_injection"
CONFIG="redteam/garak-full.yaml"
LABEL="run"
PARALLEL=1
EXTRA=()
while [ $# -gt 0 ]; do
  case "$1" in
    --target) TARGET="$2"; shift 2 ;;
    --probes) SPEC="$2"; shift 2 ;;
    --smoke) CONFIG="redteam/garak-smoke.yaml"; shift ;;
    --label) LABEL="$2"; shift 2 ;;
    --parallel) PARALLEL="$2"; shift 2 ;;
    --allow-remote) EXTRA+=(--allow-remote); shift ;;
    --max-requests) EXTRA+=(--max-requests "$2"); shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

if [ ! -x .venv-garak/bin/python ]; then
  PYTHON="$(command -v python3.13 || command -v python3.12 || command -v python3.11 || command -v python3 || true)"
  [ -n "$PYTHON" ] || { echo "garak needs Python 3.11 to 3.13" >&2; exit 1; }
  "$PYTHON" -m venv .venv-garak
  .venv-garak/bin/pip install -q -r redteam/requirements.txt
fi

RUN_ID="$(date -u +%Y%m%dT%H%M%S)-$LABEL"
mkdir -p redteam/reports
dotnet build redteam/GarakShim -c Release -v q >/dev/null
dotnet redteam/GarakShim/bin/Release/net10.0/GarakShim.dll --target "$TARGET" --run-id "$RUN_ID" "${EXTRA[@]+"${EXTRA[@]}"}" &
SHIM=$!
trap 'kill $SHIM 2>/dev/null || true' EXIT
for _ in $(seq 1 30); do curl -sf http://127.0.0.1:5099/ >/dev/null && break; sleep 1; done

echo "run $RUN_ID: $SPEC against $TARGET"
.venv-garak/bin/python -m garak \
  --target_type rest -G redteam/garak-rest.json \
  --config "$CONFIG" --spec "$SPEC" \
  --parallel_attempts "$PARALLEL" \
  --report_prefix "$PWD/redteam/reports/$RUN_ID"
echo "reports: redteam/reports/$RUN_ID.report.jsonl and .report.html"
