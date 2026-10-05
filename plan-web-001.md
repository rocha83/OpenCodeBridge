# Plano de Continuidade — OpenCodeBridge.Web (5 Fases)

> Gerado em 2026-10-05. Destinado a execução por modelo 8B (ctx 36k) via micro-enunciados atômicos.

---

## Fase 1 — Fundação & Infra (JÁ CONCLUÍDA ✅)

| Item | Status |
|------|--------|
| MDB UI Kit 6.4.2 local (`wwwroot/lib/mdb/`, `wwwroot/lib/fontawesome/`) | ✅ |
| Tema `data-bs-theme="dark"` nativo + `black.css` override | ✅ |
| Botão dark/light na navbar (persiste em `localStorage`) | ✅ |
| Flag `User.IsAdmin` + seed `admin@mova.com` = true | ✅ |
| Links "Agentes"/"Usuários" só para admin | ✅ |
| Webroot fixo via `WebApplicationOptions.WebRootPath` | ✅ |
| Foco escuro: `.form-control:focus` com `#3b82f6` + shadow suave | ✅ |
| Build Release: 0 warnings / 0 errors | ✅ |

---

## Fase 2 — Chat UX Core (JÁ CONCLUÍDA ✅)

| Item | Status |
|------|--------|
| Layout MDB: sessões à esquerda, bolhas user/agent à direita | ✅ |
| Sessões em `localStorage` (demo): Nova, Limpar, seleção, título, count | ✅ |
| Streaming SSE "digitando" via `ReadableStream` + `TextDecoder(stream:true)` | ✅ |
| Caret `.typing` pisca durante streaming | ✅ |
| Engine indicator global (bolinha + texto): Pensando.../Concluído!/Desconectado | ✅ |
| Endpoint `GET /Chat/Ping?agentId=` (timeout 3s) | ✅ |
| Ícones FA `fa-user`/`fa-robot` nas bolhas | ✅ |
| Input `form-outline` + label "Pergunta ou Instrução" | ✅ |

---

## Fase 3 — Persistência Server-Side das Sessões e Mensagens (Backend)

### 3.1 Schema e Migração
- **Arquivos:** `Rochas.OpenCodeBridge.Web/Data/AppDb.cs`, `Rochas.OpenCodeBridge.Web/Models/Session.cs`, `Rochas.OpenCodeBridge.Web/Models/SessionMessage.cs`
- **Tabelas:**
  - `sessions` — `id INTEGER PK, user_id INTEGER FK, agent_id INTEGER FK, title TEXT, created_at DATETIME DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME`
  - `session_messages` — `id INTEGER PK, session_id INTEGER FK, role TEXT, content TEXT, thinking TEXT, prompt_tokens INTEGER, completion_tokens INTEGER, created_at DATETIME DEFAULT CURRENT_TIMESTAMP`
- **Migração:** `AppDb.Init` faz `CREATE TABLE IF NOT EXISTS` + `PRAGMA table_info` + `ALTER TABLE` para colunas novas.
- **Índices:** `CREATE INDEX IF NOT EXISTS idx_sessions_user ON sessions(user_id); CREATE INDEX IF NOT EXISTS idx_messages_session ON session_messages(session_id);`

### 3.2 Repositories e Services
- **Arquivo:** `Rochas.OpenCodeBridge.Web/Services/SessionService.cs` (novo), DI em `Program.cs`
- **Métodos:**
  - `Task<Session> CreateAsync(int userId, int agentId, string title)`
  - `Task<List<Session>> GetByUserAsync(int userId)`
  - `Task<Session?> GetAsync(int sessionId, int userId)` — valida ownership
  - `Task UpdateTitleAsync(int sessionId, string title)`
  - `Task DeleteAsync(int sessionId, int userId)`
  - `Task<List<SessionMessage>> GetMessagesAsync(int sessionId, int limit = 50)`
  - `Task AddMessageAsync(int sessionId, string role, string content, string thinking, int? promptTokens, int? completionTokens)`
  - `Task<int> CountMessagesAsync(int sessionId)`

### 3.3 Endpoints em `ChatController`
- `GET /Chat/Sessions` → lista sessões do usuário logado (ordenado `updated_at DESC`)
- `POST /Chat/Sessions` → cria nova (`{ agentId }`), retorna `sessionId`
- `GET /Chat/Sessions/{id}/Messages` → histórico paginado (últimos N)
- `DELETE /Chat/Sessions/{id}` → deleta sessão + mensagens (cascata)
- `PUT /Chat/Sessions/{id}/Title` → atualiza título
- `PUT /Chat/Sessions/{id}/Agent` → troca agente da sessão (opcional)

