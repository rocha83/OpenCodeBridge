#!/usr/bin/env bash
# Sobe (ou restaura) a stack de observacao do tunel opencode -> llama.
#
# Por que existe: o lab roda em LIVE CD — `/` e overlay em tmpfs e tudo que foi
# instalado por apt/servico desaparece no reboot. O que fica em
# /media/mint/LAB-FT/toolchain (ext4) sobrevive. Este script reconstroi o que
# mora no overlay a partir do que esta persistido:
#
#   LAB-FT/toolchain/prometheus/bin/{prometheus,promtool}      binarios
#   LAB-FT/toolchain/prometheus/etc/prometheus/*.yml          config de scrape
#   LAB-FT/toolchain/prometheus/etc/prometheus.service        unit systemd
#   LAB-FT/toolchain/archives/prometheus-3.1.0*.tar.gz        fallback do binario
#
# Grafana NAO e instalado aqui (ja existe na imagem/host): o script so provisiona
# datasource + dashboard e FIXA o dashboard (estrela + home).
#
# Uso:
#   ./stack-up.sh              sobe/restaura tudo (idempotente)
#   ./stack-up.sh --check      so relata o estado, sem alterar nada
#   ./stack-up.sh --with-llama sobe tambem o llama-server :4100 se estiver fora
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
LAB="${LAB:-/media/mint/LAB-FT/toolchain}"
PROM_BIN="$LAB/prometheus/bin"
PROM_ETC="$LAB/prometheus/etc"
PROM_TGZ="$LAB/archives/prometheus-3.1.0.linux-amd64.tar.gz"
EVENTS="/tmp/opencode/llama-pure/metrics/events.jsonl"
GRAFANA="http://127.0.0.1:4800"
GUSER="admin:guest@123"
DASH="oc-llama-pure"

CHECK=0; WITH_LLAMA=0
for a in "$@"; do
  case "$a" in
    --check) CHECK=1 ;;
    --with-llama) WITH_LLAMA=1 ;;
    *) echo "argumento invalido: $a" >&2; exit 2 ;;
  esac
done

falhas=0
say() { printf '%-34s %s\n' "$1" "$2"; }
ok()  { say "$1" "OK   $2"; }
bad() { say "$1" "FALTA $2"; falhas=$((falhas+1)); }
have() { command -v "$1" >/dev/null 2>&1; }
sudo_() { sudo -n "$@"; }

http() { curl -s -m "${2:-3}" -o /dev/null -w "%{http_code}" "$1" 2>/dev/null; }

echo "== stack do tunel opencode -> llama =="
echo "   persistido em: $LAB/prometheus"
echo

# ---------------------------------------------------------------- 0. pre-requisitos
if [ "$CHECK" -eq 0 ] && ! sudo -n true 2>/dev/null; then
  echo "sudo -n indisponivel: rode como root ou libere sudo sem senha" >&2
  exit 2
fi

# ---------------------------------------------------------------- 1. llama (modelo GK da FT)
MODEL_OK="Gemma4-E4B-GK-Q4"
served() {
  curl -s -m 3 -H "Authorization: Bearer guest@123" http://127.0.0.1:4100/v1/models 2>/dev/null \
  | python3 -c 'import json,sys
try: print(json.load(sys.stdin)["data"][0]["id"])
except Exception: print("")' 2>/dev/null
}
modelo="$(served)"
if [ "$WITH_LLAMA" -eq 1 ]; then
  # unit do modelo TREINADO (FT/models), persistida no LAB-FT: instala se faltar
  if [ -f "$LAB/llama/llama-gk.service" ] && ! systemctl cat llama-gk >/dev/null 2>&1; then
    if [ "$CHECK" -eq 1 ]; then
      bad "unit llama-gk" "ausente (persistida em $LAB/llama/llama-gk.service)"
    else
      sudo_ install -m 644 "$LAB/llama/llama-gk.service" /etc/systemd/system/llama-gk.service
      sudo_ systemctl daemon-reload
      ok "unit llama-gk" "instalada do LAB-FT"
    fi
  fi
  if [ "$modelo" = "$MODEL_OK" ]; then
    ok "llama :4100" "no ar com $modelo"
  elif [ "$CHECK" -eq 1 ]; then
    bad "llama :4100" "fora ou com modelo errado (servido: ${modelo:-nada})"
  else
    say "llama :4100" "subindo llama-gk (modelo GK da FT)"
    sudo_ systemctl disable --now llama-gpu >/dev/null 2>&1 || true
    sudo_ systemctl enable --now llama-gk >/dev/null 2>&1 || sudo_ systemctl start llama-gpu || true
    for _ in $(seq 1 90); do
      [ "$(http http://127.0.0.1:4100/health 2)" = "200" ] && break
      sleep 2
    done
    modelo="$(served)"
    [ "$modelo" = "$MODEL_OK" ] && ok "llama :4100" "carregado com $modelo" \
      || bad "llama :4100" "servido: ${modelo:-nada} (esperado $MODEL_OK)"
  fi
