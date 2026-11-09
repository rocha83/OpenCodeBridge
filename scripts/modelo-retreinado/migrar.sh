#!/usr/bin/env bash
# migrar.sh — coloca o retrain na GPU (:4124) e normaliza o nome em TODAS as superfícies.
#
# Nome final do modelo (09/11/2026): Gemma4-E4B-GK-Q4  (sem "UCD", sem "ABT").
# O id aparece em 6 lugares:
#   1) launcher do llama-gpu  (MODEL_FILE/MMPROJ_FILE/--alias/Description)
#   2) launcher do bridge-gpu (SVC_MODEL + defaults + Description + comentários)
#   3) ~/.config/opencode/opencode.json (provider `openai`, 6 ids + nomes)
#   4) ~/.config/opencode/agents/*.md (frontmatter `model: openai/...`)
#   5) ~/.config/opencode/AGENTS.md (doc do harness)
#   6) seed dos agentes da Web UI + código com id fixo (AgentSeeder, Gate, BridgeOptions,
#      appsettings, Swagger, Program, AgentsController, TestContext)
# Trocar uma superfície só deixa cliente e servidor com ids diferentes — a bridge reescreve
# `node.model` para o id que ela serve (Program.cs:2898).
#
# O repo e o deploy carregam TRÊS gerações do id: `Gemma4-E4B-Q4` (unit/opencode.json),
# `Gemma4-E4B-GK-Q4` (repo após 08/11) e `Gemma4-E4B-UCD-GK-Q4` (repo atual). Todas são a
# MESMA linha de modelo em épocas diferentes; o script normaliza as três para `Gemma4-E4B-GK-Q4`
# num passe único (sem cascata).
#
# Uso:
#   ./migrar.sh --check              # plano, não escreve nada (default)
#   ./migrar.sh --apply              # artefatos + launchers + config + código, com backup
#   ./migrar.sh --rollback <ts>      # restaura do backup <ts>
#
# NÃO reinicia serviço: reiniciar llama/bridge derruba a sessão que estiver rodando neles.
#
# House rule: identificadores e mensagens em en-US; comentários e dados em pt-BR.
set -uo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
NEW="Gemma4-E4B-GK-Q4"

# Gerações do id (do mais longo para o mais curto). O nome do GGUF entregue
# (`Gemma4-E4B-IT_Principal-GK-DS-Retrained-*`) também vira o nome do modelo.
LEGADOS="Gemma4-E4B-UCD-IT_Principal-GK-DS-Retrained Gemma4-E4B-IT_Principal-GK-DS-Retrained Gemma4-E4B-UCD-GK-Q4 Gemma4-E4B-Q4"

SRC_GGUF="/media/mint/LAB-FT/models/gguf/Gemma4-E4B-IT_Principal-GK-DS-Retrained-Q4_K_M-Imatrix.gguf"
SRC_MMPROJ="/media/mint/LAB-FT/models/gguf/Gemma4-E4B-IT_Principal-GK-DS-Retrained-mmproj-bf16.gguf"
DEST_DIR="/opt/models/gemma4-e4b-gk-q4"
DEST_GGUF="$DEST_DIR/${NEW}-Q4_K_M-Imatrix.gguf"
DEST_MMPROJ="$DEST_DIR/${NEW}-mmproj-bf16.gguf"

OPENCODE_DIR="$HOME/.config/opencode"
OPENCODE_JSON="$OPENCODE_DIR/opencode.json"
AGENTS_DIR="$OPENCODE_DIR/agents"
AGENTS_MD="$OPENCODE_DIR/AGENTS.md"
LLAMA_SH="/usr/local/bin/start-llama-gpu.sh"
BRIDGE_SH="/usr/local/bin/start-bridge-gpu.sh"
MODELS_DEFAULT_SH="/usr/local/bin/start-models-default.sh"

MODE="${1:---check}"
TS="${2:-}"

fail() { printf '[ERRO] %s\n' "$*" >&2; exit 1; }
say()  { printf '[migrar] %s\n' "$*"; }
backup_dir() { echo "$REPO/scripts/modelo-retreinado/backups/$1"; }