### 3.4 Ajuste no `Chat/Stream`
- **Input:** recebe `sessionId` (opcional). Se omitido, cria sessão nova.
- **Fluxo:**
  1. Carrega mensagens da sessão (`GetMessagesAsync` com limite de tokens — ver 3.5)
  2. Anexa mensagem do usuário (persiste antes do stream)
  3. Chama bridge com histórico montado
  4. Ao final do stream, persiste resposta do assistant (`role=assistant`, `thinking`, `prompt_tokens`, `completion_tokens` do `usage` se vier)
  5. Atualiza `sessions.updated_at`
- **Retorno:** SSE igual hoje, mas o front não acumula mais em `localStorage`.

### 3.5 Janela de Contexto por Tokens (não por contagem)
- **Arquivo:** `Rochas.OpenCodeBridge.Web/Services/ContextWindow.cs` (novo, stateless)
- **Lógica:**
  - Qwen3-8B: 36864 tokens totais. Reservar 8192 p/ saída → 28672 p/ entrada.
  - Estimar tokens: `prompt_tokens` real do `usage` (se bridge devolve) **OU** heurística: `chars / 4`.
  - `BuildContext(messages, maxInputTokens)` → itera do mais recente p/ mais antigo, soma tokens estimados, para quando estourar, retorna slice invertido (cronológico).
  - Guardar `prompt_tokens` estimado/real em `session_messages` p/ telemetria futura.

### 3.6 Front: Substituição do localStorage
- **Arquivo:** `Rochas.OpenCodeBridge.Web/Views/Chat/Index.cshtml`
- Remover todo bloco `localStorage` (`sessions`, `currentId`, `save()`, `renderSessions` baseado em array local).
- `renderSessions()` → `fetch('/Chat/Sessions')` + monta `<li>`.
- Clique na sessão → `fetch('/Chat/Sessions/{id}/Messages')` + `renderConv()`.
- "Nova" → `POST /Chat/Sessions` → seleciona retorno.
- "Limpar" → `DELETE /Chat/Sessions/{id}` para cada sessão listada (ou endpoint batch `DELETE /Chat/Sessions` com array de ids).
- `agentSel.onchange` → se houver sessão ativa, `PUT /Chat/Sessions/{id}` com novo `agentId` (opcional: cria nova sessão ao trocar agente).

---

## Fase 4 — UX Refinada do Chat (Frontend)

### 4.1 Bolha do Agente Só Aparece Após Thinking Finalizar
- **Comportamento atual:** A bolha do agente é criada no `send.onclick` com spinner + `thinking` vazio + `ans` vazio.
- **Novo comportamento:**
  - No `send.onclick`: cria **apenas** a bolha do usuário + indicação global "Pensando..." (já existe).
  - **Não** cria `li` do agente até o primeiro chunk de `content` (não `reasoning_content`).
  - Quando chega o **primeiro `content`**:
    - Cria o `li` do agente com `card-header` "Agente", `card-body` com `<p class="ans">` recebendo o texto.
    - O `thinking` (se houver) vai para um `<details><summary>Raciocínio</summary><p class="small text-muted">...</p></details>` **colapsável** dentro do `card-body`, **não** no fluxo principal.
  - Se o modelo **não emitir `reasoning_content`** (ex.: perfil `build` com thinking off), não renderiza o `<details>`.
- **Implementação:** Flag `agentBubbleCreated = false` no escopo do `send.onclick`. No loop de chunks:
  ```js
  if (d.reasoning_content) { think += d.reasoning_content; engineDot.className='dot blink'; engineTxt.textContent='Pensando...'; }
  else if (d.content) {
    if (!agentBubbleCreated) { createAgentBubble(); agentBubbleCreated = true; }
    full += d.content; anP.textContent = full;
  }
  ```

### 4.2 Indicador de Engine Apenas no Topo (Sem Bolinha na Bolha)
- **Remover:** `<span class="dot blink mt-1"></span>` do template da bolha do agente (linha que cria `thWrap`/`dot` no `send.onclick`).
- **Manter:** Indicador global abaixo do dropdown (`engineDot` + `engineTxt`) — já funciona.
- **Estados globais:**
  - `Pensando...` (verde piscando) — durante stream ativo
  - `Concluído!` (verde fixo) — stream terminou sem erro
  - `Desconectado` (vermelho fixo) — erro de rede / bridge offline
  - `Aguardando...` (cinza) — idle, sem stream

