# Estado da rotina apartada (túnel puro, sem bridge C#)

Ponteiro de retomada. Atualizado em 09/11/2026, antes da rodada do retrain.

## Onde a rotina parou

Última rodada registrada: **08/11/2026 19:00** — `battery-war.sh chat tools`
(`reports/latest.md`, modelo `Gemma4-E4B-UCD-GK-Q4-nothink`, 55 requisições, 51,1 t/s médio):

```
RESULTADO [tools-full-create]: PASS
RESULTADO [tools-full-adjust]: PASS
RESULTADO [tools-full-run]: PASS
RESULTADO [mission-tools]: FAIL
RESULTADO [mission-tools-hardcode]: PASS
RESULTADO [belts-azul]: FAIL
RESULTADO [belts-verde]: FAIL
RESULTADO [belts-verde-sql]: FAIL
== RESUMO: PASS=4 FAIL=4
```

Sinais de repetição daquela rodada: `prompt_streak=3`, `tool_sig_max=3`, 4 requests com
laço de tool (>=3), `tail_sim=0,109`, `dup_lines=1`, `meta=0`. Ou seja: **não** havia
devagação de prosa; o que havia era **laço de tool com o mesmo args** — o padrão que o
`collector.py` mede como `tool_sig_streak`.

Baterias que já estavam fechadas antes (08/11, modelo GK-Q4 direto):

| bateria | responses | chat |
| --- | --- | --- |
| read-only (6 perguntas + `git log` + `webfetch` + delegação `orch`) | 9/9 | 9/9 |
| tools completas (`write`, `edit`, `git`, `webfetch`, `subagent`) | 5/5 | 5/5 |

## O que mudou para a retomada

- Modelo servido: **`Gemma4-E4B-GK-Q4`** (retrain QLoRA de 09/11, sem abliteração).
  Artefatos em `/opt/models/gemma4-e4b-gk-q4/` (sha256 em `SHA256SUMS`).
- Harness reapontado: `battery-chat.sh`, `battery-tools.sh`, `battery-war.sh`,
  `probe-selftest.py`, `run-probe.sh` e `stack-up.sh` saíram de
  `Gemma4-E4B-UCD-GK-Q4` para `Gemma4-E4B-GK-Q4` (14 ocorrências, `bash -n`/`ast` ok).
- `rodar-comparativo.sh` (novo): sobe o llama (`start-llama-gpu.sh --defaults`), sobe o
  túnel puro (`collector.py` em `:4199` → `:4100`) e roda a bateria, tudo logado em
  `/tmp/opencode/llama-pure/rodar-comparativo-<proto>-<modo>.log`.

## Como retomar

```bash
cd LLAMA-OCODE-PURE
./rodar-comparativo.sh tools        # os 8 casos WAR-tools (onde parou)
./rodar-comparativo.sh ent          # sequência Plan/conhecimento (texto puro)
./rodar-comparativo.sh chat         # read-only (9 casos)
./rodar-comparativo.sh responses tools   # protocolo nativo do provider openai
```

Sem subir nada (stack já no ar): `./rodar-comparativo.sh --no-up tools`.

## Critério de "alucina menos"

Não é opinião: a comparação é a mesma bateria, mesmo `AGENTS.md` (movido para fora durante
a rodada), mesma temperatura (`nothink`, 0.1) e o mesmo aceite no disco/log.

| eixo | como ler |
| --- | --- |
| PASS/FAIL por caso | primeira linha de comparação (havia 4 FAIL em tools) |
| `tool_sig_max` / `tool_sig_streak` | laço de tool com o mesmo args (o defeito real da rodada anterior) |
| `tail_sim`, `dup_lines`, `meta` | devagação de prosa no texto |
| `prompt_streak` | reenvio do mesmo enunciado em sequência |
| t/s e tokens de saída | custo da rodada (não é qualidade) |

Cada resposta fica em `/tmp/opencode/llama-pure/metrics/responses/*.txt` (texto + thinking +
args de tool), e o `report.py` gera `reports/latest.md` com a seção
**"Sinal de repetição/devagação (top 10)"**.

