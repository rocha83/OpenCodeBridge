# LAB-SETUP — infra da linha Gemma 4 (12 GB, TurboQuant)

Detalhes de infraestrutura, configuração dos servidores llama, contextos, VRAM,
cenários de teste e referência completa de argumentos da bridge. O resumo
expresso (componente, cenário de teste e harness) fica no `README.md`.

## 0. Nomenclatura

| nome | o que é | endpoint |
|---|---|---|
| **GPU1** | `gemma-4-12b` (denso, `Q4_K_M`) — 100% GPU | llama `:4100` / bridge `:4124` |
| **GPU2** | `gemma-4-e2b` (multimodal, `Q3_K_M`) — 100% GPU | llama `:4110` / bridge `:4125` |

Os dois rodam em **GPU full** (`-ngl 99`) e **TurboQuant** (`turbo3` no KV),
convivendo em 12 GB de VRAM (RTX 3060, 12288 MiB).

> **Nota CPU/GPU2.** O sufixo **CPU** que aparece em nomes históricos —
> `llama-cpu.service`, `bridge-cpu.service`, `--cpu-max-tokens`, provider
> `llama` — **não** significa que o E2B roda em CPU. O E2B é **GPU2**: roda na
> GPU com `-ngl 99`. O caminho CPU existe apenas como **cenário de teste** para
> tarefas granulares de execução de scripts (`Llama GPU2-CPU Toggle`, `-ngl 0`,
> `-t/-tb`), para medir/validar o executor de scripts sem disputar VRAM com a
> GPU1. Os nomes foram mantidos por compatibilidade.

---

## 1. OpenCode (cliente) — configuração detalhada

O OpenCode fala **Responses nativo** com a bridge (caminho bypass). A bridge só
remove itens `reasoning` e funde `instructions`/system no inicial
(`NormalizeSystemFirst` + `StripReasoningItems`).

### 1.1 Providers

`~/.config/opencode/opencode.json` — dois providers, mesma stack llama.cpp
atrás de duas bridges:

| provider | bridge | llama | papel |
|---|---|---|---|
| `openai` | `http://127.0.0.1:4124/v1` | `:4100` | GPU1 `gemma-4-12b` (raciocínio/tools) |
| `llama` | `http://127.0.0.1:4125/v1` | `:4110` | GPU2 `gemma-4-e2b` (execução/subagente) |

```json
{
  "providers": {
    "openai": {
      "api": "openai-chat",
      "settings": { "baseURL": "http://127.0.0.1:4124/v1", "apiKey": "guest@123" },
      "models": {
        "gemma-4-12b": {
          "name": "Gemma4 12B Q4 GPU",
          "tool_call": true,
          "compatibility": { "reasoningField": "reasoning_content" },
          "limit": { "context": 131072, "output": 16384 },
          "temperature": 0.5
        }
      }
    },
    "llama": {
      "api": "openai-chat",
      "settings": { "baseURL": "http://127.0.0.1:4125/v1", "apiKey": "guest@123" },
      "options": { "timeout": 1800000, "headerTimeout": 900000, "chunkTimeout": 900000 },
      "models": {
        "gemma-4-e2b": {
          "name": "Gemma4 E2B Q3 GPU2",
          "tool_call": true,
          "compatibility": { "reasoningField": "reasoning_content" },
          "limit": { "context": 65536, "output": 8192 },
          "temperature": 0.5
        }
      }
    }
  }
}
```

Pontos que **quebram** a linha se ficarem errados:

- `tool_call: true` e `compatibility.reasoningField: reasoning_content` são
  obrigatórios (o Gemma 4 emite thinking em `reasoning_content`).
- `limit.output` espelha o `--max-tokens` da bridge: **16384** (GPU1) e
  **8192** (GPU2). Sem isso o cliente entrega prompts de saída maiores do que o
  llama serve e o `Decode error (200 POST /v1/responses)` reaparece.
- `limit.context` é **por requisição/slot**, não a soma dos slots. A **GPU2**
  roda `-np 2` ⇒ cada slot tem **65536**; logo o cliente declara **65536**, não
  131072. A **GPU1** roda `-np 1` ⇒ **131072**.
