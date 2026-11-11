# Bateria WAR (tools) - opencode -> llama (tunel)

- **protocolo/provedor**: chat
- **modelo**: Gemma4-E4B-GK-Q4-nothink
- **gerado em**: 2026-11-11 00:45:39
- **eventos**: /tmp/opencode/llama-pure/metrics/events.jsonl

## Resumo

| metrica | valor |
| --- | --- |
| requisicoes | 9 |
| mensagens | 76 |
| tokens de prompt | 41836 |
| tokens de saida | 565 |
| velocidade media | 40.87 t/s |
| velocidade maxima | 47.7 t/s |
| maior sequencia do MESMO enunciado | 2 |
| maior similaridade de cauda | 0 |
| maior repeticao de linha | 1 |
| marcas de prosa de plano (total) | 0 |
| maior laco de TOOL (mesma tool+args) | 1 |
| requests com laco de tool (>=3) | 0 |

## Requests

| hora | endpoint | msgs | tools | system chars | prompt tok | saida tok | t/s | resp chars | streak | tool laco | tail sim | tail reps | dup line | meta | tools chamadas | enunciado |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 00:45:13 | /v1/chat/completions | 2 | 0 | 2119 | 941 | 12 | 13.89 | 36 | 1 | 0 | 0 | 0 | 1 | 0 | - | Implemente o dominio backend C# de um CRUD de reembolsos corporativos  |
| 00:45:14 | /v1/chat/completions | 2 | 11 | 6542 | 4547 | 81 | 41.08 | 0 | 2 | 1 | 0 | 0 | 0 | 0 | write | Implemente o dominio backend C# de um CRUD de reembolsos corporativos  |
| 00:45:16 | /v1/chat/completions | 5 | 11 | 6542 | 4778 | 49 | 43.65 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Created file successfully: Dominio.csproj |
| 00:45:18 | /v1/chat/completions | 6 | 11 | 6542 | 4717 | 98 | 44.81 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Created file successfully: src/Dominio/Cliente.cs |
| 00:45:19 | /v1/chat/completions | 8 | 11 | 6542 | 4839 | 22 | 44.87 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Created file successfully: src/Dominio/Reembolso.cs |
| 00:45:21 | /v1/chat/completions | 11 | 11 | 6542 | 5338 | 29 | 45.49 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | /usr/share/dotnet/sdk/10.0.402/Sdks/Microsoft.NET.Sdk/targets/Microsof |
| 00:45:28 | /v1/chat/completions | 12 | 11 | 6542 | 5351 | 171 | 43.49 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Determinando os projetos a serem restaurados...   /tmp/opencode/llama- |
| 00:45:29 | /v1/chat/completions | 14 | 11 | 6542 | 5543 | 40 | 47.7 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Created file successfully: src/Data/schema.sql |
| 00:45:34 | /v1/chat/completions | 16 | 11 | 6542 | 5782 | 63 | 42.89 | 223 | 1 | 0 | 0 | 0 | 1 | 0 | - | Dominio -> /tmp/opencode/llama-pure/war-tools-chat/belts-build-verde/b |

## Resultado da bateria

```
RESULTADO [belts-build-verde]: PASS
== RESUMO: PASS=1 FAIL=0  (log: /tmp/opencode/llama-pure/war-tools-chat.log)
```
