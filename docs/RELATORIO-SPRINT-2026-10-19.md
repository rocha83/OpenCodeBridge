# RELATÓRIO — Sprint bridge estável com 2 modelos (2026-10-19)

## 1. Objetivo

Entregar a **OpenCodeBridge estável** com os **2 modelos locais** (`gemma-4-12b` na GPU,
`gemma-4-e2b` na CPU) **funcionando com tools sem alucinar**, e uma **trilha E2E
standalone** (opencode real) reutilizável como argumento da suíte.

## 2. Stack no ar

| serviço | porta | modelo | config |
|---|---|---|---|
| `llama-gpu` | 4100 | gemma-4-12b Q4_K_M | `-c 131072 -np 1`, turbo3, `--prio 2`, mmproj F16 |
| `llama-cpu` | 4110 | gemma-4-e2b Q3_K_M | `-c 131072 -np 2` (2×65536), turbo3, `--prio -1`, mmproj Q8_0 |
| `bridge-gpu` | 4124 | gemma-4-12b | harness on, `legacy_harness` off, `max_tokens` 16384 |
| `bridge-cpu` | 4125 | gemma-4-e2b | harness on, `max_tokens` 8192, `cpu_max_tokens` 0 |
| `bridge-web` | 4130 | — | UI |

VRAM: ~11.25 GiB / 12 GiB (`.used` 11.5 GiB). Sem `auto-asymmetric`.

## 3. Métricas de velocidade (tok/s)

Bench controlado `./scripts/bench-tok-s.sh 3 256` (prompt de contagem, `n_predict=256`,
`ignore_eos=true`, GPU/CPU ociosos):

| modelo | onde | geração (média / mediana) | faixa (min–max) | prompt eval |
|---|---|---|---|---|
| **gemma-4-12b** (Q4_K_M) | GPU :4100 | **24.32 / 24.32 tok/s** | 24.29 – 24.36 | ~59–65 tok/s |
| **gemma-4-e2b** (Q3_K_M) | CPU :4110 | **56.16 / 56.03 tok/s** | 56.01 – 56.43 | ~216–257 tok/s |

Do log do llama (`journalctl -u llama-gpu`, gerações ≥100 tokens, n=102):
12B **mediana 24.95 tok/s** (máx 28.65) — confirma o bench.
As medições E2B de 21–28 tok/s vistas antes eram **contenção de CPU**; no regime
limpo o E2B faz ~56 tok/s (é um 2B Q3).

## 4. Testes (aceite)

| fase | o que | resultado |
|---|---|---|
| 0 | health llama :4100/:4110 + bridge :4124/:4125 + VRAM | `ok` |
| 1 | unit (skip e2e) | `PASS=26 FAIL=0 SKIP=1` (skip: `U-cpu-max-tokens-clamp`, `cpu_max_tokens=0`) |
| 2 | science (`E-science-gpu`, `E-science-cpu`) | `PASS` (gpu ~200s, cpu ~64s) |
| 2 | missão tools (`E-mission-tools`: C# + F# + git + zip) | `PASS` (~94s; 86–380s conforme carga) |
| 3 | standalone opencode build (GPU) `E-standalone-*` | `6/6 PASS` |
| 3b | standalone opencode exec (E2B CPU) | `PASS` |
| E2B | tools no E2B (agent exec) | `PASS` (13.2s) |

**Resultado consolidado (bateria `--phase all --no-build`, 2026-10-19):** fases 0–3
**todas verdes** — `26+2+1` testes de bridge (fases 1–2), `6` standalone GPU,
`1` standalone E2B, sem `FAIL`.

## 5. Entregas de engenharia

- **Suíte sempre no role build** (thinking off): `SuiteModel` + flags `--role`/`--model`.
- **Trilha standalone** (`--standalone`): desvia cada E2E para `opencode run` com o
  **mesmo enunciado**. `--standalone-agent`/`--standalone-model`/`--standalone-timeout`.
- **Tail unificado no código** (`HarnessToolsTail`): guard anti-alucinação + formato
  estruturado + F#/C#/math/git/zip + formatação numérica. `appsettings` não sobrescreve.
- **Pipeline** `run-e2e-pipeline.sh`: fases 0–3, build único, `--no-build`, `REPO` overridável.
- **Bench** `bench-tok-s.sh`: tok/s média/mediana por modelo.
- Doc: `RUNBOOK-E2E.md`, `HISTORY.md`, `PENDINGS.md`, `ajustes-max-tokens-turboquant-roles.md`.

## 6. Achados / correções

1. **`appsettings.harnessToolsTail` sobrescrevia o default do código** → divergência
   (C/F# imprimiam `%.6f` literal; F# quebrava em `open System.Math`). Corrigido:
   código é fonte única.
2. **`set -e` abortava o pipeline** no primeiro FAIL. Removido.
3. **F# é o ponto frágil do 12B** em multi-turno (erros de inferência: FS0039/FS0193/
   FS0001). Resolvido tornando o enunciado F# **prescritivo** (acumulador mutável +
   `for`, sem `Seq`/`List.fold`): `E-mission-tools` PASS.
4. **`opencode run` sem `--standalone`** depende do background service compartilhado
   e, sob carga, falha com `Timed out waiting for the background service to start`.
   Fix: harness usa `opencode run --standalone` (servidor privado por execução).
5. **Fase 3 sob `sudo`**: `HOME=/root` sem `opencode.json` → `Model unavailable`.
   Fix: pipeline exporta `XDG_CONFIG_HOME` do usuário invocador (`SUDO_USER`).

## 7. Como reproduzir

```bash
# bench
./scripts/bench-tok-s.sh 3 256
# fases
./scripts/run-e2e-pipeline.sh --phase 1
./scripts/run-e2e-pipeline.sh --phase 2
./scripts/run-e2e-pipeline.sh --phase 3
# standalone avulso
./scripts/run-opencode-standalone.sh --task all
```

## 8. Pendências honestas

- Cobertura ≥80%: sem coverlet/`dotnet tool` offline (bloqueio de rede).
- `PENDENTE 1`: apresentar a tool `question` no caminho bypass da Web.
