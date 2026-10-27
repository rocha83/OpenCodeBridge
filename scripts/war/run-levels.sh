#!/bin/bash
# ============================================================================
# Arena de testes por LEVELS (antes da subida para producao).
#
# Duas trilhas, sem ambiguidade:
#   --mode bridge   = caminho com bridge harness main/tails (HTTP /v1/responses
#                     + tools por /api/tools/execute; a bridge monta o turno).
#   --mode opencode = caminho livre para o opencode (o cliente traz o master).
#
# Cada run grava FATOS em events.jsonl (uma linha JSON por decisao):
#   run_start, preflight, step_start, step_stale, step_end, gate, run_end.
# summary.tsv / summary.txt sao derivados desses eventos.
#
# Pre-requisitos: o nivel so comeca se o preflight passar; senao o veredito e
# BLOCKED (exit 3) — nao FAIL. Step cujo binario mudou no meio vira STALE.
# Gate final: FAIL/BLOCKED/STALE derrubam o exit code (LEVEL_REQUIRE_GREEN=0
# desliga), para a sequencia nao encadear um nivel vermelho no proximo.
#
# Uso:
#   run-levels.sh --level 1                      # executa (retoma pelo stamp)
#   LEVEL_STAMP=fixed run-levels.sh --level 1
#   run-levels.sh --level 1 --list
#   run-levels.sh --level 2 --gate-only          # so recalcula o gate do run
# ============================================================================
set -u
# Raiz do repo: derivada da localizacao do script (scripts/war/ -> raiz) ou LEVEL_ROOT.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="${LEVEL_ROOT:-${REPO:-$(cd "$HERE/../.." && pwd)}}"
LEVEL=1
LIST=0
GATE_ONLY=0
REQUIRE_GREEN="${LEVEL_REQUIRE_GREEN:-1}"
while [ $# -gt 0 ]; do
  case "$1" in
    --level) LEVEL="${2:-1}"; shift 2 ;;
    --level=*) LEVEL="${1#*=}"; shift ;;
    --list) LIST=1; shift ;;
    --gate-only) GATE_ONLY=1; shift ;;
    --require-green) REQUIRE_GREEN=1; shift ;;
    --no-require-green) REQUIRE_GREEN=0; shift ;;
    *) echo "arg desconhecido: $1"; exit 2 ;;
  esac
done

# Lock unico: dois runners em paralelo se atropelam (porta, GPU, binario). O
# segundo vira BLOCKED (exit 3) em vez de contaminar o resultado do primeiro.
# --gate-only e leitura pura: nao precisa do lock.
WAR_LOCK="${WAR_LOCK:-/tmp/opencode/war.lock}"
mkdir -p "$(dirname "$WAR_LOCK")"
if [ "$GATE_ONLY" != "1" ]; then
  exec 9>"$WAR_LOCK"
  if ! flock -n 9; then
    echo "BLOCKED: outro runner ativo ($WAR_LOCK) — pare com scripts/war/stop-all.sh"
    exit 3
  fi
fi

# Modos da bateria: "bridge opencode" (padrao) | "opencode" | "bridge".
# Medir um caminho por vez evita alternancia: no opencode o harness e do
# CLIENTE (a tail/main harness da bridge nao atuam), no bridge e o nosso.
MODES="${LEVEL_MODES:-bridge opencode}"
# Fail-fast: com LEVEL_STOP_ON_FAIL=1 (padrao) o nivel PARA no primeiro
# FAIL/STALE. Rodar os passos seguintes com o mesmo defeito so torra GPU e
# esconde a causa; para medir o placar completo, LEVEL_STOP_ON_FAIL=0.
STOP_ON_FAIL="${LEVEL_STOP_ON_FAIL:-1}"
case "$MODES" in
  bridge|opencode|"bridge opencode"|"opencode bridge") ;;
  *) echo "LEVEL_MODES invalido: $MODES (use 'bridge', 'opencode' ou 'bridge opencode')"; exit 2 ;;
esac

