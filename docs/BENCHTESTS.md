# BENCHTESTS — bateria 2026-10-16 (9B Q4_K_M + VL-4B Q3_K_M, RTX 3060 12GB)

Setup vivo: 9B `:4100` `-c 114688 -np 2 -ngl 99 -fa on turbo3` (57344/slot),
VL `:4110` `-c 40960 -np 2 -ngl 99` (20480/slot); bridges `:4124` (out 24k)
e `:4125` (out 8k). VRAM ~11GB/12GB com ambos no ar.

## 1. Stress lorem ipsum (stream longo)
- 4B: 4000 tok @ **22.42 t/s**, completo, sem crash.
- 9B: 13.2k tok, **22.7 t/s** sob carga dupla → **31.3 t/s** solo.
- VRAM cravada em 11108 o teste todo (KV pré-alocado, sem vazamento).
- Cliente curl 400s desistiu do 9B; o llama seguiu gerando (server ok).

## 2. Natureza stateless (~10min, 8 temas x 2)
- 9B 8/8: **37–40 t/s**, coerente.
- 4B 6/8: **58–61 t/s**, mas alucina fluente (araucária de "fruta amarela",
  "folhas largas") + **2× HTTP 500** na `:4125` sem rastro no journal do
  llama nem no log da bridge (só sonda `anti-repeat`). Causa raiz aberta.
- Ressalva: perguntas avulsas sem histórico (`in≈25` sempre) — sem subida
  de ctx de verdade.

## 3. ctx80 com histórico (matemática/estatística/QM + 5 armadilhas)
### 3.1 thinking OFF (perfil build)
- 9B (24 rounds, in 76→16374): t/s **26.3→13.7** (KV). Respostas longas e
  rigorosas; armadilhas triângulo esférico, p-valor e Bell-Everett
  **corrigidas com rigor, 0 alucinação**.
- 4B-VL (26 rounds, in 202→16126): t/s 28.6→13.0 porém **100% lixo**:
  `toolcall toolchain toolchain…` até o teto em TODOS os rounds.
### 3.2 thinking ON (perfil build)
- 9B (29 rounds, in 74→932): ~38 t/s plano, 1 stall (6.1 t/s), mas
  **content vazio** — 700 tokens somem no reasoning, 0 resposta.
- 4B-VL (40 rounds, in 202→1951): respostas curtas/pedantes (12–32 tok),
  sem crescimento de ctx (alvo 16k inalcançado).

