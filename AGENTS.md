# AGENTS.md — como esta máquina usa o Qwen local

## Garantia: tudo local, nada p/ nuvem
- GPU `llama :4100`: `Qwen3.5-9B Q4_K_M` (drive persistente), ctx `159744` (156k),
  1 slot (`-np 1`), `--prio 2` → bridge `:4124` (`qwen3.5-9b`, saída `32768`).
- GPU `llama :4111`: `Qwen3-VL-4B Q3_K_M` + `mmproj Q8_0` (drive persistente),
  ctx `65536` (64k), 2 slots (`-np 2`), `--prio 0` → bridge `:4125`
  (`qwen3-vl-4b`, saída `16384`). O 4B substitui os modelos pequenos anteriores (ex.: Coder 2.5):
  é o executor da linha CPU.
- Web em `:4130` (config `Web:Port` no `appsettings.json`, override via env
  `BRIDGE_WEB_PORT`); `:4150` reservado ao Caddy (Grafana `4800`); banco em `BRIDGE_WEB_DB_DIR` (disco local).
- Verificação: cada chamada aparece em `/tmp/bridge-gpu.log` ou
  `/tmp/bridge-cpu.log` com o `model` correspondente.
  `curl /api/status` nas portas antes de rodar.

## Tuning aplicado (diálogo enxuto, tools intactas)
- `temperature`: agentes Plan 0.5–0.8, Build 0.5; bridges com default 0.4.
- System prompts dos agentes: fonte versionada em `AGENTS-PROMPTS.md` +
  seed em `Rochas.OpenCodeBridge.Web/Data/AgentSeeder.cs` (9B cita ctx
  moderado 160k; 4B cita ctx limitado 16k).
- `stop` **não** é enviado com tools (truncaria o JSON da tool call); a bridge só
  repassa `stop` se o cliente pedir explicitamente (`BuildChatRequest`).
- `presence_penalty`: Responses API não define; ignorado de propósito.

## Ordem de boot p/ testes
1. llama GPU `:4100` (9B Q4 128k, prio high) e `:4111` (4B Q3 96k, prio normal).
2. Bridges `:4124` → `:4100` e `:4125` → `:4111` (`--passthrough-responses`).
3. `curl /api/status` nas portas antes de rodar o agente.

## Segmentação obrigatória p/ modelos locais (2026-10-04)
- Runs longas multi-step degradam em loops. REGRA:
  fatiar toda tarefa em micro-runs (1 artefato + verificação cada),
  leituras limitadas (≤2 por arquivo, blocos grandes), respostas curtas.
- Subagentes do mesmo modelo SÓ onde existem (sessão interativa com Task);
  em `run --standalone` NÃO existem — a segmentação sou eu (runs curtas
  sequenciais, nunca 1 run gigante).
- Sem reescrita de arquivo gigante de uma vez; sem loop de fix infinito
  (máx 3 tentativas por artefato, depois humano assume).
- Históricos: do fim ao começo, blocos de 8k.

## Conteúdo longo + subagentes que só agem (2026-10-04)
- Thinking: GPU com thinking ligado (`--thinking events`); linha 4B/CPU
  conforme o cadastro do agente (`events` p/ Plan, `off` p/ execução direta).
- Conteúdo longo (>10k tokens) NÃO vai inteiro no prompt: fatiar em blocos
  com sobreposição e um índice (arquivo:início:fim) antes de delegar.
- Subagente = Qwen com viés executor: o 9B delega a subagentes DELE MESMO
  ou ao 4B da linha CPU
  instruídos a agir — zero conversa, só ação com tools: recebe
  slice + tarefa + critério, devolve artefato + evidência de execução.
  Sem relatório, sem resumo, sem pergunta de volta. Quem resume sou eu
  (ou o Qwen3 pai) no merge.
- Resposta de subagente sem evidência de execução = falha (repetir 1x,
  depois executar eu mesmo o slice).

## Enunciado rígido p/ subagentes (4B e Qwen-consigo-mesmo)
- Vale para o 4B **e** para subagentes do próprio 9B:
  1 tarefa por prompt, imperativo curto, exemplo de saída incluído,
  1 leitura + 1 escrita, verificação externa, temp 0.0.
