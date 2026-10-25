# LAB-SETUP — infra da linha Gemma 4 (12 GB, TurboQuant)

Detalhes de infraestrutura, configuração dos servidores llama, contextos, VRAM,
cenários de teste e referência completa de argumentos da bridge. O resumo
expresso (componente, cenário de teste e harness) fica no `README.md`.

## Snapshot atual (2026-10-23)

| linha | modelo | ctx | VRAM |
|---|---|---|---|
| GPU1 `:4100` | `Gemma4-E4B-Q4` — Gemma4 E4B **base original** `Q4_K_M` + mmproj **Q8_0** | `-c 131072 -np 1` = **1×128k** | (medir no host; chroot sem CUDA) |
| GPU2 `:4110` | `Gemma4-E4B-UD-Q3` — Huihui Gemma4 E4B **abliterated** `IQ3_XXS`, **text-only** | `-c 114688 -np 2` = **2×56k** | (medir no host; chroot sem CUDA) |
| CPU `:4120` | `Gemma4-E4B-Q4` — mesmo E4B base, **CPU puro** (`-ngl 0`) | `-c 131072 -np 1` = **1×128k** | 0 (RAM) |

Temperatura: CLI base **0.5**; perfis do runtime (`appsettings.json`) **plan 0.3 / build 0.1 / orch 0.3 / moe 0.5 / scripter 0.1 / exec 0.1** (escala baixa **0.1**, média **0.3**, alta **0.5**); o `temperature` do modelo no `opencode.json` segue a mesma escala.
Detalhes de remaster em `/media/mint/Linux/remaster-update6.md`.

**Dev × produção.** O desenvolvimento acontece em
`/media/mint/3686C649614854E61/Projetos/Git/OpenCodeBridge/` (editar, buildar,
commitar, `git push`). `/usr/src/opencodebridge` é **produção**: não se edita
lá; só `git pull --ff-only origin main` e redeploy (`dotnet build -c Release`
+ restart das units).


## 0. Nomenclatura

| nome | o que é | endpoint |
|---|---|---|
| **GPU1** | `Gemma4-E4B-Q4` (E4B base original, `Q4_K_M`) — 100% GPU | llama `:4100` / bridge `:4124` |
| **GPU2** | `Gemma4-E4B-UD-Q3` (E4B abliterated, `IQ3_XXS`) — 100% GPU | llama `:4110` / bridge `:4125` |
| **CPU Forced** | `Gemma4-E4B-Q4` (`Q4_K_M`) — `-ngl 0` (CPU puro) | llama `:4120` / bridge `:4126` |

Os dois primeiros rodam em **GPU full** (`-ngl 99`) e **TurboQuant** (`turbo3` no
KV), convivendo em 12 GB de VRAM (RTX 3060, 12288 MiB).

> **Nota CPU Forced.** A **GPU2** (`Gemma4-E4B-UD-Q3`) roda **100% em GPU**
> (`-ngl 99`), sem offload. A linha **CPU** é explícita e separada:
> `llama-cpu-forced.service` (`:4120`, `-ngl 0 -t 4 -tb 4`) +
> `bridge-cpu-forced.service` (`:4126`). Ela existe como **cenário de teste**
> para **tarefas granulares de execução de scripts** (`-ngl 0`, `-t/-tb`), para
> medir/validar o executor de scripts sem disputar VRAM com a GPU1.

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
          "limit": { "context": 131072, "output": 16384 },
          "temperature": 0.3
        }
      }
    },
    "llama": {
      "api": "openai-chat",
      "package": "@ai-sdk/openai-compatible",
      "settings": { "baseURL": "http://127.0.0.1:4125/v1", "apiKey": "guest@123" },
      "options": { "timeout": 1800000, "headerTimeout": 900000, "chunkTimeout": 900000 },
      "models": {
        "Gemma4-E4B-UD-Q3": {
          "name": "Huihui Gemma4 E4B abliterated IQ3_XXS GPU2",
          "tool_call": true,
          "compatibility": { "reasoningField": "reasoning_content" },
          "limit": { "context": 131072, "output": 16384 },
          "temperature": 0.3
        }
      }
    }
  }
}
```

Pontos que **quebram** a linha se ficarem errados:

- **`package` em todo provider customizado** (`llama`, `cpu`):
  `"package": "@ai-sdk/openai-compatible"`. No OpenCode v2, `"api":
  "openai-chat"` sozinho não resolve o pacote e o `ModelResolver` aborta com
  `Unsupported package for llama/Gemma4-E4B-UD-Q3-*` (a sessão nem abre).
  O provider `openai` é id nativo do catálogo (`@ai-sdk/openai`), por isso
  dispensa a chave — e é o caminho que já funcionava.
- `tool_call: true` e `compatibility.reasoningField: reasoning_content` são
  obrigatórios (o Gemma 4 emite thinking em `reasoning_content`).
- `limit.output` espelha o `--max-tokens` da bridge: **16384** (GPU1 e GPU2).
  Sem isso o cliente entrega prompts de saída maiores do que o
  llama serve e o `Decode error (200 POST /v1/responses)` reaparece.
- `limit.context` é **por requisição/slot**, não a soma dos slots. **GPU1** roda
  `-np 1`: **131072 (1×128k)**; **GPU2** roda `-np 2`: **114688 (2×56k, 57344 por slot)**.
- A bridge `:4126` (`bridge-cpu-forced.service` / `llama-cpu-forced.service`,
  `-ngl 0`) é o caminho CPU puro e **não** entra no par 16k: continua com
  `--max-tokens 8192` e `-c 131072`.
- O provider `llama` mantém `timeout`/`headerTimeout`/`chunkTimeout` longos
  (30/15/15 min) porque a GPU2 divide a GPU com a GPU1 e o ctx cheio é lento.

### 1.2 Modelos, perfis e contextos

O perfil (temperatura + thinking) é casado pelo **sufixo do model id**
(`TryProfile`/`RoleOfModel` em `Program.cs`). Id **sem sufixo** cai no global da
unit (`--temperature`/`--thinking`); id com sufixo **vence**.

| id | temp | thinking | papel |
|---|---|---|---|
| `Gemma4-E4B-Q4` | 0.5 | events | global GPU1 (base da unit) |
| `Gemma4-E4B-Q4-plan` | 0.3 | events | planeja |
| `Gemma4-E4B-Q4-build` | 0.1 | off | executa |
| `Gemma4-E4B-Q4-orch` | 0.3 | events | orquestra/delega |
| `Gemma4-E4B-Q4-moe` | 0.5 | events | explora/generalista |
| `Gemma4-E4B-Q4-scripter` | 0.1 | off | compõe scripts |
| `Gemma4-E4B-UD-Q3` | 0.5 | events | global GPU2 (base da unit) |
| `Gemma4-E4B-UD-Q3-plan` | 0.3 | events | planeja |
| `Gemma4-E4B-UD-Q3-build` | 0.1 | off | executa |
| `Gemma4-E4B-UD-Q3-moe` | 0.5 | events | explora |
| `Gemma4-E4B-UD-Q3-scripter` | 0.1 | off | compõe/valida scripts |
| `Gemma4-E4B-UD-Q3-exec` | 0.1 | off | valida **e executa** scripts |

Escala de temperatura: **0.1 baixa (execução) / 0.3 média (planejamento) / 0.5 alta (exploração)**;
o global da unit é **0.5**. O `temperature` do `opencode.json` (0.3/0.1/0.3/0.5/0.1)
segue a mesma escala.
O thinking off vem do perfil (build/scripter/exec); as units atuais não usam
`--thinking-off-roles`.

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
(plan/orch 0.3, build/scripter/exec 0.1, moe 0.5) vence pelo sufixo do id.

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
| `-c` | 131072 (GPU1, 1×128k) / 114688 (GPU2, 2×56k) | contexto **total** do servidor |
| `-np` | 1 (GPU1) / 2 (GPU2) | GPU1 slot único; GPU2 dois slots de 57344 |
| `-ngl 99` | 99 | **100% GPU** (sem offload para CPU) |
| `-fa on` | on | Flash Attention |
| `--no-host` | — | sem buffer host; pesos e KV ficam na VRAM (GPU1/GPU2) |
| `--load-mode none` | `none` | substitui o obsoleto `--no-mmap`: sem mmap, carga direta |
| `--op-offload` | — | operações no device (CUDA) |
| `--mmproj-offload` | — | **só GPU1**: projetor de visão na GPU |
| `--repeat-penalty` | **1.08** | anti-loop leve (os dois); valor recomendado pela comunidade llama.cpp — reduz repetição sem penalizar identificadores copiados verbatim (nomes, paths, dígitos) |
| `--dry-multiplier` / `--dry-penalty-last-n` | **0.0** / 1024 | DRY **desligado** nos dois: medido como causa da mutação de nomes/paths (ver §10) |
| `--cache-type-k` / `--cache-type-v` | `turbo3` | KV em TurboQuant nos dois |
| `--prio` / `--prio-batch` | 2/2 (GPU1) / 1/1 (GPU2) | prioridade da GPU (orquestrador ganha) |
| `-ot` | GPU1 `.*=CUDA0`; GPU2 embd no host, blocos/output na CUDA0 | offload por tensor (E4B) |
| `-t` / `-tb` | 4 (GPU2) | threads — **inertes** em GPU full, usadas no teste CPU (`-ngl 0`) |
| `--alias` | `Gemma4-E4B-Q4` / `Gemma4-E4B-UD-Q3` | id servido (bate com `--model` da bridge) |
| `--api-key` | `guest@123` | auth do upstream (bridge repassa em Bearer) |

> **Form do ícone (`ask_llama_config`).** `-c` e `-np` não são mais fixos no script: a
> janela pede Porta, Usuário, Senha, **Ctx** e **Slots** (labels curtos para telas
> baixas). Defaults: GPU1 `131072/1`, GPU2 `114688/2` (2×56k), CPU `131072/1`;
> `--defaults` mantém esses valores e o subtítulo mostra o `ctx/slots` por slot.

> **CPU puro (`:4120`, `llama-cpu-forced.service`).** Mantém `-ngl 0`, `-t 4 -tb 4`,
> `-c 131072 -np 1` e **não** usa `--no-host`/`--load-mode none`/`--op-offload`
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
  -c 131072 -np 1 -ngl 99 -fa on \
  --cache-type-k turbo3 --cache-type-v turbo3 --mmproj-offload --no-host --load-mode none --op-offload \
  -ot ".*=CUDA0" --prio 2 --prio-batch 2 \
  --repeat-penalty 1.08 --dry-multiplier 0.0 --dry-penalty-last-n 1024 --api-key guest@123
Restart=on-failure
TimeoutStartSec=300
```

