# Parte NATIVA do opencode (não é nossa — não reproduzir na bridge)

Gerado pelo próprio opencode no request capturado. Não deve ser copiado para o
harness da bridge: a API já entrega tools/ambiente, e o catálogo é específico da
versão do opencode.

| Bloco | Linhas | Chars | O que é |
|---|---|---|---|
| Your Model | 11-14 | 111 | metadados do modelo (name/provider/model id), gerados do `opencode.json` |
| Code Mode | 15-132 | 4.476 | instruções do `execute` + catálogo das 5 tools `opencode.*` (`list_mcp_resources`, `models`, `read_mcp_resource`, `session_move`, `session_rename`) |
| available_skills | 133-144 | 859 | skills instaladas (`opencode`, `report`) |
| Environment | 193-203 | 641 | data, cwd/workspace, git?, plataforma, preferência de `/tmp` |

**Total nativo: ~6.087 chars (43%).**

Também é nativo (fora do system prompt) o request interno de *title generator*
(`gpt-6-luna`, ~2,6 KB), que roda antes da conversa principal.

## Decisão
- **Não portar** Code Mode nem o catálogo assinado de tools para a bridge.
- **Exceção deliberada:** o main harness preserva os blocos dinâmicos `<env>` e
  `<available_skills>` que o cliente já envia no system; não os hardcode, apenas
  mantém o que veio. Ver `MAIN-HARNESS.md`.
- No caminho gerido, o system base é o `MainHarness` hardcoded; no caminho
  opencode, o system do cliente continua e as tails entram como delta. O
  catálogo de tools vem da API.
