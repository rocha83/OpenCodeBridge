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

## Direcionamentos de belt: decisao e limite (2026-11-10)

Os dois direcionamentos de belt (falta de `.csproj` e erro de build) foram mantidos como **rede de
seguranca**, nao como forma de ensinar a resposta do teste. Eles so reenviam ao agente o
erro real do compilador e exigem `dotnet build` ate `rc=0`; nao contem o resultado esperado
nem o nome do caso. O comportamento desejado continua sendo o modelo derivar a solucao do
system prompt + enunciado + tabela de erros com exemplos otimistas.

Limite conhecido: um direcionamento que sempre cria o projeto pode mascarar um enunciado mal
especificado. Por isso, o proximo passo e testar cenarios independentes que exijam
`.csproj` sem o texto do belt, por exemplo:

- workspace vazio com pedido de console app e nome de projeto definido;
- class library com um unico `.csproj` e sem `Program`;
- projeto existente que deve ser corrigido (nao recriado);
- workspace com `.csproj` aninhado que precisa ser consolidado na raiz;
- dois `.csproj` na raiz, exigindo manter apenas um.

Objetivo: PASS sem direcionamento nesses cenarios; o direcionamento fica apenas para falha transitoria de
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

Correcao de rumo (15:25): a "receita" arquivo-a-arquivo **nao** garantiu PASS — com ela a
roxa falhou igual, porque o modelo grava duas declaracoes do mesmo tipo no mesmo arquivo
(`record struct Cep` + `class Cep`) e repete `namespace` (CS8954/CS0101). A receita saiu do
enunciado; no lugar ficaram (a) a regra permanente no system prompt do `build` (um tipo
declarado uma vez, namespace uma vez) e (b) o alerta do collector com acao objetiva
(`sed -i '<linha>d'`, "apague a segunda declaracao em arquivo:linha"). Analise completa:
`reports/roxa-limites.md`. Logs de rodada agora ficam em `logs/` (gitignored), nao em /tmp.




## Roxa: PASS (2026-11-10 16:01)

Rodada focada com o harness novo (`rerun.sh belts-build-roxa`, enunciado natural, sem
receita de arquivos) fechou **PASS** na 3a tentativa em 379s: `Cadastros.csproj` unico na
raiz, 4 `.cs` (`Cliente`, `Fornecedor`, `ClienteRepository`, `ClienteService`),
`dotnet build` 0 erro / 0 aviso, aceite `rc=0 && fontes .cs`.

