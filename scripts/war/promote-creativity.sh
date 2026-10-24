#!/bin/bash
# ============================================================================
# Promove a TEMPERATURA dos roles Gemma4 nos configs do opencode (criatividade).
#
# Alvo (proximo de como estavam antes):
#   plan/orch .......... 0.5
#   moe ................ 0.8
#   build/scripter/exec . 0.2
#   modelo base (sem sufixo de role) ..... intacto
#
# Toca TODOS os homes com config do opencode (mint, dev-agent, moe-agent) e
# atualiza tambem o rotulo "— Plan (Thinking On, 0.3)" do campo `name`.
#
# Uso:
#   ./scripts/war/promote-creativity.sh            # dry-run (mostra o diff)
#   ./scripts/war/promote-creativity.sh --apply    # aplica (backup .bak-temps-<stamp>)
#
# Env (opcional): OC_CONFIG_MINT / OC_CONFIG_DEV / OC_CONFIG_MOE
# ============================================================================
set -u
APPLY=0
[ "${1:-}" = "--apply" ] && APPLY=1
STAMP="$(date +%Y%m%d-%H%M%S)"
CONFIGS=(
  "${OC_CONFIG_MINT:-/home/mint/.config/opencode/opencode.json}"
  "${OC_CONFIG_DEV:-/home/dev-agent/.config/opencode/opencode.json}"
  "${OC_CONFIG_MOE:-/home/moe-agent/.config/opencode/opencode.json}"
)
RC=0
for f in "${CONFIGS[@]}"; do
  echo "=== $f"
  if ! sudo test -f "$f"; then echo "  SKIP (ausente)"; continue; fi
  if [ -r "$f" ]; then
    python3 - "$f" "$APPLY" "$STAMP" <<'PY' || RC=1
import json, re, shutil, sys
path, apply_flag, stamp = sys.argv[1], sys.argv[2] == "1", sys.argv[3]
TARGET = {"-plan": 0.5, "-orch": 0.5, "-moe": 0.8, "-build": 0.2, "-scripter": 0.2, "-exec": 0.2}
with open(path, encoding="utf-8") as fh:
    doc = json.load(fh)
changes = []
for pname, prov in (doc.get("providers") or {}).items():
    for mid, model in (prov.get("models") or {}).items():
        if not isinstance(model, dict):
            continue
        for suffix, value in TARGET.items():
            if not mid.endswith(suffix):
                continue
            old = model.get("temperature")
            if old != value:
                changes.append((pname, mid, old, value))
                model["temperature"] = value
                name = model.get("name")
                if isinstance(name, str):
                    model["name"] = re.sub(r"(Thinking (?:On|Off), )\d+(?:\.\d+)?",
                                           r"\g<1>%s" % value, name)
            break
if not changes:
    print("  nada a mudar (já promovido)")
    raise SystemExit(0)
for pname, mid, old, new in changes:
    print(f"  {pname}/{mid:30s} {old} -> {new}")
if not apply_flag:
    print(f"  dry-run: {len(changes)} mudança(s); use --apply")
    raise SystemExit(0)
shutil.copy2(path, f"{path}.bak-temps-{stamp}")
with open(path, "w", encoding="utf-8") as fh:
    json.dump(doc, fh, indent=2, ensure_ascii=False)
    fh.write("\n")
print(f"  aplicado ({len(changes)} mudança(s); backup {path}.bak-temps-{stamp})")
PY
  else
    sudo python3 - "$f" "$APPLY" "$STAMP" <<'PY' || RC=1
import json, re, shutil, sys
path, apply_flag, stamp = sys.argv[1], sys.argv[2] == "1", sys.argv[3]
TARGET = {"-plan": 0.5, "-orch": 0.5, "-moe": 0.8, "-build": 0.2, "-scripter": 0.2, "-exec": 0.2}
with open(path, encoding="utf-8") as fh:
    doc = json.load(fh)
changes = []
for pname, prov in (doc.get("providers") or {}).items():
    for mid, model in (prov.get("models") or {}).items():
        if not isinstance(model, dict):
            continue
        for suffix, value in TARGET.items():
            if not mid.endswith(suffix):
                continue
            old = model.get("temperature")
            if old != value:
                changes.append((pname, mid, old, value))
                model["temperature"] = value
                name = model.get("name")
                if isinstance(name, str):
                    model["name"] = re.sub(r"(Thinking (?:On|Off), )\d+(?:\.\d+)?",
                                           r"\g<1>%s" % value, name)
            break
if not changes:
    print("  nada a mudar (já promovido)")
    raise SystemExit(0)
for pname, mid, old, new in changes:
    print(f"  {pname}/{mid:30s} {old} -> {new}")
if not apply_flag:
    print(f"  dry-run: {len(changes)} mudança(s); use --apply")
    raise SystemExit(0)
shutil.copy2(path, f"{path}.bak-temps-{stamp}")
with open(path, "w", encoding="utf-8") as fh:
    json.dump(doc, fh, indent=2, ensure_ascii=False)
    fh.write("\n")
print(f"  aplicado ({len(changes)} mudança(s); backup {path}.bak-temps-{stamp})")
PY
  fi
done
exit $RC