STAMP="${LEVEL_STAMP:-$(date +%Y%m%d-%H%M%S)}"
# Artefatos em disco NAO volatil (sda3): logs/levels do repo (override por LEVEL_OUT).
OUT="${LEVEL_OUT:-$ROOT/logs/levels}/L$LEVEL-$STAMP"
STEPS="$OUT/steps"; STATE="$OUT/state"
mkdir -p "$STEPS" "$STATE"
API="${LEVEL_API_KEY:-guest@123}"; BRIDGE="${LEVEL_BRIDGE:-http://127.0.0.1:4124}"
export WAR_UPSTREAM="${LEVEL_UPSTREAM:-http://127.0.0.1:4100}"
# Raia sem visao (Q3 :4110 nao carrega mmproj): pula E-devpipe-vision*, salvo override.
NO_VISION="${LEVEL_NO_VISION:-}"
if [ -z "$NO_VISION" ]; then case "${LEVEL_UPSTREAM:-}" in *":4110"*) NO_VISION=1 ;; *) NO_VISION=0 ;; esac; fi
export WAR_NO_VISION="$NO_VISION"
# Linha Q3 dos testes (E-tools-full / E-science-cpu): bench bridge da raia Q3,
# nunca a de producao — a de producao atende sessao real e pode estar sem harness.
if [ -n "${LEVEL_BRIDGE_Q3:-}" ]; then export WAR_CPU_BRIDGE="$LEVEL_BRIDGE_Q3"; fi
BLOG="${LEVEL_BLOG:-/tmp/bridge-gpu.log}"
TESTPROJ="$ROOT/Rochas.OpenCodeBridge.Test"
BRIDGEDLL="$ROOT/Rochas.OpenCodeBridge/bin/Release/net10.0/Rochas.OpenCodeBridge.dll"
TESTDLL="$ROOT/Rochas.OpenCodeBridge.Test/bin/Release/net10.0/Rochas.OpenCodeBridge.Test.dll"
WEBTEST="$ROOT/Rochas.OpenCodeBridge.Web.Test/bin/Release/net10.0/Rochas.OpenCodeBridge.Web.Test.dll"
SUMMARY="$OUT/summary.tsv"
EVENTS="$OUT/events.jsonl"
[ -f "$SUMMARY" ] || printf 'step\tverdict\tsecs\trc\tcause\n' > "$SUMMARY"

# --- fatos em JSONL ---------------------------------------------------------
jesc() { printf '%s' "$1" | tr -d '\000-\037' | sed 's/\\/\\\\/g; s/"/\\"/g'; }
emit() { printf '%s\n' "$1" >> "$EVENTS"; }
h() { sha256sum "$1" 2>/dev/null | cut -c1-16; }

HASH_GIT="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo sem-git)"
HASH_BRIDGE="$(h "$BRIDGEDLL")"
HASH_WEB="$(h "$WEBTEST")"
INIT_HASH_TEST="$(h "$TESTDLL")"
HASH_TEST="$INIT_HASH_TEST"

log() { echo "$*" | tee -a "$OUT/summary.txt"; }

# Causa objetiva da falha, antes de qualquer ajuste de modelo/prompt.
classify() { # logfile rc -> infra|executor|bridge|fixture|timeout|undetermined
  local logf="$1" rc="$2"
  [ "$rc" = "124" ] || [ "$rc" = "137" ] && { echo "timeout"; return; }
  grep -qE "Connection refused|No route to host|Name or service not known|corpo vazio apos retry" "$logf" 2>/dev/null \
    && { echo "infra"; return; }
  # assert com lista de calls vazia: a bridge/v1/responses entregou call e o
  # executor do teste nao rodou -> defeito do executor, nao do modelo.
  grep -q "calls=)" "$logf" 2>/dev/null && { echo "executor"; return; }
  grep -qE "ERRO: .*|ok=false|permission|negad" "$logf" 2>/dev/null && { echo "bridge"; return; }
  grep -q "artefato .*conteudo=''" "$logf" 2>/dev/null && { echo "fixture"; return; }
  echo "undetermined"
}

# Binario trocado no meio do run invalida o veredito (nao e PASS nem FAIL).
stale_check() { # -> ecoa "1" se o binario mudou desde o run_start
  local now; now="$(h "$TESTDLL")"
  [ "$now" != "$INIT_HASH_TEST" ] && { echo 1; return; }
  echo 0
}

