#!/usr/bin/env bash
# rodar-comparativo.sh — sobe o retrain no llama (:4100), o túnel puro (collector :4199)
# e roda as baterias do harness contra ele, para comparar com a rodada de 08/11.
#
# Uso:
#   ./rodar-comparativo.sh                 # garante llama + collector e roda `tools` (chat)
#   ./rodar-comparativo.sh tools           # bateria WAR tools (onde parou: PASS=4 FAIL=4)
#   ./rodar-comparativo.sh ent             # bateria WAR ent (Plan/conhecimento, texto puro)
#   ./rodar-comparativo.sh chat            # bateria read-only (9 casos, delegação orch)
#   ./rodar-comparativo.sh responses tools # protocolo nativo do provider `openai`
#   ./rodar-comparativo.sh --no-up tools   # nao mexe no llama/collector (ja no ar)
#
# Log: /tmp/opencode/llama-pure/rodar-comparativo-<proto>-<modo>.log
# Nada aqui reescreve ou contem a resposta do modelo: só mede (collector.py).
set -uo pipefail
cd "$(dirname "$0")"
HERE=$(pwd)
ROOT=/tmp/opencode/llama-pure
MODEL_OK="Gemma4-E4B-GK-Q4"
LLAMA_URL="http://127.0.0.1:4100"
TUNEL_URL="http://127.0.0.1:4199"
UP=1
args=()
for a in "$@"; do
  case "$a" in
    --no-up) UP=0 ;;
    *) args+=("$a") ;;
  esac
done
PROTO=chat; MODO=tools
for a in "${args[@]:-}"; do
  case "$a" in
    responses|chat) PROTO="$a" ;;
    tools|ent|chat-battery) MODO="$a" ;;
  esac
done
mkdir -p "$ROOT"
LOG="$ROOT/rodar-comparativo-$PROTO-$MODO.log"
exec > >(tee -a "$LOG") 2>&1
echo "=== $(date -Is) proto=$PROTO modo=$MODO up=$UP ==="

served() {
  curl -s -m 4 -H "Authorization: Bearer guest@123" "$LLAMA_URL/v1/models" 2>/dev/null \
  | python3 -c 'import json,sys
try: print(json.load(sys.stdin)["data"][0]["id"])
except Exception: print("")' 2>/dev/null
}
tunel_ok() { curl -sf -m 3 "$TUNEL_URL/api/status" >/dev/null 2>&1 || curl -sf -m 3 "$TUNEL_URL/health" >/dev/null 2>&1; }

if [ "$UP" -eq 1 ]; then
  m="$(served)"
  if [ "$m" = "$MODEL_OK" ]; then
    echo "[up] llama :4100 já serve $MODEL_OK"
  else
    echo "[up] llama :4100 serve '${m:-nada}' -> subindo o retrain via start-llama-gpu.sh"
    sudo -n /usr/local/bin/start-llama-gpu.sh --defaults
    for _ in $(seq 1 90); do
      [ "$(served)" = "$MODEL_OK" ] && break
      sleep 5
    done
    echo "[up] llama :4100 agora serve '$(served)'"
  fi
  if tunel_ok; then
    echo "[up] túnel :4199 já no ar"
  else
    echo "[up] subindo collector.py (:4199 -> :4100)"
    UPSTREAM="$LLAMA_URL" PORT=4199 METRICS_PORT=9091 \
      setsid nohup python3 "$HERE/collector.py" >>"$ROOT/collector.log" 2>&1 &
    for _ in $(seq 1 20); do tunel_ok && break; sleep 1; done
    tunel_ok && echo "[up] túnel :4199 ok" || { echo "[ERRO] collector nao subiu (ver $ROOT/collector.log)"; exit 1; }
  fi
fi

[ "$(served)" = "$MODEL_OK" ] || { echo "[ERRO] llama :4100 nao esta servindo $MODEL_OK"; exit 1; }

case "$MODO" in
  chat-battery) ./battery-chat.sh "$PROTO" ;;
  tools)        ./battery-war.sh "$PROTO" tools ;;
  ent)          ./battery-war.sh "$PROTO" ent ;;
  *)            echo "[ERRO] modo invalido: $MODO"; exit 2 ;;
esac
rc=$?
echo "=== $(date -Is) fim (rc=$rc) ==="
grep -E "^RESULTADO|^== RESUMO" "$ROOT"/*"$MODO"*.log 2>/dev/null | tail -20
exit "$rc"