### 4.3 Acessibilidade e Teclado
- `textarea#prompt`: `Enter` envia, `Shift+Enter` quebra linha (garantir `keydown` handler).
- Foco volta para `prompt` após envio.
- `aria-live="polite"` no `#conv` para leitores de tela anunciarem novas mensagens.
- Contraste WCAG AA no tema light (verificar `black.css` com `[data-bs-theme="light"]` overrides se necessário).

### 4.4 Scroll Inteligente
- Auto-scroll **somente** se o usuário já estiver no fundo (`scrollTop + clientHeight >= scrollHeight - 50`).
- Se estiver lendo histórico acima, não rouba o foco — mostra toast sutil "Nova mensagem" (MDB `toast` component).

### 4.5 Toast de Erro de Stream
- Se `fetch('/Chat/Stream')` falhar (rede, 502, bridge offline):
  - Mostra `toast` MDB (canto inferior direito) com "Erro ao conectar na engine. Verifique se a bridge está online."
  - Não quebra a UI; usuário pode tentar novamente.

### 4.6 Limpeza Visual do `black.css`
- Remover regras legadas (`#thinking`, `#answer`, `#log` — não usadas no novo layout).
- Adicionar overrides para tema light:
  ```css
  [data-bs-theme="light"] { --bs-body-bg: #f8f9fa; --bs-body-color: #212529; }
  [data-bs-theme="light"] .topbar { background: #fff; border-bottom: 1px solid #dee2e6; }
  [data-bs-theme="light"] .card { background: #fff !important; border-color: #dee2e6 !important; color: #212529; }
  [data-bs-theme="light"] .form-control, [data-bs-theme="light"] .form-select { background: #fff !important; border-color: #ced4da !important; color: #212529 !important; }
  ```

---

## Fase 5 — Qualidade, Testes, Documentação e Deploy (End-to-End)

### 5.1 Testes de Integração (`Rochas.OpenCodeBridge.Web.Test`)
- **Arquivo:** `Rochas.OpenCodeBridge.Web.Test/Integration/ChatIntegrationTests.cs` (novo)
- **Cenários:**
  - `I-chat-login-redirect`: anônimo em `/Chat` → 302 `/Account/Login`
  - `I-chat-admin-sees-agents-users`: admin logado vê links na navbar
  - `I-chat-nonadmin-hides-agents-users`: usuário não-admin **não** vê links
  - `I-chat-sessions-crud`: cria sessão, lista, renomeia, deleta (via HTTP)
  - `I-chat-stream-sse`: POST `/Chat/Stream` com `sessionId` → SSE válido, `reasoning_content` + `content`, `usage` opcional
  - `I-chat-context-window`: insere 50 mensagens longas → verifica se `BuildContext` trunca corretamente (aprox. tokens)
  - `I-chat-ping-engine`: `GET /Chat/Ping?agentId=1` → `{ok:true}` quando bridge up; `{ok:false}` quando down
- **Execução:** `dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:4130`
- **Critério:** 100% PASS, exit code 0.

### 5.2 Testes Unitários de Lógica Pura
- **Arquivo:** `Rochas.OpenCodeBridge.Web.Test/Unit/ContextWindowTests.cs` (novo)
- **Casos:** `BuildContext` com tokens estimados, edge cases (mensagem única > limite, lista vazia, tokens exatos).
- **Arquivo:** `Rochas.OpenCodeBridge.Web.Test/Unit/SessionServiceTests.cs` (novo) — usa SQLite em memória (`:memory:`) p/ isolamento.

### 5.3 Gate de Integridade (Já Existe no Runner)
- **Comando:** `dotnet Rochas.OpenCodeBridge.dll gate --new-commit --must-contain "feat\|fix\|chore" --forbid-paths "bin\|obj\|*.log" --build`
- **Adicionar ao CI local:** script `gate.sh` que roda gate + testes + build Release.

### 5.4 Atualização do `HISTORY.md` e `AGENTS.md`
- **HISTORY.md:** Adicionar seções Fase 3, 4, 5 com same level of detail.
- **AGENTS.md:** Atualizar se houver novas convenções (ex.: `SessionService`, `ContextWindow`, padrões de `fetch` SSE, migrações SQLite).

