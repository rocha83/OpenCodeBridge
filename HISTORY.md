# HISTORY.md — sessão de construção da bridge C# (`vllm-ocode-bridge`)

## Diagnóstico raiz
- opencode v2.0.21 sempre chama `POST /v1/responses`; vLLM não faz parse de tools
  nesse fluxo → texto puro, `finish_reason: stop`, `executed=False` em tudo.
- Tentativas de chave de rota no `opencode.json` (`api`, `protocol`, `options.baseURL`…)
  todas ignoradas. Decisão: bridge tradutora em vez de caçar config.

## Bridge v1.0 (mínima, sem herança do Qwen2.5-7B)
- Console .NET 9, sem NuGet (só `HttpListener` + `System.Text.Json`).
- Tradução pura Responses ⇄ chat/completions + thinking + tool calling.
- Fora de escopo de propósito: coercion, grounding, pseudo-call, nudge, footer.

## Bugs reais encontrados e corrigidos
1. **Model 404**: opencode manda `openai/qwen3-8b-awq`; upstream força `Model` configurado.
2. **Thinking**: formato real é `<think>`, não `U+6EA6` → `SplitThinking` reescrito.
3. **Temperature**: ignorava a do request + `Parse` quebrava em pt-BR → `req > config`, `InvariantCulture`.
4. **`JsonNode` pai duplo** (`already has a parent`, 4 pontos: `parameters`, `baseObj`,
   `item` do output, `function_call` added/done) → `CloneOrDefault` + cópia de trabalho.
5. **BOM** do `StreamWriter` corrompia o 1º evento SSE → `UTF8Encoding(false)`.
6. **Chunk final** com `choices: []` estourava índice no stream.

## Padrões e API
- Convenção: **código en-US, comentários pt-BR** (reescrita total v1.1).
- `--listen` (remoto `0.0.0.0`), `/api/status|translate|convert|convert|config`.
- Compat saída nova+antiga: `/v1/responses` (traduz) e `/v1/chat|completions`, `/v1/models` (repasse).
- Swagger black autocontido `/swagger` (sem CDN) + `qwen3-bridge.xml` (24 membros);
  spec regenerado via script após brace imbalance no manual.
- Streaming evoluiu: sintetizado → **real progressivo** (chunked, deltas vivos,
  `sequence_number` crescente, `completed` com ids consistentes).

## Parsers (bake-off, bateria T1/T2/T3 direto no vLLM)
- `hermes` **3/3** ✅ — modelo emite `<tool_call>{json}</tool_call>`, hermes extrai.
- `qwen3_coder` **0/3** ❌ (espera `<function=><parameter=>`).
- `qwen3_xml` ❌ (espera `<function name=>` da geração 2507).
- **hermes travado** como oficial. Bridge é agnóstica: consome `tool_calls` normalizado.

## Tuning e garantia do agente
- `temperature: 0.2` (opencode mint+root e default da bridge); system prompt + sufixo
  conciso (2 frases, sem saudação, código/tools completos).
- `max_tokens 8192` mantido (código precisa); `stop` só por passthrough explícito
  (com tools trunca o JSON — documentado); `presence_penalty` fora da Responses API.
- `AGENTS.md`: provider único (bridge), pinagem `--agent build --model openai/qwen3-8b-awq`,
  verificação via `/tmp/qwen3-bridge.log`.

## E2E e portão (em andamento)
- Ciclo arquivos/build/git/docker/curl-UA; demo DDD `PcssDemo` em `work/pcss-demo`.
- Achado honesto: tools executam (provado via git+build), mas o 8B **não se autocorrige**
  (regressões, path errado, `bin/obj` commitado) → integridade via verificação externa.
- `vllm-ocode-gate` (C#, genérico via `gate.json`): commit novo, forbid paths, mustContain,
  build; modo `--review`: commit local livre, **push (=baseline) só com aprovação**,
  rejeição faz `reset --soft`. Escopo Python descartado (`/tmp` só).
- Gate já capturou regressão real (FAIL determinístico em `node_modules/.bin` que o
  agente criou fora da tarefa — repo resetado para `6961acf`).

## Pendente
- `/api/metrics` ✅ (acumuladores + tps_out/tps_total; gate imprime METRICS).
- Frentes docker (falha graciosa sem daemon), `curl` UA Firefox Win11, zip deliverable.
