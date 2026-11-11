# Bateria WAR (tools) - opencode -> llama (tunel)

- **protocolo/provedor**: chat
- **modelo**: Gemma4-E4B-GK-Q4-nothink
- **gerado em**: 2026-11-11 00:46:59
- **eventos**: /tmp/opencode/llama-pure/metrics/events.jsonl

## Resumo

| metrica | valor |
| --- | --- |
| requisicoes | 21 |
| mensagens | 260 |
| tokens de prompt | 113541 |
| tokens de saida | 2092 |
| velocidade media | 42.21 t/s |
| velocidade maxima | 45.73 t/s |
| maior sequencia do MESMO enunciado | 2 |
| maior similaridade de cauda | 0.035 |
| maior repeticao de linha | 1 |
| marcas de prosa de plano (total) | 1 |
| maior laco de TOOL (mesma tool+args) | 2 |
| requests com laco de tool (>=3) | 0 |

## Requests

| hora | endpoint | msgs | tools | system chars | prompt tok | saida tok | t/s | resp chars | streak | tool laco | tail sim | tail reps | dup line | meta | tools chamadas | enunciado |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 00:45:44 | /v1/chat/completions | 2 | 0 | 2119 | 947 | 13 | 33.66 | 50 | 1 | 0 | 0 | 0 | 1 | 0 | - | Trabalhe em .. Implemente o cadastro de clientes e fornecedores no bac |
| 00:45:46 | /v1/chat/completions | 2 | 11 | 6540 | 4555 | 135 | 38.65 | 0 | 2 | 1 | 0 | 0 | 0 | 0 | write | Trabalhe em .. Implemente o cadastro de clientes e fornecedores no bac |
| 00:45:48 | /v1/chat/completions | 5 | 11 | 6540 | 4841 | 51 | 44.65 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Created file successfully: Cadastros.csproj |
| 00:45:50 | /v1/chat/completions | 6 | 11 | 6540 | 4782 | 55 | 45.23 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Created file successfully: src/Dominio/Cliente.cs |
| 00:45:54 | /v1/chat/completions | 8 | 11 | 6540 | 4861 | 205 | 44.52 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Created file successfully: src/Dominio/Fornecedor.cs |
| 00:45:58 | /v1/chat/completions | 10 | 11 | 6540 | 5088 | 149 | 44.76 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | write | Created file successfully: src/Repositorio/Repository.cs |
| 00:45:59 | /v1/chat/completions | 12 | 11 | 6540 | 5259 | 38 | 45.73 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | Created file successfully: src/Servico/Service.cs |
| 00:46:01 | /v1/chat/completions | 15 | 11 | 6540 | 5780 | 31 | 45.18 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | shell | /usr/share/dotnet/sdk/10.0.402/Sdks/Microsoft.NET.Sdk/targets/Microsof |
| 00:46:07 | /v1/chat/completions | 17 | 11 | 6540 | 6802 | 85 | 42.73 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | edit | Determinando os projetos a serem restaurados...   /tmp/opencode/llama- |
| 00:46:09 | /v1/chat/completions | 19 | 11 | 6540 | 6712 | 91 | 42.83 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | edit | Edited src/Dominio/Cliente.cs (1 replacement) |
| 00:46:19 | /v1/chat/completions | 20 | 11 | 6540 | 6696 | 392 | 43.37 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | edit | Edited src/Dominio/Fornecedor.cs (1 replacement) |
| 00:46:26 | /v1/chat/completions | 22 | 11 | 6540 | 7126 | 280 | 42.87 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | edit | {"error":{"type":"tool.execution","message":"No changes to apply: oldS |
| 00:46:27 | /v1/chat/completions | 24 | 11 | 6540 | 7444 | 31 | 43.51 | 0 | 2 | 1 | 0 | 0 | 0 | 0 | shell | {"error":{"type":"tool.execution","message":"No changes to apply: oldS |
| 00:46:33 | /v1/chat/completions | 27 | 11 | 6540 | 8965 | 84 | 42.75 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | edit | Determinando os projetos a serem restaurados...   Todos os projetos es |
| 00:46:37 | /v1/chat/completions | 28 | 11 | 6540 | 8810 | 186 | 41.73 | 781 | 1 | 0 | 0.035 | 0 | 1 | 1 | - | {"error":{"type":"tool.execution","message":"No changes to apply: oldS |
| 00:46:44 | /v1/chat/completions | 3 | 0 | 2119 | 1956 | 15 | 28.93 | 34 | 1 | 0 | 0 | 0 | 1 | 0 | - | ENUNCIADO: Trabalhe em .. Implemente o cadastro de clientes e forneced |
| 00:46:48 | /v1/chat/completions | 3 | 11 | 6540 | 5564 | 146 | 40.93 | 0 | 2 | 1 | 0 | 0 | 0 | 0 | edit,edit,edit | ENUNCIADO: Trabalhe em .. Implemente o cadastro de clientes e forneced |
| 00:46:49 | /v1/chat/completions | 7 | 11 | 6540 | 5777 | 42 | 43.54 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | edit | {"error":{"type":"tool.execution","message":"Could not find oldString  |
| 00:46:51 | /v1/chat/completions | 8 | 11 | 6540 | 5742 | 42 | 44.16 | 0 | 1 | 2 | 0 | 0 | 0 | 0 | edit | {"error":{"type":"tool.execution","message":"Could not find oldString  |
| 00:46:52 | /v1/chat/completions | 10 | 11 | 6540 | 5834 | 21 | 44.51 | 0 | 2 | 1 | 0 | 0 | 0 | 0 | shell | {"error":{"type":"tool.execution","message":"Could not find oldString  |
| 00:46:53 | /v1/chat/completions | 12 | 11 | 6540 | 0 | 0 | 0 | 232 | 1 | 0 | 0 | 0 | 1 | 0 | - | /tmp/opencode/llama-pure/war-tools-chat/belts-build-roxa/src/Dominio/C |

## Resultado da bateria

```
```
