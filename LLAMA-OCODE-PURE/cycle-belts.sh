#!/usr/bin/env bash
# cycle-belts.sh — ciclo de belts com N tentativas por faixa e 2 slots em paralelo.
#
# Fila: azul -> verde -> roxa -> marrom -> preta (black belt).
# Cada faixa so SAI do giro depois de CONSEC (15) PASS limpos consecutivos (sem direcionamento).
# e a proxima da fila entra na raia liberada. Esgotou as tentativas = sai como FAIL-MAX.
# Teto de tempo por passo e do battery-war.sh (secs/secs2); aqui so se controla a repeticao.
#
# Uso:  ./cycle-belts.sh            # 15 tentativas por faixa, 2 raias
#       CONSEC=15 MAX_TOTAL=60 ./cycle-belts.sh
#       WAR_RESET_OFF=1 ./cycle-belts.sh   # nao zera contadores do coletor na primeira rodada
#
# Saida: $OUT/<stamp>/<faixa>-a<N>.log e $OUT/<stamp>/summary.tsv
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
CONSEC="${CONSEC:-15}"         # PASS limpos consecutivos exigidos para a faixa sair do giro
MAX_TOTAL="${MAX_TOTAL:-60}"   # trava de seguranca: tentativas totais por faixa
RAILS="${RAILS:-2}"
OUT="${OUT:-/tmp/opencode/llama-pure/cycle}"
STAMP="$(date +%Y%m%d-%H%M)"
RUN_DIR="$OUT/$STAMP"
BELTS=(azul verde roxa marrom preta)
mkdir -p "$RUN_DIR"
QUEUE="$RUN_DIR/queue"
LOCK="$RUN_DIR/queue.lock"
printf '%s\n' "${BELTS[@]}" > "$QUEUE"
: > "$RUN_DIR/summary.tsv"
RESET_MARK="$RUN_DIR/.reset-done"

log() { printf '[cycle %s] %s\n' "$(date +%H:%M:%S)" "$*"; }

# pega a proxima faixa da fila (com trava), imprime vazio quando acabou
next_belt() {
  local b="" tries=0
  while [ "$tries" -lt 50 ]; do
    if mkdir "$LOCK" 2>/dev/null; then
      b="$(head -1 "$QUEUE" 2>/dev/null)"
      if [ -n "$b" ]; then sed -i '1d' "$QUEUE"; fi
      rmdir "$LOCK"
      printf '%s' "$b"
      return 0
    fi
    tries=$((tries+1)); sleep 0.2
  done
  printf ''
}

run_belt() {
  local belt="$1" n logf rc consec=0 tent=0
  while [ "$consec" -lt "$CONSEC" ] && [ "$tent" -lt "$MAX_TOTAL" ]; do
    tent=$((tent+1)); n=$tent
    logf="$RUN_DIR/$belt-a$n.log"
    # reset dos contadores do coletor apenas na primeira rodada do ciclo
    if [ -e "$RESET_MARK" ] || [ "${WAR_RESET_OFF:-0}" = 1 ]; then export WAR_RESET=0; else
      export WAR_RESET=1; : > "$RESET_MARK"; fi
    log "inicio $belt tentativa $n (PASS limpos ${consec}/${CONSEC})"
    ( cd "$HERE" && ./battery-war.sh chat tools "belts-build-$belt" ) > "$logf" 2>&1
    rc=$?
    if grep -q "RESULTADO \[belts-build-$belt\]: PASS" "$logf" \
       && ! grep -q "direcionamento build:" "$logf"; then
      consec=$((consec+1))
      printf '%s\t%d\tPASS-LIMPO\t%d\n' "$belt" "$n" "$consec" >> "$RUN_DIR/summary.tsv"
      log "$belt PASS limpo $consec/$CONSEC"
    else
      if grep -q "RESULTADO \[belts-build-$belt\]: PASS" "$logf"; then
        printf '%s\t%d\tPASS-COM-AJUSTE\t0\n' "$belt" "$n" >> "$RUN_DIR/summary.tsv"
        log "$belt PASSou mas precisou de ajuste -> zera contagem"
      else
        printf '%s\t%d\tFAIL\t0\n' "$belt" "$n" >> "$RUN_DIR/summary.tsv"
        log "$belt FAIL (rc=$rc) -> zera contagem"
      fi
      consec=0
    fi
  done
  if [ "$consec" -ge "$CONSEC" ]; then
    log "$belt CUMPRIU $CONSEC PASS limpos consecutivos (sai do giro)"
    return 0
  fi
  log "$belt NAO cumpriu $CONSEC PASS limpos em $MAX_TOTAL tentativas (sai como INCOMPLETO)"
  return 1
}

rail() {
  local id="$1" belt
  while :; do
    belt="$(next_belt)"
    [ -z "$belt" ] && break
    log "raia $id pega $belt"
    run_belt "$belt"
  done
  log "raia $id sem faixas na fila (encerrando)"
}

trap 'log "sinal recebido: encerrando ciclo"; exit 130' INT TERM
log "ciclo iniciado: CONSEC=$CONSEC MAX_TOTAL=$MAX_TOTAL raias=$RAILS dir=$RUN_DIR"
for i in $(seq 1 "$RAILS"); do rail "$i" & done
wait
log "ciclo encerrado"
echo "== summary =="
cat "$RUN_DIR/summary.tsv" 2>/dev/null
