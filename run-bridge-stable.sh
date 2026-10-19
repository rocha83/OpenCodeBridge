#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
TAIL_FILE="$ROOT/docs/harness/opencode/summary-tail.txt"
TAIL="$(cat "$TAIL_FILE")"
exec dotnet run --project "$ROOT/Rochas.OpenCodeBridge" -- \
  --passthrough-responses true \
  --legacy-harness 0 \
  --anti-repeat 1 --anti-repeat-window 512 \
  --harness-behavior-tail "$TAIL"
