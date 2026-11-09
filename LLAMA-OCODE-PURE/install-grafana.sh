#!/usr/bin/env bash
# Instala no host (sem docker) o que o dashboard precisa:
#   - Grafana (:4800) lendo o collector DIRETO via plugin Infinity (JSON em memoria)
#   - Prometheus OPCIONAL (--with-prometheus): guarda a curva de t/s, nao e mais
#     requisito do dashboard.
# Idempotente: pode rodar de novo depois de mudar dashboard/config.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
WITH_PROM=0
[ "${1:-}" = "--with-prometheus" ] && WITH_PROM=1

echo "1/5 grafana: plugin Infinity (le JSON do collector)"
if ! sudo -n test -d /var/lib/grafana/plugins/yesoreyeram-infinity-datasource; then
  sudo grafana cli --homepath /usr/share/grafana plugins install yesoreyeram-infinity-datasource
else
  echo "    ja instalado"
fi

echo "2/5 grafana: datasource do collector (llama-json -> 127.0.0.1:4199)"
sudo mkdir -p /etc/grafana/provisioning/datasources /etc/grafana/provisioning/dashboards
sudo install -m 644 "$HERE/grafana/provisioning/datasources/collector.yml" \
  /etc/grafana/provisioning/datasources/llama-collector.yml

echo "3/5 grafana: provider + dashboard"
sudo install -m 644 "$HERE/grafana/provisioning/dashboards/dashboards.yml" \
  /etc/grafana/provisioning/dashboards/llama-opencode.yml
sudo mkdir -p /var/lib/grafana/dashboards-llama
sudo install -m 644 "$HERE/grafana/dashboards/llama-opencode.json" \
  /var/lib/grafana/dashboards-llama/llama-opencode.json
sudo systemctl restart grafana-server

if [ "$WITH_PROM" = 1 ]; then
  echo "4/5 prometheus (opcional): scrape do coletor"
  sudo install -m 644 "$HERE/prometheus-scrape.yml" /etc/prometheus/oc-llama.yml
  if ! sudo grep -q "scrape_config_files" /etc/prometheus/prometheus.yml; then
    sudo python3 - <<'PY'
p = "/etc/prometheus/prometheus.yml"
s = open(p).read()
if "scrape_config_files" not in s:
    s = s.replace("scrape_configs:", "scrape_config_files:\n  - /etc/prometheus/oc-llama.yml\n\nscrape_configs:", 1)
    open(p, "w").write(s)
PY
  fi
  sudo promtool check config /etc/prometheus/prometheus.yml
  sudo systemctl restart prometheus
else
  echo "4/5 prometheus: pulado (use --with-prometheus se quiser a serie temporal)"
fi

echo "5/5 verificando"
sleep 6
curl -s -o /dev/null -w "dashboard=%{http_code}\n" -u admin:guest@123 \
  http://127.0.0.1:4800/api/dashboards/uid/oc-llama-pure
curl -s -u admin:guest@123 http://127.0.0.1:4800/api/datasources |
  grep -q llama-json && echo "datasource llama-json=ok" || echo "datasource llama-json=FALTA"
curl -s -m 5 http://127.0.0.1:4199/api/summary | head -c 200; echo
curl -s -u admin:guest@123 -X POST -H "Content-Type: application/json" \
  -d '{"queries":[{"refId":"A","datasource":{"type":"yesoreyeram-infinity-datasource","uid":"llama-json"},"type":"json","parser":"backend","source":"url","format":"table","url":"http://127.0.0.1:4199/api/summary","url_options":{"method":"GET"},"root_selector":"","json_options":{"root_is_not_array":true},"columns":[{"selector":"requests","text":"requests","type":"number"}]}],"from":"now-1h","to":"now"}' \
  http://127.0.0.1:4800/api/ds/query | head -c 120; echo
