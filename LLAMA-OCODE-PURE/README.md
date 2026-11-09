# LLAMA-OCODE-PURE

Experimento: **opencode → llama direto**, sem bridge. Nada de cast de protocolo:
o opencode fala o protocolo nativo do provider e o llama responde.

```
opencode ──HTTP──> collector.py :4199 ──> llama-server :4100
                        │
                        ├── observer em memoria (RUN) ──> Grafana :4800 (JSON direto, plugin Infinity)
                        ├── /api/{summary,recent,status,dump,reset}
                        ├── /metrics :9091 ──> Prometheus :9090 (opcional, so a curva de t/s)
                        └── dump no fim da rodada ──> events.jsonl ──> report.py ──> reports/*.md
```

O `collector.py` **não** é bridge: repassa o request intacto e só mede
(endpoint, modelo, nº de mensagens, enunciado, tokens de prompt/saída, tokens/s).

O coletor e um **observer em memoria**: cada request vira um dict dentro de
`RUN["requests"]` (sem I/O por request). O disco so e tocado no **dump**
(`POST /api/dump`, `SIGTERM`/`SIGINT` ou `atexit`), que grava `events.jsonl` e
`metrics/runs/<run-id>.jsonl` de forma atomica. Sem request por `IDLE_S`
(padrao 60s) as gauges vivas voltam a zero e a tabela de requests esvazia —
painel nunca mostra numero congelado de rodada que acabou.

## Protocolos testados

O protocolo é escolhido pelo `package` do provider no `opencode.json` do workspace:

| nome | package | endpoint no llama |
| --- | --- | --- |
| `chat` | `@opencode/ai/providers/openai-compatible` | `POST /v1/chat/completions` |
| `responses` | `@opencode/ai/providers/openai` (nativo do opencode) | `POST /v1/responses` |

Os scripts criam config **de projeto** no workspace (`/tmp/opencode/llama-pure/...`)
com provider próprio (`llamapure`), então não herdam o `api: openai-chat` da
config global do usuário.

## Scripts

| Arquivo | Para que serve |
| --- | --- |
| `collector.py` | proxy medidor (:4199), observer em memoria, `/api/*`, `/metrics` (:9091) e dump no fim |
| `battery-chat.sh <chat\|responses>` | 6 perguntas de cultura geral com agente sem nenhuma tool |
| `battery-tools.sh <chat\|responses>` | `write`, `edit`, `git`, `webfetch`, `subagent` — PASS conferido no disco |
| `run-probe.sh <chat\|responses>` | tarefa mínima, mostra os eventos do coletor |
| `report.py` | gera `reports/<proto>-<timestamp>.md` + `reports/latest.md` |
| `probe-selftest.py` | auto-teste da sonda + observer + idle + dump (51 asserts, sem llama) |
| `probe-watch.py` | observa a sonda em tempo real (`--once`, `-n 20`, `-i 2`) |
| `stack-up.sh` | restaura a stack no live CD (prometheus do LAB-FT, collector, dashboard fixado) |
| `install-grafana.sh` | instala o plugin Infinity + datasource JSON + dashboard (`--with-prometheus` e opcional) |
| `llog.py` | proxy antigo, só observa (não é mais usado pelos scripts) |

Cada bateria gera um **relatório legível em disco** ao final (`reports/`), com
resumo, tabela de requests, enunciados e o PASS/FAIL da rodada.

## Subir a stack

```bash
python3 collector.py &                 # :4199 -> :4100, metrics :9091, observer em memoria
./install-grafana.sh                   # plugin Infinity + datasource llama-json + dashboard
./install-grafana.sh --with-prometheus # idem, e tambem liga o scrape do Prometheus (opcional)
```

Endpoints do coletor (nao vao para o llama):

| rota | o que devolve |
| --- | --- |
| `GET /api/summary` | agregado da rodada (requests, tokens, tps last/avg/max, erros, streak max, prosa) |
| `GET /api/recent?n=30` | ultimos N requests (a tabela do dashboard) |
| `GET /api/status` | run id, quantos requests em memoria, idle, `IDLE_S` |
| `POST /api/dump` | grava a rodada em disco (`events.jsonl` + `runs/<id>.jsonl`) |
| `POST /api/reset` | zera a rodada em memoria e abre um novo run id |

Grafana: <http://127.0.0.1:4800> (`admin` / `guest@123`) → dashboard **opencode -> llama (direto)**.

## Pré-requisitos

- llama-server em `:4100` com o modelo **treinado** da unidade FT
  (`FT/models/gguf/Gemma4-E4B-UCD-IT_Principal-GK-DS-Retrained-Q4_K_M-Imatrix.gguf`
  + mmproj bf16, template canonico `FT/models/templates/gemma4-canonical-e4b-2026-07.jinja`),
  alias `Gemma4-E4B-GK-Q4`, Bearer `guest@123` — unit `llama-gk` (ver abaixo).
