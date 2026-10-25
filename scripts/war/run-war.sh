#!/bin/bash
# ============================================================================
# Bateria WAR v2 - estresse de GPU com 3 sessoes simultaneas.
#
#   lane q4   -> bridge :4134 -> llama :4100 (Q4, GPU1)
#   lane q3a  -> bridge :4135 -> llama :4110 (Q3, slot 1)
#   lane q3b  -> bridge :4136 -> llama :4110 (Q3, slot 2)
#
# Cada lane roda sua fila (embaralhada com WAR_SEED) e tem bridge + log proprios,
# entao o delta de log/metricas por step e' limpo mesmo com as 3 em paralelo.
# Por step: log do teste, delta do log da bridge da lane (harness/sonda/breaker/
# coerce/tool-ask), /api/metrics antes/depois, /metrics do llama antes/depois,
# nvidia-smi antes/depois. Amostrador nvidia-smi continuo no run inteiro.
#
# Checkpoint/retomada: state/<lane>/<teste>.done (default retoma).
# Parciais: summary.tsv + status.json atualizados a cada step; steps/ com tudo.
#
# Uso:
#   run-war.sh                 # ALL (Q4 + 2 sessoes Q3), retoma de onde parou
#   run-war.sh --fresh         # zera plano/estado e roda tudo
#   run-war.sh --q4            # SOMENTE Q4 (1 bridge/sessao, sem lanes Q3)
#   run-war.sh --q3            # SOMENTE Q3 (2 sessoes no llama :4110)
#   run-war.sh --list          # mostra o plano (lane + teste) e o que ja foi feito
#   WAR_SEED=42 run-war.sh     # fila reproduzivel
#   WAR_LANES=q4 run-war.sh    # idem --q4 (env)
# O modo fica gravado em <out>/lanes.mode: mudar de modo exige --fresh ou outro WAR_OUT.
# ============================================================================
set -u
# Raiz do repo: derivada da localizacao do script (scripts/war/ -> raiz) ou WAR_ROOT.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="${WAR_ROOT:-${REPO:-$(cd "$HERE/../.." && pwd)}}"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="${WAR_OUT:-$STAMP}"
[[ "$OUT" = /* ]] || OUT="/tmp/opencode/war/$OUT"
SEED="${WAR_SEED:-$STAMP}"
STEPS="$OUT/steps"
STATE="$OUT/state"
LOGSDIR="$OUT/bridges"
mkdir -p "$STEPS" "$STATE" "$LOGSDIR"

ACTION=run
FRESH=0
LANES="${WAR_LANES:-all}"
for arg in "$@"; do
  case "$arg" in
    --fresh) FRESH=1 ;;
    --list|--status) ACTION=--list ;;
    --q4|--lanes=q4|--only-q4) LANES=q4 ;;
    --q3|--lanes=q3|--only-q3) LANES=q3 ;;
    --all|--lanes=all) LANES=all ;;
    run|--run) : ;;
    *) echo "arg desconhecido: $arg (use --fresh | --list | --q4 | --q3 | --all)"; exit 2 ;;
  esac
done
case "$LANES" in q4|q3|all) ;; *) echo "WAR_LANES invalida: $LANES (q4|q3|all)"; exit 2 ;; esac
active_lanes() { case "$LANES" in q4) echo "q4" ;; q3) echo "q3a q3b" ;; *) echo "q4 q3a q3b" ;; esac; }
MODE_FILE="$OUT/lanes.mode"
if [ -f "$MODE_FILE" ] && [ "$(cat "$MODE_FILE")" != "$LANES" ] && [ "$FRESH" != 1 ]; then
  echo "OUT $OUT ja usado com lanes=$(cat "$MODE_FILE") (pedido: $LANES). Use --fresh ou outro WAR_OUT."
  exit 2
fi
echo "$LANES" > "$MODE_FILE"

log() { echo "$*" | tee -a "$OUT/summary.txt"; }
die() { log "ERRO: $*"; exit 1; }

# ---------------------------------------------------------------- plano (uma vez)
PLAN="$OUT/plan.tsv"
build_plan() {
  python3 - "$SEED" "$PLAN" "$LANES" <<'PY'
import random, sys
seed, plan, mode = sys.argv[1], sys.argv[2], sys.argv[3]
tests = [
 "E-science-gpu","E-science-cpu","E-tools-full","E-mission-tools","E-belts-direct",
 "E-belts-build-direct","E-belts-webui","E-front-gen","E-front-edit","E-front-verify",
 "E-responses-simple","E-responses-tools","E-vllm-tool-hermes",
 "E-devpipe-tools","E-devpipe-infra","E-devpipe-vision","E-devpipe-doc",
 "E-devpipe-vision-tool","E-stream-architect","E-review-architect",
 "E-iot-event-queue","E-error-mitigation","E-full-cycle-iot-queue-push",
 "E-healthy-concurrency","E-senior-react","E-senior-bus","E-senior-erp",
 "E-senior-arm","E-ent-crud-product","E-ent-crud-customer",
 "E-ent-crud-saleinvoice","E-ent-worker","E-ent-garnet","E-ent-dashboard",
 "E-scripter-gen","E-companion-gen","E-exec-read","E-orch-segment",
 "E-dual-tools",
 "E-standalone-oi","E-standalone-tools","E-standalone-write",
 "E-standalone-devpipe","E-standalone-science-tools","E-standalone-mission-tools",
]
active = {"q4": ["q4"], "q3": ["q3a", "q3b"], "all": ["q4", "q3a", "q3b"]}[mode]
# Q4 sozinho: nao roda o teste explicito da ponte CPU (Q3), que exigiria o :4125.
if mode == "q4":
    tests = [t for t in tests if t != "E-science-cpu"]
rnd = random.Random(seed)
rnd.shuffle(tests)
order = list(active)
rnd.shuffle(order)
lanes = {l: [] for l in active}
for i, t in enumerate(tests):
    lanes[order[i % len(order)]].append(t)
# science-cpu so' faz sentido garantido numa lane q3; no all cai na q3a
if mode != "q4" and "E-science-cpu" in tests:
    if "E-science-cpu" not in lanes["q3a"]:
        lanes["q3a"].append("E-science-cpu")
with open(plan, "w") as f:
    for lane in active:
        for t in lanes[lane]:
            f.write(f"{lane}\t{t}\n")
print("plano:", ", ".join(f"{k}={len(v)}" for k, v in lanes.items()))
PY
}

if [ "$FRESH" = "1" ]; then
  rm -rf "$STATE" "$STEPS" "$PLAN" "$OUT/summary.txt" "$OUT/summary.tsv" "$OUT/status.json"
  mkdir -p "$STATE" "$STEPS"
fi
[ -f "$PLAN" ] || build_plan | tee -a "$OUT/summary.txt"
[ -f "$OUT/summary.tsv" ] || printf 'lane\ttest\trc\tverdict\tsecs\ttest_log\n' > "$OUT/summary.tsv"

if [ "$ACTION" = "--list" ]; then
  echo "== plano =="; column -t -s $'\t' "$PLAN"
  echo; echo "== feito =="
  find "$STATE" -name '*.done' -printf '%P\n' 2>/dev/null | sed 's/\.done$//' | sort
  exit 0
fi

# ---------------------------------------------------------------- infra
DLL="$ROOT/Rochas.OpenCodeBridge/bin/Release/net10.0/Rochas.OpenCodeBridge.dll"
[ -f "$DLL" ] || die "bridge nao buildada: dotnet build -c Release Rochas.OpenCodeBridge.slnx"

bridge_port() { case "$1" in q4) echo 4134;; q3a) echo 4135;; q3b) echo 4136;; esac; }
bridge_up()   { case "$1" in q4) echo 4100;; q3a|q3b) echo 4110;; esac; }
bridge_model(){ case "$1" in q4) echo Gemma4-E4B-Q4;; q3a|q3b) echo Gemma4-E4B-UD-Q3;; esac; }
bridge_ctx()  { case "$1" in q4) echo 131072;; q3a|q3b) echo 65536;; esac; }
# flags extras que o runner exige por teste (via dupla / opencode real)
step_flags() { case "$1" in E-dual-tools) echo "--dual" ;; E-standalone-*) echo "--standalone" ;; *) echo "" ;; esac; }
# ponte CPU anunciada ao teste: na lane Q3 usa a PROPRIA lane; na Q4 usa a prod :4125
step_cpu() { case "$1" in q3a) echo "http://127.0.0.1:4135" ;; q3b) echo "http://127.0.0.1:4136" ;; *) echo "http://127.0.0.1:4125" ;; esac; }

declare -A BPID
start_lane_bridge() {
  local lane="$1" port up model ctx
  port=$(bridge_port "$lane"); up=$(bridge_up "$lane")
  model=$(bridge_model "$lane"); ctx=$(bridge_ctx "$lane")
  local log="$LOGSDIR/bridge-$lane.log"
  ( cd "$ROOT/Rochas.OpenCodeBridge/bin/Release/net10.0" && exec /usr/share/dotnet/dotnet "$DLL" \
      --listen 127.0.0.1 --port "$port" --upstream "http://127.0.0.1:$up" \
      --model "$model" --thinking events --temperature 0.5 --max-tokens 16384 \
      --context-guard 1 --context-tokens "$ctx" --api-key guest@123 \
      --passthrough-responses true --main-harness 0 --harness-tails 0 --legacy-harness 0 \
      --anti-repeat 1 --anti-repeat-retry 1 --coerce-text-tools 0 \
      --coerce-narrated 0 --tool-strategy 0 --anti-repeat-window 512 \
      --tail-scope "${WAR_TAIL_SCOPE:-all}" --diagnostic 1 --log "$log" \
      ${WAR_BRIDGE_ARGS:-} ) > "$LOGSDIR/bridge-$lane.out" 2>&1 &
  BPID[$lane]=$!
  for _ in $(seq 1 80); do
    curl -s -o /dev/null -m 2 -H "Authorization: Bearer guest@123" "http://127.0.0.1:$port/api/status" && return 0
    sleep 0.5
  done
  die "bridge $lane nao subiu em :$port ($(tail -2 "$LOGSDIR/bridge-$lane.out" 2>/dev/null))"
}

LLAMA_METRICS_Q4="http://127.0.0.1:4100/metrics"
LLAMA_METRICS_Q3="http://127.0.0.1:4110/metrics"
metrics_snapshot() { # lane, prefixo
  local lane="$1" pre="$2" port url
  port=$(bridge_port "$lane")
  url=$( [ "$lane" = q4 ] && echo "$LLAMA_METRICS_Q4" || echo "$LLAMA_METRICS_Q3" )
  curl -s -m 5 -H "Authorization: Bearer guest@123" "http://127.0.0.1:$port/api/metrics" > "$pre.bridge.json" 2>/dev/null
  curl -s -m 5 "$url" | grep -E '^llamacpp:' > "$pre.llama.prom" 2>/dev/null || true
  curl -s -m 5 -H "Authorization: Bearer guest@123" "http://127.0.0.1:$port/api/harness" > "$pre.harness.json" 2>/dev/null
  nvidia-smi --query-gpu=timestamp,index,utilization.gpu,memory.used,power.draw,temperature.gpu \
    --format=csv,noheader,nounits > "$pre.gpu.csv" 2>/dev/null || true
}

gpu_sampler() {
  while true; do
    nvidia-smi --query-gpu=timestamp,index,utilization.gpu,memory.used,power.draw,temperature.gpu \
      --format=csv,noheader,nounits >> "$OUT/gpu-samples.csv" 2>/dev/null || true
    sleep 2
  done
}

evidence() { # arquivo delta(s)
  local d="$1" f
  for f in "$d".bridge.log; do
    [ -f "$f" ] || continue
    for pat in tool-error-breaker anti-repeat anti-repeat-loop anti-repeat-args \
               anti-repeat-retry anti-repeat-sonda pattern-strip harness-strip coerce \
               tool-ask risk- tool-blocked tool-denied context-guard; do
      local n
      n=$(grep -c "$pat" "$f" 2>/dev/null || true)
      [ "${n:-0}" -gt 0 ] && printf '%-24s %s\n' "$pat" "$n"
    done
  done > "$d.evidence.txt" 2>/dev/null || true
}

SUMMARY_LOCK="$OUT/.summary.lock"

# ---------------------------------------------------------------- step
run_step() {
  local lane="$1" test="$2" port tag tlog stepdir rc secs verdict
  port=$(bridge_port "$lane")
  tag=$(printf '%s-%s' "$lane" "$test")
  tlog="$STEPS/$tag.test.log"
  stepdir="$STEPS/$tag"
  local blog="$LOGSDIR/bridge-$lane.log"
  local b0=0 a0=0
  [ -f "$blog" ] && b0=$(wc -l < "$blog")

  metrics_snapshot "$lane" "$stepdir.before"
  local t0 extra cpu_url
  t0=$(date +%s)
  extra=$(step_flags "$test")
  cpu_url=$(step_cpu "$lane")
  # Q4 sozinho: nao roda nem tenta os testes explicitos de CPU/Q3
  [ "$LANES" = "q4" ] && extra="$extra --skip-cpu"
  # Testes de navegador (frontpipe/belts) na Web UI real :4130, headed se HEADED=1
  if [ "${WAR_WEBDRIVER:-0}" = "1" ]; then
    extra="$extra --webdriver --webdriver-url ${WAR_WEBDRIVER_URL:-http://127.0.0.1:4130}"
  fi
  # shellcheck disable=SC2086
  WAR_CPU_BRIDGE="$cpu_url" timeout "${WAR_STEP_TIMEOUT:-7200}" \
      dotnet run --project "$ROOT/Rochas.OpenCodeBridge.Test" -c Release --no-build -- \
      --api-key guest@123 --skip-unit --only "$test" --bridge "http://127.0.0.1:$port" $extra \
      > "$tlog" 2>&1
  rc=$?
  secs=$(( $(date +%s) - t0 ))

  metrics_snapshot "$lane" "$stepdir.after"
  [ -f "$blog" ] && a0=$(wc -l < "$blog")
  tail -n +"$((b0 + 1))" "$blog" 2>/dev/null | head -n "$((a0 - b0))" > "$stepdir.bridge.log"
  evidence "$stepdir"

  if grep -q '^FAIL ' "$tlog"; then verdict=FAIL
  elif grep -q '^PASS ' "$tlog"; then verdict=PASS
  else verdict=SKIP; fi
  [ "$rc" -ne 0 ] && [ "$verdict" = PASS ] && verdict=FAIL
  # esperado-falha declarado (provas A/B negativas de outros testes) nao existe aqui:
  # cada step precisa PASS de verdade; rc!=0 e' FAIL.

  mkdir -p "$STATE/$lane"
  echo "$rc $verdict $secs" > "$STATE/$lane/$test.done"
  {
    flock 9
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$lane" "$test" "$rc" "$verdict" "$secs" "$tlog" >> "$OUT/summary.tsv"
    python3 - "$OUT" <<'PY'
import json, os, sys, time
out = sys.argv[1]
rows = []
for ln in open(os.path.join(out, "summary.tsv")).read().splitlines()[1:]:
    p = ln.split("\t")
    if len(p) >= 5:
        rows.append({"lane": p[0], "test": p[1], "rc": int(p[2]), "verdict": p[3], "secs": int(p[4])})
status = {
    "updated": time.strftime("%Y-%m-%dT%H:%M:%S"),
    "total": len(rows),
    "pass": sum(r["verdict"] == "PASS" for r in rows),
    "fail": sum(r["verdict"] == "FAIL" for r in rows),
    "skip": sum(r["verdict"] == "SKIP" for r in rows),
    "steps": rows,
}
json.dump(status, open(os.path.join(out, "status.json"), "w"), indent=1)
PY
  } 9>>"$SUMMARY_LOCK"
  echo "[$lane] $test -> $verdict (${secs}s, rc=$rc)" >> "$OUT/live.log"
}

lane_worker() {
  local lane="$1"
  local count=0
  while IFS=$'\t' read -r l t; do
    [ "$l" = "$lane" ] || continue
    [ -f "$STATE/$lane/$t.done" ] && { echo "[$lane] skip $t (done)" >> "$OUT/live.log"; continue; }
    count=$((count+1))
    echo "[$lane] start $t $(date +%H:%M:%S)" >> "$OUT/live.log"
    run_step "$lane" "$t"
  done < "$PLAN"
  echo "[$lane] fim ($count steps nesta sessao)" >> "$OUT/live.log"
}

# ---------------------------------------------------------------- execucao
cleanup() {
  [ -n "${GPUSAM_PID:-}" ] && kill "$GPUSAM_PID" 2>/dev/null
  for k in "${!BPID[@]}"; do kill "${BPID[$k]}" 2>/dev/null; done
}
trap cleanup EXIT INT TERM

log "=== WAR v2 $OUT (seed=$SEED, lanes=$LANES, resume=$((1-FRESH))) ==="
log "plano: $(wc -l < "$PLAN") testes nas lanes: $(active_lanes) (bridges 4134/4135/4136)"

# preflight: provas A/B das flags (stub, sem GPU) uma unica vez
if [ ! -f "$STATE/preflight.done" ]; then
  log "== preflight: provas A/B das flags (stub upstream) =="
  timeout 600 dotnet run --project "$ROOT/Rochas.OpenCodeBridge.Test" -c Release --no-build -- \
    --api-key guest@123 --skip-unit --only E-flag > "$STEPS/preflight-flags.test.log" 2>&1
  prc=$?
  grep -E '^PASS |^FAIL ' "$STEPS/preflight-flags.test.log" | sed 's/^/  /' | tee -a "$OUT/summary.txt"
  echo "$prc" > "$STATE/preflight.done"
  [ "$prc" -ne 0 ] && log "AVISO: preflight de flags falhou (rc=$prc) - segue para as lanes"
fi

for lane in $(active_lanes); do start_lane_bridge "$lane"; log "bridge $lane :$(bridge_port "$lane") -> :$(bridge_up "$lane") ok"; done

: > "$OUT/gpu-samples.csv"
gpu_sampler & GPUSAM_PID=$!

PIDS=()
for lane in $(active_lanes); do lane_worker "$lane" & PIDS+=($!); done
wait "${PIDS[@]}"

log ""
log "=== resumo por lane ==="
awk -F'\t' 'NR>1{k=$1; n[k]++; p[k]+=($4=="PASS"); f[k]+=($4=="FAIL"); s[k]+=($4=="SKIP"); t[k]+=$5}
 END{for(k in n) printf "%-4s %2d steps  PASS=%d FAIL=%d SKIP=%d  %ds\n", k, n[k], p[k], f[k], s[k], t[k]}' "$OUT/summary.tsv" | tee -a "$OUT/summary.txt"
log ""
log "=== falhas ==="
awk -F'\t' 'NR>1 && $4=="FAIL"{print $1" "$2" (rc="$3", "$5"s) -> "$6}' "$OUT/summary.tsv" | tee -a "$OUT/summary.txt"
log ""
log "artefatos: $OUT (steps/, bridges/, gpu-samples.csv, status.json)"
