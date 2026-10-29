#!/usr/bin/env bash
# Roda UM teste isolado na bridge alvo, com log no repo (logs/isolated).
# Reinicia a bridge antes (regra de bateria) e imprime o veredito + trail do step.
#
# Uso:
#   scripts/run-isolated.sh E-mission-tools
#   scripts/run-isolated.sh E-mission-tools --no-restart
#   scripts/run-isolated.sh "E-ent" --port 4125 --key guest@123
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

NAME="${1:?uso: scripts/run-isolated.sh <nome-do-teste> [--no-restart] [--port N] [--key K]}"
shift || true

PORT=4124
RESTART=1
KEY="${BRIDGE_API_KEY:-guest@123}"
while [ $# -gt 0 ]; do
  case "$1" in
    --no-restart) RESTART=0; shift ;;
    --port) PORT="${2:?--port exige valor}"; shift 2 ;;
    --key)  KEY="${2:?--key exige valor}"; shift 2 ;;
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) echo "ERRO: argumento desconhecido '$1'" >&2; exit 2 ;;
  esac
done

mkdir -p logs/isolated
STAMP=$(date +%Y%m%d-%H%M%S)
LOG="logs/isolated/$(echo "$NAME" | tr -c 'A-Za-z0-9._-' '_')-${STAMP}.log"
echo "log: $LOG"

if [ "$RESTART" -eq 1 ]; then
  BRIDGE_API_KEY="$KEY" bash scripts/bridge-restart.sh "$PORT" --print-flags || exit 1
fi

set +e
# Rastro da fase com tools (turno, reparo, breaker) vai para o log: sem TCTX_DUMP
# a rodada isolada fica sem evidencia de mecanica (medido em 2026-10-29: 11a
# amostra sem `[algo:*]`, sem `reparo:` e sem `breaker:` no log).
export TCTX_DUMP=1
timeout 2400 dotnet run --project Rochas.OpenCodeBridge.Test/Rochas.OpenCodeBridge.Test.csproj \
  -c Release --no-build -- --only "$NAME" --bridge "http://127.0.0.1:${PORT}" --api-key "$KEY" \
  >"$LOG" 2>&1
RC=$?
set -e

echo "== veredito =="
grep -E '^\[test\] (PASS=|FAIL|ABORT)|^FAIL |^PASS ' "$LOG" | tail -5
echo "== workspace publicado =="
grep -E '^  \[ws\]' "$LOG" | tail -1
echo "== ultimo trail do modelo =="
WS=$(grep -oE '/tmp/e2e-[a-z]+-[0-9]{8}-[0-9]{6}-[0-9]+' "$LOG" | tail -1)
[ -n "$WS" ] && [ -f "$WS/_mission.log" ] && tail -12 "$WS/_mission.log"
exit "$RC"
