#!/usr/bin/env bash
# install-service.sh
# Instala e inicia o serviço llama na porta 4120.
set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")" && pwd)"
UNIT_SRC="$REPO_DIR/llama-llama31-8b.service"
UNIT_DST="/etc/systemd/system/llama-llama31-8b.service"

if [[ ! -f "$UNIT_SRC" ]]; then
  echo "ERRO: unit não encontrada em $UNIT_SRC" >&2
  exit 1
fi

sudo cp "$UNIT_SRC" "$UNIT_DST"
sudo systemctl daemon-reload
sudo systemctl enable llama-llama31-8b.service
sudo systemctl restart llama-llama31-8b.service

echo "[install] aguardando subida..."
for i in $(seq 1 60); do
  if curl -s http://127.0.0.1:4120/v1/models -H "Authorization: Bearer guest@123" >/dev/null 2>&1; then
    echo "[install] OK: porta 4120 respondendo"
    curl -s http://127.0.0.1:4120/v1/models -H "Authorization: Bearer guest@123"
    echo
    exit 0
  fi
  sleep 2
done

echo "ERRO: timeout aguardando porta 4120" >&2
sudo systemctl status llama-llama31-8b.service --no-pager || true
exit 1
