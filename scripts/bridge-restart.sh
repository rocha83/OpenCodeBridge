#!/usr/bin/env bash
# Reinicia a bridge ALVO e espera /api/status com o MainHarness ligado.
# Regra do AGENTS.md: toda bateria comeca com o restart da bridge (binario em uso
# + pool HTTP); zerar contador NAO exige restart (use /api/diag/reset).
#
# Uso:  scripts/bridge-restart.sh [4124|4125|4126] [--print-flags]
#   BRIDGE_API_KEY   credencial do Bearer (default guest@123)
set -uo pipefail

PORT="${1:-4124}"
shift || true
PRINT_FLAGS=0
[ "${1:-}" = "--print-flags" ] && PRINT_FLAGS=1

case "$PORT" in
  4124) UNIT="bridge-gpu" ;;
  4125) UNIT="bridge-gpu2" ;;
  4126) UNIT="bridge-cpu-forced" ;;
  *) echo "ERRO: porta desconhecida '$PORT' (use 4124, 4125 ou 4126)" >&2; exit 2 ;;
esac

API="${BRIDGE_API_KEY:-guest@123}"
URL="http://127.0.0.1:${PORT}"

echo "restart ${UNIT} (${URL})"
if ! sudo -n systemctl restart "$UNIT"; then
  echo "ERRO: sudo -n systemctl restart ${UNIT} falhou (regra sudoers?)" >&2
  exit 1
fi

for _ in $(seq 1 30); do
  if curl -fsS -m 3 -H "Authorization: Bearer ${API}" "${URL}/api/status" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

STATUS=$(curl -fsS -m 5 -H "Authorization: Bearer ${API}" "${URL}/api/status" 2>/dev/null)
if [ -z "$STATUS" ]; then
  echo "ERRO: ${UNIT} nao respondeu /api/status em 30s" >&2
  exit 1
fi

python3 - "$STATUS" "$PRINT_FLAGS" <<'PY'
import json, sys
st = json.loads(sys.argv[1])
print("ok   %s v%s model=%s mode=%s main_harness=%s tails=%s scope=%s uptime=%ss"
      % (st.get("bridge", "?"), st.get("version", "?"),
         st.get("model"), st.get("mode"), st.get("main_harness"),
         st.get("harness_tails"), st.get("tail_scope"), st.get("uptime_seconds")))
PY
if [ "$PRINT_FLAGS" = "1" ]; then
  python3 - "$STATUS" <<'PY'
import json, sys
st = json.loads(sys.argv[1])
for k in ("max_tokens", "context_tokens", "anti_repeat_window",
          "anti_repeat_args_window", "temperature", "thinking",
          "passthrough_responses", "legacy_harness"):
    print("     %-24s %s" % (k, st.get(k)))
sys.exit(0 if (st.get("main_harness") and st.get("harness_tails")) else 1)
PY
  if [ $? -ne 0 ]; then
    echo "ERRO: main_harness/harness_tails OFF — modo bridge nao roda assim" >&2
    exit 1
  fi
fi
