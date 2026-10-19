# RUNBOOK — Pipeline E2E sequencial (por fase)

Script: `run-e2e-pipeline.sh` (raiz do repo OpenCodeBridge).

## Uso

```bash
./run-e2e-pipeline.sh --phase 0   # health llama + bridge + VRAM
./run-e2e-pipeline.sh --phase 1   # unit tests (skip e2e)
./run-e2e-pipeline.sh --phase 2   # bridge pure E2E (science + mission)
./run-e2e-pipeline.sh --phase 3   # standalone bridge calls (link opencode)
./run-e2e-pipeline.sh --phase all # tudo, em ordem
```

Log: `/tmp/opencode/pipeline.log` (tee).

## Fases

| Fase | O que roda | Aceite |
|---|---|---|
| 0 | `/health` dos dois llama, `/api/status` das duas bridges, `nvidia-smi` | llama `ok`, bridges `status:ok`, VRAM estável ~11.1–11.3 GB |
| 1 | `Rochas.OpenCodeBridge.Test --skip-e2e` | `PASS=23 FAIL=0 SKIP=3` |
| 2 | `--only E-science` + `--only E-mission-tools` | science PASS; missão C#/F# imprime `2.000000`/`5.000000` |
| 3 | `curl /v1/responses` (oi, tópico geral) | resposta com conteúdo, sem `Decode error` |

## Estado do ambiente (2026-10-18/19)

- `llama-gpu` :4100 — Gemma4 12B Q4_K_M, `-c 131072 -np 1`, turbo3, `--prio 2`, mmproj F16.
- `llama-cpu` :4110 — Gemma4 E2B Q3_K_M, `-c 131072 -np 2` (2×65536), turbo3, `--prio -1`, mmproj Q8_0.
- `bridge-gpu` :4124 — `--model gemma-4-12b --max-tokens 16384 --passthrough-responses true --legacy-harness 0 --anti-repeat 1 --thinking-off-roles Build,Scripter,Exec`.
- `bridge-cpu` :4125 — `--model gemma-4-e2b --max-tokens 8192 --diagnostic 1`.
- `bridge-web` :4130.
- VRAM medida: ~11248 MiB usados / 789 MiB livres (12 GB).
- `journalctl` sem `auto-asymmetric`; `n_ctx_slot`: 12B 131072, E2B 65536.

## Regras de deploy

- **Dev**: repo `/media/mint/3686C649614854E61/Projetos/Git/OpenCodeBridge`.
- **Produção (chroot/live)**: `/usr/src/opencodebridge` — **apenas** `git fetch + merge + build + restart`. Nunca editar código lá.
- Subir sempre **12B antes do E2B** (senão o auto-fit do E2B falha com `-ngl 99`).
- Remaster: `units/`, `opencode.json`, `agents/`, `web-ui/` em `/media/mint/Linux/remaster-fix/`.