- opencode v2 (`--standalone --auto`).
- Grafana em `:4800` com provisioning em `/etc/grafana/provisioning` e o plugin
  `yesoreyeram-infinity-datasource` (le o JSON do coletor). Prometheus `:9090`
  e opcional.

## Perfis por role

Plan, Orch e MoE têm `system` **próprio e reduzido** (substitui o prompt do CLI).
Build, Scripter e Exec **não têm override**: usam o prompt original do opencode.

| role | `system` | permissões |
| --- | --- | --- |
| `build`, `scripter`, `exec` | nenhum (prompt original do CLI) | completas (write/edit/shell/git commit/subagent) |
| `plan`, `moe` | reduzido: analista somente-leitura, proibido escrever/editar/commitar | read-only: `read`, `glob`, `grep`, `webfetch`, `websearch`, `skill` + shell só `git status|log|diff|show|branch|remote|rev-parse`, `ls`, `cat`, `rg`, `find`, `head`, `tail` |
| `orch` | reduzido: orquestrador que só delega e agrega | read-only + `subagent` |

Prompt reduzido de `plan`/`moe` usado no harness:

> Voce e um assistente de analise em modo SOMENTE LEITURA. Responda em portugues, direto e completo. Ferramentas permitidas: read, glob, grep, webfetch, websearch e git de leitura (status, log, diff, show, branch, remote, rev-parse). Voce NAO escreve, NAO edita, NAO faz commit. Use ferramenta so quando o enunciado exigir dado do repositorio ou da web; caso contrario responda em texto. Nunca escreva sintaxe de chamada de ferramenta no texto da resposta.

Detalhe que importa: tool **negada** faz o modelo vazar `<|tool_call>` cru na resposta;
tool permitida executa de verdade e o texto sai limpo.

## Bateria WAR (tunel, so enunciados)