# --- preflight --------------------------------------------------------------
PF_FAIL=""
pf() { # check ok evidence-json
  local check="$1" ok="$2" ev="$3"
  emit "{\"t\":\"preflight\",\"check\":\"$check\",\"ok\":$ok,\"evidence\":$ev}"
  [ "$ok" = "true" ] || PF_FAIL="${PF_FAIL}${PF_FAIL:+,}$check"
}
json_get() { grep -o "\"$2\":[^,}]*" <<<"$1" 2>/dev/null | head -1 | sed "s/\"$2\"://; s/\"//g"; }

preflight() {
  log "=== preflight ==="
  # 1) llama do upstream (health + ctx por slot)
  local up_port; up_port=$(sed -E 's#.*:([0-9]+).*#\1#' <<<"$WAR_UPSTREAM")
  local health props nctx slots alias
  health=$(curl -s -m 5 "http://127.0.0.1:$up_port/health" 2>/dev/null)
  props=$(curl -s -m 8 -H "Authorization: Bearer $API" "http://127.0.0.1:$up_port/props" 2>/dev/null)
  nctx=$(grep -o '"n_ctx":[0-9]*' <<<"$props" | head -1 | cut -d: -f2)
  slots=$(grep -o '"total_slots":[0-9]*' <<<"$props" | head -1 | cut -d: -f2)
  alias=$(grep -o '"model_path":"[^"]*"' <<<"$props" | head -1 | sed 's/.*\///; s/"//g')
  if grep -q '"ok"' <<<"$health"; then
    pf "llama_$up_port" true "{\"port\":$up_port,\"n_ctx\":${nctx:-0},\"slots\":${slots:-0},\"model_path\":\"$(jesc "$alias")\"}"
  else
    pf "llama_$up_port" false "{\"port\":$up_port,\"health\":\"$(jesc "$health")\"}"
  fi
  # 2) bridge alvo: params efetivos (o que o teste realmente recebe)
  local st bmodel bup btemp bpw bmain btails bctx
  st=$(curl -s -m 8 -H "Authorization: Bearer $API" "$BRIDGE/api/status" 2>/dev/null)
  bmodel=$(json_get "$st" model); bup=$(json_get "$st" upstream)
  btemp=$(json_get "$st" temperature); bpw=$(json_get "$st" profile_temp_wins)
  bmain=$(json_get "$st" main_harness); btails=$(json_get "$st" harness_tails)
  bctx=$(json_get "$st" context_tokens)
  if [ -n "$bmodel" ]; then
    pf "bridge_status" true "{\"bridge\":\"$(jesc "$BRIDGE")\",\"model\":\"$(jesc "$bmodel")\",\"upstream\":\"$(jesc "$bup")\",\"temperature\":$( [ -n "$btemp" ] && echo "$btemp" || echo 0 ),\"profile_temp_wins\":${bpw:-null},\"main_harness\":${bmain:-null},\"harness_tails\":${btails:-null},\"context_tokens\":${bctx:-0}}"
  else
    pf "bridge_status" false "{\"bridge\":\"$(jesc "$BRIDGE")\"}"
  fi
  # 3) coerencia bridge <-> upstream (modelo e ctx do slot)
  if [ -n "$bup" ] && [ "$bup" != "$WAR_UPSTREAM" ]; then
    pf "bridge_upstream" false "{\"bridge_upstream\":\"$(jesc "$bup")\",\"esperado\":\"$(jesc "$WAR_UPSTREAM")\"}"
  else
    pf "bridge_upstream" true "{\"bridge_upstream\":\"$(jesc "${bup:-$WAR_UPSTREAM}")\"}"
  fi
  if [ -n "$nctx" ] && [ -n "$bctx" ] && [ "$bctx" != "$nctx" ]; then
    pf "bridge_context_tokens" false "{\"context_tokens\":$bctx,\"n_ctx_slot\":$nctx}"
  else
    pf "bridge_context_tokens" true "{\"context_tokens\":${bctx:-0},\"n_ctx_slot\":${nctx:-0}}"
  fi
  # 4) binarios dos testes
  [ -f "$TESTDLL" ] && pf "test_binario" true "{\"sha\":\"$INIT_HASH_TEST\"}" \
    || pf "test_binario" false "{\"erro\":\"Test.dll ausente\"}"
  [ -f "$WEBTEST" ] && pf "webtest_binario" true "{\"sha\":\"$HASH_WEB\"}" \
    || pf "webtest_binario" false "{\"erro\":\"Web.Test.dll ausente\"}"
  # 5) opencode (as trilhas do nivel usam --mode opencode)
  if command -v opencode >/dev/null 2>&1; then
    pf "opencode_cli" true "{\"versao\":\"$(jesc "$(opencode --version 2>/dev/null | head -1)")\"}"
  else
    pf "opencode_cli" false "{\"erro\":\"opencode ausente no PATH\"}"
  fi
  # 6) Web UI (o step webui roda o Web.Test contra esta porta)
  local web_port="${BRIDGE_WEB_PORT:-4130}" wcode
  wcode=$(curl -s -m 5 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$web_port/Account/Login" 2>/dev/null || echo 000)
  if [ "$wcode" = "200" ]; then
    pf "web_ui" true "{\"port\":$web_port,\"login\":200}"
  else
    pf "web_ui" false "{\"port\":$web_port,\"login\":\"$wcode\"}"
  fi
  # 7) infra: run orfao, disco, GPU
  if pgrep -f "Rochas[.]OpenCodeBridge[.]Test[.]dll" >/dev/null 2>&1; then
    pf "sem_run_orfao" false "{\"pids\":\"$(pgrep -f 'Rochas[.]OpenCodeBridge[.]Test[.]dll' | tr '\n' ' ')\"}"
  else
    pf "sem_run_orfao" true "{}"
  fi
  local free_mb
  free_mb=$(df -Pm "$OUT" | awk 'NR==2{print $4}')
  if [ "${free_mb:-0}" -ge 2048 ]; then
    pf "disco" true "{\"livre_mb\":$free_mb}"
  else
    pf "disco" false "{\"livre_mb\":${free_mb:-0}}"
  fi
  local gpu
  gpu=$(nvidia-smi --query-gpu=memory.used,memory.total --format=csv,noheader 2>/dev/null | head -1)
  pf "gpu" true "{\"uso\":\"$(jesc "$gpu")\"}"
  if [ -n "$PF_FAIL" ]; then
    emit "{\"t\":\"preflight_fail\",\"checks\":\"$(jesc "$PF_FAIL")\"}"
    log "preflight FALHOU: $PF_FAIL (veredito BLOCKED)"
    return 1
  fi
  log "preflight ok"
  return 0
}

# --- steps ------------------------------------------------------------------
_step_bridge() { # nome + args do runner
  local name="$1"; shift
  [ -f "$STATE/$name.done" ] && { log "SKIP $name (done)"; return 0; }
  local mode="?" prev=""
  for a in "$@"; do [ "$prev" = "--mode" ] && mode="$a"; prev="$a"; done
  local attempt; attempt=$(( $(cat "$STATE/$name.attempt" 2>/dev/null || echo 0) + 1 )); echo "$attempt" > "$STATE/$name.attempt"
  emit "{\"t\":\"step_start\",\"step\":\"$name\",\"mode\":\"$mode\",\"attempt\":$attempt,\"run\":\"$HASH_GIT\"}"
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
  local verdict=SKIP cause=""
  grep -q '^FAIL ' "$STEPS/$name.test.log" && verdict=FAIL
  grep -q '^PASS ' "$STEPS/$name.test.log" && [ "$verdict" = SKIP ] && verdict=PASS
  [ "$rc" -ne 0 ] && [ "$verdict" = PASS ] && verdict=FAIL
  [ "$verdict" = FAIL ] && cause=$(classify "$STEPS/$name.test.log" "$rc")
  # Binario trocado durante o run: resultado nao vale como veredito.
  if [ "$(stale_check)" = "1" ]; then
    verdict=STALE; cause="binario mudou durante o run"
    emit "{\"t\":\"step_stale\",\"step\":\"$name\",\"test_sha_inicio\":\"$INIT_HASH_TEST\",\"test_sha_agora\":\"$(h "$TESTDLL")\"}"
    rm -f "$STATE/$name.done"
    HASH_TEST="$(h "$TESTDLL")"; INIT_HASH_TEST="$HASH_TEST"
  else
    # So sucesso fica em .done. Passo vermelho grava .failed: retomar com o mesmo
    # stamp deve REEXECUTAR o passo corrigido, nao pular com o veredito velho.
    if [ "$verdict" = "PASS" ] || [ "$verdict" = "SKIP" ]; then
      echo "$rc $verdict $secs" > "$STATE/$name.done"
      rm -f "$STATE/$name.failed"
    else
      echo "$rc $verdict $secs" > "$STATE/$name.failed"
      rm -f "$STATE/$name.done"
    fi
  fi
  grep -E '^PASS |^FAIL ' "$STEPS/$name.test.log" | tail -8 | sed 's/^/    /' | tee -a "$OUT/summary.txt"
  printf '%s\t%s\t%s\t%s\t%s\n' "$name" "$verdict" "$secs" "$rc" "$cause" >> "$SUMMARY"
  emit "{\"t\":\"step_end\",\"step\":\"$name\",\"mode\":\"$mode\",\"attempt\":$attempt,\"verdict\":\"$verdict\",\"cause\":\"$(jesc "$cause")\",\"secs\":$secs,\"rc\":$rc,\"log\":\"steps/$name.test.log\"}"
  log "    -> $verdict (${secs}s, rc=$rc${cause:+, causa=$cause})"
}

# Fail-fast: le o veredito que acabou de ir para o summary e encerra o nivel.
stop_check() { # $1 = passo
  [ "$STOP_ON_FAIL" = "1" ] || return 0
  local verdict; verdict=$(awk -F'\t' -v n="$1" '$1==n{print $2}' "$SUMMARY" 2>/dev/null | tail -1)
  case "$verdict" in
    FAIL|STALE)
      log "STOP-ON-FAIL: $1 -> $verdict"
      log "    bateria interrompida aqui (LEVEL_STOP_ON_FAIL=0 mede o placar inteiro)"
      emit "{\"t\":\"stop_on_fail\",\"step\":\"$1\",\"verdict\":\"$verdict\",\"modes\":\"$(jesc "$MODES")\"}"
      exit 1
      ;;
  esac
  return 0
}

