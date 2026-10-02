# AGENTS.md — como o opencode desta máquina usa o Qwen local

## Garantia: o agente dos testes é o Qwen3-8B local
- Único provider em `~/.config/opencode/opencode.json` (+ `/root/...`): `openai`
  com `baseURL http://127.0.0.1:4143/v1` → **tudo passa pela bridge** (nada vai p/ nuvem).
- Modelo topo: `openai/qwen3-8b-awq` (`tool_call: true`, ctx `28672`, out `8192`).
- Agente padrão do `run`: `build` (`mode: primary`, model travado no Qwen local, `steps: 30`).
- **Comandos de teste DEVEM pinar explicitamente:** 
  `opencode run --standalone --thinking --auto --agent build --model openai/qwen3-8b-awq "<prompt>"`
- Verificação: cada chamada aparece em `/tmp/qwen3-bridge.log` com `"model":"qwen3-8b-awq"`.
  Sem linha no log = não usou o Qwen local. Conferir com:
  `tail -1 /tmp/qwen3-bridge.log`

## Tuning aplicado (diálogo enxuto, tools intactas)
- `temperature: 0.2` (agentes + default da bridge) — direto, sem enrolação.
- System prompt: `...assistente conciso. Responda de forma direta, usando no máximo
  duas frases ou um parágrafo curto, sem introduções ou saudações.
  Código e tool calls sempre completos, nunca truncados.`
- `max_tokens` segue `8192` (código precisa de saída longa; concisão vem do prompt, não de corte).
- `stop` **não** é enviado com tools (truncaria o JSON da tool call); a bridge só
  repassa `stop` se o cliente pedir explicitamente (`BuildChatRequest`).
- `presence_penalty`: Responses API não define; ignorado de propósito.

## Ordem de boot p/ testes
1. vLLM `:4100` (`sudo bash ~/Desktop/vllm-bridge/restart-vllm-tools.sh`, parser `hermes`).
2. Bridge `:4143` (`nohup dotnet bin/Release/net9.0/Rochas.OpenCodeBridge.dll --port 4143 ... &`).
3. `curl /api/status` nas duas portas antes de rodar o agente.
