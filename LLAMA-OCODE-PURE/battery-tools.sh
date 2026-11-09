#!/usr/bin/env bash
# Bateria B: tools completas, opencode -> llama direto (sem bridge).
# Um caso por capacidade, cada um em sessao propria, tudo no mesmo workspace:
#   1 write      2 edit      3 git (shell)      4 webfetch      5 subagent
# O PASS de cada caso e' verificado no disco (nao no texto do modelo).
#
# Uso: ./battery-tools.sh [chat|responses]
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PROTO="${1:-chat}"
ROOT="/tmp/opencode/llama-pure"
WS="$ROOT/battery-tools-$PROTO"
LLAMA="${LLAMA:-http://127.0.0.1:4199}"
MODEL="llamapure/Gemma4-E4B-GK-Q4"
LOG="$ROOT/battery-tools-$PROTO.log"
START=$(date +%s)

# A config global injeta ~/.config/opencode/AGENTS.md (regras da bridge) em todo
# request; aqui medimos opencode+llama puro, entao ele fica fora durante a rodada.
AGENTS_FILE="${HOME}/.config/opencode/AGENTS.md"
restore_agents() { [ -f "${AGENTS_FILE}.battery-bak" ] && mv "${AGENTS_FILE}.battery-bak" "$AGENTS_FILE"; }
if [ -f "$AGENTS_FILE" ]; then mv "$AGENTS_FILE" "${AGENTS_FILE}.battery-bak"; fi
trap restore_agents EXIT INT TERM

case "$PROTO" in
  chat)      PKG="@opencode/ai/providers/openai-compatible" ;;
  responses) PKG="@opencode/ai/providers/openai" ;;
  *) echo "protocolo invalido: $PROTO (chat|responses)" >&2; exit 2 ;;
esac

rm -rf "$WS"; mkdir -p "$WS"
cd "$WS" && git init -q && git config user.email t@t && git config user.name t

cat > "$WS/opencode.json" <<JSON
{
  "\$schema": "https://opencode.ai/config.json",
  "providers": {
    "llamapure": {
      "name": "Llama pure",
      "package": "$PKG",
      "settings": { "baseURL": "$LLAMA/v1", "apiKey": "guest@123" },
      "models": {
        "Gemma4-E4B-GK-Q4": {
          "name": "Gemma4 E4B GK (llama direto)",
          "tool_call": true,
          "body": { "temperature": 0.1 },
          "limit": { "context": 131072, "output": 16384 }
        }
      }
    }
  },
  "model": "$MODEL",
  "agents": {
    "tools": {
      "description": "Bateria de ferramentas",
      "mode": "primary",
      "model": "$MODEL",
      "steps": 12
    },
    "assist": {
      "description": "Subagente que cria arquivos",
      "mode": "subagent",
      "model": "$MODEL",
      "steps": 6
    }
  }
}
JSON

pass=0; fail=0
check() {
  if eval "$2"; then echo "RESULTADO [$1]: PASS"; pass=$((pass+1));
  else echo "RESULTADO [$1]: FAIL"; fail=$((fail+1)); fi
}
run() {
  echo "----- $1"
  local i
  for i in 1 2; do
    (cd "$WS" && timeout "${STEP_TIMEOUT:-600}" opencode run --standalone --auto \
       --agent tools --model "$MODEL" "$1") > "$WS/.last-run.txt" 2>&1
    if [ "$i" -eq 1 ] && grep -qE 'socket connection was closed|Transport:|ECONNRESET|fetch failed' "$WS/.last-run.txt"; then
      echo "(falha de transporte do llama: repetindo 1x)"
      continue
    fi
    break
  done
  tail -16 "$WS/.last-run.txt"
}

{
  echo "== bateria TOOLS proto=$PROTO modelo=$MODEL llama=$LLAMA ws=$WS"

  run "Crie o arquivo nota.md contendo exatamente estas 3 linhas, nesta ordem, e nada mais:
- item1
- item2
- item3"
  check write "[ -f '$WS/nota.md' ] && [ \$(grep -c '^- ' '$WS/nota.md') -ge 3 ]"

  run "Use a tool edit no arquivo nota.md: troque a string exata '- item1' por '- ALTERADO'. Mantenha as outras duas linhas."
  check edit "grep -q 'ALTERADO' '$WS/nota.md'"

  run "Rode via shell: git add nota.md e git commit com a mensagem 'nota inicial'. Depois mostre o log."
  check git "[ \$(cd '$WS' && git log --oneline | wc -l) -ge 1 ]"

  run "Use webfetch na URL https://example.com e diga o titulo da pagina e o que ela contem, 2 frases."
  check webfetch "grep -qiE 'example\.com|documentation examples|Example Domain' '$WS/.last-run.txt'"

  run "Use o subagent 'assist' para criar o arquivo delegado.txt contendo o texto 'veio do subagent'. Nao crie o arquivo voce mesmo."
  check subagent "[ -f '$WS/delegado.txt' ] && grep -q 'veio do subagent' '$WS/delegado.txt'"

  echo "== RESUMO: PASS=$pass FAIL=$fail  (log: $LOG)"
} 2>&1 | tee "$LOG"

python3 "$HERE/report.py" --since "$START" --proto "$PROTO" --log "$LOG" \
  --title "Bateria TOOLS (write, edit, git, webfetch, subagent)" --out-dir "$HERE/reports"
