# OpenCodeBridge (+ Web)

Bridge C# (.NET 9) entre o OpenCode (`POST /v1/responses`) e backends
OpenAI-compatíveis (llama.cpp, `POST /v1/chat/completions`), com UI web MVC e
harness agentivo próprio. Sem SDKs no núcleo da bridge (só BCL): a bridge
traduz Responses ⇄ chat, cuida de thinking + tool calling, casa **perfil por
sufixo do model id** e aplica as **caudas de harness** (tools e comportamento).

Linha local desta máquina: **Gemma 4 E4B** — `Gemma4-E4B-Q4` (**GPU1**) e
`Gemma4-E4B-UD-Q3` (**GPU2**, text-only), os dois 100% em GPU (`-ngl 99`) e os dois
em **TurboQuant** (`turbo3` no KV), convivendo em **12 GB de VRAM** (RTX 3060).

## Componentes

- `Rochas.OpenCodeBridge` — tradutora Responses ⇄ chat/completions + thinking
  + tool calling; perfis por sufixo do id; subcomando `gate`.
- `Rochas.OpenCodeBridge.Web` — chat com bypass Responses + loop de tools
  nativas (`function_call` → `ToolExecutor` → `function_call_output`), agentes
  plan/build, subagente no mesmo modelo e gate de escopo.
- `Rochas.OpenCodeBridge.Test` / `.Web.Test` — suites console (exit 0/1).

## Nomenclatura e CPU

| nome | modelo | llama | bridge |
|---|---|---|---|
| **GPU1** | `Gemma4-E4B-Q4` (`Q4_K_M` + mmproj Q8_0) | `:4100` | `:4124` |
| **GPU2** | `Gemma4-E4B-UD-Q3` (`IQ3_XXS`, text-only) | `:4110` | `:4125` |

> **Nota CPU/GPU2.** O sufixo **CPU** em nomes históricos
> (`llama-cpu.service`, `bridge-cpu.service`, `--cpu-max-tokens`, provider
> `llama`) **não** significa que o E4B UD-Q3 roda em CPU: a **GPU2** roda na GPU com
> `-ngl 99`. O caminho CPU existe como **cenário de teste** para **tarefas
> granulares de execução de scripts** (`Llama GPU2-CPU Toggle`, `-ngl 0`),
> permitindo validar o executor de scripts sem disputar VRAM com a GPU1.

## Cenário de testes (resumo)

Regime padrão: **GPU1 e GPU2 com `-c 131072 -np 1` (1×128k/slot)**, ambos
`turbo3`; VRAM a medir no host (chroot sem CUDA).
É o cenário dos testes de integração: as duas linhas servindo ao mesmo tempo no
mesmo GPU (a taxa de cada uma cai ~metade sob concorrência). A GPU1 sobe
**antes** da GPU2.

```bash
./scripts/run-e2e-pipeline.sh --phase 0   # health llama/bridge + VRAM
./scripts/run-e2e-pipeline.sh --phase 1   # unit: PASS=23 FAIL=0 SKIP=3
./scripts/run-e2e-pipeline.sh --phase 2   # E-science + E-mission-tools
./scripts/run-e2e-pipeline.sh --phase 3   # standalone (opencode real, GPU1 + GPU2)
```

Aceite rápido: `/health` `ok` em `:4100`/`:4110`, `/api/status` `ok` em
`:4124`/`:4125`, `journalctl` **sem** `auto-asymmetric` e **sem** `fit params`,
`nvidia-smi` estável em ~11,2 GiB/12 GiB. `E-tools-full` exercita
read/write/edit/glob/grep/mkdir/git/`bash -n`/exec nos **dois** modelos em
paralelo.

Detalhe de contextos, units, VRAM, cenários A–E e como cortar VRAM:
**`docs/LAB-SETUP.md`**. Para rodar a GPU2 (E4B UD-Q3) em **CPU** com `-ngl 0` (toggle
`llama-gpu2-cpu-toggle.sh`, drop-in do `llama-cpu.service` e exemplos de devpipe
`--only E-devpipe`), veja **`docs/LAB-SETUP.md` — Cenário E**.

## Harness — main harness e caudas