Aceite do startup: `n_slots = 1, n_ctx_slot = 131072` → `model loaded` →
`listening on http://127.0.0.1:4100`. Subir **primeiro** (ver §2.5).

### 2.3 Unit da GPU2 (E4B abliterated `:4110`, text-only)

O unit chama-se `llama-gpu2` e
roda **100% GPU** (`-ngl 99`), sem offload para CPU. CPU puro é **outro**
serviço: `llama-cpu-forced.service` (`:4120`, `-ngl 0`). `-t/-tb` é inerte em
GPU full e só importa no teste CPU.

```ini
# /etc/systemd/system/llama-gpu2.service  (100% GPU)
[Service]
Type=simple
User=root
Environment=HOME=/root
Environment=TURBO_AUTO_ASYMMETRIC=0
ExecStart=/usr/local/bin/llama-server --alias Gemma4-E4B-UD-Q3 --host 127.0.0.1 --port 4110 \
  -m /opt/models/huihui-gemma4-e4b-abliterated/Huihui-gemma-4-E4B-it-qat-q4_0-unquantized-abliterated.i1-IQ3_XXS.gguf \
  -c 114688 -np 2 -ngl 99 -fa on -t 4 -tb 4 \
  --cache-type-k turbo3 --cache-type-v turbo3 --no-host --load-mode none --op-offload \
  -ot "^per_layer_token_embd\\..*=CUDA_Host,^token_embd\\..*=CUDA_Host,^blk\\..*=CUDA0,^output\\..*=CUDA0,^output_norm\\..*=CUDA0,^per_layer_model_proj\\..*=CUDA0,^per_layer_proj_norm\\..*=CUDA0" \
  --prio 1 --prio-batch 1 \
  --repeat-penalty 1.08 --dry-multiplier 0.0 --dry-penalty-last-n 1024 --api-key guest@123
Restart=on-failure
TimeoutStartSec=300
```

Aceite do startup: `n_slots = 2, n_ctx_slot = 57344` → `model loaded` →
`listening on http://127.0.0.1:4110` (**114688** total = 2×56k).

> A config atual é **2 slots** (`-np 2 -c 114688` = 2×56k) e
> `limit.context = 57344` no OpenCode (provider `llama`).

### 2.4 TurboQuant é obrigatório nos dois

`Environment=TURBO_AUTO_ASYMMETRIC=0` **+** `--cache-type-k turbo3
--cache-type-v turbo3` **+** `-fa on`, nos **dois** units. Sem a env o
llama.cpp rebaixa o K da GPU2 para `q8_0` (GQA 8:1, `n_head=8`, `n_head_kv=1`):

```
W llama_kv_cache: auto-asymmetric: GQA ratio 8:1 — upgrading K from turbo3 to
q8_0 to prevent quality degradation. Disable with TURBO_AUTO_ASYMMETRIC=0
```

Consequência (medição histórica da linha E2B, modelo aposentado — hoje as duas
linhas são E4B): o `q8_0` gasta mais VRAM por
token que o `turbo3` (GPU2 a 80k/1 com K `q8_0` = 2602 MiB contra 2022 MiB a
96k/1 com `turbo3`). Na dupla E4B atual a VRAM ainda não foi medida neste
chroot (sem CUDA); medir no host.

**Verificação de startup = ausência da linha `auto-asymmetric` no journal:**

```bash
journalctl -u llama-gpu -u llama-gpu2 --no-pager | grep -Ei "auto-asymmetric"
# não deve imprimir nada (auto-asymmetric = K rebaixado para q8_0; não usar)

journalctl -u llama-gpu -u llama-gpu2 --no-pager | grep -Ei "fit params"
# GPU2: um warning "failed to fit params ... n_gpu_layers already set to 99, abort"
# é esperado com -ngl 99 e não impede o load; o que não pode é crash de CUDA graph
# (OOM). Com a dupla E4B atual (Q4_K_M + IQ3_XXS), medir a VRAM no host.
```

### 2.5 Ordem de carga e prioridade

1. **GPU1 (E4B base) primeiro**, depois GPU2 (E4B abliterated). Invertido, a GPU2 pega a VRAM
   primeiro e a GPU1 **aborta** o auto-fit (`failed to fit params to free device
   memory: n_gpu_layers already set by user to 99, abort`) porque `-ngl 99` é
   fixo.
2. Os units `bridge-*` têm `After=`/`Wants=` nos `llama-*.service`, mas o E4B
   ainda pode levar minutos para carregar; a bridge sobe antes e devolve
   `503 Loading model` na corrida. Suba os modelos antes de abrir o OpenCode.
3. `--prio 2` (GPU1, high) e `--prio 1` (GPU2): quando as duas disputam a
   GPU, o orquestrador ganha da subagente.

### 2.6 VRAM medida (12 GB, ambos em `turbo3`)

> **Histórico (linha 12B/E2B).** A tabela abaixo é da configuração anterior.
> Para a linha E4B atual (**Q4_K_M 1×128k** na GPU1 e **IQ3_XXS 2×56k** na GPU2 —
> esta última com os lookups na RAM, ver §8.6, ambos com
> `--no-host --load-mode none --op-offload`) a VRAM ainda não foi medida neste
> chroot (sem CUDA); medir no host com `nvidia-smi` após o boot.

| configuração | GPU1 | GPU2 | total usado | livre |
|---|---|---|---|---|
| E4B Q4 1×128k + E4B IQ3_XXS 2×56k (**atual**) | (a medir) | (a medir) | (a medir) | (a medir) |
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

> **Histórico (12B/E2B).** Os cenários abaixo são da configuração anterior; a
> config atual é **GPU1 `-np 1 -c 131072` (1×128k)** e **GPU2 `-np 2 -c 114688`
> (2×56k)**, com `limit.context = 57344` no provider `llama` do OpenCode.
> Mantidos para referência de medição.

### Cenário A — padrão: ambos residentes (o do E2E)

GPU1 `2×96k` + GPU2 `1×112k` (sem mmproj no E2B) → ~10,91 GiB usados, ~0,85 GiB livres.
É o regime dos testes de integração: as duas linhas servindo ao mesmo tempo no
mesmo GPU (a taxa de cada uma cai sob concorrência). Alvo de VRAM no
boot: ~10,9 GiB usados; margem de segurança ~0,85 GiB para CUDA graphs/decodes.

```bash
./scripts/run-e2e-pipeline.sh --phase 0          # health llama/bridge + VRAM
./scripts/run-e2e-pipeline.sh --phase 1          # unit: PASS=23 FAIL=0 SKIP=3
./scripts/run-e2e-pipeline.sh --phase 2          # E-science + E-mission-tools
./scripts/run-e2e-pipeline.sh --phase 3          # standalone (opencode real, GPU1+GPU2)
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

### Cenário E — CPU Forced (teste de execução granular de scripts)

A linha CPU é **explícita e sempre ativa**: `llama-cpu-forced.service` (`:4120`)
+ `bridge-cpu-forced.service` (`:4126`, bind `0.0.0.0`). O modelo é o
`Gemma4-E4B-Q4` com **`-ngl 0`** (= `--n-gpu-layers 0`), `-t 4 -tb 4`, KV
`turbo3`, `-c 131072 -np 1`. Atenção: `-np` é o número de **slots** — **não** é
o interruptor de CPU; quem desliga a GPU é o `-ngl 0`.

Serve para testar o papel de **execução de scripts granulares** (Scripter/Exec)
sem disputar VRAM com a GPU1; não é o regime de produção, e a bridge expõe
`--cpu-max-tokens` para conter divagação em decode lento (~5 tok/s). A **GPU2**
(`:4110`/`:4125`) roda 100% em GPU (`-ngl 99`) no cenário normal.

```bash
sudo systemctl status llama-cpu-forced bridge-cpu-forced --no-pager
curl -s http://127.0.0.1:4126/api/status | head -c 200
```

Se precisar forçar a **GPU2** em CPU (teste de bancada), o unit é
`llama-gpu2.service`: drop-in trocando só o offload (`-ngl 99` → `-ngl 0`), sem
editar o unit original.

```bash
sudo systemctl edit llama-gpu2.service
# [Service]
# ExecStart=
# ExecStart=<mesma linha do unit atual, com -ngl 0 no lugar de -ngl 99>
sudo systemctl daemon-reload && sudo systemctl restart llama-gpu2
journalctl -u llama-gpu2 --no-pager | grep -E "n_ctx_slot|offloaded" | tail -3
# esperado: "offloaded 0/N layers to GPU"
```

Exemplo — devpipe na linha **CPU Forced** (`:4126`; o filtro `--only` é "contém":
`E-devpipe` roda os 5 casos):

```bash
# CPU puro (-ngl 0), HTTP direto na bridge :4126, role exec
dotnet run -c Release --project Rochas.OpenCodeBridge.Test --no-build -- \
  --only E-devpipe --api-key guest@123 --bridge http://127.0.0.1:4126 --role exec

