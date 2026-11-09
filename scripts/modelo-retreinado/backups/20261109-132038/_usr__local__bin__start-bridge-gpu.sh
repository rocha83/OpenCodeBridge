#!/bin/bash
# OpenCode Bridge GPU (:4124 -> llama :4100 Gemma4-E4B-GK-Q4): reinicia o unit
source /usr/local/bin/svc-common.sh
DEFAULTS=0
[ "${1:-}" = "--defaults" ] && DEFAULTS=1
if [ "$DEFAULTS" -eq 1 ] || [ -z "${DISPLAY:-}" ] || ! command -v zenity >/dev/null 2>&1; then
  SVC_PORT=4124; SVC_UPSTREAM_URL=http://127.0.0.1:4100; SVC_UPSTREAM_KEY=guest@123; SVC_MODEL=Gemma4-E4B-GK-Q4; SVC_CTX=131072; SVC_USER=mint; SVC_PASS=guest@123; SVC_DIAG=desligado
  export SVC_PORT SVC_UPSTREAM_URL SVC_UPSTREAM_KEY SVC_MODEL SVC_CTX SVC_USER SVC_PASS SVC_DIAG
  command -v fw_allow >/dev/null 2>&1 && fw_allow "$SVC_PORT" tcp "OpenCode Bridge GPU"
else
  ask_bridge_config "4124" "http://127.0.0.1:4100" "Gemma4-E4B-GK-Q4" "131072" "OpenCode Bridge GPU" || exit 0
fi
DIAG_FLAGS=$(bridge_diag_flags)
echo "$SVC_PASS" | sudo -S bash -c "
printf '[Unit]\nDescription=OpenCode Bridge GPU (:4124 -> llama :4100 Gemma4-E4B-GK-Q4)\nAfter=network-online.target llama-gpu.service\nWants=network-online.target llama-gpu.service\n\n[Service]\nType=simple\nUser=root\nEnvironment=HOME=/root\nWorkingDirectory=/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net10.0\nExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll --listen 0.0.0.0 --port %s --upstream %s --model %s --thinking events --temperature 0.2 --max-tokens 16384 --context-guard 1 --context-tokens %s --api-key %s --passthrough-responses true --main-harness 0 --harness-tails 1 --tail-scope all --legacy-harness 0 --anti-repeat 1 --anti-repeat-retry 1 --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 --anti-repeat-window 512 %s --log /tmp/bridge-gpu.log\nRestart=on-failure\nRestartSec=10\n\n[Install]\nWantedBy=multi-user.target\n' \"$SVC_PORT\" \"$SVC_UPSTREAM_URL\" \"$SVC_MODEL\" \"$SVC_CTX\" \"$SVC_UPSTREAM_KEY\" \"$DIAG_FLAGS\" > /etc/systemd/system/bridge-gpu.service
  systemctl daemon-reload
  systemctl restart bridge-gpu
"
sleep 3
STATE=$(systemctl is-active bridge-gpu 2>/dev/null || echo "desconhecido")
HEALTH=$(curl -s --max-time 3 http://127.0.0.1:${SVC_PORT}/api/status 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('model','?') + ' bypass=' + str(d.get('passthrough_responses','?')))" 2>/dev/null || echo "sem resposta")
if [ "$STATE" = "active" ]; then
  zenity --info --text="Bridge GPU ativa [:${SVC_PORT} -> ${SVC_UPSTREAM_URL}]\n$HEALTH" 2>/dev/null
  exit 0
else
  zenity --error --text="Bridge GPU: $STATE\n$HEALTH" 2>/dev/null
  exit 1
fi
