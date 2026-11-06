#!/usr/bin/env bash
# set-bridge-flags.sh <tails 0|1> <anti-repeat 0|1> [unit]
#
# Fixa os knots MALEAVEIS da bridge alvo (cauda de tools e anti-repeat).
# O main harness NAO e' parametro daqui: em `--mode bridge` ele e' gate duro
# (a bridge nem sobe com `--main-harness 0` neste modo).
#
# Uso tipico nas rodadas de comparacao:
#   scripts/lab/set-bridge-flags.sh 0 0   # rodada 1: so main harness
#   scripts/lab/set-bridge-flags.sh 0 1   # rodada 2: + anti-repeat
#   scripts/lab/set-bridge-flags.sh 1 1   # rodada 3: + cauda de tools
set -euo pipefail
TAILS="${1:?uso: set-bridge-flags.sh <tails 0|1> <anti-repeat 0|1> [unit]}"
ANTI="${2:?uso: set-bridge-flags.sh <tails 0|1> <anti-repeat 0|1> [unit]}"
UNIT="${3:-bridge-gpu}"
F="/etc/systemd/system/${UNIT}.service"
BACKUP_DIR="${BRIDGE_BACKUP_DIR:-/tmp/opencode}"

[ -f "$F" ] || { echo "FAIL: unit inexistente: $F" >&2; exit 1; }
mkdir -p "$BACKUP_DIR"
cp -a "$F" "${BACKUP_DIR}/${UNIT}.service.bak-$(date +%Y%m%d-%H%M%S)"

python3 - "$F" "$TAILS" "$ANTI" <<'PY'
import re, sys
path, tails, anti = sys.argv[1], sys.argv[2], sys.argv[3]
txt = open(path, encoding="utf-8").read()
scope = "managed" if tails == "1" else "off"
subs = [
    (r"--harness-tails\s+\d+", f"--harness-tails {tails}"),
    (r"--tail-scope\s+\S+", f"--tail-scope {scope}"),
    (r"--anti-repeat\s+\d+", f"--anti-repeat {anti}"),
]
for pat, rep in subs:
    txt, n = re.subn(pat, rep, txt, count=1)
    if n != 1:
        sys.exit(f"padrao nao encontrado no ExecStart: {pat}")
if not re.search(r"--main-harness\s+1", txt):
    sys.exit("--main-harness 1 ausente: modo bridge exige main harness ON")
open(path, "w", encoding="utf-8").write(txt)
print(f"flags gravadas: tails={tails} scope={scope} anti-repeat={anti}")
PY

systemctl daemon-reload
systemctl restart "$UNIT"
sleep 2
systemctl is-active "$UNIT"
grep -o -- "--harness-tails [01].*--anti-repeat-retry 1" "$F"