else
  if [ "$modelo" = "$MODEL_OK" ]; then
    ok "llama :4100" "no ar com $modelo"
  elif [ -n "$modelo" ]; then
    bad "llama :4100" "no ar com $modelo (esperado $MODEL_OK)"
  else
    say "llama :4100" "fora (use --with-llama)"
  fi
fi

# ---------------------------------------------------------------- 2. prometheus binario
if have prometheus; then
  ok "prometheus binario" "$(command -v prometheus) $(prometheus --version 2>/dev/null | head -1 | awk '{print $3}')"
elif [ -x "$PROM_BIN/prometheus" ]; then
  if [ "$CHECK" -eq 1 ]; then
    bad "prometheus binario" "ausente em /usr/local/bin (persistido em $PROM_BIN)"
  else
    sudo_ install -m 755 "$PROM_BIN/prometheus" /usr/local/bin/prometheus
    sudo_ install -m 755 "$PROM_BIN/promtool"  /usr/local/bin/promtool
    ok "prometheus binario" "instalado de $PROM_BIN"
  fi
elif [ -f "$PROM_TGZ" ]; then
  if [ "$CHECK" -eq 1 ]; then
    bad "prometheus binario" "ausente (tarball disponivel em $PROM_TGZ)"
  else
    tmp="$(mktemp -d)"
    tar xzf "$PROM_TGZ" -C "$tmp"
    sudo_ install -m 755 "$tmp/prometheus-3.1.0.linux-amd64/prometheus" /usr/local/bin/prometheus
    sudo_ install -m 755 "$tmp/prometheus-3.1.0.linux-amd64/promtool"  /usr/local/bin/promtool
    rm -rf "$tmp"
    ok "prometheus binario" "instalado do tarball persistido"
  fi
else
  bad "prometheus binario" "sem binario nem tarball em $LAB"
fi

# ---------------------------------------------------------------- 3. config + unit
if [ "$CHECK" -eq 1 ]; then
  [ -f /etc/prometheus/oc-llama.yml ] && ok "config de scrape" "/etc/prometheus/oc-llama.yml" \
    || bad "config de scrape" "ausente (persistido em $PROM_ETC/prometheus/oc-llama.yml)"
  [ -f /etc/systemd/system/prometheus.service ] && ok "unit prometheus" "presente" \
    || bad "unit prometheus" "ausente (persistida em $PROM_ETC/prometheus.service)"
else
  sudo_ mkdir -p /etc/prometheus /var/lib/prometheus
  sudo_ install -m 644 "$PROM_ETC/prometheus/prometheus.yml" /etc/prometheus/prometheus.yml
  sudo_ install -m 644 "$PROM_ETC/prometheus/oc-llama.yml"   /etc/prometheus/oc-llama.yml
  sudo_ install -m 644 "$PROM_ETC/prometheus.service"        /etc/systemd/system/prometheus.service
  sudo_ systemctl daemon-reload
  ok "config + unit" "instalados do persistido"
fi

# ---------------------------------------------------------------- 4. prometheus no ar
if [ "$CHECK" -eq 0 ]; then
  if [ "$(http http://127.0.0.1:9090/-/ready)" != "200" ]; then
    sudo_ systemctl enable --now prometheus >/dev/null 2>&1 || true
    for _ in $(seq 1 20); do [ "$(http http://127.0.0.1:9090/-/ready)" = "200" ] && break; sleep 1; done
  fi
fi
[ "$(http http://127.0.0.1:9090/-/ready)" = "200" ] && ok "prometheus :9090" "ready" \
  || bad "prometheus :9090" "fora (sudo systemctl start prometheus)"

# ---------------------------------------------------------------- 5. collector (sonda)
if [ "$(http http://127.0.0.1:9091/metrics)" = "200" ]; then
  ok "collector :4199/:9091" "no ar"
elif [ "$CHECK" -eq 1 ]; then
  bad "collector :4199/:9091" "fora (./stack-up.sh)"
else
  mkdir -p "$(dirname "$EVENTS")"
  ( cd "$HERE" && setsid nohup python3 collector.py \
      > /tmp/opencode/llama-pure/collector.log 2>&1 < /dev/null & )
  for _ in $(seq 1 10); do [ "$(http http://127.0.0.1:9091/metrics)" = "200" ] && break; sleep 1; done
  [ "$(http http://127.0.0.1:9091/metrics)" = "200" ] && ok "collector :4199/:9091" "subiu (sonda ativa)" \
    || bad "collector :4199/:9091" "nao subiu (ver /tmp/opencode/llama-pure/collector.log)"