- O provider `llama` mantém `timeout`/`headerTimeout`/`chunkTimeout` longos
  (30/15/15 min) porque a GPU2 divide a GPU com a GPU1 e o ctx cheio é lento.

### 1.2 Modelos, perfis e contextos

O perfil (temperatura + thinking) é casado pelo **sufixo do model id**
(`TryProfile`/`RoleOfModel` em `Program.cs`). Id **sem sufixo** cai no global da
unit (`--temperature`/`--thinking`); id com sufixo **vence**.

| id | temp | thinking | papel |
|---|---|---|---|
| `gemma-4-12b` | 0.5 | events | global GPU1 |
| `gemma-4-12b-plan` | 0.6 | events | planeja |
| `gemma-4-12b-build` | 0.3 | off | executa |
| `gemma-4-12b-orch` | 0.6 | events | orquestra/delega |
| `gemma-4-12b-moe` | 0.8 | events | explora/generalista |
| `gemma-4-12b-scripter` | 0.3 | off | compõe scripts |
| `gemma-4-e2b` | 0.5 | events | global GPU2 |
| `gemma-4-e2b-plan` | 0.6 | events | planeja |
| `gemma-4-e2b-build` | 0.3 | off | executa |
| `gemma-4-e2b-moe` | 0.8 | events | explora |
| `gemma-4-e2b-scripter` | 0.3 | off | compõe/valida scripts |
| `gemma-4-e2b-exec` | 0.3 | off | valida **e executa** scripts |

Escala de temperatura: **0.3 execução / 0.6 planejamento / 0.8 exploração**.
Além do sufixo, `--thinking-off-roles Build,Scripter,Exec` força thinking off por
**nome de role** (corrige sem renomear id).

### 1.3 Agents

`~/.config/opencode/agents/*.md` apontam para os ids acima:

| agent | model | temp |
|---|---|---|
| `plan.md` | `openai/gemma-4-12b-plan` | 0.6 |
| `build.md` | `openai/gemma-4-12b-build` | 0.3 |
| `orch.md` | `openai/gemma-4-12b-orch` | 0.6 |
| `moe.md` | `openai/gemma-4-12b-moe` | 0.8 |
| `scripter.md` | `openai/gemma-4-12b-scripter` | 0.3 |
| `exec.md` | `llama/gemma-4-e2b-exec` | 0.3 |
| subagente `task` | `llama/gemma-4-e2b-exec` | 0.3 |

O Exec é **só GPU2** (como Plan/Build/Orch são só GPU1). O Scripter **valida**
(`bash -n`) sem executar; o Exec **executa** (`bash <arquivo>`). Subagente
genérico `llama/gemma-4-e2b` cai no `--thinking events` da unit e abre reasoning
— use o `-exec` para manter determinismo.

---

## 2. Servidores llama (GPU full, 12 GB, TurboQuant)

### 2.1 Flags do `llama-server` usadas nesta linha

| flag | valor | por quê |
|---|---|---|
| `-m` | `/opt/models/gemma4/…` | GGUF do modelo |
| `--mmproj` | `mmproj-*.gguf` | projetor de visão (multimodal) |
| `-c` | 131072 | contexto **total** do servidor |
| `-np` | 1 (GPU1) / 2 (GPU2) | slots paralelos; **divide** o `-c` |
| `-ngl 99` | 99 | **100% GPU** (sem offload para CPU) |
| `-fa on` | on | Flash Attention |
| `--cache-type-k` / `--cache-type-v` | `turbo3` | KV em TurboQuant nos dois |
| `--prio` | 2 (GPU1) / -1 (GPU2) | prioridade da GPU (orquestrador ganha) |
| `-t` / `-tb` | 4 (GPU2) | threads — **inertes** em GPU full, usadas no teste CPU (`-ngl 0`) |
| `--alias` | `gemma-4-12b` / `gemma-4-e2b` | id servido (bate com `--model` da bridge) |
| `--api-key` | `guest@123` | auth do upstream (bridge repassa em Bearer) |

Modelos no disco:

| arquivo | bytes | ≈ |
|---|---|---|
| `/opt/models/gemma4/gemma-4-12b-it-Q4_K_M.gguf` | 7 121 861 440 | 6,63 GiB |
| `/opt/models/gemma4/mmproj-F16.gguf` | 175 115 840 | 167 MiB |
| `/opt/models/gemma4-e2b/gemma-4-E2B-it-Q3_K_M.gguf` | 3 226 811 872 | 3,01 GiB |
| `/opt/models/gemma4-e2b/mmproj-gemma-4-E2B-it-Q8_0.gguf` | 557 368 064 | 531 MiB |

### 2.2 Unit da GPU1 (12B `:4100`)

```ini
# /etc/systemd/system/llama-gpu.service
[Service]
Type=simple
User=root
Environment=HOME=/root
Environment=TURBO_AUTO_ASYMMETRIC=0
ExecStart=/usr/local/bin/llama-server --alias gemma-4-12b --host 127.0.0.1 --port 4100 \
  -m /opt/models/gemma4/gemma-4-12b-it-Q4_K_M.gguf \
  --mmproj /opt/models/gemma4/mmproj-F16.gguf \
  -c 131072 -np 1 -ngl 99 -fa on \
  --cache-type-k turbo3 --cache-type-v turbo3 --prio 2 --api-key guest@123
Restart=on-failure
TimeoutStartSec=300
```

Aceite do startup: `n_slots = 1, n_ctx_slot = 131072` → `model loaded` →
`listening on http://127.0.0.1:4100`. Subir **primeiro** (ver §2.5).

### 2.3 Unit da GPU2 (E2B `:4110`) — nome do unit é histórico

O unit chama-se `llama-cpu`, mas roda **100% GPU** (`-ngl 99`). O `-t/-tb`
existe para o cenário de teste CPU (`Llama GPU2-CPU Toggle`, `-ngl 0`).

```ini
# /etc/systemd/system/llama-cpu.service
[Service]
Type=simple
User=root
Environment=HOME=/root
Environment=TURBO_AUTO_ASYMMETRIC=0
ExecStart=/usr/local/bin/llama-server --alias gemma-4-e2b --host 127.0.0.1 --port 4110 \
  -m /opt/models/gemma4-e2b/gemma-4-E2B-it-Q3_K_M.gguf \
  --mmproj /opt/models/gemma4-e2b/mmproj-gemma-4-E2B-it-Q8_0.gguf \
  -c 131072 -np 2 -ngl 99 -fa on -t 4 -tb 4 \
  --cache-type-k turbo3 --cache-type-v turbo3 --prio -1 --api-key guest@123
Restart=on-failure
TimeoutStartSec=300
```

Aceite do startup: `n_slots = 2, n_ctx_slot = 65536` → `model loaded` →
`listening on http://127.0.0.1:4110` (**2×65536** = 131072 total).

> Trocar para um slot único de 128k: `-np 1 -c 131072` **e** subir
> `limit.context` da GPU2 no OpenCode para `131072`. Você perde o paralelismo de
> 2 subagentes — que é o motivo de a config atual ser `-np 2`.

### 2.4 TurboQuant é obrigatório nos dois

`Environment=TURBO_AUTO_ASYMMETRIC=0` **+** `--cache-type-k turbo3
--cache-type-v turbo3` **+** `-fa on`, nos **dois** units. Sem a env o
llama.cpp rebaixa o K da GPU2 para `q8_0` (GQA 8:1, `n_head=8`, `n_head_kv=1`):

```
W llama_kv_cache: auto-asymmetric: GQA ratio 8:1 — upgrading K from turbo3 to
q8_0 to prevent quality degradation. Disable with TURBO_AUTO_ASYMMETRIC=0
```

Consequência medida: o `q8_0` **gasta mais VRAM por token** que o `turbo3`
(GPU2 a 80k/1 com K `q8_0` = 2602 MiB contra **2022 MiB** a 96k/1 com `turbo3`).
Ou seja: forçar TurboQuant não é só qualidade — é o que abre espaço no
orçamento de 12 GB.

**Verificação de startup = ausência da linha `auto-asymmetric` no journal:**

```bash
journalctl -u llama-gpu -u llama-cpu --no-pager | grep -Ei "auto-asymmetric|fit params"
# não deve imprimir nada
```

