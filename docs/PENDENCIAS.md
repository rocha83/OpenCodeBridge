# PENDÊNCIAS — OpenCodeBridge (atualizado 2026-10-13)

## Testes (com mock, sem E2E)
- Unit/integração com `MockBridgeClient` cobrindo: loop com tools nativas no
  bypass + vínculo tool→bolha (`message_id`/`sub`). Hoje só o E2E cobre.

## Review — nomes intuitivos/legíveis (2026-10-11)
- Renomear classes, métodos e variáveis crípticas p/ nomes de domínio claros
  em toda a solução (ex.: `thinkPct`, `subChunk`, `ensureSubCard`,
  `toolStatus`, siglas e abreviações). Vale p/ C# e JS. Fazer em passada
  dedicada, com build + suites verdes por arquivo.


<!-- INVARIANTE-TUNEL-LLAMA:v1 -->
## Invariante — túnel de stream do llama é exclusivo da bridge/core

**Somente a bridge (`Rochas.OpenCodeBridge`, camada Core) fala com o llama-server.** Testes (`Rochas.OpenCodeBridge.Test`), `mini-bridge-python`, Web UI, Grafana e scripts são clientes/observadores: não injetam, não reescrevem, não "cutucam" e não reordenam mensagens dentro do túnel.

- Cliente envia a **requisição** (`/v1/chat/completions`, `/v1/responses`); o stream do modelo volta repassado **sem edição** pelo core.
- Proibido no cliente: nudge/continuation prompt, injeção de "próximo passo", reescrita de turno, reordenação ou truncamento de histórico.
- Harness e coerção (`main_harness`, tails, `coerce_text_tools`, `coerce_narrated`, `degenerate_retry`) vivem **só** na bridge/core, atrás de flag.
- Comportamento de teste se ajusta por **enunciado** ou **flag da bridge** — nunca editando o conteúdo do túnel no cliente.
- Nudges de continuação do `TestContext.ToolAgent` foram **removidos em 2026-11-07** (`TCTX_NUDGES` não existe mais): turno sem tool call encerra o loop e o veredito sai do artefato.
- Fonte única: `docs/TUNEL-LLAMA.md`.

