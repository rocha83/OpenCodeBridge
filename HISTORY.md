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

## E2E e portão (concluído)
- Ciclo arquivos/build/git/docker/curl-UA; demo DDD `PcssDemo` em `work/pcss-demo`.
- Achado honesto: tools executam (provado via git+build), mas o 8B **não se autocorrige**
  (regressões, path errado, `bin/obj` commitado) → integridade via verificação externa.
- **Gate integrado no mesmo projeto** como helper interno (`Gate.cs`, subcomando
  `Rochas.OpenCodeBridge.dll gate ...`): commit novo, forbid paths, mustContain,
  build; modo `--review`: commit local livre, **push (=baseline) só com aprovação**,
  rejeição faz `reset --soft`. Projeto separado `vllm-ocode-gate` descontinuado.
- Gate já capturou regressão real (FAIL determinístico em `node_modules/.bin` que o
  agente criou fora da tarefa — repo resetado para `6961acf`).
- `/api/metrics` ✅ (acumuladores + tps_out/tps_total; gate imprime METRICS).

## Pendente (backlog não bloqueante)
- Frentes docker (falha graciosa sem daemon), `curl` UA Firefox Win11, zip deliverable.

## Bridge v1.4 — pipe paralelo p/ Qwen2.5-Coder (2026-10-03)
- Problema: com `tool_choice "auto"` o Coder improvisa o call em texto
  (bloco ```json `{"name":...}` ou XML `<response><function_call>`) e nada
  executa; com `"required"` emite `tool_calls` nativo (atestado no vLLM).
- Mudança mínima em `Program.cs`: `IsCoder` (Model contém "coder") +
  `tool_choice = required` só nesse perfil; caminho Qwen3 byte-idêntico.
- Suite `Rochas.OpenCodeBridge.Test` virou agnóstica ao perfil: lê o modelo
  do `/api/status`; `E-vllm-tool-hermes` só no Qwen3; asserts de reasoning
  e de texto-do-review condicionados ao perfil (Coder é non-thinking e com
  `required` responde com call no 1º turno).
- Atestado: suite **10/10** no Coder (vLLM 0.19.1 + `qwen3_coder`) e **11/11**
  no Qwen3 selado; tarefa opencode E2E (calc.py criado+executado) PASS nos dois.
- Nota cognitiva: Coder cumpre mas é verborrágico (reescreve reports em loop);
  Qwen3 termina limpo. Coder de plantão só com `--model *coder*`.

## Bridge v1.5 — thinking=off trafega como texto (2026-10-03)
- Problema: com `--thinking off`, o Qwen3 emite tudo dentro do thinking e
  a bridge descartava → resposta vazia. Agora `off` = sem item reasoning,
  mas o texto do raciocínio é fundido no início da message (não-stream) ou
  desviado para o stream de texto (SSE). Caminho Qwen3+events byte-idêntico.
- Parâmetro: `--thinking off|events` (CLI) e `/api/config` (runtime).
  Atestado: bateria comparativa 0.2 via `:4125` com `off` → message não-vazia.

## Suite de testes (Rochas.OpenCodeBridge.Test)
- Console .NET 9 sem NuGet (BCL), na solution: 6 unit via HTTP na bridge
  (`/api/status|metrics`, `/v1/models`, `/api/translate` tools+namespace+stop,
  `/api/convert` thinking+tools) + 5 e2e no modelo vivo (hermes forcado,
  responses simples/com tools, stream SSE com sequence monotônico, review de
  arquiteto senior). Exit 0 PASS / 1 FAIL.
- Rodar: `dotnet run -c Release --project Rochas.OpenCodeBridge.Test -- [--skip-e2e]`
- 2026-10-03: suite segmentada 1 classe/arquivo (TestContext + Program orquestrador +
  UnitStatus/Translate/Convert + E2ETool/Stream/Architecture/Mitigation, 9 arquivos);
  17 cenários (8 unit + 9 e2e: multi-namespace IoT, args inválidos, ciclo IoT→fila→push,
  erro 4xx honesto, concorrência 3x); métodos en-US, comentários pt-BR. **17/17 verdes**.

## Bridge v1.6 — teto de saída 4k (2026-10-04)
- `MaxTokens` padrão 8192→4096 (padrão de fábrica sem thinking); honra pedido
  menor (`max_tokens`/`max_output_tokens`), nunca maior que o teto. Corta
  loops verborrágicos e metade da latência pior-caso; truncamento real o
  opencode contorna com follow-up (`finish_reason=length`).
- Ajuste remoto já existia: `POST /api/config {"max_tokens": N}`.

## Bridge v1.7 — perfis plan/build + appsettings.json (2026-10-04)
- Sufixo no model (`...-plan`, `...-build`): plan = temp 0.6 + thinking;
  build = temp 0.2 + thinking fundido. Temperatura explicita no request
  vence o perfil; sem sufixo valem os globais (Qwen3 padrao inalterado).
- `appsettings.json` opcional ao lado do DLL (listen/port/upstream/model/
  thinking/temperature/maxTokens/logPath/profiles); CLI vence o arquivo;
  `/api/config` continua ajustando em runtime.
- Uso: opencode com entradas `openai/qwen3-8b-awq-plan` (planejar) e
  `...-build` (executar). Atestado via `/api/translate` (suite U nova).

## Bridge v1.8 — tps em todo ciclo (2026-10-04)
- Log da bridge: cada `/v1/responses` registra `input/output_tokens` + `tps_output`.
- Suite: `PostObj` imprime `[tps] N tok / Ts = X tok/s <- url` em todo POST
  com `usage` (unit translate sem modelo nao tem usage: silencioso).
- Medido: Coder-7B ~35-45 tok/s fim-a-fim; Qwen3-8B ~25-30 tok/s (thinking
  incluso — o custo 2x de tokens explica a "lentidao", nao o decode).

## Suite +4 cenários sênior (2026-10-04, sem bump: segue 1.8)
- `E2ESeniorTests.cs`: React corporativo (fetch concorrente + AbortController),
  bus de eventos no backend (outbox + idempotência), regras de ERP (borda
  fiscal) e ARM embarcado (DMA + STOP). **22/22 verdes** no selo.
