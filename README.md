# OpenCodeBridge (+ Web)

Bridge C# (.NET 9) entre o OpenCode (`POST /v1/responses`) e backends
OpenAI-compatíveis (llama.cpp, `POST /v1/chat/completions`), com UI web MVC e
harness agentivo próprio. Sem SDKs no núcleo da bridge (só BCL): a bridge
traduz Responses ⇄ chat, cuida de thinking + tool calling, casa **perfil por
sufixo do model id** e aplica as **caudas de harness** (tools e comportamento).

Linha local desta máquina: **Gemma 4** — `gemma-4-12b` (**GPU1**) e
`gemma-4-e2b` (**GPU2**, multimodal), os dois 100% em GPU (`-ngl 99`) e os dois
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
| **GPU1** | `gemma-4-12b` (`Q4_K_M`) | `:4100` | `:4124` |
| **GPU2** | `gemma-4-e2b` (`Q3_K_M`) | `:4110` | `:4125` |

> **Nota CPU/GPU2.** O sufixo **CPU** em nomes históricos
> (`llama-cpu.service`, `bridge-cpu.service`, `--cpu-max-tokens`, provider
> `llama`) **não** significa que o E2B roda em CPU: a **GPU2** roda na GPU com
> `-ngl 99`. O caminho CPU existe como **cenário de teste** para **tarefas
> granulares de execução de scripts** (`Llama GPU2-CPU Toggle`, `-ngl 0`),
> permitindo validar o executor de scripts sem disputar VRAM com a GPU1.

## Cenário de testes (resumo)

Regime padrão: **GPU1 `-c 131072 -np 1` (128k/slot)** + **GPU2 `-c 131072
-np 2` (2×65536)**, ambos `turbo3` → ~11,15–11,20 GiB usados, ~0,85 GiB livres.
É o cenário dos testes de integração: as duas linhas servindo ao mesmo tempo no
mesmo GPU (a taxa de cada uma cai ~metade sob concorrência). A GPU1 sobe
**antes** da GPU2.

```bash
./run-e2e-pipeline.sh --phase 0   # health llama/bridge + VRAM
./run-e2e-pipeline.sh --phase 1   # unit: PASS=23 FAIL=0 SKIP=3
./run-e2e-pipeline.sh --phase 2   # E-science + E-mission-tools
./run-e2e-pipeline.sh --phase 3   # standalone (opencode real, GPU1 + GPU2)
```

Aceite rápido: `/health` `ok` em `:4100`/`:4110`, `/api/status` `ok` em
`:4124`/`:4125`, `journalctl` **sem** `auto-asymmetric` e **sem** `fit params`,
`nvidia-smi` estável em ~11,2 GiB/12 GiB. `E-tools-full` exercita
read/write/edit/glob/grep/mkdir/git/`bash -n`/exec nos **dois** modelos em
paralelo.

Detalhe de contextos, units, VRAM, cenários A–E e como cortar VRAM:
**`LAB-SETUP.md`**.

## Harness — argumentos e exemplos

O harness tem **duas caudas independentes** penduradas no system:

```
system = prompt do agent + HarnessBehaviorTail (como agir) + HarnessToolsTail (como chamar a tool)
```

| argumento | efeito (breve) | exemplo |
|---|---|---|
| `--passthrough-responses true` | bypass Responses nativo ao llama (caminho OpenCode) | `--passthrough-responses true` |
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

> ⚠️ `--harness` (switch mestre) **não existe mais**: a flag é parseada por
> compatibilidade mas é inerte. Quem manda é **ter ou não cauda configurada**
> (`/api/harness` responde `true` quando há cauda). Não use `--harness 1`.

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

## Testes com outras famílias (harness legacy + modo Qwen)

Para testar **Qwen3, Qwen3.5 e DeepSeek Coder V2-Lite** na mesma bridge, os dois
argumentos abaixo são o caminho de compatibilidade — **manter 0 no Gemma 4**:

- `--legacy-harness 1` — **reativa o harness legado**: injeta o `SystemGuardrail`
  no system, habilita a coerção texto→`tool_call`, o pattern-strip, o retry e o
  clamp de `--cpu-max-tokens`. É o caminho dos modelos que dependem do wrapper.
- `--qwen-standard 1` — **modo Qwen**: sintetiza o ModelPrompt
  (`QwenStandard.ComposeSystem`) e anuncia as tools no padrão Alibaba
  (`ask_user_question` etc.), para a linha Qwen3/Qwen3.5.
- DeepSeek V2-Lite é executor textual no padrão oficial e **não tem parser
  nativo** no upstream: combine `--legacy-harness 1 --coerce-text-tools 1`.

```bash
# Qwen3 / Qwen3.5 (parser nativo + padrão Alibaba)
--legacy-harness 1 --qwen-standard 1 --passthrough-responses false

# DeepSeek Coder V2-Lite (sem parser nativo: coercao de texto ligada)
--legacy-harness 1 --coerce-text-tools 1 --passthrough-responses false
```

## Docs

- `LAB-SETUP.md` — infra: OpenCode (providers/ids/contextos/agents), units
  llama, TurboQuant, VRAM, cenários A–E, referência completa dos argumentos e
  `appsettings.json`.
- `AGENTS.md` — regras do operador.
- `AGENTS-PROMPTS.md` — system prompts versionados (fonte dos agentes).
- `HISTORY.md` — histórico de construção e medições.
- `RUNBOOK-E2E.md` — pipeline por fases e estado do ambiente.
- `PENDENCIAS.md` / `PENDINGS.md` — o que ainda não foi feito.

## English (short)

C# bridge between OpenCode (`/v1/responses`) and llama.cpp, plus an MVC web UI
with its own agentic tool loop. Local line: **Gemma 4** — `gemma-4-12b` (GPU1)
and `gemma-4-e2b` (**GPU2**, multimodal), both fully on GPU (`-ngl 99`) and both
using TurboQuant (`turbo3` KV, `TURBO_AUTO_ASYMMETRIC=0`) inside 12 GB of VRAM.
GPU1 runs `-c 131072 -np 1` (128k/slot); GPU2 runs `-c 131072 -np 2` (2×65536).
Keep on with Gemma: `--passthrough-responses true`, `--legacy-harness 0`,
`--anti-repeat 1 --anti-repeat-window 512 --anti-repeat-retry 1`,
`--coerce-narrated 1`, `--thinking-off-roles Build,Scripter,Exec`, plus the
behavior tail (tools tail is the code default). For **Qwen3 / Qwen3.5 /
DeepSeek V2-Lite** tests use `--legacy-harness 1` and the Qwen mode
`--qwen-standard 1` (`--coerce-text-tools 1` for DeepSeek). Any "CPU" reference
to the E2B is historical: the model runs on **GPU2**; the CPU path is only the
test mode for granular script-execution tasks. Infra detail in `LAB-SETUP.md`.
