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


<!-- INVARIANTE-TUNEL-LLAMA:v1 -->
## Invariante — túnel de stream do llama é exclusivo da bridge/core

**Somente a bridge (`Rochas.OpenCodeBridge`, camada Core) fala com o llama-server.** Testes (`Rochas.OpenCodeBridge.Test`), `mini-bridge-python`, Web UI, Grafana e scripts são clientes/observadores: não injetam, não reescrevem, não "cutucam" e não reordenam mensagens dentro do túnel.

- Cliente envia a **requisição** (`/v1/chat/completions`, `/v1/responses`); o stream do modelo volta repassado **sem edição** pelo core.
- Proibido no cliente: nudge/continuation prompt, injeção de "próximo passo", reescrita de turno, reordenação ou truncamento de histórico.
- Harness e coerção (`main_harness`, tails, `coerce_text_tools`, `coerce_narrated`, `degenerate_retry`) vivem **só** na bridge/core, atrás de flag.
- Comportamento de teste se ajusta por **enunciado** ou **flag da bridge** — nunca editando o conteúdo do túnel no cliente.
- Nudges de continuação do `TestContext.ToolAgent` foram **removidos em 2026-11-07** (`TCTX_NUDGES` não existe mais): turno sem tool call encerra o loop e o veredito sai do artefato.
- Fonte única: `docs/TUNEL-LLAMA.md`.