# um caso só (ferramentas de dev: read/write/edit/glob/grep/git/bash)
dotnet run -c Release --project Rochas.OpenCodeBridge.Test --no-build -- \
  --only E-devpipe-tools --api-key guest@123 --bridge http://127.0.0.1:4126

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
| `--model` | `Gemma4-E4B-Q4` | id servido pelo upstream (deve casar o `--alias`) |
| `--thinking` | `events` | `events` mostra `reasoning_content`; `off` descarta |
| `--thinking-format` | `events` | nome canônico do formato de repasse do reasoning; `--thinking` é alias |
| `--temperature` | `0.3` | global; perfil por sufixo do id vence |
| `--max-tokens` | `0` | teto de saída (0 = sem teto; llama/cliente decidem) |
| `--coerced-max-tokens` / `--cpu-max-tokens` | `0` | teto da linha COAGIDA (upstream sem parser); `--cpu-max-tokens` é alias histórico ("cpu" era inferência por nome de modelo) |
| `--tool-output-limit` | `0` | corte por resultado de tool (0 = integral) |
| `--log` | `/tmp/bridge-gpu.log` | log JSONL |
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
| `--legacy-harness` | `false` | reativa o harness legado (README — "Compatibilidade legada") |
| `--diagnostic` | `false` | sonda Serilog extra (`/tmp/bridge-sonda.log`) |
| `--diagnostic-level` | `off` | `off`/`metrics`/`dump` — granularidade da sonda (metrics = sem corpos req/res) |
| `--qwen-standard` | `false` | **legado**: ModelPrompt + tools Alibaba (Qwen3/Qwen3.5); não usar na linha Gemma 4 |
| `--workspace` | cwd | raiz de `POST /api/tools/execute` |
| `--main-harness` | `true` | substitui o system no caminho gerido (model sem sufixo) |
| `--harness-tails` | `true` | liga as caudas (behavior/tools) conforme `tailScope`; `0` desliga |

> **Legado:** Qwen3/Qwen3.5 não são a linha atual; as flags marcadas (legado) não são usadas na linha Gemma 4.

**Coerção (legado Qwen3/Qwen3.5 e famílias com parser textual):** `--coerce-text-tools 1`
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
| transporte | `--passthrough-responses` · `--stream-per-delta` · `--qwen-standard` (legado) · `--thinking-format` (alias `--thinking`) · `--thinking-off-roles` | — |
| harness/prompt | `--main-harness` · `--harness-tails` · `--harness-behavior-tail` · `--harness-tools-tail` | main harness troca o system no gerido; tails são delta |
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
`--harness-tails 0`, `--context-tokens` sem guard, `--legacy-harness 1`).

### 4.4 Coordenadas — ctx da dupla original + saída 16k

**Llama (dupla E4B em produção):**

| linha | serviço | modelo | ctx / slots | VRAM |
|---|---|---|---|---|
| GPU1 `:4100` | `llama-gpu.service` | Gemma4 E4B base `Q4_K_M` + mmproj Q8_0 | `-c 131072` (1×128k) | (medir no host) |
| GPU2 `:4110` | `llama-gpu2.service` | Huihui Gemma4 E4B abliterated `IQ3_XXS`, text-only | `-c 114688 -np 2` (2×56k) | (medir no host) |
| CPU `:4120` | `llama-cpu-forced.service` | Gemma4 E4B base `Q4_K_M` + mmproj Q8_0, `-ngl 0` | `-c 131072` (1×128k) | 0 (RAM) |
| — | DRY | `--dry-multiplier 0.0` (**off**), `--dry-penalty-last-n 1024` | — | — |
| — | repeat-penalty | `--repeat-penalty 1.08` (os dois), janela 64 | — | — |

Total: **não medido neste chroot (sem CUDA)**; medir no host (RTX 3060, 12288 MiB).

**Bridge (saída 16k):**

| chave | valor | onde |
|---|---|---|
| `--max-tokens` | `16384` | `bridge-gpu.service`, `bridge-cpu.service` |
| runner `max_tokens` (chat) | `16384` | `/tmp/opencode/run-belts-direct.sh` e `E2EBeltsBuildTests.cs:70` |
| runner `max_tokens` (review) | `2048` | `E2EBeltsBuildTests.cs:147` |
| `--anti-repeat` / `--anti-repeat-retry` | `1` | ambas as bridges |
| `--main-harness` / `--harness-tails` / `--tail-scope` / `--legacy-harness` | `0` / `1` / `all` / `0` | todas (master do cliente intacto; tails nos dois caminhos) |
| `--diagnostic` | `0` | ambas (sonda desligada) |
| `--thinking` | `events` | ambas (formato de repasse; o thinking efetivo vem do perfil) |
| `--coerce-text-tools` / `--coerce-narrated` / `--tool-strategy` | `0` | ambas (upstream com parser nativo) |
| `--anti-repeat-window` | `512` | ambas |
| `--passthrough-responses` | `true` | ambas |
| `--upstream-parser` | `native` (default) | implícito — sem hardcode por nome de modelo |

A `bridge-cpu-forced.service` (`:4126`, `-ngl 0`) **não** entra no par 16k:
continua `--max-tokens 8192`, `--context-tokens 131072` e sem os novos
`--harness-tails`/`--anti-repeat-retry`.

**Consequência do 16k:** o turno degenerado (loop de ~120 tokens por volta) morria
em `completion_tokens=16384` (~375 s @43 t/s na E2B, ~15,5k tokens descartados
pelo parser `peg-gemma4` do llama, com apenas ~900 tokens úteis de `content` +
3 `tool_calls`). O contorno (`TryStreamAbortChat`) corta a geração no meio
(~512 chars de texto novo) em vez de pagar o turno inteiro, preservando os
tool calls já fechados e tentando 1 retry com o aviso invisível
(`--anti-repeat-notice`).

Subcomando: `Rochas.OpenCodeBridge.dll gate <repo> "<prompt>" [texto ...] [--review]`.

### 4.5 Princípios dos testes WAR — a bridge é fixa, o teste se adapta (2026-10-25)

**Driver canônico em C#.** A orquestração (lock, pausa, steps, build, redeploy,
restart e gate) vive na solução `.slnx`, em `TestCircuitDriver` → `WarTemplate`
→ `Execution`; a CLI é `--war-*` no projeto de testes. `scripts/war/` é
referência legada. Documento completo: `TEST-DRIVER.md`.

**Regra de ouro.** A bridge tem uma única estratégia estável, a mesma usada por
OpenCode, Web UI e produção: flags, ctx, `max_tokens`, harness e tails. A esteira
WAR mede o modelo **dentro** dessa estratégia. O teste se adapta à bridge; a
bridge nunca se adapta ao teste.

Consequências diretas (proibido para obter PASS):

- baixar/alterar `max_tokens`, ctx, temperatura ou `tool_choice` por belt;
- ligar `--coerce-text-tools`, `--coerce-narrated`, `--tool-strategy`,
  `--main-harness` ou tails especiais só no run;
- trocar a instância/modelo do gate (Q4 `:4124` → Q3 `:4125`) ou usar uma
  instância com tail diferente para "ficar mais fácil";
- afrouxar assertiva, aceite, `maxLength` de schema ou número de arquivos;
- contar PASS de Q3 (diagnóstico) como PASS do gate Q4.

PASS que só existe depois de mudar a bridge, o schema do teste ou a instância é
**PASS indevido**. O resultado honesto é FAIL, com a evidência.

**Horizonte estável de ctx/saída (não oscilar):**

| item | valor estável | fonte |
|---|---|---|
| Q4 `:4124` (gate do L2; L3 conforme driver) | ctx `131072` (1×128k), saída `16384` | `llama-gpu.service` + `bridge-gpu.service` |
| Q3 `:4125` (diagnóstico) | ctx `114688` (2×57344), saída `16384` | `llama-gpu2.service` + `bridge-gpu2.service` |
| Web UI `:4130` | saída `16384` (default do cliente) | `BridgeClient` |
| runner WAR — chat | `max_tokens=16384` | `E2EBeltsBuildTests.cs:70` |
| runner WAR — review | `max_tokens=2048` | `E2EBeltsBuildTests.cs:147` |

A complexidade sobe dentro do mesmo teto: o modelo usa mais dos 16k, divide em
mais turnos/tools ou falha. Não se cria um `max_tokens` por belt. Se um belt
precisar de mais de 16k de saída, isso é sintoma (loop/plano em prosa), não
justificativa para mudar o default de produção.