ARQUIVOS=(
  "$OPENCODE_JSON"
  "$AGENTS_MD"
  "$AGENTS_DIR/plan.md"
  "$AGENTS_DIR/build.md"
  "$AGENTS_DIR/orch.md"
  "$AGENTS_DIR/moe.md"
  "$AGENTS_DIR/scripter.md"
  "$AGENTS_DIR/exec.md"
  "$LLAMA_SH"
  "$BRIDGE_SH"
  "$MODELS_DEFAULT_SH"
  "$REPO/Rochas.OpenCodeBridge.Web/Data/AgentSeeder.cs"
  "$REPO/Rochas.OpenCodeBridge/Gate.cs"
  "$REPO/Rochas.OpenCodeBridge.Core/BridgeOptions.cs"
  "$REPO/Rochas.OpenCodeBridge/appsettings.4124.gpu-example.json"
  "$REPO/Rochas.OpenCodeBridge/Swagger.cs"
  "$REPO/Rochas.OpenCodeBridge/Program.cs"
  "$REPO/Rochas.OpenCodeBridge.Web/Controllers/AgentsController.cs"
  "$REPO/Rochas.OpenCodeBridge.Test/TestContext.cs"
)

snapshot() {
  local dir; dir=$(backup_dir "$1"); mkdir -p "$dir"
  local f nome
  for f in "${ARQUIVOS[@]}"; do
    [ -f "$f" ] || continue
    nome=$(echo "$f" | sed "s|^/|_|; s|/|__|g")
    cp -a "$f" "$dir/$nome"
  done
  echo "$dir"
}

apply_artifacts() {
  [ -f "$SRC_GGUF" ]   || fail "GGUF ausente: $SRC_GGUF"
  [ -f "$SRC_MMPROJ" ] || fail "mmproj ausente: $SRC_MMPROJ"
  local sha_exp sha_got
  sha_exp=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["sha256"])' \
              "${SRC_GGUF%.gguf}.manifest.json" 2>/dev/null || true)
  sha_got=$(sha256sum "$SRC_GGUF" | cut -d' ' -f1)
  [ -z "$sha_exp" ] || [ "$sha_exp" = "$sha_got" ] || fail "sha256 nao bate com o manifesto"
  say "sha256 ok: ${sha_got:0:16}…"
  if [ "$MODE" = "--apply" ]; then
    sudo mkdir -p "$DEST_DIR" || fail "sudo falhou (mkdir $DEST_DIR)"
    sudo install -m 0644 "$SRC_GGUF"   "$DEST_GGUF"   || fail "copia do GGUF falhou"
    sudo install -m 0644 "$SRC_MMPROJ" "$DEST_MMPROJ" || fail "copia do mmproj falhou"
    printf '%s  %s\n%s  %s\n' \
      "$(sha256sum "$DEST_GGUF" | cut -d' ' -f1)"   "$(basename "$DEST_GGUF")" \
      "$(sha256sum "$DEST_MMPROJ" | cut -d' ' -f1)" "$(basename "$DEST_MMPROJ")" \
      | sudo tee "$DEST_DIR/SHA256SUMS" >/dev/null
    say "artefatos em $DEST_DIR ($(stat -c%s "$DEST_GGUF") B + $(stat -c%s "$DEST_MMPROJ") B)"
  else
    say "copiaria o GGUF e o mmproj para $DEST_DIR como ${NEW}-Q4_K_M-Imatrix.gguf / ${NEW}-mmproj-bf16.gguf"
  fi
}

