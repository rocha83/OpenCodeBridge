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
2. Bridge `:4143` (`nohup dotnet Rochas.OpenCodeBridge/bin/Release/net9.0/Rochas.OpenCodeBridge.dll --port 4143 ... &`).
3. `curl /api/status` nas duas portas antes de rodar o agente.

## Segmentação obrigatória p/ modelos locais (2026-10-04)
- Runs longas multi-step morrem em `Compaction produced no summary`
  (Coder ~80% das runs; Qwen3 resiste, mas degrada em loops). REGRA:
  fatiar toda tarefa em micro-runs (1 artefato + verificação cada),
  leituras limitadas (≤2 por arquivo, blocos grandes), respostas curtas.
- Subagentes do mesmo modelo SÓ onde existem (sessão interativa com Task);
  em `run --standalone` NÃO existem — a segmentação sou eu (runs curtas
  sequenciais, nunca 1 run gigante).
- Sem reescrita de arquivo gigante de uma vez; sem loop de fix infinito
  (máx 3 tentativas por artefato, depois humano assume).
- Históricos: do fim ao começo, blocos de 8k.

## Conteúdo longo + subagentes que só agem (2026-10-04)
- Thinking é requisito: bridge sempre com thinking ligado p/ Qwen3
  (`--thinking events`, default); Coder não tem thinking — nada a desligar.
- Conteúdo longo (>10k tokens) NÃO vai inteiro no prompt: fatiar em blocos
  com sobreposição e um índice (arquivo:início:fim) antes de delegar.
- Subagente = Qwen3 com viés Coder: o Qwen3 delega a subagentes DELE MESMO
  instruídos a agir como Coder — zero conversa, só ação com tools: recebe
  slice + tarefa + critério, devolve artefato + evidência de execução.
  Sem relatório, sem resumo, sem pergunta de volta. Quem resume sou eu
  (ou o Qwen3 pai) no merge.
- Resposta de subagente sem evidência de execução = falha (repetir 1x,
  depois executar eu mesmo o slice).

## Enunciado rígido p/ subagentes (1.5B e Qwen3-consigo-mesmo)
- Vale para 1.5B **e** para subagentes do próprio Qwen3 com viés coder:
  1 tarefa por prompt, imperativo curto, exemplo de saída incluído,
  1 leitura + 1 escrita, verificação externa, temp 0.0.
