# 11 — Fix de ícones, scripts e units (runbook para chroot/live)

Atualizado em **2026-10-27**. Este documento é a instrução operacional para
reconstruir, em outro ambiente Linux live/chroot **igual**, o estado correto dos
ícones, scripts, units e env files deste laboratório.

> Execute como **root no chroot**. O instalador grava arquivos e **não reinicia
> nenhum serviço**. No chroot o `systemd` normalmente não está rodando; as units
> ficam gravadas e o `daemon-reload`/`enable` é feito depois do boot.

## 0. Resultado esperado

| Item | Estado correto |
|---|---|
| OpenCode Web principal | `0.0.0.0:4200`, **HTTP**, Basic auth `opencode` / `guest@123`; unit com `EnvironmentFile=/etc/opencode-web.env` |
| OpenCode Web dev | `127.0.0.1:4210`, senha `Developer.PCSS@LVP006` |
| OpenCode Web moe | `127.0.0.1:4220`, senha `Convidado.PCSS@LVP006` |
| Bridge Web UI | `0.0.0.0:4130`, porta em `/etc/bridge-web.env` |
| Bridges | formulário de **8 campos**; combo `Diagnostico` com default **desligado** |
| Llama servers | formulário de **5 campos**; `Cancelar` não reinicia |
| Cancelar | encerra com `exit 0`; não regenera unit e não reinicia serviço |
| 4200 no boot | unit `opencode-web.service` com a senha vinda de `/etc/opencode-web.env` (modo 600) |
| 4210/4220 | bind **loopback**; expostos só por Caddy/cloudflared |

## 1. Pré-requisitos (falhe antes de escrever)

Rode no chroot como root:

```bash
id mint
id dev-agent
id moe-agent
test -x /usr/local/bin/opencode
test -x /usr/local/bin/llama-server
test -x /usr/local/bin/ocagent-sandbox.sh
test -x /usr/share/dotnet/dotnet
test -f /usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net10.0/Rochas.OpenCodeBridge.dll
test -f /usr/src/opencodebridge/Rochas.OpenCodeBridge.Web/bin/Release/net10.0/Rochas.OpenCodeBridge.Web.dll
command -v zenity jq curl sudo
```

No usuário que abre os ícones (`mint`), o `sudo` precisa ser **sem senha**;
os launchers usam `sudo -n` em vários pontos:

```bash
sudo -u mint sudo -n true && echo 'sudo -n OK'
```

Se algum item faltar, **pare** e corrija a base. O instalador não inventa
usuário, modelo, binário, build ou permissão.

## 2. Instalador único (conteúdo integral)

Salve o bloco abaixo como `/tmp/fix-icons-units.sh`, dê permissão e execute
como root. Ele:

1. faz backup em `/root/opencode-fix-backup-<data>`;
2. grava **14 scripts**, **10 units**, **11 ícones** e **4 env files**;
3. aplica modo/dono (`root:root` ou `mint:mint`);
4. valida `bash -n` de todos os launchers;
5. roda `daemon-reload` **somente** se o systemd estiver ativo;
6. **não reinicia** nada.

O mesmo arquivo está versionado em `scripts/lab/fix-icons-units.sh`; se o repo
estiver disponível no chroot, pode executar direto:

```bash
bash scripts/lab/fix-icons-units.sh
```

Conteúdo do instalador:

````bash
#!/usr/bin/env bash
# Aplica o estado canônico de ícones, scripts e units do OpenCodeBridge.
# Fonte: docs/11-FIX-ICONES-UNITS.md. Rode como root; NÃO reinicia serviços.
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
  echo "ERRO: rode como root (no chroot, root já é o contexto)." >&2
  exit 1
fi

umask 022
BACKUP="/root/opencode-fix-backup-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$BACKUP"
backup_one() {
  local f="$1"
  [ -e "$f" ] || return 0
  cp -a "$f" "$BACKUP/$(basename "$f")"
}

backup_one "/usr/local/bin/svc-common.sh"
backup_one "/usr/local/bin/svc-firewall.sh"
backup_one "/usr/local/bin/start-llama-gpu.sh"
backup_one "/usr/local/bin/start-llama-gpu2.sh"
backup_one "/usr/local/bin/start-llama-cpu-forced.sh"
backup_one "/usr/local/bin/start-bridge-gpu.sh"
backup_one "/usr/local/bin/start-bridge-gpu2.sh"
backup_one "/usr/local/bin/start-bridge-cpu-forced.sh"
backup_one "/usr/local/bin/start-opencodeweb.sh"
backup_one "/usr/local/bin/start-ocagent-web.sh"
backup_one "/usr/local/bin/start-opencode-web-dev.sh"
backup_one "/usr/local/bin/start-opencode-web-moe.sh"
backup_one "/usr/local/bin/start-opencode-app.sh"
backup_one "/usr/local/bin/start-bridge-web.sh"
backup_one "/etc/systemd/system/llama-gpu.service"
backup_one "/etc/systemd/system/llama-gpu2.service"
backup_one "/etc/systemd/system/llama-cpu-forced.service"
backup_one "/etc/systemd/system/bridge-gpu.service"
backup_one "/etc/systemd/system/bridge-gpu2.service"
backup_one "/etc/systemd/system/bridge-cpu-forced.service"
backup_one "/etc/systemd/system/bridge-web.service"
backup_one "/etc/systemd/system/opencode-web-dev.service"
backup_one "/etc/systemd/system/opencode-web-moe.service"
backup_one "/home/mint/Desktop/Llama GPU.desktop"
backup_one "/home/mint/Desktop/Llama GPU2.desktop"
backup_one "/home/mint/Desktop/Llama CPU Forced.desktop"
backup_one "/home/mint/Desktop/OpenCode Bridge GPU.desktop"
backup_one "/home/mint/Desktop/OpenCode Bridge GPU2.desktop"
backup_one "/home/mint/Desktop/OpenCode Bridge CPU Forced.desktop"
backup_one "/home/mint/Desktop/OpenCode.desktop"
backup_one "/home/mint/Desktop/OpenCode Web.desktop"
backup_one "/home/mint/Desktop/OpenCode Web dev-agent (4210).desktop"
backup_one "/home/mint/Desktop/OpenCode Web moe-agent (4220).desktop"
backup_one "/home/mint/Desktop/Bridge Web UI.desktop"
backup_one "/etc/systemd/system/opencode-web.service"
backup_one "/etc/opencode-web.env"
backup_one "/etc/opencode-web-dev.env"
backup_one "/etc/opencode-web-moe.env"
backup_one "/etc/bridge-web.env"

install -d /usr/local/bin /etc/systemd/system /home/mint/Desktop

cat > "/usr/local/bin/svc-common.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# Base nova sem sigla: forms janela unica Porta|Usuario|Senha + PID exato
# Fallback: mint / guest@123. Zenity mantido (4.1.90).
SVC_DEF_USER="mint"
SVC_DEF_PASS="guest@123"
SVC_DIAG="desligado"   # ligado|desligado: default dos icones de bridge
# Firewall helper (UFW): fw_allow / ask_fw_or_cancel / fw_url_port
[ -f /usr/local/bin/svc-firewall.sh ] && source /usr/local/bin/svc-firewall.sh

# Flags de diagnostico para a unit gerada pelos icones de bridge.
# SVC_DIAG=ligado -> metrics (sem corpos req/res); desligado -> nada.
bridge_diag_flags() {
  [ "${SVC_DIAG:-desligado}" = "ligado" ] && printf '%s' '--diagnostic 1 --diagnostic-level metrics'
}

ask_port_user_pass() {
  local DEF_PORT="$1"; local TITLE="$2"
  local OUT
  OUT=$(zenity --forms --title="$TITLE" --text="Configuracao do servico $TITLE" \
    --add-entry="Porta (padrao: $DEF_PORT)" \
    --add-entry="Usuario (padrao: $SVC_DEF_USER)" \
    --add-password="Senha (padrao: $SVC_DEF_PASS)" \
    --width=440 --height=220 2>/dev/null) || return 1
  SVC_PORT=$(echo "$OUT" | cut -d'|' -f1)
  SVC_USER=$(echo "$OUT" | cut -d'|' -f2)
  SVC_PASS=$(echo "$OUT" | cut -d'|' -f3)
  [ -z "$SVC_PORT" ] && SVC_PORT="$DEF_PORT"
  [ -z "$SVC_USER" ] && SVC_USER="$SVC_DEF_USER"
  [ -z "$SVC_PASS" ] && SVC_PASS="$SVC_DEF_PASS"
  id "$SVC_USER" >/dev/null 2>&1 || SVC_USER="$SVC_DEF_USER"
  export SVC_PORT SVC_USER SVC_PASS
  ask_fw_or_cancel "$SVC_PORT" "$TITLE" "tcp" || return 1
  echo "$SVC_PASS" | sudo -S -v 2>/dev/null || sudo -v 2>/dev/null || true
}

ask_port_pass_only() {  local DEF_PORT="$1"; local TITLE="$2"
  local OUT
  OUT=$(zenity --forms --title="$TITLE" --text="Configuracao do $TITLE" \
    --add-entry="Porta (padrao: $DEF_PORT)" \
    --add-password="Senha (padrao: $SVC_DEF_PASS)" \
    --width=440 --height=200 2>/dev/null) || return 1
  SVC_PORT=$(echo "$OUT" | cut -d'|' -f1)
  SVC_PASS=$(echo "$OUT" | cut -d'|' -f2)
  [ -z "$SVC_PORT" ] && SVC_PORT="$DEF_PORT"
  [ -z "$SVC_PASS" ] && SVC_PASS="$SVC_DEF_PASS"
  SVC_USER="$SVC_DEF_USER"
  id "$SVC_USER" >/dev/null 2>&1 || SVC_USER="$(whoami)"
  export SVC_PORT SVC_USER SVC_PASS
  ask_fw_or_cancel "$SVC_PORT" "$TITLE" "tcp" || return 1
}

# ask_llama_config <def_port> <def_ctx> <def_slots> <Titulo>
# -> SVC_PORT SVC_USER SVC_PASS SVC_CTX SVC_SLOTS
# Janela unica do llama: porta + usuario + senha + contexto total (-c) + slots (-np).
# Defaults por linha: E4B Q4 = 1 slot; E4B UD-Q3 = 2 slots (2x56k).
ask_llama_config() {
  local DEF_PORT="$1"; local DEF_CTX="$2"; local DEF_SLOTS="$3"; local TITLE="$4"
  local DEF_PER=$(( DEF_CTX / DEF_SLOTS ))
  local OUT
  OUT=$(zenity --forms --title="$TITLE" \
    --text="ctx $DEF_CTX | $DEF_SLOTS slots x $DEF_PER" \
    --add-entry="Porta ($DEF_PORT)" \
    --add-entry="Usuario ($SVC_DEF_USER)" \
    --add-password="Senha" \
    --add-entry="Ctx -c ($DEF_CTX)" \
    --add-entry="Slots -np ($DEF_SLOTS)" 2>/dev/null) || return 1
  SVC_PORT=$(echo "$OUT" | cut -d'|' -f1)
  SVC_USER=$(echo "$OUT" | cut -d'|' -f2)
  SVC_PASS=$(echo "$OUT" | cut -d'|' -f3)
  SVC_CTX=$(echo "$OUT" | cut -d'|' -f4)
  SVC_SLOTS=$(echo "$OUT" | cut -d'|' -f5)
  [ -z "$SVC_PORT" ] && SVC_PORT="$DEF_PORT"
  [ -z "$SVC_USER" ] && SVC_USER="$SVC_DEF_USER"
  [ -z "$SVC_PASS" ] && SVC_PASS="$SVC_DEF_PASS"
  [ -z "$SVC_CTX" ] && SVC_CTX="$DEF_CTX"
  [ -z "$SVC_SLOTS" ] && SVC_SLOTS="$DEF_SLOTS"
  case "$SVC_CTX" in ''|*[!0-9]*) SVC_CTX="$DEF_CTX" ;; esac
  case "$SVC_SLOTS" in ''|*[!0-9]*) SVC_SLOTS="$DEF_SLOTS" ;; esac
  [ "$SVC_CTX" -ge 1024 ] 2>/dev/null || SVC_CTX="$DEF_CTX"
  [ "$SVC_SLOTS" -ge 1 ] 2>/dev/null || SVC_SLOTS="$DEF_SLOTS"
  [ "$SVC_CTX" -ge "$SVC_SLOTS" ] 2>/dev/null || { SVC_CTX="$DEF_CTX"; SVC_SLOTS="$DEF_SLOTS"; }
  id "$SVC_USER" >/dev/null 2>&1 || SVC_USER="$SVC_DEF_USER"
  export SVC_PORT SVC_USER SVC_PASS SVC_CTX SVC_SLOTS
  ask_fw_or_cancel "$SVC_PORT" "$TITLE" "tcp" || return 1
  echo "$SVC_PASS" | sudo -S -v 2>/dev/null || sudo -v 2>/dev/null || true
}

