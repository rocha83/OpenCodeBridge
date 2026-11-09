#!/usr/bin/env bash
# Bateria A: perfil READ-ONLY (arquivo, git de leitura e web), opencode -> llama.
# O prompt mestre do opencode NUNCA e' cortado: quem limita o role e' a lista de
# permissoes (allow-list). Tool negada faz o modelo vazar <|tool_call> no texto,
# entao aqui toda tool que ele usa de fato executa.
#
# Uso: ./battery-chat.sh [chat|responses]
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PROTO="${1:-chat}"
ROOT="/tmp/opencode/llama-pure"
WS="$ROOT/battery-chat-$PROTO"
LLAMA="${LLAMA:-http://127.0.0.1:4199}"
MODEL="llamapure/Gemma4-E4B-GK-Q4"
LOG="$ROOT/battery-chat-$PROTO.log"
START=$(date +%s)

# AGENTS.md global (regras da bridge) entra em todo request; fora durante a rodada.
AGENTS_FILE="${HOME}/.config/opencode/AGENTS.md"
restore_agents() { [ -f "${AGENTS_FILE}.battery-bak" ] && mv "${AGENTS_FILE}.battery-bak" "$AGENTS_FILE"; }
if [ -f "$AGENTS_FILE" ]; then mv "$AGENTS_FILE" "${AGENTS_FILE}.battery-bak"; fi
trap restore_agents EXIT INT TERM

# chat -> openai-compatible (POST /v1/chat/completions)
# responses -> openai nativo (POST /v1/responses)
case "$PROTO" in
  chat)      PKG="@opencode/ai/providers/openai-compatible" ;;
  responses) PKG="@opencode/ai/providers/openai" ;;
  *) echo "protocolo invalido: $PROTO (chat|responses)" >&2; exit 2 ;;
esac

rm -rf "$WS"; mkdir -p "$WS"
cd "$WS" && git init -q && git config user.email t@t && git config user.name t
echo "nota" > nota.txt && git add nota.txt && git commit -qm "commit-inicial-bateria"

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
    "plan": {
      "description": "Plan/MoE: prompt reduzido, somente leitura",
      "mode": "primary",
      "model": "$MODEL",
      "steps": 8,
      "system": "Voce e um assistente de analise em modo SOMENTE LEITURA. Responda em portugues, direto e completo. Ferramentas permitidas: read, glob, grep, webfetch, websearch e git de leitura (status, log, diff, show, branch, remote, rev-parse). Voce NAO escreve, NAO edita, NAO faz commit. Use ferramenta so quando o enunciado exigir dado do repositorio ou da web; caso contrario responda em texto. Nunca escreva sintaxe de chamada de ferramenta no texto da resposta.",
      "permissions": [
        { "action": "*", "resource": "*", "effect": "deny" },
        { "action": "read", "resource": "*", "effect": "allow" },
        { "action": "glob", "resource": "*", "effect": "allow" },
        { "action": "grep", "resource": "*", "effect": "allow" },
        { "action": "webfetch", "resource": "*", "effect": "allow" },
        { "action": "websearch", "resource": "*", "effect": "allow" },
        { "action": "skill", "resource": "*", "effect": "allow" },
        { "action": "shell", "resource": "git status*", "effect": "allow" },
        { "action": "shell", "resource": "git log*", "effect": "allow" },
        { "action": "shell", "resource": "git diff*", "effect": "allow" },
        { "action": "shell", "resource": "git show*", "effect": "allow" },
        { "action": "shell", "resource": "git branch*", "effect": "allow" },
        { "action": "shell", "resource": "git remote*", "effect": "allow" },
        { "action": "shell", "resource": "git rev-parse*", "effect": "allow" },
        { "action": "shell", "resource": "ls*", "effect": "allow" },
        { "action": "shell", "resource": "cat*", "effect": "allow" },
        { "action": "shell", "resource": "rg*", "effect": "allow" },
        { "action": "shell", "resource": "find*", "effect": "allow" },
        { "action": "shell", "resource": "head*", "effect": "allow" },
        { "action": "shell", "resource": "tail*", "effect": "allow" }
      ]
    },
    "leitor": {
      "description": "Subagente leitor somente-leitura",
      "mode": "subagent",
      "model": "$MODEL",
      "system": "Voce e um subagente de leitura. Voce DEVE obter a informacao com ferramenta: use glob com pattern '**/*' ou shell 'ls -a' e leia o resultado real. PROIBIDO responder de memoria ou inventar nomes. Responda em uma linha com os nomes que a ferramenta retornou. Nao escreva sintaxe de tool no texto.",
      "permissions": [
        { "action": "*", "resource": "*", "effect": "deny" },
        { "action": "read", "resource": "*", "effect": "allow" },
        { "action": "glob", "resource": "*", "effect": "allow" },
        { "action": "grep", "resource": "*", "effect": "allow" },
        { "action": "shell", "resource": "ls*", "effect": "allow" }
      ]
    },
    "orch": {
      "description": "Orch: prompt reduzido, somente leitura + delegacao",
      "mode": "primary",
      "model": "$MODEL",
      "steps": 10,
      "system": "Voce e um orquestrador. Voce NAO implementa nada: apenas segmenta o pedido e delega micro-enunciados ao subagent disponivel, depois agrega e verifica o resultado. Voce tem apenas leitura (read, glob, grep, webfetch, websearch, git de leitura) e delegacao. Responda em portugues, curto.",
      "permissions": [
        { "action": "*", "resource": "*", "effect": "deny" },
        { "action": "read", "resource": "*", "effect": "allow" },
        { "action": "glob", "resource": "*", "effect": "allow" },
        { "action": "grep", "resource": "*", "effect": "allow" },
        { "action": "subagent", "resource": "*", "effect": "allow" }
      ]
    }
  }
}
JSON

