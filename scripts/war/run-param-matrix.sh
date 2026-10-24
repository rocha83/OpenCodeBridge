#!/bin/bash
# Wrapper da matriz de amostragem x harness (ver param_matrix.py).
# Uso: scripts/war/run-param-matrix.sh --stage 1 --reps 5
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="${REPO:-$(cd "$HERE/../.." && pwd)}"
DLL="$REPO/Rochas.OpenCodeBridge/bin/Release/net9.0/Rochas.OpenCodeBridge.dll"

[ -f "$DLL" ] || dotnet build -c Release "$REPO/Rochas.OpenCodeBridge.slnx"

# O llama precisa estar no ar: sem ele nao ha o que medir (fail fast).
LLAMA="${MATRIX_LLAMA:-http://127.0.0.1:4100}"
curl -sf -m 5 -H "Authorization: Bearer ${LEVEL_API_KEY:-guest@123}" "$LLAMA/props" >/dev/null \
  || { echo "llama fora do ar em $LLAMA"; exit 2; }

exec python3 "$HERE/param_matrix.py" "$@"