# check_running <unit|pidfile:xxx> <porta> <Nome> -> 0 se pode continuar, 1 se deve sair
# Se ja roda: pergunta reiniciar. Sim=0 continua, Nao=1 sai.
check_running() {
  local UNIT="$1"; local PORT="$2"; local NAME="$3"
  local RUNINFO=""
  if [[ "$UNIT" == pidfile:* ]]; then
    local PF="${UNIT#pidfile:}"
    if [ -f "$PF" ]; then
      local P
      P=$(cat "$PF" 2>/dev/null)
      if [ -n "$P" ] && kill -0 "$P" 2>/dev/null; then
        RUNINFO="PID $P, porta $PORT"
      fi
    fi
    if [ -z "$RUNINFO" ] && ss -tln 2>/dev/null | grep -q ":$PORT "; then
      RUNINFO="porta $PORT em uso"
    fi
  else
    if systemctl is-active --quiet "$UNIT" 2>/dev/null; then
      local MP
      MP=$(systemctl show "$UNIT" -p MainPID 2>/dev/null | cut -d= -f2)
      RUNINFO="$UNIT ativo (PID $MP, porta $PORT)"
    elif ss -tln 2>/dev/null | grep -q ":$PORT "; then
      RUNINFO="porta $PORT em uso (fora do systemd)"
    fi
  fi
  if [ -n "$RUNINFO" ]; then
    zenity --question --title="$NAME" --text="$NAME ja em execucao ($RUNINFO).\nReiniciar?" --width=400 2>/dev/null || return 1
  fi
  return 0
}

# stop_exact_pid <pidfile> <padrao_cmdline> : mata so se cmdline bate exato
stop_exact_pid() {
  local PF="$1"; local EXPECT="$2"
  [ -f "$PF" ] || return 0
  local P
  P=$(cat "$PF" 2>/dev/null)
  [ -n "$P" ] || { rm -f "$PF"; return 0; }
  if ! kill -0 "$P" 2>/dev/null; then rm -f "$PF"; return 0; fi
  local CMD
  CMD=$(tr '\0' ' ' < "/proc/$P/cmdline" 2>/dev/null)
  case "$CMD" in
    *"$EXPECT"*) ;;
    *) echo "PID $P reciclado (cmdline divergente), nao mato: $CMD"; rm -f "$PF"; return 0 ;;
  esac
  kill "$P" 2>/dev/null || true
  for _ in 1 2 3 4 5; do kill -0 "$P" 2>/dev/null || break; sleep 1; done
  if kill -0 "$P" 2>/dev/null; then
    CMD=$(tr '\0' ' ' < "/proc/$P/cmdline" 2>/dev/null)
    case "$CMD" in *"$EXPECT"*) kill -9 "$P" 2>/dev/null || true;; esac
  fi
  rm -f "$PF"
}

# ask_upstream <url_padrao> <Titulo> -> SVC_UPSTREAM (vazio = padrao)
ask_upstream() {
  local DEF_URL="$1"; local TITLE="$2"
  local OUT
  OUT=$(zenity --entry --title="$TITLE" --text="Servidor upstream" --entry-text="$DEF_URL" --width=480 2>/dev/null) || return 1
  [ -z "$OUT" ] && OUT="$DEF_URL"
  SVC_UPSTREAM="$OUT"
  export SVC_UPSTREAM
}

# ask_bridge_ports <gpu_def> <cpu_def> <Titulo> -> SVC_BRIDGE_GPU / SVC_BRIDGE_CPU
ask_bridge_ports() {
  local DEF_GPU="$1"; local DEF_CPU="$2"; local TITLE="$3"
  local OUT
  OUT=$(zenity --forms --title="$TITLE" --text="Portas das bridges" \
    --add-entry="Bridge GPU vLLM (padrao: $DEF_GPU)" \
    --add-entry="Bridge CPU llama (padrao: $DEF_CPU)" \
    --width=440 --height=200 2>/dev/null) || return 1
  SVC_BRIDGE_GPU=$(echo "$OUT" | cut -d'|' -f1)
  SVC_BRIDGE_CPU=$(echo "$OUT" | cut -d'|' -f2)
  [ -z "$SVC_BRIDGE_GPU" ] && SVC_BRIDGE_GPU="$DEF_GPU"
  [ -z "$SVC_BRIDGE_CPU" ] && SVC_BRIDGE_CPU="$DEF_CPU"
  export SVC_BRIDGE_GPU SVC_BRIDGE_CPU
  # Uma UNICA confirmacao de firewall (as duas portas na mesma janela).
  ask_fw_ports_or_cancel "$TITLE" "${SVC_BRIDGE_GPU}/tcp" "${SVC_BRIDGE_CPU}/tcp" || return 1
}

# ask_bridge_config <def_port> <def_upstream_url> <def_model> <def_ctx> <Titulo>
# -> SVC_PORT SVC_UPSTREAM_URL SVC_UPSTREAM_KEY SVC_MODEL SVC_CTX SVC_USER SVC_PASS SVC_DIAG
# Janela unica: porta bridge + URL upstream (llama) + apikey upstream + nome do
# modelo + tamanho de contexto + usuario/senha da bridge. Usada pelos icones de
# bridge (GPU, CPU e a 3a instancia CPU ngl=0).
ask_bridge_config() {
  local DEF_PORT="$1"; local DEF_UPSTREAM="$2"; local DEF_MODEL="$3"; local DEF_CTX="$4"; local TITLE="$5"
  local OUT
  OUT=$(zenity --forms --title="$TITLE" --text="Configuracao da bridge $TITLE" \
    --add-entry="Porta bridge (padrao: $DEF_PORT)" \
    --add-entry="URL upstream llama (padrao: $DEF_UPSTREAM)" \
    --add-password="API key upstream/bridge (padrao: $SVC_DEF_PASS)" \
    --add-entry="Nome do modelo (padrao: $DEF_MODEL)" \
    --add-entry="Contexto ctx (padrao: $DEF_CTX)" \
    --add-entry="Usuario bridge (padrao: $SVC_DEF_USER)" \
    --add-password="Senha bridge (padrao: $SVC_DEF_PASS)" \
    --add-combo="Diagnostico (padrao: desligado)" \
    --combo-values="desligado|ligado" \
    --width=560 --height=390 2>/dev/null) || return 1
  SVC_PORT=$(echo "$OUT" | cut -d'|' -f1)
  SVC_UPSTREAM_URL=$(echo "$OUT" | cut -d'|' -f2)
  SVC_UPSTREAM_KEY=$(echo "$OUT" | cut -d'|' -f3)
  SVC_MODEL=$(echo "$OUT" | cut -d'|' -f4)
  SVC_CTX=$(echo "$OUT" | cut -d'|' -f5)
  SVC_USER=$(echo "$OUT" | cut -d'|' -f6)
  SVC_PASS=$(echo "$OUT" | cut -d'|' -f7)
  SVC_DIAG=$(echo "$OUT" | cut -d'|' -f8)
  [ -z "$SVC_DIAG" ] && SVC_DIAG="desligado"
  case "$SVC_DIAG" in
    [Ll][Ii][Gg][Aa][Dd][Oo]|1|on|ON|true|True) SVC_DIAG="ligado" ;;
    [Dd][Ee][Ss][Ll][Ii][Gg][Aa][Dd][Oo]|0|off|OFF|false|False) SVC_DIAG="desligado" ;;
    *) SVC_DIAG="desligado" ;;
  esac
  [ -z "$SVC_PORT" ] && SVC_PORT="$DEF_PORT"
  [ -z "$SVC_UPSTREAM_URL" ] && SVC_UPSTREAM_URL="$DEF_UPSTREAM"
  [ -z "$SVC_UPSTREAM_KEY" ] && SVC_UPSTREAM_KEY="$SVC_DEF_PASS"
  [ -z "$SVC_MODEL" ] && SVC_MODEL="$DEF_MODEL"
  [ -z "$SVC_CTX" ] && SVC_CTX="$DEF_CTX"
  [ -z "$SVC_USER" ] && SVC_USER="$SVC_DEF_USER"
  [ -z "$SVC_PASS" ] && SVC_PASS="$SVC_DEF_PASS"
  id "$SVC_USER" >/dev/null 2>&1 || SVC_USER="$SVC_DEF_USER"
  export SVC_PORT SVC_UPSTREAM_URL SVC_UPSTREAM_KEY SVC_MODEL SVC_CTX SVC_USER SVC_PASS SVC_DIAG
  ask_fw_or_cancel "$SVC_PORT" "$TITLE" "tcp" || return 1
  echo "$SVC_PASS" | sudo -S -v 2>/dev/null || sudo -v 2>/dev/null || true
}
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/svc-firewall.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# svc-firewall.sh — helper UFW para os lancadores zenity.
# fw_allow <porta> [proto=tcp] [nome] : abre no ufw (best-effort, nunca falha o servico).
# ask_fw_or_cancel <porta> <Nome> [proto=tcp] : pergunta zenity; Nao/cancelar => return 1 (chamador deve sair).
fw_allow() {
  local PORT="$1" PROTO="${2:-tcp}" NAME="${3:-svc}"
  [[ "$PORT" =~ ^[0-9]+$ ]] || return 0
  [ "$PORT" -ge 1 ] 2>/dev/null && [ "$PORT" -le 65535 ] 2>/dev/null || return 0
  case "$PROTO" in tcp|udp) ;; *) PROTO="tcp" ;; esac
  command -v ufw >/dev/null 2>&1 || return 0
  sudo ufw allow "$PORT/$PROTO" comment "$NAME" >/dev/null 2>&1 || \
    sudo ufw allow "$PORT/$PROTO" >/dev/null 2>&1 || true
  return 0
}
ask_fw_or_cancel() {
  local PORT="$1" NAME="$2" PROTO="${3:-tcp}"
  [[ "$PORT" =~ ^[0-9]+$ ]] || { zenity --error --text="Porta invalida para $NAME: $PORT" 2>/dev/null; return 1; }
  [ "$PORT" -ge 1 ] 2>/dev/null && [ "$PORT" -le 65535 ] 2>/dev/null || { zenity --error --text="Porta fora do alcance para $NAME: $PORT" 2>/dev/null; return 1; }
  zenity --question --title="Firewall (UFW)" --width=460 \
    --text="Abrir porta $PORT/$PROTO no firewall para $NAME?\n\n[Sim] abre e continua.\n[Nao] cancela a execucao." 2>/dev/null || return 1
  fw_allow "$PORT" "$PROTO" "$NAME"
  return 0
}
# fw_url_port <url> : extrai porta numerica de http://host:porta (default 80/443 por esquema)
fw_url_port() {
  local URL="$1" P=""
  P=$(echo "$URL" | sed -nE 's#^https?://[^/:]+:([0-9]+).*#\1#p')
  if [ -z "$P" ]; then
    case "$URL" in https://*) P=443;; *) P=80;; esac
  fi
  echo "$P"
}

# ask_fw_ports_or_cancel <Nome> <porta/proto> [porta/proto...]
# Uma unica confirmacao zenity para varias portas; Nao/cancelar => return 1.
ask_fw_ports_or_cancel() {
  local NAME="$1"; shift
  local SPEC P PROTO LIST=""
  [ "$#" -gt 0 ] || return 1
  for SPEC in "$@"; do
    P="${SPEC%%/*}"; PROTO="${SPEC##*/}"
    [[ "$P" =~ ^[0-9]+$ ]] || { zenity --error --text="Porta invalida para $NAME: $P" 2>/dev/null; return 1; }
    [ "$P" -ge 1 ] 2>/dev/null && [ "$P" -le 65535 ] 2>/dev/null || { zenity --error --text="Porta fora do alcance para $NAME: $P" 2>/dev/null; return 1; }
    case "$PROTO" in tcp|udp) ;; *) PROTO="tcp" ;; esac
    LIST="${LIST}${LIST:+\\n}${P}/${PROTO}"
  done
  zenity --question --title="Firewall (UFW)" --width=500 \
    --text="Abrir no firewall para $NAME?\\n\\n${LIST}\\n\\n[Sim] abre e continua.\\n[Nao] cancela a execucao." 2>/dev/null || return 1
  for SPEC in "$@"; do
    fw_allow "${SPEC%%/*}" "${SPEC##*/}" "$NAME"
  done
  return 0
}
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-llama-gpu.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# Llama TurboQuant GPU (Gemma4 E4B Q4_K_M + mmproj, CUDA+Vulkan, TurboQuant KV): porta padrao 4100 (ex-vLLM)
source /usr/local/bin/svc-common.sh
# GPU presente? Sem driver (/dev/nvidia*) nem adianta subir o 12B com offload.
if ! nvidia-smi -L >/dev/null 2>&1 && [ ! -e /dev/nvidia0 ] && [ ! -e /dev/nvidiactl ]; then
  [ -n "$DISPLAY" ] && zenity --error --text="GPU NVIDIA não detectada (sem driver /dev/nvidia*). Verifique o driver proprietario." 2>/dev/null
  echo "ERRO: GPU NVIDIA não detectada (sem driver /dev/nvidia*)" >&2
  exit 1
fi
DEFAULTS=0
[ "${1:-}" = "--defaults" ] && DEFAULTS=1
[ "$DEFAULTS" -eq 1 ] || check_running "llama-gpu" "4100" "Llama GPU E4B Q4" || exit 0
if [ "$DEFAULTS" -eq 1 ] || [ -z "${DISPLAY:-}" ] || ! command -v zenity >/dev/null 2>&1; then
  SVC_PORT=4100; SVC_USER=mint; SVC_PASS=guest@123; SVC_CTX=131072; SVC_SLOTS=1
  export SVC_PORT SVC_USER SVC_PASS SVC_CTX SVC_SLOTS
  command -v fw_allow >/dev/null 2>&1 && fw_allow "$SVC_PORT" tcp "Llama GPU E4B Q4"
else
  ask_llama_config "4100" "131072" "1" "Llama GPU E4B Q4" || exit 0
