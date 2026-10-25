#!/bin/bash
# ============================================================================
# Bridges de BANCADA (descartaveis): um unico dono, uma unica tabela de flags.
#
#   bench.sh start       # sobe 4144 (Q4 main+tails), 4146 (Q4 harness), 4147 (Q3)
#   bench.sh stop        # derruba as tres
#   bench.sh restart     # stop + start (recusa se um runner estiver ativo)
#   bench.sh status      # pid + /api/status resumido de cada porta
#
# Regras (para nao se auto-atropelar):
#   - binario SEMPRE o build Release do repo persistente, cwd = bin/Release/net9.0
#   - modo declarado (--mode bridge), nao um amontoado de flags de harness
#   - PID/log por porta em /tmp/opencode/bench/
#   - restart bloqueado enquanto o lock de runner (war.lock) estiver tomado
# ============================================================================
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="${REPO:-$(cd "$HERE/../.." && pwd)}"
DLLDIR="$ROOT/Rochas.OpenCodeBridge/bin/Release/net9.0"
DLL="$DLLDIR/Rochas.OpenCodeBridge.dll"
RUNDIR="${BENCH_RUN_DIR:-/tmp/opencode/bench}"
LOCK="${WAR_LOCK:-/tmp/opencode/war.lock}"
KEY="${LEVEL_API_KEY:-guest@123}"
PORTS="4144 4146 4147"
mkdir -p "$RUNDIR"

flags_for() { # $1 = porta -> flags da bridge daquela porta
  case "$1" in
    4144) echo "--upstream http://127.0.0.1:4100 --model Gemma4-E4B-Q4 --thinking events --context-tokens 131072 --log /tmp/bridge-q4-level2.log" ;;
    4146) echo "--upstream http://127.0.0.1:4100 --model Gemma4-E4B-Q4 --thinking events --context-tokens 131072 --log /tmp/bridge-q4-harness.log" ;;
    4147) echo "--upstream http://127.0.0.1:4110 --model Gemma4-E4B-UD-Q3 --thinking events --context-tokens 65536 --log /tmp/bridge-q3-level1.log" ;;
    *) return 1 ;;
  esac
}

pid_of() { # $1 = porta
  local f="$RUNDIR/$1.pid"
  if [ -f "$f" ] && kill -0 "$(cat "$f")" 2>/dev/null; then cat "$f"; return 0; fi
  local p
  p=$(ss -ltnpH "sport = :$1" 2>/dev/null | grep -oP 'pid=\K[0-9]+' | head -1)
  [ -n "$p" ] && { echo "$p"; return 0; }
  return 1
}

running_runner() { # sonda o lock sem bloquear
  exec 200>"$LOCK"
  if flock -n 200; then flock -u 200; exec 200>&-; return 1; fi
  exec 200>&-; return 0
}

start_one() {
  local port="$1" pid
  if pid=$(pid_of "$port"); then echo "  :$port ja no ar (pid $pid)"; return 0; fi
  [ -f "$DLL" ] || { echo "  :$port SEM binario ($DLL) — rode dotnet build -c Release"; return 1; }
  ( cd "$DLLDIR" && nohup /usr/share/dotnet/dotnet "$DLL" \
      --listen 0.0.0.0 --port "$port" $(flags_for "$port") \
      --temperature 0.3 --max-tokens 16384 --context-guard 1 --api-key "$KEY" \
      --passthrough-responses true --mode bridge \
      --anti-repeat 1 --anti-repeat-retry 1 --anti-repeat-window 512 \
      --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 \
      > "$RUNDIR/$port.out" 2>&1 & echo $! > "$RUNDIR/$port.pid" )
  for _ in $(seq 1 30); do
    if curl -s -m 1 -H "Authorization: Bearer $KEY" "http://127.0.0.1:$port/api/status" >/dev/null 2>&1; then
      echo "  :$port OK (pid $(cat "$RUNDIR/$port.pid"))"; return 0
    fi
    sleep 0.5
  done
  echo "  :$port NAO subiu (ver $RUNDIR/$port.out)"; return 1
}

stop_one() {
  local port="$1" pid
  if ! pid=$(pid_of "$port"); then rm -f "$RUNDIR/$port.pid"; echo "  :$port ja parada"; return 0; fi
  kill -TERM "$pid" 2>/dev/null
  for _ in $(seq 1 20); do kill -0 "$pid" 2>/dev/null || break; sleep 0.25; done
  kill -0 "$pid" 2>/dev/null && kill -KILL "$pid" 2>/dev/null
  rm -f "$RUNDIR/$port.pid"
  echo "  :$port parada (pid $pid)"
}

status_one() {
  local port="$1" pid body
  if pid=$(pid_of "$port"); then echo -n "  :$port pid=$pid "; else echo "  :$port PARADA"; return 0; fi
  body=$(curl -s -m 3 -H "Authorization: Bearer $KEY" "http://127.0.0.1:$port/api/status" || true)
  if [ -z "$body" ]; then echo "(sem /api/status)"; return 0; fi
  printf '%s' "$body" | python3 -c '
import json, sys
d = json.load(sys.stdin)
print("mode={mode} model={model} temp={temperature} harness={main_harness} tails={harness_tails} ctx={ctx}".format(
    mode=d.get("mode"), model=d.get("model"), temperature=d.get("temperature"),
    main_harness=d.get("main_harness"), harness_tails=d.get("harness_tails"), ctx=d.get("context_tokens")))' 2>/dev/null \
    || echo "$body" | head -c 160
}

cmd="${1:-status}"
case "$cmd" in
  start)
    echo "bench: start"; rc=0
    for p in $PORTS; do start_one "$p" || rc=1; done; exit $rc ;;
  stop)
    echo "bench: stop"; for p in $PORTS; do stop_one "$p"; done ;;
  restart)
    if running_runner; then echo "bench: runner ativo ($LOCK) — restart recusado (use stop-all.sh primeiro)"; exit 4; fi
    echo "bench: restart"; for p in $PORTS; do stop_one "$p"; done
    rc=0; for p in $PORTS; do start_one "$p" || rc=1; done; exit $rc ;;
  status)
    echo "bench: status"; for p in $PORTS; do status_one "$p"; done
    running_runner && echo "  runner: ATIVO ($LOCK)" || echo "  runner: livre" ;;
  *) echo "uso: bench.sh start|stop|restart|status"; exit 2 ;;
esac
