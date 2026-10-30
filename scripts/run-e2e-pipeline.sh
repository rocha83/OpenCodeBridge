#!/usr/bin/env bash
# Pipeline E2E sequencial ativavel por fase.
# Uso: ./run-e2e-pipeline.sh --phase 0|1|2|3|all
set -uo pipefail

PHASE="${1:-all}"
if [[ "$PHASE" == "--phase" ]]; then PHASE="${2:-all}"; fi
NOBUILD=0
for a in "$@"; do [[ "$a" == "--no-build" ]] && NOBUILD=1; done

REPO="${REPO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
# Sob sudo, o HOME vira /root: o opencode perde o config de modelos/agents.
# Aponta o XDG_CONFIG_HOME para o home do usuario invocador.
CFG_USER="${SUDO_USER:-$(id -un)}"
CFG_HOME="$(getent passwd "$CFG_USER" | cut -d: -f6)"
[[ -n "$CFG_HOME" ]] && export XDG_CONFIG_HOME="${XDG_CONFIG_HOME:-$CFG_HOME/.config}"
LOG="$REPO/logs/pipeline.log"
mkdir -p "$REPO/logs"
exec > >(tee -a "$LOG") 2>&1

echo "=== Pipeline E2E - $(date) - phase=$PHASE ==="

# Build unico (Release) antes das fases; testes rodam com --no-build.
# Em producao, pare as bridges antes (PDB lock): use --no-build apos build manual.
build_once() {
  if [[ "$NOBUILD" == "1" ]]; then echo "--- Build unico: PULADO (--no-build) ---"; return; fi
  echo "--- Build unico (Release) ---"
  cd "$REPO"
  dotnet build Rochas.OpenCodeBridge.Test/Rochas.OpenCodeBridge.Test.csproj -c Release | tail -4
}

phase0() {
  echo "--- Phase 0: preflight (llama + bridges + web + auth + VRAM) ---"
  "$REPO/scripts/health-check.sh" || { echo "PREFLIGHT FALHOU — corrija o stack antes das suites"; return 1; }
}

phase1() {
  echo "--- Phase 1: unit tests (bridge + web, sem modelo) ---"
  cd "$REPO"
  dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --skip-e2e --api-key guest@123 --bridge http://127.0.0.1:4124
  dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release --no-build -- --web "http://127.0.0.1:${BRIDGE_WEB_PORT:-4130}" --units-only
}

phase_web() {
  echo "--- Phase web: integracao da UI (Web :${BRIDGE_WEB_PORT:-4130} + modelo vivo) ---"
  cd "$REPO"
  dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release --no-build -- --web "http://127.0.0.1:${BRIDGE_WEB_PORT:-4130}" --integration-only
}

phase2() {
  echo "--- Phase 2: bridge pure E2E (science + mission, role build) ---"
  cd "$REPO"
  dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-science --api-key guest@123 --bridge http://127.0.0.1:4124
  dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --only E-mission-tools --api-key guest@123 --bridge http://127.0.0.1:4124
}

phase3() {
  echo "--- Phase 3: opencode standalone (mesmos enunciados, agent build / 12B GPU) ---"
  cd "$REPO"
  dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --standalone --only E-standalone --api-key guest@123 --bridge http://127.0.0.1:4124
  echo "--- Phase 3b: opencode standalone (agent exec / E2B CPU) ---"
  dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --standalone --standalone-agent exec --only E-standalone-oi --api-key guest@123 --bridge http://127.0.0.1:4125
}

case "$PHASE" in
  0) phase0 ;;
  1) build_once; phase1 ;;
  2) build_once; phase2 ;;
  3) build_once; phase3 ;;
  web) build_once; phase_web ;;
  all) build_once; phase0; phase1; phase2; phase3; phase_web ;;
  *) echo "phase invalida: $PHASE (use 0|1|2|3|web|all)"; exit 1 ;;
esac

echo "=== Pipeline phase=$PHASE done $(date) ==="
