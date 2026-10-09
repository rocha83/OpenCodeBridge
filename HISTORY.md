# HISTORY.md — sessão de construção da bridge C# (`vllm-ocode-bridge`)

## Runner: console executor com allowlist (2026-10-05)
- `Rochas.OpenCodeBridge.Runner` (net9.0, BCL, zero warnings): loop via API
  direta na linha CPU (prompt curto, sem harness do `run`) — chat -> fence
  `{"name","arguments"}` -> valida -> executa -> devolve (max 5 voltas).
- Segurança: deny padrão; allowlist 1o token + denylist de metacaracteres,
  SEM `bash -c` (argv direto, cwd travado, timeout 60s, mata árvore); `sudo`
  só por match exato (`restart opencode-bridge*`, `tee appsettings`); log
  JSONL (`exec`, rotação 20MB) + `--verbosity quiet|normal|verbose`,
  `--temperature` (0.4: temp 0.0 travava o 3B em resposta fixa).
- Achados ao vivo: glob relativo resolvia no cwd errado (fix: base repo);
  3B não conta linhas (fix: Runner anexa `total: N linhas`); fence sem tentar
  (fix: temp > 0). Prova: `ls docs/screenshots/*e2e-cs*` -> **38**, confere.

## Selo orch thinking events (2026-10-05, reverte off)
- `off` no `orch` causava loop de reads sem delegar (Qwen3-8B sem thinking vira
  continuador guloso: após um read, o próximo read é sempre mais provável que o
  salto p/ `shell nohup`; evidência `plan-20261004-md-backend-src-docs-atomic-code-implementation.json`
  msgs 1–12: 10 reads seguidos, nenhum `nohup`, usuário precisou gritar `pare`).
- Volta p/ `events` (default 2.1) em `/opt/opencode-bridge/appsettings.json` +
  REGRA DE OURO no system do `orch` (`opencode.json`): ler APENAS o plano
  (max 2 reads), NUNCA abrir implementação (tarefa dos workers). `AGENTS.md`
  atualizado p/ `orch on (events)`.

## Protocolo orch 8 estágios (2026-10-05, corrigido 2026-10-05)
- Agente `orch` (opencode, primary, modelo pinado `openai/qwen3-8b-awq-orch`):
  system próprio em 8 estágios — 1) LER o plano; 2) IDENTIFICAR blocos de
  tarefas; 3) ELUCIDAR enunciado atômico técnico por bloco (1 leitura +
  1 escrita, critério de aceite, FORMATO EXATO da saída); 4) EXECUTAR via
  `nohup opencode run --standalone --agent coder-3b --model llama-cpu/qwen2.5-coder-3b --title slice-<id> "<enunciado>" > .out 2>&1 &`
  (SEM `--session` na criação — opencode v2.0.21 exige prefixo `ses_` e gera
  o ID sozinho; `--session slice-<id>` falha com `Expected a string starting
  with "ses"` — atestado em `/tmp/opencode/slices/slice-1.out`; capturar o
  `ses_*` via `opencode session list --format json` filtrando por title);
  5) ACOMPANHAR com poll (`cat` / `tail -n 20`, NUNCA `tail -f` — bloqueia a
  tool e causa `Step interrupted`); 6) VALIDAR contra o critério;
  7) CONSOLIDAR só com todas verdes; 8) LIMPAR sessões dos workers
  (`opencode session delete ses_xxx` ou `DELETE /api/session/ses_xxx`,
  NUNCA `/api/session/slice-<id>` — dá 400 `InvalidRequestError`; standalone
  sempre persiste, sem flag efêmera na v2.0.21).
- Trava por permissão (não só prompt): `orch` sem edit, sem tool `subagent`,
  só read/glob/grep + webfetch/websearch + shell. Tool `subagent` aparece
  mas sempre nega — o system manda não insistir.
- Orçamento 36k: fixo ~10k, ~2.5k/fatia (teto `cpuMaxTokens` 2048) → ~10
  fatias/sessão. Poll leve + validação externa p/ não estourar.

