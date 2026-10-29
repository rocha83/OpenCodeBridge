#!/usr/bin/env bash
# Build Release + bateria de units (sem E2E). Fail-fast do runner vale aqui:
# o primeiro FAIL aborta a rodada.
#
# Uso:
#   scripts/check-units.sh                 # build + todos os units
#   scripts/check-units.sh --no-build      # reaproveita o binario atual
#   scripts/check-units.sh --only U-tails  # build + units filtrados
#   BRIDGE_API_KEY=... scripts/check-units.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

API_KEY="${BRIDGE_API_KEY:-guest@123}"
ONLY=""
NOBUILD=0
while [ $# -gt 0 ]; do
  case "$1" in
    --only)    ONLY="${2:?--only exige valor}"; shift 2 ;;
    --no-build) NOBUILD=1; shift ;;
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) echo "ERRO: argumento desconhecido '$1'" >&2; exit 2 ;;
  esac
done

if [ "$NOBUILD" -eq 0 ]; then
  echo "== build Release =="
  dotnet build Rochas.OpenCodeBridge.slnx -c Release --nologo -v:minimal
fi

ARGS=(--skip-e2e --api-key "$API_KEY")
[ -n "$ONLY" ] && ARGS+=(--only "$ONLY")

echo "== units =="
set +e
dotnet run --project Rochas.OpenCodeBridge.Test/Rochas.OpenCodeBridge.Test.csproj \
  -c Release --no-build -- "${ARGS[@]}" 2>&1 | tee /tmp/units-$(date +%H%M%S).log | grep -E '^\[test\] (PASS=|FAIL|ABORT)|^FAIL |^PASS '
RC=${PIPESTATUS[0]}
set -e
[ "$RC" -eq 0 ] || { echo "ERRO: units falharam (rc=$RC)" >&2; exit "$RC"; }
