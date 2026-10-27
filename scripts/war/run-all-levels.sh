#!/bin/bash
# ============================================================================
# Trilha unica de levels: roda Level 1 -> 2 -> 3 -> 4 em sequencia e PARA no
# primeiro gate vermelho (FAIL/BLOCKED/STALE). Cada nivel grava seus artefatos
# em logs/levels/L<n>-<stamp>/ e o console consolidado em
# logs/levels/all-levels-<stamp>.console.log.
#
# Uso:
#   scripts/war/run-all-levels.sh                 # 1 -> 2 -> 3 -> 4
#   scripts/war/run-all-levels.sh --from 2        # 2 -> 3 -> 4
#   scripts/war/run-all-levels.sh --levels 1,2    # so 1 e 2
#   scripts/war/run-all-levels.sh --gate-only     # recalcula gate dos runs
#
# Variaveis de ambiente repassadas ao run-levels.sh:
#   LEVEL_BRIDGE, LEVEL_UPSTREAM, LEVEL_BRIDGE_Q3, LEVEL_API_KEY,
#   LEVEL_STEP_TIMEOUT, LEVEL_REQUIRE_GREEN.
# ============================================================================
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="${LEVEL_ROOT:-${REPO:-$(cd "$HERE/../.." && pwd)}}"
OUTBASE="${LEVEL_OUT:-$ROOT/logs/levels}"
FROM=1
TO=4
GATE_ONLY=0
LEVELS=""
RESUME_STAMP=""
RESUME_LEVEL=1

while [ $# -gt 0 ]; do
  case "$1" in
    --from) FROM="${2:-1}"; shift 2 ;;
    --from=*) FROM="${1#*=}"; shift ;;
    --levels) LEVELS="${2:-}"; shift 2 ;;
    --levels=*) LEVELS="${1#*=}"; shift ;;
    --resume-stamp) RESUME_STAMP="${2:-}"; shift 2 ;;
    --resume-stamp=*) RESUME_STAMP="${1#*=}"; shift ;;
    --resume-level) RESUME_LEVEL="${2:-1}"; shift 2 ;;
    --gate-only) GATE_ONLY=1; shift ;;
    -h|--help) sed -n '2,20p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "arg desconhecido: $1" >&2; exit 2 ;;
  esac
done

mkdir -p "$OUTBASE"
STAMP="$(date +%Y%m%d-%H%M%S)"
GLOBAL_LOG="$OUTBASE/all-levels-$STAMP.console.log"
: > "$GLOBAL_LOG"

if [ "$GATE_ONLY" = "1" ]; then
  # Recalcula o gate de cada run existente (sem executar steps).
  shopt -s nullglob
  for d in "$OUTBASE"/L*-*; do
    [ -f "$d/summary.tsv" ] || continue
    base="${d##*/}"
    lvl="${base:1:1}"
    stamp="${base:3}"
    echo "--- gate $d"
    LEVEL_OUT="$OUTBASE" LEVEL_STAMP="$stamp" \
      "$ROOT/scripts/war/run-levels.sh" --level "$lvl" --gate-only
  done
  exit 0
fi

if [ -n "$LEVELS" ]; then
  LIST="$(printf '%s' "$LEVELS" | tr ',' ' ')"
else
  LIST="$(seq "$FROM" "$TO")"
fi

log() { echo "$*" | tee -a "$GLOBAL_LOG"; }

log "=== run-all-levels $STAMP (levels: $LIST) ==="
log "repo: $ROOT"
log "artefatos: $OUTBASE"
DRIVER_SUMMARY="$OUTBASE/driver-summary-$STAMP.tsv"
printf 'level\trc\tstamp\tout\n' > "$DRIVER_SUMMARY"
failed=0

for lvl in $LIST; do
  if [ -n "$RESUME_STAMP" ] && [ "$lvl" -eq "$RESUME_LEVEL" ]; then
    stamp="$RESUME_STAMP"
  else
    stamp="$STAMP-l$lvl"
  fi
  out="$OUTBASE/L$lvl-$stamp"
  log ""
  log "############ LEVEL $lvl ($(date '+%F %T')) ############"
  log "OUT: $out"

  # Console do nivel vai para OUT/console.out (nao volatil) e para o log global.
  set -o pipefail
  LEVEL_OUT="$OUTBASE" LEVEL_STAMP="$stamp" \
    "$ROOT/scripts/war/run-levels.sh" --level "$lvl" 2>&1 | tee -a "$GLOBAL_LOG"
  rc=$?
  set +o pipefail

  printf '%s\t%s\t%s\t%s\n' "$lvl" "$rc" "$stamp" "$out" >> "$DRIVER_SUMMARY"
  log "############ LEVEL $lvl -> rc=$rc ($(date '+%F %T')) ############"
  if [ "$rc" -ne 0 ]; then
    failed=$((failed + 1))
    log "Level $lvl VERMELHO (rc=$rc) — registrado; SEGUINDO para o proximo nivel."
    log "  retomar: LEVEL_STAMP=$stamp scripts/war/run-levels.sh --level $lvl"
  else
    log "Level $lvl VERDE — seguindo."
  fi
done

log ""
if [ "$failed" -gt 0 ]; then
  log "=== TRILHA CONCLUIDA COM $failed NIVEL(IS) VERMELHO(S) ==="
  log "resumo: $DRIVER_SUMMARY"
  exit 1
fi
log "=== TODOS OS LEVELS DA TRILHA VERDES ($(date '+%F %T')) ==="