fi
MODEL_FILE="${LLAMA_GPU_MODEL:-/opt/models/gemma4-e4b-base/google_gemma-4-E4B-it-Q4_K_M.gguf}"
MMPROJ_FILE="${LLAMA_GPU_MMPROJ:-/opt/models/gemma4-e4b-base/mmproj-gemma-4-E4B-it-Q8_0.gguf}"
echo "$SVC_PASS" | sudo -S bash -c "
  if [ ! -f \"$MODEL_FILE\" ] || [ ! -f \"$MMPROJ_FILE\" ]; then
    /usr/local/bin/zenity --error --text=\"Modelo/mmproj ausente: $MODEL_FILE / $MMPROJ_FILE\" 2>/dev/null
    exit 1
  fi
  # Garante /dev/nvidia* no live (chroot/ISO pode subir sem os nos).
  nvidia-modprobe -u -c=0 -m 2>/dev/null || true
  printf '[Unit]\nDescription=llama-server GPU TurboQuant (Gemma4 E4B Q4_K_M, ctx %s, -np %s)\nAfter=network-online.target\nWants=network-online.target\n\n[Service]\nType=simple\nUser=root\nEnvironment=HOME=/root\nEnvironment=TURBO_AUTO_ASYMMETRIC=0\nExecStart=/usr/local/bin/llama-server --alias Gemma4-E4B-Q4 --host 127.0.0.1 --port %s -m $MODEL_FILE --mmproj $MMPROJ_FILE -c %s -np %s -ngl 99 -fa on --cache-type-k turbo3 --cache-type-v turbo3 --mmproj-offload --no-host --load-mode none --op-offload -ot .*=CUDA0 --prio 2 --prio-batch 2 --repeat-penalty 1.08 --dry-multiplier 0.0 --dry-penalty-last-n 1024 --api-key %s\nRestart=on-failure\nRestartSec=10\nTimeoutStartSec=300\nTimeoutStopSec=60\n\n[Install]\nWantedBy=multi-user.target\n' \"$SVC_CTX\" \"$SVC_SLOTS\" \"$SVC_PORT\" \"$SVC_CTX\" \"$SVC_SLOTS\" \"$SVC_PASS\" > /etc/systemd/system/llama-gpu.service
  systemctl daemon-reload
  systemctl restart llama-gpu
"
# Aguarda modelo carregar (ate 5 min) via /health - 12B Q4 leva minutos.
for i in $(seq 1 60); do
  sleep 5
  curl -sf --max-time 3 "http://127.0.0.1:${SVC_PORT}/health" >/dev/null 2>&1 && break
  [ "$i" -eq 60 ] && zenity --error --text="llama-gpu >5min (journalctl -u llama-gpu)" 2>/dev/null && exit 1
done
zenity --info --text="Llama GPU Q4 :$SVC_PORT | ctx $SVC_CTX | $SVC_SLOTS x $((SVC_CTX/SVC_SLOTS)) | CUDA" 2>/dev/null
exit 0
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-llama-gpu2.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# Llama GPU2 (Gemma4 E4B QAT abliterated i1-IQ3_XXS, ctx 128k, padrao GPU -ngl 99): porta padrao 4110
# Roda 100% GPU (-ngl 99). CPU puro e servico distinto: "Llama CPU Forced" (start-llama-cpu-forced.sh, :4120).
source /usr/local/bin/svc-common.sh
DEFAULTS=0
[ "${1:-}" = "--defaults" ] && DEFAULTS=1
[ "$DEFAULTS" -eq 1 ] || check_running "llama-gpu2" "4110" "Llama GPU2 E4B UD-Q3" || exit 0
if [ "$DEFAULTS" -eq 1 ] || [ -z "${DISPLAY:-}" ] || ! command -v zenity >/dev/null 2>&1; then
  SVC_PORT=4110; SVC_USER=mint; SVC_PASS=guest@123; SVC_CTX=114688; SVC_SLOTS=2
  export SVC_PORT SVC_USER SVC_PASS SVC_CTX SVC_SLOTS
  command -v fw_allow >/dev/null 2>&1 && fw_allow "$SVC_PORT" tcp "Llama GPU2 E4B UD-Q3"
else
  ask_llama_config "4110" "114688" "2" "Llama GPU2 E4B UD-Q3" || exit 0
fi
MODEL_FILE="${LLAMA_CPU_MODEL:-/opt/models/huihui-gemma4-e4b-abliterated/Huihui-gemma-4-E4B-it-qat-q4_0-unquantized-abliterated.i1-IQ3_XXS.gguf}"
MMPROJ_FILE=""
THREADS=$(nproc 2>/dev/null || echo 2)
[ "$THREADS" -lt 2 ] && THREADS=2
echo "$SVC_PASS" | sudo -S bash -c "
  if [ ! -f \"$MODEL_FILE\" ]; then
    /usr/local/bin/zenity --error --text=\"Modelo ausente: $MODEL_FILE\" 2>/dev/null
    exit 1
  fi
  printf '[Unit]\nDescription=llama-server GPU2 (Gemma4 E4B UD-Q3, ctx %s, -np %s, turbo3)\nAfter=network-online.target\nWants=network-online.target\n\n[Service]\nType=simple\nUser=root\nEnvironment=HOME=/root\nEnvironment=TURBO_AUTO_ASYMMETRIC=0\nExecStart=/usr/local/bin/llama-server --alias Gemma4-E4B-UD-Q3 --host 127.0.0.1 --port %s -m $MODEL_FILE -c %s -np %s -ngl 99 -fa on -t $THREADS -tb $THREADS --cache-type-k turbo3 --cache-type-v turbo3 --no-host --load-mode none --op-offload -ot ^per_layer_token_embd.*=CUDA_Host,^token_embd.*=CUDA_Host,^blk.*=CUDA0,^output.*=CUDA0,^output_norm.*=CUDA0,^per_layer_model_proj.*=CUDA0,^per_layer_proj_norm.*=CUDA0 --prio 1 --prio-batch 1 --repeat-penalty 1.08 --dry-multiplier 0.0 --dry-penalty-last-n 1024 --api-key %s\nRestart=on-failure\nRestartSec=10\nTimeoutStartSec=300\nTimeoutStopSec=30\n\n[Install]\nWantedBy=multi-user.target\n' \"$SVC_CTX\" \"$SVC_SLOTS\" \"$SVC_PORT\" \"$SVC_CTX\" \"$SVC_SLOTS\" \"$SVC_PASS\" > /etc/systemd/system/llama-gpu2.service
  systemctl daemon-reload
  systemctl restart llama-gpu2
"
# Aguarda modelo carregar (ate 5 min) via /health
for i in $(seq 1 60); do
  sleep 5
  curl -sf --max-time 3 "http://127.0.0.1:${SVC_PORT}/health" >/dev/null 2>&1 && break
  [ "$i" -eq 60 ] && zenity --error --text="llama-gpu2 >5min (journalctl -u llama-gpu2)" 2>/dev/null && exit 1
done
zenity --info --text="Llama GPU2 UD-Q3 :$SVC_PORT | ctx $SVC_CTX | $SVC_SLOTS x $((SVC_CTX/SVC_SLOTS)) | CUDA" 2>/dev/null
exit 0
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-llama-cpu-forced.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# Llama CPU Forced (Gemma4 E4B Q4 base, force CPU -ngl 0): porta padrao 4120
source /usr/local/bin/svc-common.sh
DEFAULTS=0
[ "${1:-}" = "--defaults" ] && DEFAULTS=1
[ "$DEFAULTS" -eq 1 ] || check_running "llama-cpu-forced" "4120" "Llama CPU Forced E4B" || exit 0
if [ "$DEFAULTS" -eq 1 ] || [ -z "${DISPLAY:-}" ] || ! command -v zenity >/dev/null 2>&1; then
  SVC_PORT=4120; SVC_USER=mint; SVC_PASS=guest@123; SVC_CTX=131072; SVC_SLOTS=1
  export SVC_PORT SVC_USER SVC_PASS SVC_CTX SVC_SLOTS
  command -v fw_allow >/dev/null 2>&1 && fw_allow "$SVC_PORT" tcp "Llama CPU Forced"
else
  ask_llama_config "4120" "131072" "1" "Llama CPU Forced E4B" || exit 0
fi
MODEL_FILE="${LLAMA_CPU_FORCED_MODEL:-/opt/models/gemma4-e4b-base/google_gemma-4-E4B-it-Q4_K_M.gguf}"
THREADS=$(nproc 2>/dev/null || echo 2)
[ "$THREADS" -lt 2 ] && THREADS=2
echo "$SVC_PASS" | sudo -S bash -c "
  if [ ! -f \"$MODEL_FILE\" ]; then
    /usr/local/bin/zenity --error --text=\"Modelo ausente: $MODEL_FILE\" 2>/dev/null
    exit 1
  fi
  printf '[Unit]\nDescription=llama-server CPU Forced (Gemma4 E4B Q4, ctx %s, -np %s, ngl=0)\nAfter=network-online.target\nWants=network-online.target\n\n[Service]\nType=simple\nUser=root\nEnvironment=HOME=/root\nEnvironment=TURBO_AUTO_ASYMMETRIC=0\nExecStart=/usr/local/bin/llama-server --alias Gemma4-E4B-Q4 --host 127.0.0.1 --port %s -m $MODEL_FILE -c %s -np %s -ngl 0 -fa on -t $THREADS -tb $THREADS --cache-type-k turbo3 --cache-type-v turbo3 --prio 2 --repeat-penalty 1.08 --dry-multiplier 0.0 --dry-penalty-last-n 1024 --api-key %s\nRestart=on-failure\nRestartSec=10\nTimeoutStartSec=300\nTimeoutStopSec=30\n\n[Install]\nWantedBy=multi-user.target\n' \"$SVC_CTX\" \"$SVC_SLOTS\" \"$SVC_PORT\" \"$SVC_CTX\" \"$SVC_SLOTS\" \"$SVC_PASS\" > /etc/systemd/system/llama-cpu-forced.service
  systemctl daemon-reload
  systemctl restart llama-cpu-forced
"
for i in $(seq 1 60); do
  sleep 5
  curl -sf --max-time 3 "http://127.0.0.1:${SVC_PORT}/health" >/dev/null 2>&1 && break
  if [ "$i" -eq 60 ]; then
    zenity --error --text="llama-cpu-forced >5min (journalctl -u llama-cpu-forced)" 2>/dev/null
    exit 1
  fi
done
zenity --info --text="Llama CPU Q4 :$SVC_PORT | ctx $SVC_CTX | $SVC_SLOTS x $((SVC_CTX/SVC_SLOTS)) | ngl 0" 2>/dev/null
exit 0
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-bridge-gpu.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# OpenCode Bridge GPU (:4124 -> llama :4100 Gemma4-E4B-Q4): reinicia o unit
source /usr/local/bin/svc-common.sh
DEFAULTS=0
[ "${1:-}" = "--defaults" ] && DEFAULTS=1
if [ "$DEFAULTS" -eq 1 ] || [ -z "${DISPLAY:-}" ] || ! command -v zenity >/dev/null 2>&1; then
  SVC_PORT=4124; SVC_UPSTREAM_URL=http://127.0.0.1:4100; SVC_UPSTREAM_KEY=guest@123; SVC_MODEL=Gemma4-E4B-Q4; SVC_CTX=131072; SVC_USER=mint; SVC_PASS=guest@123; SVC_DIAG=desligado
  export SVC_PORT SVC_UPSTREAM_URL SVC_UPSTREAM_KEY SVC_MODEL SVC_CTX SVC_USER SVC_PASS SVC_DIAG
  command -v fw_allow >/dev/null 2>&1 && fw_allow "$SVC_PORT" tcp "OpenCode Bridge GPU"
else
  ask_bridge_config "4124" "http://127.0.0.1:4100" "Gemma4-E4B-Q4" "131072" "OpenCode Bridge GPU" || exit 0
fi
DIAG_FLAGS=$(bridge_diag_flags)
echo "$SVC_PASS" | sudo -S bash -c "
printf '[Unit]\nDescription=OpenCode Bridge GPU (:4124 -> llama :4100 Gemma4-E4B-Q4)\nAfter=network-online.target llama-gpu.service\nWants=network-online.target llama-gpu.service\n\n[Service]\nType=simple\nUser=root\nEnvironment=HOME=/root\nWorkingDirectory=/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net10.0\nExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll --listen 0.0.0.0 --port %s --upstream %s --model %s --thinking events --temperature 0.2 --max-tokens 16384 --context-guard 1 --context-tokens %s --api-key %s --passthrough-responses true --main-harness 1 --harness-tails 1 --tail-scope all --legacy-harness 0 --anti-repeat 1 --anti-repeat-retry 1 --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 --anti-repeat-window 512 %s --log /tmp/bridge-gpu.log\nRestart=on-failure\nRestartSec=10\n\n[Install]\nWantedBy=multi-user.target\n' \"$SVC_PORT\" \"$SVC_UPSTREAM_URL\" \"$SVC_MODEL\" \"$SVC_CTX\" \"$SVC_UPSTREAM_KEY\" \"$DIAG_FLAGS\" > /etc/systemd/system/bridge-gpu.service
  systemctl daemon-reload
  systemctl restart bridge-gpu
"
sleep 3
STATE=$(systemctl is-active bridge-gpu 2>/dev/null || echo "desconhecido")
HEALTH=$(curl -s --max-time 3 http://127.0.0.1:${SVC_PORT}/api/status 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('model','?') + ' bypass=' + str(d.get('passthrough_responses','?')))" 2>/dev/null || echo "sem resposta")
if [ "$STATE" = "active" ]; then
  zenity --info --text="Bridge GPU ativa [:${SVC_PORT} -> ${SVC_UPSTREAM_URL}]\n$HEALTH" 2>/dev/null
  exit 0
else
  zenity --error --text="Bridge GPU: $STATE\n$HEALTH" 2>/dev/null
  exit 1
fi
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-bridge-gpu2.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# OpenCode Bridge GPU2 (:4125 -> llama :4110 Gemma4-E4B-UD-Q3): reinicia o unit
source /usr/local/bin/svc-common.sh
DEFAULTS=0
[ "${1:-}" = "--defaults" ] && DEFAULTS=1
if [ "$DEFAULTS" -eq 1 ] || [ -z "${DISPLAY:-}" ] || ! command -v zenity >/dev/null 2>&1; then
  SVC_PORT=4125; SVC_UPSTREAM_URL=http://127.0.0.1:4110; SVC_UPSTREAM_KEY=guest@123; SVC_MODEL=Gemma4-E4B-UD-Q3; SVC_CTX=57344; SVC_USER=mint; SVC_PASS=guest@123; SVC_DIAG=desligado
  export SVC_PORT SVC_UPSTREAM_URL SVC_UPSTREAM_KEY SVC_MODEL SVC_CTX SVC_USER SVC_PASS SVC_DIAG
  command -v fw_allow >/dev/null 2>&1 && fw_allow "$SVC_PORT" tcp "OpenCode Bridge GPU2"
else
  ask_bridge_config "4125" "http://127.0.0.1:4110" "Gemma4-E4B-UD-Q3" "57344" "OpenCode Bridge GPU2" || exit 0
fi
DIAG_FLAGS=$(bridge_diag_flags)
echo "$SVC_PASS" | sudo -S bash -c "
printf '[Unit]\nDescription=OpenCode Bridge GPU2 (:4125 -> llama :4110 Gemma4-E4B-UD-Q3)\nAfter=network-online.target llama-gpu2.service\nWants=network-online.target llama-gpu2.service\n\n[Service]\nType=simple\nUser=root\nEnvironment=HOME=/root\nWorkingDirectory=/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net10.0\nExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll --listen 0.0.0.0 --port %s --upstream %s --model %s --thinking events --temperature 0.2 --max-tokens 16384 --context-guard 1 --context-tokens %s --api-key %s --passthrough-responses true --main-harness 1 --harness-tails 1 --tail-scope all --legacy-harness 0 --anti-repeat 1 --anti-repeat-retry 1 --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 --anti-repeat-window 512 %s --log /tmp/bridge-gpu2.log\nRestart=on-failure\nRestartSec=10\n\n[Install]\nWantedBy=multi-user.target\n' \"$SVC_PORT\" \"$SVC_UPSTREAM_URL\" \"$SVC_MODEL\" \"$SVC_CTX\" \"$SVC_UPSTREAM_KEY\" \"$DIAG_FLAGS\" > /etc/systemd/system/bridge-gpu2.service
  systemctl daemon-reload
  systemctl restart bridge-gpu2
"
sleep 3
STATE=$(systemctl is-active bridge-gpu2 2>/dev/null || echo "desconhecido")
HEALTH=$(curl -s --max-time 3 http://127.0.0.1:${SVC_PORT}/api/status 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('model','?') + ' bypass=' + str(d.get('passthrough_responses','?')))" 2>/dev/null || echo "sem resposta")
if [ "$STATE" = "active" ]; then
  zenity --info --text="Bridge GPU2 ativa [:${SVC_PORT} -> ${SVC_UPSTREAM_URL}]\n$HEALTH" 2>/dev/null
  exit 0
else
  zenity --error --text="Bridge GPU2: $STATE\n$HEALTH" 2>/dev/null
  exit 1
fi
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-bridge-cpu-forced.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# OpenCode Bridge CPU Forced (:4126 -> llama :4120 Gemma4-E4B-Q4, ngl=0): reinicia o unit
source /usr/local/bin/svc-common.sh
DEFAULTS=0
[ "${1:-}" = "--defaults" ] && DEFAULTS=1
if [ "$DEFAULTS" -eq 1 ] || [ -z "${DISPLAY:-}" ] || ! command -v zenity >/dev/null 2>&1; then
  SVC_PORT=4126; SVC_UPSTREAM_URL=http://127.0.0.1:4120; SVC_UPSTREAM_KEY=guest@123; SVC_MODEL=Gemma4-E4B-Q4; SVC_CTX=131072; SVC_USER=mint; SVC_PASS=guest@123; SVC_DIAG=desligado
  export SVC_PORT SVC_UPSTREAM_URL SVC_UPSTREAM_KEY SVC_MODEL SVC_CTX SVC_USER SVC_PASS SVC_DIAG
  command -v fw_allow >/dev/null 2>&1 && fw_allow "$SVC_PORT" tcp "OpenCode Bridge CPU Forced"
else
  ask_bridge_config "4126" "http://127.0.0.1:4120" "Gemma4-E4B-Q4" "131072" "OpenCode Bridge CPU Forced" || exit 0
fi
DIAG_FLAGS=$(bridge_diag_flags)
echo "$SVC_PASS" | sudo -S bash -c "
printf '[Unit]\nDescription=OpenCode Bridge CPU Forced (:4126 -> llama :4120 Gemma4-E4B-Q4, ngl=0)\nAfter=network-online.target llama-cpu-forced.service\nWants=network-online.target llama-cpu-forced.service\n\n[Service]\nType=simple\nUser=root\nEnvironment=HOME=/root\nWorkingDirectory=/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net10.0\nExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll --listen 0.0.0.0 --port %s --upstream %s --model %s --thinking events --temperature 0.5 --max-tokens 16384 --context-guard 1 --context-tokens %s --api-key %s --passthrough-responses true --main-harness 0 --harness-tails 1 --tail-scope all --legacy-harness 0 --anti-repeat 1 --anti-repeat-retry 1 --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 --anti-repeat-window 512 %s --log /tmp/bridge-cpu-forced.log\nRestart=on-failure\nRestartSec=10\n\n[Install]\nWantedBy=multi-user.target\n' \"$SVC_PORT\" \"$SVC_UPSTREAM_URL\" \"$SVC_MODEL\" \"$SVC_CTX\" \"$SVC_UPSTREAM_KEY\" \"$DIAG_FLAGS\" > /etc/systemd/system/bridge-cpu-forced.service
  systemctl daemon-reload
  systemctl restart bridge-cpu-forced
"
sleep 3
STATE=$(systemctl is-active bridge-cpu-forced 2>/dev/null || echo "desconhecido")
HEALTH=$(curl -s --max-time 3 http://127.0.0.1:${SVC_PORT}/api/status 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('model','?') + ' bypass=' + str(d.get('passthrough_responses','?')))" 2>/dev/null || echo "sem resposta")
if [ "$STATE" = "active" ]; then
  zenity --info --text="Bridge CPU Forced ativa [:${SVC_PORT} -> ${SVC_UPSTREAM_URL}]\n$HEALTH" 2>/dev/null
  exit 0
else
  zenity --error --text="Bridge CPU Forced: $STATE\n$HEALTH" 2>/dev/null
  exit 1
fi
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-opencodeweb.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# OpenCode Web principal: dialogo zenity (porta bridge, apikey bridge, porta web, senha).
# Defaults: bridge 4124 / apikey guest@123 / web 4200 / senha guest@123 / host 0.0.0.0.
# Overrides por env: OPENCODE_WEB_PORT, OPENCODE_WEB_HOST, OPENCODE_WEB_USER,
# OPENCODE_CONFIG, OPENCODE_WEB_PIDFILE, OPENCODE_WEB_LOG, OPENCODE_WEB_PASSWORD.
# --defaults: sem dialogo; usa os valores ja gravados (ou os defaults acima).
set -u

source /usr/local/bin/svc-common.sh

DEFAULTS=0
[ "${1:-}" = "--defaults" ] && DEFAULTS=1

WEB_HOST="${OPENCODE_WEB_HOST:-0.0.0.0}"
WEB_USER="${OPENCODE_WEB_USER:-mint}"
WEB_HOME=$(getent passwd "$WEB_USER" | cut -d: -f6)
CFG="${OPENCODE_CONFIG:-$WEB_HOME/.config/opencode/opencode.json}"
PIDFILE="${OPENCODE_WEB_PIDFILE:-/run/opencodeweb.pid}"
LOG="${OPENCODE_WEB_LOG:-/tmp/opencodeweb.log}"

id "$WEB_USER" >/dev/null 2>&1 || { echo "usuario $WEB_USER inexistente" >&2; exit 1; }
[ -n "$WEB_HOME" ] || { echo "usuario $WEB_USER sem home" >&2; exit 1; }

ui_error() { zenity --error --title="OpenCode Web" --text="$1" 2>/dev/null || true; }

cfg_bridge_port() {
  local port
  port=$(jq -r '.providers.openai.settings.baseURL // empty' "$CFG" 2>/dev/null \
    | sed -n 's#.*:\([0-9][0-9]*\)/v1.*#\1#p' | head -1)
  [ -n "$port" ] || port="4124"
  printf '%s\n' "$port"
}

cfg_api_key() {
  local key
  key=$(jq -r '.providers.openai.settings.apiKey // empty' "$CFG" 2>/dev/null)
  [ -n "$key" ] || key="$SVC_DEF_PASS"
  printf '%s\n' "$key"
}

DEF_BRIDGE_PORT=$(cfg_bridge_port)
DEF_API_KEY=$(cfg_api_key)
DEF_WEB_PORT="${OPENCODE_WEB_PORT:-4200}"
DEF_WEB_PASS="${OPENCODE_WEB_PASSWORD:-guest@123}"

open_browser() { (sleep 1; xdg-open "http://localhost:$WEB_PORT" >/dev/null 2>&1) & }

# Dialogo zenity (4 campos), todos com valor padrao. Vem antes de qualquer
# atalho de "ja esta rodando": a janela sempre pede os dados.
if [ "$DEFAULTS" -eq 0 ]; then
  OUT=$(zenity --forms --title="OpenCode Web" \
    --text="Configuracao do OpenCode Web (host $WEB_HOST)" \
    --add-entry="Porta bridge (padrao: $DEF_BRIDGE_PORT)" \
    --add-password="API key bridge (padrao: $DEF_API_KEY)" \
    --add-entry="Porta OpenCode Web (padrao: $DEF_WEB_PORT)" \
    --add-password="Senha OpenCode Web (padrao: $DEF_WEB_PASS)" \
    --width=520 --height=260 2>/dev/null) || exit 0
  BRIDGE_PORT=$(echo "$OUT" | cut -d'|' -f1)
  API_KEY=$(echo "$OUT" | cut -d'|' -f2)
  WEB_PORT=$(echo "$OUT" | cut -d'|' -f3)
  WEB_PASS=$(echo "$OUT" | cut -d'|' -f4)
  [ -z "$BRIDGE_PORT" ] && BRIDGE_PORT="$DEF_BRIDGE_PORT"
  [ -z "$API_KEY" ] && API_KEY="$DEF_API_KEY"
  [ -z "$WEB_PORT" ] && WEB_PORT="$DEF_WEB_PORT"
  [ -z "$WEB_PASS" ] && WEB_PASS="$DEF_WEB_PASS"
else
  BRIDGE_PORT="$DEF_BRIDGE_PORT"
  API_KEY="$DEF_API_KEY"
  WEB_PORT="$DEF_WEB_PORT"
  WEB_PASS="$DEF_WEB_PASS"
fi

# Validacao.
case "$BRIDGE_PORT" in ''|*[!0-9]*) ui_error "Porta da bridge invalida: $BRIDGE_PORT"; exit 1 ;; esac
case "$WEB_PORT" in ''|*[!0-9]*) ui_error "Porta do OpenCode Web invalida: $WEB_PORT"; exit 1 ;; esac
[ "$BRIDGE_PORT" -ge 1 ] 2>/dev/null && [ "$BRIDGE_PORT" -le 65535 ] 2>/dev/null || { ui_error "Porta da bridge fora da faixa."; exit 1; }
[ "$WEB_PORT" -ge 1 ] 2>/dev/null && [ "$WEB_PORT" -le 65535 ] 2>/dev/null || { ui_error "Porta do OpenCode Web fora da faixa."; exit 1; }
[ -n "$API_KEY" ] || API_KEY="$SVC_DEF_PASS"
[ -n "$WEB_PASS" ] || WEB_PASS="guest@123"

# Servidor ja responde na mesma porta: o dialogo ja foi feito; apenas abre.
if [ "$WEB_PORT" = "$DEF_WEB_PORT" ] && curl -sf --max-time 2 "http://127.0.0.1:$WEB_PORT/" >/dev/null 2>&1; then
  open_browser
  exit 0
fi

# Unit canonica ativa na mesma porta? Nao briga com o systemd.
if [ "$WEB_PORT" = "$DEF_WEB_PORT" ] && systemctl is-active --quiet opencode-web.service 2>/dev/null; then
  open_browser
  exit 0
fi

# Icone ja rodando: pergunta reiniciar (padrao dos demais icones).
if [ "$DEFAULTS" -eq 0 ]; then
  check_running "pidfile:$PIDFILE" "$WEB_PORT" "OpenCode Web" || exit 0
fi

command -v fw_allow >/dev/null 2>&1 && fw_allow "$WEB_PORT" tcp "OpenCode Web" || true
if [ "$DEFAULTS" -eq 0 ]; then
  ask_fw_or_cancel "$WEB_PORT" "OpenCode Web" "tcp" || exit 0
fi

# Reinicio limpo: mata instancia anterior (pidfile e porta).
stop_exact_pid "$PIDFILE" "opencode serve"
if ss -tln 2>/dev/null | grep -q ":$WEB_PORT "; then
  P=$(pgrep -u "$WEB_USER" -f "opencode serve --port $WEB_PORT" | head -1 || true)
  [ -n "$P" ] && kill "$P" 2>/dev/null || true
  sleep 1
fi
sudo -n rm -f "$PIDFILE" 2>/dev/null || rm -f "$PIDFILE" 2>/dev/null || true

# Atualiza providers no opencode.json (porta/apikey da bridge).
if [ -f "$CFG" ] && command -v jq >/dev/null 2>&1; then
  tmp=$(mktemp "${CFG}.XXXXXX") || { ui_error "Sem permissao para gravar $CFG"; exit 1; }
  if jq --arg bp "$BRIDGE_PORT" --arg key "$API_KEY" '
    .providers.openai.settings.baseURL = ("http://127.0.0.1:" + $bp + "/v1") |
    .providers.openai.settings.apiKey = $key |
    .providers.llama.settings.apiKey = $key |
    .providers.cpu.settings.apiKey = $key
  ' "$CFG" > "$tmp" 2>/dev/null; then
    cp "$tmp" "$CFG" || ui_error "Falha ao gravar $CFG"
  else
    ui_error "Falha ao atualizar $CFG"
  fi
  rm -f "$tmp"
fi

# Sobe como mint, com HOME correto, senha via env.
sudo -n -H -u "$WEB_USER" env \
  OPENCODE_SERVER_PASSWORD="$WEB_PASS" \
  OPENAI_API_KEY="$API_KEY" \
  LLAMA_API_KEY="$API_KEY" \
  /usr/local/bin/opencode serve \
  --port "$WEB_PORT" --hostname "$WEB_HOST" >"$LOG" 2>&1 &
SUDO_PID=$!

OC_PID=""
for _ in $(seq 1 30); do
  OC_PID=$(pgrep -u "$WEB_USER" -f "opencode serve --port $WEB_PORT" | head -1 || true)
  [ -n "$OC_PID" ] && break
  sleep 0.5
done
if [ -z "$OC_PID" ] && ! kill -0 "$SUDO_PID" 2>/dev/null; then
  ui_error "Falha ao subir o OpenCode Web. Veja $LOG."
  exit 1
fi
printf '%s\n' "${OC_PID:-$SUDO_PID}" | sudo -n /usr/bin/tee "$PIDFILE" >/dev/null 2>&1 || true

for _ in $(seq 1 30); do
  curl -sf --max-time 2 "http://127.0.0.1:$WEB_PORT/" >/dev/null 2>&1 && break
  sleep 1
done
open_browser
exit 0
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-ocagent-web.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# OpenCode Web dev-agent/moe-agent: uma janela com a bridge e a instancia.
# Campos: porta/API key da bridge + porta/senha da propria instancia.
# Uso: start-ocagent-web.sh dev|moe [--defaults]
set -u

ROLE="${1:-}"
case "$ROLE" in
  dev)
    UNIT=opencode-web-dev.service
    DEF_PORT=4210
    DEF_PASS='Developer.PCSS@LVP006'
    DEF_BRIDGE=4125
    RUN_USER=dev-agent
    TITLE='OpenCode Web dev-agent (4210)'
    ;;
  moe)
    UNIT=opencode-web-moe.service
    DEF_PORT=4220
    DEF_PASS='Convidado.PCSS@LVP006'
    DEF_BRIDGE=4125
    RUN_USER=moe-agent
    TITLE='OpenCode Web moe-agent (4220)'
    ;;
  *)
    echo "uso: $0 dev|moe [--defaults]" >&2
    exit 2
    ;;
esac

DEFAULTS=0
[ "${2:-}" = "--defaults" ] && DEFAULTS=1

HOME_DIR=$(getent passwd "$RUN_USER" | cut -d: -f6)
CFG="$HOME_DIR/.config/opencode/opencode.json"
ENVFILE="/etc/opencode-web-$ROLE.env"

SAVED_PORT=""
SAVED_PASS=""
if [ -r "$ENVFILE" ]; then
  # shellcheck disable=SC1090
  . "$ENVFILE" 2>/dev/null || true
  SAVED_PORT="${OPENCODE_WEB_PORT:-}"
  SAVED_PASS="${OPENCODE_SERVER_PASSWORD:-}"
fi
DEF_PORT="${SAVED_PORT:-$DEF_PORT}"
DEF_PASS="${SAVED_PASS:-$DEF_PASS}"

DEF_BRIDGE_CFG=$(jq -r '.providers.llama.settings.baseURL // empty' "$CFG" 2>/dev/null \
  | sed -n 's#.*:\([0-9][0-9]*\)/v1.*#\1#p' | head -1)
DEF_BRIDGE_PORT="${DEF_BRIDGE_CFG:-$DEF_BRIDGE}"
DEF_API_KEY=$(jq -r '.providers.llama.settings.apiKey // empty' "$CFG" 2>/dev/null)
[ -n "$DEF_API_KEY" ] || DEF_API_KEY='guest@123'

ui_error() { zenity --error --title="$TITLE" --text="$1" 2>/dev/null || true; }

if [ "$DEFAULTS" -eq 0 ] && [ -n "${DISPLAY:-}" ] && command -v zenity >/dev/null 2>&1; then
  OUT=$(zenity --forms --title="$TITLE" \
    --text="Bridge que este OpenCode Web vai usar e dados da propria instancia (host 127.0.0.1)" \
    --add-entry="Porta bridge (padrao: $DEF_BRIDGE_PORT)" \
    --add-password="API key bridge (padrao: $DEF_API_KEY)" \
    --add-entry="Porta OpenCode Web (padrao: $DEF_PORT)" \
    --add-password="Senha/API key da instancia (padrao: $DEF_PASS)" \
    --width=520 --height=280 2>/dev/null) || exit 0
  BRIDGE_PORT=$(echo "$OUT" | cut -d'|' -f1)
  API_KEY=$(echo "$OUT" | cut -d'|' -f2)
  WEB_PORT=$(echo "$OUT" | cut -d'|' -f3)
  WEB_PASS=$(echo "$OUT" | cut -d'|' -f4)
else
  BRIDGE_PORT="$DEF_BRIDGE_PORT"
  API_KEY="$DEF_API_KEY"
  WEB_PORT="$DEF_PORT"
  WEB_PASS="$DEF_PASS"
fi

[ -z "$BRIDGE_PORT" ] && BRIDGE_PORT="$DEF_BRIDGE_PORT"
[ -z "$API_KEY" ] && API_KEY="$DEF_API_KEY"
[ -z "$WEB_PORT" ] && WEB_PORT="$DEF_PORT"
[ -z "$WEB_PASS" ] && WEB_PASS="$DEF_PASS"

case "$BRIDGE_PORT" in ''|*[!0-9]*) ui_error "Porta da bridge invalida: $BRIDGE_PORT"; exit 1 ;; esac
case "$WEB_PORT" in ''|*[!0-9]*) ui_error "Porta do OpenCode Web invalida: $WEB_PORT"; exit 1 ;; esac
[ "$BRIDGE_PORT" -ge 1 ] 2>/dev/null && [ "$BRIDGE_PORT" -le 65535 ] 2>/dev/null || { ui_error "Porta da bridge fora da faixa."; exit 1; }
[ "$WEB_PORT" -ge 1 ] 2>/dev/null && [ "$WEB_PORT" -le 65535 ] 2>/dev/null || { ui_error "Porta do OpenCode Web fora da faixa."; exit 1; }
[ -n "$API_KEY" ] || API_KEY='guest@123'
[ -n "$WEB_PASS" ] || WEB_PASS="$DEF_PASS"

