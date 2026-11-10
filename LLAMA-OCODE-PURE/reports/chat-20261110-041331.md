# Bateria WAR (tools) - opencode -> llama (tunel)

- **protocolo/provedor**: chat
- **modelo**: Gemma4-E4B-GK-Q4-nothink
- **gerado em**: 2026-11-10 04:13:31
- **eventos**: /tmp/opencode/llama-pure/metrics/events.jsonl

## Resumo

| metrica | valor |
| --- | --- |
| requisicoes | 17 |
| mensagens | 274 |
| tokens de prompt | 102990 |
| tokens de saida | 1056 |
| velocidade media | 42.28 t/s |
| velocidade maxima | 46.17 t/s |
| maior sequencia do MESMO enunciado | 6 |
| maior similaridade de cauda | 0 |
| maior repeticao de linha | 1 |
| marcas de prosa de plano (total) | 0 |
| maior laco de TOOL (mesma tool+args) | 6 |
| requests com laco de tool (>=3) | 4 |

## Requests

| hora | endpoint | msgs | tools | system chars | prompt tok | saida tok | t/s | resp chars | streak | tool laco | tail sim | tail reps | dup line | meta | tools chamadas | enunciado |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 04:12:36 | /v1/chat/completions | 2 | 0 | 2119 | 813 | 7 | 3.47 | 28 | 1 | 0 | 0 | 0 | 1 | 0 | - | Implemente o build do backend C# do Portal do Colaborador como um cons |
| 04:12:39 | /v1/chat/completions | 2 | 11 | 8183 | 4915 | 133 | 44.5 | 0 | 2 | 1 | 0 | 0 | 0 | 0 | write | Implemente o build do backend C# do Portal do Colaborador como um cons |
| 04:12:49 | /v1/chat/completions | 4 | 11 | 8183 | 5065 | 434 | 44.44 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Created file successfully: Portal.csproj |
| 04:12:50 | /v1/chat/completions | 6 | 11 | 8183 | 5518 | 21 | 46.03 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Created file successfully: src/Program.cs |
| 04:12:52 | /v1/chat/completions | 8 | 11 | 8183 | 5860 | 27 | 45.38 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | /usr/share/dotnet/sdk/10.0.402/Sdks/Microsoft.NET.Sdk/targets/Microsof |
| 04:12:56 | /v1/chat/completions | 10 | 11 | 8183 | 6029 | 69 | 44.42 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Determinando os projetos a serem restaurados...   /tmp/opencode/llama- |
| 04:12:57 | /v1/chat/completions | 12 | 11 | 8183 | 6119 | 20 | 46.17 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Created file successfully: src/Data/schema.sql |
| 04:13:01 | /v1/chat/completions | 14 | 11 | 8183 | 6194 | 182 | 44.08 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | CREATE TABLE Colaboradores (Id INTEGER PRIMARY KEY, Nome TEXT NOT NULL |
| 04:13:02 | /v1/chat/completions | 16 | 11 | 8183 | 6397 | 20 | 45.36 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Created file successfully: src/ml/train.py |
| 04:13:03 | /v1/chat/completions | 18 | 11 | 8183 | 6585 | 29 | 44.88 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | import numpy as np  def train_perceptron(X, y):     w = np.zeros(X[0]. |
| 04:13:07 | /v1/chat/completions | 20 | 11 | 8183 | 7118 | 19 | 45.03 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Portal -> /tmp/opencode/llama-pure/war-tools-chat/belts-build-preta/bi |
| 04:13:10 | /v1/chat/completions | 22 | 11 | 8183 | 7559 | 19 | 44.74 | 0 | 1 | 2 | 0 | 0 | 0 | 0 | shell | Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005 |
| 04:13:13 | /v1/chat/completions | 24 | 11 | 8183 | 8000 | 19 | 44.94 | 0 | 2 | 3 | 0 | 0 | 0 | 0 | shell | Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005 |
| 04:13:16 | /v1/chat/completions | 26 | 11 | 8183 | 8527 | 19 | 44.64 | 0 | 3 | 4 | 0 | 0 | 0 | 0 | shell | Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005 |
| 04:13:20 | /v1/chat/completions | 28 | 11 | 8183 | 8882 | 19 | 44.51 | 0 | 4 | 5 | 0 | 0 | 0 | 0 | shell | Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005 |
| 04:13:23 | /v1/chat/completions | 30 | 11 | 8183 | 9409 | 19 | 43.97 | 0 | 5 | 6 | 0 | 0 | 0 | 0 | shell | Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005 |
| 04:13:25 | /v1/chat/completions | 32 | 11 | 8183 | 0 | 0 | 0 | 314 | 6 | 0 | 0 | 0 | 1 | 0 | - | Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005 |

## Sinal de repeticao/devagacao (top 10)

| hora | streak | tool laco | tail sim | tail reps | dup line | meta | resp chars | tools | enunciado |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 04:13:23 | 5 | 6 | 0 | 0 | 0 | 0 | 0 | shell | Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite  |
| 04:13:20 | 4 | 5 | 0 | 0 | 0 | 0 | 0 | shell | Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite  |
| 04:13:16 | 3 | 4 | 0 | 0 | 0 | 0 | 0 | shell | Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite  |
| 04:13:13 | 2 | 3 | 0 | 0 | 0 | 0 | 0 | shell | Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite  |
| 04:13:25 | 6 | 0 | 0 | 0 | 1 | 0 | 314 | - | Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite  |

## Resultado da bateria

```
RESULTADO [belts-build-preta]: PASS
== RESUMO: PASS=1 FAIL=0  (log: /tmp/opencode/llama-pure/war-tools-chat.log)
```
