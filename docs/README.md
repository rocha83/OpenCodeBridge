# Índice da documentação

A raiz do repo guarda só `README.md` (visão geral), `AGENTS.md` (regras do
operador), `HISTORY.md` (histórico/medições) e a solução. Tudo o mais é doc
aqui ou automação em `../scripts/`.

## Infra e operação

| doc | conteúdo |
|---|---|
| `LAB-SETUP.md` | units llama, TurboQuant, VRAM, contextos, cenários A–E, referência de todos os argumentos da bridge e `appsettings.json` |
| `04-rede-e-exposicao.md` | portas, ufw, Caddy/cloudflared e exposição |
| `09-dialogos-zenity.md` | diálogos dos ícones (zenity house style) |
| `10-icones-scripts-units.md` | inventário operacional: ícone → script → unit, binds, harness por lane e armadilhas |
| `11-FIX-ICONES-UNITS.md` | **runbook de aplicação no chroot/live**: instalador único com scripts, units, env files e ícones exatos |
| `PENDENCIAS-CHROOT-CUBIC.md` | checklist do remaster Cubic (chroot) |

## Testes e baterias

| doc | conteúdo |
|---|---|
| `BENCHTESTS.md` | catálogo de testes: unit, E2E, belts, frontpipe, bateria dual, **WAR v2** (lanes Q4/Q3), Levels 1–3 e **auto-gestão** (supervisor orch, §11) |
| `RUNBOOK-E2E.md` | pipeline por fases (`scripts/run-e2e-pipeline.sh`) e estado do ambiente |
| `RELATORIO-SPRINT-2026-10-19.md` | relatório do sprint de E2E/standalone |
| `harness/opencode/` | captura e análise do harness do opencode: `README.md` (captura), `NATIVE.md`, `CUSTOM.md`, `SUMMARY.md`, `MAIN-HARNESS.md` (texto e switches atuais), `system.md` |

## Pendências e decisões

| doc | conteúdo |
|---|---|
| `PENDINGS.md` | fila viva, incluindo a **avaliação de parâmetros do llama × anti-repeat** |
| `PENDENCIAS.md` | pendências antigas (histórico) |
| `Pendings-Req-Approval.md` | pendências que exigem aprovação |
| `AGENTS-PROMPTS.md` | system prompts versionados (fonte dos agentes do opencode) |

## Convenções

- Caminho de script sempre relativo à raiz: `./scripts/<nome>.sh`.
- Baterias WAR/levels: `./scripts/war/<nome>.sh` (artefatos em `/tmp/opencode/...`).
- Doc novo entra neste índice e, se for operacional, aponta o script correspondente.