`battery-war.sh <chat|responses> <tools|ent>` carrega os enunciados da suite WAR
sem nenhuma dependencia de harness externo (nenhuma porta 4124/4125, nenhum
processo C#, nenhuma "receita" de execucao). Duas trilhas:

- **tools** — agent `tools` (permissao completa, prompt original do CLI):
  - `tools-full`: app console C# (App.csproj + Program.cs, scaffold `dotnet new`), compila/executa, `git init`, commit `hello world console`; passo 2 troca para `Hello World v2` e commita `hello world v2`.
  - `mission-tools`: escreve `Program.cs` no `mcs.csproj` e imprime, um por linha, 10!, mdc(1071,462) e a quantidade de primos <= 50 (3628800 / 21 / 15), calculados (hardcode reprova).
  - `belts-azul`: repositorio generico com ORM/Dapper + SQLite, `dotnet build` rc=0.
  - `belts-verde`: dominio + DDL/indices/constraints/seeds, validacao CPF/CNPJ, `dotnet build` rc=0.
- **ent** — agent `plan` (system reduzido, `capabilities.tools=false`, texto puro):
  `crud-product`, `crud-customer`, `crud-saleinvoice`, `worker`, `redis-cache`,
  `redis-di` (continua o bloco 1 em handoff), `bus` (outbox + consumidor
  idempotente), `erp` (preco final com NCM 8471 isento) e `dashboard` (React 19
  + AbortController).

Aceite de cada caso roda no disco/log (arquivo, `git log`, `dotnet build`,
`dotnet run`, greps de contrato) — nenhum veredito vem do texto do modelo.

## Medido

Rodada final (2026-11-08), llama `Gemma4-E4B-GK-Q4` direto:

| bateria | responses | chat |
| --- | --- | --- |
| read-only (6 perguntas + `git log` + `webfetch` + delegação `orch`) | **9/9** | **9/9** |
| tools completas (`write`, `edit`, `git`, `webfetch`, `subagent`) | **5/5** | **5/5** |

- `responses` é o protocolo nativo do provider `openai` do opencode;
  `chat` é o do `openai-compatible`. Em confiabilidade empatam; em velocidade
  o `chat/completions` ficou ~10% à frente (mediana 40,8 vs 37,1 t/s, 208 requests).
- Correções medidas, sem bridge:
  1. `~/.config/opencode/AGENTS.md` (regras da bridge) entrava em todo request —
     as baterias o movem para fora durante a rodada.
  2. o llama rodava com temperatura default (~0.8); com `body: {"temperature": 0.1}`
     os argumentos de tool pararam de vir incompletos.

## Sonda de repeticao / devagacao (so observa)

O `collector.py` passou a medir o que o modelo **devolve** (nao so o que ele
recebe). Nada e contido, reescrito ou corrigido — e instrumentacao para ver
ciclo degenerado e devagacao de ideias:

| campo (events.jsonl) | gauge (/metrics) | o que mede |
| --- | --- | --- |
| `prompt_streak` | `oc_prompt_repeat_streak` | quantas vezes o **mesmo enunciado** foi reenviado em sequencia (>=3 = ciclo) |
| `tail_sim` | `oc_tail_similarity` | ultima janela (256 chars / 4 linhas) vs a anterior; >=0.85 = repetindo a saida |
| `tail_reps` | `oc_tail_repetitions` | quantas vezes a cauda de 256 chars aparece antes no texto |
| `dup_lines` | `oc_dup_lines_max` | maior numero de repeticoes da mesma linha (>=8 chars) |
| `meta` | `oc_meta_phrases` | marcas de prosa de plano (`vou`, `agora vou`, `em seguida`, `proximo passo`, `plano:`) |
| `resp_chars` / `resp_tools` / `tool_args` / `finish` | `oc_response_chars`, `oc_tool_calls_total` | texto, tools chamadas (inclusive `subagent`) e os argumentos delas |
| `think_chars`, `think_tail_sim`, `think_dup_lines`, `think_meta` | `oc_think_*` | a mesma sonda aplicada ao **thinking** (`reasoning_content`) — o Gemma4 GK pensa antes de responder |

Cada resposta e gravada em `/tmp/opencode/llama-pure/metrics/responses/*.txt`
(inclusive thinking e args de tool), para leitura posterior.

Auto-teste (sem llama): `python3 probe-selftest.py` → `PASS=51 FAIL=0`.

### Laco de tool (o que a prosa nao mostra)

Num modelo que responde **so `tool_calls`** (texto vazio), a devagacao nao aparece
como prosa: aparece como a **mesma tool com os mesmos args** repetida com o mesmo
erro de retorno. Por isso a sonda mede a assinatura `tool + args`:

| campo (events.jsonl) | gauge (/metrics) | o que mede |
| --- | --- | --- |
| `tool_sig` | - | assinatura da chamada (nomes + args normalizados, 200 chars) |
| `tool_sig_streak` | `oc_tool_sig_streak` | quantas vezes a MESMA assinatura veio em sequencia |
| `tool_sig_max` | `oc_tool_sig_max` | maior laco da rodada |

Medido na bateria `chat tools` (GK): `tail_sim=0`, `dup=0`, `meta=0` enquanto o
modelo repetia 8x `shell` com o caminho errado (`src/src/Data`). O sinal que
pegava era o `prompt_streak=8` (o resultado da tool volta como mensagem do usuario).

Ao vivo: `python3 probe-watch.py` (mostra streak, laco de tool, tail, dup, prosa).

O `report.py` mostra essas colunas por request e uma secao **"Sinal de
repeticao/devagacao (top 10)"** com os casos que passam dos limiares.

## Anti-repeat (texto e tools)

Mecanismo **enxuto** dentro do proprio `collector.py` (nao e bridge, nao reescreve a
resposta do modelo). Estado: assinatura `tool + args` da ultima resposta e o probe
de texto.

| sinal | gatilho | acao |
| --- | --- | --- |
| laco de tool | mesma assinatura >= `NUDGE_AT` (3) | injeta uma mensagem `user` de aviso no request seguinte (no maximo a cada 2 repeticoes) |
| texto repetido | `dup_lines >= 4` ou `tail_sim >= 0.9` | injeta aviso de "nao repita o que ja foi dito" |
| laco de tool em modo `stop` | mesma assinatura >= `STOP_AT` (6) | **nao chama o llama**: devolve resposta sintetica (SSE/JSON) dizendo que o laco foi cortado |

```bash
ANTI_REPEAT=stop python3 collector.py   # padrao: nudge em 3, corte em 6
ANTI_REPEAT=nudge python3 collector.py  # so avisa
ANTI_REPEAT=off  python3 collector.py   # so observa
NUDGE_AT=3 STOP_AT=6                    # limiares
```

Contadores: `oc_anti_repeat_nudges_total`, `oc_anti_repeat_stops_total`,
`nudges`/`stops` no `/api/summary`, flags `nudged`/`stopped` no evento e no
`/api/recent` (coluna `CORTADO`/`nudge` no `probe-watch.py`).

### Thinking e temperatura por modo

| modo | modelo no `opencode.json` | thinking | temperatura |
| --- | --- | --- | --- |
| plan | `llamapure/Gemma4-E4B-GK-Q4` | **on** (`chat_template_kwargs.enable_thinking=true`) | 0.2 |
| build/tools | `llamapure/Gemma4-E4B-GK-Q4-nothink` | **off** (`enable_thinking=false`) | 0.1 |

O `battery-war.sh` declara os dois modelos no provider e escolhe por agente
(`run_agent` usa `$MODEL` no plan e `$MODEL_BUILD` no tools). O alias `-nothink`
e so do cliente: o llama-server continua com o mesmo GGUF GK.

## Dashboard fixado

O dashboard `oc-llama-pure` tem 11 paineis e **le o coletor direto** (datasource
`llama-json`, plugin Infinity), sem TSDB no meio: 10 stats do `/api/summary`
(requests, t/s agora/medio/maximo, mensagens, erros, estado ativo/ocioso, tool
calls, maior streak, prosa de plano) e a tabela **"Ultimos requests (sonda em
memoria)"** vinda de `/api/recent?n=30` (streak, similaridade de cauda, linha
repetida, prosa, chars de resposta/thinking, tools, finish).

