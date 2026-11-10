# Ajustes Grafana/coletor — 2026-11-10

## Problemas encontrados

1. O modo `ent` morria em `battery-war.sh:151` com `EXPECT: variável não associada`:
   `ent_case` usava `CASE_DIR="$WS"` sem passar por `new_case`, que é quem define
   `EXPECT`/`CASE_T0`; com `set -u`, o primeiro caso conceitual nem rodava.
2. O painel `Tool calls` ficava zerado enquanto `Maior laço de tool` mostrava valor:
   `summary()` calculava `tool_calls` pelas linhas da rodada atual, enquanto `tool_loop_max`
   usava o `SIG_MAX` global (não zerado no `api/reset`).
3. Não existia status PASS/FAIL dos casos no dashboard; o resultado só aparecia como
   `RESULTADO [...]` no log.
4. Não existia contador de arquivos gerados; `metrics/responses` acumulava respostas sem
   exposição no painel.

## Correções

- `battery-war.sh`
  - `ent_case` chama `new_case "$id"` (workspace `$WS/$id`, `opencode.json` próprio,
    `EXPECT=480`) e as mensagens usam `${EXPECT:-300}`.
  - `check()` grava uma linha JSONL em `$ROOT/metrics/cases.jsonl` com
    `case`, `status`, `duration`, `proto`, `modo`, `run` e `ts`.
- `collector.py`
  - `GET /api/cases`: lê o JSONL e devolve o último status por caso.
  - `/api/reset`: zera `TOOL_CALLS`, `SIG_MAX`, `SIG_STREAK`, `CYCLE`, `CYCLE_MAX`,
    `NUDGES`, `STOPS`, `ERR`, `LAST_PROMPT`, `STREAK`, `LAST_SIG`, `SIG_HIST` e `LAST`;
    `Tool calls` e `Maior laço` passam a refletir a mesma rodada.
  - `summary()` publica `tool_calls` (contador da rodada) e `files_generated`
    (quantidade de arquivos em `metrics/responses`).
- Dashboard `grafana/dashboards/llama-opencode.json`
  - Painel 14 `Resultados da bateria (PASS/FAIL)`: tabela `caso` + `status`, com
    `PASS` verde e `FAIL` vermelho.
  - Painel 15 `Arquivos gerados`: stat com `files_generated`.
  - Coluna `streak` traduzida para `repetição do enunciado`.

## Evidências

- `crud-product` **PASS** em 80s (`reports/chat-20261110-112944.md`).
- `GET /api/cases` → `{"cases":[{"case":"crud-product","status":"PASS",...}]}`.
- `GET /api/summary` → `files_generated=2540`, `tool_calls=0` e `tool_loop_max=0`
  (coerentes em collector ocioso; evoluem juntos durante a rodada).
- Dashboard API: painéis 14 e 15 presentes; `dashboard=200`; `stack-up` sem falhas.
- `bash -n battery-war.sh`, `python3 -m py_compile collector.py` e
  `python3 -m json.tool grafana/dashboards/llama-opencode.json` verdes.

## Infra

- Grafana: `http://127.0.0.1:4800` — usuário `admin`, senha `guest@123`; dashboard
  `oc-llama-pure` fixado como home/estrela.
- Collector: `:4199` (API) e `:9091` (Prometheus); Prometheus `:9090`; llama `:4100`.

## Pendências

- Push: remote HTTPS `rocha83/OpenCodeBridge`, sem `gh`/credential helper e com o PAT
  anterior inválido; o commit fica local até credencial válida.
- Campanha sequencial 2x e, depois, paralelo aleatório em 4 slots x112k.
