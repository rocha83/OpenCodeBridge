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

Baterias escrevem em `/tmp/opencode/` (`war/`, `levels/`, `battery/`). tmpfs é
volátil: o que precisa sobreviver (script, doc, resultado) vai para o repo —
`test-logs/` para logs e `HISTORY.md` para o registro.
