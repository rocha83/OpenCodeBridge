# Temperatura por role — tabela canônica, precedência e política de variância

> **Escala em vigor desde 2026-11-07.** Este documento é a fonte de leitura
> humana; a fonte **de código** é `Rochas.OpenCodeBridge.Core/RoleProfiles.cs`
> (`RoleProfiles.Defaults`). Se as duas divergirem, o código vence e este doc
> está errado — abra issue/corrija no mesmo commit.

## 1. Tabela canônica

| role | temperatura | thinking | papel |
|---|---|---|---|
| `plan` | **0.2** | events | elucida, fatia em micro-enunciados, verifica ACEITE |
| `orch` | **0.2** | events | orquestra/delega e agrega subagentes |
| `moe` | **0.4** | events | exploração/generalista (assuntos globais) |
| `build` | **0.1** | off | executa: escreve código, roda build/teste |
| `scripter` | **0.1** | off | compõe scripts (`bash -n`), sem `exec` |
| `exec` | **0.1** | off | valida (`bash -n`) **e executa** (`bash <arquivo>`) |
| *(sem sufixo)* | **0.2** | conforme a unit | modelo base / conversa geral |

Escala: **0.1 baixa** (execução/determinismo), **0.2 média-baixa**
(planejamento), **0.4 média-alta** (exploração). Não existe mais 0.3/0.5 como
valor de role; 0.3/0.5/0.7/0.8 só aparecem como *valores legados* em migrações.

## 2. Onde cada valor mora (e quem precisa ser atualizado junto)

Mudar temperatura **não** é editar um arquivo só. A lista completa:

| # | Arquivo | O que contém | Observação |
|---|---|---|---|
| 1 | `Rochas.OpenCodeBridge.Core/RoleProfiles.cs` | `Defaults` (plan/build/orch/moe/scripter/exec) | **fonte única**; `Sampling`/retry leem daqui |
| 2 | `Rochas.OpenCodeBridge/appsettings.json` | `profiles.*.temperature` + `_comment` | runtime do PROD; sobrescreve o default do Core |
| 3 | `Rochas.OpenCodeBridge/appsettings.4124.gpu-example.json` | perfis + `temperature` global | exemplo versionado da GPU1 |
| 4 | `Rochas.OpenCodeBridge/appsettings.4125.cpu-example.json` | perfis + `temperature` global | exemplo versionado da GPU2/CPU |
| 5 | `Rochas.OpenCodeBridge/appsettings.4124.orch-off-example.json` | override `orch` | exemplo de `thinking off` |
| 6 | `~/.config/opencode/opencode.json` (mint, dev-agent, moe-agent) | `providers.*.models.*.temperature` e o rótulo `name` | **cliente**; o perfil do role vence no túnel, mas o valor do cliente aparece em `temperature_source=request` |
| 7 | `~/.config/opencode/AGENTS.md` | seção `TEMPERATURA` e descrição dos modelos | instruções do agente |
| 8 | `Rochas.OpenCodeBridge.Web/Data/AgentSeeder.cs` | seeds 1–10 + `PromoteRoleTemperatures` + `MigrateLegacyConfig` | banco SQLite da Web UI; migração roda a cada boot |
| 9 | `Rochas.OpenCodeBridge.Web/Controllers/AgentsController.cs` | `DefaultAgent()` | preset do form "Novo agente" |
| 10 | `Rochas.OpenCodeBridge.Web/Services/ToolExecutor.cs` | fallback `agent?.Temperature ?? 0.2` | agente sem temperatura no banco |
| 11 | `Rochas.OpenCodeBridge/Program.cs` | `Temperature = 0.2` (global) | fallback sem role |
| 12 | `Rochas.OpenCodeBridge.Core/BridgeOptions.cs` | default de `Model` | id servido (GK-Q4) |
| 13 | `scripts/war/promote-creativity.sh` | `TARGET` (mapa por sufixo) | ferramenta de normalização dos homes |
| 14 | `docs/LAB-SETUP.md`, `README.md`, `docs/RUNBOOK-E2E.md`, `docs/AGENTS-PROMPTS.md`, `docs/BENCHTESTS.md`, `HISTORY.md` | tabelas/exemplos | documentação |

Unidades systemd **não** carregam a tabela por role: elas passam só o global
(`--temperature 0.2`) e o modo (`--thinking`). O perfil por sufixo do model id
vem do `appsettings.json` ao lado do DLL.

### 2.1 Modelos e ids (não confundir com a temperatura)

