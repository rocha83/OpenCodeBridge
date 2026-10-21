#!/usr/bin/env bash
# Pipeline E2E sequencial ativavel por fase.
# Uso: ./run-e2e-pipeline.sh --phase 0|1|2|3|all
set -uo pipefail

PHASE="${1:-all}"
if [[ "$PHASE" == "--phase" ]]; then PHASE="${2:-all}"; fi
NOBUILD=0
for a in "$@"; do [[ "$a" == "--no-build" ]] && NOBUILD=1; done

REPO="${REPO:-/media/mint/3686C649614854E61/Projetos/Git/OpenCodeBridge}"
# Sob sudo, o HOME vira /root: o opencode perde o config de modelos/agents.
# Aponta o XDG_CONFIG_HOME para o home do usuario invocador.
CFG_USER="${SUDO_USER:-$(id -un)}"
CFG_HOME="$(getent passwd "$CFG_USER" | cut -d: -f6)"
[[ -n "$CFG_HOME" ]] && export XDG_CONFIG_HOME="${XDG_CONFIG_HOME:-$CFG_HOME/.config}"
LOG=/tmp/opencode/pipeline.log
mkdir -p /tmp/opencode
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
  echo "--- Phase 0: health ---"
  for url in http://127.0.0.1:4100/health http://127.0.0.1:4110/health; do
    echo -n "$url: "; curl -s -m 5 -H "Authorization: Bearer guest@123" "$url"; echo
  done
  for url in http://127.0.0.1:4124/api/status http://127.0.0.1:4125/api/status; do
    echo -n "$url: "; curl -s -m 5 -H "Authorization: Bearer guest@123" "$url"; echo
  done
  nvidia-smi --query-gpu=memory.used,memory.free --format=csv,noheader
}

phase1() {
  echo "--- Phase 1: unit tests (skip e2e) ---"
  cd "$REPO"
  dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- --skip-e2e --api-key guest@123 --bridge http://127.0.0.1:4124
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
  all) build_once; phase0; phase1; phase2; phase3 ;;
  *) echo "phase invalida: $PHASE"; exit 1 ;;
esac

echo "=== Pipeline phase=$PHASE done $(date) ==="
