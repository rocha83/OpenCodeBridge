#!/bin/bash
# Autoteste do runner de levels: prova que (1) events.jsonl e JSON valido,
# (2) o gate derruba FAIL/STALE e aprova verde, (3) BLOCKED nao e FAIL.
# Nao usa GPU: opera em LEVEL_OUT temporario com summary.tsv sintetico.
# Uso: scripts/war/selftest-runner.sh
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
RUNNER="$HERE/run-levels.sh"
FAILS=0

ok()   { echo "  PASS $*"; }
bad()  { echo "  FAIL $*"; FAILS=$((FAILS + 1)); }

[ -f "$RUNNER" ] || { echo "runner ausente: $RUNNER"; exit 1; }
bash -n "$RUNNER" && ok "sintaxe do runner" || bad "sintaxe do runner"

mk() { # dir + summary sintetico ($2 = linhas de veredito) no layout real do runner
  local d; d=$(mktemp -d /tmp/opencode/selftest-runner.XXXXXX)
  mkdir -p "$d/L2-fixed"
  printf 'step\tverdict\tsecs\trc\tcause\n%s' "$2" > "$d/L2-fixed/summary.tsv"
  echo "$d"
}

# 1) verde -> gate aprova
D=$(mk x "$(printf 'a\tPASS\t1\t0\t\nb\tPASS\t2\t0\t\n')")
LEVEL_OUT="$D" LEVEL_STAMP=fixed "$RUNNER" --level 2 --gate-only >/dev/null 2>&1
[ $? -eq 0 ] && ok "gate aprova nivel verde" || bad "gate reprovou nivel verde"
grep -q '"green":true' "$D/L2-fixed/events.jsonl" 2>/dev/null && ok "evento gate green=true" || bad "evento gate green=true"
[ "$(python3 - "$D/L2-fixed/events.jsonl" <<'PY'
import json,sys
try:
    [json.loads(l) for l in open(sys.argv[1]) if l.strip()]
    print("ok")
except Exception as e:
    print("erro", e)
PY
)" = "ok" ] && ok "events.jsonl valido (JSON por linha)" || bad "events.jsonl invalido"

# 2) FAIL -> gate derruba
D=$(mk x "$(printf 'a\tPASS\t1\t0\t\nb\tFAIL\t2\t1\texecutor\n')")
LEVEL_OUT="$D" LEVEL_STAMP=fixed "$RUNNER" --level 2 --gate-only >/dev/null 2>&1
[ $? -ne 0 ] && ok "gate derruba FAIL" || bad "gate ignorou FAIL"
grep -q '"green":false' "$D/L2-fixed/events.jsonl" && ok "evento gate green=false" || bad "evento gate green=false"

# 3) STALE -> gate derruba
D=$(mk x "$(printf 'a\tPASS\t1\t0\t\nb\tSTALE\t2\t1\tbinario mudou\n')")
LEVEL_OUT="$D" LEVEL_STAMP=fixed "$RUNNER" --level 2 --gate-only >/dev/null 2>&1
[ $? -ne 0 ] && ok "gate derruba STALE" || bad "gate ignorou STALE"

# 4) REQUIRE_GREEN=0 -> nao derruba (modo observacao)
D=$(mk x "$(printf 'a\tFAIL\t1\t1\tundetermined\n')")
LEVEL_OUT="$D" LEVEL_STAMP=fixed LEVEL_REQUIRE_GREEN=0 "$RUNNER" --level 2 --gate-only >/dev/null 2>&1
[ $? -eq 0 ] && ok "gate desarmado passa" || bad "gate desarmado derrubou"

# 5) veredito BLOCKED existe no vocabulario do gate (nao conta como PASS)
D=$(mk x "$(printf 'a\tBLOCKED\t0\t3\tpreflight\n')")
LEVEL_OUT="$D" LEVEL_STAMP=fixed "$RUNNER" --level 2 --gate-only >/dev/null 2>&1
[ $? -ne 0 ] && ok "BLOCKED derruba o gate" || bad "BLOCKED passou no gate"

# 6) --mode pure nao existe mais (alias removido): o runner recusa
"$RUNNER" --level 1 --mode pure >/dev/null 2>&1
[ $? -ne 0 ] && ok "runner recusa --mode no CLI" || bad "runner aceitou --mode pure"

echo
if [ "$FAILS" -eq 0 ]; then echo "SELFTEST RUNNER: PASS"; exit 0; fi
echo "SELFTEST RUNNER: FAIL ($FAILS)"; exit 1
