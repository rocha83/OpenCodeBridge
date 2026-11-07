# MISSION-REPORT — programa científico C#+F#, git+zip (2026-10-16)
Enunciados em ENUNCIADOS.md (8 stages, alternância TEXTO/TOOLS).
Ferramentas idênticas p/ ambos (read/write/edit/glob/grep/shell sandbox).
IDs sufixados (tubo limpo). 1 slot cheio cada.

## 9B (qwen3.5-9b-build) — 8/8 stages, 46 tool calls (35 ok), t/s médio 28.6
- C#: CORRETO (Simpson 2.0, média 5.0, desvio 2.138, y=0.8x+1.8).
- F#: FALHOU (24 turns S4+S5 brigando com erros, sem binário).
- git: init ok, **sem commit** (parou no t1). zip: criado (123KB) mas só
  com scics (+bin/obj dentro — sem exclusão).
- Veredito: executa tools de verdade, acerta C#, tropeça em F# e desiste
  de finalizar (git/zip parciais).

## 4B-VL (qwen3-vl-4b-build) — 8 stages "passados", 0 tool executada
- S1 vazio; S2–S6: `{"json": "error": "No such tool available"}` no lugar
  de calls; S7 fabrica números (Simpson=2.95778, errado).
- Veredito: VL **não consome array de tools** — responder TEXTO ok,
  chamar tools não. Parâmetro tools é global na bridge (igual p/ ambos);
  a diferença é o modelo.


<!-- INVARIANTE-TUNEL-LLAMA:v1 -->
## Invariante — túnel de stream do llama é exclusivo da bridge/core

**Somente a bridge (`Rochas.OpenCodeBridge`, camada Core) fala com o llama-server.** Testes (`Rochas.OpenCodeBridge.Test`), `mini-bridge-python`, Web UI, Grafana e scripts são clientes/observadores: não injetam, não reescrevem, não "cutucam" e não reordenam mensagens dentro do túnel.

- Cliente envia a **requisição** (`/v1/chat/completions`, `/v1/responses`); o stream do modelo volta repassado **sem edição** pelo core.
- Proibido no cliente: nudge/continuation prompt, injeção de "próximo passo", reescrita de turno, reordenação ou truncamento de histórico.
- Harness e coerção (`main_harness`, tails, `coerce_text_tools`, `coerce_narrated`, `degenerate_retry`) vivem **só** na bridge/core, atrás de flag.
- Comportamento de teste se ajusta por **enunciado** ou **flag da bridge** — nunca editando o conteúdo do túnel no cliente.
- Nudges de continuação do `TestContext.ToolAgent` foram **removidos em 2026-11-07** (`TCTX_NUDGES` não existe mais): turno sem tool call encerra o loop e o veredito sai do artefato.
- Fonte única: `docs/TUNEL-LLAMA.md`.

