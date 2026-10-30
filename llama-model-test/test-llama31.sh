#!/usr/bin/env bash
# test-llama31.sh
# Verifica servidor Llama 3.1 8B na porta 4120 e faz um chat completion.
set -euo pipefail

BASE="http://127.0.0.1:4120/v1"
KEY="guest@123"
MODEL="Llama-3.1-8B-Instruct-Q4_K_M"

echo "[test] /v1/models:"
curl -s "$BASE/models" -H "Authorization: Bearer $KEY" | python3 -m json.tool 2>/dev/null || curl -s "$BASE/models" -H "Authorization: Bearer $KEY"
echo

echo "[test] chat completion:"
curl -s "$BASE/chat/completions" \
  -H "Authorization: Bearer $KEY" \
  -H "Content-Type: application/json" \
  -d "{\"model\":\"$MODEL\",\"messages\":[{\"role\":\"user\",\"content\":\"Responda apenas: OK\"}],\"max_tokens\":16,\"temperature\":0.1}" \
  | python3 -m json.tool 2>/dev/null || true
echo
echo "[test] fim"
