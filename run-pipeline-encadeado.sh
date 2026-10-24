#!/usr/bin/env bash
# Pipeline encadeado completo: health -> units -> devpipe -> science -> mission -> standalone -> belts -> frontpipe
# Reporta tempo e status a cada fase. Log persistente.
set -uo pipefail

REPO="/media/mint/3686C649614854E61/Projetos/Git/OpenCodeBridge"
LOG="$REPO/test-logs/pipeline-encadeado.log"
mkdir -p "$REPO/test-logs"
exec > >(tee -a "$LOG") 2>&1

API_KEY="guest@123"
BRIDGE="http://127.0.0.1:4124"
WEB="http://127.0.0.1:4130"

ts() { date '+%Y-%m-%d %H:%M:%S'; }
phase() { echo; echo "========== $1 | $(ts) =========="; }
ok() { echo ">>> $1 OK | $(ts)"; }
fail() { echo ">>> $1 FAIL | $(ts)"; }

cd "$REPO"

phase "PHASE 0 - HEALTH"
for url in http://127.0.0.1:4100/health http://127.0.0.1:4110/health; do
  echo -n "$url: "; curl -s -m 5 -H "Authorization: Bearer $API_KEY" "$url"; echo
done
for url in http://127.0.0.1:4124/api/status http://127.0.0.1:4125/api/status; do
  echo -n "$url: "; curl -s -m 5 -H "Authorization: Bearer $API_KEY" "$url" | python3 -c 'import sys,json; d=json.load(sys.stdin); print("status=",d.get("status"),"main_harness=",d.get("main_harness"),"harness_tails=",d.get("harness_tails"),"max_tokens=",d.get("max_tokens"))'
done
nvidia-smi --query-gpu=memory.used,memory.total,utilization.gpu --format=csv,noheader

phase "PHASE 1 - UNIT TESTS (skip e2e)"
T0=$(date +%s)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --skip-e2e --api-key "$API_KEY" --bridge "$BRIDGE"
RC=$?
T1=$(date +%s); echo ">>> phase1 exit=$RC | $(( (T1-T0)/60 ))m $(( (T1-T0)%60 ))s | $(ts)"
[[ $RC -eq 0 ]] && ok "phase1" || fail "phase1"

phase "PHASE 2a - DEVPIPE (bridge pura, 12B tools)"
T0=$(date +%s)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-devpipe-tools --api-key "$API_KEY" --bridge "$BRIDGE"
RC=$?
T1=$(date +%s); echo ">>> phase2a exit=$RC | $(( (T1-T0)/60 ))m $(( (T1-T0)%60 ))s | $(ts)"
[[ $RC -eq 0 ]] && ok "phase2a" || fail "phase2a"

phase "PHASE 2b - SCIENCE (bridge pura, 12B GPU1)"
T0=$(date +%s)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-science-gpu --api-key "$API_KEY" --bridge "$BRIDGE"
RC=$?
T1=$(date +%s); echo ">>> phase2b-gpu1 exit=$RC | $(( (T1-T0)/60 ))m $(( (T1-T0)%60 ))s | $(ts)"
[[ $RC -eq 0 ]] && ok "phase2b-gpu1" || fail "phase2b-gpu1"

phase "PHASE 2c - SCIENCE (bridge pura, E2B GPU2)"
T0=$(date +%s)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-science-cpu --api-key "$API_KEY" --bridge "$BRIDGE"
RC=$?
T1=$(date +%s); echo ">>> phase2c-gpu2 exit=$RC | $(( (T1-T0)/60 ))m $(( (T1-T0)%60 ))s | $(ts)"
[[ $RC -eq 0 ]] && ok "phase2c-gpu2" || fail "phase2c-gpu2"

phase "PHASE 2d - MISSION (bridge pura, 12B)"
T0=$(date +%s)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-mission-tools --api-key "$API_KEY" --bridge "$BRIDGE"
RC=$?
T1=$(date +%s); echo ">>> phase2d exit=$RC | $(( (T1-T0)/60 ))m $(( (T1-T0)%60 ))s | $(ts)"
[[ $RC -eq 0 ]] && ok "phase2d" || fail "phase2d"

phase "PHASE 3 - STANDALONE (opencode real, 12B GPU1)"
T0=$(date +%s)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --standalone --only E-standalone --api-key "$API_KEY" --bridge "$BRIDGE"
RC=$?
T1=$(date +%s); echo ">>> phase3 exit=$RC | $(( (T1-T0)/60 ))m $(( (T1-T0)%60 ))s | $(ts)"
[[ $RC -eq 0 ]] && ok "phase3" || fail "phase3"

phase "PHASE 3b - STANDALONE (opencode real, E2B GPU2)"
T0=$(date +%s)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --standalone --standalone-agent exec --only E-standalone-oi --api-key "$API_KEY" --bridge http://127.0.0.1:4125
RC=$?
T1=$(date +%s); echo ">>> phase3b exit=$RC | $(( (T1-T0)/60 ))m $(( (T1-T0)%60 ))s | $(ts)"
[[ $RC -eq 0 ]] && ok "phase3b" || fail "phase3b"

phase "PHASE 4 - BELTS (Web UI, ate roxa)"
T0=$(date +%s)
cd "$REPO/Rochas.OpenCodeBridge.Web.Test"
dotnet run --project . -c Release --no-build -- --ui --belts azul,verde,roxa --phase full --web "$WEB" 2>&1 | tail -20
RC=${PIPESTATUS[0]}
T1=$(date +%s); echo ">>> phase4 exit=$RC | $(( (T1-T0)/60 ))m $(( (T1-T0)%60 ))s | $(ts)"
cd "$REPO"
[[ $RC -eq 0 ]] && ok "phase4" || fail "phase4"

phase "PHASE 5 - FRONTPIPE (12B gera, E2B ajusta, Playwright confere)"
T0=$(date +%s)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-front --api-key "$API_KEY" --bridge "$BRIDGE"
RC=$?
T1=$(date +%s); echo ">>> phase5 exit=$RC | $(( (T1-T0)/60 ))m $(( (T1-T0)%60 ))s | $(ts)"
[[ $RC -eq 0 ]] && ok "phase5" || fail "phase5"

phase "PIPELINE ENCADEADO COMPLETO | $(ts)"
echo "GPU1 12B: 131072x1 | GPU2 E2B: 65536x2 | VRAM: $(nvidia-smi --query-gpu=memory.used,memory.total --format=csv,noheader) | consumo: GPU $(nvidia-smi --query-gpu=power.draw --format=csv,noheader,nounits)W"