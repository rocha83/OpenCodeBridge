# Bateria WAR (ent) - opencode -> llama (tunel)

- **protocolo/provedor**: chat
- **modelo**: Gemma4-E4B-GK-Q4
- **gerado em**: 2026-11-10 11:29:44
- **eventos**: /tmp/opencode/llama-pure/metrics/events.jsonl

## Resumo

| metrica | valor |
| --- | --- |
| requisicoes | 2 |
| mensagens | 5 |
| tokens de prompt | 1244 |
| tokens de saida | 3067 |
| velocidade media | 28.95 t/s |
| velocidade maxima | 37.56 t/s |
| maior sequencia do MESMO enunciado | 2 |
| maior similaridade de cauda | 0.139 |
| maior repeticao de linha | 5 |
| marcas de prosa de plano (total) | 0 |
| maior laco de TOOL (mesma tool+args) | 0 |
| requests com laco de tool (>=3) | 0 |

## Requests

| hora | endpoint | msgs | tools | system chars | prompt tok | saida tok | t/s | resp chars | streak | tool laco | tail sim | tail reps | dup line | meta | tools chamadas | enunciado |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 11:28:46 | /v1/chat/completions | 2 | 0 | 2119 | 681 | 422 | 20.34 | 43 | 1 | 0 | 0 | 0 | 1 | 0 | - | C# .NET 9 Minimal API + Dapper + Postgres: CRUD completo de Product (I |
| 11:29:36 | /v1/chat/completions | 3 | 0 | 1027 | 563 | 2645 | 37.56 | 6700 | 2 | 0 | 0.139 | 0 | 5 | 0 | - | C# .NET 9 Minimal API + Dapper + Postgres: CRUD completo de Product (I |

## Sinal de repeticao/devagacao (top 10)

| hora | streak | tool laco | tail sim | tail reps | dup line | meta | resp chars | tools | enunciado |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 11:29:36 | 2 | 0 | 0.139 | 0 | 5 | 0 | 6700 | - | C# .NET 9 Minimal API + Dapper + Postgres: CRUD completo de Product (Id, Name, P |

## Resultado da bateria

```
RESULTADO [crud-product]: PASS
== RESUMO: PASS=1 FAIL=0  (log: /tmp/opencode/llama-pure/war-ent-chat.log)
```