apply_launchers() {
  local tmp tmp2 tmp3
  tmp=$(mktemp); tmp2=$(mktemp); tmp3=$(mktemp)
  sed -e "s|^MODEL_FILE=.*|MODEL_FILE=\"\${LLAMA_GPU_MODEL:-$DEST_GGUF}\"|" \
      -e "s|^MMPROJ_FILE=.*|MMPROJ_FILE=\"\${LLAMA_GPU_MMPROJ:-$DEST_MMPROJ}\"|" \
      -e "s|--alias [A-Za-z0-9_.-]* |--alias $NEW |" \
      -e "s|(Gemma4 E4B Q4_K_M, ctx|[Gemma4-E4B-GK-Q4 retrain, ctx|" \
      "$LLAMA_SH" > "$tmp"
  grep -q -- "--alias $NEW " "$tmp" || fail "launcher do llama: --alias nao substituido"
  grep -q "$DEST_DIR" "$tmp"         || fail "launcher do llama: MODEL_FILE nao substituido"

  sed -e "s|Gemma4-E4B-Q4|$NEW|g" "$BRIDGE_SH" > "$tmp2"
  grep -q "SVC_MODEL=$NEW;" "$tmp2" || fail "launcher da bridge: SVC_MODEL nao substituido"

  sed -e "s|Gemma4-E4B-Q4|$NEW|g" "$MODELS_DEFAULT_SH" > "$tmp3"

  if [ "$MODE" = "--apply" ]; then
    sudo install -m 0755 "$tmp"  "$LLAMA_SH"         || fail "escrita em $LLAMA_SH"
    sudo install -m 0755 "$tmp2" "$BRIDGE_SH"        || fail "escrita em $BRIDGE_SH"
    sudo install -m 0755 "$tmp3" "$MODELS_DEFAULT_SH" || fail "escrita em $MODELS_DEFAULT_SH"
    say "launchers atualizados (as units são regeneradas por eles)"
  else
    say "atualizaria $LLAMA_SH, $BRIDGE_SH e o texto de $MODELS_DEFAULT_SH"
  fi
  rm -f "$tmp" "$tmp2" "$tmp3"
}

apply_opencode_json() {
  python3 - "$OPENCODE_JSON" "$NEW" "$MODE" $LEGADOS <<'PY'
import json, sys
path, new, mode, *legados = sys.argv[1:]
d = json.load(open(path, encoding="utf-8"))
# SOMENTE o provider `openai` (:4124). `cpu` (:4126) e `llama` (:4125) são outras units/GGUFs
# e mantêm os ids deles.
prov = d.get("providers", {}).get("openai", {})
models = prov.get("models", {})
def legado(k):
    return any(k == x or k.startswith(x + "-") for x in legados)
alvos = [k for k in models if legado(k)]
if not alvos:
    print("[migrar] opencode.json: nada a trocar no provider openai"); raise SystemExit(0)
novo = {}
for k, v in models.items():
    if legado(k):
        sufixo = ""
        for x in sorted(legados, key=len, reverse=True):
            if k.startswith(x):
                sufixo = k[len(x):]; break
        if isinstance(v, dict) and isinstance(v.get("name"), str):
            for velho in ("Gemma4 E4B Q4", "Gemma4 E4B GK-Q4", "Gemma4 E4B UCD-GK-Q4"):
                v["name"] = v["name"].replace(velho, "Gemma4 E4B GK-Q4")
        novo[new + sufixo] = v
    else:
        novo[k] = v
prov["models"] = novo
print(f"[migrar] opencode.json: {len(alvos)} modelo(s) -> {new}*")
if mode == "--apply":
    with open(path, "w", encoding="utf-8") as f:
        json.dump(d, f, indent=2, ensure_ascii=False); f.write("\n")
    print("[migrar] opencode.json gravado")
PY
}

