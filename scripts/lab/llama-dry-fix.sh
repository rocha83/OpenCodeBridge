#!/bin/bash
# Desliga o sampler DRY do llama (causa medida da mutacao de nomes/paths).
#
# Evidencia (scripts/war/param_matrix.py, 2026-10-24):
#   DRY 0.8 -> 0/64 tool calls com path exato  (.png -> .ng/.PNG,
#              placa-001.png -> placa-placa-001.txt, TESTE-APAGAR-123 -> -124/APAGRAR)
#   DRY 0.0 -> 100% exato nas mesmas variantes (rp 1.0 e 1.1, tails/anti-repeat on/off)
# DRY penaliza sequencias repetidas: justamente o identificador copiado do
# prompt. repeat-penalty 1.0 x 1.1 nao mudou nada no teste.
#
# Uso:
#   scripts/lab/llama-dry-fix.sh --dry 0            # aplica (dry-multiplier 0.0)
#   scripts/lab/llama-dry-fix.sh --dry 0 --no-restart # so aplica, sem restart
#   scripts/lab/llama-dry-fix.sh --restore          # volta o backup
#   scripts/lab/llama-dry-fix.sh --dry 0 --no-restart
set -euo pipefail
UNITS="llama-gpu.service llama-gpu2.service llama-cpu-forced.service"
DIR="/etc/systemd/system"
# Os start scripts regeram a unit a cada execucao; sem patch neles o DRY 0.8
# volta no proximo provisionamento (foi o que aconteceu depois do fix de 24/10).
SCRIPTS="/usr/local/bin/start-llama-gpu.sh /usr/local/bin/start-llama-gpu2.sh /usr/local/bin/start-llama-cpu-forced.sh"
STAMP="$(date +%Y%m%d-%H%M%S)"
DRY="0.0"
RESTORE=0
NO_RESTART=0
while [ $# -gt 0 ]; do
  case "$1" in
    --dry) DRY="$2"; shift 2 ;;
    --restore) RESTORE=1; shift ;;
    --no-restart) NO_RESTART=1; shift ;;
    *) echo "arg desconhecido: $1"; exit 2 ;;
  esac
done

changed=0
for u in $UNITS; do
  f="$DIR/$u"
  [ -f "$f" ] || { echo "SKIP $u (nao existe)"; continue; }
  if [ "$RESTORE" = "1" ]; then
    b=$(ls -1t "$f".bak-* 2>/dev/null | head -1 || true)
    [ -n "$b" ] || { echo "SKIP $u (sem backup)"; continue; }
    cp -a "$b" "$f"; echo "restaurado $u <- $b"; changed=1; continue
  fi
  before=$(grep -o -- '--dry-multiplier [^ ]*' "$f" || true)
  if [ -z "$before" ]; then
    echo "SKIP $u (sem --dry-multiplier)"; continue
  fi
  cp -a "$f" "$f.bak-$STAMP"
  sed -i -E "s/--dry-multiplier [^ ]+/--dry-multiplier $DRY/" "$f"
  echo "$u: $before -> --dry-multiplier $DRY"
  changed=1
done

for f in $SCRIPTS; do
  [ -f "$f" ] || { echo "SKIP $f (nao existe)"; continue; }
  if [ "$RESTORE" = "1" ]; then
    b=$(ls -1t "$f".bak-* 2>/dev/null | head -1 || true)
    [ -n "$b" ] || { echo "SKIP $f (sem backup)"; continue; }
    cp -a "$b" "$f"; echo "restaurado $f <- $b"; changed=1; continue
  fi
  before=$(grep -o -- '--dry-multiplier [^ ]*' "$f" | head -1 || true)
  [ -n "$before" ] || { echo "SKIP $f (sem --dry-multiplier)"; continue; }
  cp -a "$f" "$f.bak-$STAMP"
  sed -i -E "s/--dry-multiplier [^ ]+/--dry-multiplier $DRY/g" "$f"
  echo "$f: $before -> --dry-multiplier $DRY"
  changed=1
done

[ "$changed" = "1" ] || { echo "nada mudou"; exit 0; }
systemctl daemon-reload
echo "daemon-reload ok"

if [ "$NO_RESTART" = "1" ]; then
  echo "(sem restart: --no-restart)"
  exit 0
fi

for u in $UNITS; do
  systemctl is-enabled "$u" >/dev/null 2>&1 || continue
  systemctl is-active "$u" >/dev/null 2>&1 || continue
  systemctl restart "$u"
  echo "restart $u ok"
done

# Verificacao: o servidor precisa voltar a atender e a flag tem de estar no ps.
sleep 2
for u in $UNITS; do
  systemctl is-active "$u" >/dev/null 2>&1 || continue
  port=$(systemctl show -p ExecStart --value "$u" | grep -o -- '--port [0-9]*' | awk '{print $2}')
  [ -n "$port" ] || continue
  code=$(curl -s -o /dev/null -m 5 -w '%{http_code}' "http://127.0.0.1:$port/health" || true)
  dry=$(pgrep -af "port $port" | grep -o -- '--dry-multiplier [^ ]*' | head -1)
  echo "  $u :$port health=$code $dry"
done