**Schema do teste = schema de produção.** O `write` de produção não tem
`maxLength` (`ToolCatalog.cs:30`). O teste **adicionava** `content.maxLength=1200`
e o enunciado pedia 400–1200 chars/arquivo. Era um artefato do teste, não um
threshold de produção: distorcia belts complexos (roxa/marrom/preta) e não
podia ser usado para justificar saída baixa. Em 2026-10-25 o cap foi **removido**
do schema e o system prompt passou a pedir implementação (≥3 arquivos), **sem**
afrouxar o aceite (≥3 arquivos, >500 chars, arquivos reais no disco). A mudança
foi do teste para a bridge, nunca o contrário; não foi aplicada para transformar
FAIL em PASS, e sim para remover um limite que a produção não tem.

**`tool_choice=required` e prosa.** A bridge honra `tool_choice` explícito
(bloco `BuildChatRequest` em `Program.cs`); o WAR envia `required`. Se o modelo
devolve prosa e `finish_reason=stop`, é comportamento do modelo/harness
(tail/instrução), **não** falta de `max_tokens` nem de ctx. A correção é na
tail/instrução e a validação é na instância do gate. Coerção
(`coerce-text-tools`) fica `0` em produção e não pode ser ligada para o teste
passar.

**Evidência L2 roxa (2026-10-25 13:50).** `belts-build-roxa-bridge` e
`-opencode`: 3 turnos sem tools, `arquivos=0`, com ~11,3k/14,4k chars de prosa
somados nos turnos. O volume (~3–4k tokens) não mostra corte em 16k, e o ctx de
128k não foi pressionado. O L2 rodou no Q4 (gate) com tail antiga 1019/1527; o
Q3 já tinha 1275/2289. O FAIL do L2 é o resultado honesto do Q4. Um eventual
PASS no Q3 é **diagnóstico** e não substitui o gate até o Q4 rodar a mesma tail
(restart exige autorização).

**Gate Q4 com o enunciado novo (2026-10-25, pós-13:50).**
`belts-build-roxa-bridge` no Q4 (`:4124`, tail 1019/1527) **PASS**: 8 arquivos,
16.327 chars, 213,4 s. Turnos 3/5/7 vieram com prosa (4.630/2.566/2.066 chars);
o nudge recuperou e o turno seguinte chamou `write`. O gate usou a instância
estável, sem restart e sem coerção; o Q3 segue diagnóstico. Confirma que o FAIL
anterior era enunciado, não orçamento.

**Causa do roxa não era teto: verbo do enunciado (2026-10-25).** Um run
isolado no Q3 com a tail nova (1275/2289) repetiu o sintoma: turno 0 com
10.785 chars de prosa e nenhuma tool call. O enunciado começava com
"Especifique ..." e não dizia onde implementar: o modelo entregou uma
especificação, não arquivos. Tail/ctx/`max_tokens` estavam corretos.

**Correção da belt roxa (teste, padrão tools-dual).** O enunciado passou a ser
imperativo e caminhado, como um humano no OpenCode/Web UI:

- `Trabalhe em {ws}. Implemente ...` (não "Especifique");
- `Crie os arquivos .cs e .sql com caminho absoluto dentro de {ws}, usando a
  tool write`;
- ordem explícita: domínio → SQL → repositórios/serviços;
- `não escreva plano, vá direto ao write`; resposta final `BUILD-OK`;
- system prompt sem o cap "400 a 1200 chars"; schema `write` sem `maxLength`
  (igual à produção);
- aceite inalterado: ≥3 arquivos, >500 chars, arquivos reais no disco.

O mesmo padrão do `tools-dual` (caminho absoluto, ação por tool, saída curta)
foi aplicado à belt roxa build; a roxa orch recebeu o enunciado imperativo e
mantém paths relativos no workspace (o Scripter recusa path absoluto).

**Regra de redação de enunciado.** O verbo manda: "Especifique" produz prosa;
"Implemente/Crie" produz tool call. Enunciado humano = objetivo + escopo +
caminho + ação + aceite, sem meta-instrução de planejamento. Ajustar enunciado
é legítimo (teste se adapta à bridge); não é legítimo mexer em ctx,
`max_tokens`, coerção ou instância para passar.

**Critério de correção (>30%).** Comportamento repetido/instável que atinge mais
de 30% dos diálogos da esteira (ex.: roxa build falhou nos 2 transportes, 2/2)
é elegível para correção de enunciado/teste. Abaixo disso, registrar como flaky,
não alterar a bridge nem os defaults. A correção mantém o aceite e é validada
no gate Q4; o Q3 continua diagnóstico.

**Checklist antes de declarar PASS:**

1. `/api/harness` da instância do gate: `main_harness=false`,
   `harness_tails=true`, `tail_scope=all`, `coerce=0`; anotar chars das tails.
2. `/api/status`: upstream/modelo/ctx esperados; instância = a do gate.
3. Request do teste: `max_tokens=16384`, `tool_choice=required`, schema `write`
   igual ao de produção.
4. Evidência: `finish_reason`, `completion_tokens`, tool calls, arquivos no
   disco e aceite inalterado.
5. Se FAIL: registrar causa (teto vs comportamento vs schema) e corrigir
   tail/teste; nunca relaxar o default de produção.

**Onde documentar:** `AGENTS.md` = comportamento do agente/roles/tools;
`docs/LAB-SETUP.md` = referência operacional (esta seção: ctx, max_tokens,
flags, testes); `HISTORY.md` = registro cronológico com evidência. Princípio
novo de orçamento/teste entra aqui e é referenciado no HISTORY.

### 4.2 `appsettings.json` (opcional, ao lado do DLL)

Chaves em **camelCase**, equivalentes 1:1 às flags (`port`, `listen`,
`upstream`, `model`, `thinking`, `temperature`, `maxTokens`, `cpuMaxTokens`,
`toolOutputLimit`, `logPath`, `apiKey`, `passthroughResponses`,
`coerceTextTools`, `coerceNarrated`, `antiRepeat`, `antiRepeatWindow`,
`antiRepeatRetry`, `antiRepeatNotice`, `harnessToolsTail`,
`harnessBehaviorTail`, `diagnostic`, `workspace`, `riskAsk`, `riskAskMinutes`,
`thinkingOffRoles[]`, `toolBlacklist{Role:[tools]}`, `riskToolsAlerts{Role:[regex]}`,
`profiles{role:{temperature,thinking}}`, `contextGuard{enabled,threshold,contextTokens}`,
`jwt{secret,expiryMinutes}`, `usersDb`, `qwenStandard` [legado]).

Regras de precedência:

1. `appsettings.json` **sobrescreve** os defaults do código;
2. **CLI vence o arquivo**;
3. o arquivo tem precedência sobre o dicionário de perfis em `Program.cs` —
   mudar perfil no código **exige** alinhar o `appsettings.json` e reiniciar,
   senão o runtime continua no valor antigo.

No deploy atual (pré-migração .NET 10), o `appsettings.json` do runtime fica em
`/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net9.0`; depois do
deploy do release `2.0.0`, o caminho passa a ser
`/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net10.0`. Os valores
atuais são `profiles.plan.temperature = 0.3`, `profiles.orch.temperature = 0.3`,
`build/scripter/exec = 0.1` e `moe = 0.5`; a unit passa `--temperature 0.5`
como base. As bridges foram reiniciadas depois do ajuste.

Variáveis de ambiente: `DIAGNOSTIC=1` liga a sonda; `MAIN_HARNESS=0` e
`HARNESS_TAILS=0` desligam o main harness e as tails (equivalem às flags).

### 4.3 Units de produção (exemplo aplicado)

```ini
# bridge-gpu.service  (GPU1 / E4B base) — linha única no unit
ExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll \
  --listen 0.0.0.0 --port 4124 --upstream http://127.0.0.1:4100 --model Gemma4-E4B-Q4 \
  --thinking events --temperature 0.5 --max-tokens 16384 --api-key guest@123 \
  --passthrough-responses true --main-harness 0 --harness-tails 1 --tail-scope all --legacy-harness 0 \
  --anti-repeat 1 --anti-repeat-retry 1 \
  --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 \
  --anti-repeat-window 512 --log /tmp/bridge-gpu.log

# bridge-cpu.service  (GPU2 / E4B abliterated)
ExecStart=/usr/share/dotnet/dotnet Rochas.OpenCodeBridge.dll \
  --listen 0.0.0.0 --port 4125 --upstream http://127.0.0.1:4110 --model Gemma4-E4B-UD-Q3 \
  --thinking events --temperature 0.5 --max-tokens 16384 --api-key guest@123 \
  --passthrough-responses true --main-harness 0 --harness-tails 1 --tail-scope all --legacy-harness 0 \
  --anti-repeat 1 --anti-repeat-retry 1 \
  --coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0 \
  --anti-repeat-window 512 --log /tmp/bridge-cpu.log
```

A `bridge-cpu-forced.service` (`:4126`, `-ngl 0`) também roda
`--main-harness 0 --harness-tails 1 --tail-scope all --anti-repeat-retry 1`.

Atalhos de desktop: `OpenCode Bridge GPU/CPU` reiniciam as units e mostram
saúde; `Llama GPU`/`Llama CPU` regeneram e reiniciam os servidores;
`Llama GPU2-CPU Toggle` alterna `-ngl 99` ⇄ `-ngl 0` (benchmark CPU puro).

### 4.6 Ferramentas de bancada oficiais — draw.io

O **draw.io desktop** é ferramenta oficial do ambiente de trabalho para os C4,
fluxos de contrato e diagramas do AI.Manager/OpenCodeBridge. Instalação validada
nesta máquina (LMDE 7, x86_64):

