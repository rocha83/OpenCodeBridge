# PENDÊNCIAS — OpenCodeBridge.Web (2026-10-10)

## Árvore do chat na sessão
- Persistir mensagens intermediárias do loop (relatos `[exec]`, textos de tool)
  como mensagens da sessão — hoje só user inicial + assistant final ficam na árvore.
- Garantir conteúdo do assistant + cards de subagente sempre persistidos
  (texto e thinking separados, sem colar).

## Testes (com mock, sem E2E)
- Unit/integração com `MockBridgeClient` cobrindo: persistência da árvore
  (user → tools → assistant), acordeões de thinking/subagente, `tool_calls`
  no SQLite por sessão. Hoje só o E2E cobre — lacuna assumida.

## Remover após testes
- `cap200` (teto de 200 linhas na EXIBIÇÃO do thinking, `Index.cshtml`):
  exibição volta integral; gravação já é integral.