command -v sudo >/dev/null 2>&1 || { ui_error "sudo nao encontrado."; exit 1; }
sudo -n true 2>/dev/null || { ui_error "sudo sem senha; configure o sudoers para este icone."; exit 1; }
command -v jq >/dev/null 2>&1 || { ui_error "jq nao encontrado."; exit 1; }
[ -f "$CFG" ] || { ui_error "Config ausente: $CFG"; exit 1; }

TMP=$(mktemp) || { ui_error "Sem /tmp gravavel."; exit 1; }
if sudo -n jq --arg bp "$BRIDGE_PORT" --arg key "$API_KEY" '
  .providers.llama.settings.baseURL = ("http://127.0.0.1:" + $bp + "/v1") |
  .providers.llama.settings.apiKey = $key
' "$CFG" > "$TMP" 2>/dev/null; then
  sudo -n install -o "$RUN_USER" -g "$RUN_USER" -m 644 "$TMP" "$CFG" || { rm -f "$TMP"; ui_error "Falha ao gravar $CFG"; exit 1; }
else
  rm -f "$TMP"
  ui_error "Falha ao atualizar $CFG (provider llama)."
  exit 1
fi
rm -f "$TMP"

printf "OPENCODE_WEB_PORT='%s'\nOPENCODE_SERVER_PASSWORD='%s'\n" "$WEB_PORT" "$WEB_PASS" \
  | sudo -n tee "$ENVFILE" >/dev/null || { ui_error "Falha ao gravar $ENVFILE"; exit 1; }
