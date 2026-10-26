# OpenCodeBridge Observation

Painel de acompanhamento (Grafana) para a bridge e para as sondas Serilog dos steps.

## O que sobe
- Grafana (porta padrao 4800, conforme `start-grafana.sh`).
- Dashboard `opencodebridge` provisionado a partir de `grafana/dashboards/opencodebridge.json`.
- Sonda C# (`Rochas.OpenCodeBridge.Test` com `StepProbe`) emite logs estruturados Serilog em
  `logs/levels/*/probes/*.probe.json` e no console.

## Queries in-memory (TestData)
O template usa a fonte nativa `TestData DB` do Grafana (em memoria, sem servidor externo).
Paineis:
- `random_walk` para requests/throughput da bridge.
- `random` para latencia.
- `slow_query` para simulacao de cauda longa.
- `datapoints` para contadores de tools.
Para dados reais, substitua o datasource por Prometheus/Loki/Infinity e aponte para
`http://127.0.0.1:4124/api/metrics` (JSON) ou para os arquivos `*.probe.json`.

## Garnet
Nao e necessario para o template: o acompanhamento em tempo real usa Grafana + TestData.
Se quiser persistir series, suba o Garnet nativo pelo icone `Garnet.desktop` e aponte um
exporter Redis/Prometheus para ele; o dashboard pode ser duplicado com datasource Redis.

## Uso
1. `dotnet run --project observation/OpenCodeBridge.Observation.csproj`
2. O launcher inicia o Grafana, provisiona o dashboard e abre o navegador.
