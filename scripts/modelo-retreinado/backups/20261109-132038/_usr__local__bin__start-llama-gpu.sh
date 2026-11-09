#!/bin/bash
# Llama TurboQuant GPU (Gemma4 E4B Q4_K_M + mmproj, CUDA+Vulkan, TurboQuant KV): porta padrao 4100 (ex-vLLM)
source /usr/local/bin/svc-common.sh
# GPU presente? Sem driver (/dev/nvidia*) nem adianta subir o 12B com offload.
if ! nvidia-smi -L >/dev/null 2>&1 && [ ! -e /dev/nvidia0 ] && [ ! -e /dev/nvidiactl ]; then
  [ -n "$DISPLAY" ] && zenity --error --text="GPU NVIDIA não detectada (sem driver /dev/nvidia*). Verifique o driver proprietario." 2>/dev/null
  echo "ERRO: GPU NVIDIA não detectada (sem driver /dev/nvidia*)" >&2
  exit 1
fi
DEFAULTS=0
[ "${1:-}" = "--defaults" ] && DEFAULTS=1
[ "$DEFAULTS" -eq 1 ] || check_running "llama-gpu" "4100" "Llama GPU E4B Q4" || exit 0
if [ "$DEFAULTS" -eq 1 ] || [ -z "${DISPLAY:-}" ] || ! command -v zenity >/dev/null 2>&1; then
  SVC_PORT=4100; SVC_USER=mint; SVC_PASS=guest@123; SVC_CTX=131072; SVC_SLOTS=1
  export SVC_PORT SVC_USER SVC_PASS SVC_CTX SVC_SLOTS
  command -v fw_allow >/dev/null 2>&1 && fw_allow "$SVC_PORT" tcp "Llama GPU E4B Q4"
else
  ask_llama_config "4100" "131072" "1" "Llama GPU E4B Q4" || exit 0
fi
MODEL_FILE="${LLAMA_GPU_MODEL:-/opt/models/gemma4-e4b-gk-q4/Gemma4-E4B-GK-Q4-Q4_K_M-Imatrix.gguf}"
MMPROJ_FILE="${LLAMA_GPU_MMPROJ:-/opt/models/gemma4-e4b-gk-q4/Gemma4-E4B-GK-Q4-mmproj-bf16.gguf}"
echo "$SVC_PASS" | sudo -S bash -c "
  if [ ! -f \"$MODEL_FILE\" ] || [ ! -f \"$MMPROJ_FILE\" ]; then
    /usr/local/bin/zenity --error --text=\"Modelo/mmproj ausente: $MODEL_FILE / $MMPROJ_FILE\" 2>/dev/null
    exit 1
  fi
  # Garante /dev/nvidia* no live (chroot/ISO pode subir sem os nos).
  nvidia-modprobe -u -c=0 -m 2>/dev/null || true
  printf '[Unit]\nDescription=llama-server GPU TurboQuant [Gemma4-E4B-GK-Q4 retrain, ctx %s, -np %s)\nAfter=network-online.target\nWants=network-online.target\n\n[Service]\nType=simple\nUser=root\nEnvironment=HOME=/root\nEnvironment=TURBO_AUTO_ASYMMETRIC=0\nExecStart=/usr/local/bin/llama-server --alias Gemma4-E4B-GK-Q4 --host 127.0.0.1 --port %s -m $MODEL_FILE --mmproj $MMPROJ_FILE -c %s -np %s -ngl 99 -fa on --cache-type-k turbo3 --cache-type-v turbo3 --mmproj-offload --no-host --load-mode none --op-offload -ot .*=CUDA0 --prio 2 --prio-batch 2 --repeat-penalty 1.08 --dry-multiplier 0.0 --dry-penalty-last-n 1024 --api-key %s\nRestart=on-failure\nRestartSec=10\nTimeoutStartSec=300\nTimeoutStopSec=60\n\n[Install]\nWantedBy=multi-user.target\n' \"$SVC_CTX\" \"$SVC_SLOTS\" \"$SVC_PORT\" \"$SVC_CTX\" \"$SVC_SLOTS\" \"$SVC_PASS\" > /etc/systemd/system/llama-gpu.service
  systemctl daemon-reload
  systemctl restart llama-gpu
"
# Aguarda modelo carregar (ate 5 min) via /health - 12B Q4 leva minutos.
for i in $(seq 1 60); do
  sleep 5
  curl -sf --max-time 3 "http://127.0.0.1:${SVC_PORT}/health" >/dev/null 2>&1 && break
  [ "$i" -eq 60 ] && zenity --error --text="llama-gpu >5min (journalctl -u llama-gpu)" 2>/dev/null && exit 1
done
zenity --info --text="Llama GPU Q4 :$SVC_PORT | ctx $SVC_CTX | $SVC_SLOTS x $((SVC_CTX/SVC_SLOTS)) | CUDA" 2>/dev/null
exit 0
