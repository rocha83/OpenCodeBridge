#!/usr/bin/env bash
# verificar.sh — checa, SEM escrever nada, se as superfícies do nome do modelo batem.
#
# Modelo final: Gemma4-E4B-GK-Q4 (GPU :4124). Providers `cpu` (:4126) e `llama` (:4125)
# usam outros GGUFs e continuam com os ids deles (`Gemma4-E4B-Q4` e `Gemma4-E4B-UD-Q3`).
#
# Uso: ./verificar.sh            # sai 0 quando tudo está coerente
set -uo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
NEW="Gemma4-E4B-GK-Q4"
LEGADOS_RE="Gemma4-E4B-UCD-GK-Q4|Gemma4-E4B-UCD-IT_Principal|Gemma4-E4B-IT_Principal"
DEST_DIR="/opt/models/gemma4-e4b-gk-q4"
OPENCODE_DIR="$HOME/.config/opencode"
OPENCODE_JSON="$OPENCODE_DIR/opencode.json"
AGENTS_DIR="$OPENCODE_DIR/agents"
AGENTS_MD="$OPENCODE_DIR/AGENTS.md"
LLAMA_SH="/usr/local/bin/start-llama-gpu.sh"
BRIDGE_SH="/usr/local/bin/start-bridge-gpu.sh"
falhas=0

ok()  { printf '  [ok]    %s\n' "$*"; }
bad() { printf '  [FALHA] %s\n' "$*"; falhas=$((falhas+1)); }

echo "[verificar] modelo final: $NEW"

# 1) artefatos em /opt (o llama não pode depender de /media montado na mão)
for f in "$DEST_DIR/${NEW}-Q4_K_M-Imatrix.gguf" "$DEST_DIR/${NEW}-mmproj-bf16.gguf"; do
  [ -f "$f" ] && ok "artefato $(basename "$f") ($(stat -c%s "$f") B)" || bad "ausente: $f"
done
if [ -f "$DEST_DIR/SHA256SUMS" ] && command -v sha256sum >/dev/null; then
  ( cd "$DEST_DIR" && sha256sum -c --status SHA256SUMS ) \
    && ok "sha256 dos artefatos confere" || bad "sha256 dos artefatos não confere"
fi

# 2) launcher do llama: arquivo, alias e descrição
grep -q -- "--alias $NEW " "$LLAMA_SH" 2>/dev/null && ok "launcher llama: --alias $NEW" \
  || bad "launcher llama sem --alias $NEW"
grep -q "$DEST_DIR" "$LLAMA_SH" 2>/dev/null && ok "launcher llama aponta p/ $DEST_DIR" \
  || bad "launcher llama não aponta p/ $DEST_DIR"

# 3) launcher da bridge
grep -q "SVC_MODEL=$NEW;" "$BRIDGE_SH" 2>/dev/null && ok "launcher bridge: SVC_MODEL=$NEW" \
  || bad "launcher bridge sem SVC_MODEL=$NEW"

# 4) unit em vigor (só faz sentido depois do restart)
UNIT=$(sudo -n systemctl cat llama-gpu.service 2>/dev/null | grep -o -- "--alias [^ ]*" | head -1)
if [ "$UNIT" = "--alias $NEW" ]; then ok "unit ativa já serve $NEW"
elif [ -n "$UNIT" ]; then bad "unit ativa ainda serve ${UNIT#--alias } (falta restart)"
else bad "unit llama-gpu não legível (rode: sudo systemctl cat llama-gpu)"
fi

# 5) config do opencode: provider openai (:4124)
python3 - "$OPENCODE_JSON" "$NEW" <<'PY'
import json, sys
path, new = sys.argv[1:3]
try:
    d = json.load(open(path, encoding="utf-8"))
except Exception as e:
    print(f"  [FALHA] opencode.json ilegível: {e}"); raise SystemExit(1)
provs = d.get("providers", {})
ms = provs.get("openai", {}).get("models", {})
novos = [k for k in ms if k == new or k.startswith(new + "-")]
velhos = [k for k in ms if "UCD" in k or (k.startswith("Gemma4-E4B-Q4"))]
if velhos:
    print(f"  [FALHA] openai ainda tem {len(velhos)} id(s) antigo(s): {velhos[:3]}")
    raise SystemExit(1)
if len(novos) < 6:
    print(f"  [FALHA] openai: {len(novos)} id(s) {new}* (esperado 6)")
    raise SystemExit(1)
print(f"  [ok]    opencode.json: {len(novos)} id(s) {new}*")
saveis = {"cpu", "llama"}
for pid in saveis:
    velhos = [k for k in provs.get(pid, {}).get("models", {}) if "UCD" in k]
    if velhos:
        print(f"  [FALHA] provider {pid}: id com UCD: {velhos[:2]}")
        raise SystemExit(1)
print("  [ok]    providers cpu/llama intactos")
PY
[ $? -eq 0 ] || falhas=$((falhas+1))

# 6) agentes do opencode (frontmatter model:)
n=$(grep -l "model: openai/$NEW" "$AGENTS_DIR"/*.md 2>/dev/null | wc -l)
[ "$n" -ge 5 ] && ok "agents/*.md: $n papéis no id novo" || bad "agents/*.md: $n com modelo novo (esperado 5)"
r=$(grep -c "$LEGADOS_RE" "$AGENTS_DIR"/*.md "$AGENTS_MD" 2>/dev/null | awk -F: '{s+=$2} END{print s+0}')
[ "$r" -eq 0 ] && ok "harness (agents/*.md + AGENTS.md) sem id legado" || bad "harness com $r ocorrência(s) legada(s)"

# 7) seed dos agentes da Web UI
seeder="$REPO/Rochas.OpenCodeBridge.Web/Data/AgentSeeder.cs"
n=$(grep -o "\"$NEW\"" "$seeder" 2>/dev/null | wc -l)
[ "$n" -ge 6 ] && ok "AgentSeeder: $n agente(s) no id novo" || bad "AgentSeeder com $n ocorrência(s) (esperado >= 6)"
r=$(grep -cE "$LEGADOS_RE" "$seeder" 2>/dev/null || true)
[ "${r:-0}" -le 5 ] && ok "AgentSeeder: só as referências históricas do SQL de migração" \
  || bad "AgentSeeder ainda tem ${r} ocorrência(s) legada(s)"

# 8) linhas no banco da Web (se a tabela existir)
for DB in "$REPO/Rochas.OpenCodeBridge/bridge.db" "/usr/src/opencodebridge/Rochas.OpenCodeBridge/bridge.db"; do
  [ -f "$DB" ] || continue
  python3 - "$DB" "$NEW" <<'PY'
import sqlite3, sys
db, new = sys.argv[1:3]
try:
    c = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    tabs = [r[0] for r in c.execute("select name from sqlite_master where type='table'")]
    if "agents" not in tabs:
        print(f"  [warn]  {db}: sem tabela agents (a Web cria o schema no boot)"); raise SystemExit(0)
    v = c.execute("select count(*) from agents where model like '%UCD%'").fetchone()[0]
    n = c.execute("select count(*) from agents where model = ?", (new,)).fetchone()[0]
    print(f"  [{'FALHA' if v else 'ok'}] {db}: {n} no id novo, {v} com UCD")
    raise SystemExit(1 if v else 0)
except sqlite3.Error as e:
    print(f"  [warn]  {db}: sqlite {e}")
PY
  [ $? -eq 0 ] || falhas=$((falhas+1))
done

echo
[ "$falhas" -eq 0 ] && { echo "[verificar] COERENTE"; exit 0; }
echo "[verificar] $falhas falha(s)"; exit 1