```bash
# pacote oficial (GitHub jgraph/drawio-desktop), versão 32.6.1
cd /tmp/opencode
curl -L --fail -o drawio-amd64-32.6.1.deb \
  https://github.com/jgraph/drawio-desktop/releases/download/v32.6.1/drawio-amd64-32.6.1.deb
sudo dpkg -i drawio-amd64-32.6.1.deb
sudo apt-get install -f -y
drawio --version        # 32.6.1
```

- Binário: `/opt/drawio/drawio`, alternativa `/usr/bin/drawio`.
- Desktop: `~/Desktop/drawio.desktop` (`chmod +x`, dono `mint`), ícone
  `com.jgraph.drawio.desktop`.
- Export headless/CLI (usa o `DISPLAY` da sessão; sem X, instalar `xvfb`):

```bash
drawio -x -f png --size page -s 2.5 \
  -o docs/ai-manager/diagrams/ai-manager-next-steps-c4.png \
  docs/ai-manager/diagrams/ai-manager-next-steps-c4.drawio
```

- Diagramas oficiais ficam em `docs/ai-manager/diagrams/`; o PNG é derivado do
  `.drawio` e ambos entram no commit.
- Não versionar o `.deb` nem o binário. O pacote e o atalho fazem parte da
  imagem/remaster (ver checklist 9.8).

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
./scripts/run-e2e-pipeline.sh --phase all
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

- `../README.md` — resumo expresso (componente, cenário de teste, harness).
- `../AGENTS.md` — regras do operador.
- `AGENTS-PROMPTS.md` — system prompts versionados (fonte dos agentes).
- `../HISTORY.md` — histórico de construção e medições.
- `RUNBOOK-E2E.md` — pipeline por fases e estado do ambiente.
- `PENDENCIAS.md` / `PENDINGS.md` — o que ainda não foi feito.

---

## 8. Ajustes desta sessão (2026-10-23)

### 8.1 Servidores llama

- `:4100` (`llama-gpu.service`): E4B base `Q4_K_M` + mmproj `Q8_0`,
  `-c 131072 -np 1`, `-ngl 99`, KV `turbo3`, `--mmproj-offload --no-host
  --load-mode none --op-offload`, `-ot ".*=CUDA0" --prio 2 --prio-batch 2`.
- `:4110` (`llama-gpu2.service`): Huihui E4B abliterated `i1-IQ3_XXS`
  (`3 982 841 536` bytes, SHA256
  `9043e0b94e6a4f18fa498c5e5fd7e1d86a82dcfe440e1a6587f71882be76cf48`),
  `-c 131072 -np 1`, `-ngl 99`, KV `turbo3`, `--no-host --load-mode none
  --op-offload`, `-ot` com embd no host e `--prio 1 --prio-batch 1`.
- `:4120` (`llama-cpu-forced.service`): E4B base `Q4_K_M` + mmproj `Q8_0`,
  `-c 131072 -np 1`, **`-ngl 0`** (CPU puro), **intocado**.
- `--no-mmap` foi substituído por `--load-mode none` (o `--no-mmap` está
  obsoleto; `none` = sem modo especial de carga). `--no-host` remove o buffer
  host; `--op-offload` leva as operações ao device. O KV continua na VRAM via
  `-ngl 99` (sem flags de KV offload).

### 8.2 Bridge

- Pull `origin/main` + rebuild do DLL
  (`dotnet build Rochas.OpenCodeBridge/Rochas.OpenCodeBridge.csproj -c Release`);
  smoke test em instância descartável OK.
- `:4124`/`:4125` com args canônicos: `--max-tokens 16384`, `--main-harness 0
  --harness-tails 1 --tail-scope all`, `--legacy-harness 0`, `--anti-repeat 1 --anti-repeat-retry 1`,
  `--coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0`,
  `--anti-repeat-window 512`, `--passthrough-responses true` e sem
  `--diagnostic`/`--context-guard`/`--tool-blacklist`/`--thinking-off-roles`.
- `:4126` (`bridge-cpu-forced.service`) **intocada** (`--max-tokens 8192`,
  `--context-tokens 131072`).

### 8.3 OpenCode

- `~/.config/opencode/opencode.json` (root e mint): `limit.output = 16384`;
  ctx `131072` (provider `openai`/`:4124`) e `131072` (provider `llama`/`:4125`).

### 8.4 Ícones de desktop

- Root: `Llama GPU` → `start-llama-gpu.sh`; `Llama GPU2` →
  `start-llama-gpu2.sh`; `Bridge Web UI` → `start-bridge-web.sh`.
- Mint: `Llama GPU`/`Llama GPU2` → `start-llama-gpu.sh`/`start-llama-gpu2.sh`;
  `Llama CPU Forced` → `start-llama-cpu-forced.sh`;
  `OpenCode Bridge GPU`/`GPU2`/`CPU Forced` →
  `start-bridge-gpu.sh`/`start-bridge-gpu2.sh`/`start-bridge-cpu-forced.sh`;
  `Bridge Web UI` → `start-bridge-web.sh`; `draw.io` → `/opt/drawio/drawio`
  (ferramenta oficial de diagramas, ver 4.6).
- A partir do release `2.0.0`, os scripts das bridges chamam
  `dotnet Rochas.OpenCodeBridge.dll` no `bin/Release/net10.0`; o da Web chama
  `Rochas.OpenCodeBridge.Web.dll`. Até o deploy, o PROD continua no
  `bin/Release/net9.0` (rollback imediato). A Web UI sobe na `:4130`;
  `start-bridge-web.sh` não precisou de ajuste (já aponta para o DLL da Web e
  usa `bridge-web`/processo direto).

### 8.5 Observações

- Este chroot não tem systemd (PID 1 = bash) nem CUDA visível: não houve
  `daemon-reload`/`restart` nem medição de VRAM aqui. As units/scripts valem no
  host; ao subir, conferir `n_ctx_slot` (131072/131072), `offloaded ... layers to
  GPU`, ausência de `auto-asymmetric` e VRAM com `nvidia-smi`.
- O warning `cauda de harness declarada com --harness-tails 0: inerte` é esperado
  (o `appsettings.json` mantém `harnessBehaviorTail`, mas `--harness-tails 0` não
  injeta prompt).

### 8.6 Alocação por tensor: GPU1 full-VRAM × GPU2 lookups na RAM

`-ngl 99` sozinho offloada as camadas, mas os *lookups* do Gemma4
(`token_embd` e `per_layer_token_embd`) ainda podem ocupar VRAM. Os units usam
`--override-tensor` (`-ot`) para escolher **onde cada tensor mora** — e só isso:

| linha | pesos | KV | buffers / offload | ctx |
|---|---|---|---|---|
| **GPU1 `:4100`** `Gemma4-E4B-Q4` | **100% VRAM**: `-ot .*=CUDA0` | VRAM (`turbo3`, `-fa on`) | `--no-host` + `--op-offload` (ops na GPU), `--mmproj-offload` | `-c 131072 -np 1` = **1×128k** |
| **GPU2 `:4110`** `Gemma4-E4B-UD-Q3` | blocos/output na CUDA0; **lookups na RAM** (`CUDA_Host`): `token_embd`, `per_layer_token_embd` | VRAM (`turbo3`, `-fa on`) | `--no-host` + `--op-offload`, **nunca CPU offload** de camada (`-ngl 99` sempre) | `-c 114688 -np 2` = **2×56k (57344/slot)** |
| CPU `:4120` `Gemma4-E4B-Q4` | `-ngl 0` (RAM) | RAM | sem `-ot` | `-c 131072 -np 1` = 1×128k |

Regras que não se mexem:

1. **GPU1 é full-VRAM.** `.*=CUDA0` cobre pesos, buffers e KV; offload de ops
   ligado (`--op-offload`), mmproj na GPU (`--mmproj-offload`). 1 slot de 128k.
2. **GPU2 usa a RAM apenas para os lookups** (embedding de token e per-layer
   input) para **caber** e **liberar VRAM** para o KV dos **2 slots**. Não é
   CPU offload: as camadas seguem `-ngl 99` e as operações seguem na GPU
   (`--op-offload`). `-t 4 -tb 4` só dimensiona o lado host desses buffers.
3. **`-ot` não mexe no KV**; o KV continua em `CUDA0` nas duas linhas.
4. **`--context-tokens` da bridge acompanha o slot, não o total**: GPU1
   `131072` (1 slot), GPU2 **`57344`** (2 slots de 56k). Trocar o `-np` da GPU2
   exige trocar esse valor na bridge (`:4125`) junto.

Medição histórica (commit `addade2`): com `-ot ".*=CUDA0"` nos dois, `--mmproj`
removido da GPU1 e ctx **81920/32768**, o boot mediu 6581 MiB (GPU1) + 4685 MiB
(GPU2) = **11623 / 413 MiB**. Essa configuração foi substituída pela atual
(128k nas duas linhas e mmproj de volta na GPU1); a VRAM atual ainda não foi
medida neste chroot (sem CUDA) — medir no host com `nvidia-smi`.

## 9. Ambiente dos ícones, ponte para o OpenCode e checklist de remaster (2026-10-23)

Esta seção é a **memória de remaster**: o host é live USB (overlay), então tudo
abaixo precisa ser gravado na ISO/remaster — nada disso sobrevive a um reboot
sem estar na imagem.

### 9.1 Nomes, portas e units (fim da confusão CPU × GPU2)

