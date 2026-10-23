# LAB-SETUP — infra da linha Gemma 4 (12 GB, TurboQuant)

Detalhes de infraestrutura, configuração dos servidores llama, contextos, VRAM,
cenários de teste e referência completa de argumentos da bridge. O resumo
expresso (componente, cenário de teste e harness) fica no `README.md`.

## Snapshot atual (2026-10-23)

| linha | modelo | ctx | VRAM |
|---|---|---|---|
| GPU1 `:4100` | `Gemma4-E4B-Q4` — Gemma4 E4B **base original** `Q4_K_M` + mmproj **Q8_0** | `-c 81920 -np 1` = **1×80k** | (medir no host; chroot sem CUDA) |
| GPU2 `:4110` | `Gemma4-E4B-UD-Q3` — Huihui Gemma4 E4B **abliterated** `IQ3_XXS`, **text-only** | `-c 40960 -np 1` = **1×40k** | (medir no host; chroot sem CUDA) |
| CPU `:4120` | `Gemma4-E4B-Q4` — mesmo E4B base, **CPU puro** (`-ngl 0`) | `-c 81920 -np 1` = **1×80k** | 0 (RAM) |

Temperatura: CLI base **0.5**; perfis do runtime (`appsettings.json`) **plan 0.6 / build 0.3 / orch 0.6 / moe 0.8 / scripter 0.3 / exec 0.3**; o `temperature` do modelo no `opencode.json` (0.3/0.1/0.3/0.5/0.1) é só hint do cliente — o perfil da bridge vence.
Detalhes de remaster em `/media/mint/Linux/remaster-update6.md`.


## 0. Nomenclatura

| nome | o que é | endpoint |
|---|---|---|
| **GPU1** | `Gemma4-E4B-Q4` (E4B base original, `Q4_K_M`) — 100% GPU | llama `:4100` / bridge `:4124` |
| **GPU2** | `Gemma4-E4B-UD-Q3` (E4B abliterated, `IQ3_XXS`) — 100% GPU | llama `:4110` / bridge `:4125` |

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
| `openai` | `http://127.0.0.1:4124/v1` | `:4100` | GPU1 `Gemma4-E4B-Q4` (E4B base original Q4_K_M) |
| `llama` | `http://127.0.0.1:4125/v1` | `:4110` | GPU2 `Gemma4-E4B-UD-Q3` (E4B abliterated IQ3_XXS) |

```json
{
  "providers": {
    "openai": {
      "api": "openai-chat",
      "settings": { "baseURL": "http://127.0.0.1:4124/v1", "apiKey": "guest@123" },
      "models": {
        "Gemma4-E4B-Q4": {
          "name": "Gemma4 E4B base Q4_K_M GPU",
          "tool_call": true,
          "compatibility": { "reasoningField": "reasoning_content" },
          "limit": { "context": 81920, "output": 16384 },
          "temperature": 0.3
        }
      }
    },
    "llama": {
      "api": "openai-chat",
      "settings": { "baseURL": "http://127.0.0.1:4125/v1", "apiKey": "guest@123" },
      "options": { "timeout": 1800000, "headerTimeout": 900000, "chunkTimeout": 900000 },
      "models": {
        "Gemma4-E4B-UD-Q3": {
          "name": "Huihui Gemma4 E4B abliterated IQ3_XXS GPU2",
          "tool_call": true,
          "compatibility": { "reasoningField": "reasoning_content" },
          "limit": { "context": 40960, "output": 16384 },
          "temperature": 0.3
        }
      }
    }
  }
}
```

Pontos que **quebram** a linha se ficarem errados:

- `tool_call: true` e `compatibility.reasoningField: reasoning_content` são
  obrigatórios (o Gemma 4 emite thinking em `reasoning_content`).
- `limit.output` espelha o `--max-tokens` da bridge: **16384** (GPU1 e GPU2).
  Sem isso o cliente entrega prompts de saída maiores do que o
  llama serve e o `Decode error (200 POST /v1/responses)` reaparece.
- `limit.context` é **por requisição/slot**, não a soma dos slots. As duas rodam
  `-np 1`: **GPU1 = 81920 (1×80k)** e **GPU2 = 40960 (1×40k)**.
- A bridge `:4126` (`bridge-cpu-forced.service` / `llama-cpu-forced.service`,
  `-ngl 0`) é o caminho CPU puro e **não** entra no par 16k: continua com
  `--max-tokens 8192` e `-c 81920`.
- O provider `llama` mantém `timeout`/`headerTimeout`/`chunkTimeout` longos
  (30/15/15 min) porque a GPU2 divide a GPU com a GPU1 e o ctx cheio é lento.

### 1.2 Modelos, perfis e contextos

O perfil (temperatura + thinking) é casado pelo **sufixo do model id**
(`TryProfile`/`RoleOfModel` em `Program.cs`). Id **sem sufixo** cai no global da
unit (`--temperature`/`--thinking`); id com sufixo **vence**.

