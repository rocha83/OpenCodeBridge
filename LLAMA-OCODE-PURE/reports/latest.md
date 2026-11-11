# Bateria WAR (tools) - opencode -> llama (tunel)

- **protocolo/provedor**: chat
- **modelo**: Gemma4-E4B-GK-Q4-nothink
- **gerado em**: 2026-11-11 00:40:00
- **eventos**: /tmp/opencode/llama-pure/metrics/events.jsonl

## Resumo

| metrica | valor |
| --- | --- |
| requisicoes | 7 |
| mensagens | 31 |
| tokens de prompt | 29529 |
| tokens de saida | 753 |
| velocidade media | 37.45 t/s |
| velocidade maxima | 45.45 t/s |
| maior sequencia do MESMO enunciado | 2 |
| maior similaridade de cauda | 0 |
| maior repeticao de linha | 1 |
| marcas de prosa de plano (total) | 0 |
| maior laco de TOOL (mesma tool+args) | 1 |
| requests com laco de tool (>=3) | 0 |

## Requests

| hora | endpoint | msgs | tools | system chars | prompt tok | saida tok | t/s | resp chars | streak | tool laco | tail sim | tail reps | dup line | meta | tools chamadas | enunciado |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 00:39:23 | /v1/chat/completions | 2 | 0 | 2119 | 859 | 10 | 34.86 | 45 | 1 | 0 | 0 | 0 | 1 | 0 | - | Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reem |
| 00:39:38 | /v1/chat/completions | 2 | 11 | 6540 | 4465 | 627 | 43.23 | 0 | 2 | 1 | 0 | 0 | 0 | 0 | write,write,write,shell | Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reem |
| 00:39:41 | /v1/chat/completions | 8 | 11 | 6540 | 5621 | 27 | 43.14 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | /usr/share/dotnet/sdk/10.0.402/Sdks/Microsoft.NET.Sdk/targets/Microsof |
| 00:39:45 | /v1/chat/completions | 9 | 11 | 6540 | 5631 | 40 | 43.3 | 141 | 1 | 0 | 0 | 0 | 1 | 0 | - | Determinando os projetos a serem restaurados...   /tmp/opencode/llama- |
| 00:39:52 | /v1/chat/completions | 3 | 0 | 2119 | 1925 | 11 | 30.8 | 51 | 1 | 0 | 0 | 0 | 1 | 0 | - | ENUNCIADO: Crie no backend .NET C# uma consulta de dados em uma tabela |
| 00:39:53 | /v1/chat/completions | 3 | 11 | 6540 | 5531 | 23 | 21.4 | 0 | 2 | 1 | 0 | 0 | 0 | 0 | shell | ENUNCIADO: Crie no backend .NET C# uma consulta de dados em uma tabela |
| 00:39:55 | /v1/chat/completions | 4 | 11 | 6540 | 5497 | 15 | 45.45 | 43 | 1 | 0 | 0 | 0 | 1 | 0 | - | Determinando os projetos a serem restaurados...   /tmp/opencode/llama- |

## Resultado da bateria

```
RESULTADO [belts-build-azul]: PASS
== RESUMO: PASS=1 FAIL=0  (log: /tmp/opencode/llama-pure/war-tools-chat.log)
```