O harness tem **dois switches independentes**: o **main harness** (system base
hardcoded em `Core/MainHarness.cs`) e as **duas caudas** (`HarnessBehaviorTail`
+ `HarnessToolsTail`). No caminho gerido (model sem sufixo) o main harness
substitui o system do cliente e dispensa as tails; no caminho opencode (sufixo
`-build`/`-plan`/...) o system do cliente continua e as tails entram como delta.

```
gerido   = MainHarness + <env>/<available_skills> do cliente
opencode = system do cliente + HarnessBehaviorTail + HarnessToolsTail
```

| argumento | efeito (breve) | exemplo |
|---|---|---|
| `--passthrough-responses true` | bypass Responses nativo ao llama (caminho OpenCode) | `--passthrough-responses true` |
| `--main-harness 0` | desliga o main harness; o system do cliente volta no caminho gerido | `--main-harness 0` |
| `--harness-tails 0` | desliga as caudas; o main harness segue ativo | `--harness-tails 0` |
| `--legacy-harness 0` | mantém desligado o harness antigo (guardrail/coerção/strip/retry) | `--legacy-harness 0` |
| `--coerce-narrated 1` | resgata `[Assistant tool call]: nome({...})` copiado em ctx longo | `--coerce-narrated 1` |
| `--coerce-text-tools 1` | coage call escrita em texto (```json / `<tool_call>`) | `--coerce-text-tools 1` |
| `--thinking-off-roles Build,Scripter,Exec` | força thinking off por **role** | `--thinking-off-roles Build,Scripter,Exec` |
| `--tool-blacklist "Plan=write,edit;MoE=write"` | barra a **chamada** por role (a tool segue anunciada) | `--tool-blacklist "Plan=write,edit,exec,install,git;sed,rm,chmod,chown,mv,cp"` |
| `--risk-ask true` | comando de risco vira **pergunta** ao usuário | `--risk-ask true --risk-ask-minutes 30` |
| `--harness-behavior-tail "<texto>"` | cauda de **comportamento** (disciplina de execução) | `--harness-behavior-tail "Cumpra o enunciado antes de responder; valide scripts com bash -n; responda em pt-BR com evidencia."` |
| `--harness-tools-tail "<texto>"` | cauda de **tools** — **não definir** (default no `Program.cs`) | — |

Exemplo de `ExecStart` Gemma 4 (GPU1), com o harness que deve estar ligado:

```bash
--passthrough-responses true --legacy-harness 0 --anti-repeat 1 \
--thinking-off-roles Build,Scripter,Exec \
--tool-blacklist "Plan=write,edit,exec,install,git;sed,rm,chmod,chown,mv,cp;MoE=…;Orch=…" \
--anti-repeat-window 512
```

> ⚠️ Use apenas `--main-harness` e `--harness-tails` (independentes). Tubo puro:
> `--main-harness 0 --harness-tails 0`. `/api/harness` expõe `main_harness` e
> `harness_tails`; não há campo agregado.

## Anti-repetição — argumentos e exemplos

A contenção observa o stream do thinking e corta loop de repetição; o retry
opcional refaz **uma** chamada com o aviso, descartando a janela repetida.

| argumento | default | efeito | exemplo |
|---|---|---|---|
| `--anti-repeat` | `true` | liga a contenção no stream | `--anti-repeat 1` |
| `--anti-repeat-window` | `256` | janela em chars comparada com o histórico (64..2048) | `--anti-repeat-window 512` |
| `--anti-repeat-retry` | `true` | 1 retry após a contenção | `--anti-repeat-retry 1` |
| `antiRepeatNotice` | texto padrão | mensagem de contenção (via `appsettings.json`) | `"antiRepeatNotice": "pare de repetir..."` |

```bash
# anti-repetição calibrada para contexto longo (Gemma 4)
--anti-repeat 1 --anti-repeat-window 512 --anti-repeat-retry 1
```

## Compatibilidade legada (Qwen3/Qwen3.5) + coerção

Qwen3/Qwen3.5 são **modelos legados**; a linha atual é **Gemma 4**. A bridge
ainda os atende pelo caminho legado — e tudo isso fica **em 0 na linha Gemma 4**:

- `--legacy-harness 1` — **reativa o harness legado**: injeta o `SystemGuardrail`
  no system, o pattern-strip, o retry e o clamp de `--cpu-max-tokens`. É também o
  **pré-requisito da coerção de texto** (`CoerceFor` só roda com legacy ligado).
- `--qwen-standard 1` — **modo legado Qwen**: sintetiza o ModelPrompt
  (`QwenStandard.ComposeSystem`) e anuncia as tools no padrão Alibaba
  (`ask_user_question` etc.). **Não usar na linha Gemma 4.**
- `--coerce-text-tools 1` — **coerção de texto→`tool_call`**: extrai a chamada
  escrita no corpo e sintetiza `tool_calls`. É o caminho para famílias com
  **parser textual** (sem parser nativo no upstream) e para modelos que não
  emitem o canal nativo.
- `--coerce-narrated 1` (default) — resgata a chamada **narrada** que o modelo
  copia do histórico: `[Assistant tool call]: nome({...})`. Vale nos três (ctx
  longo); desligue com `--coerce-narrated 0` se o modelo já emitir o canal nativo.
- `--coerce-text-tools 0 --coerce-narrated 0` — caminho **cru**, sem coerção
  nenhuma (para medir o parser nativo do upstream sem interferência).

```bash
# Legado Qwen3 / Qwen3.5 (parser nativo + padrão Alibaba + coerção)
--legacy-harness 1 --qwen-standard 1 --coerce-text-tools 1 --coerce-narrated 1 \
--passthrough-responses false

# Outra família com parser textual (sem parser nativo: coerção obrigatória)
--legacy-harness 1 --coerce-text-tools 1 --coerce-narrated 1 \
--passthrough-responses false
```

O que a coerção reconhece (`ExtractTextToolCalls`): fence ` ```json `, tags
`<tool_call>…</tool_call>`, os wrappers `<tools>`/`<function>` do template e o
formato narrado `[Assistant tool call]:`. A allowlist são as tools **anunciadas
no pedido** (catálogo `RealTools`: shell/read/grep/glob/fetch/git/subagent/write/
edit) — prosa que só *menciona* uma tool não vira chamada.

## Docs

A raiz guarda só o essencial (`README.md`, `AGENTS.md`, `HISTORY.md`, solução).
Documentação em **`docs/`** e automação em **`scripts/`** (índices em
`docs/README.md` e `scripts/README.md`).

- `docs/LAB-SETUP.md` — infra: OpenCode (providers/ids/contextos/agents), units
  llama, TurboQuant, VRAM, cenários A–E, referência completa dos argumentos e
  `appsettings.json`.
- `AGENTS.md` — regras do operador.
- `docs/AGENTS-PROMPTS.md` — system prompts versionados (fonte dos agentes).
- `HISTORY.md` — histórico de construção e medições.
- `docs/RUNBOOK-E2E.md` — pipeline por fases e estado do ambiente.
- `docs/BENCHTESTS.md` — baterias (`run-battery-dual.sh`, WAR, frontpipe).
- `docs/PENDENCIAS.md` / `docs/PENDINGS.md` — o que ainda não foi feito.

## English (short)

C# bridge between OpenCode (`/v1/responses`) and llama.cpp, plus an MVC web UI
with its own agentic tool loop. Local line: **Gemma 4 E4B** — `Gemma4-E4B-Q4` (GPU1)
and `Gemma4-E4B-UD-Q3` (**GPU2**, text-only), both fully on GPU (`-ngl 99`) and both
using TurboQuant (`turbo3` KV, `TURBO_AUTO_ASYMMETRIC=0`) inside 12 GB of VRAM.
GPU1 and GPU2 run `-c 131072 -np 1` (1×128k/slot).
Keep on with Gemma: `--passthrough-responses true`, `--legacy-harness 0`,
`--harness-tails 0`, `--anti-repeat 1 --anti-repeat-window 512 --anti-repeat-retry 1`,
`--coerce-narrated 0 --coerce-text-tools 0 --tool-strategy 0`, plus the
behavior tail (tools tail is the code default). Legacy Qwen3/Qwen3.5
compatibility remains available via `--qwen-standard 1` and `--legacy-harness 1`;
other legacy families use `--coerce-text-tools 1` as needed. Any "CPU" reference
to the E4B UD-Q3 is historical: the model runs on **GPU2**; the CPU path is only the
test mode for granular script-execution tasks. Infra detail in `LAB-SETUP.md`.