| id | temp | thinking | papel |
|---|---|---|---|
| `Gemma4-E4B-Q4` | 0.5 | events | global GPU1 (base da unit) |
| `Gemma4-E4B-Q4-plan` | 0.6 | events | planeja |
| `Gemma4-E4B-Q4-build` | 0.3 | off | executa |
| `Gemma4-E4B-Q4-orch` | 0.6 | events | orquestra/delega |
| `Gemma4-E4B-Q4-moe` | 0.8 | events | explora/generalista |
| `Gemma4-E4B-Q4-scripter` | 0.3 | off | compõe scripts |
| `Gemma4-E4B-UD-Q3` | 0.5 | events | global GPU2 (base da unit) |
| `Gemma4-E4B-UD-Q3-plan` | 0.6 | events | planeja |
| `Gemma4-E4B-UD-Q3-build` | 0.3 | off | executa |
| `Gemma4-E4B-UD-Q3-moe` | 0.8 | events | explora |
| `Gemma4-E4B-UD-Q3-scripter` | 0.3 | off | compõe/valida scripts |
| `Gemma4-E4B-UD-Q3-exec` | 0.3 | off | valida **e executa** scripts |

Escala de temperatura: **0.3 baixo (execução) / 0.6 médio (planejamento) / 0.8 alto (exploração)**;
o global da unit é **0.5**. O `temperature` do `opencode.json` (0.3/0.1/0.3/0.5/0.1)
é hint do cliente e **não** sobrescreve o perfil da bridge.
Além do sufixo, `--thinking-off-roles Build,Scripter,Exec` força thinking off por
**nome de role** (corrige sem renomear id).

### 1.3 Agents

`~/.config/opencode/agents/*.md` apontam para os ids acima:

| agent | model | temp |
|---|---|---|
| `plan.md` | `openai/Gemma4-E4B-Q4-plan` | 0.3 |
| `build.md` | `openai/Gemma4-E4B-Q4-build` | 0.1 |
| `orch.md` | `openai/Gemma4-E4B-Q4-orch` | 0.3 |
| `moe.md` | `openai/Gemma4-E4B-Q4-moe` | 0.5 |
| `scripter.md` | `openai/Gemma4-E4B-Q4-scripter` | 0.1 |
| `exec.md` | `llama/Gemma4-E4B-UD-Q3-exec` | 0.1 |
| subagente `task` | `llama/Gemma4-E4B-UD-Q3-exec` | 0.1 |

As temps acima são as do `opencode.json`; no runtime o **perfil da bridge**
(plan/orch 0.6, build/scripter/exec 0.3, moe 0.8) vence pelo sufixo do id.

O Exec é **só GPU2** (como Plan/Build/Orch são só GPU1). O Scripter **valida**
(`bash -n`) sem executar; o Exec **executa** (`bash <arquivo>`). Subagente
genérico `llama/Gemma4-E4B-UD-Q3` cai no `--thinking events` da unit e abre reasoning
— use o `-exec` para manter determinismo.

---

## 2. Servidores llama (GPU full, 12 GB, TurboQuant)

### 2.1 Flags do `llama-server` usadas nesta linha

| flag | valor | por quê |
|---|---|---|
| `-m` | `/opt/models/gemma4-e4b-base/…` (GPU1) / `/opt/models/huihui-gemma4-e4b-abliterated/…` (GPU2) | GGUF do modelo |
| `--mmproj` | `mmproj-gemma-4-E4B-it-Q8_0.gguf` | projetor de visão — **só na GPU1 (E4B base)**; o GPU2 é text-only |
| `-c` | 81920 (GPU1, 1×80k) / 40960 (GPU2, 1×40k) | contexto **total** do servidor |
| `-np` | 1 (GPU1) / 1 (GPU2) | slot único; o ctx do `-c` é o ctx do slot |
| `-ngl 99` | 99 | **100% GPU** (sem offload para CPU) |
| `-fa on` | on | Flash Attention |
| `--no-host` | — | sem buffer host; pesos e KV ficam na VRAM (GPU1/GPU2) |
| `--load-mode none` | `none` | substitui o obsoleto `--no-mmap`: sem mmap, carga direta |
| `--op-offload` | — | operações no device (CUDA) |
| `--mmproj-offload` | — | **só GPU1**: projetor de visão na GPU |
| `--repeat-penalty` | 1.1 | anti-loop (os dois) |
| `--dry-multiplier` / `--dry-penalty-last-n` | 0.8 / 1024 | DRY anti-repeat (os dois) |
| `--cache-type-k` / `--cache-type-v` | `turbo3` | KV em TurboQuant nos dois |
| `--prio` | 2 (GPU1) / -1 (GPU2) | prioridade da GPU (orquestrador ganha) |
| `-t` / `-tb` | 4 (GPU2) | threads — **inertes** em GPU full, usadas no teste CPU (`-ngl 0`) |
| `--alias` | `Gemma4-E4B-Q4` / `Gemma4-E4B-UD-Q3` | id servido (bate com `--model` da bridge) |
| `--api-key` | `guest@123` | auth do upstream (bridge repassa em Bearer) |

