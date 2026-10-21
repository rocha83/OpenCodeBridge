#!/usr/bin/env bash
# Sobe a mini bridge (pattern-strip) e confere os 4 modelos/agentes ALT.
# Uso: ./run-alt.sh [start|stop|status|models]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
KEY='guest@123'
PIDS="$ROOT/bridge.pids"

start_bridge() { # port upstream log
  python3 "$ROOT/pattern_bridge.py" --port "$1" --upstream "$2" \
    --api-key "$KEY" --log "$ROOT/bridge-$1.jsonl" --verbose \
    >>"$ROOT/bridge-$1.out" 2>&1 &
  echo $!
}

cmd="${1:-start}"
case "$cmd" in
  start)
    : >"$PIDS"
    start_bridge 4300 http://127.0.0.1:4100 >>"$PIDS"
    start_bridge 4301 http://127.0.0.1:4110 >>"$PIDS"
    sleep 1
    for p in 4300 4301; do
      printf '%s -> ' "$p"
      curl -s -m 5 "http://127.0.0.1:$p/health" || { echo "FALHOU"; exit 1; }
      echo
    done
    echo "pids: $(tr '\n' ' ' <"$PIDS")"
    ;;
  stop)
    for p in 4300 4301; do
      pid=$(ss -ltnp 2>/dev/null | grep ":$p " | grep -o 'pid=[0-9]*' | cut -d= -f2 | head -1)
      [ -n "${pid:-}" ] && kill "$pid" && echo "parado :$p (pid $pid)"
    done
    rm -f "$PIDS"
    ;;
  status)
    for p in 4300 4301; do
      printf '%s: ' "$p"
      curl -s -m 5 "http://127.0.0.1:$p/health" || echo "fora"
      echo
    done
    ;;
  models)
    cd "$ROOT" && opencode models | grep -E '^alt(12b|-e2b)/' | sort
    ;;
  role)
    # ./run-alt.sh role plan-12b|build-12b|plan-e2b|build-e2b "<mensagem>"
    role="${2:?informe plan-12b|build-12b|plan-e2b|build-e2b}"
    shift 2
    case "$role" in
      plan-12b)  agent=plan;   model=alt12b/gemma-4-12b ;;
      build-12b) agent=build;  model=alt12b/gemma-4-12b ;;
      plan-e2b)  agent=plan;   model=alt-e2b/gemma-4-e2b ;;
      build-e2b) agent=build;  model=alt-e2b/gemma-4-e2b ;;
      *) echo "role invalido: $role" >&2; exit 2 ;;
    esac
    cd "$ROOT" && exec opencode run --agent "$agent" --model "$model" "$@"
    ;;
  *)
    echo "uso: $0 [start|stop|status|models]" >&2
    exit 2
    ;;
esac