fi

# ---------------------------------------------------------------- 6. scrape do coletor
# O target so fica "up" depois do primeiro scrape (intervalo 5s): espera ate 20s.
tgt=0
tent=$([ "$CHECK" -eq 1 ] && echo 1 || echo 20)
for _ in $(seq 1 "$tent"); do
  tgt="$(curl -s -m 3 http://127.0.0.1:9090/api/v1/targets 2>/dev/null \
         | python3 -c 'import json,sys
try: ts=json.load(sys.stdin)["data"]["activeTargets"]
except Exception: ts=[]
print(sum(1 for t in ts if t["labels"].get("job")=="llama-opencode" and t["health"]=="up"))' 2>/dev/null)"
  [ "${tgt:-0}" -ge 1 ] && break
  [ "$tent" -gt 1 ] && sleep 1
done
[ "${tgt:-0}" -ge 1 ] && ok "scrape llama-opencode" "target up" \
  || bad "scrape llama-opencode" "target fora (confira /etc/prometheus/oc-llama.yml)"

# ---------------------------------------------------------------- 7. grafana: dashboard + pin
gcode="$(http "$GRAFANA/api/health")"
if [ "$gcode" != "200" ]; then
  bad "grafana :4800" "fora (Grafana nao e instalado por este script)"
else
  ok "grafana :4800" "no ar"
  # O dashboard le o coletor direto: plugin Infinity + datasource llama-json.
  if sudo -n test -d /var/lib/grafana/plugins/yesoreyeram-infinity-datasource; then
    ok "plugin Infinity" "instalado"
  elif [ "$CHECK" -eq 1 ]; then
    bad "plugin Infinity" "ausente (./install-grafana.sh instala)"
  else
    bash "$HERE/install-grafana.sh" >/tmp/opencode/llama-pure/install-grafana.log 2>&1 \
      && ok "plugin Infinity" "instalado" \
      || bad "plugin Infinity" "install-grafana.sh falhou (ver install-grafana.log)"
  fi
  curl -s -m 3 -u "$GUSER" "$GRAFANA/api/datasources" 2>/dev/null | grep -q llama-json \
    && ok "datasource llama-json" "presente" \
    || bad "datasource llama-json" "ausente (./install-grafana.sh)"
  dash="$(curl -s -m 3 -u "$GUSER" "$GRAFANA/api/dashboards/uid/$DASH" \
          | python3 -c 'import json,sys
try: d=json.load(sys.stdin); print(d.get("dashboard",{}).get("title",""))
except Exception: print("")' 2>/dev/null)"
  if [ -n "$dash" ]; then
    ok "dashboard $DASH" "provisionado ($dash)"
  elif [ "$CHECK" -eq 1 ]; then
    bad "dashboard $DASH" "ausente (./stack-up.sh provisiona)"
  else
    bash "$HERE/install-grafana.sh" >/tmp/opencode/llama-pure/install-grafana.log 2>&1 \
      && ok "dashboard $DASH" "provisionado" \
      || bad "dashboard $DASH" "install-grafana.sh falhou (ver install-grafana.log)"
  fi
  if [ "$CHECK" -eq 0 ]; then
    curl -s -m 3 -u "$GUSER" -X POST -o /dev/null \
      "$GRAFANA/api/user/stars/dashboard/uid/$DASH" || true
    curl -s -m 3 -u "$GUSER" -X PUT -o /dev/null -H "Content-Type: application/json" \
      -d "{\"homeDashboardUID\":\"$DASH\"}" "$GRAFANA/api/org/preferences" || true
  fi
  star="$(curl -s -m 3 -u "$GUSER" "$GRAFANA/api/user/stars" 2>/dev/null)"
  home="$(curl -s -m 3 -u "$GUSER" "$GRAFANA/api/org/preferences" 2>/dev/null)"
  case "$star" in *"$DASH"*) ok "dashboard fixado (estrela)" "sim" ;; *) bad "dashboard fixado (estrela)" "nao" ;; esac
  case "$home" in *"$DASH"*) ok "dashboard home da org" "sim" ;; *) bad "dashboard home da org" "nao" ;; esac
fi

echo
if [ "$falhas" -eq 0 ]; then
  echo "== STACK OK (0 falhas) =="
  exit 0
fi
echo "== STACK INCOMPLETA ($falhas pendencia(s)) =="
exit 1
