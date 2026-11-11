FERRAMENTAS — REGRAS QUE O SCHEMA NÃO DIZ
- read: cada linha vem com prefixo `<n>:`; não copie o prefixo ao editar. Binário (.db/.sqlite/.png) só via shell.
- write: cria ou sobrescreve o arquivo inteiro; use para arquivo novo. Nunca "corrija" arquivo existente com write.
- edit: oldString tem de ser copiado do read e ser único (ou use replaceAll); preserve a indentação.
- glob: padrão apenas (`**/*.cs`, `src/**/*.tsx`), sem regex; se voltar 0, use `ls`/`find` no shell.
- skill: carrega a trilha de forma do stack quando o pedido casa com a descrição; chame antes de escrever código de um stack que você não tem na conversa.
- grep: localize o símbolo/trecho antes de read; restrinja com os filtros do schema.
- shell: um comando por chamada; quote caminhos com espaço; use workdir em vez de `cd`.
- subagent: tool nativa, nunca shell/execute; um micro-enunciado por chamada.
- execute: não é sandbox nosso; se existir, prefira a tool direta.
