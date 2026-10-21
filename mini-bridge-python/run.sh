#!/usr/bin/env bash
# Sobe/derruba a mini bridge python (pattern-strip + dump de stream).
# Uso:
#   ./run.sh start [porta] [upstream]   # default 4300 http://127.0.0.1:4100
#   ./run.sh stop
#   ./run.sh status
#   ./run.sh dump [dir]                 # roda o analisador nos dumps
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
PORT="${2:-${PORT:-4300}}"
UPSTREAM="${3:-${UPSTREAM:-http://127.0.0.1:4100}}"
KEY="${API_KEY:-guest@123}"
LOG="$ROOT/logs/bridge-$PORT.jsonl"
DUMPS="$ROOT/logs/dumps-$PORT"
PIDFILE="$ROOT/logs/bridge-$PORT.pid"

cmd="${1:-status}"
case "$cmd" in
  start)
    mkdir -p "$ROOT/logs" "$DUMPS"
    setsid nohup python3 "$ROOT/pattern_bridge.py" \
      --port "$PORT" --upstream "$UPSTREAM" --api-key "$KEY" \
      --log "$LOG" --dump-stream "$DUMPS" --verbose \
      >"$ROOT/logs/bridge-$PORT.out" 2>&1 < /dev/null &
    echo $! >"$PIDFILE"
    sleep 1
    curl -s -m 5 "http://127.0.0.1:$PORT/health" || { echo "FALHOU"; exit 1; }
    echo " log=$LOG dumps=$DUMPS"
    ;;
  stop)
    if [ -f "$PIDFILE" ]; then
      kill "$(cat "$PIDFILE")" 2>/dev/null || true
      rm -f "$PIDFILE"
      echo "parado :$PORT"
    else
      pid=$(ss -ltnp 2>/dev/null | grep ":$PORT " | grep -o 'pid=[0-9]*' | cut -d= -f2 | head -1)
      [ -n "${pid:-}" ] && kill "$pid" && echo "parado :$PORT (pid $pid)" || echo "fora do ar"
    fi
    ;;
  status)
    for p in "${PORT}"; do
      printf ':%s ' "$p"
      curl -s -m 5 "http://127.0.0.1:$p/health" || echo "fora"
      echo
    done
    ;;
  dump)
    python3 "$ROOT/analyze_dumps.py" --dir "${2:-$DUMPS}"
    ;;
  *)
    echo "uso: $0 [start|stop|status|dump] [porta] [upstream]" >&2
    exit 2
    ;;
esac
