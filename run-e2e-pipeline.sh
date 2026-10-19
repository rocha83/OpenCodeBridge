#!/usr/bin/env bash
# Pipeline E2E sequencial ativavel por fase.
# Uso: ./run-e2e-pipeline.sh --phase 0|1|2|3|all
set -euo pipefail

PHASE="${1:-all}"
if [[ "$PHASE" == "--phase" ]]; then PHASE="${2:-all}"; fi

LOG=/tmp/opencode/pipeline.log
mkdir -p /tmp/opencode
exec > >(tee -a "$LOG") 2>&1

echo "=== Pipeline E2E - $(date) - phase=$PHASE ==="

phase0() {
  echo "--- Phase 0: health ---"
  for url in http://127.0.0.1:4100/health http://127.0.0.1:4110/health; do
    echo -n "$url: "; curl -s -m 5 -H "Authorization: Bearer guest@123" "$url"; echo
  done
  for url in http://127.0.0.1:4124/api/status http://127.0.0.1:4125/api/status; do
    echo -n "$url: "; curl -s -m 5 -H "Authorization: Bearer guest@123" "$url"; echo
  done
  nvidia-smi --query-gpu=memory.used,memory.free --format=csv,noheader
}

phase1() {
  echo "--- Phase 1: unit tests (skip e2e) ---"
  cd /media/mint/3686C649614854E61/Projetos/Git/OpenCodeBridge
  dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --skip-e2e --api-key guest@123 --bridge http://127.0.0.1:4124
}

phase2() {
  echo "--- Phase 2: bridge pure E2E (science + mission) ---"
  cd /media/mint/3686C649614854E61/Projetos/Git/OpenCodeBridge
  dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --only E-science --api-key guest@123 --bridge http://127.0.0.1:4124
  dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --only E-mission-tools --api-key guest@123 --bridge http://127.0.0.1:4124
}

phase3() {
  echo "--- Phase 3: standalone bridge calls (opencode link) ---"
  # oi
  curl -s -m 60 -H "Authorization: Bearer guest@123" -H "Content-Type: application/json" \
    http://127.0.0.1:4124/v1/responses -d '{"model":"gemma-4-12b","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"oi"}]}],"stream":false}' | head -c 500; echo
  # topico geral
  curl -s -m 60 -H "Authorization: Bearer guest@123" -H "Content-Type: application/json" \
    http://127.0.0.1:4124/v1/responses -d '{"model":"gemma-4-12b","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Explique rapidamente o que e DDD"}]}],"stream":false}' | head -c 500; echo
  # cientifico (E[X] da exponencial)
  curl -s -m 60 -H "Authorization: Bearer guest@123" -H "Content-Type: application/json" \
    http://127.0.0.1:4124/v1/responses -d '{"model":"gemma-4-12b","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Qual a esperanca E[X] de uma exponencial de parametro lambda? Responda so texto."}]}],"stream":false}' | head -c 500; echo
  # devpipe (tools: read/grep/shell)
  curl -s -m 90 -H "Authorization: Bearer guest@123" -H "Content-Type: application/json" \
    http://127.0.0.1:4124/v1/responses -d '{"model":"gemma-4-12b-build","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Liste os arquivos .cs do diretorio atual usando a tool glob e diga quantos sao."}]}],"stream":false,"tools":[{"type":"function","name":"glob","description":"Lista arquivos por padrao","parameters":{"type":"object","properties":{"pattern":{"type":"string"}},"required":["pattern"]}}]}' | head -c 500; echo
  # missao (tools: write + shell)
  curl -s -m 90 -H "Authorization: Bearer guest@123" -H "Content-Type: application/json" \
    http://127.0.0.1:4124/v1/responses -d '{"model":"gemma-4-12b-build","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Escreva o arquivo /tmp/opencode/missao.txt com o conteudo ok usando a tool write."}]}],"stream":false,"tools":[{"type":"function","name":"write","description":"Escreve arquivo","parameters":{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}}]}' | head -c 500; echo
}

case "$PHASE" in
  0) phase0 ;;
  1) phase1 ;;
  2) phase2 ;;
  3) phase3 ;;
  all) phase0; phase1; phase2; phase3 ;;
  *) echo "phase invalida: $PHASE"; exit 1 ;;
esac

echo "=== Pipeline phase=$PHASE done $(date) ==="
