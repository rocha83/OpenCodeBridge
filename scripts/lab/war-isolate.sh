#!/usr/bin/env bash
# war-isolate.sh "<filtro1,filtro2,...>" [rotulo]
#
# Isola FAILs da esteira SEM as 4 raias: roda os testes pedidos num UNICO
# processo (lista --only por virgula, sem fail-fast) contra a bridge alvo ja
# configurada. Nao mexe nos knots da bridge e nao reinicia nada: a rodada de
# comparacao (war-round.sh) e' quem fixa tails/anti-repeat.
#
# Artefatos em logs/levels/iso-<rotulo>-<stamp>.console.log do repositorio.
set -uo pipefail
FILTERS="${1:?uso: war-isolate.sh \"<filtro1,filtro2>\" [rotulo]}"
LABEL="${2:-iso}"
REPO="${REPO:-/usr/src/opencodebridge}"
BRIDGE="${WAR_BRIDGE:-http://127.0.0.1:4124}"
API_KEY="${WAR_API_KEY:-guest@123}"
MIN_VRAM="${WAR_MIN_FREE_VRAM:-256}"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$REPO/logs/levels/iso-${LABEL}-${STAMP}.console.log"
BUILD_LOG="$REPO/logs/levels/.build-iso-${LABEL}.log"

echo "== isolado ${LABEL} (${STAMP}) filtros=${FILTERS}"
echo "== bridge alvo: ${BRIDGE}"

# BUILD -> REDEPLOY -> RESTART antes de medir: o isolado roda o binario corrente,
# nunca o de uma rodada anterior (regra do laboratorio: build sempre que reiniciar).
if [ "${ISO_REBUILD:-1}" = "1" ]; then
  echo "== build (Release) -> $BUILD_LOG"
  if ! sudo -n bash -c "cd '$REPO' && dotnet build -c Release Rochas.OpenCodeBridge.slnx > '$BUILD_LOG' 2>&1"; then
    echo "FAIL: build quebrou — ultimas linhas:"; tail -20 "$BUILD_LOG"; exit 1
  fi
  grep -E "Aviso\(s\)|Erro\(s\)|Compilação com êxito" "$BUILD_LOG" | tail -3
  sudo -n systemctl restart "${BRIDGE_UNIT:-bridge-gpu}"
  for _ in $(seq 1 20); do
    curl -fsS -H "Authorization: Bearer $API_KEY" "$BRIDGE/api/status" > /dev/null && break
    sleep 1
  done
  echo "== bridge reiniciada (${BRIDGE_UNIT:-bridge-gpu})"
fi

sudo -n bash -c "cd '$REPO' && WAR_MIN_FREE_VRAM='$MIN_VRAM' \
  dotnet Rochas.OpenCodeBridge.Test/bin/Release/net10.0/Rochas.OpenCodeBridge.Test.dll \
  --only '$FILTERS' --no-stop-on-fail --mode bridge \
  --bridge '$BRIDGE' --api-key '$API_KEY' 2>&1 | tee '$OUT'"
rc=${PIPESTATUS[0]:-$?}
echo "== rc=$rc artefato=$OUT"
exit "$rc"
