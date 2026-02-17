#!/usr/bin/env bash
set -euo pipefail

# Chaos soak test harness.
# Runs proxy with fault injection enabled and polls readiness over time.

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_NAME="${ENV_NAME:-Stress}"
DURATION_SEC="${DURATION_SEC:-900}"
HEALTH_PORT="${HEALTH_PORT:-18082}"

DOTNET_ENVIRONMENT="$ENV_NAME" \
TDS_PROXY_ADMIN_TOKEN="${TDS_PROXY_ADMIN_TOKEN:-stress-admin-token}" \
dotnet run --project "$ROOT_DIR/TDSQueue.Proxy.csproj" >/tmp/tdsqueue-chaos.log 2>&1 &
PID=$!

cleanup() {
  kill "$PID" || true
  wait "$PID" 2>/dev/null || true
}
trap cleanup EXIT

sleep 4

echo "Starting soak for ${DURATION_SEC}s against /health/ready"
START=$(date +%s)
OK=0
DEGRADED=0
FAIL=0

while true; do
  NOW=$(date +%s)
  if (( NOW - START >= DURATION_SEC )); then
    break
  fi

  STATUS=$(curl -s -o /tmp/tdsqueue-health.json -w "%{http_code}" "http://127.0.0.1:${HEALTH_PORT}/health/ready" || true)

  if [[ "$STATUS" == "200" ]]; then
    OK=$((OK+1))
  elif [[ "$STATUS" == "503" ]]; then
    DEGRADED=$((DEGRADED+1))
  else
    FAIL=$((FAIL+1))
  fi

  sleep 2

done

echo "Soak summary: ok=${OK}, degraded=${DEGRADED}, fail=${FAIL}"
echo "Detailed logs: /tmp/tdsqueue-chaos.log"