| linha | id servido pelo llama | bridge | uso |
|---|---|---|---|
| GPU1 (GGUF retreinado `Gemma4-E4B-UCD-IT_Principal-GK-DS-Retrained-Q4_K_M-Imatrix`) | **`Gemma4-E4B-GK-Q4`** | `:4124` → `:4100` | Plan/Build/Orch/MoE/Scripter + conversa |
| GPU2 (aposentada, `llama-gpu2` disabled) | `Gemma4-E4B-UD-Q3` | `:4125` → `:4110` | só se religar a unit |
| CPU Forced (`-ngl 0`, `google_gemma-4-E4B-it-Q4_K_M`) | `Gemma4-E4B-Q4` | `:4126` → `:4120` | cenário de execução sem VRAM |

O cliente pode pedir `Gemma4-E4B-GK-Q4-plan` (preferido) ou o alias legado
`Gemma4-E4B-Q4-plan`: a bridge **sempre** encaminha o `Model` configurado no
upstream e devolve o id pedido como eco. Quem fala **direto** com o llama
(caminho Web UI, `UpstreamUrl=:4100`) precisa usar o id **servido**
(`Gemma4-E4B-GK-Q4`) — é por isso que o seeder da Web UI foi migrado.
Os ids `gemma-4-12b` e `gemma-4-e2b` estão **mortos**: a migração os converte
para GK-Q4 (GPU) e E4B-Q4 (CPU), respectivamente.

## 3. Precedência

```
temperature do corpo do request  >  perfil do role (sufixo do model id)  >  global (--temperature / appsettings.temperature)
```

- O `role` é casado pelo **sufixo** do id (`-plan`, `-build`, `-orch`, `-moe`,
  `-scripter`, `-exec`), case-insensitive.
- A procedência sai no campo `temperature_source` (`request` | `profile` |
  `global`) da resposta e no log — a telemetria não mente sobre a origem.
- `--profile-temp-wins` inverte o primeiro par (perfil vence o cliente). O
  default é **cliente vence**; por isso o `opencode.json` precisa espelhar a
  tabela: se ele mandar 0.5, o role roda 0.5 mesmo com o perfil em 0.4.
- O turno de **retry/reconsideração** usa a temperatura do role `plan`
  (`RoleProfiles.DetourRole`), via `Sampling.ForRetry`, e nunca esfria um turno
  que já estava mais quente. `--aux-temp` é o análogo global do desvio.

## 4. Por que 0.1 / 0.2 / 0.4 (e não 0.3 / 0.5)

Medição de 2026-11-07 na linha **E-ent** (conceitual, texto puro, `-plan`,
`temperature 0.3`), mesmo enunciado, amostras independentes:

| sintoma | frequência observada | leitura |
|---|---|---|
| artefato correto sem o literal `409` (usou `Results.Conflict`) | comum | assert por **contrato** resolveu (não é defeito do modelo) |
| paginação só com `LIMIT` (sem `OFFSET`) | comum | dialeto Postgres fixado no enunciado |
| `NotImplementedException` num CRUD simples | eventual | variância de amostragem, não incapacidade |
| prosa de ~14k tokens em step de texto (sem `tool_choice`) | eventual | template do Gemma 4 ignora `tool_choice: required` |

Conclusão: o conteúdo estava **certo**; o que variava era a *forma*. Duas
alavancas corretas, nesta ordem:

1. **Fixar o contrato no enunciado** (dialeto, endpoints, o que é obrigatório).
2. **Baixar a temperatura na fonte** — 0.2 no planejamento e 0.4 na exploração
   reduz a cauda sem matar a diversidade útil. Build/Scripter/Exec já estavam em
   0.1 e continuam: execução precisa de determinismo.

O que **não** se faz: retry no assert. Ver §6.

## 5. Como mudar a escala (checklist)

1. `RoleProfiles.cs` → `Defaults` (e o `<summary>` da tabela).
2. `appsettings.json` → `profiles.*` + `_comment`.
3. Os três `appsettings.*-example.json`.
4. `~/.config/opencode/opencode.json` de **todos** os homes (ou rode
   `scripts/war/promote-creativity.sh --apply`, que grava backup
   `.bak-temps-<stamp>` e atualiza o rótulo `name`).
5. `~/.config/opencode/AGENTS.md` (seção `TEMPERATURA`).
6. `AgentSeeder.cs` → seeds + `PromoteRoleTemperatures` + `MigrateLegacyConfig`.
7. `AgentsController.DefaultAgent()` e o fallback de `ToolExecutor`.
8. Docs (§2, itens 14) e uma entrada nova no `HISTORY.md`.
9. `dotnet build -c Release` (solução inteira) + units.
10. `scripts/deploy-pull.sh` (rebuild no PROD) e restart de `bridge-web` para a
    migração do SQLite rodar.
11. Verificar (§7) e só então rodar o WAR.

## 6. Política de variância (gate × medição)

Regra do lab: **o gate não repete amostra para "ficar verde"**.

- **Gate (default)**: `WAR_ENT_SAMPLES` ausente = **1 amostra**. Um veredito.
  Se falhou, falhou — o log traz `[fail-dump]` com os primeiros 800 chars do
  artefato e `[contract]` com o motivo.
