#!/bin/bash
# Preflight do stack local: llama, bridges, Web e auth. Falha rapido (exit 1)
# com a lista do que esta fora — antes de qualquer suite de testes.
set -uo pipefail
API="${BRIDGE_API_KEY:-guest@123}"
WEB_PORT="${BRIDGE_WEB_PORT:-4130}"
FAIL=0

check() { # nome url [bearer]
  local name="$1" url="$2" bearer="${3:-}"
  local args=(-s -m 5 -o /dev/null -w '%{http_code}')
  [ -n "$bearer" ] && args+=(-H "Authorization: Bearer $bearer")
  local code; code=$(curl "${args[@]}" "$url" 2>/dev/null || echo 000)
  if [ "$code" = "200" ]; then
    echo "ok   $name ($url)"
  else
    echo "FAIL $name ($url) -> HTTP ${code}"; FAIL=1
  fi
}

check "llama-gpu  :4100" "http://127.0.0.1:4100/health"
check "llama-gpu2 :4110" "http://127.0.0.1:4110/health"
check "bridge-gpu :4124" "http://127.0.0.1:4124/api/status" "$API"
check "bridge-gpu2:4125" "http://127.0.0.1:4125/api/status" "$API"
check "web-login  :$WEB_PORT" "http://127.0.0.1:$WEB_PORT/Account/Login"

# Ping do agente 1: exige login. Prova que a Web fala com a bridge, nao so
# que a porta abriu. Credenciais do appsettings (BridgeAuth).
JAR=$(mktemp)
HTML=$(curl -s -m 5 -c "$JAR" "http://127.0.0.1:$WEB_PORT/Account/Login" 2>/dev/null)
TOKEN=$(grep -oP 'name="__RequestVerificationToken"[^>]*value="\K[^"]+' <<<"$HTML" | head -1)
curl -s -m 5 -b "$JAR" -c "$JAR" -o /dev/null \
  --data-urlencode "email=${BRIDGE_WEB_EMAIL:-admin@bridge.pcss.com.br}" \
  --data-urlencode "password=${BRIDGE_WEB_PASSWORD:-123}" \
  --data-urlencode "__RequestVerificationToken=$TOKEN" \
  "http://127.0.0.1:$WEB_PORT/Account/Login" 2>/dev/null
PING=$(curl -s -m 5 -b "$JAR" "http://127.0.0.1:$WEB_PORT/Chat/Ping?agentId=1" 2>/dev/null)
rm -f "$JAR"
if grep -q '"ok":true' <<<"$PING"; then
  echo "ok   web-ping  (agentId=1)"
else
  echo "FAIL web-ping  (agentId=1) -> ${PING:0:120}"; FAIL=1
fi

# VRAM (informativo; nao derruba o preflight).
nvidia-smi --query-gpu=memory.used,memory.free --format=csv,noheader 2>/dev/null | sed 's/^/gpu  /'

exit "$FAIL"