step_bridge() { _step_bridge "$@"; stop_check "$1"; }
step_webtest() { _step_webtest; stop_check "webui-integracao-topicos"; }

_step_webtest() { # integracao + topicos (suite web; TopicsSweep roda no bloco unit dela)
  local name="webui-integracao-topicos"
  [ -f "$STATE/$name.done" ] && { log "SKIP $name (done)"; return 0; }
  [ -f "$WEBTEST" ] || { log "ERRO: Web.Test nao buildado"; return 1; }
  local attempt; attempt=$(( $(cat "$STATE/$name.attempt" 2>/dev/null || echo 0) + 1 )); echo "$attempt" > "$STATE/$name.attempt"
  emit "{\"t\":\"step_start\",\"step\":\"$name\",\"mode\":\"web\",\"attempt\":$attempt,\"run\":\"$HASH_GIT\"}"
  local t0; t0=$(date +%s)
  log "=== $name :: Web.Test (integracao + topicos)"
  timeout "${LEVEL_STEP_TIMEOUT:-3600}" dotnet exec "$WEBTEST" > "$STEPS/$name.test.log" 2>&1
  local rc=$?
  local secs=$(( $(date +%s) - t0 ))
  local verdict=SKIP cause=""
  grep -q '^FAIL ' "$STEPS/$name.test.log" && verdict=FAIL
  grep -q '^PASS ' "$STEPS/$name.test.log" && [ "$verdict" = SKIP ] && verdict=PASS
  [ "$rc" -ne 0 ] && [ "$verdict" = PASS ] && verdict=FAIL
  [ "$verdict" = FAIL ] && cause=$(classify "$STEPS/$name.test.log" "$rc")
  if [ "$(h "$WEBTEST")" != "$HASH_WEB" ]; then
    verdict=STALE; cause="binario web mudou durante o run"
    emit "{\"t\":\"step_stale\",\"step\":\"$name\",\"web_sha_inicio\":\"$HASH_WEB\",\"web_sha_agora\":\"$(h "$WEBTEST")\"}"
    rm -f "$STATE/$name.done"; HASH_WEB="$(h "$WEBTEST")"
  else
    echo "$rc $verdict $secs" > "$STATE/$name.done"
  fi
  grep -E '^PASS |^FAIL ' "$STEPS/$name.test.log" | tail -8 | sed 's/^/    /' | tee -a "$OUT/summary.txt"
  printf '%s\t%s\t%s\t%s\t%s\n' "$name" "$verdict" "$secs" "$rc" "$cause" >> "$SUMMARY"
  emit "{\"t\":\"step_end\",\"step\":\"$name\",\"mode\":\"web\",\"attempt\":$attempt,\"verdict\":\"$verdict\",\"cause\":\"$(jesc "$cause")\",\"secs\":$secs,\"rc\":$rc,\"log\":\"steps/$name.test.log\"}"
  log "    -> $verdict (${secs}s, rc=$rc${cause:+, causa=$cause})"
}