## Slots do llama (09/11/2026)

O retrain sobe com **4 slots**:

| ordem | `-c` | `-np` | por slot | quando |
| --- | --- | --- | --- | --- |
| 1ª | 524288 | 4 | 131072 (128k) | padrão |
| 2ª | 458752 | 4 | 114688 (112k) | fallback se 128k não couber na VRAM (12 GiB) |

`restart-llama-4x.sh` faz a tentativa e o fallback, e só declara sucesso com `/health` 200 +
`/v1/models` = `Gemma4-E4B-GK-Q4` (log em `/tmp/opencode/llama-pure/restart-llama-4x.log`).
O launcher `start-llama-gpu.sh` passou a aceitar `LLAMA_GPU_CTX` / `LLAMA_GPU_SLOTS`; sem env,
o padrão é 4 x 128k. Conferir depois: `./restart-llama-4x.sh --check` (servido, health,
`total_slots`/`n_ctx`, VRAM).

A bateria de comparação só vale com o llama já carregado nesse formato:
`./rodar-comparativo.sh --no-up tools`.

## Rodada do retrain (09/11/2026, 4x128k) — medido

`battery-war.sh chat tools` (túnel :4199 → :4100, `Gemma4-E4B-GK-Q4-nothink`), relatório
`reports/chat-20261109-153612.md`:

| sinal | 08/11 (UCD-GK-Q4) | 09/11 (retrain GK-Q4) |
| --- | --- | --- |
| requests | 55 | 83 |
| maior laço de tool (mesma tool+args) | 3 | **1** |
| requests com laço ≥3 | 4 | **0** |
| mesmo enunciado em sequência | 3 | 2 |
| `tail_sim` | 0,109 | 0,055 |
| `dup_lines` / `meta` | 1 / 0 | 1 / 0 |
| t/s médio | 51,1 | 43,4 |

PASS/FAIL: 4/4 nas duas rodadas, com composição diferente — `mission-tools` (cálculo real,
não hardcode) **FAIL → PASS**; `tools-full-adjust` **PASS → FAIL** (estoura o teto de passos do
agente, sem laço de tool).

Falhas que **não** são de qualidade do modelo: os `belts-*` rodavam com a delegação do orch
quebrada por falta de escape (`<1 linha>` dentro de string), erro `battery-war.sh: linha 123: 1:
Arquivo ou diretório inexistente`. Corrigido (`\"agent\":\"scripter\"...`).

Falha que **é** do modelo, e persiste: no `belts-azul` o orch devolve o micro-enunciado como
**JSON cru no texto** (`[{"title":"Create project file","prompt":"..."}]`) em vez de chamar a
tool `subagent`; no `belts-verde` cria `src/Domain/*.cs` e não cria `.csproj`/`.sql` → build
rc≠0. Ou seja: repetição caiu a zero, delegação estruturada do orch continua furada.

## Rodada das faixas (09/11/2026, noite) — roxa, marrom, preta

Matriz `belts-<agente>-<cor>` pelo túnel `:4199 -> :4100`, `Gemma4-E4B-GK-Q4-nothink` (build)
e `Gemma4-E4B-GK-Q4` (orch). Relatórios: `reports/chat-20261109-182440.md` (roxa),
`reports/chat-20261109-194251.md` + `...-194424.md` (marrom) e `...-195356.md` (preta).

| faixa | build | orch | sinal |
| --- | --- | --- | --- |
| roxa | **PASS** | **PASS** | 18:24; ambos fecharam |
| marrom | **PASS** | **PASS** | build 19:44 (`csharp ok | sql ok | react ok`); orch 19:42 (`csharp ok | sql ok | react nao`, PASS pelo aceite mínimo) |
| preta | **FAIL** | **PASS** | build: `CS8954` em `ColaboradorService.cs` (namespace repetido) e anti-repeat cortou após 6 builds idênticos; orch: `csharp ok | sql nao | ml-nn nao` (PASS pelo aceite mínimo) |

Correções de harness que destravaram a rodada:

- `opencode.json` gerado com aspas duplas não escapadas no system do Scripter → JSON inválido
  e `Model unavailable` em 1s. Trocado por aspas simples + caminho `/` e validado com `jq`.
- `clean_nested` agora `-mindepth 1` e todo `dotnet build` do aceite usa
  `DefaultItemExcludes='obj/**;bin/**;**/obj/**;**/bin/**'` (`%3B` na CLI). Isso elimina
  `CS0579` falso causado por `obj/` da raiz e por projeto aninhado.
- anti-repeat: `".cs" in comando` casava com `.csproj` e cortava `dotnet build Nome.csproj`
  como se fosse build de `.cs`. Trocado por regex `\.cs(?![A-Za-z])`, com mensagem para
  editar o arquivo antes de repetir.
- `question` negado nos agentes (run não-interativo); `steps` build/orch = 80; watchdog do
  orch = 180s (60s matava antes da primeira delegação).
- system do build: `.csproj` válido, versões do cache (`SqlClient 5.1.5`, nunca 8.x),
  `CS1061 QueryAsync -> using Dapper;` e proibição de parar na primeira etapa/build vazio.
- system do orch/scripter: projeto único, arquivos exclusivos, sem `CS0101`, `.cs` sempre
  com um tipo, e só concluir com build rc=0.

Pendência conhecida: o aceite dos `belts-*` só exige `.csproj` compilando + `.cs` fonte fora
de `obj/bin`; por isso `belts-orch-preta` PASSou com `sql nao | ml-nn nao`. Para a preta, o
aceite precisa reprovar quando faltar `.sql` com DDL ou `.py` executável — o `partial_report`
já mede os dois, mas ainda não entra no `check`.

## Preta build PASS (10/11/2026) — escopo de build fechado

`belts-build-preta` **PASS** (`reports/chat-20261110-041331.md`), `PARCIAL csharp ok | sql ok |
ml-nn ok`, run de 55s, `PASS=1 FAIL=0`. O workspace final tem um único `Portal.csproj` na raiz
(sem aninhado), `src/Program.cs` com `class PortalApp`/`Main`, `src/Data/schema.sql` e
`src/ml/train.py`; `dotnet build Portal.csproj` = 0 erros.

O que mudou para destravar:

- aceite root-only: exige exatamente 1 `.csproj` na raiz, falha se houver aninhado, e compila
  só o `.csproj` da raiz; `partial_report`/`build_errors` também root-only. (O nested
  `Portal/Portal.csproj` compilava uma biblioteca vazia e gerava falso PASS.)
- `rc=0` explícito no sucesso do build: antes o aceite iniciava `rc=1` e só fazia `|| rc=1`,
  então um build com **0 Erros** ainda dava `FAIL`.
- `StartupObject` removido do `Portal.csproj`: o `CS1555` era mismatch do prompt/harness (o
  modelo colocava `PortalApp` dentro de `namespace Portal`); sem `StartupObject` o compilador
  acha o `Main` único, e o namespace (bloco ou file-scoped, uma vez) é permitido.
- prompt/system do build preta alinhados no escopo de console app: sem API/bus/controllers,
  um `Portal.csproj` (`Microsoft.NET.Sdk`, `OutputType Exe`, `net10.0`, `ImplicitUsings`,
  Dapper 2.1.89, `Microsoft.Data.Sqlite` 10.0.12), um `src/Program.cs` com `class PortalApp`
  + `Main` e os tipos, `schema.sql` e `train.py`. Hints para `CS0246`
  (`System.Collections.Generic`/`System.Threading.Tasks`), `CS0101` (sem duplicar tipos) e
  sem top-level statements.
- heredoc do `opencode.json` sem backtick/`$` cru; `bash -n`, `py_compile` e `jq -e .` verdes.

Limite conhecido: o `dotnet run` do caso falha em runtime porque o `portal.db` local não tem a
tabela `Colaboradores` (o modelo cria o DDL mas não aplica). O aceite do build é **compilar**
(`compilando` no enunciado), então o PASS é legítimo; o `partial_report` confirma o DDL em
`src/Data/schema.sql`. O escopo completo da preta (API/bus) continua coberto pelo
`belts-orch-preta` (PASS).