> **CPU puro (`:4120`, `llama-cpu-forced.service`).** Mantém `-ngl 0`, `-t 4 -tb 4`,
> `-c 81920 -np 1` e **não** usa `--no-host`/`--load-mode none`/`--op-offload`
> (é o caminho de teste sem VRAM, intocado nesta sessão).

Modelos no disco:

| arquivo | bytes | ≈ |
|---|---|---|
| `/opt/models/gemma4-e4b-base/google_gemma-4-E4B-it-Q4_K_M.gguf` | 5 405 170 144 | ~5,03 GiB |
| `/opt/models/gemma4-e4b-base/mmproj-gemma-4-E4B-it-Q8_0.gguf` | 559 874 816 | ~534 MiB |
| `/opt/models/huihui-gemma4-e4b-abliterated/Huihui-gemma-4-E4B-it-qat-q4_0-unquantized-abliterated.i1-IQ3_XXS.gguf` | 3 982 841 536 | ~3,71 GiB |

O GGUF do GPU2 é o `i1-IQ3_XXS` (SHA256 `9043e0b94e6a4f18fa498c5e5fd7e1d86a82dcfe440e1a6587f71882be76cf48`);
o antigo `Q3_K_M` foi removido.

### 2.2 Unit da GPU1 (E4B base `:4100`)

```ini
# /etc/systemd/system/llama-gpu.service
[Service]
Type=simple
User=root
Environment=HOME=/root
Environment=TURBO_AUTO_ASYMMETRIC=0
ExecStart=/usr/local/bin/llama-server --alias Gemma4-E4B-Q4 --host 127.0.0.1 --port 4100 \
  -m /opt/models/gemma4-e4b-base/google_gemma-4-E4B-it-Q4_K_M.gguf \
  --mmproj /opt/models/gemma4-e4b-base/mmproj-gemma-4-E4B-it-Q8_0.gguf \
  -c 81920 -np 1 -ngl 99 -fa on \
  --cache-type-k turbo3 --cache-type-v turbo3 --mmproj-offload --no-host --load-mode none --op-offload \
  --prio 2 --repeat-penalty 1.1 --dry-multiplier 0.8 --dry-penalty-last-n 1024 --api-key guest@123
Restart=on-failure
TimeoutStartSec=300
```

Aceite do startup: `n_slots = 1, n_ctx_slot = 81920` → `model loaded` →
`listening on http://127.0.0.1:4100`. Subir **primeiro** (ver §2.5).

### 2.3 Unit da GPU2 (E4B abliterated `:4110`, text-only)

O unit chama-se `llama-cpu`, mas roda **100% GPU** (`-ngl 99`). O `-t/-tb`
existe para o cenário de teste CPU (`Llama GPU2-CPU Toggle`, `-ngl 0`).

```ini
# /etc/systemd/system/llama-cpu.service  (nome histórico; roda 100% GPU)
[Service]
Type=simple
User=root
Environment=HOME=/root
Environment=TURBO_AUTO_ASYMMETRIC=0
ExecStart=/usr/local/bin/llama-server --alias Gemma4-E4B-UD-Q3 --host 127.0.0.1 --port 4110 \
  -m /opt/models/huihui-gemma4-e4b-abliterated/Huihui-gemma-4-E4B-it-qat-q4_0-unquantized-abliterated.i1-IQ3_XXS.gguf \
  -c 40960 -np 1 -ngl 99 -fa on -t 4 -tb 4 \
  --cache-type-k turbo3 --cache-type-v turbo3 --no-host --load-mode none --op-offload \
  --prio -1 --repeat-penalty 1.1 --dry-multiplier 0.8 --dry-penalty-last-n 1024 --api-key guest@123
Restart=on-failure
TimeoutStartSec=300
```

Aceite do startup: `n_slots = 1, n_ctx_slot = 40960` → `model loaded` →
`listening on http://127.0.0.1:4110` (**40960** total).

> A config atual é **slot único** (`-np 1 -c 40960` = 1×40k) e
> `limit.context = 40960` no OpenCode.

### 2.4 TurboQuant é obrigatório nos dois

`Environment=TURBO_AUTO_ASYMMETRIC=0` **+** `--cache-type-k turbo3
--cache-type-v turbo3` **+** `-fa on`, nos **dois** units. Sem a env o
llama.cpp rebaixa o K da GPU2 para `q8_0` (GQA 8:1, `n_head=8`, `n_head_kv=1`):

```
W llama_kv_cache: auto-asymmetric: GQA ratio 8:1 — upgrading K from turbo3 to
q8_0 to prevent quality degradation. Disable with TURBO_AUTO_ASYMMETRIC=0
```

Consequência (medição histórica da linha E2B): o `q8_0` gasta mais VRAM por
token que o `turbo3` (GPU2 a 80k/1 com K `q8_0` = 2602 MiB contra 2022 MiB a
96k/1 com `turbo3`). Na dupla E4B atual a VRAM ainda não foi medida neste
chroot (sem CUDA); medir no host.

