# RUNBOOK — Pipeline E2E sequencial (por fase)

Script: `scripts/run-e2e-pipeline.sh` (na raiz do repo).

## Uso

```bash
./scripts/run-e2e-pipeline.sh --phase 0   # health llama + bridge + VRAM
./scripts/run-e2e-pipeline.sh --phase 1   # unit tests (skip e2e)
./scripts/run-e2e-pipeline.sh --phase 2   # bridge pure E2E (science + mission)
./scripts/run-e2e-pipeline.sh --phase 3   # standalone bridge calls (link opencode)
./scripts/run-e2e-pipeline.sh --phase all # tudo, em ordem
```

Log: `/tmp/opencode/pipeline.log` (tee).

## Fases

| Fase | O que roda | Aceite |
|---|---|---|
| 0 | `/health` dos dois llama, `/api/status` das duas bridges, `nvidia-smi` | llama `ok`, bridges `status:ok`, VRAM estável ~11.1–11.3 GB |
| 1 | `Rochas.OpenCodeBridge.Test --skip-e2e` | `PASS=23 FAIL=0 SKIP=3` |
| 2 | `--only E-science` + `--only E-mission-tools` | science PASS; missão C#/F# imprime `2.000000`/`5.000000` |
| 3 | `curl /v1/responses` (oi, tópico geral) | resposta com conteúdo, sem `Decode error` |
| 3b | Suíte com `--standalone` (opencode real, mesmos enunciados) | `E-standalone-*` PASS |

## Modo standalone (opencode real)

A suíte desvia cada E2E para o **opencode CLI** rodando o **mesmo enunciado**
(`opencode run`), em vez de HTTP direto na bridge:

```bash
# toda a trilha standalone (6 testes, agent build / 12B GPU)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- \
  --standalone --only E-standalone --api-key guest@123 --bridge http://127.0.0.1:4124

# um teste só
--standalone --only E-standalone-oi

# E2B (CPU) via agent exec — model derivado -> llama/gemma-4-e2b-exec
--standalone --standalone-agent exec --only E-standalone --bridge http://127.0.0.1:4125
# override explícito do modelo (provider/model)
--standalone --standalone-model openai/gemma-4-12b-build
```

- `--standalone` liga o conjunto `E-standalone-{oi,tools,write,devpipe,science,mission-tools}`.
- `--standalone-agent` (default `build`) escolhe o agent do opencode; `exec` usa `llama/` (E2B).
- `--standalone-model` força `provider/model`; sem ele deriva do agent (`openai/` GPU, `llama/` CPU).
- `--standalone-timeout` (default 300s) limita cada chamada `opencode run`.
- O harness chama `opencode run --standalone` (servidor **privado** por execução) para
  não depender do background service compartilhado — evita o erro
  `Timed out waiting for the background service to start` sob carga.
- Enunciados são idênticos aos da trilha HTTP — só muda o caminho (opencode real vs bridge pura).

## Estado do ambiente (2026-10-18/19)

- `llama-gpu` :4100 — Gemma4 12B Q4_K_M, `-c 131072 -np 1`, turbo3, `--prio 2`, mmproj F16.
- `llama-cpu` :4110 — Gemma4 E2B Q3_K_M, `-c 131072 -np 2` (2×65536), turbo3, `--prio -1`, mmproj Q8_0.
- `bridge-gpu` :4124 — `--model gemma-4-12b --max-tokens 16384 --passthrough-responses true --legacy-harness 0 --anti-repeat 1 --thinking-off-roles Build,Scripter,Exec`.
- `bridge-cpu` :4125 — `--model gemma-4-e2b --max-tokens 8192 --diagnostic 1`.
- `bridge-web` :4130.
- VRAM medida: ~11248 MiB usados / 789 MiB livres (12 GB).
- `journalctl` sem `auto-asymmetric`; `n_ctx_slot`: 12B 131072, E2B 65536.

## Métricas (tok/s) — bench limpo 2026-10-19 (GPU/CPU ociosos)

`./scripts/bench-tok-s.sh 3 256` (prompt de contagem, `n_predict=256`, `ignore_eos=true`):

| modelo | onde | geração (média / mediana) | prompt eval |
|---|---|---|---|
| **gemma-4-12b** (Q4_K_M) | GPU :4100 | **24.32 / 24.32 tok/s** (24.29–24.36) | ~59–65 tok/s |
| **gemma-4-e2b** (Q3_K_M) | CPU :4110 | **56.16 / 56.03 tok/s** (56.01–56.43) | ~216–257 tok/s |

Do log do llama (`journalctl -u llama-gpu`, gerações ≥100 tokens): 12B **mediana 24.95 tok/s** (máx 28.65), consistente com o bench.
Obs.: medições E2B mais baixas (21–28 tok/s) vistas antes eram contenção de CPU, não o regime limpo.

## Regras de deploy

- **Dev**: repo `/media/mint/3686C649614854E61/Projetos/Git/OpenCodeBridge`.
- **Produção (chroot/live)**: `/usr/src/opencodebridge` — **apenas** `git fetch + merge + build + restart`. Nunca editar código lá.
- Suíte em produção: **pare as bridges antes do build** (PDB lock) e rode a fase com
  `--no-build`, com `REPO` apontando para produção:
  ```bash
  sudo dotnet build Rochas.OpenCodeBridge.Test/Rochas.OpenCodeBridge.Test.csproj -c Release   # bridges paradas
  sudo systemctl start bridge-gpu bridge-cpu bridge-web
  sudo REPO=/usr/src/opencodebridge bash scripts/run-e2e-pipeline.sh --phase all --no-build
  ```
- O `harnessToolsTail` é **fonte única no código** (`Program.cs`); não definir nos appsettings.
- Sob `sudo`, o `HOME` vira `/root` e o `opencode` perde o config de modelos/agents
  (erro `Model unavailable`). O pipeline exporta `XDG_CONFIG_HOME` do usuário
  invocador (`SUDO_USER`) — necessário para a fase 3 standalone.
- Subir sempre **12B antes do E2B** (senão o auto-fit do E2B falha com `-ngl 99`).
- Remaster: `units/`, `opencode.json`, `agents/`, `web-ui/` em `/media/mint/Linux/remaster-fix/`.