# --- gate -------------------------------------------------------------------
gate() { # recalcula pelo summary.tsv e decide o exit code
  local n p f s t st
  read -r n p f s t st < <(awk -F'\t' 'NR>1{n++; p+=($2=="PASS"); f+=($2=="FAIL"); s+=($2=="SKIP"); t+=$3; st+=($2=="STALE")}
    END{printf "%d %d %d %d %d %d\n", n, p, f, s, t, st}' "$SUMMARY")
  log ""
  log "=== resumo LEVEL $LEVEL ==="
  log "$n steps: PASS=$p FAIL=$f SKIP=$s STALE=$st (${t}s)"
  log ""
  log "=== falhas ==="
  awk -F'\t' 'NR>1 && ($2=="FAIL"||$2=="STALE"){printf "  %s (%s, rc=%s, causa=%s)\n", $1, $2, $4, ($5==""?"-":$5)}' "$SUMMARY" | tee -a "$OUT/summary.txt"
  local blocking="${f}${st}"
  local green=false
  [ "$p" -gt 0 ] && [ "$f" -eq 0 ] && [ "$st" -eq 0 ] && green=true
  emit "{\"t\":\"gate\",\"level\":$LEVEL,\"pass\":$p,\"fail\":$f,\"skip\":$s,\"stale\":$st,\"green\":$green,\"require_green\":$([ "$REQUIRE_GREEN" = "1" ] && echo true || echo false)}"
  emit "{\"t\":\"run_end\",\"level\":$LEVEL,\"stamp\":\"$STAMP\",\"pass\":$p,\"fail\":$f,\"skip\":$s,\"stale\":$st,\"green\":$green}"
  log "artefatos: $OUT (events.jsonl + summary.tsv)"
  if [ "$REQUIRE_GREEN" = "1" ] && [ "$green" != "true" ]; then
    log "GATE VERMELHO: FAIL=$f STALE=$st (PASS=$p)"
    return 1
  fi
  return 0
}

if [ "$GATE_ONLY" = "1" ]; then
  gate; exit $?
fi

case "$LEVEL" in
  1)
    STEPS_DESC="units bridge/opencode; devpipe bridge/opencode; tools-full (com ciclo git/arquivo) bridge/opencode; belts build (azul,verde) bridge/opencode; belts orch (azul,verde) bridge/opencode"
    if [ "$LIST" = "1" ]; then
      echo "Level 1 -> $STEPS_DESC"; exit 0
    fi
    emit "{\"t\":\"run_start\",\"level\":1,\"stamp\":\"$STAMP\",\"git\":\"$(jesc "$HASH_GIT")\",\"dlls\":{\"bridge\":\"$HASH_BRIDGE\",\"test\":\"$INIT_HASH_TEST\",\"web\":\"$HASH_WEB\"},\"bridge\":\"$(jesc "$BRIDGE")\",\"upstream\":\"$(jesc "$WAR_UPSTREAM")\",\"no_vision\":$([ "$NO_VISION" = "1" ] && echo true || echo false)}"
    log "=== LEVEL 1 ($OUT) ==="
    preflight || { log "BLOCKED: preflight reprovado"; gate; exit 3; }
    for mode in $MODES; do
      step_bridge "units-$mode" --only U- --mode "$mode"
      step_bridge "devpipe-$mode" --only E-devpipe --mode "$mode"
      step_bridge "tools-full-$mode" --only E-tools-full --mode "$mode"
      # Ordem: trilha Build primeiro, trilha Orch depois (duas trilhas da suite).
      step_bridge "belts-build-$mode" --only E-belts-build-direct --belts azul,verde --mode "$mode"
      step_bridge "belts-orch-$mode" --only E-belts-direct --belts azul,verde --mode "$mode"
    done
    step_webtest
    ;;
  2)
    STEPS_DESC="mission-tools; responses-tools; science-gpu; science-cpu(Q3); belts roxa build/orch (bridge+opencode); frontend Gen/Edit Razor/Edit React/Edit CSS/Verify; webui movida p/ fim do L3"
    if [ "$LIST" = "1" ]; then
      echo "Level 2 -> $STEPS_DESC"; exit 0
    fi
    emit "{\"t\":\"run_start\",\"modes\":\"$(jesc "$MODES")\",\"level\":2,\"stamp\":\"$STAMP\",\"git\":\"$(jesc "$HASH_GIT")\",\"dlls\":{\"bridge\":\"$HASH_BRIDGE\",\"test\":\"$INIT_HASH_TEST\",\"web\":\"$HASH_WEB\"},\"bridge\":\"$(jesc "$BRIDGE")\",\"upstream\":\"$(jesc "$WAR_UPSTREAM")\",\"q3_bridge\":\"$(jesc "${LEVEL_BRIDGE_Q3:-}")\"}"
    log "=== LEVEL 2 ($OUT) ==="
    preflight || { log "BLOCKED: preflight reprovado"; gate; exit 3; }
    for mode in $MODES; do
      step_bridge "mission-tools-$mode" --only E-mission-tools --mode "$mode"
      step_bridge "responses-tools-$mode" --only E-responses-tools --mode "$mode"
      step_bridge "science-gpu-$mode" --only E-science-gpu --mode "$mode"
      # Cientifico na linha Q3 (bridge de bancada via LEVEL_BRIDGE_Q3).
      if [ "${LEVEL_CPU:-1}" = "1" ]; then
        step_bridge "science-cpu-$mode" --only E-science-cpu --mode "$mode"
      else
        log "SKIP science-cpu-$mode (LEVEL_CPU=0)"
      fi
      # Faixa roxa: distribuida aqui; azul/verde ficam no L1 e marrom/preta no L3.
      step_bridge "belts-build-roxa-$mode" --only E-belts-build-direct --belts roxa --mode "$mode"
      step_bridge "belts-orch-roxa-$mode" --only E-belts-direct --belts roxa --mode "$mode"
      # Frontend pipeline fecha o L2: Gen MVC+Razor/React, edits reais e Verify.
      step_bridge "front-$mode" --only E-front --mode "$mode"
    done
    # Web UI saiu do L2: roda no fim da fila do L3.
    ;;
  3)
    STEPS_DESC="belts marrom,preta: trilha build + trilha orch, modos bridge e opencode (harness on); webui no fim"
    if [ "$LIST" = "1" ]; then
      echo "Level 3 -> $STEPS_DESC"; exit 0
    fi
    emit "{\"t\":\"run_start\",\"level\":3,\"stamp\":\"$STAMP\",\"git\":\"$(jesc "$HASH_GIT")\",\"dlls\":{\"bridge\":\"$HASH_BRIDGE\",\"test\":\"$INIT_HASH_TEST\",\"web\":\"$HASH_WEB\"},\"bridge\":\"$(jesc "$BRIDGE")\",\"upstream\":\"$(jesc "$WAR_UPSTREAM")\"}"
    log "=== LEVEL 3 ($OUT) ==="
    preflight || { log "BLOCKED: preflight reprovado"; gate; exit 3; }
    HSTATUS="$(curl -s -m 5 -H "Authorization: Bearer $API" "$BRIDGE/api/harness" 2>/dev/null)"
    [ -n "$HSTATUS" ] || HSTATUS="$(curl -s -m 5 -H "Authorization: Bearer $API" "$BRIDGE/api/status" 2>/dev/null)"
    log "harness na bridge $BRIDGE: $(echo "$HSTATUS" | head -c 300)"
    echo "$HSTATUS" | grep -q '"main_harness":true' \
      || log "AVISO: main_harness nao esta true — Level 3 pede harness ligado (--main-harness 1 --harness-tails 1)"
    for mode in $MODES; do
      step_bridge "belts-build-final-$mode" --only E-belts-build-direct --belts marrom,preta --mode "$mode"
      step_bridge "belts-orch-final-$mode" --only E-belts-direct --belts marrom,preta --mode "$mode"
    done
    # Web UI no fim da fila do L3 (saiu do L2).
    step_webtest
    ;;
  *)
    echo "Level $LEVEL ainda nao definido (implementado: 1, 2, 3)"; exit 2 ;;
esac

gate; exit $?
