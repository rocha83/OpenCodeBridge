#!/usr/bin/env bash
# download-llama31-8b.sh
# Baixa Llama 3.1 8B Instruct (GGUF Q4_K_M) para o disco persistente.
# Destino: /media/mint/2a295c1a-afad-44fb-84dc-053406a7bbfb/opt/llama
set -euo pipefail

DEST="/media/mint/2a295c1a-afad-44fb-84dc-053406a7bbfb/opt/llama"
FILE="Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf"
URL="https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF/resolve/main/${FILE}"

mkdir -p "$DEST"
cd "$DEST"

if [[ -f "$FILE" ]]; then
  echo "[download] arquivo já existe, retomando/verificando com -c"
fi

wget -c --show-progress -O "$FILE" "$URL"

echo "[download] concluído: $DEST/$FILE"
ls -lh "$DEST/$FILE"
sha256sum "$DEST/$FILE" | tee "$DEST/${FILE}.sha256"