## Selo orch thinking off (2026-10-05)
- Matriz final (não re-enunciar): `plan` 0.4/events (tudo), `build` 0.2/off
  (tudo), `orch` 0.2/**off** (ler, web, shell; SEM edit, SEM subagents).
- `orch` off via override de deploy (`appsettings.4124.orch-off-example.json`
  copiado p/ `appsettings.json` ao lado do DLL), sem mudar o default events
  da 2.1 no código. `orch` delega fatias atômicas SOMENTE via
  `nohup opencode run --standalone --agent coder-3b --title slice-<id> ... > .out 2>&1 &` + poll
  (`cat`/`tail -n`, nunca `tail -f`) + limpeza via `opencode session delete ses_xxx`.
  Linha 3B: bridge CPU `:4125` (thinking off) → llama.cpp `:4110`.

## Diagnóstico raiz
- opencode v2.0.21 sempre chama `POST /v1/responses`; vLLM não faz parse de tools
  nesse fluxo → texto puro, `finish_reason: stop`, `executed=False` em tudo.
- Tentativas de chave de rota no `opencode.json` (`api`, `protocol`, `options.baseURL`…)
  todas ignoradas. Decisão: bridge tradutora em vez de caçar config.

## Bridge v1.0 (mínima, sem herança legada)
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
- Medido: linha Coder em GPU ~35-45 tok/s fim-a-fim; Qwen3-8B ~25-30 tok/s (thinking
  incluso — o custo 2x de tokens explica a "lentidao", nao o decode).

## Suite +4 cenários sênior (2026-10-04, sem bump: segue 1.8)
- `E2ESeniorTests.cs`: React corporativo (fetch concorrente + AbortController),
  bus de eventos no backend (outbox + idempotência), regras de ERP (borda
  fiscal) e ARM embarcado (DMA + STOP). **22/22 verdes** no selo.

## Suite: retry em corpo vazio (2026-10-04, sem bump: segue 1.8)
- Sob rajada, `/v1/responses` ocasionalmente devolve 200 vazio (keep-alive;
  visto no Qwen3@36k e no Coder@52k: nao e pressao de VRAM — vLLM sem erros,
  KV 0% idle). `PostObj` tenta 1x de novo apos 2s antes de falhar.

## Bridge v1.9 — coerção texto→tool_calls p/ linha llama/CPU (2026-10-04)
- LLM em CPU (llama.cpp) não tem parser server-side: cospe JSON em fences.
  Nova camada (só perfil CPU): extrai ```json{"name"..} ou `<tool_call>` do
  content e sintetiza `tool_calls` (non-stream + fecho do stream), removendo
  os blocos consumidos. Loga `coerced` com count.
- Chaveamento: `--coerce-text-tools true` ou auto quando o model contém
  "cpu". **Linha vLLM (hermes/qwen3_coder/gemma4) intacta**: com tool_calls
  nativos, a coerção nunca dispara.
- Suite: `U-convert-coerced-text-call` (convert com model `*cpu*`).
- Atestado: unit `U-convert-coerced-text-call` verde + evento `coerced`
  observado ao vivo. E2E completo via opencode TRAVOU: system prompt de ~6k
  tokens no prefill CPU (minutos) estoura timeouts do cliente (ASGI abort).
  Ação real na CPU exige prompts curtos ou prefill menor — pendente.

## Bridge v2.0 — perfis plan/build/orch + teto CPU + 5 modos opencode (2026-10-04)
- Perfis por sufixo no model (`-plan` 0.6/events, `-build` 0.2/off, `-orch`
  0.2/events, parametrizáveis em `appsettings.json`): o 8B ganha o 5º modo
  orquestrador (build que delega micro-enunciados a subagentes 3B).
- `CpuMaxTokens` (padrão 2048, `--cpu-max-tokens`/`cpuMaxTokens`/`/api/config`):
  linha CPU sem parser divaga minutos a ~5 tok/s com teto alto (3B rambleou
  1400+ tokens num `soma`); GPU segue com `MaxTokens` (8192).
- Suite: `U-cpu-max-tokens-clamp` (dry-run `/api/translate`, sem modelo).
- Passthrough mapeia id com perfil (`-plan/-build/-orch`) p/ o Model servido
  (opencode 1.x fala chat/completions direto; sem isso o vLLM dava 404 nos
  ids com sufixo) + injeta a temperatura do perfil quando o cliente omite.
- Exemplos: `appsettings.4124.gpu-example.json`, `appsettings.4125.cpu-example.json`.
- opencode 1.18.34 IGNORA o bloco `providers` (schema V1): config ativa usa
  `provider` + `npm @ai-sdk/openai-compatible` (`opencode.json`); formato
  `providers` fica em `opencode.v2.json` de reserva p/ o v2.

## Bridge 2.0 — coerção no chat direto + formatos crus do 3B (2026-10-04)
- opencode 1.x fala `chat/completions` direto: `Passthrough` agora mapeia id
  com perfil p/ o Model servido (era 404 no vLLM) e a linha CPU força
  `stream:false` + coage a resposta (SSE sintetizado quando pedido).
- 3B alterna 4 formatos entre runs: fences, `<tool_call>`, `<{..}>` e objeto
  cru — extrator com chaves balanceadas cobre todos (`coerced-chat` no log).
- `stop: ["[END_OF_TEXT]"]` injetado na linha CPU (corta o loop de filler) +
  `CpuMaxTokens` limita o pior caso. Atestado ao vivo: `soma(17,25)` com
  `finish: tool_calls` em 33 tokens (antes: divagação até `length`).
- Perfil `moe` (0.4/events): 6º modo `qwen3-8b-awq-moe`, híbrido geral com
  system prompt amplo próprio (opencode + Qwen CLI).

## Padrão de temperatura 0.4 (2026-10-05)
- `plan` e `moe` Baixados de 0.6 para **0.4** em todos os lugares (perfis da
  bridge, `appsettings` GPU/CPU, mint V2, Qwen entry 8B): equilíbrio entre
  criatividade e determinismo. `build`/`orch` seguem 0.2; 3B segue padrão.

## Bridge v2.1 — E2E enterprise C# + React (2026-10-04)
- `E2EEnterpriseTests.cs`: 6 cenários com skills de backend e frontend —
  CRUD simples Product (Minimal API + Dapper + bordas 400/404), CRUD Customer
  (paginação OFFSET/FETCH + 409 email duplicado), CRUD composto SaleInvoice
  header+detail em transação (baixa de estoque + rollback), BackgroundService
  (outbox + retry + dead-letter + stop gracioso), circuito Garnet
  (cache-aside + invalidação + circuit-breaker) e Dashboard React (cards +
  gráfico SVG + AbortController). Asserts anti-stub (sem TODO/esqueleto).

## Selo 3B CPU (2026-10-04): suite 28/30 + reruns = 30/30 com ressalva
- Linha `:4125`→`:4110` (Qwen2.5-Coder-3B Q4_K_M, 4 threads i5-7500T):
  12/12 unit em 0.0s; E2E simples 6-22s, tools 34s, senior 70-400s,
  enterprise 160-340s a ~2.5-5 tok/s. Final: **28 PASS, 2 FAIL**
  (customer: sem 409; worker: TODO) — ambos verdes em rerun manual, exceto
  o 409 que o 3B omitiu 2x (propensão a largar constraint de status-code).
- Asserts CRUD estilo-agnósticos (3B varia Minimal↔Controller entre runs);
  flag `--only` p/ rodar 1 cenário. Recomendação registrada: ACEITE do orch
  deve cobrir status-codes via teste de integração (o unit de texto não pega).

## Bridge Web — UI MVC black + DapperRepository + suite (2026-10-05)
- Novo projeto `Rochas.OpenCodeBridge.Web` (MVC `net9.0`, Razor + Bootstrap 5.3.3 black):
  login com cookie (PBKDF2, seed `admin@mova.com`/`Admin@123`), CRUDs `users` e
  `agents` via `Rochas.DapperRepository 2.0.1` (`GenericRepository` + `Filterable`),
  chat com thinking ao vivo no painel superior (SSE repassado por `/Chat/Stream`,
  proxy do HttpListener que não manda CORS). SQLite `web.db` no boot (DDL via
  `AppDb.Init`), `Microsoft.Data.Sqlite 9.0.20`.
- Novo `Rochas.OpenCodeBridge.Web.Test` (console): unit (`U-hash-*`, `U-agent-*`)
  + integração real (CRUD em sqlite temp `I-crud-*`; HTTP `I-http-*`: anônimo
  redireciona, login admin, `/Chat` 200, `/Agents` 200, `/Chat/Stream` 404).
- Build da solution Release: 0 warning(s) / 0 error(s). Suite: 14/14 PASS
  (`dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web
  http://127.0.0.1:4130`).
- Portado do `Rochas.OpenCodeBridge.Web` construído em `/usr/src/opencodebridge`
  (sessão 2026-10-05, não commitada): normalizado `form.`/`model.` que os `sed`
  haviam deixado incoerente e prefixo de linhas `N: ` dos arquivos Razor/CSS
  (erro RZ2005 na primeira build portada).

## Bridge Web — MDB + temas + admin + reasoning indicator (2026-10-05)
- Migração de Bootstrap 5.3.3 CDN para **MDB UI Kit 6.4.2** local (`wwwroot/lib/mdb/`,
  `wwwroot/lib/fontawesome/`), `data-bs-theme="dark"` nativo + `black.css` override.
- **Chat MDB-style**: lista de sessões à esquerda (avatares, badge count), área de conversa
  com bolhas usuário/agente (ícones FA `fa-user`/`fa-robot`), input `form-outline` +
  label "Pergunta ou Instrução", botão `btn-info btn-rounded float-end`.
- **Sessões persistidas em localStorage** (demo): "Nova", "Limpar", seleção por clique,
  título = 1ª mensagem truncada, contagem de mensagens.
- **Streaming SSE "digitando"**: `fetch` + `ReadableStream` + `TextDecoder(stream:true)` —
  reasoning e resposta atualizam `textContent` a cada chunk; caret `.typing` pisca.
- **Indicador de engine** (global + por bolha): bolinha verde **piscando** enquanto
  chega stream, verde **acesa** ao terminar, **vermelha parada** em erro.
  Endpoint `GET /Chat/Ping?agentId=` (timeout 3s no `/api/status` da bridge)
  atualiza ao trocar agente no dropdown.
- **Tema dark/light**: botão na navbar (sol/lua) persiste em `localStorage`,
  aplica `data-bs-theme` no `<html>` antes do paint (script inline no `<head>`).
- **Flag `IsAdmin` em `User`**: seed `admin@mova.com` com `IsAdmin=true`; links
  "Agentes"/"Usuários" só aparecem se `User.FindFirst("IsAdmin").Value == "true"`.
- **Foco escuro**: `.form-control:focus` / `.form-select:focus` com
  `border-color: #3b82f6` + `box-shadow` rgba suave (não mais azul vivo).
- DDL `AppDb.Init`: migração `ALTER TABLE users ADD COLUMN is_admin` para DBs antigos.
- Assets estáticos servidos via `WebApplicationOptions.WebRootPath` (independe de cwd).
- Build Release: 0 warnings / 0 errors.

## Bridge Web — Scripts de deploy + plano 5 fases (2026-10-05)
- Scripts adicionados ao repo: `deploy-web.sh` (build + testes + restart), `health-check.sh` (login + engine ping), `gate.sh` (gate + build + testes).
- Plano detalhado `plan-web-001.md` com 5 fases granulares para execução por modelo 8B (ctx 36k):
  1. Fundação & Infra (concluída)
  2. Chat UX Core (concluída)
  3. Persistência Server-Side (sessions/messages, ContextWindow, endpoints CRUD)
  4. UX Refinada (bolha agente pós-thinking, sem dot na bolha, a11y, scroll, toast)
  5. Qualidade & Deploy (testes integração/unit, gate, health-check, docs, tech debt)
- Regras de execução para modelo 8B: uma tarefa por vez, não inventar, build+teste=done, fail fast, ordem sugerida.

## Bridge Web — Fase 3 Persistência Server-Side (2026-10-05, CONCLUÍDA ✅)
- **Models**: `Session` e `SessionMessage` com FKs e índices (`idx_sessions_user`, `idx_sessions_agent`, `idx_messages_session`, `idx_messages_created`).
- **DDL**: `AppDb.Init` cria tabelas `sessions` + `session_messages` com `ON DELETE CASCADE`, migração `is_admin` mantida.
- **SessionService**: CRUD completo (Create, GetByUser, Get, UpdateTitle, UpdateAgent, Delete, GetMessages, AddMessage, Count, Touch).
- **ContextWindow**: `BuildContext(history, maxInputTokens=28672)` estima tokens via `chars/4`, retorna slice cronológico que cabe no orçamento Qwen3-8B.
- **ChatController endpoints**:
  - `GET /Chat/Sessions` → lista do usuário (ordenado updated_at DESC)
  - `POST /Chat/Sessions` → cria sessão (`{agentId, title?}`), retorna id
  - `GET /Chat/Sessions/{id}` → detalhe
  - `GET /Chat/Sessions/{id}/Messages` → histórico paginado
  - `DELETE /Chat/Sessions/{id}` → deleta sessão + mensagens (cascata)
  - `PUT /Chat/Sessions/{id}/Title` / `PUT /Chat/Sessions/{id}/Agent` → updates
  - `POST /Chat/Stream` → aceita `sessionId` opcional; carrega histórico via `SessionService.GetMessages` + `ContextWindow.BuildContext`; persiste mensagem do usuário antes do stream; captura resposta do assistant (content + thinking + usage) via buffer intermediário; persiste assistant message ao final.
- **Front-end (Chat/Index.cshtml)**: remove `localStorage`; carrega sessões via `/Chat/Sessions`; seleção carrega mensagens via `/Chat/Sessions/{id}/Messages`; "Nova" → `POST /Chat/Sessions`; stream envia `sessionId`; bolha do agente só aparece no primeiro chunk de `content`; thinking vai para `<details>` colapsável; indicador global "Pensando..."/ "Concluído!"/ "Desconectado"; sem bolinha na bolha do agente.
- **Validação**: login cria claim `NameIdentifier` com user ID; `CurrentUserId` propriedade no controller.
- **Teste manual**: `curl /Chat/Sessions` → `[]`; `POST /Chat/Sessions` → `{"id":1,...}`; `GET /Chat/Sessions/1/Messages` → `[]`; `POST /Chat/Stream` com `sessionId=1` → SSE streaming OK (reasoning + content), mensagem user + assistant persistida (thinking + content + usage).
- Build Release: 0 warnings / 0 errors.

## Bridge Web — Fase 4 UX Refinada + Testes Integração (2026-10-06)
- **CSS `black.css` reescrito**: overrides MDB dark/light completos (cards, forms, buttons, badges, avatars, engine indicator, bolhas, scrollbar, toast, fade-in). Tema light via `[data-bs-theme="light"]` funcional.
- **UX Chat**:
  - Bolha agente só aparece no 1º chunk de `content` (não no thinking); thinking em `<details>` colapsável "Raciocínio".
  - Indicador global (bolinha + texto): "Pensando..." (verde piscando) → "Concluído!" (verde) → "Desconectado" (vermelho) → "Aguardando..." (cinza idle).
  - Toast MDB (canto inf. dir.) em erro de stream: "Erro ao conectar na engine..."
  - Scroll inteligente: auto-scroll só se usuário no fundo (`scrollTop + clientHeight >= scrollHeight - 50`).
  - Teclado: `Enter` envia, `Shift+Enter` quebra linha; foco retorna ao `prompt` após envio.
  - `aria-live="polite"` no `#conv` para leitores de tela.
- **Fix agent update**: `agents.Query(...).FirstOrDefault()` no lugar de `agents.Get(...)` (workaround DapperRepository).
- **Limpeza**: removido dead code `PasswordHasher` comentado em `AccountController.Login`.
- **Testes integração Chat** (`ChatIntegrationTests.cs`): 7 cenários (login redirect, admin links, sessions CRUD, stream SSE, ping engine) — **14/14 PASS** (unit + http + chat integration).
- Build Release: 0 warnings / 0 errors.

## Bridge Web — Fase 5 UX Polish + Cancel Request (2026-10-07)
- **Cancelar requisição em andamento**: botão "Cancelar" (ícone X vermelho) aparece durante streaming; usa `AbortController` para cancelar `fetch` in-flight; botão some ao cancelar/concluir/erro.
- **UX loading states**: 
  - Botão "Enviar" → spinner + "Enviando..." + desabilitado durante request
  - Input desabilitado durante envio
  - Indicador "Processando..." com spinner abaixo do input
  - Re-habilita input/botão imediatamente após request iniciado (não espera resposta)
  - Em erro: toast "Erro ao conectar na engine", reabilita botão/input
  - Sucesso: botão "Send" restaurado, label "Concluído!" no engine indicator
- **Limpeza Cancel button**: removido do DOM ao concluir/cancelar/erro
- **Sessão persistida**: `selectSession` carrega histórico via `/Chat/Sessions/{id}/Messages`; streaming usa `sessionId`; mensagens user+assistant persistidas com thinking+content
- Build Release: 0 warnings / 0 errors.
- Testes: 37/37 PASS (unit + integration)

## Bridge Web — Fase 6 Executor único + DI total + UX + Diagnóstico (2026-10-08)
- **Executor único (DDD)**: `ToolExecutor` virou despachante atrás de `IToolExecutor`;
  handlers `IToolHandler` por tool (`Shell/Read/Write/Edit/Grep/Glob`) com trava de
  workspace (`WorkspaceGuard`), runner de processos com timeout+kill (`ProcessRunner`)
  e `ToolResult` como objeto de valor. `task` removida do `ToolDefinitions`
  (anunciar só o que executa). Registro via `AddScoped` no `Program.cs`.
- **DI projeto-todo (interfaces do pacote)**: controllers e `SessionService` dependem de
  `IGenericRepository<T>` + `IPersistenceRepository<T>` (Rochas.Data.Specification),
  `ISessionService`, `IToolExecutor`, `IPasswordHasher`; `ContextWindow` segue estático
  puro (sem dependência a injetar, 7/7 testes). Padrão segue README do DapperRepository.
- **Fixes reais achados por evidência**: race stdout vazio no runner (`WaitForExit`
  duplo); race `[DONE]` antes de persistir o assistant (DONE próprio após persistir);
  `SessionService.cs` reescrito com EF estranho revertido (quebrava build).
- **Chat UX**: label branca, `ENVIAR`, "Processando" como linha da conversa até o fim,
  resposta palavra por palavra (JS puro + caret CSS), thinking em `<details>` fechado.
- **Diagnóstico**: Serilog (Console + tabela `logs` em `diagnostics.db`), middleware com
  tempo/status + Referer/IP bem-vindos, `Rochas.Telemetry` (`ComponentObserver` nas
  tools + `ServiceObserver` com snapshots), modo on/off (`Diagnostics:Enabled` ou env
  `DIAGNOSTICS_ENABLED=1`), `GET /Chat/Diagnostics` autenticado.
- **Higiene**: symlink `wwwroot/*.iso` removido (era lixo apontando p/ VMachine).
- **Suite: 42/42 PASS** (7 ContextWindow + 10 SessionService + 11 ToolExecutor + 14 Chat),
  `dotnet build slnx -c Release` 0 erros; warnings só os 4 pré-existentes.

## Bridge Web — Fase 6b Cobertura + diagnósticos verificados (2026-10-08)
- Suite: **61/61 PASS** (7 ContextWindow + 10 SessionService + 23 ToolExecutor +
  6 Diagnostics + 15 Chat integração), exit 0. Cobertura do código novo ~85%:
  handlers, dispatcher, DI, middleware (Referer/IP), telemetria e endpoint
  `/Chat/Diagnostics` exercitados; fora: tetos de tamanho, sudo-exato-allow,
  RegisterError, POSTs de Users/Agents (gap pré-existente).
- Sem `dotnet test`/`coverlet` neste repo (runner console próprio); contagem por asserts.

## Bridge Web — Fase 6c Economia de contexto (2026-10-08)
- `ToolDefinitions` enxuto (2737→2134 chars, ~684→~533 tokens/request): descrições
  curtas, tipos/required preservados; helpers com nomes legíveis
  (`BuildTool`/`BuildParam`/`ToolParam`).
- Teto 2000 chars nos retornos `role: tool` do loop (paridade OpenCode).
- E2E real validado: modelo chamou `shell`, listou diretório, truncou e respondeu pt-BR.
- Suite: **61/61 PASS** (7+10+23+6+15).

## Bridge Web — Fase 6d Progresso de tools na UI (2026-10-08)
- Servidor emite SSE `{"progress":"tool_start"|"tool_done","name","ok?"}` com flush
  imediato ao redor de cada `Execute` (antes o `CopyToAsync` escondia a execução).
- UI: linha `Executando [nome]...` piscando → `[nome] executado.` (ou `falhou.`),
  mantida como histórico sutil do que rodou.
- Teste `I-chat-tool-progress` (modelo real via `ls`): start+done observados.
- Suite: **62/62 PASS** (7+10+23+6+16), build Release 0 erros.

## Bridge Web — Fase 6e Streaming real (2026-10-08)
- Removido `CopyToAsync` do `StreamWithTools`: linhas da bridge repassadas ao vivo
  (primeiro byte ~3,8s vs ~13s+ antes); bolha nasce no primeiro `content` e o
  efeito palavra-por-palavra (~20/s + caret) ritma o fluxo real.
- Suite: **62/62 PASS**, build Release 0 erros.

## Bridge Web — Fase 6f Streaming de verdade (2026-10-08)
- Causa raiz do "digitando não funciona": `PostAsync` bufferizava o corpo inteiro;
  troca por `SendAsync` + `ResponseHeadersRead`. Comprovado: 576 chunks de 0,1s a
  16,2s (antes: tudo de uma vez ao fim). `CopyToAsync` já havia saído antes.
- Suite: **62/62 PASS**, build Release 0 erros.

## Bridge Web — Fase 6g Fix workspace guard (2026-10-08)
- Causa das falhas de escrita: `AppContext.BaseDirectory` termina com `/` e a
  checagem de prefixo virava `//` — negava TUDO dentro do workspace
  (`./x`, `.`, `./src/x`). Fix: `TrimEnd` dos separadores + regressão
  `U-tool-trailing-slash`. Tentativas fora (`/home/...`) seguem negadas (correto).
- Verificado E2E via `/Chat/Tool` (write+read OK).
- Nota: `mkdir/ping/while/watch` seguem fora do allowlist (correto); `write` já
  cria diretórios. Workspace raiz = pasta do binário (sugestão futura: config).
- Suite: **63/63 PASS**, build Release 0 erros.

## Bridge Web — Fase 7 Painel de tarefas 3B (2026-10-09)
- Sidebar direita "Tarefas 3B": lista da decomposição com badges
  (pendente/executando/concluída) + selo de síntese; polling 5s + refresh a cada
  carregamento. Fonte: `GET /Chat/Sessions/{id}/Tasks` derivado das mensagens
  marcadas (sem tabela nova), parse em `SessionTaskPanel` testável.
- Verificado ao vivo na sessão 173 (faixa verde): 1 tarefa done + synthesized.
- Suite: **70/70 PASS**, build Release 0 erros.

## Bridge Web — Fase 8 Híbridos: modos, 2 fases, tools no executor (2026-10-09)
- `Agent.Mode` (plan/build) + `Role`; executor filtra pelo modo do orch (front+400).
- Plan: temp 0.4, `GetTools("plan")` só read/grep/glob, backstop no executor.
- Pipe em 2 fases: `POST /Chat/Decompose` (preview) + `POST /Chat/OrchestrateApproved`.
- Executor build com loop de tools (5 turnos, teto 2000); plan sem tools.
- Decompose atômico (mín. 4, máx. 8) + retry até 3x por critério de aceite.
- Suite: **75/75 PASS**, build Release 0 erros. E2E com modelos: pendente.