sudo -n chmod 600 "$ENVFILE" 2>/dev/null || true

if ! grep -q "EnvironmentFile=-$ENVFILE" "/etc/systemd/system/$UNIT" 2>/dev/null; then
  sudo -n sed -i "/^Environment=TERM=/a EnvironmentFile=-$ENVFILE" "/etc/systemd/system/$UNIT" 2>/dev/null || true
  sudo -n systemctl daemon-reload 2>/dev/null || true
fi

if ! sudo -n systemctl restart "$UNIT" 2>/dev/null; then
  ui_error "Falha ao reiniciar $UNIT."
  exit 1
fi

for _ in $(seq 1 60); do
  curl -s -o /dev/null -m 1 "http://127.0.0.1:$WEB_PORT/api/info" && break
  sleep 1
done
if curl -s -o /dev/null -m 2 "http://127.0.0.1:$WEB_PORT/api/info"; then
  [ -n "${DISPLAY:-}" ] && xdg-open "http://127.0.0.1:$WEB_PORT" >/dev/null 2>&1 &
  zenity --info --title="$TITLE" --text="OpenCode Web ativo em http://127.0.0.1:$WEB_PORT\nBridge :$BRIDGE_PORT" 2>/dev/null || true
else
  LOG=$(sudo -n journalctl -u "$UNIT" -n 8 --no-pager 2>/dev/null | tail -n 8)
  ui_error "OpenCode Web nao respondeu em :$WEB_PORT.\n$LOG"
  exit 1
fi
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-opencode-web-dev.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
set -u
PORT="${OPENCODE_WEB_PORT:-4210}"
PASS="${OPENCODE_SERVER_PASSWORD:-Developer.PCSS@LVP006}"
export OPENCODE_SERVER_PASSWORD="$PASS"
exec /usr/local/bin/ocagent-sandbox.sh /home/dev-agent /home/dev-agent/workspace \
  /usr/local/bin/opencode serve --port "$PORT" --hostname 127.0.0.1
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-opencode-web-moe.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
set -u
PORT="${OPENCODE_WEB_PORT:-4220}"
PASS="${OPENCODE_SERVER_PASSWORD:-Convidado.PCSS@LVP006}"
export OPENCODE_SERVER_PASSWORD="$PASS"
exec /usr/local/bin/ocagent-sandbox.sh /home/moe-agent /home/moe-agent/workspace \
  /usr/local/bin/opencode serve --port "$PORT" --hostname 127.0.0.1
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-opencode-app.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# OpenCode (TUI): SEM gatilho de bridges no icone do desktop — abre o terminal e
# usa a config assada no opencode.json (GPU1 :4124 / GPU2 :4125). Sobrescreve so por
# env: OPENCODE_API_KEY, OPENCODE_GPU_BASE, OPENCODE_CPU_BASE (legado; GPU2).
source /usr/local/bin/svc-common.sh

API_KEY="${OPENCODE_API_KEY:-$SVC_DEF_PASS}"
GPU_BASE="${OPENCODE_GPU_BASE:-http://127.0.0.1:4124}"
CPU_BASE="${OPENCODE_CPU_BASE:-http://127.0.0.1:4125}"

# Reserva: chaves no ambiente que abre o opencode (o serve ressuscitado nasce sem elas).
ESC_KEY=${API_KEY//\'/\'\\\'\'}
gnome-terminal --working-directory="$HOME" --title="OpenCode (GPU1 $GPU_BASE / GPU2 $CPU_BASE)" \
  -- bash -c "export OPENAI_API_KEY='$ESC_KEY' LLAMA_API_KEY='$ESC_KEY'; opencode; exec bash" 2>/dev/null &
__FIX_ICONES_EOF_7F3A__

cat > "/usr/local/bin/start-bridge-web.sh" <<'__FIX_ICONES_EOF_7F3A__'
#!/bin/bash
# Bridge Web :4130 (Rochas.OpenCodeBridge.Web): UI + nucleo in-process (Core).
# NAO conhece porta de bridge: garante os llama upstream (:4100/:4110) e sobe a Web.
source /usr/local/bin/svc-common.sh
# 0) Porta da propria Web UI (padrao 4130; janela pede, campo pre-preenchido).
DEF_WEB_PORT=4130
if [ -r /etc/bridge-web.env ]; then
  # shellcheck disable=SC1091
  . /etc/bridge-web.env 2>/dev/null || true
  DEF_WEB_PORT="${BRIDGE_WEB_PORT:-4130}"
fi
DEFAULTS=0
[ "${1:-}" = "--defaults" ] && DEFAULTS=1
WEB_PORT="$DEF_WEB_PORT"
if [ "$DEFAULTS" -eq 0 ] && [ -n "${DISPLAY:-}" ] && command -v zenity >/dev/null 2>&1; then
  OUT=$(zenity --entry --title="Bridge Web UI" \
    --text="Porta da Web UI (padrao: $DEF_WEB_PORT)" \
    --entry-text="$DEF_WEB_PORT" --width=420 2>/dev/null) || exit 0
  [ -n "$OUT" ] && WEB_PORT="$OUT"
fi
case "$WEB_PORT" in ''|*[!0-9]*) echo "porta invalida: $WEB_PORT" >&2; exit 1 ;; esac
[ "$WEB_PORT" -ge 1 ] && [ "$WEB_PORT" -le 65535 ] || { echo "porta fora da faixa: $WEB_PORT" >&2; exit 1; }
if [ "$WEB_PORT" != "$DEF_WEB_PORT" ] || [ ! -r /etc/bridge-web.env ]; then
  printf 'BRIDGE_WEB_PORT=%s\n' "$WEB_PORT" | sudo -n tee /etc/bridge-web.env >/dev/null || {
    echo "sem sudo para gravar /etc/bridge-web.env" >&2; exit 1; }
  sudo -n systemctl daemon-reload 2>/dev/null || true