Sem request por 60s o painel mostra `OCIOSO (zerado)` e a tabela esvazia; no
restart do coletor a rodada anterior e arquivada e a nova comeca em zero. Ele
esta **estrelado** e e o **home da org** no Grafana :4800:

```bash
curl -u admin:guest@123 -X POST http://127.0.0.1:4800/api/user/stars/dashboard/uid/oc-llama-pure
curl -u admin:guest@123 -X PUT -H 'Content-Type: application/json' \
  -d '{"homeDashboardUID":"oc-llama-pure"}' http://127.0.0.1:4800/api/org/preferences
```

## Persistencia no live CD

`/` e overlay em tmpfs: prometheus, unit e configs **desaparecem no reboot**. O
que sobrevive fica no ext4 do LAB-FT (mesmo padrao do `FT/toolchain`):

```
/media/mint/LAB-FT/toolchain/prometheus/bin/{prometheus,promtool}
/media/mint/LAB-FT/toolchain/prometheus/etc/prometheus/{prometheus.yml,oc-llama.yml}
/media/mint/LAB-FT/toolchain/prometheus/etc/prometheus.service
/media/mint/LAB-FT/toolchain/archives/prometheus-3.1.0.linux-amd64.tar.gz
```

Restauracao depois do boot (idempotente):

```bash
./stack-up.sh --check      # so relata o que falta (exit 1 se incompleto)
./stack-up.sh              # reinstala prometheus do LAB-FT, sobe collector, provisiona e fixa o dashboard
./stack-up.sh --with-llama # idem + sobe o llama-server :4100
```

Grafana **nao** e instalado por esse script (a imagem ja tem); ele so instala o
plugin Infinity (fica em `/var/lib/grafana/plugins`, tambem efemero) e provisiona
datasource + dashboard, refazendo o pin. Prometheus virou opcional
(`--with-prometheus`): o dashboard nao depende dele.

## Modelo servido (correcao de 2026-11-08)

O alias `Gemma4-E4B-GK-Q4` existia no `opencode.json`, mas o `llama-gpu.service`
carregava **outro** arquivo: `/opt/models/gemma4-e4b-base/google_gemma-4-E4B-it-Q4_K_M.gguf`
(modelo base, alias `Gemma4-E4B-Q4`). O llama-server aceita qualquer nome de
modelo no request, entao os relatorios anteriores saiam rotulados `GK` medindo o
**base**. Medido e corrigido:

| item | antes | agora |
| --- | --- | --- |
| pesos | `/opt/models/gemma4-e4b-base/google_gemma-4-E4B-it-Q4_K_M.gguf` | `FT/models/gguf/Gemma4-E4B-UCD-IT_Principal-GK-DS-Retrained-Q4_K_M-Imatrix.gguf` (5,3 GB) |
| mmproj | `mmproj-gemma-4-E4B-it-Q8_0.gguf` | `...-Retrained-mmproj-bf16.gguf` |
| template | embutido no GGUF base | `--chat-template-file FT/models/templates/gemma4-canonical-e4b-2026-07.jinja` (fixes de tool-calling/thinking) |
| alias | `Gemma4-E4B-Q4` | `Gemma4-E4B-GK-Q4` |
| VRAM | ~6,2 GB | 8,2 GB (ctx 131072, `-np 1`, turbo3) |

Unit `llama-gk` (persistida em `LAB-FT/toolchain/llama/llama-gk.service`, instalada
pelo `stack-up.sh --with-llama`; o `llama-gpu` e desabilitado para nao disputar a
porta 4100).

Prova de que o template canonico esta no caminho (tool call limpa, sem thinking
vazando e sem loop):

```json
{"finish_reason":"tool_calls","message":{"content":"",
 "tool_calls":[{"function":{"name":"write",
   "arguments":"{\"content\":\"ola\",\"path\":\"notas.txt\"}"}}]}}
```

O `stack-up.sh --check` passa a **conferir o modelo servido** e falha se o
`:4100` estiver respondendo com outro alias.