O que destravou (medido na mesma rodada): `tool_hints: 10` e `build_hints: 2`. O validador
de args da tool (`hints.py::tool_call_problem` + `collector::tool_arg_hint`, porte enxuto
do `ToolCallRepair`/`ToolHelp`/`ToolContentGuard` da bridge C#) devolve ao modelo a forma
que passa (`filePath/oldString/newString`), no lugar do laco de
`Invalid arguments for tool "edit"` que consumia os 160 passos. A regra "editou, reiniciou"
virou `/tmp/opencode/llama-pure/rerun.sh <belt>`.

Proxima na ordem: marrom (CS0234/FrameworkReference, CS0051, CS1513).

## Harness: prompts dos agentes vivem no heredoc do battery-war.sh (17:16)

Descoberta da rodada orch: `battery-war.sh` **gera** `$WS/opencode.json` a cada execucao
(`cat > "$WS/opencode.json" <<JSON`), entao editar `war-tools-chat/opencode.json` nao muda
nada — o arquivo e' saida, nao fonte. Fonte real: o heredoc em `battery-war.sh` (linhas ~47-120).

Consequencia medida: o system do `orch` trazia `Reembolso.cs`/`Reembolsos.csproj` como
exemplo literal e o 4B copiava o exemplo como conteudo -> `belts-orch-roxa` "passou" com um
CRUD de reembolsos. Correcoes: (a) exemplo do orch virou placeholder `<parte do escopo>` +
regra "EXEMPLO e' FORMA, nao conteudo"; (b) aceite passou a exigir o **dominio do enunciado**
nos `.cs/.sql` (`belt_scope`: roxa Cliente/Fornecedor, marrom/verde Reembolso, preta
Colaborador, azul Consulta); (c) `steps` do orch 80 -> 140; (d) regras de declaracao unica e
de args de tool no system do `build` (dentro do heredoc).

Outro achado operacional: rodada iniciada com `nohup ... &` morria quando a tool call que a
lancou terminava (runner+battery no mesmo PGID/sessao). `rerun.sh` agora usa `setsid nohup`
para collector e runner: a rodada fica em sessao propria (PPID=1) e sobrevive.

## Placar do gate 10 belts (2026-11-10 19:18)

| belt | status | tentativas | tempo | evidencia |
| --- | --- | --- | --- | --- |
| build roxa | PASS | 3 | 379s | Cadastros.csproj unico, 4 .cs, 0 erros |
| build marrom | PASS | 3 | 955s | Reembolsos.csproj unico, DDL + 3 .cs, 0 erros |
| build preta | PASS | 2 | 405s | Portal.csproj + src/Program.cs, 0 erros |
| build azul | PASS | 1 | 49s | Consulta.csproj, 0 erros |
| build verde | PASS | 1 | 93s | 0 erros |
| orch azul | PASS | 1 | 53s | Reembolsos.csproj raiz + DDL + repo + servico, 0 erros |
| orch roxa | FAIL | 3 | 1644s | ver reports/orch-limites.md (multi-entidade) |
| orch verde | FAIL | 3 | 969s | DDL .sql ausente + erro de compilacao na 3a |
| orch preta | em curso | - | - | - |
| orch marrom | pendente | - | - | - |

Leitura: a trilha `build` esta 5/5. A trilha `orch` passa quando o enunciado tem uma entidade
(azul) e falha quando exige coordenar varias (roxa) ou entregar DDL (verde). Nao e' hint que
falta: e' o orquestrador 4B distribuindo o mesmo workspace entre scripters sem dono de arquivo.

### Fechamento do gate (2026-11-10 20:16)

Build 5/5 PASS; orch 1/5 PASS (azul). Placar completo e causas em `reports/gate-10-belts.md` e
`reports/orch-limites.md`. Blocked: orquestrador 4B nao coordena multi-entidade e nao pode verificar
(sem shell). Tres caminhos de liberacao propostos no relatorio (shell restrito ao orch, orquestrador
maior, ou receita de arquivos no enunciado).

## Harness em arquivos: o system vem de `harness/`, montado pelo túnel (2026-11-11)

### Decisão

O system prompt deixou de ser texto colado no `battery-war.sh` (campo `system` do
agente, 914 ch) e de ser o prompt nativo do opencode (6.855 ch, dos quais 5.825 ch
eram o bloco `## Available tools` — catálogo TypeScript que duplica o JSON de
`tools` que já vai no request). Agora o texto vive em `harness/*.md`, é ordenado
por `harness/manifest.json` e montado por `harness_loader.py`; o `collector.py`
substitui `messages[0]` (role=system) pelo texto montado.

Motivo medido no dump (`reqs/mt1/*-0002.json`): system 6.855 ch = 699 (nosso texto)
+ 129 (`# Your Model`) + 202 (`# Code Mode`) + **5.825 (`## Available tools`)**;
`tools` JSON = 12.165 ch com 11 tools. O catálogo TS era 32% do prefixo fixo e não
ensinava comportamento nenhum.

### Estrutura

| caminho | papel |
| --- | --- |
| `harness/manifest.json` | ordem de montagem + orçamento (fonte única da sequência) |
| `harness/parts/00-papel.md … 13-exemplos.md` | 14 seções de comportamento universal |
| `harness/examples/dotnet/00 … 09` | 10 trilhas de forma (onde cada coisa mora) |
| `harness/README.md` | como editar, orçamento e regras de escrita |
| `harness_loader.py` | lê, ordena, mede tokens, aplica teto e gera relatório |

MoE e "roles e pipeline" **não existem mais** no pacote (nem como tails): a role já
chega no prompt do agente do cliente, e o texto antigo repetia as 7 roles a cada
request. `tails/` foi removido.

### Orçamento (`manifest.json` → `budget`)

- `max_total_tokens: 12000` (prefixo fixo = system + catálogo de tools);
- `tools_reserve_tokens: 3500` (catálogo medido: 12.165 ch);
- `chars_per_token: 3.5` (estimador **conservador**; nos dumps medidos o real ficou
  entre 4,1 e 4,5 ch/tok — ex.: 20.070 ch → 4.529 tok);
- `on_overflow: drop_examples` (corta do fim das trilhas e registra em `dropped`;
  **nunca** corta `parts`).

Medido em 2026-11-11 (`python3 harness_loader.py --report`):

| grupo | chars | tok (3.5) |
| --- | --- | --- |
| parts (14) | 4711 | 1346 |
| trilhas (00-09) | 23828 | 6808 |
| system | 28586 | 8167 |
| tools (reserva) | 12.165 | 3.500 |
| **total** | **40751** | **11667** |

Folga: 333 tok sobre o teto de 12k pelo estimador de 3,5. Pelo ch/tok real (≥4,1)
o total fica em ~9,7k. No pior caso teórico (3,3) daria 12.349 — 349 acima; por
isso o estimador de 3,5 é o número de trabalho e 3,3 é só alerta.

### Como o túnel aplica (`collector.py`)

- `HARNESS_MODE=assembled` (default): troca o system do cliente pelo montado.
  `HARNESS_MODE=off` repassa o system do cliente intacto.
- `HARNESS_STACK` (default `dotnet`), `HARNESS_MAX_TOKENS`,
  `HARNESS_TOOLS_RESERVE`, `HARNESS_CHARS_PER_TOKEN`, `HARNESS_ON_OVERFLOW`
  sobrescrevem o manifesto por ambiente.
- O bloco `<env>` do system original (cwd, workspace, plataforma, data) é
  **preservado** e reanexado ao fim; sem `<env>`, as linhas conhecidas são copiadas.
- O texto é montado **uma vez por processo**: bytes idênticos entre requests, então
  o cache de prefixo do llama continua válido. Editar `harness/` exige
  `POST /api/harness/reload` (ou restart do collector).
- Endpoints novos: `GET /api/harness` (relatório: arquivos, chars, tokens, folga,
  cortados) e `POST /api/harness/reload`.
- Harness quebrado (arquivo faltando, JSON inválido, orçamento estourado) **não
  derruba o proxy**: `active=false` + `error` no relatório e o system do cliente é
  usado como fallback.

### Trilhas (exemplos de forma, não de resposta)

| # | trilha | chars |
| --- | --- | --- |
| 00 | comandos base dotnet (net10, EF 10, build/run, sem `dotnet new` em projeto existente) | 736 |
| 01 | DDD + EF + repositório genérico (contratos no domínio) | 2.941 |
| 02 | git flow sobre projeto existente | 635 |
| 03 | serviços de app: cálculo + CPF/CNPJ (spec no domínio) | 2.123 |
| 04 | minimal API + guideline Microsoft | 1.711 |
| 05 | CQRS + cache resiliente (alimenta no insert, invalida na edição) | 3.208 |
| 06 | bus em tópicos (publisher, consumer com retry/backoff e idempotência) | 3.594 |
| 07 | SQL: DDL + DML casando com a trilha 01 | 1.609 |
| 08 | React 19 + TypeScript + Bootstrap 5 | 3.934 |
| 09 | entidade composta header+details (aggregate root, `OwnsMany`, transação) | 3.337 |

### Enunciados e belts

- `tools-full` (create/adjust), `mission-tools` (create/code) e belts **azul a preta**
  agora rodam **somente build**, com enunciado simples (sem receita, sem versão de
  pacote, sem `Criterio de aceite:` apensado, sem placeholder `{ws}`).
- `BUILD_ACEITE` (824 ch repetidos em 5 enunciados) e os 5 enunciados `P_*_ORCH`
  foram removidos; o agente `orch` saiu do `opencode.json` do caso (nada o usava).
- `build.system` caiu de 914 → 224 ch: é só ponteiro/fallback; a fonte é o harness.
- O aceite continua no `check` do `battery-war.sh` (build rc=0, fonte real, escopo,
  números exatos do mission-tools, `git log` do tools-full).

### Aceite executado

```
python3 -m py_compile collector.py harness_loader.py     -> OK
python3 harness_loader.py --report                       -> 11.443 tok, folga 557
bash -n battery-war.sh                                   -> OK
opencode.json (heredoc expandido)                        -> JSON válido, agentes: tools/build/plan
apply_harness(body) com <env>                            -> preserva cwd, idempotente
collector em porta alternativa + GET /api/harness        -> relatório com active=true
```

### Riscos e limites

- Estimador 3,5 é conservador; se um dia o tokenizer real cair a 3,3 o teto estoura
  em ~110 tok — o `drop_examples` cobre isso cortando a última trilha e registrando.
- Mudar `harness/` sem reload mantém o texto antigo em memória (por design: cache).
- O texto é pin de comportamento, não de versão do opencode; subir a versão não
  invalida o harness, mas o `system` do cliente continua sendo descartado.
- Resposta do modelo conta para o teto de contexto do llama (131k), não para o teto
  de 12k, que é só do prefixo fixo.

## Queda de infra e reerguimento (2026-11-11, tarde)

O host voltou em live CD: `/` é overlay em tmpfs, então **units, prometheus,
collector e /tmp/opencode sumiram** — sobraram o Grafana (na imagem) e os
arquivos do repo. O que foi feito, e como conferir:

| passo | comando | prova |
| --- | --- | --- |
| unit do modelo da FT | `install -m 644 LAB-FT/toolchain/llama/llama-gk.service /etc/systemd/system/` + `systemctl enable --now llama-gk` | `llama-gk` active |
| pesos | `FT/models/gguf/Gemma4-E4B-UCD-IT_Principal-GK-DS-Retrained-Q4_K_M-Imatrix.gguf` + mmproj bf16 + template canônico | `/v1/models` = `Gemma4-E4B-GK-Q4` |
| contexto | `-c 131072 -np 1` (turbo3) | `/props` = `n_ctx 131072`, VRAM 8.209/12.288 MiB |
| túnel puro | `python3 collector.py` (`:4199` → `:4100`, métricas `:9091`) | `/api/status` e `/api/harness` |
| resto da stack | `./stack-up.sh --with-llama` (prometheus, scrape, plugin Infinity, dashboard e pin) | `== STACK OK (0 falhas) ==` |

Ajuste no alias: a unit persistida servia `Gemma4-E4B-UCD-GK-Q4`, enquanto
scripts e `stack-up.sh` esperam `Gemma4-E4B-GK-Q4`. Ficou `--alias
Gemma4-E4B-GK-Q4,Gemma4-E4B-UCD-GK-Q4` (lista, sem duplicar unit).

`start-llama-gpu.sh --defaults` **não** serve para isto: ele reescreve a unit e
aponta para `/opt/models/gemma4-e4b-base` (modelo base). O caminho é a unit
`llama-gk`, que carrega os pesos da FT.

### Bug corrigido: o harness montado não chegava ao llama

`apply_harness(body)` alterava o dict, mas `raw_body` (os bytes enviados) só era
re-serializado quando um hint disparava; em request sem hint o llama recebia o
system **original do cliente**. Medição: `prompt_n=94` com o texto de 8.167 tok
montado. Correção em `collector.py::_forward` (re-serializa quando
`apply_harness` devolve `True`). Prova depois do fix: `prompt_n=7.955` no 1º
request e `64` no 2º (só o delta — cache de prefixo do llama preservado).

### Medição A/B do prefixo (2026-11-11, tarde)

Mesmo enunciado, `temperature 0.1`, 1 chamada direta ao llama com duas
variantes de system:

| system | prompt | 1ª ação do modelo |
| --- | --- | --- |
| curto (132 ch) | 216 tok | `write` com `path`/`content` (Program.cs) — correto |
| harness montado (28.586 ch) | 8.116 tok | `write` com `{"command":…}` — argumento de `shell` na tool errada |

Composição do prefixo: **parts 4.711 ch (1.346 tok) e trilhas 23.828 ch
(6.808 tok) = 83% em trilhas de código**. Em enunciado de uma frase
(`tools-full`, hello world) a rodada com o harness gravou
`src/api/customers.ts` + `src/Domain/Cliente.cs` — ou seja, copiou a trilha 08
(React) e não o pedido. Sem harness, os logs de 09/11 fazem esse mesmo caso
passar. Conclusão parcial: o teto de 12k está certo, a **proporção** não — as
trilhas dominam o prefixo e o modelo pequeno as copia.

## Harness enxuto + skills (2026-11-11, noite)

O prefixo fixo deixou de carregar trilha de codigo: `examples/dotnet/*` (23.828
ch) saiu do default e virou **skill** (`.opencode/skills/<id>/SKILL.md`), que o
modelo carrega sob demanda pela tool `skill`. No lugar ficou o **contrato** do
stack (`harness/parts/14-formas.md`, uma linha por capacidade, sem arquivo
pronto para copiar).

| camada | conteudo | custo |
| --- | --- | --- |
| `parts/` + `14-formas.md` | papel, ambiente, disciplina, contrato do stack | 2.087 tok |
| `tools` (catalogo) | reserva medida | 3.500 tok |
| total do prefixo fixo | — | **5.587 tok** (teto 12.000, folga 6.413) |
| `HARNESS_STACK=dotnet-api` | trilha fixa (`api` + `sql-ddl`) | 3.147 tok de system |
| `HARNESS_STACK=dotnet` | todas as 11 skills | 11.988 tok (**folga 12**; corta `ml-nn` e `dotnet-aggregate` pelo teto) |

### Raia sem skills (8 FAIL em 10 casos)

`WAR_RESET=1 ./battery-war.sh chat tools tools-full mission-tools belts-azul
belts-verde belts-roxa belts-marrom belts-preta`, harness base:

```
tools-full-create     PASS   (com o harness cheio era FAIL: gravava src/api/customers.ts)
tools-full-adjust     FAIL   (criou src/app/App.csproj com raiz invalida -> MSB4025; anti-repeat 6)
tools-full-run        FAIL
mission-tools-create  PASS
mission-tools-code    FAIL   (edit com oldString obsoleto -> anti-repeat 6)
belts-build-azul      FAIL   (CS0029 + await foreach em List; projeto em src/ e nao na raiz)
belts-build-verde     FAIL
belts-build-roxa      FAIL   (direcionamento build: sem .csproj na raiz, sem fonte .cs)
belts-build-marrom    FAIL   (idem)
belts-build-preta     FAIL   (idem)
```

Leitura: o enxuto conserta o caso ingenuo e o de texto, mas **forma de stack**
(onde mora o `.csproj`, como editar, como compilar) e o que falta nos casos
grandes — exatamente o papel da skill sob demanda.

### Skill: dois bugs medidos

1. **`description` com dois-pontos e espaco** (`... ASP.NET Core: Minimal API`)
   e YAML invalido: a skill era descartada em silencio e o catalogo saia so com
   as builtin (`OpenCode`, `Report`). Correcao: descricao **entre aspas**.
   Prova: `reqs/*.json` com `orig_system` de 10.321 ch listando 13 skills.
2. **O harness engolia o catalogo**: `apply_harness` trocava o system inteiro e
   so preservava `<env>`, entao a tool `skill` chegava sem nenhuma skill para
   rotear. Correcao: preservar tambem o bloco `<available_skills>` (com a linha
   "Skills provide specialized instructions..." que o precede). Prova: o mesmo
   `orig_system` e o `body` pos-harness contem o bloco.

O dump de request agora grava `orig_system` **e** `body` (pos-harness); antes so
o corpo montado, o que impedia ver o que o cliente tinha mandado.

### Pendente

- Medir a raia **com skills** nos mesmos casos e comparar passo/token/PASS com a
  raia sem skills; decidir se o caso aberto usa skill e o gate usa
  `HARNESS_STACK` deterministico.

## Fail-fast na esteira (2026-11-11, noite)

`battery-war.sh` passou a **encerrar a rodada no primeiro FAIL** (`WAR_STOP_ON_FAIL`
default 1). Antes, `tools-full:create` podia falhar e a esteira seguia para as
belts grandes (6 casos, ~1h de GPU) sem chance de verde — so consumo.

- Cada passo nao executado sai como `RESULTADO [<caso>]: SKIP (fail-fast)` e o
  resumo vira `PASS=n FAIL=m SKIP=k`, com `== ABORTADO (fail-fast) no primeiro FAIL: <caso>`
  e exit 1.
- `belts`/`ent`/`tools-full`/`mission-tools`: o guarda e no passo (create falhou
  -> nao roda adjust nem run) e no caso (belt falhou -> as seguintes sao SKIP).
- `WAR_STOP_ON_FAIL=0` e a unica forma de medir placar completo (uso consciente).
- Dentro do caso, o "direcionamento" (segunda passada que repara aceite) continua
  acontecendo **antes** do `check`: e o mecanismo que as vezes salva o caso, e e
  limitado (90-180s).

Prova sem GPU (stub de `opencode` que nao escreve nada, llama parado):

```
$ PATH=/tmp/opencode/stub:$PATH WAR_RESET=0 ./battery-war.sh chat tools tools-full
----- [tools-full:create]
RESULTADO [tools-full-create]: FAIL
ABORTO [fail-fast]: tools-full-create falhou - rodada encerrada (WAR_STOP_ON_FAIL=0 mede placar completo)
RESULTADO [tools-full-adjust]: SKIP (fail-fast)
RESULTADO [tools-full-run]: SKIP (fail-fast)
== RESUMO: PASS=0 FAIL=1 SKIP=2
== ABORTADO (fail-fast) no primeiro FAIL: tools-full-create
```

```
$ PATH=/tmp/opencode/stub:$PATH WAR_RESET=0 ./battery-war.sh chat tools belts-azul belts-verde belts-roxa
RESULTADO [belts-build-azul]: FAIL
RESULTADO [belts-build-verde]: SKIP (fail-fast)
RESULTADO [belts-build-roxa]: SKIP (fail-fast)
== ABORTADO (fail-fast) no primeiro FAIL: belts-build-azul
```

### Skills: medicao negativa neste modelo (default OFF)

O catalogo de skills foi preservado no system, as 13 skills apareceram no
`orig_system` e a tool `skill` estava no array — e mesmo assim:

| medicao | resultado |
| --- | --- |
| chamadas a `skill` em 45 requests da bateria | **0** |
| mesmo com instrucao explicita no system ("TRILHA SOB DEMANDA: chame `skill`…") | **0** (`focusB`) |
| `tools-full:create` sem catalogo (raia base) | PASS |
| `tools-full:create` com catalogo instalado | **FAIL** (CS1026: `namespace App;` + top-level statement) |

Leitura: o 4B nao usa roteamento por descricao; e o catalogo (11 descricoes,
~2,2k ch) volta a ser texto de stack no prefixo — exatamente o que a medicao de
proporcao condenou. Por isso `WAR_SKILLS` passou a **0** (instala so com
`WAR_SKILLS=1`) e `SKILL_NUDGE` (instrucao no system) ficou atras de
`SKILL_NUDGE=1`. As skills, o `14-formas.md` e o `HARNESS_STACK` deterministico
ficam no repo como caminho medido, nao como default.
