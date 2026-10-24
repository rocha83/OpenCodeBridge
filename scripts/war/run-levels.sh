#!/bin/bash
# ============================================================================
# Arena de testes por LEVELS (antes da subida para producao).
# Level 1: units (U-*), devpipe (E-devpipe-*), belts azul+verde e a suite web
# (integracao + topicos no Web.Test). Cada bloco da bridge roda em DOIS passes:
# --mode pure e --mode opencode. Por step: log, delta do log da bridge, tempo,
# PASS/FAIL e checkpoint em state/ (retomada por LEVEL_STAMP).
#
# Uso:
#   run-levels.sh --level 1                 # executa (retoma pelo stamp atual)
#   LEVEL_STAMP=fixed run-levels.sh --level 1
#   run-levels.sh --level 1 --list
# Levels futuros entram no case abaixo (2 = belts maiores + frontpipe, etc.).
# ============================================================================
set -u
# Raiz do repo: derivada da localizacao do script (scripts/war/ -> raiz) ou LEVEL_ROOT.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="${LEVEL_ROOT:-${REPO:-$(cd "$HERE/../.." && pwd)}}"
LEVEL=1
LIST=0
while [ $# -gt 0 ]; do
  case "$1" in
    --level) LEVEL="${2:-1}"; shift 2 ;;
    --level=*) LEVEL="${1#*=}"; shift ;;
    --list) LIST=1; shift ;;
    *) echo "arg desconhecido: $1"; exit 2 ;;
  esac
done

STAMP="${LEVEL_STAMP:-$(date +%Y%m%d-%H%M%S)}"
OUT="/tmp/opencode/levels/L$LEVEL-$STAMP"
STEPS="$OUT/steps"; STATE="$OUT/state"
mkdir -p "$STEPS" "$STATE"
API="${LEVEL_API_KEY:-guest@123}"; BRIDGE="${LEVEL_BRIDGE:-http://127.0.0.1:4124}"
BLOG="${LEVEL_BLOG:-/tmp/bridge-gpu.log}"
TESTPROJ="$ROOT/Rochas.OpenCodeBridge.Test"
WEBTEST="$ROOT/Rochas.OpenCodeBridge.Web.Test/bin/Release/net9.0/Rochas.OpenCodeBridge.Web.Test.dll"
SUMMARY="$OUT/summary.tsv"
[ -f "$SUMMARY" ] || printf 'step\tverdict\tsecs\trc\n' > "$SUMMARY"

log() { echo "$*" | tee -a "$OUT/summary.txt"; }

step_bridge() { # nome + args do runner
  local name="$1"; shift
  [ -f "$STATE/$name.done" ] && { log "SKIP $name (done)"; return 0; }
  local b0=0 a0=0
  [ -f "$BLOG" ] && b0=$(wc -l < "$BLOG")
  local t0; t0=$(date +%s)
  log "=== $name :: $*"
  timeout "${LEVEL_STEP_TIMEOUT:-7200}" dotnet run --project "$TESTPROJ" -c Release --no-build -- \
      --api-key "$API" --bridge "$BRIDGE" "$@" > "$STEPS/$name.test.log" 2>&1
  local rc=$?
  local secs=$(( $(date +%s) - t0 ))
  [ -f "$BLOG" ] && a0=$(wc -l < "$BLOG")
  tail -n +"$((b0 + 1))" "$BLOG" 2>/dev/null | head -n "$((a0 - b0))" > "$STEPS/$name.bridge.log"
  local verdict=SKIP
  grep -q '^FAIL ' "$STEPS/$name.test.log" && verdict=FAIL
  grep -q '^PASS ' "$STEPS/$name.test.log" && [ "$verdict" = SKIP ] && verdict=PASS
  [ "$rc" -ne 0 ] && [ "$verdict" = PASS ] && verdict=FAIL
  grep -E '^PASS |^FAIL ' "$STEPS/$name.test.log" | tail -8 | sed 's/^/    /' | tee -a "$OUT/summary.txt"
  printf '%s\t%s\t%s\t%s\n' "$name" "$verdict" "$secs" "$rc" >> "$SUMMARY"
  echo "$rc $verdict $secs" > "$STATE/$name.done"
  log "    -> $verdict (${secs}s, rc=$rc)"
}

step_webtest() { # integracao + topicos (suite web; TopicsSweep roda no bloco unit dela)
  local name="webui-integracao-topicos"
  [ -f "$STATE/$name.done" ] && { log "SKIP $name (done)"; return 0; }
  [ -f "$WEBTEST" ] || { log "ERRO: Web.Test nao buildado"; return 1; }
  local t0; t0=$(date +%s)
  log "=== $name :: Web.Test (integracao + topicos)"
  timeout "${LEVEL_STEP_TIMEOUT:-3600}" dotnet exec "$WEBTEST" > "$STEPS/$name.test.log" 2>&1
  local rc=$?
  local secs=$(( $(date +%s) - t0 ))
  local verdict=SKIP
  grep -q '^FAIL ' "$STEPS/$name.test.log" && verdict=FAIL
  grep -q '^PASS ' "$STEPS/$name.test.log" && [ "$verdict" = SKIP ] && verdict=PASS
  [ "$rc" -ne 0 ] && [ "$verdict" = PASS ] && verdict=FAIL
  grep -E '^PASS |^FAIL ' "$STEPS/$name.test.log" | tail -8 | sed 's/^/    /' | tee -a "$OUT/summary.txt"
  printf '%s\t%s\t%s\t%s\n' "$name" "$verdict" "$secs" "$rc" >> "$SUMMARY"
  echo "$rc $verdict $secs" > "$STATE/$name.done"
  log "    -> $verdict (${secs}s, rc=$rc)"
}

case "$LEVEL" in
  1)
    STEPS_DESC="units pure/opencode; devpipe pure/opencode; belts build (azul,verde) pure/opencode; belts orch (azul,verde) pure/opencode; webui integracao+topicos"
    if [ "$LIST" = "1" ]; then
      echo "Level 1 -> $STEPS_DESC"; exit 0
    fi
    log "=== LEVEL 1 ($OUT) ==="
    for mode in pure opencode; do
      step_bridge "units-$mode" --only U- --mode "$mode"
      step_bridge "devpipe-$mode" --only E-devpipe --mode "$mode"
      # Ordem: trilha Build primeiro, trilha Orch depois (duas trilhas da suite).
      step_bridge "belts-build-$mode" --only E-belts-build-direct --belts azul,verde --mode "$mode"
      step_bridge "belts-orch-$mode" --only E-belts-direct --belts azul,verde --mode "$mode"
    done
    step_webtest
    ;;
  *)
    echo "Level $LEVEL ainda nao definido (implementado: 1)"; exit 2 ;;
esac

log ""
log "=== resumo LEVEL $LEVEL ==="
awk -F'\t' 'NR>1{n++; p+=($2=="PASS"); f+=($2=="FAIL"); s+=($2=="SKIP"); t+=$3}
 END{printf "%d steps: PASS=%d FAIL=%d SKIP=%d (%ds)\n", n, p, f, s, t}' "$SUMMARY" | tee -a "$OUT/summary.txt"
log ""
log "=== falhas ==="
awk -F'\t' 'NR>1 && $2=="FAIL"{print $1" (rc="$4", "$3"s)"}' "$SUMMARY" | tee -a "$OUT/summary.txt"
log "artefatos: $OUT"