### 5.5 Scripts de Deploy Local
- `deploy-web.sh`:
  ```bash
  #!/bin/bash
  set -euo pipefail
  cd /media/mint/3686C649614854E6/Projetos/Git/OpenCodeBridge
  dotnet build Rochas.OpenCodeBridge.Web -c Release
  dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:4130
  # Se testes passam, reinicia systemd/user service ou nohup
  ```
- `health-check.sh`: `curl -sf http://127.0.0.1:4130/Account/Login && curl -sf http://127.0.0.1:4130/Chat/Ping?agentId=1 | jq -e '.ok==true'`

### 5.6 Limpeza Técnica (Tech Debt)
- Remover `PasswordHasher` dead code se login livre ficar permanente (ou reativar com bcrypt/Argon2).
- `AccountController.Login`: remover bloco comentado TEMP.
- `ChatController.Stream`: validar `CancellationToken` propagation p/ bridge (já passa, confirmar).
- `BridgeClient.StreamAsync`: logar `usage` se bridge devolver (para `prompt_tokens`/`completion_tokens` real).

---

## Resumo das 5 Fases

| Fase | Foco | Entregável Principal |
|------|------|---------------------|
| **1** | Fundação & Infra | MDB local, tema dark/light, admin flag, webroot fixo, build verde |
| **2** | Chat UX Core | Sessões localStorage, streaming SSE digitando, engine indicator, bolhas MDB |
| **3** | Persistência Server | SQLite sessions/messages, ContextWindow por tokens, endpoints CRUD, front consome API |
| **4** | UX Refinada | Bolha agente só após thinking, sem dot na bolha, acessibilidade, scroll inteligente, toast erros |
| **5** | Qualidade & Deploy | Testes integração/unit, gate, health-check, docs, scripts deploy |

## Fase 3.7 — Integração Real com Bridge + Mock para Demo (Obrigatório)

### 3.7.1 Integração Real com Bridge (Produção)
- **Arquivo:** `Rochas.OpenCodeBridge.Web/Services/BridgeClient.cs` (já existe, ajustar)
- **Fluxo:** `Chat/Stream` recebe `sessionId` → monta histórico via `SessionService.GetMessagesAsync` + `ContextWindow.BuildContext` → chama `BridgeClient.StreamAsync(agent.BridgeUrl, agent.Model, agent.Temperature, agent.SystemPrompt, messages, Response.Body, ct)`
- **Parsing SSE:** bridge já devolve chunks `data: {...}` com `choices[0].delta.reasoning_content` e `choices[0].delta.content` + final `usage` (se houver).
- **Persistência:** ao final do stream, `SessionService.AddMessageAsync` com `prompt_tokens`/`completion_tokens` do `usage` se disponível.

### 3.7.2 Mock de Modelo para Demo (Desenvolvimento / CI)
- **Objetivo:** Permitir testar o chat **sem bridge real rodando** (CI, demo offline, dev sem GPU).
- **Arquivo:** `Rochas.OpenCodeBridge.Web/Services/MockBridgeClient.cs` (novo), implementa mesma interface que `BridgeClient` (`StreamAsync`).
- **Ativação:** Config `appsettings.json` → `"UseMockBridge": true` (ou env var `USE_MOCK_BRIDGE=1`). DI em `Program.cs` faz swap condicional.
- **Comportamento do Mock:**
  - Recebe `messages` (histórico) + `model` + `temperature` + `systemPrompt`.
  - Simula latência: `await Task.Delay(Random.Shared.Next(300, 800))` antes de iniciar stream.
  - Emite **thinking** (se modelo suporta) → chunks `reasoning_content` por ~1-2s (5-10 chunks).
  - Emite **resposta** → chunks `content` por ~2-4s (10-20 chunks).
  - Finaliza com `usage: { prompt_tokens: N, completion_tokens: M, total_tokens: N+M }`.
  - **Mensagens mock variadas** (rotacionadas ou baseadas no último `user` content):
    - Echo simples: `"Você disse: {lastUserMsg}. Isso é um mock."`
    - Código: `` ```csharp\n// Mock response\nConsole.WriteLine("Hello");\n``` ``
    - Reasoning simulado: `"Analisando a pergunta... identificando intenção... formulando resposta."`
  - **Flag `thinking`:** se `agent.Thinking == "off"` → **não** emite `reasoning_content`, só `content`.
  - **Erro simulado (opcional):** 1 em 20 calls → lança `HttpRequestException` para testar toast de erro.