### 2.5 Ordem de carga e prioridade

1. **GPU1 (12B) primeiro**, depois GPU2 (E2B). Invertido, a GPU2 pega a VRAM
   primeiro e a GPU1 **aborta** o auto-fit (`failed to fit params to free device
   memory: n_gpu_layers already set by user to 99, abort`) porque `-ngl 99` é
   fixo.
2. Os units `bridge-*` têm `After=`/`Wants=` nos `llama-*.service`, mas o 12B
   ainda pode levar minutos para carregar; a bridge sobe antes e devolve
   `503 Loading model` na corrida. Suba os modelos antes de abrir o OpenCode.
3. `--prio 2` (GPU1, high) e `--prio -1` (GPU2, low): quando as duas disputam a
   GPU, o orquestrador ganha da subagente.

### 2.6 VRAM medida (12 GB, ambos em `turbo3`)

| configuração | GPU1 (12B) | GPU2 (E2B) | total usado | livre |
|---|---|---|---|---|
| 12B 160k/1 + E2B 96k/1 | 8512 | 2572 | 11234 | 802 |
| 12B 160k/1 + E2B 112k/1 | 8512 | — | **aborta** (`fit params`) | — |
| **12B 128k/1 + E2B 128k/2 (atual)** | **8298** | **2702** | **11154** | **882** |
| live 2026-10-19 (`nvidia-smi`) | — | — | 11197 | 840 |

Regras aprendidas com as medições:

- O `-c` do llama é **total**; `-np 2` divide por 2 (`n_ctx_slot`).
- Cortar 32k do 12B (160k→128k) **devolveu** 214 MiB; dobrar os slots da GPU2
  custou 130 MiB. O saldo (11234→11154 MiB) pagou 2×64k úteis em paralelo.
- O que estoura é o **KV** (cresce com `n_ctx`), não os pesos.

---

## 3. Cenários de teste (12 GB + TurboQuant ativo nos dois)

### Cenário A — padrão: ambos residentes (o do E2E)

GPU1 `128k/1` + GPU2 `128k/2` → ~11,15–11,20 GiB usados, ~0,82–0,88 GiB livres.
É o regime dos testes de integração: as duas linhas servindo ao mesmo tempo no
mesmo GPU (a taxa de cada uma cai ~metade sob concorrência).

```bash
./run-e2e-pipeline.sh --phase 0          # health llama/bridge + VRAM
./run-e2e-pipeline.sh --phase 1          # unit: PASS=23 FAIL=0 SKIP=3
./run-e2e-pipeline.sh --phase 2          # E-science + E-mission-tools
./run-e2e-pipeline.sh --phase 3          # standalone (opencode real, GPU1+GPU2)
```

`E-tools-full` cobre read/write/edit/glob/grep/mkdir/git/`bash -n`/exec nos
**dois** modelos, em paralelo — é o teste que exercita o regime de 2 slots.

### Cenário B — GPU1 solo

Sem a GPU2 residente, o 12B sobe até **160k** com folga (medido **8512 MiB** a
160k/1). Use para benchmark de raciocínio puro (thinking on) e `E-science-gpu`.
Acima de 160k é **não medido** nesta placa.

### Cenário C — GPU2 solo / benchmark

Valores conhecidos: **2022 MiB** a 96k/1 e **2702 MiB** a 128k/2 (ambos
`turbo3`). O E2B é o modelo leve: costuma sobrar VRAM para subir `-c` acima de
128k, mas isso **não foi medido** — meça com `nvidia-smi` antes de fixar.

### Cenário D — VRAM apertada (regras de corte)

Ordem de corte, pelos deltas medidos:

1. **GPU2 `128k/2` → `96k/1`**: libera ~680 MiB (2702 → 2022), ao preço de
   perder o 2º slot (subagentes passam a serializar).
2. **GPU1 `128k/1` → `96k/1`**: libera ~214 MiB por 32k cortado.
3. **`--mmproj` da GPU2**: `F16` (985 MB) → `Q8_0` (531 MB) libera **~482 MiB**
   com a visão preservada (já aplicado).
4. **Nunca** baixe `-ngl` (quebra "GPU full"). Se não cabe, reduza `-c`/`-np`,
   não offloadeie camadas para CPU.