ask() {
  local n="$1" p="$2" want="${3:-}" agent="${4:-plan}"
  echo "----- [$n] $p  (agent=$agent)"
  local out
  out="$(cd "$WS" && timeout "${STEP_TIMEOUT:-420}" opencode run --standalone --auto \
        --agent "$agent" --model "$MODEL" "$p" 2>&1)"
  local rc=$?
  echo "$out" | tail -14
  local chars=${#out}
  local lixo negado
  lixo="$(echo "$out" | grep -cE '<tool_call|<\|tool_call|<system-reminder')"
  negado="$(echo "$out" | grep -cE 'Permission denied|not permitted')"
  if [ $rc -ne 0 ]; then
    echo "RESULTADO [$n]: FAIL rc=$rc"
  elif [ "$lixo" -gt 0 ]; then
    echo "RESULTADO [$n]: FAIL vazou sintaxe de tool ($lixo)"
  elif echo "$out" | grep -qE 'Missing key|Invalid arguments'; then
    echo "RESULTADO [$n]: FAIL argumento invalido de tool"
  elif [ -n "$want" ] && ! echo "$out" | grep -qi -- "$want"; then
    echo "RESULTADO [$n]: FAIL nao citou '$want'"
  elif [ -n "$want" ]; then
    echo "RESULTADO [$n]: PASS ($chars chars, citou '$want', negadas=$negado)"
  elif [ "$chars" -lt 200 ]; then
    echo "RESULTADO [$n]: FAIL resposta curta ($chars chars)"
  else
    echo "RESULTADO [$n]: PASS ($chars chars, negadas=$negado)"
  fi
}

{
  echo "== bateria READ-ONLY proto=$PROTO modelo=$MODEL llama=$LLAMA ws=$WS"
  ask 1 "Explique em 4 frases por que o ceu e azul."
  ask 2 "Qual a diferenca entre processo e thread? Responda em 3 frases."
  ask 3 "Liste 3 marcos historicos da computacao, cada um com o ano."
  ask 4 "Explique a complexidade do quicksort no melhor e no pior caso."
  ask 5 "Escreva um haiku sobre o mar e comente cada verso em uma linha."
  ask 6 "Explique entropia na termodinamica e na teoria da informacao, 4 frases."
  ask 7 "Rode no shell 'git log --oneline -3' neste diretorio e diga a mensagem do commit." "commit-inicial-bateria"
  ask 8 "Use webfetch em https://example.com e diga o titulo da pagina." "example"
  ask 9 "Delegue ao subagent leitor a tarefa de listar os arquivos .txt do diretorio atual e depois diga quais existem." "nota.txt" orch
} 2>&1 | tee "$LOG"

echo "== log: $LOG"
python3 "$HERE/report.py" --since "$START" --proto "$PROTO" --log "$LOG" \
  --title "Bateria READ-ONLY (arquivo, git leitura, web) - opencode -> llama" --out-dir "$HERE/reports"
