# Índice de scripts

Todos derivam a raiz do repo da própria localização (`scripts/` → `..`) e aceitam
override por `REPO=...` (ou `WAR_ROOT`/`LEVEL_ROOT`), então funcionam em qualquer
clone (`/media/mint/78AA...`, `/media/mint/3686...`, `/usr/src/opencodebridge`).

## Baterias e pipelines

| script | o que faz |
|---|---|
| `run-e2e-pipeline.sh` | pipeline por fases (`--phase 0|1|2|3|all`) contra as bridges live |
| `run-battery-dual.sh` | bateria completa nos dois modelos (Q4 e Q3) em paralelo |
| `run-pipeline-encadeado.sh` | health → units → devpipe → science → mission → belts → frontpipe (sequencial, log em `test-logs/`) |
| `run-pipeline-paralelo.sh` | mesma coisa com GPU1 e GPU2 em paralelo |
| `run-opencode-standalone.sh` | mesmas tarefas via `opencode run` real (`--task oi|tools|write|devpipe|science|mission|all`) |

## Arena de guerra

| script | o que faz |
|---|---|
| `war/run-levels.sh` | arena por níveis (Level 1 = units + devpipe + belts azul/verde + Web.Test, cada bloco em `--mode pure` e `--mode opencode`), checkpoint por step |
| `war/run-war.sh` | WAR v2: 3 lanes simultâneas (Q4 `:4134`, Q3 `:4135`/`:4136`), plano aleatório reproduzível (`WAR_SEED`), retomada por checkpoint, evidência por step (log do teste + delta do log da bridge + `/api/metrics` + `llamacpp:*` + `nvidia-smi`) |
| `war/run-param-matrix.sh` | matriz de amostragem (temperatura × repeat-penalty × DRY × anti-repeat/tails da bridge) no cenário de visão/typo, com métrica de path exato e repetição |

## Serviço e manutenção

| script | o que faz |
|---|---|
| `run-bridge-stable.sh` | sobe a bridge com a cauda de comportamento e flags estáveis |
| `bridge-restart.sh` | reinicia a bridge alvo (`4124/4125/4126`), espera `/api/status` e (com `--print-flags`) confere orçamento/tails |
| `check-units.sh` | build Release + bateria de units (`--skip-e2e`), rc do runner |
| `run-isolated.sh` | reinicia a bridge e roda **um** teste isolado, log em `logs/isolated/` + trail do modelo |
| `push-persist.sh` | push do persistente com `GITHUB_TOKEN` fora do repo, conferido por `ls-remote` |
| `prod-repair-ownership.sh` | conserta PROD com `.git`/worktree de root (DRIVER-FIXES #21) |
| `health-check.sh` | smoke da Web UI (`/Account/Login`, `/Chat/Ping`) |
| `bench-tok-s.sh` | tok/s do llama direto (média/mediana) |
| `gate.sh` | gate de commit + build + suite web |
| `deploy-web.sh` | build da Web + suite + restart |

## Artefatos

**Tudo dentro do repositório** — nada de artefato de teste em `/tmp/opencode`:

| o que | onde |
|---|---|
| Bateria/WAR por nível | `logs/levels/L<N>-<stamp>/` (summary, events, probes) e `logs/levels/<stamp>.console.log` |
| Teste isolado (`run-isolated.sh`) | `logs/isolated/<nome>_<stamp>.log` |
| Sondas Serilog dos steps | `logs/levels/*/probes/*.probe.json` |
| Pipeline e standalone | `logs/pipeline.log`, `logs/standalone.log`, `logs/standalone/` |
| Guard de rebuild | `logs/.rerun-guard.json` |

O único `/tmp` legítimo é o workspace de execução do teste (`/tmp/e2e-*`, volátil
por desenho) e os locks (`/tmp/opencode/war.lock`). O registro consolidado de
cada rodada e correção vai para `HISTORY.md` e `docs/DRIVER-FIXES.md`.


<!-- INVARIANTE-TUNEL-LLAMA:v1 -->
## Invariante — túnel de stream do llama é exclusivo da bridge/core

**Somente a bridge (`Rochas.OpenCodeBridge`, camada Core) fala com o llama-server.** Testes (`Rochas.OpenCodeBridge.Test`), `mini-bridge-python`, Web UI, Grafana e scripts são clientes/observadores: não injetam, não reescrevem, não "cutucam" e não reordenam mensagens dentro do túnel.

- Cliente envia a **requisição** (`/v1/chat/completions`, `/v1/responses`); o stream do modelo volta repassado **sem edição** pelo core.
- Proibido no cliente: nudge/continuation prompt, injeção de "próximo passo", reescrita de turno, reordenação ou truncamento de histórico.
- Harness e coerção (`main_harness`, tails, `coerce_text_tools`, `coerce_narrated`, `degenerate_retry`) vivem **só** na bridge/core, atrás de flag.
- Comportamento de teste se ajusta por **enunciado** ou **flag da bridge** — nunca editando o conteúdo do túnel no cliente.
- Nudges de continuação do `TestContext.ToolAgent` foram **removidos em 2026-11-07** (`TCTX_NUDGES` não existe mais): turno sem tool call encerra o loop e o veredito sai do artefato.
- Fonte única: `docs/TUNEL-LLAMA.md`.

