#!/usr/bin/env bash
# Pipeline com GPU1 (12B) e GPU2 (E2B) rodando em paralelo (forca total na GPU).
set -uo pipefail

REPO="/media/mint/3686C649614854E61/Projetos/Git/OpenCodeBridge"
LOG="$REPO/test-logs/pipeline-paralelo.log"
mkdir -p "$REPO/test-logs"
exec > >(tee -a "$LOG") 2>&1

API_KEY="guest@123"
BRIDGE="http://127.0.0.1:4124"

ts() { date '+%Y-%m-%d %H:%M:%S'; }
phase() { echo; echo "========== $1 | $(ts) =========="; }

cd "$REPO"
> "$LOG"

phase "PHASE 0 - HEALTH"
for url in http://127.0.0.1:4100/health http://127.0.0.1:4110/health; do
  echo -n "$url: "; curl -s -m 5 -H "Authorization: Bearer $API_KEY" "$url"; echo
done
nvidia-smi --query-gpu=memory.used,memory.total,utilization.gpu --format=csv,noheader

phase "PHASE 1 - UNIT TESTS (skip e2e)"
T0=$(date +%s)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --skip-e2e --api-key "$API_KEY" --bridge "$BRIDGE"
echo ">>> phase1 exit=$? | $(( ($(date +%s)-T0)/60 ))m | $(ts)"

phase "PHASE 2 - PARALELO: GPU1 (12B) + GPU2 (E2B) trabalhando juntos"
T0=$(date +%s)
# GPU1 (12B): devpipe + scripter-gen (compoe script bash + companion .md)
( dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-devpipe-tools --api-key "$API_KEY" --bridge "$BRIDGE" > "$REPO/test-logs/par-devpipe.log" 2>&1; echo "devpipe exit=$?" > "$REPO/test-logs/par-devpipe.exit" ) &
P1=$!
( dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-scripter-gen --api-key "$API_KEY" --bridge "$BRIDGE" > "$REPO/test-logs/par-scripter.log" 2>&1; echo "scripter exit=$?" > "$REPO/test-logs/par-scripter.exit" ) &
P2=$!
# GPU2 (E2B): companion-gen (le script + gera companion .md) + science
( dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-companion-gen --api-key "$API_KEY" --bridge "$BRIDGE" > "$REPO/test-logs/par-companion.log" 2>&1; echo "companion exit=$?" > "$REPO/test-logs/par-companion.exit" ) &
P3=$!
( dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-exec-read --api-key "$API_KEY" --bridge "$BRIDGE" > "$REPO/test-logs/par-exec.log" 2>&1; echo "exec exit=$?" > "$REPO/test-logs/par-exec.exit" ) &
P4=$!
( dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-science-cpu --api-key "$API_KEY" --bridge "$BRIDGE" > "$REPO/test-logs/par-science-gpu2.log" 2>&1; echo "science-gpu2 exit=$?" > "$REPO/test-logs/par-science-gpu2.exit" ) &
P5=$!

echo "5 processos em paralelo: devpipe, scripter-gen, companion-gen, exec-read, science-gpu2"
wait $P1 $P2 $P3 $P4 $P5
T1=$(date +%s)
echo ">>> phase2 paralelo concluido | $(( (T1-T0)/60 ))m $(( (T1-T0)%60 ))s | $(ts)"
for f in par-devpipe par-scripter par-companion par-exec par-science-gpu2; do
  echo "--- $f: $(cat $REPO/test-logs/$f.exit 2>/dev/null) ---"
  grep -E 'PASS |FAIL ' $REPO/test-logs/$f.log 2>/dev/null | tail -2
done
nvidia-smi --query-gpu=memory.used,memory.total,utilization.gpu,power.draw --format=csv,noheader

phase "PHASE 3 - STANDALONE (opencode real, sequencial)"
T0=$(date +%s)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --standalone --only E-standalone --api-key "$API_KEY" --bridge "$BRIDGE"
echo ">>> phase3 exit=$? | $(( ($(date +%s)-T0)/60 ))m | $(ts)"
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --standalone --standalone-agent exec --only E-standalone-oi --api-key "$API_KEY" --bridge http://127.0.0.1:4125
echo ">>> phase3b exit=$? | $(ts)"

phase "PHASE 4 - BELTS (Web UI, ate roxa, forca total)"
T0=$(date +%s)
cd "$REPO/Rochas.OpenCodeBridge.Web.Test"
dotnet run --project . -c Release --no-build -- --ui --belts azul,verde,roxa --phase full --web http://127.0.0.1:4130 2>&1 | tail -25
echo ">>> phase4 exit=${PIPESTATUS[0]} | $(( ($(date +%s)-T0)/60 ))m | $(ts)"
cd "$REPO"

phase "PHASE 5 - FRONTPIPE (12B gera, E2B ajusta, Playwright confere)"
T0=$(date +%s)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-front --api-key "$API_KEY" --bridge "$BRIDGE"
echo ">>> phase5 exit=$? | $(( ($(date +%s)-T0)/60 ))m | $(ts)"

phase "PIPELINE PARALELO COMPLETO | $(ts)"
echo "GPU1 12B: 131072x1 | GPU2 E2B: 65536x2 | VRAM: $(nvidia-smi --query-gpu=memory.used,memory.total --format=csv,noheader) | consumo GPU: $(nvidia-smi --query-gpu=power.draw --format=csv,noheader)"