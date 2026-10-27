#!/usr/bin/env bash
# Reenvia amostras de t/s do log JSONL da bridge para /api/test/probe.
#
# A bridge guarda as amostras da sonda apenas em memoria; um restart zera o
# ring e o painel "Throughput por step" do Grafana fica vazio ate o proximo
# StepProbe. Este helper e manual (botao Sonda t/s) e repovoa o ring com os
# eventos `response` que ja estao no log, sem reiniciar nada e sem gravar
# corpos de requisicao/resposta.
#
# Uso: seed-bridge-probe.sh [porta] [log] [api-key] [limite]
set -uo pipefail

PORT="${1:-4124}"
LOG="${2:-/tmp/bridge-gpu.log}"
KEY="${3:-guest@123}"
LIMIT="${4:-500}"

case "$LIMIT" in
  ''|*[!0-9]*) LIMIT=500 ;;
esac
[ -f "$LOG" ] || exit 0
command -v curl >/dev/null 2>&1 || exit 0
command -v jq >/dev/null 2>&1 || exit 0

BASE="http://127.0.0.1:${PORT}"

# Chamado manualmente apos o restart, espera o listener responder.
for _ in $(seq 1 30); do
  if curl -fsS -m 2 -H "Authorization: Bearer ${KEY}" "${BASE}/api/status" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
curl -fsS -m 3 -H "Authorization: Bearer ${KEY}" "${BASE}/api/status" >/dev/null 2>&1 || exit 0

# So eventos de resposta: tps/usage reais. O `step` fixo identifica a origem
# no painel sem competir com os steps publicados pelo StepProbe.
tail -n 5000 "$LOG" 2>/dev/null \
  | jq -c '
      select(.event == "response")
      | {
          step: "bridge-replay",
          mode: (.mode // "chat"),
          source: "bridge-log",
          ts: (.ts // 0),
          time: (((.ts // 0) / 1000) | todate),
          elapsed_s: (.elapsed // 0),
          tps_output: (.tps_output // 0),
          tps_total: (.tps_total // 0),
          input_tokens: (.input_tokens // 0),
          output_tokens: (.output_tokens // 0)
        }
    ' 2>/dev/null \
  | tail -n "$LIMIT" \
  | while IFS= read -r row; do
      [ -n "$row" ] || continue
      curl -fsS -m 3 -X POST \
        -H 'Content-Type: application/json' \
        -H "Authorization: Bearer ${KEY}" \
        --data "$row" "${BASE}/api/test/probe" >/dev/null 2>&1 || true
    done

exit 0
