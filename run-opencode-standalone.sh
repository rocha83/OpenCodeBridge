#!/usr/bin/env bash
# Pipeline opencode STANDALONE (link real) — mesma bateria da suite, via `opencode run`.
# Uso: ./run-opencode-standalone.sh --task oi|tools|write|devpipe|science|mission|all [--agent build|exec]
set -uo pipefail

TASK="${1:-all}"; [[ "$TASK" == "--task" ]] && TASK="${2:-all}"
AGENT="${3:-build}"; [[ "${3:-}" == "--agent" ]] && AGENT="${4:-build}"
MODEL="openai/gemma-4-12b-build"
[[ "$AGENT" == "exec" ]] && MODEL="llama/gemma-4-e2b-exec"

LOG=/tmp/opencode/standalone.log
mkdir -p /tmp/opencode/standalone
exec > >(tee -a "$LOG") 2>&1
cd /tmp/opencode/standalone

run() {
  local name="$1"; shift
  local prompt="$1"; shift
  local expect="${1:-}"
  echo "=== [$name] agent=$AGENT model=$MODEL ==="
  local out
  out="$(timeout 300 opencode run --standalone --agent "$AGENT" --model "$MODEL" "$prompt" 2>&1)"
  echo "$out" | tail -20
  if [[ -n "$expect" ]]; then
    if echo "$out" | grep -qiE "$expect"; then echo "PASS $name"; else echo "FAIL $name (esperado: $expect)"; fi
  fi
}

task_oi()      { run oi "responda apenas: ok" "ok"; }
task_tools()   { run tools "Use a tool shell para rodar 'date +%F' e diga a data." "2026|202[0-9]"; }
task_write()   { run write "Use a tool write para criar o arquivo /tmp/opencode/standalone/ok.txt com o conteudo 'ok' e depois leia com read para confirmar." "ok"; }
task_devpipe() { run devpipe "Use glob para listar os arquivos *.cs deste diretorio e diga quantos sao. Se nao houver, diga 0." "[0-9]"; }
task_science() { run science "Qual a esperanca E[X] de uma exponencial de parametro lambda? Responda so texto, sem tools." "1/lambda|lambda"; }
task_mission() { run mission "Use a tool shell para rodar exatamente: echo missao-ok; diga a saida." "missao-ok"; }

case "$TASK" in
  oi) task_oi ;; tools) task_tools ;; write) task_write ;; devpipe) task_devpipe ;;
  science) task_science ;; mission) task_mission ;;
  all) task_oi; task_tools; task_write; task_devpipe; task_science; task_mission ;;
  *) echo "task invalida: $TASK"; exit 1 ;;
esac
echo "=== standalone task=$TASK done $(date) ==="
