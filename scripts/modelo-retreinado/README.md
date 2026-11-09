# Modelo retreinado na GPU (:4124) — `Gemma4-E4B-GK-Q4`

Pasta de trabalho da troca do modelo servido na GPU1 pelo retrain de 09/11/2026 e da
normalização do nome em todas as superfícies. **Nome final: `Gemma4-E4B-GK-Q4`** — sem `UCD`,
sem `ABT`. Tudo aqui é script + documentação: nada roda sozinho.

## Por que

O E4B entregue em 09/11/2026 (QLoRA sobre o QAT de julho, **sem** abliteração — ver
`docs/ABLITERATION.md` §12-13 no projeto FT) tem GGUF próprio:

| Artefato de origem | Bytes | sha256 |
|---|---|---|
| `Gemma4-E4B-IT_Principal-GK-DS-Retrained-Q4_K_M-Imatrix.gguf` | 5.302.268.992 | `dbbf6c89…` |
| `Gemma4-E4B-IT_Principal-GK-DS-Retrained-mmproj-bf16.gguf` | 991.552.288 | — |
| `Gemma4-E4B-IT_Principal-GK-DS-Retrained-Q6_K.gguf` (reserva) | 6.172.074.784 | `e83dd8e5…` |

Na instalação ele é copiado como `Gemma4-E4B-GK-Q4-Q4_K_M-Imatrix.gguf` +
`Gemma4-E4B-GK-Q4-mmproj-bf16.gguf`; o sha256 é conferido contra o manifesto antes da cópia e
gravado em `/opt/models/gemma4-e4b-gk-q4/SHA256SUMS`.

### Três gerações do id convivem no repo/deploy

| Id | Onde aparecia |
|---|---|
| `Gemma4-E4B-Q4` | unit `llama-gpu` em produção, `opencode.json`, `agents/*.md`, `AGENTS.md` |
| `Gemma4-E4B-GK-Q4` | repo editável, já apontando para o retrain (08/11) |
| `Gemma4-E4B-UCD-GK-Q4` | repo no estado atual (working tree não commitado) |

São a **mesma** linha de modelo em épocas diferentes. O `migrar.sh` normaliza as três num
**passe único** (sem cascata) para `Gemma4-E4B-GK-Q4`.

### Superfícies (trocar uma só quebra o resto)

A bridge reescreve `node.model` para o id que ela serve (`Program.cs:2898`), então um id
divergente no cliente vira 404 no llama.

| # | Superfície | Arquivo |
|---|---|---|
| 1 | launcher do llama (arquivo, `--alias`, `Description`) | `/usr/local/bin/start-llama-gpu.sh` |
| 2 | launcher da bridge (`SVC_MODEL`, defaults, `Description`) | `/usr/local/bin/start-bridge-gpu.sh` |
| 3 | agregador de boot (texto/log) | `/usr/local/bin/start-models-default.sh` |
| 4 | config do opencode (provider `openai`) | `~/.config/opencode/opencode.json` |
| 5 | papéis do opencode (`model: openai/…`) + `AGENTS.md` | `~/.config/opencode/agents/*.md` |
| 6 | seed dos agentes (Web UI) + código com id fixo | `AgentSeeder.cs`, `Gate.cs`, `BridgeOptions.cs`, `appsettings.4124.gpu-example.json`, `Swagger.cs`, `Program.cs`, `AgentsController.cs`, `TestContext.cs` |

## Como

```bash
scripts/modelo-retreinado/migrar.sh --check      # plano, não escreve
scripts/modelo-retreinado/migrar.sh --apply      # artefatos + launchers + config + código
scripts/modelo-retreinado/verificar.sh           # checa as superfícies (após o restart)
scripts/modelo-retreinado/migrar.sh --rollback <ts>   # volta do backup
```

O `--apply` **copia o GGUF e o mmproj para `/opt/models/gemma4-e4b-gk-q4/`**. Motivo: a origem
(`/media/mint/LAB-FT`) **não está no fstab** (montagem manual), e um `Type=simple` com
`After=network-online.target` subiria antes do disco.

Backups: `scripts/modelo-retreinado/backups/<ts>/` (cópia integral de cada arquivo tocado).

## Ordem de aplicação (o restart derruba quem está rodando na bridge)

```bash
# 1) repo editável (aqui), depois de revisar o diff
git add -A
git commit -m "feat(model): serve Gemma4-E4B-GK-Q4 (retrain) na GPU :4124 e normaliza o id"

# 2) produtivo — SÓ pull, deploy e restart
cd /usr/src/opencodebridge && sudo git pull
cd /usr/src/opencodebridge && dotnet build -c Release Rochas.OpenCodeBridge.slnx

# 3) runtime
sudo systemctl restart llama-gpu      # carrega o GGUF retreinado com o alias novo (~2-4 min)
sudo systemctl restart bridge-gpu     # passa a anunciar o alias novo
sudo systemctl restart <serviço-web>  # o seed migra as linhas :4124 no boot

# 4) prova
scripts/modelo-retreinado/verificar.sh
curl -s http://127.0.0.1:4124/api/status | python3 -m json.tool | head
curl -s http://127.0.0.1:4100/v1/models | python3 -m json.tool
```

## O que **não** muda (de propósito)

- **provider `cpu` (`:4126`/`:4120`)** e **`llama` (`:4125`/`:4110`)**: outros units e outros
  GGUFs; mantêm `Gemma4-E4B-Q4` e `Gemma4-E4B-UD-Q3`.
- `start-llama-cpu-forced.sh`, `start-bridge-cpu-forced.sh` e `start-models-default.sh`
  (fora o texto/log da linha GPU) — a bancada CPU é outro par de modelos.
- `opencode.v1.json` / `opencode.v2.json`: arquivos históricos, não são lidos.
- **Papéis por sufixo** continuam iguais: `RoleProfiles.TryResolve` casa pelo final do id
  (`-plan`, `-build`, `-orch`, `-moe`, `-scripter`), nunca pelo prefixo.
- **Testes e docs históricos** citam ids antigos de propósito (registro do que existia).

## Cuidados

- Reiniciar `llama-gpu`/`bridge-gpu` **derruba a sessão** que estiver rodando neles: faça a
  troca com o agente parado.
- `bridge.db` (repo e deploy) hoje **não tem tabelas** — o schema nasce no boot da Web; por
  isso a migração de `agents.model` é feita no `MigrateGpuModelId` e é idempotente.
- Piso de tensores do `verify-deliverables.sh` (projeto FT) foi corrigido para 640/560: 666 é
  o número correto do e4b (18 camadas com KV compartilhado).
