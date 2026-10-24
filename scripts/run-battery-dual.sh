#!/usr/bin/env bash
# =============================================================================
#  run-battery-dual.sh — bateria completa nos DOIS transportes, por modelo.
# -----------------------------------------------------------------------------
#  Para cada modelo (Q4 :4124 e Q3 :4125), em sequencia dentro do modelo e em
#  paralelo entre modelos:
#    G1 unit          — sem modelo (rapido): breaker, escape, cauda, temperatura
#    G2 tools-dual    — matriz de tools em modo puro E em opencode (--dual)
#    G3 topics        — ciencia (GPU/CPU) + missao com tools
#    G4 tools-rest    — tools-full, responses-simple/tools, devpipe-tools, mitigacao
#    G5 belts         — faixas azul..preta (bridge pura)
#    G6 pipe-puro     — 1 pipe em modo puro (E-responses-simple)
#    G7 opencode      — modo opencode implicito (--standalone, 6 enunciados)
#    G8 rest          — senior, enterprise, front, scripter, orch, arquitetura
#
#  Cada grupo grava seu log e uma linha de resumo; o resumo geral fica em
#  <out>/SUMMARY.txt. Roda em background:
#     nohup ./run-battery-dual.sh > /tmp/opencode/battery.out 2>&1 &
#
#  Nada aqui altera as units: a suite fala HTTP com as bridges ja rodando.
# =============================================================================
set -uo pipefail

REPO="${REPO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
OUT="/tmp/opencode/battery/$(date +%Y%m%d-%H%M%S)"
API_KEY="guest@123"
mkdir -p "$OUT"

TEST=(dotnet run --project "$REPO/Rochas.OpenCodeBridge.Test" -c Release --no-build --)

# modelo | porta | provider do opencode | modelo servido
MODELS=(
  "q4|4124|openai|Gemma4-E4B-Q4"
  "q3|4125|llama|Gemma4-E4B-UD-Q3"
)

summary() { # etapa log
  local etapa="$1" log="$2"
  local line
  line="$(grep -E '^\[test\] PASS=' "$log" 2>/dev/null | tail -1)"
  [ -z "$line" ] && line="(sem resumo: $(tail -1 "$log" 2>/dev/null | cut -c1-120))"
  printf '%-10s %-16s %s\n' "${MODEL_TAG}" "${etapa}" "${line}" >> "$OUT/SUMMARY.txt"
  echo "[$MODEL_TAG] $etapa: $line"
}

run_group() { # etapa args...
  local etapa="$1"; shift
  local log="$OUT/${MODEL_TAG}-${etapa}.log"
  echo "[$MODEL_TAG] >>> $etapa $(date +%H:%M:%S)" >&2
  "${TEST[@]}" --api-key "$API_KEY" --bridge "http://127.0.0.1:$PORT" \
    --role build --standalone-provider "$PROVIDER" "$@" > "$log" 2>&1
  summary "$etapa" "$log"
}

battery() { # tag port provider model
  MODEL_TAG="$1"; PORT="$2"; PROVIDER="$3"; SERVED="$4"
  echo "==================== $MODEL_TAG (: $PORT, opencode $PROVIDER) $(date -Is) ====================" >> "$OUT/SUMMARY.txt"

  # G1 unit (sem modelo): valida binario novo em PROD (tail_scope, breaker, temp).
  run_group unit --skip-e2e

  # G2 tools nos dois transportes (cada caso roda 2x: -pure e -opencode).
  run_group tools-dual --only E-dual

  # G3 topicos.
  run_group topics-science --only E-science
  run_group topics-mission --only E-mission

  # G4 resto dos testes de tools.
  run_group tools-full --only E-tools-full
  run_group tools-responses --only E-responses
  run_group tools-devpipe --only E-devpipe-tools
  run_group tools-mitigation --only E-error-mitigation

  # G5 belts (azul -> preta), bridge pura.
  run_group belts --only E-belts

  # G6 1 pipe em modo puro.
  run_group pipe-puro --only E-responses-simple

  # G7 modo opencode implicito (opencode run, 6 enunciados).
  run_group opencode-implicito --standalone --only E-standalone

  # G8 resto da bateria (senior, enterprise, front, scripter, orch, arquitetura).
  run_group rest-senior --only E-senior
  run_group rest-enterprise --only E-ent-
  run_group rest-front --only E-front
  run_group rest-scripts --only E-scripter
  run_group rest-companion --only E-companion
  run_group rest-exec --only E-exec
  run_group rest-orch --only E-orch
  run_group rest-arch --only E-iot
  run_group rest-fullcycle --only E-full-cycle
  run_group rest-healthy --only E-healthy
  run_group rest-stream --only E-stream
  run_group rest-review --only E-review

  echo "[$MODEL_TAG] FIM $(date -Is)" >> "$OUT/SUMMARY.txt"
}

for spec in "${MODELS[@]}"; do
  IFS='|' read -r tag port prov served <<< "$spec"
  battery "$tag" "$port" "$prov" "$served" &
done
wait

echo "==== CONSOLIDADO ====" >> "$OUT/SUMMARY.txt"
echo "logs em $OUT" >> "$OUT/SUMMARY.txt"
echo "bateria concluida: $OUT"
grep -E '^(q4|q3) ' "$OUT/SUMMARY.txt" | tail -40
