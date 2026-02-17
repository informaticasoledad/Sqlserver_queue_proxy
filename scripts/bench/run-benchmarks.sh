#!/usr/bin/env bash
set -euo pipefail

# Quick comparative benchmark runner by queue engine.
# Requires: sqlcmd, jq, dotnet.

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RESULTS_DIR="$ROOT_DIR/scripts/bench/results"
mkdir -p "$RESULTS_DIR"

SQL_HOST="${SQL_HOST:-127.0.0.1}"
SQL_USER="${SQL_USER:-sa}"
SQL_PASSWORD="${SQL_PASSWORD:-Your_password123}"
LISTEN_PORT="${LISTEN_PORT:-14330}"
ITERATIONS="${ITERATIONS:-200}"

run_engine() {
  local engine="$1"
  local env_name="Development"
  local out_file="$RESULTS_DIR/${engine,,}-$(date +%Y%m%d-%H%M%S).csv"

  echo "engine,iteration,elapsed_ms" > "$out_file"

  local appsettings="$ROOT_DIR/appsettings.${env_name}.json"
  cp "$appsettings" "$appsettings.tmp"

  jq --arg eng "$engine" '.Proxy.QueueEngine = $eng' "$appsettings.tmp" > "$appsettings"

  DOTNET_ENVIRONMENT="$env_name" dotnet run --project "$ROOT_DIR/TDSQueue.Proxy.csproj" >/tmp/tdsqueue-bench.log 2>&1 &
  local pid=$!

  sleep 3

  for i in $(seq 1 "$ITERATIONS"); do
    local start_ns end_ns elapsed_ms
    start_ns=$(date +%s%N)
    sqlcmd -S "127.0.0.1,${LISTEN_PORT}" -U "$SQL_USER" -P "$SQL_PASSWORD" -Q "SELECT 1" -l 5 >/dev/null
    end_ns=$(date +%s%N)
    elapsed_ms=$(( (end_ns - start_ns) / 1000000 ))
    echo "$engine,$i,$elapsed_ms" >> "$out_file"
  done

  kill "$pid" || true
  wait "$pid" 2>/dev/null || true

  mv "$appsettings.tmp" "$appsettings"
  echo "Result written: $out_file"
}

run_engine "Channel"
run_engine "RabbitMq"
run_engine "Redis"
