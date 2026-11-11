# Bateria WAR (tools) - opencode -> llama (tunel)

- **protocolo/provedor**: chat
- **modelo**: Gemma4-E4B-GK-Q4-nothink
- **gerado em**: 2026-11-11 00:19:18
- **eventos**: /tmp/opencode/llama-pure/metrics/events.jsonl

## Resumo

| metrica | valor |
| --- | --- |
| requisicoes | 7 |
| mensagens | 46 |
| tokens de prompt | 31247 |
| tokens de saida | 424 |
| velocidade media | 37.55 t/s |
| velocidade maxima | 43.83 t/s |
| maior sequencia do MESMO enunciado | 2 |
| maior similaridade de cauda | 0 |
| maior repeticao de linha | 1 |
| marcas de prosa de plano (total) | 0 |
| maior laco de TOOL (mesma tool+args) | 1 |
| requests com laco de tool (>=3) | 0 |

## Requests

| hora | endpoint | msgs | tools | system chars | prompt tok | saida tok | t/s | resp chars | streak | tool laco | tail sim | tail reps | dup line | meta | tools chamadas | enunciado |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 00:18:57 | /v1/chat/completions | 2 | 0 | 2119 | 859 | 10 | 10.38 | 45 | 1 | 0 | 0 | 0 | 1 | 0 | - | Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reem |
| 00:18:59 | /v1/chat/completions | 2 | 11 | 7111 | 4613 | 133 | 39.92 | 0 | 2 | 1 | 0 | 0 | 0 | 0 | write | Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reem |
| 00:19:01 | /v1/chat/completions | 5 | 11 | 7111 | 4895 | 50 | 42.41 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Created file successfully: Consulta.csproj |
| 00:19:05 | /v1/chat/completions | 6 | 11 | 7111 | 4835 | 160 | 41.86 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Created file successfully: src/Dominio/Cliente.cs |
| 00:19:06 | /v1/chat/completions | 8 | 11 | 7111 | 5018 | 18 | 43.83 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Created file successfully: src/Persistencia/ClienteRepository.cs |
| 00:19:08 | /v1/chat/completions | 11 | 11 | 7111 | 5510 | 24 | 42.0 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | /usr/share/dotnet/sdk/10.0.402/Sdks/Microsoft.NET.Sdk/targets/Microsof |
| 00:19:11 | /v1/chat/completions | 12 | 11 | 7111 | 5517 | 29 | 42.47 | 108 | 1 | 0 | 0 | 0 | 1 | 0 | - | Determinando os projetos a serem restaurados...   /tmp/opencode/llama- |

## Resultado da bateria

```
RESULTADO [belts-build-azul]: FAIL
== RESUMO: PASS=0 FAIL=1  (log: /tmp/opencode/llama-pure/war-tools-chat.log)
```
