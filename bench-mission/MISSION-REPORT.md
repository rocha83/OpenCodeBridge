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