## 4. Achado: parâmetro tool chega ao VL em teste de diálogo
O operador lembrava de "mecanismo que envia parâmetro tool diferente aos
modelos VL" — **procede**. No `Passthrough` (chat), id de modelo SEM sufixo
de perfil (`qwen3-vl-4b`) não é reconhecido como opencode (`IsOpencodeModel`
=false) e, com `Harness=true`, o **system recebe a fórmula**
(`WithFormula`: system + `HarnessToolsTail` = "MODO DE USO DAS TOOLS: uma
tool call por resposta…"). A bateria usou `qwen3-vl-4b` (sem sufixo) + um
SystemPrompt acadêmico — ou seja, todo round do VL levou instrução de tool
junto. O 9B usou `qwen3.5-9b-build` (com sufixo → tubo limpo).
Correlação com a degeneração `toolchain`: o tail empurra o VL p/ o canal de
tool-template; somado a thinking OFF + histórico longo, o Q3 colapsa em
loop. Single-shot curto não reproduz (só viés leve), então é gatilho
combinado, não causa única.
**Recomendação: testes só-diálogo devem usar id COM sufixo**
(`qwen3-vl-4b-build`) = tubo limpo, sem tail. Não alterar o roteamento da
bridge sem ordem (comportamento de produto p/ a Web).

## 5. Curvas e limites honestos
- t/s cai ~50% até 16k de ctx (decode O(n) no KV). Pico solo 31–38 t/s;
  dupla simultânea ~22–23 t/s cada.
- Com 2 slots o ctx DIVIDE: 9B 57344/slot, VL 20480/slot. "80% do total"
  (45k/16k) só vale por slot — 80% de 112k não cabe num slot.
- Compactação automática é do **opencode** (cliente); llama faz ctx-shift
  silencioso; bridge não limitava (agora há `context-guard` 95% → 413).
- VRAM com ambos: 11.0–11.6GB/12GB (90–95%). Margem mínima.

## 6. Pendências do operador
- Thinking por perfil/modelo (VL precisa ON p/ não degenerar; 9B rende
  melhor OFF p/ agir).
- Deploy do binário novo nas units (`context-guard`, JWT, thinking-perfil
  no chat) — implementado e commitado, não deployado.
- Push ao GitHub aguardando key.
- Re-rodar ctx80 no VL com id sufixado + SystemPrompt neutro p/ isolar o
  efeito do tail (previsto, não executado).

## 7. HTTP 500 intermitente do VL — causa raiz (2026-10-16 14h)
Re-teste das requests que falharam (nature r1/r5): 4/6 OK, 2/6 500.
Corpo do 500: `{"code":500,"message":"The model produced output that
does not match the expected peg-native format"}`. Log do llama:
`W common_chat_peg_parse: unparsed peg-native output` + `got exception`
→ **é o próprio llama-server que derruba**: a saída crua do Qwen3-VL-Q3
(vazamento do template de tool no meio do chat) não passa no parser
PEG/jinja do template de chat do llama.cpp na extração do
`message.content`. Intermitente por amostragem: quando o VL formata
limpo, 200; quando vaza template, 500.
Não é anti-repeat da bridge, nem prompt, nem ctx. Mitigações possíveis
(não aplicadas): retry da bridge nesse 500 específico (reamostragem
costuma sair limpa); trocar o template de chat do VL no llama;
parâmetros que reduzam o vazamento de template.

## 8. Bateria dual (puro + opencode) e guardas de tools — 2026-10-24
Origem: análise de duas sessões reais (`an-lise-falha-uso-de-ferramentas-em-opencode-sqlite.json`
e `greeting-and-quick-check-in.json`) com 18 falhas em 30 chamadas de tool. Causas medidas
e o que passou a existir no código:

| Causa medida | Guarda implementada | Onde |
|---|---|---|
| role `-build` rodando em temp 0.5 (o cliente reenviava a temp do modelo base) → identificador corrompido (`proj-teste-001`→`999`, `opencodebridge`→`opencodedbidge`) | perfil do role vence a temp do request; `--request-temp-wins` restaura o antigo; `temperature_source` exposto em `/api/status`, `/api/translate` e no log `response` | `Program.cs` (`BuildChatRequest`, `ForwardResponses`, `Passthrough`) |
| loop de 10 tentativas no mesmo alvo com nomes mutados (anti-repeat textual não pega: cada iteração é texto novo) | `ToolLoopBreaker` (Core): lê `function_call`+resultado do próprio request, normaliza alvo (remove dígitos/separadores), 3 falhas = aviso no último resultado, 6 = curto-circuito sem chamar o modelo (`--breaker-soft/--breaker-hard`) | `Core/ToolLoopBreaker.cs` |
| glob com alternância (`a\|b`), heredoc, `\n` literal, `cd` em nome não criado, build simulado, `sudo` em PROD, `read` em `.db` | help curado por tool no schema (`Core/ToolHelp.cs`) + tail transversal em `HarnessTails.DefaultTools` (caminho absoluto copiado de `ls`, probe antes do retry, glob sem `\|`, sem heredoc, sem simulação, sem `sudo`, `git init` antes do commit) | `Core/ToolHelp.cs`, `Core/HarnessTails.cs` |
| `\n` de 2 caracteres no comando shell → "here-document delimitado pelo fim do arquivo" | `FixLiteralEscapes`: vira quebra real quando o comando não tem nenhuma quebra; independe de `ToolStrategy` | `Program.cs` |
| caudas inertes no caminho opencode (`--harness-tails 0`) | `--tail-scope managed\|all\|off` (default `managed`); `all` injeta as caudas nos DOIS caminhos; `/api/harness` reporta `tails_managed`, `tails_opencode` e o efetivo | `Program.cs` + `appsettings.json` |

### Configuração em produção
`Rochas.OpenCodeBridge/appsettings.json` (versionado, copiado para o output) carrega
`tailScope: all`, `profileTempWins: true`, `breakerSoft: 3`, `breakerHard: 6` e os seis
perfis. As caudas de texto moram só em `HarnessTails.cs` — não sobrescrever
`harnessBehaviorTail`/`harnessToolsTail` no appsettings (o parágrafo que existia só no PROD
foi trazido para o código).

### Testes
- **`--dual`**: `E-dual-tools` roda a matriz (write/read/edit, glob+grep, ciclo git, probe
  obrigatório, script com erro de sintaxe sem simulação, SQLite via python3) em dois
  transportes — `pure` (HTTP `/v1/responses` + `/api/tools/execute`) e `opencode`
  (`opencode run --standalone`) — com sufixo `-pure`/`-opencode` e checagem de ARTEFATO em
  disco, não do texto do modelo.
- **`--standalone-provider openai|llama`** escolhe o provider do opencode (Q4 :4124 x Q3 :4125).
- **Unit novos**: `U-breaker-*` (streak, alvo mutado, trip, alvos distintos, normalização,
  marcadores pt/en), `U-toolargs-literal-newline`, `U-tails-path-simulation-rules`,
  `U-translate-profile-temp-wins`, `U-translate-breaker-verdicts`.

### Bateria
`./scripts/run-battery-dual.sh` (background: `nohup ./scripts/run-battery-dual.sh > /tmp/opencode/battery.out 2>&1 &`)
roda Q4 e Q3 em paralelo; por modelo: unit → tools-dual → tópicos → tools-full/responses/devpipe/
mitigação → belts → 1 pipe puro → opencode implícito (`--standalone`) → senior/enterprise/front/
scripter/orch/arquitetura. Logs e `SUMMARY.txt` em `/tmp/opencode/battery/<stamp>/`.

## 9. Bateria WAR v2 (3 sessões simultâneas, A/B de flags, retomada)

Objetivo: provar cada recurso do harness **na marra** (falha sem o arg → funciona com o arg) e
estressar a GPU com até 3 sessões simultâneas, com evidência por step.

```bash
# 3 sessões: Q4 (GPU1) + 2 slots do Q3 (GPU2)
./scripts/war/run-war.sh --fresh
# somente Q4 (uma sessão, sem lanes Q3; pula o teste explícito da ponte CPU)
./scripts/war/run-war.sh --q4 --fresh
# somente Q3 (duas sessões no llama :4110)
./scripts/war/run-war.sh --q3 --fresh
# retomar de onde parou (default) / inspecionar plano e concluídos
./scripts/war/run-war.sh
./scripts/war/run-war.sh --list
```

- **Lanes e bridges**: `q4` → `:4134`→llama `:4100`; `q3a` → `:4135`→`:4110`; `q3b` →
  `:4136`→`:4110` (slots `-np 2`). Cada lane tem bridge e log próprios, então o delta de log
  por step é limpo mesmo com as 3 rodando juntas. `WAR_CPU_BRIDGE` aponta a ponte CPU que o
  teste deve usar (Q3 da lane ou a prod `:4125` no modo Q4).
- **Fila aleatória reproduzível**: `WAR_SEED=42`. O plano fica em `<out>/plan.tsv`.
- **Checkpoint**: `<out>/state/<lane>/<teste>.done`; retomar pula o que já passou (inclusive
  após horas de GPU). `--fresh` zera. Mudar de lanes exige `--fresh`/outro `WAR_OUT`
  (`<out>/lanes.mode` trava o modo).
- **Evidência por step** em `<out>/steps/<lane>-<teste>.*`: log do teste, delta do log da
  bridge (harness/sonda/breaker/coerce/tool-ask/context-guard), `/api/metrics` e
  `/api/harness` antes/depois, `llamacpp:*` do `/metrics` antes/depois e `nvidia-smi`
  antes/depois. Amostrador contínuo em `<out>/gpu-samples.csv`; parciais em `summary.tsv`,
  `summary.txt` e `status.json` (atualizados a cada step).
- **Preflight A/B (stub, sem GPU)**: `E-flag-context-guard`, `E-flag-anti-repeat`,
  `E-flag-coerce-narrated`, `E-flag-tail-scope`. Cada um roda o cenário negativo e o positivo
  contra um upstream stub determinístico; falha se o arg não for o responsável pela diferença.
- **Via dupla**: `E-dual-tools` roda com `--dual` (pure + opencode); `E-standalone-*` com
  `--standalone`.

### Frontpipe sem webdriver

`E-front-*` avalia a skill de criação/edição **direto nos artefatos** (Razor `.cshtml`, JS/TSX e
CSS) por padrão: a sonda estática parseia o CSS e computa os mesmos valores do
`getComputedStyle` (`#titulo`, `#quadro`, `rect_w/rect_h` com `box-sizing`), confere links/DOM e
estrutura TSX (`createRoot`). O caminho de navegador fica atrás de `--front-webdriver`
(Playwright via Web.Test `--front-measure`), com URL configurável por `FRONT_WEBDRIVER_URL` e
`HEADED=1` para o modo visível — gancho pronto para a Web UI `:4130` quando o parâmetro headed
geral for criado.

### Fronteira Core x consumidores

A suíte de testes é **consumidora** do Core, igual à Web UI, OpenCode e CLI.
Ela não define catálogo/schema de tools, não executa tools por conta própria e
não converte protocolo na mão. O caminho único é:

- `Rochas.OpenCodeBridge.Core.ToolCatalog.Chat/Responses` para o schema;
- `POST /api/tools/execute` (ou `BridgeToolExecutor`) para executar;
- `Core.StackPreflight` para preflight de stack;
- `Core.HarnessTails`/`MainHarness` para base e tails.

No projeto de testes ficam apenas orquestração, enunciado, fixture, transporte
HTTP, assertivas e evidência. Catálogo local (`Tools()`/`ToolCatalog()`),
executor local (`Exec()`/`LocalExec()`), `Shell()` de tool, nomes paralelos
(`read_file`/`write_file`/`edit_file`/`list_dir`) ou conversão manual
chat→Responses são proibidos. Se o teste precisa de outra tool, a tool entra no
Core e todos os consumidores passam a usá-la.

A bateria frontend (`E-front-*`) segue a mesma regra: Gen/Edit/Verify usam o
catálogo do Core e `POST /api/tools/execute`; os enunciados editam peças reais
de MVC Razor e React, não um vocabulário próprio do teste.

## 10. Disposição canônica dos LEVELS (L1 → L4)

Ordem obrigatória da esteira WAR, válida para `WarTemplate` (`--war-level`)
e para `scripts/war/run-levels.sh` (`--level`). Cada nível roda sozinho; os
steps de um nível podem ser filtrados por `--war-only`; um teste dentro do
step pode ser filtrado por `--only`.

| Level | Ordem interna |
|---|---|
| **L1** | `units` (uma vez, bridge) → `integração` → `tópicos` |
| **L2** | `E-responses-tools` → `E-tools-full` → belts **azul e verde** (build + orch) |
| **L3** | `E-science-gpu` → `E-mission-tools` → belt **roxa** (build + orch) |
| **L4** | `E-mission-algo` → belts **marrom e preta** (build + orch) → frontend `E-front-*` → `webui-integracao-topicos` |

- **L1 integração**: `E-stream-architect`, `E-review-architect`,
  `E-iot-event-queue`, `E-error-mitigation`,
  `E-full-cycle-iot-queue-push`, `E-healthy-concurrency`.
- **L1 tópicos**: `E-senior-react`, `E-senior-bus`, `E-senior-erp`,
  `E-senior-arm`, `E-ent-crud-product`, `E-ent-crud-customer`,
  `E-ent-crud-saleinvoice`, `E-ent-worker`, `E-ent-garnet`, `E-ent-dashboard`.
- **L4 frontend/web**: `E-front-gen`, `E-front-edit` (CSS),
  `E-front-edit-razor`, `E-front-edit-react`, `E-front-verify` e
  `webui-integracao-topicos` (Web.Test).

### Rodar um nível sozinho

```bash
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- \
  --war-level 1
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- \
  --war-level 4
```

### Rodar um step do nível

`--war-only` aceita substring ou lista separada por vírgula:

```bash
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- \
  --war-level 1 --war-only integration
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- \
  --war-level 4 --war-only belts-build-final,front
```

### Rodar um teste individual

O step é só o agrupamento; o teste específico é `--only` no runner:

```bash
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- \
  --only E-front-edit-react --mode bridge
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- \
  --only E-senior-erp --mode bridge
```

### Scripts e trilha completa

```bash
./scripts/war/run-levels.sh --level 3
./scripts/war/run-all-levels.sh --levels 1,2
./scripts/war/run-all-levels.sh --from 3   # 3 -> 4
```

`--war-all` e `run-all-levels.sh` sem argumentos percorrem **1 → 2 → 3 → 4**,
parando no primeiro gate vermelho. O modo padrão é **bridge**; para a raia
opencode use `--war-modes opencode` ou `LEVEL_MODES=opencode`. Fail-fast é o
padrão e pode ser declarado explicitamente com `--war-stop-on-fail`.

> **`export PORT=<porta livre>`** antes dos blocos abaixo. A bridge é **de bancada**:
> descartável, criada e morta na execução. Ela **não** entra em unit, firewall, `opencode.json`
> nem em agente — o ambiente só conhece os serviços predefinidos (retrato em `LAB-SETUP.md` §12).
> A bridge de bancada pode usar `--main-harness 1` para testar o modo
> bridge e a Web UI; as units OpenCode `:4124/:4125/:4126` ficam em `0`.

```bash
# bridge própria (para testar o build do repo, não o binário de produção)
cd Rochas.OpenCodeBridge/bin/Release/net10.0
dotnet Rochas.OpenCodeBridge.dll --port $PORT --upstream http://127.0.0.1:4100 \
  --model Gemma4-E4B-Q4 --thinking events --temperature 0.3 --max-tokens 16384 \
  --context-guard 1 --context-tokens 131072 --api-key guest@123 \
  --passthrough-responses true --main-harness 1 --harness-tails 1 \
  --anti-repeat 1 --anti-repeat-retry 1 --anti-repeat-window 512 \
  --log logs/bridge-q4-level1.log &

LEVEL_BRIDGE=http://127.0.0.1:$PORT LEVEL_BLOG=logs/bridge-q4-level1.log \
  ./scripts/war/run-levels.sh --level 1
```

Artefatos: `logs/levels/L1-<stamp>/{summary.tsv,steps/,state/}` (disco da sda3); retomada pelo
`STATE` do mesmo stamp (`LEVEL_STAMP=<stamp>`). Sem ordem fixa de faixas: `--belts azul,verde`
filtra.

### Raia Q3 (opcional, texto-only)

A `:4110` roda o UD-Q3 **sem mmproj**, então os testes de imagem (`E-devpipe-vision`,
`E-devpipe-doc`, `E-devpipe-vision-tool`) não têm como passar. O runner trata isso por
ambiente, sem tocar no catálogo:

```bash
# bridge Q3 dedicada (harness off, mesmo perfil da :4125)
dotnet Rochas.OpenCodeBridge.dll --port $PORT --upstream http://127.0.0.1:4110 \
  --model Gemma4-E4B-UD-Q3 --thinking events --temperature 0.3 --max-tokens 16384 \
  --context-guard 1 --context-tokens 65536 --api-key guest@123 \
  --passthrough-responses true --main-harness 1 --harness-tails 1 \
  --anti-repeat 1 --anti-repeat-retry 1 --anti-repeat-window 512 \
  --log logs/bridge-q3-level1.log &

LEVEL_BRIDGE=http://127.0.0.1:$PORT LEVEL_UPSTREAM=http://127.0.0.1:4110 \
LEVEL_BLOG=logs/bridge-q3-level1.log ./scripts/war/run-levels.sh --level 1
```

**Os dois caminhos de teste** (o rótulo `pure` foi aposentado por ambíguo):

- **`--mode bridge`** = caminho com **bridge harness main/tails**: o cliente fala HTTP
  (`/v1/responses`) e executa as tools por `/api/tools/execute`; quem monta o turno é a
  bridge (`main-harness` + `harness-tails` ligados, como no caminho gerido e na Web
  in-process). É o caminho que serve para medir o produto.
- **`--mode opencode`** = caminho **livre para o opencode**: o cliente entra com o próprio
  master/harness (`opencode run --standalone`) e a bridge só transporta.

Bridge sem harness e sem opencode plugado não é cenário de uso: não mede nada e só gera
mitigação de granularidade. `--mode pure` continua aceito como **alias** de `bridge`.

Duas variáveis mandam na raia: **`LEVEL_UPSTREAM`** vira `WAR_UPSTREAM` (o assert
`U-status` compara o `upstream` do `/api/status` com esse valor, em vez do `:4100` fixo) e
**`WAR_NO_VISION`** marca upstream sem visão — o runner liga `1` automaticamente quando
`LEVEL_UPSTREAM` termina em `:4110`, e `LEVEL_NO_VISION=0` força a execução dos testes de
imagem. Sem essas variáveis o comportamento é o de antes (`:4100`, visão ligada).

### L2 — tools + belts azul/verde

`LEVEL_BRIDGE=http://127.0.0.1:$PORT ./scripts/war/run-levels.sh --level 2`

Roda, em `--mode bridge` (default), nesta ordem:
`E-responses-tools` → `E-tools-full` → `belts-build` azul+verde →
`belts-orch` azul+verde. As duas trilhas de belt são
`E-belts-build-direct` (build implementa direto) e `E-belts-direct`
(orch segmenta → Scripter → Exec). Não roda science, mission nem frontend.

### L3 — science + mission tools + roxa

`LEVEL_BRIDGE=http://127.0.0.1:$PORT ./scripts/war/run-levels.sh --level 3`

Roda, em `--mode bridge` (default), nesta ordem:
`E-science-gpu` → `E-mission-tools` → `belts-build` roxa →
`belts-orch` roxa. A linha `E-science-cpu` não entra neste nível; roda
isolada com `--only E-science-cpu` quando a Q3 estiver dedicada.

### L4 — mission algo + marrom/preta + web UI

`LEVEL_BRIDGE=http://127.0.0.1:$PORT ./scripts/war/run-levels.sh --level 4`

Roda, em `--mode bridge` (default), nesta ordem:
`E-mission-algo` → `belts-build` marrom+preta →
`belts-orch` marrom+preta → `E-front-*` (Gen/Edit CSS/Razor/React/Verify)
→ `webui-integracao-topicos` (Web.Test: integração + tópicos).

L3 e L4 exigem **harness ligado** na bridge; o runner consulta `/api/harness`
e loga aviso se `main_harness` não estiver `true`:

```bash
cd Rochas.OpenCodeBridge/bin/Release/net10.0
dotnet Rochas.OpenCodeBridge.dll --port $PORT --upstream http://127.0.0.1:4100 \
  --model Gemma4-E4B-Q4 --thinking events --temperature 0.3 --max-tokens 16384 \
  --context-guard 1 --context-tokens 131072 --api-key guest@123 \
  --passthrough-responses true --main-harness 1 --harness-tails 1 \
  --anti-repeat 1 --anti-repeat-retry 1 --anti-repeat-window 512 \
  --log logs/bridge-q4-harness.log &
```

## 11. Auto-gestão da bateria (supervisor orch)

Objetivo: o **próprio modelo** gerencia a bateria E2E sobre instâncias de si mesmo,
por subagentes, com segmentação compatível com a capacidade do alvo. Absorve o antigo
`DOC-EVOLUCAO-AUTO-TESTE.md` (que citava 12B/E2B — modelos aposentados; hoje os alvos
são **Gemma4 E4B Q4** na GPU1 e **UD-Q3** na GPU2).

```
Supervisor (orch Q4)  ── lê o catálogo E-* e GERA a fila de micro-enunciados
   ├── Runner build (Q4, GPU1)   ── micro-tarefas de código multi-arquivo
   ├── Runner exec  (Q3, GPU2)   ── executa/valida scripts e micro-tarefas simples
   └── Aceite (suíte)            ── PASS/FAIL por artefato; retroalimenta o supervisor
```

- **Micro-enunciado** (formato canônico em `../AGENTS.md`): `OBJETIVO` (1 frase) +
  `ESCOPO` (arquivos exatos) + `PROIBIDO` (o que não tocar) + `ACEITE` (comando exato).
  Um micro por chamada `task`; ao subagente volta só o **erro** (trecho), não o log inteiro.
- **Supervisor não improvisa**: a fila sai do catálogo `E-*`; ele só ordena, segmenta
  e re-despacha. Micro que estoura ctx/ambiguidade é **refatiado em dois**.
- **Aceite por artefato** (arquivo/log/número), nunca "parece bom"; o supervisor
  re-executa o ACEITE de cada micro antes de avançar (≤2 tentativas por micro).

| faixa | escopo | alvo | aceite (artefato) |
|---|---|---|---|
| **azul** | aquecimento (1 tool) | Q3 exec | saída de tool no texto |
| **verde** | domínio + SQL (CRUD) | Q4 build | DDL + entidades compilam |
| **roxa** | + repositórios/serviços | Q4 build | `dotnet build` verde |
| **marrom** | + API + React + Docker | Q4 build | build + `compose config` |
| **preta** | full stack + CI | Q4 build + Q3 exec | build + teste do ACEITE |

Reprodução sem supervisor (execução manual da mesma fila):

```bash
# trilha build, faixas azul+verde, modo puro
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- \
  --bridge http://127.0.0.1:$PORT --only E-belts-build-direct --belts azul,verde --mode pure
# fila completa por nível
LEVEL_BRIDGE=http://127.0.0.1:$PORT ./scripts/war/run-levels.sh --level 2
```

## 12. Coding journey / stream history (2026-10-26)

Objetivo: registrar a **jornada completa de codificação** de cada step, nos dois
transportes, para comparar o comportamento do modelo local (bridge) com o do
opencode e alimentar histogramas de adaptatividade. O número de calls do opencode
não aparecia no log porque o CLI executa as tools fora da bridge; o export nativo
resolve isso sem inventar métrica.

### 12.1 Contrato e artefatos

O driver exporta `WAR_JOURNEY_DIR=<run>/journey` para o processo do teste. A sonda
(`StepProbe`) recebe o mesmo diretório e publica a análise no fim do step (e no
tick quando o arquivo muda).

| Artefato | Quem grava | Conteúdo |
|---|---|---|
| `<run>/journey/<step>.opencode.raw.json` | `TestContext.OpencodeRunDetailed` | alias do último `opencode session export`; é o que a sonda lê. |
| `<run>/journey/<step>.opencode.<session>.raw.json` | idem | export completo por sessão (retries/loop não se perdem): info da sessão (modelo/agent/tokens/custo), 100% das mensagens, reasoning, tool calls, inputs e resultados. |
| `<run>/journey/<step>.opencode.meta.json` | idem | step, agent, model, cwd, prompt, sessões criadas, stdout/stderr, `elapsed_ms`, caminho do export/resume. |
| `<run>/journey/<step>.resume.sh` | idem | importa o export e retoma com `opencode run --session <id> --agent <agent> --model <model>`. |
| `<run>/journey/harness/` | idem | cópia do harness primordial (`docs/harness/opencode/system.md`, `tools.json`, `req-chat.json`, `MAIN-HARNESS.md`). |
| `<run>/journey/<step>.bridge.json` | `TestContext.ToolAgent` | jornada normalizada do caminho bridge: prompt, texto final, calls com `round`, `args`, `ok`, `output`, `ms`. |
| `<run>/probes/<step>.journey.json` | `StepProbe` | histogramas e métricas de cada raia + `contrast` quando as duas rodam no mesmo step (`--dual`). |
| `<run>/journey/<step>.histograms.json` | `StepProbe` | cópia dos histogramas dentro do bundle de stream history (raw/meta/resume/harness). |
| `POST /api/test/probe` | `StepProbe` | amostra `{kind:"journey", journey:{...}}`; o painel/API já lê a série. |

O export opencode é feito **antes** de `OpencodeSessionsCleanup`: a sessão pode
ser apagada do storage sem perder o stream. O bundle é local; não versionar raw
(sem `WAR_JOURNEY_DIR`, unit/manual não grava nada).

### 12.2 Histogramas e métricas

Os dois analisadores (`CodingJourney.AnalyzeOpencode` e `AnalyzeBridge`) produzem
o mesmo formato, para comparação direta:

- `tool_histogram` — chamadas por tool (`shell`, `edit`, `write`, `read`, `grep`,
  `glob`, `git`, ...).
- `status_histogram` — `ok` x `fail`.
- `sequence_histogram` — pares consecutivos (`read>edit`, `edit>shell`,
  `shell>shell`, ...); revela o “caminho” de codificação.
- `turn_tools_histogram` — tools por turno/round (`0`, `1`, `2`, `3`, `4+`).
- `text_chars_histogram` / `reasoning_chars_histogram` — tamanho do texto e do
  raciocínio por turno (buckets `0`, `1-200`, `201-1k`, `1k-4k`, `4k-12k`, `12k+`).
- `file_histogram` — arquivos mais tocados.
- `validation_histogram` — `bash-n`, `dotnet-build`, `dotnet-test`, `git-diff`,
  `git-status`, `git-log`, `git-commit`, `npm-test`, `tsc`, `eslint`, ...
- `metrics` — `avg_tools_per_turn`, `tool_diversity`, `edit_ratio`,
  `validation_per_edit`, `failure_rate`, `reasoning_ratio`.
- `tokens` e `elapsed_ms` — custo/velocidade do stream (quando disponíveis).

### 12.3 Baseline opencode (sessão real de 2026-10-26)

Export de 5,3 MB / 828 mensagens / 676 turnos do assistente, analisado pelo
próprio `CodingJourney.AnalyzeOpencode` (não por script externo):

| Métrica | Valor |
|---|---|
| tools total | 689 |
| `shell` | 558 |
| `edit` | 120 |
| `read` / `write` | 2 / 2 |
| `grep` | 1 |
| `websearch` / `webfetch` | 3 / 3 |
| validações | 31 `dotnet-build`, 18 `git-commit`, 16 `git-status`, 13 `git-log`, 9 `git-diff`, 2 `bash-n` |
| `avg_tools_per_turn` | 1,02 |
| `tool_diversity` | 7 |
| `edit_ratio` | 0,984 |
| `validation_per_edit` | 0,74 |
| `failure_rate` | 0,006 |
| `reasoning_ratio` | 33,02 |

Leitura: o opencode desta sessão é **shell-first** (81% shell), edita muito e
lê pouco (`edit_ratio` 0,984 com só 2 `read`), valida o build com frequência
(31 `dotnet-build`), mas usa `bash -n` raramente (2). É exatamente o tipo de
desvio que o comparativo com a bridge deve expor: se o modelo local gasta
turnos em `read`/`write` ou não valida, o histograma mostra; a correção é no
enunciado/tail, não no assert.

### 12.4 Leitura e uso na adaptação

```bash
# último journey de um step
jq . /path/to/run/probes/tools-full-opencode.journey.json

# comparar as duas raias no mesmo run (--dual)
jq '{oc:.opencode.metrics, br:.bridge.metrics, delta:.contrast.tool_delta}' \
  /path/to/run/probes/<step>.journey.json

# top tools e sequências de um export opencode
jq '{tools:.tool_histogram, seq:.sequence_histogram, files:.file_histogram}' \
  /path/to/run/probes/<step>.journey.json
```

Ciclo de adaptação: rodar o step nos dois modos → comparar histogramas →
identificar desvio (ex.: local faz `write` onde o opencode faz `read>edit`; local
não valida com `bash -n`/`dotnet build`; local repete `shell>shell` sem progresso)
→ ajustar **enunciado/tail/harness** no ponto exato → repetir e medir o delta.
O aceite continua sendo o artefato/build/teste do step; histograma é diagnóstico,
nunca PASS.

### 12.5 Retenção e privacidade

O raw contém código e caminhos do workspace. Guardar só em `/tmp`/disco local,
nunca commitar; o bundle é recriado a cada WAR. Para auditoria longa, copiar
apenas `*.journey.json` e `*.opencode.meta.json` (sem raw) ou usar
`opencode session export --sanitize`.

## Testes cognitivos puros (2026-10-28)

- `E-tools-full` e' exclusivamente cognitivo: o modelo recebe um enunciado
  natural e executa as tools via `ToolAgent` + `Core.ToolCatalog`; a suite
  valida o artefato em disco. Nao existe mais execucao direta de tools no
  teste (`RunOnBridge` removido) — o objetivo e a capacidade cognitiva do
  modelo, nao o Core (que tem seus proprios testes).
- `E-ent-redis` (ex-`E-ent-garnet`): termo Garnet -> Redis, enunciado pede
  gravacao via tool `write` em `ProductRepository.cs`; a suite le o arquivo e
  aplica `CheckNoStub` + `IDistributedCache`/`RemoveAsync`. Renomeado apos
  PASS=1 FAIL=0.
- Enunciados agentivos sem coordenadas (`oldString=...`/`newString=...`):
  objetivo + aceite, o modelo decide as tools.