### 3.7.3 Configuração e DI
- **appsettings.json:**
  ```json
  {
    "UseMockBridge": false,
    "MockBridge": {
      "MinThinkingChunks": 3,
      "MaxThinkingChunks": 8,
      "MinContentChunks": 8,
      "MaxContentChunks": 18,
      "ErrorRate": 0.05
    }
  }
  ```
- **Program.cs:** `builder.Services.AddScoped<IBridgeClient>(sp => config.GetValue<bool>("UseMockBridge") ? sp.GetRequiredService<MockBridgeClient>() : sp.GetRequiredService<BridgeClient>());`
- **Interface:** Extrair `IBridgeClient` de `BridgeClient` (método `StreamAsync` + props necessárias).

### 3.7.4 Testes de Integração com Mock
- **Arquivo:** `Rochas.OpenCodeBridge.Web.Test/Integration/ChatMockIntegrationTests.cs` (novo)
- **Cenários:**
  - `I-chat-mock-thinking-on`: agent com `thinking=events` → recebe `reasoning_content` + `content` + `usage`
  - `I-chat-mock-thinking-off`: agent com `thinking=off` → **não** recebe `reasoning_content`, só `content`
  - `I-chat-mock-error-rate`: 100 calls → ~5 erros simulados → toast aparece
  - `I-chat-mock-latency`: mede tempo total < 5s (não travado)

### 3.7.5 Aceite Fase 3.7
- `dotnet build -c Release` → 0 warn/err
- `USE_MOCK_BRIDGE=1 dotnet run ...` → chat funcional sem bridge real
- `curl -X POST /Chat/Stream` com mock → SSE válido com thinking + content + usage
- Teste `I-chat-mock-thinking-off` PASS (sem reasoning_content)

---

## Regras de Execução para o Modelo 8B (Executor)

1. **Uma tarefa por vez** — cada item acima vira um micro-enunciado com: OBJETIVO (1 frase), ESCOPO (arquivos exatos + trechos), PROIBIDO (o que não tocar), ACEITE (comando exato: `dotnet build`, `curl ... | grep ...`).
2. **Não invente** — se arquivo não existe, falhe com pergunta exata. Não crie stubs, placeholders, `TODO`.
3. **Build + teste = done** — cada micro-enunciado termina com `dotnet build -c Release` (0 warn/err) + teste do ACEITE executado de verdade (log anexado).
4. **Contexto 36k** — leia apenas os arquivos citados no ESCOPO. Histórico lido do fim ao começo em blocos de 8k se necessário.
5. **Fail fast** — se algo não compila ou teste falha, corrija **tudo** antes de declarar pronto. Nunca entregue vermelho.
6. **Ordem sugerida:** 3.1 → 3.2 → 3.3 → 3.4 → 3.5 → 3.6 → 4.1 → 4.2 → 4.3 → 4.4 → 4.5 → 4.6 → 5.1 → 5.2 → 5.3 → 5.4 → 5.5 → 5.6.

---

## Estado Atual do Projeto (2026-10-05)

- **Repo:** `/media/mint/3686C649614854E6/Projetos/Git/OpenCodeBridge`
- **Branch:** `main` (HEAD `e5ca4dd`)
- **Site rodando:** `http://0.0.0.0:4130` (ASPNETCORE_URLS)
- **Bridges:** GPU `:4124` (qwen3-8b-awq) OK, CPU `:4125` (qwen25-coder-3b) offline
- **DB:** `web.db` com `users` (is_admin=1), `agents` (2 rows)
- **Login:** livre TEMP (sem verificação de senha)
- **Build:** `dotnet build -c Release` → 0 warn / 0 err

---

## Próximo Micro-Enunciado Sugerido (Início Fase 3)

```
OBJETIVO: Criar models Session e SessionMessage + migração DDL em AppDb.Init
ESCOPO:
  - Novo: Rochas.OpenCodeBridge.Web/Models/Session.cs
  - Novo: Rochas.OpenCodeBridge.Web/Models/SessionMessage.cs
  - Editar: Rochas.OpenCodeBridge.Web/Data/AppDb.cs (CREATE TABLE + ALTER TABLE is_admin + índices)
PROIBIDO: Tocar em Controllers, Views, Services, Program.cs
ACEITE: dotnet build -c Release → 0 warn/err; sqlite3 web.db ".schema sessions" e ".schema session_messages" mostram tabelas corretas
```