**Verificação de startup = ausência da linha `auto-asymmetric` no journal:**

```bash
journalctl -u llama-gpu -u llama-cpu --no-pager | grep -Ei "auto-asymmetric"
# não deve imprimir nada (auto-asymmetric = K rebaixado para q8_0; não usar)

journalctl -u llama-gpu -u llama-cpu --no-pager | grep -Ei "fit params"
# GPU2: um warning "failed to fit params ... n_gpu_layers already set to 99, abort"
# é esperado com -ngl 99 e não impede o load; o que não pode é crash de CUDA graph
# (OOM). Com a dupla E4B atual (Q4_K_M + IQ3_XXS), medir a VRAM no host.
```

### 2.5 Ordem de carga e prioridade

1. **GPU1 (E4B base) primeiro**, depois GPU2 (E4B abliterated). Invertido, a GPU2 pega a VRAM
   primeiro e a GPU1 **aborta** o auto-fit (`failed to fit params to free device
   memory: n_gpu_layers already set by user to 99, abort`) porque `-ngl 99` é
   fixo.
2. Os units `bridge-*` têm `After=`/`Wants=` nos `llama-*.service`, mas o 12B
   ainda pode levar minutos para carregar; a bridge sobe antes e devolve
   `503 Loading model` na corrida. Suba os modelos antes de abrir o OpenCode.
3. `--prio 2` (GPU1, high) e `--prio -1` (GPU2, low): quando as duas disputam a
   GPU, o orquestrador ganha da subagente.

### 2.6 VRAM medida (12 GB, ambos em `turbo3`)

> **Histórico (linha 12B/E2B).** A tabela abaixo é da configuração anterior.
> Para a linha E4B atual (Q4_K_M 1×80k + IQ3_XXS 1×40k, ambos com
> `--no-host --load-mode none --op-offload`) a VRAM ainda não foi medida neste
> chroot (sem CUDA); medir no host com `nvidia-smi` após o boot.

| configuração | GPU1 (12B Q4_K_M) | GPU2 (E2B coder Q4) | total usado | livre |
|---|---|---|---|---|
| E4B Q4 1×80k + E4B IQ3_XXS 1×40k (**atual**) | (a medir) | (a medir) | (a medir) | (a medir) |
| 12B 2×96k + E2B 1×112k, E2B sem mmproj (histórico) | 8712 | 2082 | **11168** | **868** |
| 12B 2×112k + E2B 1×112k | 9014 | 2072 | 11476 | 560 |
| 12B 2×112k + E2B 1×140k | 9014 | 2190 | 11593 | 443 |
| 12B 2×112k + E2B 1×160k | 8892 | 2272 | 11519 | 518 |
| 12B Q3_K_L 2×112k + E2B 4×96k (histórico) | 8114 | 2956 | 11419 | 617 |
| 12B Q3_K_L 1×128k + E2B 2×80k (histórico) | ~6600 | ~2400 | 9877 | 2411 |

Regras aprendidas com as medições:

- O `-c` do llama é **total**; `-np N` divide por N (`n_ctx_slot`).
- O que estoura é o **KV** (cresce com `n_ctx`), não só os pesos.
- Com 12B Q4_K_M + E2B Q4 coder heretic (sem mmproj), o par
  `12B 2×96k + E2B 1×112k` mede **11168 MiB** com overhead e deixa
  **868 MiB livres**. O par `12B 2×112k + E2B 1×112k` sobe para 11476 MiB
  (560 MiB livres); `E2B 1×140k` e `1×160k` apertam para 443/518 MiB.

---

## 3. Cenários de teste (12 GB + TurboQuant ativo nos dois)

### Cenário A — padrão: ambos residentes (o do E2E)

GPU1 `2×96k` + GPU2 `1×112k` (sem mmproj no E2B) → ~10,91 GiB usados, ~0,85 GiB livres.
É o regime dos testes de integração: as duas linhas servindo ao mesmo tempo no
mesmo GPU (a taxa de cada uma cai sob concorrência). Alvo de VRAM no
boot: ~10,9 GiB usados; margem de segurança ~0,85 GiB para CUDA graphs/decodes.

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

E2B coder Q4 **1×112k sem mmproj** medido em **2082 MiB**. O E2B é o modelo
leve: sobra VRAM para subir `-c` acima de 112k, mas meça com `nvidia-smi` antes
de fixar (1×140k = 2190 MiB; 1×160k = 2272 MiB).

### Cenário D — VRAM apertada (regras de corte)

Ordem de corte, pelos deltas medidos:

1. **GPU2 `1×112k` → `1×96k`**: libera ~130 MiB, mantendo slot único.
2. **GPU1 `2×96k` → `1×96k`**: libera o KV do 2º slot (subagentes serializam).
3. **`--mmproj` da GPU2**: removido de vez (E2B text-only) — libera **531 MiB**.
   A GPU1 mantém o mmproj Q8_0 (única linha multimodal).
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