| papel | unit | porta | script (fonte da unit) | observação |
|---|---|---|---|---|
| GPU1 (orquestrador) | `llama-gpu.service` | 4100 | `/usr/local/bin/start-llama-gpu.sh` | E4B **Q4_K_M** + mmproj, `-ot .*=CUDA0` |
| **GPU2** (segunda instância, **GPU**) | `llama-gpu2.service` | 4110 | `/usr/local/bin/start-llama-gpu2.sh` | E4B UD-Q3, sem mmproj, `-ot` com embd/per-layer no host |
| CPU puro (bancada) | `llama-cpu-forced.service` | 4120 | `/usr/local/bin/start-llama-cpu-forced.sh` | E4B Q4 base, `-ngl 0` |
| bridge GPU1 | `bridge-gpu.service` | 4124 | `/usr/local/bin/start-bridge-gpu.sh` | Q4, guard 131072 |
| **bridge GPU2** | `bridge-gpu2.service` | 4125 | `/usr/local/bin/start-bridge-gpu2.sh` | Q3, guard 131072 |
| bridge CPU puro | `bridge-cpu-forced.service` | 4126 | `/usr/local/bin/start-bridge-cpu-forced.sh` | Q4 CPU, guard 131072 |

- **Removidos** em 2026-10-23: `llama-cpu.service` e `bridge-cpu.service`
  (nomes enganosos — rodavam 100% GPU). Não recriar no remaster.
- `GPU2` **não** é CPU. CPU puro é sempre `*-cpu-forced` (`-ngl 0`).
- Regra de VRAM: **nunca** offload para CPU nas duas linhas GPU
  (`-ngl 99` fixo; sem `-nkvo`, sem `--no-op-offload`). Só o CPU-forced usa `-ngl 0`.

### 9.2 Launchers zenity (`/usr/local/bin`) — padrão único

- Todos os ícones chamam um script em `/usr/local/bin` que **gera a unit e
  reinicia o serviço**. A unit em `/etc/systemd/system` é derivada; a fonte é o
  script. Mexer na unit à mão é perda de tempo: o próximo clique sobrescreve.
- Todos aceitam **`--defaults`** (sem diálogo, com os mesmos valores do form) e
  seguem o padrão da casa para root: **senha do formulário → `sudo -S`**
  (como `start-nginx.sh`, `start-garnet.sh`, `start-mariadb.sh`).
  Não usar `SUDO_ASKPASS`/`pkexec` nos novos launchers (o `pkexec` existe só em
  `start-docker*.sh`/`start-portainer.sh`, por legado).
- `start-models-default.sh` — sobe/valida o **par principal sem diálogo**
  (`--restart` regenera as 4 units; sem flag só sobe o que estiver fora do ar).
  Pede a senha do sudo no zenity e reexecuta com `sudo -n`.

### 9.3 Ícones (`~/Desktop`, dono `mint`, `chmod +x`)

`Llama GPU.desktop` (4100) · `Llama GPU2.desktop` (4110) ·
`OpenCode Bridge GPU.desktop` (4124) · `OpenCode Bridge GPU2.desktop` (4125) ·
`Llama CPU Forced.desktop` (4120) · `OpenCode Bridge CPU Forced.desktop` (4126) ·
`Llama GPU2 + Bridge GPU2 (padrao).desktop` (launcher sem diálogo) ·
`draw.io.desktop` (ferramenta oficial de diagramas C4, ver 4.6) ·
`Compartilhar-Samba.desktop` (ver 9.6).

### 9.4 Bridges — args canônicos e o que realmente está ativo

Args gravados nas duas units (`bridge-gpu` e `bridge-gpu2`):

```
--thinking events --temperature 0.5 --max-tokens 16384
--context-guard 1 --context-tokens 131072
--passthrough-responses true --harness-tails 0 --legacy-harness 0
--anti-repeat 1 --anti-repeat-retry 1 --anti-repeat-window 512
--coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0
--upstream-parser native
```

- **anti-repeat está ligado nos dois caminhos** (chat e Responses): é contenção
  de stream/abort no upstream, independe de harness e de sufixo de modelo.
- **tool-shim fica desligado** (`--tool-shim ""`) na ponte do OpenCode. Motivo
  medido em 2026-10-23: `/v1/responses` + tools, **sem shim**, devolve
  `response.function_call_arguments.delta/done` nativo nas duas portas
  (`:4124` e `:4125`, 4 deltas cada). O shim força upstream non-stream e desliga
  thinking em turno com tools (perda de UX/qualidade) — use-o só no caminho puro
  de testes/WebUI (`--tool-shim json --tool-shim-max-string 1200
  --tool-shim-min-calls 3`), nunca por padrão na ponte do OpenCode.
- **context-guard: aceito, reportado — e hoje inerte.** `--context-tokens 131072`
  aparece em `/api/status` (`config_groups.orcamento`), mas o 413
  `context_compact_required` não dispara porque (a) a guarda vive só no ramo
  agregado non-stream do `Passthrough` (chat) e (b) com `--anti-repeat-abort 1`
  o request non-stream é atendido antes por `TryStreamAbortChat`, que não tem
  guarda; e (c) o OpenCode usa `/v1/responses` (`HandleResponses`), também sem
  guarda. Prova: instância descartável (porta efêmera) com `--context-tokens 1000`
  recebeu prompt de 3734 tokens → **HTTP 200**. A proteção real hoje é o
  `limit.context = 131072` do opencode + compactação do próprio cliente.
  **Pendência para a próxima release:** estimar tokens na entrada (pré-flight)
  e/ou plugar a guarda em `HandleResponses` e no ramo de stream-abort.
- Com `--harness-tails 0` as tails não entram em nenhum caminho: o OpenCode já
  roda o "tubo puro". Não é preciso "configurar caminho puro" para ele — só para
  WebUI/testes sem sufixo.

### 9.5 OpenCode (`~/.config/opencode/opencode.json`)

- provider `openai` → `:4124` (**Q4 1×128k**, `limit.context = 131072`) e
  provider `llama` → `:4125` (**Q3 2×56k**, `limit.context = 57344`);
  `limit.output = 16384` nos dois (bate com `--max-tokens 16384` da bridge).
- Nomes de exibição da linha Q3 usam **GPU2** (não "CPU").
- `providers.llama` e `providers.cpu` exigem
  `"package": "@ai-sdk/openai-compatible"` (v2). Após editar:
  `opencode reload` ou reabrir a TUI.
- **Só 21 modelos habilitados** (6 Q4 + 6 Q3 + 9 do grupo `opencode` *free*).
  O provider `openai` usa o mesmo id do provider nativo do OpenCode, então o
  catálogo do models.dev injeta ~80 modelos (`gpt-5`, `gpt-4o`, ...) que **não
  existem** no bridge local. Eles ficam com `"disabled": true` em
  `providers.openai.models.<id>` (chave aceita pelo plugin
  `opencode.config.provider`: `if (S.disabled !== undefined) T.enabled = !S.disabled`).
  Para reabilitar um modelo do catálogo, troque para `"disabled": false` (ou
  remova a chave) e rode `opencode reload`.
- **Visibilidade padrão (Q3 igual ao Q4)**: `~/.local/state/opencode/model.json`
  guarda `{recent, favorite}` do seletor de modelos. Os 12 locais entram em
  `favorite` (bloco "Favorites" no topo do seletor), então Q4 e Q3 aparecem
  iguais sem precisar habilitar nada em gerenciamento. Formato de cada item:
  `{"providerID":"llama","modelID":"Gemma4-E4B-UD-Q3-exec"}`.
- Após editar o `opencode.json`: `opencode reload` (recarrega a config no
  servidor sem derrubar sessão). A TUI lê `model.json` no start: reabra a TUI
  para ver o bloco de favoritos.

### 9.6 Compartilhar Samba (`/usr/local/bin/share-samba.sh`)

- Form único (`zenity --forms`): **Modo** (`Leitura|Escrita|Anonimo`), caminho,
  nome do share, usuário e senha de rede. `Leitura`/`Escrita` **exigem**
  usuário+senha (padrões `mint`/`guest@123`); `Anonimo` dispensa (guest ok,
  read only).
- Grava em `/etc/samba/share-zenity.conf` e adiciona **uma vez**
  `include = /etc/samba/share-zenity.conf` no `smb.conf` (não reescreve o
  principal). Backup `smb.conf.bak-<stamp>`, `testparm` obrigatório com rollback
  se rejeitar, `smbpasswd -a -e` para o usuário de escrita, `systemctl restart smbd`.
- **Root é necessário** (escreve `/etc/samba`, roda `smbpasswd`, reinicia `smbd`).
  `usershare` sem root não serve nesta imagem: `usershare max shares` está
  comentado (=0 desabilitado). A elevação segue o padrão da casa: senha do form
  → `sudo -S`; no modo Anônimo (sem senha no form) pede uma vez com
  `zenity --password`.
- Teste não interativo (hooks): `SMB_ARGS="Modo|Caminho|Nome|Usuario|Senha"`,
  `SMB_CONF_OVERRIDE`, `SMB_INC_OVERRIDE`, `SMB_SKIP_RESTART=1`.
  Validado em 2026-10-23: Escrita, Leitura e Anonimo, com idempotência
  (repetir o mesmo share não duplica bloco) e `testparm` OK.

### 9.7 Medições desta rodada (live, RTX 3060 12 GB)

