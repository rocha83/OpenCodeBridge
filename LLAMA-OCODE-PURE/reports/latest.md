# Bateria WAR (ent) - opencode -> llama (tunel)

- **protocolo/provedor**: chat
- **modelo**: Gemma4-E4B-GK-Q4
- **gerado em**: 2026-11-20 22:14:13
- **eventos**: /media/mint/FT/logs/collector/metrics/events.jsonl

## Resumo

| metrica | valor |
| --- | --- |
| requisicoes | 18 |
| mensagens | 45 |
| tokens de prompt | 46568 |
| tokens de saida | 45240 |
| velocidade media | 54.02 t/s |
| velocidade maxima | 55.24 t/s |
| maior sequencia do MESMO enunciado | 2 |
| maior similaridade de cauda | 0.299 |
| maior repeticao de linha | 8 |
| marcas de prosa de plano (total) | 2 |
| maior laco de TOOL (mesma tool+args) | 0 |
| requests com laco de tool (>=3) | 0 |

## Requests

| hora | endpoint | msgs | tools | system chars | prompt tok | saida tok | t/s | resp chars | streak | tool laco | tail sim | tail reps | dup line | meta | tools chamadas | enunciado |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 21:59:05 | /v1/chat/completions | 2 | 0 | 7306 | 2236 | 2575 | 55.0 | 8228 | 1 | 0 | 0.121 | 0 | 5 | 0 | - | C# .NET 9 Minimal API + Dapper + Postgres: CRUD completo de Product (I |
| 21:59:28 | /v1/chat/completions | 3 | 0 | 7621 | 2444 | 1253 | 55.24 | 2807 | 2 | 0 | 0.086 | 0 | 1 | 1 | - | C# .NET 9 Minimal API + Dapper + Postgres: CRUD completo de Product (I |
| 22:00:26 | /v1/chat/completions | 2 | 0 | 7306 | 2253 | 2849 | 53.96 | 7990 | 1 | 0 | 0.069 | 0 | 8 | 0 | - | C# .NET 9 Minimal API + Dapper + Postgres: CRUD de Customer (Id, Name, |
| 22:00:53 | /v1/chat/completions | 3 | 0 | 7622 | 2461 | 1424 | 54.77 | 3141 | 2 | 0 | 0.06 | 0 | 2 | 0 | - | C# .NET 9 Minimal API + Dapper + Postgres: CRUD de Customer (Id, Name, |
| 22:02:03 | /v1/chat/completions | 2 | 0 | 7306 | 2304 | 3220 | 54.57 | 10344 | 1 | 0 | 0.104 | 0 | 4 | 0 | - | C# .NET 9 + Dapper + Postgres: POST /api/saleinvoices cria SaleInvoice |
| 22:03:13 | /v1/chat/completions | 3 | 0 | 7625 | 2513 | 3789 | 54.2 | 10968 | 1 | 0 | 0.043 | 0 | 3 | 1 | - | C# .NET 9 + Dapper + Postgres: POST /api/saleinvoices cria SaleInvoice |
| 22:04:14 | /v1/chat/completions | 2 | 0 | 7306 | 2286 | 2468 | 54.82 | 8623 | 1 | 0 | 0.047 | 0 | 4 | 0 | - | C# .NET 9 + Postgres: entregue UMA classe OutboxWorker que HERDA Backg |
| 22:05:20 | /v1/chat/completions | 3 | 0 | 7615 | 2492 | 3528 | 53.23 | 11452 | 1 | 0 | 0.074 | 0 | 3 | 0 | - | C# .NET 9 + Postgres: entregue UMA classe OutboxWorker que HERDA Backg |
| 22:06:13 | /v1/chat/completions | 2 | 0 | 7306 | 2271 | 2107 | 53.46 | 6437 | 1 | 0 | 0.047 | 0 | 2 | 0 | - | C# .NET 9 + Redis (StackExchange.Redis): repositorio ProductRepository |
| 22:07:01 | /v1/chat/completions | 3 | 0 | 7620 | 2479 | 2506 | 53.52 | 7691 | 2 | 0 | 0.047 | 0 | 3 | 0 | - | C# .NET 9 + Redis (StackExchange.Redis): repositorio ProductRepository |
| 22:07:42 | /v1/chat/completions | 2 | 0 | 7306 | 4293 | 1283 | 48.58 | 2116 | 1 | 0 | 0.125 | 0 | 1 | 0 | - | REPOSITORIO DO STEP ANTERIOR (bloco 1, CONTEXTO) [0m > plan · Gemma4- |
| 22:08:01 | /v1/chat/completions | 3 | 0 | 7617 | 4501 | 913 | 53.9 | 1988 | 2 | 0 | 0.082 | 0 | 1 | 0 | - | REPOSITORIO DO STEP ANTERIOR (bloco 1, CONTEXTO) [0m > plan · Gemma4- |
| 22:09:03 | /v1/chat/completions | 2 | 0 | 7306 | 2223 | 3208 | 54.53 | 11597 | 1 | 0 | 0.02 | 0 | 4 | 0 | - | C# .NET 9 + Dapper: endpoint POST /api/schedule publica Clinic.Schedul |
| 22:10:07 | /v1/chat/completions | 3 | 0 | 7612 | 2429 | 3476 | 54.33 | 11650 | 2 | 0 | 0.109 | 0 | 5 | 0 | - | C# .NET 9 + Dapper: endpoint POST /api/schedule publica Clinic.Schedul |
| 22:11:31 | /v1/chat/completions | 2 | 0 | 7306 | 2276 | 2846 | 54.79 | 5859 | 1 | 0 | 0.299 | 0 | 3 | 0 | - | ERP: regra de preco final = base - desconto + imposto, onde desconto e |
| 22:11:47 | /v1/chat/completions | 3 | 0 | 7612 | 2441 | 841 | 54.97 | 1432 | 2 | 0 | 0.126 | 0 | 1 | 0 | - | ERP: regra de preco final = base - desconto + imposto, onde desconto e |
| 22:13:39 | /v1/chat/completions | 2 | 0 | 7306 | 2230 | 5628 | 53.74 | 17710 | 1 | 0 | 0.132 | 0 | 7 | 0 | - | React 19 + TS + Tailwind: Dashboard que consome GET /api/saleinvoices? |
| 22:14:04 | /v1/chat/completions | 3 | 0 | 7618 | 2436 | 1326 | 54.81 | 3247 | 2 | 0 | 0.125 | 0 | 1 | 0 | - | React 19 + TS + Tailwind: Dashboard que consome GET /api/saleinvoices? |

## Sinal de repeticao/devagacao (top 10)

| hora | streak | tool laco | tail sim | tail reps | dup line | meta | resp chars | tools | enunciado |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 22:10:07 | 2 | 0 | 0.109 | 0 | 5 | 0 | 11650 | - | C# .NET 9 + Dapper: endpoint POST /api/schedule publica Clinic.Schedule no barra |
| 22:07:01 | 2 | 0 | 0.047 | 0 | 3 | 0 | 7691 | - | C# .NET 9 + Redis (StackExchange.Redis): repositorio ProductRepository com cache |
| 22:11:31 | 1 | 0 | 0.299 | 0 | 3 | 0 | 5859 | - | ERP: regra de preco final = base - desconto + imposto, onde desconto e min(desco |
| 22:13:39 | 1 | 0 | 0.132 | 0 | 7 | 0 | 17710 | - | React 19 + TS + Tailwind: Dashboard que consome GET /api/saleinvoices?days=30 do |
| 21:59:05 | 1 | 0 | 0.121 | 0 | 5 | 0 | 8228 | - | C# .NET 9 Minimal API + Dapper + Postgres: CRUD completo de Product (Id, Name, P |
| 22:02:03 | 1 | 0 | 0.104 | 0 | 4 | 0 | 10344 | - | C# .NET 9 + Dapper + Postgres: POST /api/saleinvoices cria SaleInvoice (header:  |
| 22:05:20 | 1 | 0 | 0.074 | 0 | 3 | 0 | 11452 | - | C# .NET 9 + Postgres: entregue UMA classe OutboxWorker que HERDA BackgroundServi |
| 22:00:26 | 1 | 0 | 0.069 | 0 | 8 | 0 | 7990 | - | C# .NET 9 Minimal API + Dapper + Postgres: CRUD de Customer (Id, Name, Email, Cr |
| 22:04:14 | 1 | 0 | 0.047 | 0 | 4 | 0 | 8623 | - | C# .NET 9 + Postgres: entregue UMA classe OutboxWorker que HERDA BackgroundServi |
| 22:03:13 | 1 | 0 | 0.043 | 0 | 3 | 1 | 10968 | - | C# .NET 9 + Dapper + Postgres: POST /api/saleinvoices cria SaleInvoice (header:  |

## Resultado da bateria

```
RESULTADO [crud-product]: FAIL
RESULTADO [crud-customer]: PASS
RESULTADO [crud-saleinvoice]: PASS
RESULTADO [worker]: PASS
RESULTADO [redis-cache]: PASS
RESULTADO [redis-di]: PASS
RESULTADO [bus]: PASS
RESULTADO [erp]: PASS
RESULTADO [dashboard]: FAIL
== RESUMO: PASS=7 FAIL=2 SKIP=0  (log: /media/mint/FT/logs/collector/war/war-ent-chat.log)
```