apply_sources() {
  python3 - "$REPO" "$NEW" "$MODE" $LEGADOS <<'PY'
import re, sys
from pathlib import Path
repo, new, mode, *legados = sys.argv[1:]
repo = Path(repo)
# passe único, do padrão mais longo para o mais curto: nada de cascata
pat = re.compile("|".join(re.escape(x) for x in sorted(legados, key=len, reverse=True)))

alvos = [
    "Rochas.OpenCodeBridge.Web/Data/AgentSeeder.cs",
    "Rochas.OpenCodeBridge/Gate.cs",
    "Rochas.OpenCodeBridge.Core/BridgeOptions.cs",
    "Rochas.OpenCodeBridge/appsettings.4124.gpu-example.json",
    "Rochas.OpenCodeBridge/Swagger.cs",
    "Rochas.OpenCodeBridge/Program.cs",
    "Rochas.OpenCodeBridge.Web/Controllers/AgentsController.cs",
    "Rochas.OpenCodeBridge.Test/TestContext.cs",
]
total = 0
for rel in alvos:
    p = repo / rel
    if not p.is_file():
        print(f"[migrar] {rel}: nao existe"); continue
    s = p.read_text(encoding="utf-8")
    s2, n = pat.subn(new, s)
    if not n:
        print(f"[migrar] {rel}: sem ocorrencia"); continue
    # a migração de boot precisa reapontar as linhas :4124 de QUALQUER geração para o id novo
    sql_novo = (
        f"                UPDATE agents SET model = '{new}'\n"
        "                WHERE model IN ('Gemma4-E4B-Q4', 'Gemma4-E4B-GK-Q4',\n"
        "                                'Gemma4-E4B-UCD-GK-Q4', 'Gemma4-E4B-UCD-IT_Principal-GK-DS-Retrained',\n"
        "                                'Gemma4-E4B-IT_Principal-GK-DS-Retrained')\n"
        "                  AND (bridge_url LIKE '%:4124%' OR upstream_url LIKE '%:4100%');"
    )
    s2 = re.sub(
        r'(private static void MigrateGpuModelId\(SqliteConnection conn\)\s*\{.*?cmd\.CommandText = """)(.*?)(""";)',
        lambda m: m.group(1) + "\n" + sql_novo + "\n                " + m.group(3),
        s2, count=1, flags=re.S)
    print(f"[migrar] {rel}: {n} ocorrencia(s){'' if mode == '--apply' else ' (nao gravado)'}")
    if mode == "--apply":
        p.write_text(s2, encoding="utf-8")
    total += n
print(f"[migrar] total no codigo: {total} ocorrencia(s)")
PY
}

apply_opencode_md() {
  python3 - "$AGENTS_MD" "$AGENTS_DIR" "$NEW" "$MODE" $LEGADOS <<'PY'
import re, sys
from pathlib import Path
md, agents_dir, new, mode, *legados = sys.argv[1:]
pat = re.compile("|".join(re.escape(x) for x in sorted(legados, key=len, reverse=True)))
alvos = [Path(md)] + sorted(Path(agents_dir).glob("*.md"))
total = 0
for p in alvos:
    if not p.is_file():
        continue
    s = p.read_text(encoding="utf-8")
    s2, n = pat.subn(new, s)
    if not n:
        continue
    print(f"[migrar] {p.name}: {n} ocorrencia(s){'' if mode == '--apply' else ' (nao gravado)'}")
    if mode == "--apply":
        p.write_text(s2, encoding="utf-8")
    total += n
print(f"[migrar] total no harness do opencode: {total}")
PY
}

rollback() {
  [ -n "$TS" ] || fail "uso: migrar.sh --rollback <ts>"
  local dir; dir=$(backup_dir "$TS")
  [ -d "$dir" ] || fail "backup nao encontrado: $dir"
  local n=0 f nome
  for f in "${ARQUIVOS[@]}"; do
    nome=$(echo "$f" | sed "s|^/|_|; s|/|__|g")
    [ -f "$dir/$nome" ] || continue
    case "$f" in
      /usr/local/bin/*) sudo install -m 0755 "$dir/$nome" "$f" ;;
      *)                install -m 0644 "$dir/$nome" "$f" ;;
    esac
    n=$((n+1))
  done
  say "restaurados $n arquivo(s) de $dir"
  say "falta reiniciar: sudo systemctl restart llama-gpu bridge-gpu"
}

case "$MODE" in
  --check|--apply)
    say "modo $MODE | modelo final: $NEW"
    apply_artifacts
    apply_launchers
    apply_opencode_json
    apply_opencode_md
    apply_sources
    if [ "$MODE" = "--apply" ]; then
      dir=$(snapshot "$(date +%Y%m%d-%H%M%S)")
      say "backup em $dir"
      say "próximo passo (derruba a sessão em curso):"
      say "  sudo systemctl restart llama-gpu && sudo systemctl restart bridge-gpu"
      say "  # Web UI: dotnet publish/build + restart do serviço (o seed migra as linhas :4124 no boot)"
    fi
    ;;
  --rollback) rollback ;;
  *) fail "uso: migrar.sh [--check|--apply|--rollback <ts>]" ;;
esac
