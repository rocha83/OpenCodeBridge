# Diferencial das 3 sessões — E2E faixas até a preta (2026-10-09/10)

Documento de continuidade: o que cada sessão decidiu e implementou, sem repetir o `HISTORY.md`.

## S1 — `session-6ffea29a` (1725 msgs, "Verificação de placa Nvidia presente")
- 6 faixas ao vivo pela UI (43 tasks: verde 6, roxa 6, marrom 10, preta 12, azul 3, ferramentas 6); suite 77/77 → 80/80.
- Gargalo VRAM medido: 8B 9,3 GB + 3×3B AWQ com offload = 0,05–4 t/s vs 35 t/s do 8B sozinho.
- Decisão: **swap com força total** (nada de coabitação) + `MaxTaskRetries: 2`.
- Achado: preta com `executou:0 + 9 refinos + 9 splits` — 3B em prosa sem tools; refino sem split antecipado repete o mesmo espaço de falha.

## S2 — `sauda-o-de-boa-noite` (157 msgs, plan)
- Porquê dos 9 refinos: mesmo system prompt + JsonArray amplo + aceite não verificável por máquina.
- Requisitos: validador pré-execução que barra tool fora do escopo e grava `refinements` como alucinação;
  `refinements` alimenta o system prompt do 3B (assinatura repetida 2x sobe para o prompt, não só o enunciado).
- Ctx: plan 8k, build 12k (JsonArray enxuto); 2×3B-AWQ 16k em swap sem 8B residente (>30 t/s); 8B 36k sozinho.
- Sidebar "Tarefas do Executor" (não "Tarefas 3B") com comandos executados + resumo do enunciado.

## S3 — sessão atual (infra + E2E codificado)
- `vllm.service` com `ExecStartPost` multilinha inválido (systemd recusava o unit) → extraído para
  `/opt/vllm/healthcheck.sh [porta] [timeout]`; depois **removido do unit** (mascarava falha: job preso
  em `activating` 9 min com o main morto). Prontidão = `/health`, não `systemctl`.
- Units novas: `vllm-3b-1` (`:4101`) + `vllm-3b-2` (`:4102`), AWQ ctx 16k, model `qwen25-coder-3b-gpu`,
  `Conflicts=vllm.service`; scripts `/opt/vllm/swap-to-3b.sh` / `swap-to-8b.sh`.
- Teste E2E em C# (`Web.Test/Ui/BeltUiTests.cs`, Playwright + Firefox + UA Win11, flags
  `--ui --mode plan|build --belts --phase decompose|execute|review|full`):
  login real → cria sessão → `Decompose` (8B) → aprova `needsTools=true` → `OrchestrateApproved`
  → poll `/Tasks` → **revisão em lote do 8B** → re-executa rejeitadas → `Synthesize`.
- Faixa preta: ML regressão + classificação + MLP anomalias, ~14 subtarefas (plan PASS com 14).
- Plan total PASS: azul 12, verde 6, roxa 6, marrom 10, preta 14.
- Fix `SessionService`: `CreatedAt/UpdatedAt` nunca preenchidos (UI mostrava 00:00) → preenchidos em
  `CreateAsync`/`AddMessageAsync` (no model, para não envenenar filtros Dapper); backfill de NULLs.
- Experimento CUDA graphs (revertido): sem `--enforce-eager` + ctx 32k → OOM KV (4,5 GB > 4,2 GB);
  ctx 28k caberia mas cortaria o orçamento de síntese da preta. **Voltado ao estável: 36k + eager,
  ~30 t/s média medida (25–37)**.
- Bugs do site isolados (fora de escopo): parênteses no título quebram o filtro Dapper (NRE);
  `agents.Query` com race transitória (contornada com retry no teste).
- Agente `Orquestrador Plan` (id 6, modo plan) criado no `web.db` para o plan E2E.
- Incidente: ~300 sessões antigas sumiram do `web.db` (sequence em 314, 1 linha; zero DELETEs logados,
  sem DROP no código) — causa não identificada.

## Pendente
- Build E2E (swap → 2×3B executam → swap de volta → review em lote → re-execução → síntese).
- `ReviewAsync` em lote no site (pós-ciclo); validador pré-execução; `refinements` → system prompt.
- Push com token (manual, sem persistir).