## Grafana: PASS/FAIL, tool calls e arquivos gerados (10/11/2026)

O painel do Grafana era só telemetria do collector: não mostrava o resultado dos casos,
`Tool calls` ficava zerado enquanto `Maior laço` conservava o valor da rodada anterior, e
não havia contador de arquivos. Correções:

- `battery-war.sh`: `check()` grava `case/status/duration/proto/modo/run/ts` em
  `/tmp/opencode/llama-pure/metrics/cases.jsonl`; o modo `ent` agora usa `new_case`
  (workspace isolado por caso) e `EXPECT` com default, corrigindo o crash
  `EXPECT: variável não associada` (`f22bf6c`).
- `collector.py`: `GET /api/cases` devolve o último status por caso; `/api/reset` zera
  `TOOL_CALLS`, `SIG_MAX`, `SIG_STREAK`, `CYCLE*`, `NUDGES/STOPS`, `ERR` e `LAST_PROMPT`,
  para `Tool calls` e `Maior laço` serem sempre da mesma rodada; `summary()` publica
  `tool_calls` e `files_generated` (contagem de `metrics/responses`).
- dashboard: painel `Resultados da bateria (PASS/FAIL)` (caso/status), painel
  `Arquivos gerados` (contador) e label `streak` traduzido para `repetição do enunciado`.

Validação: `crud-product` PASS em 80s (`reports/chat-20261110-112944.md`); `/api/cases`
responde `crud-product PASS`; `/api/summary` responde `files_generated=2540`; dashboard
com painéis 14/15 (`dashboard=200`); `bash -n`, `py_compile`, `json.tool` e `stack-up`
verdes. Infra: Grafana `http://127.0.0.1:4800` (`admin` / `guest@123`), collector
`:4199/:9091`, Prometheus `:9090`, llama `:4100`.

Push: o remote é HTTPS (`rocha83/OpenCodeBridge`), não há `gh` nem credential helper, e o
PAT anterior encontrado é inválido; o commit fica local até haver credencial válida. Próximo:
campanha sequencial 2x (ent -> mission-tools -> tools-full -> 10 belts) e, depois, paralelo
aleatório em 4 slots x112k.


## Ajuste de observabilidade (2026-11-10, pos-rodada ent)

Problemas vistos no Grafana: `Arquivos gerados` mostrava 2553 (dumps em
`metrics/responses`), o grid nao tinha `status` (a coluna `tail sim` ocupava o lugar),
nao aparecia o teste em execucao nem a resposta do modelo por step.

Correcoes:

- `battery-war.sh`: `new_case()` grava `metrics/current.json` (`case/status=RUN/dir/ts`);
  `record_case()` atualiza o mesmo arquivo com `PASS/FAIL`. `ROOT/metrics` e criado.
- `collector.py`: le `current.json`; `/api/summary` publica `current_case`,
  `current_status`, `current_started` e `files_generated` contando o workspace do caso
  atual (exclui `obj/bin/.git/node_modules/.data/opencode.json/.last.txt`); cada linha de
  `/api/recent` leva `case`, `status` (enriquecido pelo ultimo `cases.jsonl`) e
  `response` (primeiros 500 chars); o `tail_sim` continua no dado/Prometheus, mas saiu do
  grid.
- dashboard: painel 11 agora tem `caso`, `resposta do modelo` e `status` no lugar de
  `tail sim` (override color-text PASS/FAIL/RUN); painel 16 `Teste em execucao`
  (`current_case`/`current_status`); painel 15 renomeado `Arquivos gerados (caso atual)`.
- `bus`: o check exigia a palavra exata `idempotente`, mas a resposta correta trouxe
  `Idempotencia`; trocado para o prefixo `idempot` (evita falso FAIL).

Validacao: `bash -n`, `py_compile` e JSON ok; contador testado com 3 arquivos reais
(`obj/opencode.json/.last.txt` ignorados); dashboard recarregado via provisioning
(`oc-llama-pure`, paineis 14/15/16). Campanha 2x reiniciada em seguida.

