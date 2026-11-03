#!/usr/bin/env bash
# war-round.sh <id> <tails 0|1> <anti-repeat 0|1>
#
# Uma rodada completa L1..L4 em RAIAS PARALELAS com fail-fast DESLIGADO: cada
# raia segue o placar inteiro (fail-fast dentro da raia e' coisa de rodada
# sequencial, onde a WAR para no primeiro nivel vermelho).
#
# A rodada fixa antes os knots maleaveis da bridge alvo (main harness sempre
# ON) e relaxa no WAR SO' o gate correspondente ao knot desligado: sem cauda de
# tools nao se cobra a cauda; sem anti-repeat nao se cobra anti-repeat.
# O relaxamento sai na evidencia como `preflight_aviso` + `relaxado_por`.
#
# Sequencia de comparacao do laboratorio (uma rodada por vez, nunca juntas):
#   scripts/lab/war-round.sh 1 0 0   # baseline: so main harness
#   scripts/lab/war-round.sh 2 0 1   # + anti-repeat
#   scripts/lab/war-round.sh 3 1 1   # + cauda de tools
set -euo pipefail
ID="${1:?uso: war-round.sh <id> <tails 0|1> <anti-repeat 0|1>}"
TAILS="${2:?uso: war-round.sh <id> <tails 0|1> <anti-repeat 0|1>}"
ANTI="${3:?uso: war-round.sh <id> <tails 0|1> <anti-repeat 0|1>}"
REPO="${REPO:-/usr/src/opencodebridge}"
BRIDGE="${WAR_BRIDGE:-http://127.0.0.1:4124}"
UPSTREAM="${WAR_UPSTREAM:-http://127.0.0.1:4100}"
API_KEY="${WAR_API_KEY:-guest@123}"
UNIT="${BRIDGE_UNIT:-bridge-gpu}"
LAB_DIR="${LAB_DIR:-$REPO/logs/levels}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAMP="R${ID}-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$LAB_DIR"
echo "$STAMP" > "$LAB_DIR/.stamp-R${ID}"

# 1) BUILD -> REDEPLOY -> RESTART e' o ciclo padrao de toda rodada: o binario
#    novo entra ANTES de o driver medir (sem isso a rodada mede codigo velho e o
#    guard de rebuild barra a reexecucao com "binarios inalterados desde o FAIL").
BUILD_LOG="$LAB_DIR/.build-R${ID}.log"
echo "== build (Release) -> $BUILD_LOG"
if ! sudo -n bash -c "cd '$REPO' && dotnet build -c Release Rochas.OpenCodeBridge.slnx > '$BUILD_LOG' 2>&1"; then
  echo "FAIL: build quebrou — ultimas linhas:"
  tail -20 "$BUILD_LOG"
  exit 1
fi
grep -E "Aviso\(s\)|Erro\(s\)|Compilação com êxito" "$BUILD_LOG" | tail -3

# 2) knots da bridge alvo (main harness fica ON: gate duro do modo bridge);
#    set-bridge-flags grava as flags e reinicia a unit (redeploy -> restart).
"$HERE/set-bridge-flags.sh" "$TAILS" "$ANTI" "$UNIT"

# 2) status da bridge alvo: fail fast se nao subir ou se main harness vier off
for _ in $(seq 1 20); do
  if curl -fsS -H "Authorization: Bearer $API_KEY" "$BRIDGE/api/status" > "$LAB_DIR/.status-R${ID}.json"; then break; fi
  sleep 1
done
python3 - "$LAB_DIR/.status-R${ID}.json" <<'PY'
import json, sys
s = json.load(open(sys.argv[1]))
chk = {k: s.get(k) for k in ("mode", "main_harness", "harness_tails", "tail_scope", "anti_repeat", "slot")}
print("status:", json.dumps(chk))
if s.get("main_harness") is not True:
    sys.exit("FAIL: main harness OFF na bridge alvo (modo bridge exige ON)")
if s.get("mode") != "bridge":
    sys.exit(f"FAIL: bridge em mode={s.get('mode')} (esperado bridge)")
PY

# 3) gates do WAR: relaxa SO' o knot desligado nesta rodada
GATES=()
[ "$TAILS" = "0" ] && GATES+=(--war-no-tails-gate)
[ "$ANTI" = "0" ] && GATES+=(--war-no-anti-repeat-gate)

cd "$REPO"
echo "== rodada ${ID}: tails=${TAILS} anti-repeat=${ANTI} raias paralelas, fail-fast OFF (${STAMP})"
sudo -n bash -c "cd $REPO && REPO=$REPO dotnet Rochas.OpenCodeBridge.Test/bin/Release/net10.0/Rochas.OpenCodeBridge.Test.dll \
  --war-all --war-parallel --war-modes bridge --war-min-free-vram 256 --war-no-stop-on-fail \
  --war-no-build --war-no-cpu --war-api-key $API_KEY --war-bridge $BRIDGE \
  --war-upstream $UPSTREAM --war-out logs/levels/$STAMP ${GATES[*]:-} \
  2>&1 | tee logs/levels/$STAMP.console.log"