- `n_ctx` real conferido nas duas portas: **131072** (`/v1/models` → `meta.n_ctx`).
- VRAM com os dois residentes: **10759 MiB usados / 1277 livres**
  (GPU1 `:4100` 7643 MiB, GPU2 `:4110` 2755 MiB) — `-ot` do GPU2 devolve ~400 MiB
  ao host sem offload de CPU.
- Pipes: `/v1/responses` + tools com function call nativo nas duas portas;
  `start-models-default.sh` fecha em `rc=0` com os 4 serviços `[OK]`.

### 9.8 Checklist de remaster (gravar na imagem)

1. Scripts `/usr/local/bin/start-*.sh` e `svc-common.sh`/`svc-firewall.sh`
   (este repositório **não** versiona os launchers — eles vivem na imagem).
2. Units geradas: `llama-gpu`, `llama-gpu2`, `llama-cpu-forced`, `bridge-gpu`,
   `bridge-gpu2`, `bridge-cpu-forced` (+ `systemctl enable` se quiser boot
   automático; hoje estão `disabled` e sobem pelos ícones/launcher).
3. Ícones `.desktop` em `/home/mint/Desktop` com `chmod +x` e dono `mint`,
   incluindo `draw.io.desktop` → `/opt/drawio/drawio`.
4. `~/.config/opencode/opencode.json` com `131072/16384` nos dois providers.
   draw.io desktop 32.6.1 instalado (`.deb` oficial) e ícone
   `com.jgraph.drawio.desktop` disponível.
5. `/etc/samba/share-zenity.conf` + a linha `include` no `smb.conf` (se já
   houver shares configurados).
6. **Não** gravar: `llama-cpu.service`, `bridge-cpu.service`,
   `sudo-askpass-zenity.sh`, `share-samba-sudo.sh`, `start_inference.sh`
   (artefatos da tentativa quebrada de 2026-10-23, removidos).
7. Aceite no boot do remaster:
   `nvidia-smi`, `curl -s localhost:4110/v1/models | jq .data[0].meta.n_ctx`
   (131072), `curl -sH 'Authorization: Bearer guest@123' localhost:4125/api/status`,
   `/usr/local/bin/start-models-default.sh` (rc=0).

## 0.1 Contas isoladas do OpenCode (host autentica, bridge não)

| conta | uid | shell | acesso | workspace | porta |
|---|---|---|---|---|---|
| `moe-agent` | 1001 | `/usr/sbin/nologin` | só Web (chat-only) | `/home/moe-agent/workspace` | `:4220` (Caddy `4250`) |
| `dev-agent` | 1002 | `/bin/bash` | TUI via SSH:22 + Web | `/home/dev-agent/workspace` | `:4210` (Caddy `4240`) |
| `mint` | 1000 | `/bin/bash` | TUI/Web completo | livre | `:4200` (Caddy `4230`) |

- Nenhuma tem sudo/docker; homes `750`; `hidden-users` no LightDM.
- Sandbox: `bwrap` via `/usr/local/bin/ocagent-sandbox.sh` (home tmpfs + bind do próprio home;
  `/media`, `/mnt`, `/opt`, `/root`, `/srv` vazios; `--share-net` para `127.0.0.1:4125`).
- Serviço gerenciado do OpenCode por conta: `service.json` com `49376` (dev) e `49377` (moe).
  A `49374` é do `mint` — colisão causa sessão que morre sem mensagem.
- `dev-agent` vê **somente** Q3 (6 modelos `llama/Gemma4-E4B-UD-Q3*`) e os papéis
  Build/Plan/MoE/Scripter; `moe-agent` vê só `Gemma4-E4B-UD-Q3-moe` (papel MoE, chat-only).
- Bridges continuam em `0.0.0.0` com ApiKey (consumo direto sem UI); o Caddy publica só as
  portas do OpenCode Web, com basicauth por conta.

## 0.2 Atualização do chroot (2026-10-23) — GPU2 2×56k e smoke CPU

- A linha **GPU2** usa `llama-gpu2` com `-c 114688 -np 2` = **2×56k**; a bridge
  `bridge-gpu2` usa `SVC_CTX=57344` / `--context-tokens 57344` e o `opencode.json`
  (`llama`) usa `limit.context: 57344`. Menções históricas a "GPU2 1×128k"
  (cenários das seções 2.x/9.x) descrevem a configuração anterior.
- Portas públicas do Caddy por conta: `4230` (mint), `4240` (dev-agent), `4250`
  (moe-agent). A lista do firewall é a de `setup-ufw-remaster.sh` — fonte única, **não**
  é alterada por teste: bancada roda em porta livre e a liberação é descartada junto.
- Chroot aplicado: contas 1001/1002, homes `750`, `service.json` 49376/49377,
  LightDM hidden, units `disabled` (sem symlink). `AgentSeeder` → 10 agentes,
  10/10 com cláusula, 0 `(CPU Forced)`. Build Release 0/0.
- Smoke CPU no chroot: `:4120` (llama `-ngl 0`, n_ctx 131072) e `:4126`
  (bridge) responderam `ok`; GPU indisponível no ambiente. `bwrap` não monta
  `/proc` no chroot quando cria user+PID namespace (`mount -t proc` retorna
  `EPERM` no runtime externo) — validar no host live; não é falta de `CAP_SYS_ADMIN`
  no processo do agente.

## 10. DRY desligado nos dois llama (2026-10-24) — mutação de nomes era sampler

**Sintoma (uso real e testes):** o modelo troca letras/dígitos de nomes que o
usuário acabou de escrever, sobretudo **na hora de gravar via tool**:
`placa.png` → `placa.ng`, `placa.PNG`; `placa-001.png` →
`placa-placa-001.txt`; `TESTE-APAGAR-123` → `mkdir TESTE-APAGAR-2024_subfolder`,
`TESTE-APAGAR-124`, `TESTE-APAGRAR-123`.

**Medição (`scripts/war/param_matrix.py`, Q4 `:4100` e Q3 `:4110`):** duas
variantes de cenário — `chat-vision` (imagem + tool `read_file`, **sem** tails) e
`resp-path` (Responses com tool `write`, com tails/anti-repeat conforme a
variante). Métrica = argumento emitido igual ao path pedido.

| eixo | variantes | path exato |
|---|---|---|
| **DRY 0.8** | rp 1.0 e 1.1 × tails on/off × anti-repeat on/off × main harness | **0%** (0/64) |
| **DRY 0.0** | idem | **100%** (64/64) |
| repeat-penalty | 1.0 × 1.1 | sem diferença |
| harness | tails on, tails off, anti-repeat on/off, main harness | sem diferença |
| temperatura | 0.0 / 0.1 / 0.2 | sem diferença (com DRY off: 100%) |

**Causa:** o sampler DRY penaliza sequências repetidas — exatamente o
identificador que precisa ser **copiado verbatim** do prompt para o argumento da
tool. A mutação não vinha das tails nem do anti-repeat da bridge: reproduz no
caminho `chat`, que não recebe tail nenhuma.

**Mudança aplicada na infra (host live):**

```
/etc/systemd/system/llama-gpu.service          --dry-multiplier 0.8 -> 0.0
/etc/systemd/system/llama-gpu2.service         --dry-multiplier 0.8 -> 0.0
/etc/systemd/system/llama-cpu-forced.service   --dry-multiplier 0.8 -> 0.0
```

- Script idempotente (faz backup `.bak-<stamp>` e `daemon-reload`):
  `scripts/lab/llama-dry-fix.sh --dry 0.0` (reverter: `--restore`).
- `--repeat-penalty` ajustado de **1.1 → 1.08** (2026-10-25): valor recomendado
  pela comunidade llama.cpp para reduzir repetição sem penalizar nomes, paths e
  dígitos copiados verbatim em tool calls. Aplicado nas três linhas (`:4100`,
  `:4110`, `:4120`) e nos geradores do Desktop; o anti-repeat textual da bridge
  continua como última linha.
- Verificação: `pgrep -af llama-server` mostra `--dry-multiplier 0.0` nas três
  instâncias (`:4100`, `:4110`, `:4120`); `/health` 200.

**Sonda em uso real (`:4125`):** drop-in
`/etc/systemd/system/bridge-gpu2.service.d/diagnostic.conf` acrescenta
`--diagnostic 1` (o resto do `ExecStart` é o mesmo do unit). Reverter:
`sudo rm -rf /etc/systemd/system/bridge-gpu2.service.d && sudo systemctl daemon-reload && sudo systemctl restart bridge-gpu2`.
Com a sonda, `/tmp/bridge-gpu2.log` registra `traffic`/`response`/`tool-*` por
request — usar junto de `scripts/war/probe_session.py --session <id>` para ver,
ao vivo, o args que o modelo emitiu e o que a bridge fez.

**Regra que fica:** parâmetro de amostragem é infra, não harness. Para copiar
nome/caminho verbatim, DRY fica **off**; qualquer camada nova de correção na
bridge exige medição primeiro (ver `PENDINGS.md`).

## 11. Um tool call por resposta — `parallel_tool_calls: false` (2026-10-24)

Decisão do lab: **um tool call por resposta**, para passo determinístico e
contável. O default do servidor não garante single-call, então a bridge
explicita o campo.

**Mudança:** `BuildChatRequest` envia `"parallel_tool_calls": false` sempre que há
tools, e os dois objetos de resposta (shell do SSE e resposta única) reportam
`false`. Não é flag: é comportamento padrão. O opencode continua emitindo vários
**passos**; cada passo traz um call.

