# Observabilidade do ACEITE (Grafana)

Data: 2026-11-10

## Sintoma
- `Arquivos gerados` = 2553: contava dumps de `metrics/responses`, nao arquivos do caso.
- Grid `Ultimos requests` sem coluna de status (mostrava `tail sim`).
- Sem nome do teste em execucao e sem a resposta do modelo por step.

## Correcao
- Harness publica `metrics/current.json` (`case`, `status`, `dir`, `ts`) em `new_case`
  e `record_case`.
- Collector expoe `current_case`/`current_status`/`current_started`, conta arquivos do
  `dir` do caso atual (sem `obj/bin/.git/node_modules/.data/opencode.json/.last.txt`) e
  inclui `case`, `status` e `response` (500 chars) em `/api/recent`; status enriquecido
  com o ultimo `cases.jsonl`.
- Dashboard: `status` no lugar de `tail sim`, colunas `caso` e `resposta do modelo`,
  painel `Teste em execucao` e `Arquivos gerados (caso atual)`.
- Check do `bus` aceita `idempot` (a resposta correta usa `Idempotencia`).

## Validacao
- `bash -n battery-war.sh`; `python3 -m py_compile collector.py`; dashboard JSON valido.
- Contador: 3 arquivos reais contados, `obj/` e arquivos do harness ignorados.
- Dashboard provisionado (`uid=oc-llama-pure`), paineis 11/15/16 presentes.