Se o warning for `fit params` na **GPU2**, corte na GPU2; se for na **GPU1**
(que aborta), corte na GPU1 — e sempre suba a GPU1 antes (§2.5).

### Cenário E — GPU2 em CPU (teste de execução granular de scripts)

O teste CPU da GPU2 usa a **mesma unit** `llama-cpu.service`, trocando só o
offload: **`-ngl 0`** (= `--n-gpu-layers 0`). Atenção: `-np` é o número de
**slots** — **não** é o interruptor de CPU; quem desliga a GPU é o `-ngl 0`.
Mantêm-se `-t 4 -tb 4` e o KV `turbo3`. Serve para testar o papel de **execução
de scripts granulares** (Scripter/Exec) sem disputar VRAM; não é o regime de
produção, e a bridge expõe `--cpu-max-tokens` para conter divagação em decode
lento (~5 tok/s).

Toggle pronto (reescreve `-ngl` no unit + template, `daemon-reload` + restart):

```bash
llama-gpu2-cpu-toggle.sh        # ou o atalho "Llama GPU2-CPU Toggle"
# -ngl 99 (GPU full) <-> -ngl 0 (CPU puro); rodar de novo devolve para GPU
```

Manual via drop-in (não edita o unit original):

```bash
sudo systemctl edit llama-cpu.service
# [Service]
# ExecStart=
# ExecStart=/usr/local/bin/llama-server --alias gemma-4-e2b --host 127.0.0.1 --port 4110 \
#   -m /opt/models/gemma4-e2b/gemma-4-E2B-it-Q3_K_M.gguf \
#   --mmproj /opt/models/gemma4-e2b/mmproj-gemma-4-E2B-it-Q8_0.gguf \
#   -c 131072 -np 2 -ngl 0 -fa on -t 4 -tb 4 \
#   --cache-type-k turbo3 --cache-type-v turbo3 --prio -1 --api-key guest@123
sudo systemctl daemon-reload && sudo systemctl restart llama-cpu
journalctl -u llama-cpu --no-pager | grep -E "n_ctx_slot|offloaded" | tail -3
# esperado: n_ctx_slot = 65536 e "offloaded 0/N layers to GPU"
```

Exemplo — devpipe (o filtro `--only` é "contém": `E-devpipe` roda os 5 casos):

```bash
# GPU2 em CPU (-ngl 0), HTTP direto na bridge :4125, role exec
dotnet run -c Release --project Rochas.OpenCodeBridge.Test --no-build -- \
  --only E-devpipe --api-key guest@123 --bridge http://127.0.0.1:4125 --role exec

# um caso só (ferramentas de dev: read/write/edit/glob/grep/git/bash)
dotnet run -c Release --project Rochas.OpenCodeBridge.Test --no-build -- \
  --only E-devpipe-tools --api-key guest@123 --bridge http://127.0.0.1:4125

# devpipe pelo opencode real (agente exec -> provider llama/ = GPU2)
dotnet run -c Release --project Rochas.OpenCodeBridge.Test --no-build -- \
  --standalone --only E-standalone-devpipe --standalone-agent exec \
  --api-key guest@123 --bridge http://127.0.0.1:4125
```

---

## 4. Argumentos da bridge (referência completa)

Uso: `dotnet Rochas.OpenCodeBridge.dll --chave valor ...` (ou direto
`Rochas.OpenCodeBridge --chave valor`). Booleanos são desligados com `false` ou
`0`. Valores com espaço vão entre aspas.

### 4.1 CLI