fi
URL="http://127.0.0.1:$WEB_PORT"
has_systemd() { [ -d /run/systemd/system ] && systemctl is-system-running >/dev/null 2>&1; }
llama_ok() { curl -sf --max-time 2 "http://127.0.0.1:$1/health" >/dev/null 2>&1; }

# 1) Upstreams llama (Q4 :4100 / Q3 :4110): sobem se faltarem (root via zenity).
need_llama=0
llama_ok 4100 || need_llama=1
llama_ok 4110 || need_llama=1
if [ "$need_llama" -eq 1 ] && [ "$(id -u)" -ne 0 ]; then
  if [ -n "${DISPLAY:-}" ] && command -v zenity >/dev/null 2>&1; then
    PASS=$(zenity --password --title="Senha do sudo ($(id -un))" 2>/dev/null) || exit 0
    [ -n "$PASS" ] || exit 0
    echo "$PASS" | sudo -S -v 2>/dev/null || { zenity --error --text="Senha do sudo invalida" 2>/dev/null; exit 1; }
  fi
fi
if has_systemd; then
  llama_ok 4100 || sudo -n systemctl start llama-gpu 2>/dev/null || sudo systemctl start llama-gpu 2>/dev/null || true
  llama_ok 4110 || sudo -n systemctl start llama-gpu2 2>/dev/null || sudo systemctl start llama-gpu2 2>/dev/null || true
fi
for _ in $(seq 1 90); do llama_ok 4100 && llama_ok 4110 && break; sleep 2; done
llama_ok 4100 || zenity --warning --text="llama Q4 (:4100) fora do ar - a Web sobe, mas agentes Q4 falham." 2>/dev/null || true
llama_ok 4110 || zenity --warning --text="llama Q3 (:4110) fora do ar - a Web sobe, mas agentes Q3 falham." 2>/dev/null || true

# 2) Web UI (:4130)
if curl -s --max-time 3 "$URL" >/dev/null 2>&1; then
  (xdg-open "$URL" 2>/dev/null || sensible-browser "$URL" 2>/dev/null || true) &
  exit 0
fi
if has_systemd; then
  sudo -n systemctl start bridge-web 2>/dev/null || sudo systemctl start bridge-web 2>/dev/null || systemctl --user start bridge-web 2>/dev/null || true
else
  PF=/run/bridge-web.pid
  ALIVE=0
  if [ -f "$PF" ]; then P=$(cat "$PF" 2>/dev/null); [ -n "$P" ] && kill -0 "$P" 2>/dev/null && ALIVE=1; fi
  if [ "$ALIVE" -eq 0 ] && ! ss -tln 2>/dev/null | grep -q ":$WEB_PORT "; then
    nohup /usr/share/dotnet/dotnet /usr/src/opencodebridge/Rochas.OpenCodeBridge.Web/bin/Release/net10.0/Rochas.OpenCodeBridge.Web.dll --urls "http://0.0.0.0:$WEB_PORT" >/tmp/bridge-web.log 2>&1 &
    echo $! > "$PF" 2>/dev/null || true
  fi
fi
for _ in $(seq 1 30); do curl -s --max-time 2 "$URL" >/dev/null 2>&1 && break; sleep 1; done
if curl -s --max-time 3 "$URL" >/dev/null 2>&1; then
  (xdg-open "$URL" 2>/dev/null || sensible-browser "$URL" 2>/dev/null || true) &
  zenity --info --text="Bridge Web em $URL (llama :4100/:4110)" 2>/dev/null || true
else
  LOG=$(tail -n 5 /tmp/bridge-web.log 2>/dev/null)
  zenity --error --text="Bridge Web fora do ar ($URL).\n$LOG" 2>/dev/null || echo "ERRO: $URL fora do ar" >&2
  exit 1
fi
__FIX_ICONES_EOF_7F3A__

cat > "/etc/systemd/system/llama-gpu.service" <<'__FIX_ICONES_EOF_7F3A__'
[Unit]
Description=llama-server GPU TurboQuant (Gemma4 E4B Q4_K_M, ctx 131072, -np 1)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=root
Environment=HOME=/root
Environment=TURBO_AUTO_ASYMMETRIC=0
ExecStart=/usr/local/bin/llama-server --alias Gemma4-E4B-Q4 --host 127.0.0.1 --port 4100 -m /opt/models/gemma4-e4b-base/google_gemma-4-E4B-it-Q4_K_M.gguf --mmproj /opt/models/gemma4-e4b-base/mmproj-gemma-4-E4B-it-Q8_0.gguf -c 131072 -np 1 -ngl 99 -fa on --cache-type-k turbo3 --cache-type-v turbo3 --mmproj-offload --no-host --load-mode none --op-offload -ot .*=CUDA0 --prio 2 --prio-batch 2 --repeat-penalty 1.08 --dry-multiplier 0.0 --dry-penalty-last-n 1024 --api-key guest@123
Restart=on-failure
RestartSec=10
TimeoutStartSec=300
TimeoutStopSec=60

[Install]
WantedBy=multi-user.target
__FIX_ICONES_EOF_7F3A__

cat > "/etc/systemd/system/llama-gpu2.service" <<'__FIX_ICONES_EOF_7F3A__'
[Unit]
Description=llama-server GPU2 (Gemma4 E4B UD-Q3, ctx 114688, -np 2, turbo3)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=root
Environment=HOME=/root
Environment=TURBO_AUTO_ASYMMETRIC=0
ExecStart=/usr/local/bin/llama-server --alias Gemma4-E4B-UD-Q3 --host 127.0.0.1 --port 4110 -m /opt/models/huihui-gemma4-e4b-abliterated/Huihui-gemma-4-E4B-it-qat-q4_0-unquantized-abliterated.i1-IQ3_XXS.gguf -c 114688 -np 2 -ngl 99 -fa on -t 4 -tb 4 --cache-type-k turbo3 --cache-type-v turbo3 --no-host --load-mode none --op-offload -ot ^per_layer_token_embd.*=CUDA_Host,^token_embd.*=CUDA_Host,^blk.*=CUDA0,^output.*=CUDA0,^output_norm.*=CUDA0,^per_layer_model_proj.*=CUDA0,^per_layer_proj_norm.*=CUDA0 --prio 1 --prio-batch 1 --repeat-penalty 1.08 --dry-multiplier 0.0 --dry-penalty-last-n 1024 --api-key guest@123
Restart=on-failure
RestartSec=10
TimeoutStartSec=300
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
__FIX_ICONES_EOF_7F3A__

cat > "/etc/systemd/system/llama-cpu-forced.service" <<'__FIX_ICONES_EOF_7F3A__'
[Unit]
Description=llama-server CPU Forced (Gemma4 E4B Q4 base, ngl=0, sem mmproj)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=root
Environment=HOME=/root
Environment=TURBO_AUTO_ASYMMETRIC=0
ExecStart=/usr/local/bin/llama-server --alias Gemma4-E4B-Q4 --host 127.0.0.1 --port 4120 -m /opt/models/gemma4-e4b-base/google_gemma-4-E4B-it-Q4_K_M.gguf -c 131072 -np 1 -ngl 0 -fa on -t 4 -tb 4 --cache-type-k turbo3 --cache-type-v turbo3 --prio 2 --repeat-penalty 1.08 --dry-multiplier 0.0 --dry-penalty-last-n 1024 --api-key guest@123
Restart=on-failure
RestartSec=10
TimeoutStartSec=300
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
__FIX_ICONES_EOF_7F3A__

cat > "/etc/systemd/system/bridge-gpu.service" <<'__FIX_ICONES_EOF_7F3A__'
[Unit]
Description=OpenCode Bridge GPU (:4124 -> llama :4100 Gemma4-E4B-Q4)
After=network-online.target llama-gpu.service
Wants=network-online.target llama-gpu.service

[Service]
Type=simple
User=root
Environment=HOME=/root
WorkingDirectory=/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net10.0
ExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll --listen 0.0.0.0 --port 4124 --upstream http://127.0.0.1:4100 --model Gemma4-E4B-Q4 --thinking events --temperature 0.2 --max-tokens 16384 --context-guard 1 --context-tokens 131072 --api-key guest@123 --passthrough-responses true --main-harness 1 --harness-tails 1 --tail-scope all --legacy-harness 0 --anti-repeat 1 --anti-repeat-retry 1 --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 --anti-repeat-window 512 --diagnostic 1 --diagnostic-level metrics --log /tmp/bridge-gpu.log
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
__FIX_ICONES_EOF_7F3A__

cat > "/etc/systemd/system/bridge-gpu2.service" <<'__FIX_ICONES_EOF_7F3A__'
[Unit]
Description=OpenCode Bridge GPU2 (:4125 -> llama :4110 Gemma4-E4B-UD-Q3)
After=network-online.target llama-gpu2.service
Wants=network-online.target llama-gpu2.service

[Service]
Type=simple
User=root
Environment=HOME=/root
WorkingDirectory=/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net10.0
ExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll --listen 0.0.0.0 --port 4125 --upstream http://127.0.0.1:4110 --model Gemma4-E4B-UD-Q3 --thinking events --temperature 0.2 --max-tokens 16384 --context-guard 1 --context-tokens 57344 --api-key guest@123 --passthrough-responses true --main-harness 1 --harness-tails 1 --tail-scope all --legacy-harness 0 --anti-repeat 1 --anti-repeat-retry 1 --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 --anti-repeat-window 512  --log /tmp/bridge-gpu2.log
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
__FIX_ICONES_EOF_7F3A__

cat > "/etc/systemd/system/bridge-cpu-forced.service" <<'__FIX_ICONES_EOF_7F3A__'
[Unit]
Description=OpenCode Bridge CPU Forced (:4126 -> llama :4120 Gemma4-E4B-Q4, ngl=0)
After=network-online.target llama-cpu-forced.service
Wants=network-online.target llama-cpu-forced.service

[Service]
Type=simple
User=root
Environment=HOME=/root
WorkingDirectory=/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net10.0
ExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll --listen 0.0.0.0 --port 4126 --upstream http://127.0.0.1:4120 --model Gemma4-E4B-Q4 --thinking events --temperature 0.5 --max-tokens 16384 --context-guard 1 --context-tokens 131072 --api-key guest@123 --passthrough-responses true --main-harness 0 --harness-tails 1 --tail-scope all --legacy-harness 0 --anti-repeat 1 --anti-repeat-retry 1 --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 --anti-repeat-window 512 --log /tmp/bridge-cpu-forced.log
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
__FIX_ICONES_EOF_7F3A__

cat > "/etc/systemd/system/bridge-web.service" <<'__FIX_ICONES_EOF_7F3A__'
[Unit]
Description=Bridge Web UI (:4130 Rochas.OpenCodeBridge.Web)
After=network-online.target llama-gpu.service llama-gpu2.service
Wants=network-online.target llama-gpu.service llama-gpu2.service

[Service]
Type=simple
User=root
Environment=HOME=/root
Environment=BRIDGE_WEB_PORT=4130
EnvironmentFile=-/etc/bridge-web.env
WorkingDirectory=/usr/src/opencodebridge/Rochas.OpenCodeBridge.Web/bin/Release/net10.0
ExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.Web.dll --urls http://0.0.0.0:${BRIDGE_WEB_PORT}
Restart=on-failure
RestartSec=10
TimeoutStartSec=120
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
__FIX_ICONES_EOF_7F3A__

cat > "/etc/systemd/system/opencode-web-dev.service" <<'__FIX_ICONES_EOF_7F3A__'
[Unit]
Description=OpenCode Web dev-agent (4210, bwrap, workspace-only)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=dev-agent
Group=dev-agent
Environment=HOME=/home/dev-agent
Environment=TERM=xterm-256color
EnvironmentFile=-/etc/opencode-web-dev.env
ExecStart=/usr/local/bin/start-opencode-web-dev.sh
Restart=on-failure
RestartSec=3
NoNewPrivileges=yes

[Install]
WantedBy=multi-user.target
__FIX_ICONES_EOF_7F3A__

cat > "/etc/systemd/system/opencode-web-moe.service" <<'__FIX_ICONES_EOF_7F3A__'
[Unit]
Description=OpenCode Web moe-agent (4220, bwrap, chat-only)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=moe-agent
Group=moe-agent
Environment=HOME=/home/moe-agent
Environment=TERM=xterm-256color
EnvironmentFile=-/etc/opencode-web-moe.env
ExecStart=/usr/local/bin/start-opencode-web-moe.sh
Restart=on-failure
RestartSec=3
NoNewPrivileges=yes

[Install]
WantedBy=multi-user.target
__FIX_ICONES_EOF_7F3A__

