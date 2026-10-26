#!/bin/bash
# Preflight do L1 (Q4 gate + Q3 espelho) ANTES de subir o driver.
#
# Motivo: o step belts-orch-* chama a bridge :4125 (Q3) e falha com
# causa=infra quando o llama :4110 esta fora. O preflight do driver so cobre a
# Q4; este script cobre as duas pontas, a Web, o lock e a VRAM.
#
# Uso:
#   scripts/war/preflight-l1.sh            # GO/NO-GO
#   BRIDGE_API_KEY=guest@123 scripts/war/preflight-l1.sh
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
API="${BRIDGE_API_KEY:-guest@123}"
FAIL=0
note() { echo "$@"; }
no() { echo "FAIL $*"; FAIL=1; }

cd "$ROOT" || exit 1

echo "=== stack (health-check) ==="
if ! bash scripts/health-check.sh; then FAIL=1; fi

echo "=== bridges ==="
for spec in "4100 q4" "4110 q3"; do
  set -- $spec
  port="$1"; role="$2"
  body=$(curl -s -m 5 -H "Authorization: Bearer $API" "http://127.0.0.1:${port}/health")
  if grep -q '"status":"ok"' <<<"$body"; then
    note "ok   llama-${role} :${port}"
  else
    no "llama-${role} :${port} sem status ok"
  fi
done

up4125=$(curl -s -m 5 -H "Authorization: Bearer $API" http://127.0.0.1:4125/api/status)
if grep -q '"upstream":"http://127.0.0.1:4110"' <<<"$up4125"; then
  note "ok   bridge-gpu2 :4125 -> :4110"
else
  no "bridge-gpu2 :4125 nao aponta para :4110"
fi

echo "=== modelo da Q3 responde (belts-orch) ==="
q3=$(curl -s -m 90 -H "Authorization: Bearer $API" -H "Content-Type: application/json" \
  -d '{"model":"Gemma4-E4B-UD-Q3","max_tokens":4,"messages":[{"role":"user","content":"ok"}]}' \
  http://127.0.0.1:4125/v1/chat/completions)
if grep -q '"choices"' <<<"$q3"; then
  note "ok   :4125 completou"
else
  no ":4125 nao completou (modelo carregando?)"
fi

echo "=== slot/lock ==="
if command -v lsof >/dev/null && lsof /tmp/opencode/circuit.lock >/dev/null 2>&1; then
  no "outro runner ativo (/tmp/opencode/circuit.lock)"
else
  note "ok   sem runner ativo"
fi

free_mib=$(nvidia-smi --query-gpu=memory.free --format=csv,noheader,nounits 2>/dev/null | head -1 | tr -d ' ')
if [ -n "$free_mib" ] && [ "$free_mib" -lt 800 ]; then
  no "VRAM livre baixa (${free_mib} MiB < 800 MiB)"
else
  note "ok   VRAM livre ${free_mib:-?} MiB"
fi

if [ "$FAIL" = "0" ]; then
  echo "=== GO: stack pronto para o L1 ==="
  exit 0
fi
echo "=== NO-GO: corrija os FAIL acima antes de rodar ==="
exit 1
