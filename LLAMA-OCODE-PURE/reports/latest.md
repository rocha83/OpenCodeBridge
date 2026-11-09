# Bateria WAR (tools) - opencode -> llama (tunel)

- **protocolo/provedor**: chat
- **modelo**: Gemma4-E4B-GK-Q4-nothink
- **gerado em**: 2026-11-09 16:29:31
- **eventos**: /tmp/opencode/llama-pure/metrics/events.jsonl

## Resumo

| metrica | valor |
| --- | --- |
| requisicoes | 15 |
| mensagens | 212 |
| tokens de prompt | 101401 |
| tokens de saida | 1343 |
| velocidade media | 43.98 t/s |
| velocidade maxima | 46.63 t/s |
| maior sequencia do MESMO enunciado | 2 |
| maior similaridade de cauda | 0 |
| maior repeticao de linha | 1 |
| marcas de prosa de plano (total) | 0 |
| maior laco de TOOL (mesma tool+args) | 1 |
| requests com laco de tool (>=3) | 0 |

## Requests

| hora | endpoint | msgs | tools | system chars | prompt tok | saida tok | t/s | resp chars | streak | tool laco | tail sim | tail reps | dup line | meta | tools chamadas | enunciado |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 16:28:48 | /v1/chat/completions | 2 | 0 | 2119 | 755 | 13 | 21.19 | 50 | 1 | 0 | 0 | 0 | 1 | 0 | - | Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reem |
| 16:28:51 | /v1/chat/completions | 2 | 12 | 8845 | 5379 | 139 | 44.09 | 0 | 2 | 1 | 0 | 0 | 0 | 0 | write | Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reem |
| 16:28:53 | /v1/chat/completions | 4 | 12 | 8845 | 5541 | 51 | 46.54 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Created file successfully: Reembolsos/Reembolsos.csproj |
| 16:28:58 | /v1/chat/completions | 6 | 12 | 8845 | 5617 | 223 | 45.57 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Created file successfully: Reembolsos/Models/Reembolso.cs |
| 16:28:59 | /v1/chat/completions | 8 | 12 | 8845 | 5867 | 42 | 46.28 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Created file successfully: Reembolsos/Repositories/RepositorioReembols |
| 16:29:03 | /v1/chat/completions | 10 | 12 | 8845 | 6305 | 60 | 46.0 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | edit | Determinando os projetos a serem restaurados...   /tmp/opencode/llama- |
| 16:29:04 | /v1/chat/completions | 12 | 12 | 8845 | 6393 | 30 | 46.45 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Edited Reembolsos/Repositories/RepositorioReembolso.cs (1 replacement) |
| 16:29:07 | /v1/chat/completions | 14 | 12 | 8845 | 7202 | 72 | 45.6 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | edit | /tmp/opencode/llama-pure/war-tools-chat/belts-build-azul/Reembolsos/Re |
| 16:29:12 | /v1/chat/completions | 16 | 12 | 8845 | 7329 | 222 | 44.94 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | {"error":{"type":"tool.execution","message":"Could not find oldString  |
| 16:29:13 | /v1/chat/completions | 18 | 12 | 8845 | 7579 | 30 | 46.63 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Wrote file successfully: Reembolsos/Repositories/RepositorioReembolso. |
| 16:29:16 | /v1/chat/completions | 20 | 12 | 8845 | 7950 | 74 | 45.33 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | edit | /tmp/opencode/llama-pure/war-tools-chat/belts-build-azul/Reembolsos/Re |
| 16:29:17 | /v1/chat/completions | 22 | 12 | 8845 | 8052 | 30 | 46.3 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Edited Reembolsos/Repositories/RepositorioReembolso.cs (1 replacement) |
| 16:29:24 | /v1/chat/completions | 24 | 12 | 8845 | 8931 | 227 | 44.45 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | /tmp/opencode/llama-pure/war-tools-chat/belts-build-azul/Reembolsos/Re |
| 16:29:25 | /v1/chat/completions | 26 | 12 | 8845 | 9186 | 30 | 45.95 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Wrote file successfully: Reembolsos/Repositories/RepositorioReembolso. |
| 16:29:29 | /v1/chat/completions | 28 | 12 | 8845 | 9315 | 100 | 44.38 | 301 | 1 | 0 | 0 | 0 | 1 | 0 | - | Reembolsos -> /tmp/opencode/llama-pure/war-tools-chat/belts-build-azul |

## Resultado da bateria

```
RESULTADO [belts-build-azul]: PASS
== RESUMO: PASS=1 FAIL=0  (log: /tmp/opencode/llama-pure/war-tools-chat.log)
```
