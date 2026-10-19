#!/usr/bin/env bash
# Bench simples de tok/s por modelo (llama.cpp direto), media e mediana.
# Uso: ./bench-tok-s.sh [runs] [n_predict]
set -uo pipefail
RUNS="${1:-3}"; NP="${2:-256}"
KEY="Authorization: Bearer guest@123"
PROMPT="Conte de 1 a 300 em portugues, um numero por linha."

bench() {
  local name="$1" url="$2" model="$3"
  echo "=== $name ($url, model=$model) ==="
  local vals=()
  for i in $(seq 1 "$RUNS"); do
    local out
    out="$(curl -s -m 600 "$url/completion" -H "$KEY" -H "Content-Type: application/json" \
      -d "{\"prompt\":\"$PROMPT\",\"n_predict\":$NP,\"ignore_eos\":true,\"cache_prompt\":false,\"temperature\":0.3}")"
    local line
    line="$(printf '%s' "$out" | python3 -c "
import json,sys
d=json.load(sys.stdin); t=d.get('timings',{}) or {}
print('%s %s %.2f %.1f'%(t.get('predicted_n'),t.get('predicted_ms',0),t.get('predicted_per_second',0),t.get('prompt_per_second',0)))
")"
    echo "  run$i: pred_n=$(echo $line|cut -d' ' -f1) gen=$(echo $line|cut -d' ' -f3) tok/s  prompt=$(echo $line|cut -d' ' -f4) tok/s"
    vals+=("$(echo $line|cut -d' ' -f3)")
  done
  printf '%s\n' "${vals[@]}" | python3 -c "
import sys,statistics
v=[float(x) for x in sys.stdin if x.strip()]
print('  MEDIA  = %.2f tok/s'%(statistics.mean(v)))
print('  MEDIANA= %.2f tok/s'%(statistics.median(v)))
print('  MIN/MAX= %.2f / %.2f tok/s'%(min(v),max(v)))
"
}

bench "12B GPU" "http://127.0.0.1:4100" "gemma-4-12b"
bench "E2B CPU" "http://127.0.0.1:4110" "gemma-4-e2b"
