#!/bin/bash
# start-models-default.sh - sobe/verifica os 4 servicos do par principal SEM dialogo,
# usando exatamente os mesmos valores padrao que os icones zenity propoem:
#   llama-gpu   :4100  Gemma4-E4B-GK-Q4     (ctx 131072, mmproj, turbo3, -ot .*=CUDA0)
#   llama-gpu2  :4110  Gemma4-E4B-UD-Q3  (ctx 131072, turbo3, -ot embd/per-layer no host)
#   bridge-gpu  :4124  -> :4100          (passthrough Responses, anti-repeat, context-guard 131072)
#   bridge-gpu2 :4125  -> :4110          (passthrough Responses, anti-repeat, context-guard 131072)
# Bancada CPU pura e outro par, on-demand: llama-cpu-forced (:4120) + bridge-cpu-forced (:4126).
# Uso: start-models-default.sh [--restart] [--quiet]
# --restart  regenera as units e reinicia os 4 (llama recarrega modelo, ~2-4 min)
# sem flag   so sobe o que estiver fora do ar; o resto apenas valida
# Fonte unica de verdade: os proprios lancadores zenity, chamados com --defaults.

set -u
KEY="guest@123"
RESTART=0
QUIET=0
for a in "$@"; do
  case "$a" in
    --restart) RESTART=1 ;;
    --quiet)   QUIET=1 ;;
    *) echo "uso: $0 [--restart] [--quiet]" >&2; exit 2 ;;
  esac
done

# root pelo padrao dos outros lancadores: zenity pede a senha -> sudo -S
if [ "$(id -u)" -ne 0 ]; then
  if [ -z "${DISPLAY:-}" ] || ! command -v zenity >/dev/null 2>&1; then
    echo "ERRO: rode como root (sudo $0 $*)" >&2
    exit 1
  fi
  PASS=$(zenity --password --title="Senha do sudo ($(id -un))" 2>/dev/null) || exit 0
  [ -n "$PASS" ] || exit 0
  echo "$PASS" | sudo -S -v 2>/dev/null || { echo "ERRO: senha do sudo invalida" >&2; exit 1; }
  exec sudo -n "$0" "$@"
fi

SERVICOS="llama-gpu llama-gpu2 bridge-gpu bridge-gpu2"
declare -A PORTA=( [llama-gpu]=4100 [llama-gpu2]=4110 [bridge-gpu]=4124 [bridge-gpu2]=4125 )
declare -A SCRIPT=(
  [llama-gpu]=/usr/local/bin/start-llama-gpu.sh
  [llama-gpu2]=/usr/local/bin/start-llama-gpu2.sh
  [bridge-gpu]=/usr/local/bin/start-bridge-gpu.sh
  [bridge-gpu2]=/usr/local/bin/start-bridge-gpu2.sh
)
log() { [ "$QUIET" -eq 1 ] || echo "$@"; }

saudavel() {
  local u="$1" p="${PORTA[$1]}"
  case "$u" in
    llama-*)  curl -sf --max-time 2 "http://127.0.0.1:$p/health" >/dev/null 2>&1 ;;
    bridge-*) curl -sf --max-time 2 -H "Authorization: Bearer $KEY" "http://127.0.0.1:$p/api/status" >/dev/null 2>&1 ;;
  esac
}

falhou=0
for u in $SERVICOS; do
  if saudavel "$u"; then
    if [ "$RESTART" -eq 1 ]; then
      log "[*] $u :${PORTA[$u]} saudavel -> reiniciando com padroes"
    else
      log "[=] $u :${PORTA[$u]} ja no ar"
      continue
    fi
  else
    log "[*] $u :${PORTA[$u]} fora do ar -> subindo com padroes"
  fi
  # DISPLAY vazio: os lancadores chamados aqui nao abrem zenity (o resumo e nosso)
  if ! DISPLAY= bash "${SCRIPT[$u]}" --defaults; then
    echo "[FAIL] ${SCRIPT[$u]} --defaults retornou erro" >&2
    falhou=1
  fi
done

log ""
log "== status ==============================================="
for u in $SERVICOS; do
  p="${PORTA[$u]}"
  if saudavel "$u"; then
    case "$u" in
      llama-*)  info=$(curl -s --max-time 3 "http://127.0.0.1:$p/v1/models" 2>/dev/null | python3 -c "import json,sys;m=json.load(sys.stdin)['data'][0];print('%s n_ctx=%s' % (m['id'], m['meta']['n_ctx']))" 2>/dev/null) ;;
      bridge-*) info=$(curl -s --max-time 3 -H "Authorization: Bearer $KEY" "http://127.0.0.1:$p/api/status" 2>/dev/null | python3 -c "import json,sys;d=json.load(sys.stdin);g=d.get('config_groups',{});print('%s | %s | anti-repeat=%s' % (d.get('model','?'), ' '.join(g.get('orcamento',[])), 'on' if d.get('anti_repeat') else 'off'))" 2>/dev/null) ;;
    esac
    log "[OK]   $u :$p ${info:+($info)}"
  else
    echo "[FAIL] $u :$p sem resposta - journalctl -u $u" >&2
    falhou=1
  fi
done
log "========================================================="
log "baseURL Q4   -> http://127.0.0.1:4124/v1  (model Gemma4-E4B-GK-Q4, ctx 131072)"
log "baseURL Q3   -> http://127.0.0.1:4125/v1  (model Gemma4-E4B-UD-Q3, ctx 131072)"
log "apiKey       -> $KEY"
log "CPU puro     -> :4120/:4126 (on-demand, icones '... CPU Forced')"

if [ "$falhou" -ne 0 ]; then
  [ -n "${DISPLAY:-}" ] && command -v zenity >/dev/null 2>&1 && \
    zenity --error --text="Um ou mais servicos locais falharam. Veja o terminal / journalctl." 2>/dev/null
  exit 1
fi
[ -n "${DISPLAY:-}" ] && [ "$QUIET" -eq 0 ] && command -v zenity >/dev/null 2>&1 && \
  zenity --info --text="Llama GPU (:4100) + GPU2 (:4110) e bridges (:4124/:4125) prontos." 2>/dev/null
exit 0