cat > "/home/mint/Desktop/Llama GPU.desktop" <<'__FIX_ICONES_EOF_7F3A__'
[Desktop Entry]
Type=Application
Name=Llama GPU E4B Q4
Comment=Iniciar Llama GPU (Gemma4 E4B Q4_K_M + mmproj, ctx 131072, turbo3, -ot .*=CUDA0) na porta 4100
Exec=/usr/local/bin/start-llama-gpu.sh
Icon=/usr/share/pixmaps/svc-gear.svg
Terminal=false
Categories=Development;System;
StartupNotify=true
__FIX_ICONES_EOF_7F3A__

cat > "/home/mint/Desktop/Llama GPU2.desktop" <<'__FIX_ICONES_EOF_7F3A__'
[Desktop Entry]
Version=1.0
Type=Application
Name=Llama GPU2 E4B UD-Q3
Comment=Llama GPU2 (Gemma4 E4B UD-Q3 IQ3_XXS, ctx 131072, -ngl 99, KV turbo3, embd/per-layer no host) na porta 4110
Exec=/usr/local/bin/start-llama-gpu2.sh
Icon=/usr/share/pixmaps/svc-gear.svg
Terminal=false
Categories=Development;System;
StartupNotify=true
__FIX_ICONES_EOF_7F3A__

cat > "/home/mint/Desktop/Llama CPU Forced.desktop" <<'__FIX_ICONES_EOF_7F3A__'
[Desktop Entry]
Version=1.0
Type=Application
Name=Llama CPU Forced E4B
Comment=Bancada CPU pura (Gemma4 E4B Q4 base + mmproj, ctx 131072, -ngl 0) na porta 4120 - nao confundir com o GPU2
Exec=/usr/local/bin/start-llama-cpu-forced.sh
Icon=/usr/share/pixmaps/svc-gear.svg
Terminal=false
Categories=Development;System;
StartupNotify=true
__FIX_ICONES_EOF_7F3A__

cat > "/home/mint/Desktop/OpenCode Bridge GPU.desktop" <<'__FIX_ICONES_EOF_7F3A__'
[Desktop Entry]
Version=1.0
Type=Application
Name=OpenCode Bridge GPU
Comment=Reinicia a bridge C# :4124 -> llama :4100 (Gemma4-E4B-Q4, passthrough Responses, context-guard 81920)
Exec=/usr/local/bin/start-bridge-gpu.sh
Icon=/usr/share/icons/Mint-Y/apps/48/Terminal.png
Terminal=false
Categories=Development;System;
StartupNotify=true
__FIX_ICONES_EOF_7F3A__

cat > "/home/mint/Desktop/OpenCode Bridge GPU2.desktop" <<'__FIX_ICONES_EOF_7F3A__'
[Desktop Entry]
Version=1.0
Type=Application
Name=OpenCode Bridge GPU2
Comment=Reinicia a bridge C# :4125 -> llama :4110 (Gemma4-E4B-UD-Q3, passthrough Responses, anti-repeat, context-guard 131072)
Exec=/usr/local/bin/start-bridge-gpu2.sh
Icon=/usr/share/icons/Mint-Y/apps/48/Terminal.png
Terminal=false
Categories=Development;System;
StartupNotify=true
__FIX_ICONES_EOF_7F3A__

cat > "/home/mint/Desktop/OpenCode Bridge CPU Forced.desktop" <<'__FIX_ICONES_EOF_7F3A__'
[Desktop Entry]
Version=1.0
Type=Application
Name=OpenCode Bridge CPU Forced
Comment=Reinicia a bridge C# :4126 -> llama :4120 (Gemma4-E4B-Q4, ngl=0, anti-repeat, context-guard 131072) - bancada CPU pura
Exec=/usr/local/bin/start-bridge-cpu-forced.sh
Icon=/usr/share/icons/Mint-Y/apps/48/Terminal.png
Terminal=false
Categories=Development;System;
StartupNotify=true
__FIX_ICONES_EOF_7F3A__

cat > "/home/mint/Desktop/OpenCode.desktop" <<'__FIX_ICONES_EOF_7F3A__'
[Desktop Entry]
Version=1.0
Type=Application
Name=OpenCode
Comment=OpenCode TUI (bridge GPU :4124 / CPU :4125, bypass llama)
Exec=/usr/local/bin/start-opencode-app.sh
Icon=/usr/share/icons/Mint-Y/apps/48/Terminal.png
Terminal=false
Categories=Development;System;
StartupNotify=true
__FIX_ICONES_EOF_7F3A__

cat > "/home/mint/Desktop/OpenCode Web.desktop" <<'__FIX_ICONES_EOF_7F3A__'
[Desktop Entry]
Type=Application
Name=OpenCode Web
Comment=Iniciar OpenCode Web (bridge GPU :4124 / CPU :4125, bypass llama)
Exec=/usr/local/bin/start-opencodeweb.sh
Icon=/usr/share/icons/Mint-Y/apps/48/Terminal.png
Terminal=false
Categories=Development;System;
StartupNotify=true
__FIX_ICONES_EOF_7F3A__

cat > "/home/mint/Desktop/OpenCode Web dev-agent (4210).desktop" <<'__FIX_ICONES_EOF_7F3A__'
[Desktop Entry]
Version=1.0
Type=Application
Name=OpenCode Web dev-agent (4210)
Comment=Sobe opencode-web-dev (bwrap, workspace-only, so Q3) e abre http://127.0.0.1:4210
Exec=/usr/local/bin/start-ocagent-web.sh dev
Icon=/usr/share/icons/Mint-Y/apps/48/Terminal.png
Terminal=false
Categories=Development;System;
StartupNotify=true
__FIX_ICONES_EOF_7F3A__

cat > "/home/mint/Desktop/OpenCode Web moe-agent (4220).desktop" <<'__FIX_ICONES_EOF_7F3A__'
[Desktop Entry]
Version=1.0
Type=Application
Name=OpenCode Web moe-agent (4220)
Comment=Sobe opencode-web-moe (bwrap, chat-only, E4B UD Q3 - MoE) e abre http://127.0.0.1:4220
Exec=/usr/local/bin/start-ocagent-web.sh moe
Icon=/usr/share/icons/Mint-Y/apps/48/Terminal.png
Terminal=false
Categories=Development;System;
StartupNotify=true
__FIX_ICONES_EOF_7F3A__

