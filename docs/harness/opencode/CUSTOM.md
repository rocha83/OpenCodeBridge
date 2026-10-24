> **Nota:** análise da captura histórica de 2026-10-22 (12B/E2B). Os hardcodes atuais já foram removidos/atualizados em `/root/.config/opencode/AGENTS.md` (E4B, 128k, temps 0.1/0.3/0.5).
>
> **Status (2026-10-24):** o comportamento consolidado virou o `MainHarness`
> hardcoded e as tails ficaram no `appsettings.json`; ver `MAIN-HARNESS.md`.
> Esta tabela permanece como registro da análise, não como config vigente.

# Parte CUSTOM (nossa) — veio do agente e do AGENTS.md

Injetada por nós no system prompt via config/agentes do opencode. É o que
realmente descreve "como trabalhar", mas contém **hardcodes** que não devem
viajar para um harness genérico.

| Bloco | Linhas | Chars | Origem |
|---|---|---|---|
| A. Papel do agente build + regras de tools | 1-10 | 1.236 | `~/.config/opencode/agents/build.md` |
| E. Instruções globais | 146-190 | 6.679 | `~/.config/opencode/AGENTS.md` |

**Total custom: ~7.915 chars (57%).**

## Subtópicos do AGENTS.md (E)
- E1. Regras de tool irrefutáveis (7)
- E2. Modelos locais (2 modelos / 6 roles)
- E3. Orquestração multi-agente
- E4. Regras do subagente E2B
- E5. Pipeline padrão
- E6. Temperatura por role
- E7. System prompt geral (generalista)
- E8. Escrita extrema + build de integridade

## Hardcodes a remover no resumo

| Linha | Hardcode | Ação |
|---|---|---|
| 9 | `contexto (...) >=64k tokens` | generalizar: "quando a tarefa/contexto for grande" |
| 12 | `Gemma4 12B — Build (Thinking Off, 0.3)` | remover (metadado nativo) |
| 146 | `/home/mint/.config/opencode/AGENTS.md` | remover (path local) |
| 154, 162, 166, 168, 171, 183 | `llama/gemma-4-e2b` | "o subagente configurado" |
| 160 | `:4124/:4125`, "2 modelos / 6 roles" | remover (infra local) |
| 161 | `openai/gemma-4-12b`, `:4124`, `:4100`, `160k/32k`, `0.5` | remover (infra/modelo) |
| 162 | `:4125`, `:4110`, `64k/16k`, `0.3` | remover (infra/modelo) |
| 165 | `GPU tem 160k mas a CPU tem 64k` | remover (capacidade local) |
| 166 | `64k do E2B, saída de 16k` | remover (capacidade local) |
| 178, 180 | temperaturas `0.5/0.8/0.3` | remover (config, não harness) |
| 12, 161-162 | `peg-gemma4/slot único` | remover (detalhe de infra) |

> Regra: harness descreve **comportamento**; valores de infraestrutura
> (modelo, porta, contexto, temperatura, caminho) ficam na config, nunca no texto.
