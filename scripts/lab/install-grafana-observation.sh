#!/usr/bin/env bash
# Instala/atualiza o dashboard OpenCodeBridge no Grafana local.
# - Plugin Infinity (yesoreyeram-infinity-datasource)
# - Dashboard JSON em /var/lib/grafana/dashboards (copia local lida pelo Grafana)
# - Provisioning (dashboard + datasource) em /etc/grafana/provisioning
# - Habilita no boot, reinicia grafana-server e valida /api/health
#
# Uso:
#   sudo bash scripts/lab/install-grafana-observation.sh
#   sudo GRAFANA_PORT=4800 bash scripts/lab/install-grafana-observation.sh
#   sudo GRAFANA_USER=admin GRAFANA_PASS=... bash scripts/lab/install-grafana-observation.sh
#
# Idempotente: pode rodar de novo a qualquer momento. A fonte do dashboard e'
# o JSON do repositorio; a copia em /var/lib/grafana/dashboards e' o que o
# provisioning le (funciona mesmo sem o repo montado na sessao live).
set -euo pipefail

REPO="${REPO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
PORT="${GRAFANA_PORT:-4800}"
GRAFANA_CLI="${GRAFANA_CLI:-/usr/sbin/grafana-cli}"
GRAFANA_USER="${GRAFANA_USER:-admin}"
GRAFANA_PASS="${GRAFANA_PASS:-guest@123}"
PLUGIN="yesoreyeram-infinity-datasource"
DASH_SRC="$REPO/observation/grafana/dashboards/opencodebridge.json"
DASH_DST="/var/lib/grafana/dashboards/opencodebridge.json"
PROV_DIR="/etc/grafana/provisioning/dashboards"
DS_DIR="/etc/grafana/provisioning/datasources"

if [ "$(id -u)" -ne 0 ]; then
  echo "ERRO: rode como root (sudo)." >&2
  exit 1
fi

[ -f "$DASH_SRC" ] || { echo "ERRO: dashboard ausente: $DASH_SRC" >&2; exit 1; }
command -v curl >/dev/null 2>&1 || { echo "ERRO: curl ausente." >&2; exit 1; }
systemctl list-unit-files grafana-server.service >/dev/null 2>&1 \
  || { echo "ERRO: grafana-server.service nao encontrado." >&2; exit 1; }

# 1) Plugin Infinity
if [ ! -d "/var/lib/grafana/plugins/$PLUGIN" ]; then
  echo "== instalando plugin $PLUGIN =="
  "$GRAFANA_CLI" --homepath /usr/share/grafana \
    --config /etc/grafana/grafana.ini plugins install "$PLUGIN"
else
  echo "== plugin $PLUGIN ja instalado =="
fi

# 2) Dashboard JSON
install -d -m 0755 /var/lib/grafana/dashboards
install -m 0644 "$DASH_SRC" "$DASH_DST"

# 3) Provisioning do dashboard: le a copia local, nao o repo. Assim a sessao
# live continua com dashboard mesmo se o disco persistente nao estiver montado;
# ao editar o JSON no repo, rode este script para atualizar a copia.
install -d -m 0755 "$PROV_DIR" "$DS_DIR"
cat > "$PROV_DIR/opencodebridge.yaml" <<EOF
apiVersion: 1
providers:
  - name: 'OpenCodeBridge'
    orgId: 1
    folder: 'OpenCodeBridge'
    type: file
    disableDeletion: false
    updateIntervalSeconds: 30
    allowUiUpdates: true
    options:
      path: /var/lib/grafana/dashboards
EOF

# 4) Datasource Infinity
install -m 0644 \
  "$REPO/observation/grafana/provisioning/datasources/opencodebridge.yaml" \
  "$DS_DIR/opencodebridge.yaml"

# 5) Permissoes, boot e restart
chown -R grafana:grafana /var/lib/grafana/dashboards "$PROV_DIR" "$DS_DIR" 2>/dev/null || true
systemctl enable grafana-server >/dev/null
systemctl restart grafana-server

# 6) Health
for _ in $(seq 1 30); do
  curl -sf -m 2 "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1 && break
  sleep 1
done
if ! curl -sf -m 3 "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1; then
  echo "ERRO: Grafana nao respondeu em :$PORT" >&2
  systemctl status grafana-server --no-pager -n 15 >&2 || true
  exit 1
fi

echo "OK: Grafana :$PORT ativo."
echo "Dashboard: http://127.0.0.1:$PORT/d/opencodebridge/opencodebridge-observation"

# 7) Verificacao do dashboard/datasource via API (credenciais default do
# ambiente; sobrescreva com GRAFANA_USER/GRAFANA_PASS se mudar a senha).
echo "== verificando dashboard/datasource via API =="
curl -sf -u "$GRAFANA_USER:$GRAFANA_PASS" \
  "http://127.0.0.1:$PORT/api/search?query=opencodebridge" \
  | grep -q '"uid":"opencodebridge"' \
  || { echo "ERRO: dashboard opencodebridge nao encontrado na API." >&2; exit 1; }
curl -sf -u "$GRAFANA_USER:$GRAFANA_PASS" \
  "http://127.0.0.1:$PORT/api/datasources" \
  | grep -q '"uid":"opencodebridge-infinity"' \
  || { echo "ERRO: datasource opencodebridge-infinity nao encontrado." >&2; exit 1; }
echo "OK: dashboard e datasource provisionados."
