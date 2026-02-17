#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

PROXY_QUEUE_ENGINE=RabbitMq docker compose -f docker-compose.rabbitmq.yml up -d --build