| argumento | default | descrição |
|---|---|---|
| `--port` | `4124` | porta da bridge |
| `--listen` / `--host` | `0.0.0.0` | interface de escuta (`0.0.0.0` = exposta na rede) |
| `--upstream` | `http://127.0.0.1:4100` | base do llama.cpp (`/v1/...`) |
| `--model` | `qwen3-8b-awq` | id servido pelo upstream (deve casar o `--alias`) |
| `--thinking` | `events` | `events` mostra `reasoning_content`; `off` descarta |
| `--temperature` | `0.5` | global; perfil por sufixo do id vence |
| `--max-tokens` | `0` | teto de saída (0 = sem teto; llama/cliente decidem) |
| `--cpu-max-tokens` | `0` | teto da linha CPU (0 = sem teto) |
| `--tool-output-limit` | `0` | corte por resultado de tool (0 = integral) |
| `--log` | `/tmp/qwen3-bridge.log` | log JSONL |
| `--api-key` | vazio | Bearer que a bridge usa no upstream e aceita do cliente |
| `--jwt-secret` | vazio | segredo JWT (≥16 chars) p/ o painel |
| `--jwt-expiry` | — | validade do token em minutos |
| `--users-db` | — | SQLite de usuários (login JWT) |
| `--passthrough-responses` | `false` | **ligar**: bypass Responses nativo |
| `--coerce-text-tools` | `false` | coage tool call em texto (```json / `<tool_call>`) |
| `--coerce-narrated` | `true` | coage `[Assistant tool call]: nome({...})` |
| `--anti-repeat` | `true` | contenção de repetição no stream |
| `--anti-repeat-window` | `256` | janela em chars (64..2048) |
| `--anti-repeat-retry` | `true` | 1 retry após contenção |
| `--thinking-off-roles` | vazio | CSV de roles (`Build,Scripter,Exec`) sempre sem thinking |
| `--tool-blacklist` | vazio | `"Role=tool,tool;Role=tool"` |
| `--risk-tools-alerts` | vazio | `"Role=regex,regex;Role=regex"` (comandos de risco) |
| `--risk-ask` | `true` | pergunta ao usuário antes de comando de risco |
| `--risk-ask-minutes` | `30` | validade da autorização concedida |
| `--context-guard` | `false` | devolve 413 ao atingir o limiar do ctx |
| `--context-threshold` | `0.95` | fração do ctx (0.5..0.99) |
| `--context-tokens` | `0` | tamanho do ctx do modelo servido (0 = inativo) |
| `--harness-behavior-tail` | vazio | tail de **comportamento** |
| `--harness-tools-tail` | default do código | tail de **tools** — não sobrescrever |
| `--legacy-harness` | `false` | reativa o harness legado (README — "Testes com outras famílias") |
| `--diagnostic` | `false` | sonda Serilog extra (`/tmp/bridge-sonda.log`) |
| `--qwen-standard` | `false` | ModelPrompt sintetizado + tools Alibaba (Qwen) |
| `--workspace` | cwd | raiz de `POST /api/tools/execute` |
| `--harness` | — | **inerte** (switch mestre removido); não use |

**Coerção (Qwen3 / Qwen3.5 / DeepSeek Coder V2-Lite):** `--coerce-text-tools 1`
extrai a chamada escrita no corpo (fence ` ```json `, `<tool_call>`, `<tools>`,
`<function>`) e sintetiza `tool_calls`; `--coerce-narrated 1` (default) resgata a
forma copiada do histórico `[Assistant tool call]: nome({...})`. A coerção de
texto só roda com **`--legacy-harness 1`** (gate `CoerceFor`) e a allowlist são
as tools anunciadas no pedido (`RealTools`). No Gemma 4 mantenha
`--legacy-harness 0` e `--coerce-text-tools 0`.

Subcomando: `Rochas.OpenCodeBridge.dll gate <repo> "<prompt>" [texto ...] [--review]`.

### 4.2 `appsettings.json` (opcional, ao lado do DLL)

Chaves em **camelCase**, equivalentes 1:1 às flags (`port`, `listen`,
`upstream`, `model`, `thinking`, `temperature`, `maxTokens`, `cpuMaxTokens`,
`toolOutputLimit`, `logPath`, `apiKey`, `passthroughResponses`,
`coerceTextTools`, `coerceNarrated`, `antiRepeat`, `antiRepeatWindow`,
`antiRepeatRetry`, `antiRepeatNotice`, `harnessToolsTail`,
`harnessBehaviorTail`, `diagnostic`, `workspace`, `riskAsk`, `riskAskMinutes`,
`thinkingOffRoles[]`, `toolBlacklist{Role:[tools]}`, `riskToolsAlerts{Role:[regex]}`,
`profiles{role:{temperature,thinking}}`, `contextGuard{enabled,threshold,contextTokens}`,
`jwt{secret,expiryMinutes}`, `usersDb`, `qwenStandard`).