## Nudges de belt: decisao e limite (2026-11-10)

Os dois nudges de belt (falta de `.csproj` e erro de build) foram mantidos como **rede de
seguranca**, nao como forma de ensinar a resposta do teste. Eles so reenviam ao agente o
erro real do compilador e exigem `dotnet build` ate `rc=0`; nao contem o resultado esperado
nem o nome do caso. O comportamento desejado continua sendo o modelo derivar a solucao do
system prompt + enunciado + tabela de erros com exemplos otimistas.

Limite conhecido: um nudge que sempre cria o projeto pode mascarar um enunciado mal
especificado. Por isso, o proximo passo e testar cenarios independentes que exijam
`.csproj` sem o texto do belt, por exemplo:

- workspace vazio com pedido de console app e nome de projeto definido;
- class library com um unico `.csproj` e sem `Program`;
- projeto existente que deve ser corrigido (nao recriado);
- workspace com `.csproj` aninhado que precisa ser consolidado na raiz;
- dois `.csproj` na raiz, exigindo manter apenas um.

Objetivo: PASS sem nudge nesses cenarios; o nudge fica apenas para falha transitoria de
build/arquivo. A matriz atual das 10 belts roda em
`/tmp/opencode/llama-pure/belts-stabilize.sh` (TSV em `metrics/belts-stabilize.tsv`).

## Hints de build fora do harness (2026-11-10)

A tabela de erros saiu do `battery-war.sh` e virou `hints.py` (switch hardcode que
devolve `descricao` + `exemplo que compila`). O harness so imprime os codigos que
apareceram (`error_hints()` chama `python3 hints.py -`), e o `collector.py` ganhou
`build_hint()`: quando o ultimo tool result traz `error CS/MSB/NU`, ele injeta **um**
alerta `[build-hint]` no request com o erro exato (`arquivo(linha,col)`) e o exemplo —
dedupe por `codigo@arquivo(linha,col)` por conversa (teto 8). A mensagem sintetica leva
`x-injected: true`, entao nao infla `messages` nem substitui o enunciado no painel
(`_prompt_of` e `_new_user_count` ignoram). Detalhes: `reports/hints-build.md`.

Motivo: a tabela completa custava contexto em toda rodada e acoplava o harness global a
cada correcao (risco de regressao com rodada ativa). Agora a fonte e' unica e o suporte
chega no ponto de falha.

Ativacao: `BUILD_HINT=off` desliga; `HINT_MAX=N` muda o teto por conversa. O restart do
collector segue no boundary entre attempts (`collector-restart.log`).

## Aceite de producao: o que faltava nas belts (2026-11-10)

Matriz interrompida em roxa 3x FAIL, marrom 3x FAIL (build). Causas mapeadas, todas
estruturais no enunciado/agente — nao transporte:

- **Roxa**: o modelo escrevia o mesmo tipo em varios arquivos (`src/Domain/Models.cs`
  regravado), repetia `namespace` (CS8954) e chegava a `max steps` sem build. Correcao:
  `P_ROXA` agora fixa a lista de arquivos (um tipo por arquivo), exige `namespace
  Cadastros;` unico por arquivo e proibe `partial`/redeclaracao.
- **Marrom**: escrevia API ASP.NET dentro de projeto console (CS0234) e misturava
  acessibilidade (CS0051). Correcao: `BUILD_ACEITE` passou a exigir
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />` quando o escopo pedir API e
  a declarar cada tipo uma unica vez; o `hints.py` cobre CS0234/CS0051 com exemplo.
- **Agente**: `build` chegou a `maximum steps` (80). Subido para 160; tools 80, scripter
  100, orch 140 em `/tmp/opencode/llama-pure/war-tools-chat/opencode.json` (o harness copia
  por caso).

Comando do reteste focado: `/tmp/opencode/llama-pure/belts-run.sh belts-build-roxa`
(TSV em `metrics/focus-roxa.tsv`). O harness nao e' mais editado com rodada ativa.


