#!/bin/bash
# ============================================================================
# Fecha TUDO que e de teste, na ordem certa, sem tocar em producao.
#
#   stop-all.sh                # runners + bridges de bancada + processos de teste
#   stop-all.sh --keep-bridges # so runners e processos de teste
#
# NAO toca: llama :4100/:4110, bridges de producao :4124/:4125 (systemd),
# Web :4130. Aqueles se reiniciam por systemctl (deploy), nunca por aqui.
# ============================================================================
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
KEEP_BRIDGES=0
[ "${1:-}" = "--keep-bridges" ] && KEEP_BRIDGES=1

kill_pat() { # $1 = trecho da linha de comando
  local pids
  pids=$(ps -eo pid,args | awk -v p="$1" 'index($0,p) && !/awk/ && !/stop-all.sh/ {print $1}')
  [ -z "$pids" ] && return 0
  echo "  $1 -> $pids"
  for pid in $pids; do
    pkill -TERM -P "$pid" 2>/dev/null
    kill -TERM "$pid" 2>/dev/null
  done
}

echo "stop-all: runners"
kill_pat "scripts/war/run-levels.sh"
kill_pat "war-sequence"
kill_pat "scripts/war/selftest-runner.sh"
sleep 1

echo "stop-all: processos de teste"
kill_pat "Rochas.OpenCodeBridge.Test.dll"
kill_pat "opencode run --standalone"
kill_pat "opencode run --auto"

if [ "$KEEP_BRIDGES" = "1" ]; then
  echo "stop-all: bridges de bancada mantidas (--keep-bridges)"
else
  echo "stop-all: bridges de bancada"
  bash "$HERE/bench.sh" stop
fi

echo "stop-all: restante"
ps -eo pid,args | grep -E "run-levels|war-sequence|selftest-runner|OpenCodeBridge.Test.dll|opencode run" | grep -v grep || echo "  nada"
echo "stop-all: producao intacta"
for p in 4124 4125; do
  curl -s -m 3 -H "Authorization: Bearer ${LEVEL_API_KEY:-guest@123}" "http://127.0.0.1:$p/api/status" \
    | python3 -c "import json,sys;d=json.load(sys.stdin);print(f'  :{d.get(\"port\")} {d.get(\"status\")} model={d.get(\"model\")} temp={d.get(\"temperature\")} mode={d.get(\"mode\")}')" 2>/dev/null \
    || echo "  :$p sem resposta"
done