Na config atual o CPU puro dedicado é `llama-cpu-forced.service` (`:4120`,
`-ngl 0`), que fica **intocado**; o toggle em `llama-cpu.service` é o cenário de
teste histórico.

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
# ExecStart=/usr/local/bin/llama-server --alias Gemma4-E4B-Q4 --host 127.0.0.1 --port 4120 \
#   -m /opt/models/gemma4-e4b-base/google_gemma-4-E4B-it-Q4_K_M.gguf \
#   --mmproj /opt/models/gemma4-e4b-base/mmproj-gemma-4-E4B-it-Q8_0.gguf \
#   -c 81920 -np 1 -ngl 0 -fa on -t 4 -tb 4 \
#   --cache-type-k turbo3 --cache-type-v turbo3 --prio -1 --repeat-penalty 1.1 \
#   --dry-multiplier 0.8 --dry-penalty-last-n 1024 --api-key guest@123
sudo systemctl daemon-reload && sudo systemctl restart llama-cpu
journalctl -u llama-cpu --no-pager | grep -E "n_ctx_slot|offloaded" | tail -3
# esperado: n_ctx_slot = 81920 e "offloaded 0/N layers to GPU"
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

### Limitação medida — tools no 12B abliterated (2026-10-21)

`E-devpipe-tools` no 12B (`Gemma4-E4B-Q4-build`, `enable_thinking=false`) falha:
o modelo devolve `tool_calls[glob].arguments` com **2.574** marcadores
`<|channel>thought` (53.566 chars, `finish_reason=length` em 8192 tokens) e o
llama rejeita o JSON no turno seguinte (`parse error at column 53567`).
`E-responses-tools` passa, mas com `tool chamada: False` (8192 tokens em 363,7 s).
Não é VRAM/ctx (2×96k + 868 MiB livres): é o tool calling do modelo abliterated.

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
| `--thinking-format` | `events` | nome canônico do formato de repasse do reasoning; `--thinking` é alias |
| `--temperature` | `0.3` | global; perfil por sufixo do id vence |
| `--max-tokens` | `0` | teto de saída (0 = sem teto; llama/cliente decidem) |
| `--coerced-max-tokens` / `--cpu-max-tokens` | `0` | teto da linha COAGIDA (upstream sem parser); `--cpu-max-tokens` é alias histórico ("cpu" era inferência por nome de modelo) |
| `--tool-output-limit` | `0` | corte por resultado de tool (0 = integral) |
| `--log` | `/tmp/qwen3-bridge.log` | log JSONL |
| `--api-key` | vazio | Bearer que a bridge usa no upstream e aceita do cliente |
| `--jwt-secret` | vazio | segredo JWT (≥16 chars) p/ o painel |
| `--jwt-expiry` | — | validade do token em minutos |
| `--users-db` | — | SQLite de usuários (login JWT) |
| `--passthrough-responses` | `false` | **ligar**: bypass Responses nativo |
| `--coerce-text-tools` | `false` | coage tool call em texto (```json / `<tool_call>`) |
| `--upstream-parser` | `native` | `native`/`none` — capacidade de parser do upstream; substitui a heurística por nome de modelo |
| `--coerce-narrated` | `true` | coage `[Assistant tool call]: nome({...})` |
| `--anti-repeat` | `true` | contenção de repetição no stream |
| `--anti-repeat-window` | `256` | janela em chars (64..2048) |
| `--anti-repeat-retry` | `true` | 1 retry após contenção |
| `--anti-repeat-abort` | `true` | corta a geração no upstream na hora (SSE) ao trip; flag própria (desliga só o corte) |
| `--anti-repeat-notice` | (texto) | aviso invisível anexado no retry (CLI ou appsettings) |
| `--thinking-off-roles` | vazio | CSV de roles (`Build,Scripter,Exec`) sempre sem thinking |
| `--tool-blacklist` | vazio | `"Role=tool,tool;Role=tool"` |
| `--risk-tools-alerts` | vazio | `"Role=regex,regex;Role=regex"` (comandos de risco) |
| `--risk-ask` | `true` | pergunta ao usuário antes de comando de risco |
| `--risk-ask-minutes` | `30` | validade da autorização concedida |
| `--context-guard` | `false` | devolve 413 ao atingir o limiar do ctx |
| `--context-threshold` | `0.95` | fração do ctx (0.3..0.99) |
| `--context-tokens` | `0` | tamanho do ctx do modelo servido (0 = inativo) |
| `--harness-behavior-tail` | vazio | tail de **comportamento** |
| `--harness-tools-tail` | default do código | tail de **tools** — não sobrescrever |
| `--legacy-harness` | `false` | reativa o harness legado (README — "Testes com outras famílias") |
| `--diagnostic` | `false` | sonda Serilog extra (`/tmp/bridge-sonda.log`) |
| `--diagnostic-level` | `off` | `off`/`metrics`/`dump` — granularidade da sonda (metrics = sem corpos req/res) |
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

### 4.3 Grupos de flags (rotinas distintas)

A bridge expõe **39 nomes de flags** (38 linhas `case`, pois `--host` é alias de
`--listen`), das quais **24 estão ativas nas units**. Elas foram agrupadas em 11
rotinas distintas para que nenhuma flag governasse dois subsistemas ao mesmo tempo
(o bug que custou a rodada de 2026-10-22: `--legacy-harness 0` apagava o retry do
anti-repeat e a coerção de narrada porque os três compartilhavam o mesmo flag).

| grupo | flags | observação |
|---|---|---|
| rede/processo | `--listen` (alias `--host`) · `--port` · `--upstream` · `--model` · `--log` · `--workspace` | — |
| auth/admin | `--api-key` · `--jwt-secret` · `--jwt-expiry` · `--users-db` · `--bridge-managed` | — |
| transporte | `--passthrough-responses` · `--stream-per-delta` · `--qwen-standard` · `--thinking-format` (alias `--thinking`) · `--thinking-off-roles` | — |
| harness/prompt | `--harness` · `--harness-behavior-tail` · `--harness-tools-tail` | tails inertes sem `--harness 1` |
| coerção de tools | `--upstream-parser` · `--coerce-text-tools` · `--coerce-narrated` · `--tool-strategy` | capacidade do upstream, não nome de modelo |
| anti-repeat | `--anti-repeat` · `--anti-repeat-abort` · `--anti-repeat-retry` · `--anti-repeat-window` · `--anti-repeat-notice` | `--anti-repeat-abort` desliga só o corte em stream |
| orçamento/contexto | `--max-tokens` · `--coerced-max-tokens` (alias `--cpu-max-tokens`) · `--context-guard` · `--context-tokens` · `--context-threshold` | fail-fast se guard sem tokens |
| geração/artefatos | `--temperature` · `--tool-output-limit` | — |
| segurança | `--risk-ask` · `--risk-ask-minutes` · `--risk-tools-alerts` · `--tool-blacklist` | — |
| observabilidade | `--diagnostic` · `--diagnostic-level` | `metrics` = sem corpos req/res |
| legado | `--legacy-harness` | nunca em produção |

As flags não-governadas por grupos (ex.: `--temperature`) ficam no grupo de
geração. O mapa em vigor é exposto em `GET /api/status` → `config_groups`
(objeto `{grupo: [flags ativas]}`), para quem opera a bridge de fora ver a qual
rotina cada flag pertence sem ler a linha de comando.

**Validação de coerência no boot** (`ValidateConfig`, roda antes do listener):
`--context-guard 1` sem `--context-tokens` → exit 2 (fail-fast); `--diagnostic-level`,
`--upstream-parser` fora do domínio → exit 2; `--anti-repeat-window` fora de
64..2048 → exit 2; `--anti-repeat-notice` < 20 chars → exit 2. Avisos em stderr
para flag aceita mas inerte (`--coerced-max-tokens` sem coerção, tail com
`--harness 0`, `--context-tokens` sem guard, `--legacy-harness 1`).

### 4.4 Coordenadas — ctx da dupla original + saída 16k

**Llama (original duo, em produção):**

| linha | serviço | modelo | ctx / slots | VRAM |
|---|---|---|---|---|
| GPU1 `:4100` | `llama-gpu.service` | Gemma4 E4B base `Q4_K_M` + mmproj Q8_0 | `-c 81920` (1×80k) | (medir no host) |
| GPU2 `:4110` | `llama-cpu.service` | Huihui Gemma4 E4B abliterated `IQ3_XXS`, text-only | `-c 40960` (1×40k) | (medir no host) |
| CPU `:4120` | `llama-cpu-forced.service` | Gemma4 E4B base `Q4_K_M` + mmproj Q8_0, `-ngl 0` | `-c 81920` (1×80k) | 0 (RAM) |
| — | DRY | `--dry-multiplier 0.8`, `--dry-penalty-last-n 1024` | — | — |
| — | repeat-penalty | `--repeat-penalty 1.1` (os dois), janela 64 | — | — |

Total: **não medido neste chroot (sem CUDA)**; medir no host (RTX 3060, 12288 MiB).

**Bridge (saída 16k):**

| chave | valor | onde |
|---|---|---|
| `--max-tokens` | `16384` | `bridge-gpu.service`, `bridge-cpu.service` |
| runner `max_tokens` (chat) | `16384` | `/tmp/opencode/run-belts-direct.sh` e `E2EBeltsBuildTests.cs:70` |
| runner `max_tokens` (review) | `4096` | `E2EBeltsBuildTests.cs:116` |
| `--anti-repeat` / `--anti-repeat-retry` | `1` | ambas as bridges |
| `--harness` / `--legacy-harness` | `0` | ambas (sem harness ativo na bridge) |
| `--diagnostic` | `1` | ambas (nível `dump` por default legado) |
| `--thinking` | `events` | ambas (formato de repasse; o thinking efetivo vem do perfil) |
| `--coerce-text-tools` / `--coerce-narrated` / `--tool-strategy` | `0` | ambas (upstream com parser nativo) |
| `--anti-repeat-window` | `512` | ambas |
| `--context-guard` / `--context-tokens` | `1` / `81920` (GPU) e `1` / `40960` (CPU) | ambas |
| `--tool-blacklist` | Plan/MoE/Orch = escrita/exec/install/git + `sed,rm,chmod,chown,mv,cp` | ambas |
| `--upstream-parser` | `native` (default) | implícito — sem hardcode por nome de modelo |

A `bridge-cpu-forced.service` (`:4126`, `-ngl 0`) **não** entra no par 16k:
continua `--max-tokens 8192`, `--context-tokens 81920` e sem os novos
`--harness`/`--anti-repeat-retry`.

**Consequência do 16k:** o turno degenerado (loop de ~120 tokens por volta) morria
em `completion_tokens=16384` (~375 s @43 t/s na E2B, ~15,5k tokens descartados
pelo parser `peg-gemma4` do llama, com apenas ~900 tokens úteis de `content` +
3 `tool_calls`). O contorno (`TryStreamAbortChat`) corta a geração no meio
(~512 chars de texto novo) em vez de pagar o turno inteiro, preservando os
tool calls já fechados e tentando 1 retry com o aviso invisível
(`--anti-repeat-notice`).

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

No deploy atual, o `appsettings.json` do runtime
(`/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net9.0`) está com
`profiles.plan.temperature = 0.6`, `profiles.orch.temperature = 0.6`,
`build/scripter/exec = 0.3` e `moe = 0.8`; a unit passa `--temperature 0.5`
como base. As bridges foram reiniciadas depois do ajuste.

Variáveis de ambiente: `DIAGNOSTIC=1` liga a sonda; `HARNESS=0` é legado inerte
(use `--harness 0`).

### 4.3 Units de produção (exemplo aplicado)

```ini
# bridge-gpu.service  (GPU1 / E4B base) — linha única no unit
ExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll \
  --listen 0.0.0.0 --port 4124 --upstream http://127.0.0.1:4100 --model Gemma4-E4B-Q4 \
  --thinking events --temperature 0.5 --max-tokens 16384 --api-key guest@123 \
  --passthrough-responses true --harness 0 --legacy-harness 0 \
  --anti-repeat 1 --anti-repeat-retry 1 --thinking-off-roles Build,Scripter,Exec \
  --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 \
  --tool-blacklist "Plan=write,edit,exec,install,git;sed,rm,chmod,chown,mv,cp;MoE=write,edit,exec,install,git;sed,rm,chmod,chown,mv,cp;Orch=write,edit,exec,install,git;sed,rm,chmod,chown,mv,cp" \
  --anti-repeat-window 512 --diagnostic 1 --context-guard 1 --context-tokens 81920 \
  --log /tmp/bridge-gpu.log

# bridge-cpu.service  (GPU2 / E4B abliterated)
ExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll \
  --listen 0.0.0.0 --port 4125 --upstream http://127.0.0.1:4110 --model Gemma4-E4B-UD-Q3 \
  --thinking events --temperature 0.5 --max-tokens 16384 --api-key guest@123 \
  --passthrough-responses true --harness 0 --legacy-harness 0 \
  --anti-repeat 1 --anti-repeat-retry 1 --thinking-off-roles Build,Scripter,Exec \
  --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 \
  --tool-blacklist "Plan=write,edit,exec,install,git;sed,rm,chmod,chown,mv,cp;MoE=write,edit,exec,install,git;sed,rm,chmod,chown,mv,cp;Orch=write,edit,exec,install,git;sed,rm,chmod,chown,mv,cp" \
  --anti-repeat-window 512 --diagnostic 1 --context-guard 1 --context-tokens 40960 \
  --log /tmp/bridge-cpu.log
```

A `bridge-cpu-forced.service` (`:4126`, `-ngl 0`) permanece **intocada**:
`--max-tokens 8192`, `--context-tokens 81920`, sem `--harness`/`--anti-repeat-retry`.

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

---

## 8. Ajustes desta sessão (2026-10-23)

### 8.1 Servidores llama

- `:4100` (`llama-gpu.service`): E4B base `Q4_K_M` + mmproj `Q8_0`,
  `-c 81920 -np 1`, `-ngl 99`, KV `turbo3`, `--mmproj-offload --no-host
  --load-mode none --op-offload`, `--prio 2`.
- `:4110` (`llama-cpu.service`): Huihui E4B abliterated `i1-IQ3_XXS`
  (`3 982 841 536` bytes, SHA256
  `9043e0b94e6a4f18fa498c5e5fd7e1d86a82dcfe440e1a6587f71882be76cf48`),
  `-c 40960 -np 1`, `-ngl 99`, KV `turbo3`, `--no-host --load-mode none
  --op-offload`, `--prio -1`.
- `:4120` (`llama-cpu-forced.service`): E4B base `Q4_K_M` + mmproj `Q8_0`,
  `-c 81920 -np 1`, **`-ngl 0`** (CPU puro), **intocado**.
- `--no-mmap` foi substituído por `--load-mode none` (o `--no-mmap` está
  obsoleto; `none` = sem modo especial de carga). `--no-host` remove o buffer
  host; `--op-offload` leva as operações ao device. O KV continua na VRAM via
  `-ngl 99` (sem flags de KV offload).

### 8.2 Bridge

- Pull `origin/main` até `0937333` e rebuild do DLL
  (`dotnet build Rochas.OpenCodeBridge/Rochas.OpenCodeBridge.csproj -c Release`);
  smoke test na `:4199` OK.
- `:4124`/`:4125` com args canônicos: `--max-tokens 16384`, `--harness 0
  --legacy-harness 0`, `--anti-repeat 1 --anti-repeat-retry 1`, `--diagnostic 1`,
  `--coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0`,
  `--anti-repeat-window 512`, `--context-guard 1` e `--context-tokens`
  81920/40960, `--tool-blacklist` por role.
- `:4126` (`bridge-cpu-forced.service`) **intocada** (`--max-tokens 8192`).

### 8.3 OpenCode

- `~/.config/opencode/opencode.json` (root e mint): `limit.output = 16384`;
  ctx `81920` (provider `openai`/`:4124`) e `40960` (provider `llama`/`:4125`).

### 8.4 Ícones de desktop

- Root: `Llama GPU` → `start-llama-gpu.sh`; `Llama GPU2-CPU` →
  `start-llama-cpu.sh`; `Bridge Web UI` → `start-bridge-web.sh`.
- Mint: `Llama GPU`/`Llama CPU` → `start-llama-gpu.sh`/`start-llama-cpu.sh`;
  `OpenCode Bridge GPU`/`CPU` → `start-bridge-gpu.sh`/`start-bridge-cpu.sh`;
  `Bridge Web UI` → `start-bridge-web.sh`.
- Os scripts das bridges chamam `dotnet Rochas.OpenCodeBridge.dll` no
  `bin/Release/net9.0`; o da Web chama `Rochas.OpenCodeBridge.Web.dll`.
  Caminhos conferidos; a Web UI sobe na `:4130`. `start-bridge-web.sh` não
  precisou de ajuste (já aponta para o DLL da Web e usa `bridge-web`/processo
  direto).

### 8.5 Observações

- Este chroot não tem systemd (PID 1 = bash) nem CUDA visível: não houve
  `daemon-reload`/`restart` nem medição de VRAM aqui. As units/scripts valem no
  host; ao subir, conferir `n_ctx_slot` (81920/40960), `offloaded ... layers to
  GPU`, ausência de `auto-asymmetric` e VRAM com `nvidia-smi`.
- O warning `cauda de harness declarada com --harness 0: inerte` é esperado
  (o `appsettings.json` mantém `harnessBehaviorTail`, mas `--harness 0` não
  injeta prompt).

### 8.6 `-ot ".*=CUDA0"` — modelo 100% na VRAM (2026-10-23)

`-ngl 99` sozinho offloada as 43 camadas, mas embeddings/per-layer-input/output
do Gemma4 continuam no host (`CUDA_Host model buffer`). Para forçar **todo** o
modelo no device, os units passaram a usar `--override-tensor`:

```ini
-ot ".*=CUDA0"
```

Efeito medido (`-lv 5`, GPU1 sozinha): `CUDA0 model buffer = 5664.69 MiB`,
`CUDA_Host model buffer = 0.00 MiB`, KV `CUDA0 = 250.13 + 7.94 MiB`.
Sem `-ot`, GPU1 mantinha `CUDA_Host = 2730.00 MiB` (RSS ~3,98 GiB); com `-ot`,
RSS caiu para ~0,69 GiB.

Para caber os dois com `-ot`, o `--mmproj` foi removido da GPU1 (o E4B base
fica **text-only** nesta linha). O ctx da GPU2 foi reduzido de 40960 para
**32768** (bridge-cpu `--context-tokens 32768`). Boot validado:

| linha | VRAM processo | ctx |
|---|---|---|
| GPU1 `:4100` `Gemma4-E4B-Q4` | 6581 MiB | 81920 |
| GPU2 `:4110` `Gemma4-E4B-UD-Q3` | 4685 MiB | 32768 |
| **total / livre** | **11623 / 413 MiB** | — |

Queda de ctx 40k→32k liberou apenas ~64 MiB (o KV do Gemma4 com SWA/shared-KV
é pequeno: ~125 MiB a 40k), insuficiente para devolver o `--mmproj` (~740 MiB)
à GPU1.

`-ot ".*=CUDA0"` não altera o KV (continua em `CUDA0`); ele só move os tensores
de modelo que estavam em `CUDA_Host`. Se a visão da GPU1 for necessária, é
preciso reduzir ctx/quant ou aceitar o host buffer.