cat > "/home/mint/Desktop/Bridge Web UI.desktop" <<'__FIX_ICONES_EOF_7F3A__'
[Desktop Entry]
Version=1.0
Type=Application
Name=Bridge Web UI
Comment=Interface web da Bridge (http://127.0.0.1:4130) — sobe o servico se parado
Exec=/usr/local/bin/start-bridge-web.sh
Icon=/usr/share/icons/Mint-Y/apps/48/browser.png
Terminal=false
Categories=Development;System;
StartupNotify=true
__FIX_ICONES_EOF_7F3A__

cat > "/etc/systemd/system/opencode-web.service" <<'__FIX_ICONES_EOF_7F3A__'
[Unit]
Description=OpenCode Web (porta 4200, HTTP, Basic auth)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=mint
Group=mint
Environment=HOME=/home/mint
EnvironmentFile=-/etc/opencode-web.env
ExecStart=/usr/local/bin/opencode serve --port 4200 --hostname 0.0.0.0
Restart=on-failure
RestartSec=3

[Install]
WantedBy=multi-user.target
__FIX_ICONES_EOF_7F3A__

cat > "/etc/opencode-web.env" <<'__FIX_ICONES_EOF_7F3A__'
OPENCODE_SERVER_PASSWORD=guest@123
__FIX_ICONES_EOF_7F3A__

cat > "/etc/opencode-web-dev.env" <<'__FIX_ICONES_EOF_7F3A__'
OPENCODE_WEB_PORT=4210
OPENCODE_SERVER_PASSWORD='Developer.PCSS@LVP006'
__FIX_ICONES_EOF_7F3A__

cat > "/etc/opencode-web-moe.env" <<'__FIX_ICONES_EOF_7F3A__'
OPENCODE_WEB_PORT=4220
OPENCODE_SERVER_PASSWORD='Convidado.PCSS@LVP006'
__FIX_ICONES_EOF_7F3A__

cat > "/etc/bridge-web.env" <<'__FIX_ICONES_EOF_7F3A__'
BRIDGE_WEB_PORT=4130
__FIX_ICONES_EOF_7F3A__

# Permissões e donos exatos
chmod 0755 "/usr/local/bin/svc-common.sh"
chown root:root "/usr/local/bin/svc-common.sh"
chmod 0755 "/usr/local/bin/svc-firewall.sh"
chown root:root "/usr/local/bin/svc-firewall.sh"
chmod 0755 "/usr/local/bin/start-llama-gpu.sh"
chown root:root "/usr/local/bin/start-llama-gpu.sh"
chmod 0755 "/usr/local/bin/start-llama-gpu2.sh"
chown root:root "/usr/local/bin/start-llama-gpu2.sh"
chmod 0755 "/usr/local/bin/start-llama-cpu-forced.sh"
chown root:root "/usr/local/bin/start-llama-cpu-forced.sh"
chmod 0755 "/usr/local/bin/start-bridge-gpu.sh"
chown root:root "/usr/local/bin/start-bridge-gpu.sh"
chmod 0755 "/usr/local/bin/start-bridge-gpu2.sh"
chown root:root "/usr/local/bin/start-bridge-gpu2.sh"
chmod 0755 "/usr/local/bin/start-bridge-cpu-forced.sh"
chown root:root "/usr/local/bin/start-bridge-cpu-forced.sh"
chmod 0755 "/usr/local/bin/start-opencodeweb.sh"
chown root:root "/usr/local/bin/start-opencodeweb.sh"
chmod 0755 "/usr/local/bin/start-ocagent-web.sh"
chown root:root "/usr/local/bin/start-ocagent-web.sh"
chmod 0755 "/usr/local/bin/start-opencode-web-dev.sh"
chown root:root "/usr/local/bin/start-opencode-web-dev.sh"
chmod 0755 "/usr/local/bin/start-opencode-web-moe.sh"
chown root:root "/usr/local/bin/start-opencode-web-moe.sh"
chmod 0755 "/usr/local/bin/start-opencode-app.sh"
chown root:root "/usr/local/bin/start-opencode-app.sh"
chmod 0755 "/usr/local/bin/start-bridge-web.sh"
chown root:root "/usr/local/bin/start-bridge-web.sh"
chmod 0644 "/etc/systemd/system/llama-gpu.service"
chown root:root "/etc/systemd/system/llama-gpu.service"
chmod 0644 "/etc/systemd/system/llama-gpu2.service"
chown root:root "/etc/systemd/system/llama-gpu2.service"
chmod 0644 "/etc/systemd/system/llama-cpu-forced.service"
chown root:root "/etc/systemd/system/llama-cpu-forced.service"
chmod 0644 "/etc/systemd/system/bridge-gpu.service"
chown root:root "/etc/systemd/system/bridge-gpu.service"
chmod 0644 "/etc/systemd/system/bridge-gpu2.service"
chown root:root "/etc/systemd/system/bridge-gpu2.service"
chmod 0644 "/etc/systemd/system/bridge-cpu-forced.service"
chown root:root "/etc/systemd/system/bridge-cpu-forced.service"
chmod 0644 "/etc/systemd/system/bridge-web.service"
chown root:root "/etc/systemd/system/bridge-web.service"
chmod 0644 "/etc/systemd/system/opencode-web-dev.service"
chown root:root "/etc/systemd/system/opencode-web-dev.service"
chmod 0644 "/etc/systemd/system/opencode-web-moe.service"
chown root:root "/etc/systemd/system/opencode-web-moe.service"
chmod 0755 "/home/mint/Desktop/Llama GPU.desktop"
chown mint:mint "/home/mint/Desktop/Llama GPU.desktop"
chmod 0755 "/home/mint/Desktop/Llama GPU2.desktop"
chown mint:mint "/home/mint/Desktop/Llama GPU2.desktop"
chmod 0755 "/home/mint/Desktop/Llama CPU Forced.desktop"
chown mint:mint "/home/mint/Desktop/Llama CPU Forced.desktop"
chmod 0755 "/home/mint/Desktop/OpenCode Bridge GPU.desktop"
chown mint:mint "/home/mint/Desktop/OpenCode Bridge GPU.desktop"
chmod 0755 "/home/mint/Desktop/OpenCode Bridge GPU2.desktop"
chown mint:mint "/home/mint/Desktop/OpenCode Bridge GPU2.desktop"
chmod 0755 "/home/mint/Desktop/OpenCode Bridge CPU Forced.desktop"
chown mint:mint "/home/mint/Desktop/OpenCode Bridge CPU Forced.desktop"
chmod 0755 "/home/mint/Desktop/OpenCode.desktop"
chown mint:mint "/home/mint/Desktop/OpenCode.desktop"
chmod 0755 "/home/mint/Desktop/OpenCode Web.desktop"
chown mint:mint "/home/mint/Desktop/OpenCode Web.desktop"
chmod 0755 "/home/mint/Desktop/OpenCode Web dev-agent (4210).desktop"
chown mint:mint "/home/mint/Desktop/OpenCode Web dev-agent (4210).desktop"
chmod 0755 "/home/mint/Desktop/OpenCode Web moe-agent (4220).desktop"
chown mint:mint "/home/mint/Desktop/OpenCode Web moe-agent (4220).desktop"
chmod 0755 "/home/mint/Desktop/Bridge Web UI.desktop"
chown mint:mint "/home/mint/Desktop/Bridge Web UI.desktop"
chmod 0644 "/etc/systemd/system/opencode-web.service"
chown root:root "/etc/systemd/system/opencode-web.service"
chmod 0600 "/etc/opencode-web.env"
chown root:root "/etc/opencode-web.env"
chmod 0600 "/etc/opencode-web-dev.env"
chown root:root "/etc/opencode-web-dev.env"
chmod 0600 "/etc/opencode-web-moe.env"
chown root:root "/etc/opencode-web-moe.env"
chmod 0600 "/etc/bridge-web.env"
chown root:root "/etc/bridge-web.env"

# Sintaxe dos launchers
bash -n "/usr/local/bin/svc-common.sh"
bash -n "/usr/local/bin/svc-firewall.sh"
bash -n "/usr/local/bin/start-llama-gpu.sh"
bash -n "/usr/local/bin/start-llama-gpu2.sh"
bash -n "/usr/local/bin/start-llama-cpu-forced.sh"
bash -n "/usr/local/bin/start-bridge-gpu.sh"
bash -n "/usr/local/bin/start-bridge-gpu2.sh"
bash -n "/usr/local/bin/start-bridge-cpu-forced.sh"
bash -n "/usr/local/bin/start-opencodeweb.sh"
bash -n "/usr/local/bin/start-ocagent-web.sh"
bash -n "/usr/local/bin/start-opencode-web-dev.sh"
bash -n "/usr/local/bin/start-opencode-web-moe.sh"
bash -n "/usr/local/bin/start-opencode-app.sh"
bash -n "/usr/local/bin/start-bridge-web.sh"

if [ -d /run/systemd/system ]; then
  systemctl daemon-reload
  echo "daemon-reload aplicado."
else
  echo "systemd não está rodando (chroot): units gravadas, daemon-reload fica para o boot."
fi

echo "OK: arquivos gravados. Backup: $BACKUP"
echo "Nada foi reiniciado. Siga o pós-boot do documento."
````

## 3. O que ele grava (manifesto)

### 3.1 Scripts (`/usr/local/bin`, modo 0755, root:root)

| Script | Função |
|---|---|
| `svc-common.sh` | defaults, formulários e `bridge_diag_flags`; combo diagnóstico default `desligado` |
| `svc-firewall.sh` | helpers UFW; `ask_fw_*` retorna 1 no cancelar |
| `start-llama-gpu.sh` | llama :4100, form de 5 campos, cancelar sai |
| `start-llama-gpu2.sh` | llama :4110, form de 5 campos, cancelar sai |
| `start-llama-cpu-forced.sh` | llama :4120, form de 5 campos, cancelar sai |
| `start-bridge-gpu.sh` | bridge :4124, form de 8 campos, `--main-harness 1` |
| `start-bridge-gpu2.sh` | bridge :4125, form de 8 campos, `--main-harness 1` |
| `start-bridge-cpu-forced.sh` | bridge :4126, form de 8 campos, `--main-harness 0` (bancada CPU) |
| `start-opencodeweb.sh` | 4200; host `0.0.0.0`; senha default `guest@123`; form sempre |
| `start-ocagent-web.sh` | dev/moe; form de 4 campos; grava env e reinicia a unit |
| `start-opencode-web-dev.sh` | wrapper dev :4210, host `127.0.0.1` |
| `start-opencode-web-moe.sh` | wrapper moe :4220, host `127.0.0.1` |
| `start-opencode-app.sh` | TUI OpenCode no terminal |
| `start-bridge-web.sh` | Web UI :4130; `--entry-text=4130`; grava env |

### 3.2 Units (`/etc/systemd/system`, modo 0644, root:root)

| Unit | Porta | Observação |
|---|---|---|
| `llama-gpu.service` | 4100 | modelo Q4 + mmproj |
| `llama-gpu2.service` | 4110 | Q3 abliterated, 2 slots |
| `llama-cpu-forced.service` | 4120 | `-ngl 0`; não habilitar por padrão |
| `bridge-gpu.service` | 4124 | `--main-harness 1`; diagnóstico `metrics` na unit atual |
| `bridge-gpu2.service` | 4125 | `--main-harness 1`; diagnóstico desligado |
| `bridge-cpu-forced.service` | 4126 | `--main-harness 0`; não habilitar por padrão |
| `bridge-web.service` | 4130 | usa `${BRIDGE_WEB_PORT}` + env |
| `opencode-web.service` | 4200 | **corrigida**: `EnvironmentFile=-/etc/opencode-web.env`, host `0.0.0.0` |
| `opencode-web-dev.service` | 4210 | roda como `dev-agent`; env file |
| `opencode-web-moe.service` | 4220 | roda como `moe-agent`; env file |

### 3.3 Env files (`/etc`, modo 0600, root:root)

| Arquivo | Conteúdo |
|---|---|
| `/etc/opencode-web.env` | `OPENCODE_SERVER_PASSWORD=guest@123` |
| `/etc/opencode-web-dev.env` | porta `4210` + senha `Developer.PCSS@LVP006` |
| `/etc/opencode-web-moe.env` | porta `4220` + senha `Convidado.PCSS@LVP006` |
| `/etc/bridge-web.env` | `BRIDGE_WEB_PORT=4130` |

### 3.4 Ícones (`/home/mint/Desktop`, modo 0755, mint:mint)

`Llama GPU`, `Llama GPU2`, `Llama CPU Forced`, `OpenCode Bridge GPU`,
`OpenCode Bridge GPU2`, `OpenCode Bridge CPU Forced`, `OpenCode`,
`OpenCode Web`, `OpenCode Web dev-agent (4210)`,
`OpenCode Web moe-agent (4220)` e `Bridge Web UI`.

## 4. Pós-boot (fora do chroot)

Depois que o systemd estiver rodando de verdade:

```bash
systemctl daemon-reload

# Upstreams primeiro (llama pode levar minutos no primeiro load)
systemctl enable --now llama-gpu llama-gpu2

# Bridges e Web UI
systemctl enable --now bridge-gpu bridge-gpu2 bridge-web

# OpenCode Web dev/moe (loopback)
systemctl enable --now opencode-web-dev opencode-web-moe

# 4200: escolha UMA forma de operação.
# (a) unit no boot:
systemctl enable --now opencode-web
# (b) ou deixe desabilitada e use o ícone "OpenCode Web":
# systemctl disable --now opencode-web
```

Se o ícone for clicado com a unit `opencode-web` ativa, ele detecta que a porta
já responde e apenas abre o navegador; não sobe um segundo processo.

Não habilitar por padrão:

```bash
# systemctl disable --now llama-cpu-forced bridge-cpu-forced
```

`bridge-cpu-forced` usa `--main-harness 0`; é bancada CPU/OpenCode. Se ele for
usado pela raia WAR/bridge, troque para `--main-harness 1` **no script e na
unit** antes de habilitar.

## 5. Verificação (evidência mínima)

### 5.1 Portas e binds

```bash
ss -tlnH | grep -E ':(4100|4110|4120|4124|4125|4126|4130|4200|4210|4220)\b'
```

Esperado: `4100/4110/4120` em `127.0.0.1`; `4124/4125/4126/4130/4200` em
`0.0.0.0`; `4210/4220` em `127.0.0.1`.

### 5.2 4200: HTTP + Basic auth

```bash
curl -s -o /dev/null -w 'auth=%{http_code}\n' -u 'opencode:guest@123' \
  http://127.0.0.1:4200/api/info
# auth=200

curl -s -o /dev/null -w 'sem_auth=%{http_code}\n' \
  http://127.0.0.1:4200/api/info
# sem_auth=401
```

A raiz `/` é SPA e pode devolver 200; o que precisa estar protegido é a API
(`/api/info` etc.). A senha vem de `/etc/opencode-web.env`; sem esse arquivo a
unit de boot sobe sem Basic auth.

### 5.3 4210/4220: loopback + auth

```bash
ss -tlnH | grep -E ':(4210|4220)\b'
curl -s -o /dev/null -w '%{http_code}\n' -u 'opencode:Developer.PCSS@LVP006' \
  http://127.0.0.1:4210/api/info
curl -s -o /dev/null -w '%{http_code}\n' -u 'opencode:Convidado.PCSS@LVP006' \
  http://127.0.0.1:4220/api/info
```

### 5.4 Units e harness

```bash
for u in llama-gpu llama-gpu2 bridge-gpu bridge-gpu2 bridge-web \
         opencode-web opencode-web-dev opencode-web-moe; do
  printf '%-24s %s\n' "$u" "$(systemctl is-active "$u" 2>/dev/null || true)"
done

curl -s http://127.0.0.1:4125/api/harness | jq .
curl -s -H 'Authorization: Bearer guest@123' \
  http://127.0.0.1:4125/api/status | jq .
```

Em `:4125` espere:

```json
{
  "main_harness": true,
  "main_harness_opencode": false,
  "harness_tails": true,
  "tails_opencode": false
}
```

### 5.5 Formulários e cancelamento

```bash
grep -n -- '--add-combo\|Diagnostico\|SVC_DIAG' /usr/local/bin/svc-common.sh
grep -n -- '--main-harness\|--diagnostic\|--anti-repeat-window' \
  /usr/local/bin/start-bridge-*.sh
```

Teste de cancelar sem abrir janela (stub de `zenity --forms` retornando 1):

```bash
mkdir -p /tmp/zenity-cancel
printf '#!/bin/bash\ncase " $* " in *" --forms "*) exit 1;; *) exit 0;; esac\n' \
  >/tmp/zenity-cancel/zenity
chmod +x /tmp/zenity-cancel/zenity

DISPLAY=:0 PATH=/tmp/zenity-cancel:$PATH /usr/local/bin/start-bridge-gpu2.sh
echo "rc=$?"
```

Esperado: `rc=0`, **sem** `systemctl restart` e sem mensagem de “bridge ativa”.
Repita para `start-opencodeweb.sh`, `start-ocagent-web.sh dev`,
`start-llama-gpu2.sh` e `start-bridge-web.sh`.

## 6. Harness: não confunda flag da unit com lane do cliente

O flag `--main-harness` é global, mas a aplicação é **por cliente**
(`Program.cs`):

```text
opencodeClient = UA opencode ou X-Bridge-Mode: opencode
BridgeLane     = !opencodeClient && (Mode is null || Mode.MainHarness)
mainHarness    = MainHarnessOn && BridgeLane
```

| Cliente | Unit `--main-harness 1` | Efetivo | Tails |
|---|---|---|---|
| OpenCode CLI/Web | sim | **off** | off |
| WAR / `X-Bridge-Mode: bridge` | sim | **on** | on |
| Web UI :4130 (Core in-process) | não usa bridge | **on** | n/a |

Portanto:

- “main harness off para OpenCode” é o **efeito**, não o flag da unit.
- As units `:4124`/`:4125` **devem continuar `--main-harness 1`** se a mesma
  bridge atende WAR/bridge. Com `0`, o OpenCode continua off, mas o preflight
  `bridge_main_harness` da raia WAR falha.
- A Web UI `:4130` não passa pela bridge: `ChatController` chama
  `MainHarness.BuildSystem` no chat e em `instructions` do Responses. Por isso
  ela mantém harness **true** por dentro.
- `:4126` está `0` por ser bancada CPU/OpenCode. Se for usada pela raia WAR,
  mude para `1` no script e na unit.

## 7. Armadilhas que não podem voltar

1. **`ask_* ... || { defaults; }`** — transforma Cancelar em reinício. O correto
   é `|| exit 0`; fallback de defaults só com `--defaults` ou sem
   `DISPLAY`/`zenity`.
2. **`--harness`** — alias removido; use `--main-harness` e `--harness-tails`.
3. **4200 sem `EnvironmentFile`** — a unit sobe sem senha e expõe a API sem
   Basic auth. O `opencode-web.env` precisa existir e ser modo 600.
4. **4200 com host em dois lugares** — `start-opencodeweb.sh` e
   `opencode-web.service`; mudar só um faz o próximo clique/boot reverter.
5. **4210/4220 em `0.0.0.0`** — proibido; são loopback e saem só por
   Caddy/cloudflared.
6. **Diagnóstico default** — o combo abre em `desligado`; para Grafana escolha
   `ligado`, que gera `--diagnostic 1 --diagnostic-level metrics`.
7. **Unit editada à mão** — o próximo clique no ícone regenera; a fonte é o
   script. O runbook grava ambos para o boot.
8. **`/usr/src` é deploy-only** — este runbook grava `/usr/local/bin`, `/etc` e
   `/home/mint/Desktop`; não edita fonte em `/usr/src`.

## 8. Rollback

O instalador guarda os arquivos antigos em
`/root/opencode-fix-backup-<data>`. Para restaurar:

```bash
BACKUP=$(ls -dt /root/opencode-fix-backup-* | head -1)
for f in "$BACKUP"/*; do
  case "$(basename "$f")" in
    *.sh)      install -m 0755 "$f" "/usr/local/bin/$(basename "$f")" ;;
    *.service) install -m 0644 "$f" "/etc/systemd/system/$(basename "$f")" ;;
    *.env)     install -m 0600 "$f" "/etc/$(basename "$f")" ;;
    *.desktop) install -m 0755 "$f" "/home/mint/Desktop/$(basename "$f")" ;;
  esac
done
chown mint:mint /home/mint/Desktop/*.desktop
systemctl daemon-reload 2>/dev/null || true
```

O backup usa `basename`, e os nomes são únicos entre esses 39 arquivos.

## 9. Critério de aceite

- O instalador termina com `OK: arquivos gravados` e `bash -n` de todos os
  scripts.
- `ss` mostra os binds da tabela do §0.
- `:4200` responde **401 sem** e **200 com** `opencode:guest@123` em
  `/api/info`.
- `:4210`/`:4220` só em `127.0.0.1`.
- Cancelar os formulários retorna `rc=0` e não reinicia.
- `:4125/api/harness` mostra `main_harness_opencode=false` e
  `tails_opencode=false`.

Se qualquer item falhar, **não** afrouxe asserts, contexto, `max_tokens` ou
coerção. Corrija o arquivo/unit e repita.