Regras de precedência:

1. `appsettings.json` **sobrescreve** os defaults do código;
2. **CLI vence o arquivo**;
3. o arquivo tem precedência sobre o dicionário de perfis em `Program.cs` —
   mudar perfil no código **exige** alinhar o `appsettings.json` e reiniciar,
   senão o runtime continua no valor antigo.

Variáveis de ambiente: `DIAGNOSTIC=1` liga a sonda; `HARNESS=0` é legado inerte.

### 4.3 Units de produção (exemplo aplicado)

```ini
# bridge-gpu.service  (GPU1 / 12B)
ExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll \
  --listen 0.0.0.0 --port 4124 --upstream http://127.0.0.1:4100 --model gemma-4-12b \
  --thinking events --temperature 0.5 --max-tokens 16384 --api-key guest@123 \
  --passthrough-responses true --legacy-harness 0 --anti-repeat 1 \
  --thinking-off-roles Build,Scripter,Exec \
  --tool-blacklist "Plan=write,edit,exec,install,git;sed,rm,chmod,chown,mv,cp;MoE=…;Orch=…" \
  --anti-repeat-window 512 --log /tmp/bridge-gpu.log

# bridge-cpu.service  (GPU2 / E2B)
ExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll \
  --listen 0.0.0.0 --port 4125 --upstream http://127.0.0.1:4110 --model gemma-4-e2b \
  --thinking events --temperature 0.5 --max-tokens 8192 --api-key guest@123 \
  --passthrough-responses true --legacy-harness 0 --anti-repeat 1 \
  --thinking-off-roles Build,Scripter,Exec \
  --tool-blacklist "Plan=…;MoE=…;Orch=…" \
  --anti-repeat-window 512 --diagnostic 1 --log /tmp/bridge-cpu.log
```

Atalhos de desktop: `OpenCode Bridge GPU/CPU` reiniciam as units e mostram
saúde; `Llama GPU`/`Llama CPU` regeneram e reiniciam os servidores;
`Llama GPU2-CPU Toggle` alterna `-ngl 99` ⇄ `-ngl 0` (benchmark CPU puro).

---

## 5. Build, testes e pipeline

```bash
dotnet build Rochas.OpenCodeBridge.slnx -c Release

# unit da bridge (sem modelo)
dotnet run -c Release --project Rochas.OpenCodeBridge.Test -- --skip-e2e --api-key guest@123

# unit da Web
dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --units-only

# E2E por faixas pela UI (navegador + engine viva)
dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --ui --belts azul --phase batch

# pipeline sequencial (fase 0 health/VRAM → 1 unit → 2 E2E → 3 standalone)
./run-e2e-pipeline.sh --phase all
```

Flags da suíte: `--bridge`, `--vllm`, `--api-key`, `--jwt`, `--only <teste>`,
`--skip-e2e`, `--role`, `--model`, `--belts`, `--standalone`,
`--standalone-agent`, `--standalone-model`, `--standalone-timeout`.

**Regra de deploy (produção `/usr/src/opencodebridge`)**: só `git fetch` +
merge + build + restart; nunca editar código lá. Em produção, **pare as bridges
antes do build** (lock de PDB) e rode com `--no-build`.

---

## 6. Endpoints (bridge)

`/health`, `/v1/responses`, `/v1/chat/completions`, `/v1/models`,
`/api/status|harness|translate|convert|sanitize|config|metrics|permission/check|tools/execute`,
`/swagger`, `/api/swagger.json`. Auth: `Authorization: Bearer <api-key>`.

---

## 7. Docs

- `README.md` — resumo expresso (componente, cenário de teste, harness).
- `AGENTS.md` — regras do operador.
- `AGENTS-PROMPTS.md` — system prompts versionados (fonte dos agentes).
- `HISTORY.md` — histórico de construção e medições.
- `RUNBOOK-E2E.md` — pipeline por fases e estado do ambiente.
- `PENDENCIAS.md` / `PENDINGS.md` — o que ainda não foi feito.