**Verificação:** `curl -s :4124/api/status` mostra a bridge up; o corpo enviado
ao llama sai com o campo (conferir em `traffic` com `--diagnostic 1`). A suíte de
níveis (`BENCHTESTS.md`) usa esse mesmo build nas bridges de bancada.

Referência do flag: llama.cpp#21375.

## 12. Política de bind e estado do DRY (2026-10-24)

### Bind — interno × exposto

Regra da release: **porta interna escuta só em loopback; porta de serviço de cara
para o cliente escuta em `0.0.0.0`**.

| porta | bind | o que é |
|---|---|---|
| `4100` / `4110` / `4120` | `127.0.0.1` | llama Q4 / Q3 / CPU forçada (**interno**) |
| `4210` / `4220` | `127.0.0.1` | opencode web dev-agent / moe-agent (**interno**) |
| `4124` / `4125` / `4126` | `0.0.0.0` | bridges (única face p/ o llama, `--api-key`) |
| `4130` | `0.0.0.0` | Web UI (`Core` in-process, não chama bridge) |
| `4200` | **`0.0.0.0`** | opencode web principal — **indispensável na próxima remaster** |

**Pegadinha do `4200`:** ele é configurado em **dois** lugares — em
`/etc/systemd/system/opencode-web.service` **e** em `/usr/local/bin/start-opencodeweb.sh`
(o ícone sobe processo próprio com pidfile `/run/opencodeweb.pid`, fora do systemd).
Mudar só a unit faz o próximo clique no ícone reverter para `127.0.0.1`. Os dois arquivos
precisam sair com `--hostname 0.0.0.0` no pacote da remaster (critério de pronto).
Os `start-*.sh` dos outros serviços seguem a mesma lógica de duplicação: unit e script
andam em par.

```bash
ss -tlnH | grep -E ':(4100|4110|4120|4210|4220)\b'    # 127.0.0.1
ss -tlnH | grep -E ':(4124|4125|4126|4130|4200)\b'    # 0.0.0.0
curl -s -m3 -o /dev/null -w '%{http_code}\n' http://<IP-LAN>:4200/
```

### DRY do llama

Os três units (e os três processos vivos) estão com `--dry-multiplier 0.0` — o
sintoma de mutação de nomes/paths era o sampler (§10). `--repeat-penalty 1.08`
permanece.

```bash
grep -o -- '--dry-multiplier [^ ]*' /etc/systemd/system/llama-*.service   # 0.0 nos 3
for p in $(pgrep -f llama-server); do tr '\0' ' ' < /proc/$p/cmdline | grep -o -- '--port [0-9]*\|--dry-multiplier [^ ]*'; echo; done
```

## 13. Agentes do opencode — um role por comportamento (sem duplicata `*-cpu`)

Regra: **role = comportamento** (`plan`, `build`, `orch`, `moe`, `scripter`, `exec`). O que
muda entre as linhas é só o **modelo** escolhido — nunca o role.

| linha | provider | modelo escolhido | encadeamento |
|---|---|---|---|
| GPU1 Q4 | `openai` | `Gemma4-E4B-Q4-<role>` | bridge `:4124` → llama `:4100` |
| GPU2 Q3 | `llama` | `Gemma4-E4B-UD-Q3-<role>` | bridge `:4125` → llama `:4110` |
| CPU forced | `cpu` | `Gemma4-E4B-Q4-<role>` | bridge `:4126` → llama `:4120` |

Config em `~/.config/opencode/agents/`: `build.md`, `exec.md`, `moe.md`, `orch.md`,
`plan.md`, `scripter.md` (os homes isolados `dev-agent`/`moe-agent` têm também
`build-exec.md`).

**Removidas as duplicatas** `build-cpu.md`, `exec-cpu.md`, `moe-cpu.md`, `orch-cpu.md`,
`plan-cpu.md`, `scripter-cpu.md`: eram o mesmo role com outra linha de modelo e
`(CPU Forced)` na descrição. Para rodar um role na linha CPU basta selecionar
`cpu/Gemma4-E4B-Q4-<role>` no seletor de modelo — system prompt, permissões e tools são
idênticos. O provider `cpu` **continua** no `opencode.json` (é ele que dá sentido ao
sufixo). Backup dos arquivos removidos:
`/tmp/opencode/agents-cpu-backup-<stamp>/`.

O sufixo de role no **nome do modelo** é o que a bridge usa para aplicar o perfil
(temp/thinking/coerção): `Gemma4-E4B-Q4-build` ≠ `Gemma4-E4B-Q4`. Sem sufixo, roda o
perfil base.

## 14. Persistência dos artefatos de teste (sda3, `logs/`)

Regra: execução no live grava log/artefato em **disco não volátil** — `logs/levels/`, na
`/dev/sda3` (montada em `/media/mint/78AA0831AA07EB08`, 160 GB), sob
`/Projetos/Git/OpenCodeBridge/logs`. `/tmp` não é destino final de execução de teste.

- Default do runner: `OUT="${LEVEL_OUT:-$ROOT/logs/levels}/L<level>-<stamp>"`
  (`LEVEL_OUT` sobrepõe; `LEVEL_STAMP` nomeia).
- Conteúdo por run: `summary.tsv`, `summary.txt`, `steps/*.test.log`,
  `steps/*.bridge.log` (delta do log da bridge), `state/*.done`.
- `logs/` está no `.gitignore`: é artefato de execução, não fonte.
- Bridge de bancada: `--log` também sob `logs/` (ex.: `logs/bridge-q4-level1.log`).
- Retenção: o disco tem ~20 GB livres — limpar runs antigas com
  `ls -1dt logs/levels/L* | tail -n +11 | xargs rm -rf` (mantém as 10 últimas).

### Flags efetivas dos serviços (retrato desta release)

| serviço | flags-chave |
|---|---|
| bridges `:4124` / `:4125` | `--main-harness 0 --harness-tails 1 --legacy-harness 0 --anti-repeat 1 --anti-repeat-retry 1 --anti-repeat-window 512 --passthrough-responses true`; request com **`parallel_tool_calls: false`** (comportamento do código, não flag — §11) |
| bridge `:4126` (CPU forced) | **parada e desabilitada** (o usuário não ligou a linha CPU); unit preservada com `--harness-tails 1` para quando for ativada |
| Web UI `:4130` | `Core` in-process: `mainHarness`, `harnessTails` e `antiRepeat` nos defaults (ligados) — o `BridgeUrl` do agente só dimensiona contexto |
| llama `:4100` / `:4110` / `:4120` | `--dry-multiplier 0.0`, `--repeat-penalty 1.08` (§10) |

- O alias **`--harness`** não existe mais: o binário atual recusa no boot. Os
  `start-bridge-*.sh` reescrevem a unit do zero — já emitindo
  `--main-harness 0 --harness-tails 0`; se um pacote antigo voltar, o clique no ícone
  recria a unit quebrada (mesmo padrão do `4200` acima).
## Estado final do ambiente (2026-10-25, pós-deploy)

- **Produção** `/usr/src/opencodebridge` em **`6e49fd3`** (`git reset --hard origin/main`,
  `dotnet build -c Release` 0 avisos/0 erros). Persistente no mesmo commit.
- **GPU1 `:4124`** — `Gemma4-E4B-Q4`, `-c 131072 -np 1` (1×128k), bridge
  `--context-tokens 131072`, **tails ON** (`tail_scope=all`), `anti_repeat=true`,
  breaker `soft=3/hard=6`, `passthrough_responses=true`.
- **GPU2 `:4125`** — `Gemma4-E4B-UD-Q3`, `-c 114688 -np 2` (2×57344), bridge
  `--context-tokens 57344`, **tails ON** (`tail_scope=all`), 100% GPU, `anti_repeat=true`.
- **CPU Forced `:4120/:4126`** — **parada e desabilitada**; VRAM liberada (~906 MiB).
- **Logs**: `:4124` → `/tmp/bridge-gpu.log`; `:4125` → `/tmp/bridge-gpu2.log`
  (o antigo `/tmp/bridge-cpu.log` não é mais escrito); `:4126` →
  `/tmp/bridge-cpu-forced.log` (quando ativa).
- **Anti-repeat de tools**: vale no caminho **`/v1/responses`** (OpenCode/provider
  `openai`, WAR e agentes GPU1). A assinatura agora canonicaliza JSON
  (`{"a": 1}` == `{"a":1}`), então chamada idêntica reemitida é bloqueada e vira
  `AntiRepeatNotice` (`anti-repeat-loop` no log).
- **Limitação conhecida**: o passthrough **`/v1/chat/completions`** (provider
  `llama`/openai-chat, usado pelo dev-agent na GPU2) **não tem** o anti-repeat de
  tools — ele só repassa o stream. O loop do dev-agent (`--add --config=true`
  repetido) aconteceu nesse caminho; lá a contenção é enunciado + tails. Guarda
  de tools no passthrough chat fica como próximo passo (não aplicar com WAR ativa).
- **Tails**: `HarnessTails.DefaultTools` inclui `GIT-RAIZ` (repo nasce na pasta do
  projeto; conferir `git -C <dir> rev-parse --show-toplevel`) e `COMANDO INVALIDO`
  (`unknown option`/`usage:` não repete a mesma chamada).
- **WAR**: reiniciada do começo em Q4 (`run-all-levels.sh`), stamp
  **`L1-20261025-130450-l1`**, driver em `logs/levels/driver-console.out`.
- **Cloudflare**: `code/dev/moe/home` respondendo 200 (inalterado).