- **Medição de estabilidade (opt-in)**: `WAR_ENT_SAMPLES=N` +
  `WAR_ENT_MIN_PASS=k` roda **N amostras fixas**, exige `k` e loga
  `[stability] ... taxa=k/N`. N **não** cresce até passar; abaixo do mínimo o
  step reprova (texto vazio força o assert), sem "quase passou".
- Asserts da linha E-ent são **por contrato** (`409` **ou** `Conflict` **ou**
  `UNIQUE`; `OFFSET` **ou** `LIMIT` **ou** `Skip`/`pageSize`; `IDistributedCache`
  **ou** `ConnectionMultiplexer`), nunca por literal único.
- Enunciado fixa o dialeto (`Postgres`) e proíbe tool call (`SEM TOOL CALLS`)
  quando o step é conceitual.

Exemplo de medição (3 amostras, exige 2):

```bash
sudo -n rm -f /usr/src/opencodebridge/logs/.rerun-guard.json
sudo -n env WAR_ENT_SAMPLES=3 WAR_ENT_MIN_PASS=2 \
  dotnet exec Rochas.OpenCodeBridge.Test/bin/Release/net10.0/Rochas.OpenCodeBridge.Test.dll \
  --api-key guest@123 --bridge http://127.0.0.1:4124 --only E-ent --mode bridge
```

Se a taxa cair entre releases, isso é **regressão de estabilidade** e deve
aparecer no relatório — não ser absorvida por retry.

## 7. Verificação (comandos exatos)

```bash
# 1) a tabela do Core compilada
grep -n '"plan"\|"build"\|"orch"\|"moe"\|"scripter"\|"exec"' \
  Rochas.OpenCodeBridge.Core/RoleProfiles.cs | head -8

# 2) o runtime que está no ar (procedência + valor efetivo)
curl -s -X POST http://127.0.0.1:4124/api/translate \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-GK-Q4-plan","input":"ping"}' \
  | jq '{requested_model,temperature,temperature_source,thinking}'
# esperado: temperature=0.2, temperature_source="profile"

# 3) id servido (a Web UI fala direto com ele)
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/v1/models | jq -r '.data[].id'
# esperado: Gemma4-E4B-GK-Q4

# 4) seeder/migração da Web UI (após restart de bridge-web)
sqlite3 /usr/src/opencodebridge/Rochas.OpenCodeBridge.Web/opencodebridge.db \
  "SELECT id,name,model,temperature FROM agents ORDER BY id;"
# esperado: MoE 0.4; Plan/Orch 0.2; Build/Scripter/Exec 0.1; GPU com model GK-Q4

# 5) nenhuma referência velha sobrou nos configs versionados
grep -rn '"temperature"' Rochas.OpenCodeBridge/appsettings*.json
grep -rn 'Gemma4-E4B-Q4' Rochas.OpenCodeBridge.Web/Data/AgentSeeder.cs
```

## 8. Armadilhas conhecidas

- **Perfil vence o cliente** (default): mudar só o `opencode.json` não muda o
  role; mudar só o `appsettings.json` não muda o que o cliente registra.
- **`appsettings.json` do PROD** é cópia do repo no deploy; editar
  `/usr/src/opencodebridge/.../appsettings.json` sem commit volta atrás no
  próximo `deploy-pull.sh`.
- **Rebuild antes de remover override** (ver `_comment` do `appsettings.json`):
  binário antigo usa o default antigo.
- **Dois bancos**: Web UI usa SQLite (`agents`) e migra a cada boot; um agente
  editado na mão na UI pode ser renormalizado por `PromoteRoleTemperatures`.
- **GPU2 aposentada**: agentes `(GPU2)` no seeder continuam existindo, mas a
  unit `llama-gpu2` está `disabled`. Religar exige
  `systemctl enable --now llama-gpu2 bridge-gpu2`.
- **Bench mede, não define**: `scripts/war/bench.sh` e `param_matrix.py` passam
  temperatura explícita de propósito (matriz de medição). Não os use como
  referência da escala do lab.

## 9. Histórico das escalas

| data | plan | orch | moe | build | scripter | exec | motivo |
|---|---|---|---|---|---|---|---|
| 2026-10-29 | 0.3 | 0.3 | 0.5 | 0.1 | 0.1 | 0.1 | restauração após `50138b8` (0.4/0.7/0.2 sem medição) |
| 2026-11-07 | **0.2** | **0.2** | **0.4** | 0.1 | 0.1 | 0.1 | variância medida na linha E-ent (texto puro); retry removido do gate |

Escalas intermediárias (0.5/0.8/0.2 do `promote-creativity.sh` e 0.4/0.7/0.2 do
seeder antigo) eram alvos de "criatividade" nunca validados com a linha E-ent
estável — foram descartadas.
