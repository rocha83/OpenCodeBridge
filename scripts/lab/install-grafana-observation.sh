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

# 4b) Acesso anonimo (Viewer): painel local de observacao precisa aparecer na
# lista sem login. Sem `[auth.anonymous] enabled = true` o Grafana devolve 302
# para /login e a lista "Dashboards" fica vazia para quem abre :4800 direto.
GINI=/etc/grafana/grafana.ini
if [ -f "$GINI" ]; then
  awk '
    /^\[auth\.anonymous\]/ { insec=1; print; next }
    /^\[/ { insec=0 }
    insec && /^;?enabled[[:space:]]*=/ { print "enabled = true"; next }
    insec && /^;?org_name[[:space:]]*=/ { print "org_name = Main Org."; next }
    insec && /^;?org_role[[:space:]]*=/ { print "org_role = Viewer"; next }
    { print }
  ' "$GINI" > /tmp/opencode-grafana.ini.tmp && cat /tmp/opencode-grafana.ini.tmp > "$GINI"
fi

# 4c) Escuta PUBLICA em 0.0.0.0 (regra do lab): o painel e' lido da rede, nao
# so' do loopback. `http_addr` vazio ja escuta em todas as interfaces, mas o
# valor explicito evita depender do default do pacote. Idempotente: reescreve
# apenas a chave dentro de [server] e mantem o resto do arquivo intacto.
if [ -f "$GINI" ]; then
  awk '
    /^\[server\]/ { insec=1; print; print "http_addr = 0.0.0.0"; next }
    /^\[/ { insec=0 }
    insec && /^;?http_addr[[:space:]]*=/ { next }
    { print }
  ' "$GINI" > /tmp/opencode-grafana.ini.tmp && cat /tmp/opencode-grafana.ini.tmp > "$GINI"
fi

# 4d) Senha do lab (guest@123) sempre aplicada: e' a mesma da bridge/testes e a
# que o WAR e os scripts usam. `grafana cli` e' o caminho oficial (o hash do
# admin nao se escreve na mao); idempotente quando a senha ja e' essa.
if [ -x "$GRAFANA_CLI" ] && [ "${GRAFANA_RESET_PASS:-1}" = "1" ]; then
  "$GRAFANA_CLI" --homepath /usr/share/grafana admin reset-admin-password "$GRAFANA_PASS" >/dev/null 2>&1 \
    && echo "== senha admin do Grafana garantida (usuario $GRAFANA_USER) ==" \
    || echo "AVISO: reset de senha do Grafana falhou (siga com a senha atual)" >&2
fi

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

# 8) Revisao de TODOS os paineis: cada target do dashboard e' consultado pelo
# caminho real do Grafana (POST /api/ds/query com o datasource Infinity). Um
# painel "sem dado" aqui e' painel quebrado (401 na bridge, `source` ausente ou
# datasource inexistente) — mesma checagem que o WAR faz no inicio da bateria
# (WarTemplate.ReviewPanels). Falha NAO derruba o install: avisa e lista.
echo "== revisando paineis (ds/query) =="
python3 - "$PORT" "$GRAFANA_USER" "$GRAFANA_PASS" <<'PY'
import base64, json, sys, urllib.request

port, user, pwd = sys.argv[1], sys.argv[2], sys.argv[3]
bridge = "http://127.0.0.1:4124"
alvos = ["/api/status", "/api/metrics", "/api/test/steps?war=1",
         "/api/test/probe/latest", "/api/test/probe?limit=1000",
         "/api/test/activity?limit=6", "/api/test/enunciado/messages"]
auth = base64.b64encode(f"{user}:{pwd}".encode()).decode()
ok = 0
falhas = []
for path in alvos:
    body = json.dumps({"queries": [{
        "refId": "A",
        "datasource": {"uid": "opencodebridge-infinity",
                       "type": "yesoreyeram-infinity-datasource"},
        "type": "json", "source": "url", "format": "table",
        "url": bridge + path, "url_options": {"method": "GET"},
        "root_selector": "", "columns": [{"selector": "mode", "text": "mode", "type": "string"}]}],
        "from": "now-6h", "to": "now"}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/api/ds/query", data=body,
                                 headers={"Content-Type": "application/json",
                                          "Authorization": "Basic " + auth})
    try:
        with urllib.request.urlopen(req, timeout=8) as r:
            j = json.loads(r.read() or b"{}")
        res = (j.get("results") or {}).get("A") or {}
        has_data = bool((res.get("frames") or [{}])[0].get("data"))
        status = res.get("status", 0)
        if status == 200 and has_data:
            ok += 1
        else:
            falhas.append(f"{path}(status={status},data={has_data})")
    except Exception as e:
        falhas.append(f"{path}({e.__class__.__name__})")
print(f"[painel] ds/query: {ok}/{len(alvos)} endpoints com dado")
if falhas:
    print("[painel] AVISO sem dado: " + ", ".join(falhas))
    print("[painel] confira bridge :4124 no ar, Bearer do datasource e `source: url` no target")
PY
