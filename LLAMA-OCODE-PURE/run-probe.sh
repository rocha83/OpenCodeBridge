#!/usr/bin/env bash
# Sonda: opencode -> llama DIRETO (sem bridge), descobrindo qual protocolo o
# opencode usa e se as tools funcionam. O unico intermediario e' o llog.py, que
# so observa e repassa (grava METHOD + PATH de cada request).
#
# Uso:
#   ./run-probe.sh chat "Crie hello.cs e rode dotnet build. Responda BUILD-OK."
#   ./run-probe.sh responses "Crie hello.cs e rode dotnet build. Responda BUILD-OK."
#
# Protocolo = package do provider:
#   chat      -> @opencode/ai/providers/openai-compatible
#   responses -> @opencode/ai/providers/openai-compatible/responses
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PROTO="${1:-chat}"
PROMPT="${2:-Crie hello.cs com Console.WriteLine(\"hello\") e rode dotnet build. Responda BUILD-OK no fim.}"
PORT="${PROXY_PORT:-4199}"
LLAMA="${LLAMA:-http://127.0.0.1:4100}"
ROOT="/tmp/opencode/llama-pure"
WS="$ROOT/ws-$PROTO"

case "$PROTO" in
  chat)      PKG="@opencode/ai/providers/openai-compatible" ;;
  responses) PKG="@opencode/ai/providers/openai" ;;
  *) echo "protocolo invalido: $PROTO (use chat|responses)" >&2; exit 2 ;;
esac

mkdir -p "$WS" "$ROOT"
# O observador agora e' o collector.py (proxy + /metrics). Nao subimos llog.py
# aqui para nao disputar a porta :4199.

# Config do PROJETO (nao toca na global do usuario): provider proprio, para nao
# herdar o "api": "openai-chat" do provider openai do ~/.config/opencode.
cat > "$WS/opencode.json" <<JSON
{
  "\$schema": "https://opencode.ai/config.json",
  "providers": {
    "llamapure": {
      "name": "Llama pure",
      "package": "$PKG",
      "settings": {
        "baseURL": "http://127.0.0.1:$PORT/v1",
        "apiKey": "guest@123"
      },
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
  "model": "llamapure/Gemma4-E4B-GK-Q4",
  "agents": {
    "probe": {
      "description": "Sonda direta llama sem bridge",
      "mode": "primary",
      "model": "llamapure/Gemma4-E4B-GK-Q4"
    }
  }
}
JSON

cd "$WS"
echo "== protocolo=$PROTO package=$PKG ws=$WS"
echo "== rodando opencode (sem bridge, via proxy observador :$PORT)"
timeout "${PROBE_TIMEOUT:-900}" opencode run --standalone --auto \
  --agent probe --model llamapure/Gemma4-E4B-GK-Q4 "$PROMPT" 2>&1 | tail -25

echo "== eventos do coletor (ultimos)"
tail -3 "$ROOT/metrics/events.jsonl" 2>/dev/null || echo "(nenhum request — o opencode nao usou este provider)"
