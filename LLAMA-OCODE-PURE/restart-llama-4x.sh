#!/usr/bin/env bash
# restart-llama-4x.sh — sobe o retrain (Gemma4-E4B-GK-Q4, Q4_K_M-Imatrix) com 4 slots.
#
# Tenta 4 x 128k (-c 524288 -np 4) e, se nao couber na VRAM (12 GiB da RTX 3060),
# cai para 4 x 112k (-c 458752 -np 4). O que vale e o que o llama-server aceitar:
# a checagem e' /health 200 + /v1/models com o alias certo.
#
# Uso: ./restart-llama-4x.sh [--check]
#   --check  so relata o que esta servido agora, nao mexe em nada
#
# Log: /tmp/opencode/llama-pure/restart-llama-4x.log
set -uo pipefail
LOG=/tmp/opencode/llama-pure/restart-llama-4x.log
mkdir -p "$(dirname "$LOG")"
MODEL=Gemma4-E4B-GK-Q4
H=http://127.0.0.1:4100

served() {
  curl -s -m 5 -H 'Authorization: Bearer guest@123' "$H/v1/models" 2>/dev/null \
  | python3 -c 'import json,sys
try: print(json.load(sys.stdin)["data"][0]["id"])
except Exception: print("")' 2>/dev/null
}
health() { curl -s -m 4 -o /dev/null -w "%{http_code}" "$H/health" 2>/dev/null; }
slots()  { curl -s -m 5 -H 'Authorization: Bearer guest@123' "$H/props" 2>/dev/null \
  | python3 -c 'import json,sys
try:
  d=json.load(sys.stdin); print(d.get("total_slots"), d.get("default_generation_settings",{}).get("n_ctx"))
except Exception: print("? ?")' 2>/dev/null; }
vram()  { nvidia-smi --query-gpu=memory.used,memory.total --format=csv,noheader 2>/dev/null; }

if [ "${1:-}" = "--check" ]; then
  echo "servido : $(served)"
  echo "health  : $(health)"
  echo "slots/ctx: $(slots)"
  echo "vram    : $(vram)"
  exit 0
fi

exec > >(tee -a "$LOG") 2>&1
echo "=== $(date -Is) inicio ==="

attempt() {
  local ctx="$1" sl="$2"
  echo "-- tentativa: ctx=$ctx slots=$sl ($(( ctx / sl )) por slot)"
  sudo -n systemctl stop llama-gpu >/dev/null 2>&1 || true
  sudo -n env LLAMA_GPU_CTX="$ctx" LLAMA_GPU_SLOTS="$sl" \
    /usr/local/bin/start-llama-gpu.sh --defaults
  local i
  for i in $(seq 1 60); do
    [ "$(health)" = "200" ] && break
    sleep 5
  done
  if [ "$(health)" = "200" ] && [ "$(served)" = "$MODEL" ]; then
    echo "-- OK: $(served) | slots/ctx=$(slots) | vram=$(vram)"
    return 0
  fi
  echo "-- FALHOU (health=$(health) servido='$(served)')"
  sudo -n journalctl -u llama-gpu -n 12 --no-pager 2>/dev/null | tail -12
  return 1
}

if attempt 524288 4; then
  echo "=== $(date -Is) fim: 4 x 128k ==="
elif attempt 458752 4; then
  echo "=== $(date -Is) fim: fallback 4 x 112k ==="
else
  echo "=== $(date -Is) fim: FALHOU nas duas (128k e 112k) ==="
  exit 1
fi
