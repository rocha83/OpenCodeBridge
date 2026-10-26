#!/usr/bin/env bash
# Supervisor da WAR: a cada falha, reinicia a WAR COMPLETA (stamp novo,
# comecando pela raia opencode), nunca apenas o step que falhou.
# Uso: sudo WAR_MAX_ATTEMPTS=5 ./scripts/war-retry-opencode.sh
#   WAR_MAX_ATTEMPTS=0 => repete sem limite
#   WAR_NO_BRIDGE_RESTART=1 => nao reinicia a bridge entre tentativas
set -uo pipefail

ROOT="${WAR_ROOT:-/usr/src/opencodebridge}"
API="${WAR_API_KEY:-guest@123}"
MAX="${WAR_MAX_ATTEMPTS:-5}"
LOG_DIR="${WAR_LOG_DIR:-/tmp/opencode}"
BRIDGE="${WAR_BRIDGE:-http://127.0.0.1:4124}"

mkdir -p "$LOG_DIR"
attempt=0

restart_bridge() {
  if [ "${WAR_NO_BRIDGE_RESTART:-0}" = "1" ]; then return 0; fi
  echo "[war-retry] reiniciando bridge-gpu para estado limpo"
  systemctl restart bridge-gpu
  for _ in $(seq 1 30); do
    if curl -fsS -m 2 -H "Authorization: Bearer $API" "$BRIDGE/api/status" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  echo "[war-retry] ERRO: bridge nao respondeu apos restart" >&2
  return 1
}

while :; do
  attempt=$((attempt + 1))
  stamp=$(date +%Y%m%d-%H%M%S)
  log="$LOG_DIR/war-full-attempt-${attempt}-${stamp}.log"
  echo "=== WAR tentativa $attempt/${MAX:-infinito} stamp=$stamp ==="
  rm -f /tmp/opencode/war.lock "$ROOT/logs/.rerun-guard.json"
  restart_bridge || exit 2

  cd "$ROOT"
  dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- \
    --api-key "$API" --war-all --war-no-cpu --war-retries 1 \
    >"$log" 2>&1
  rc=$?
  echo "=== tentativa $attempt rc=$rc log=$log ==="

  # Corrida do dotnet run: o filho do teste pode sobreviver alguns segundos
  # depois do rc. Se o proximo attempt subir nesse intervalo, o preflight
  # "sem_run_orfao" bloqueia a WAR inteira. Espera (e limpa) antes de seguir.
  for _ in $(seq 1 30); do
    pgrep -f 'Rochas[.]OpenCodeBridge[.]Test' >/dev/null 2>&1 || break
    sleep 1
  done
  if pgrep -f 'Rochas[.]OpenCodeBridge[.]Test' >/dev/null 2>&1; then
    pkill -TERM -f '[R]ochas[.]OpenCodeBridge[.]Test' || true
    sleep 2
  fi

  if [ "$rc" -eq 0 ]; then
    echo "[war-retry] WAR VERDE na tentativa $attempt"
    exit 0
  fi

  if [ "$MAX" -gt 0 ] && [ "$attempt" -ge "$MAX" ]; then
    echo "[war-retry] WAR VERMELHA apos $attempt tentativa(s); ultimo log: $log" >&2
    exit "$rc"
  fi

  echo "[war-retry] FALHA -> reiniciando WAR COMPLETA desde a raia opencode (nova stamp)"
  sleep "${WAR_RETRY_DELAY:-3}"
done
