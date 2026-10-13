# PENDÊNCIAS — OpenCodeBridge (atualizado 2026-10-13)

## Testes (com mock, sem E2E)
- Unit/integração com `MockBridgeClient` cobrindo: loop com tools nativas no
  bypass + vínculo tool→bolha (`message_id`/`sub`). Hoje só o E2E cobre.

## Review — nomes intuitivos/legíveis (2026-10-11)
- Renomear classes, métodos e variáveis crípticas p/ nomes de domínio claros
  em toda a solução (ex.: `thinkPct`, `subChunk`, `ensureSubCard`,
  `toolStatus`, siglas e abreviações). Vale p/ C# e JS. Fazer em passada
  dedicada, com build + suites verdes por arquivo.
