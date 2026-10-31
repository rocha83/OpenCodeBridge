# Rochas.OpenCodeBridge

[![NuGet](https://img.shields.io/nuget/v/Rochas.OpenCodeBridge.svg)](https://www.nuget.org/packages/Rochas.OpenCodeBridge)

> **STATUS DO PROJETO / PROJECT STATUS**
>
> **Este projeto será descontinuado. A bridge será fundida ao `AI.Manager`.**
> **This project is being discontinued. The bridge is being merged into `AI.Manager`.**
>
> Até a fusão, este código é a implementação de referência e o harness E2E.
> Features novas vão para o `AI.Manager`.

[Português](#português) | [English](#english) | [Español](#español) | [Français](#français) | [Deutsch](#deutsch)

---

## Português

**Bridge HTTP em C# (.NET 10) entre a API `/v1/responses` do OpenCode e qualquer
backend OpenAI-compatível (llama.cpp, vLLM), mais UI web MVC com loop de tools
agentivo próprio.** O núcleo usa só a BCL (`HttpListener` + `System.Text.Json`):
traduz Responses ⇄ chat/completions, trata `thinking` e tool calling, resolve
perfil por sufixo do model id e aplica o **main harness** e as **duas caudas**
(comportamento + tools).

Linha local de referência: **Gemma 4 E4B** — `Gemma4-E4B-Q4` (**GPU1**,
`:4100`/`:4124`) e `Gemma4-E4B-UD-Q3` (**GPU2**, text-only, `:4110`/`:4125`), os
dois 100 % em GPU (`-ngl 99`) com TurboQuant (`turbo3` no KV) em 12 GB de VRAM
(RTX 3060).

### Como este documento está organizado

Cada funcionalidade aparece **num lugar só**, no mesmo formato:

1. **Por que existe** — a dor real que a feature resolve.
2. **Como resolve** — o mecanismo, e onde ele vive (Core, bridge, Web).
3. **Console** — as flags que ligam/ajustam no boot.
4. **API** — o endpoint HTTP (e o corpo) quando a feature é operável em runtime.
5. **C#** — como consumir o `Rochas.OpenCodeBridge.Core` no seu código.

No fim há a **referência rápida de argumentos** (todas as flags numa tabela, para
consulta pontual) — a explicação de cada uma está na feature correspondente.

### Componentes

| Assembly | Responsabilidade |
|---|---|
| `Rochas.OpenCodeBridge` | Tradutor Responses ⇄ chat/completions, thinking, tool calling, perfis de role, harness, subcomando `gate`. |
| `Rochas.OpenCodeBridge.Core` | Biblioteca BCL pura: tradução, anti-repeat, coerção de tools, composição de harness, guarda de contexto, métricas. |
| `Rochas.OpenCodeBridge.Web` | UI MVC: chat com bypass de Responses, loop de tools nativo (`function_call` → `ToolExecutor` → `function_call_output`), agentes, subagente, escopo. |
| `Rochas.OpenCodeBridge.Test` | Suíte console da **bridge/Core**: unit (HTTP, sem modelo) + E2E (modelo vivo). Exit 0/1. |
| `Rochas.OpenCodeBridge.Web.Test` | Suíte console da **Web**: unit (hash, agentes, CRUD SQLite, sessões, SSE, tool executor) + integração HTTP. Exit 0/1. |

> `dotnet test` não é usado: os dois projetos de teste são runners de console
> (`dotnet run --project …`, exit 0 = PASS, 1 = FAIL).

### Topologia e portas

| nome | modelo | llama | bridge | observações |
|---|---|---|---|---|
| **GPU1** | `Gemma4-E4B-Q4` (`Q4_K_M` + mmproj Q8_0) | `:4100` | `:4124` | visão + tools; `-c 524288 -np 4` (131072/slot) |
| **GPU2** | `Gemma4-E4B-UD-Q3` (`IQ3_XXS`, text-only) | `:4110` | `:4125` | `-c 114688 -np 2` (2×56k), 100 % GPU |
| **CPU forçada** | `Gemma4-E4B-Q4` (`-ngl 0`) | `:4120` | `:4126` | cenário de teste explícito, não é fallback |
| **Web UI** | — | — | `:4130` | MVC + SQLite; `BRIDGE_WEB_PORT` |

```bash
# GPU1 — raia bridge (harness + caudas + anti-repeat), a configuração de produção
dotnet Rochas.OpenCodeBridge.dll --port 4124 --upstream http://127.0.0.1:4100 \
  --model Gemma4-E4B-Q4 --api-key guest@123 --thinking events \
  --mode bridge --main-harness 1 --harness-tails 1 --tail-scope all \
  --temperature 0.2 --max-tokens 16384 \
  --anti-repeat 1 --anti-repeat-window 512 --anti-repeat-retry 1 \
  --breaker-soft 3 --breaker-hard 6 --diagnostic 1 --diagnostic-level metrics
```

```bash
# GPU2 — raia opencode (o cliente se auto-gerencia; SEM main harness)
dotnet Rochas.OpenCodeBridge.dll --port 4125 --upstream http://127.0.0.1:4110 \
  --model Gemma4-E4B-UD-Q3 --api-key guest@123 --mode opencode \
  --harness-tails 1 --tail-scope all --temperature 0.2 --max-tokens 16384
```

> `--mode bridge --main-harness 0` é **combinação inválida**: o processo não sobe
> e devolve `[bridge] ERRO DE ARGUMENTOS: modo bridge exige main harness ON`
> (exit 2). Sem o nosso harness, só existe a raia `--mode opencode`.

### Instalação

```bash
dotnet add package Rochas.OpenCodeBridge
```

---

### Funcionalidades

#### 1) Tradução Responses ⇄ chat/completions

**Por que existe.** O OpenCode fala `/v1/responses`; llama.cpp e vLLM falam
`/v1/chat/completions`. São três contratos diferentes no mesmo turno: `input` vs
`messages`, `function_call` vs `tool_calls`, `reasoning` em itens vs
`reasoning_content` no delta. Sem tradução o cliente não conversa com o backend,
e sem ida-e-volta fiel o modelo perde histórico, tools e raciocínio.

**Como resolve.** `ResponsesTranslation` (Core) é a única rotina de tradução: o
`system` inicial do `input` sobe para `instructions` (`TakeLeadingSystem`), o
input vira `messages` (`InputToMessages`), as tools são renomeadas para o alias
canônico (`CanonToolAlias`) e a resposta de chat volta como objeto Responses. No
bypass nativo (`--passthrough-responses`) os itens de `reasoning` são removidos
antes de encaminhar (`StripReasoningItems`), porque o llama rejeita o campo. O
stream é reemitido delta a delta (ou agregado em 256 chars) e `usage`/`timings`
do upstream são preservados no lugar certo de cada contrato.

**Console.**

```bash
# bypass nativo de Responses no upstream (só remove itens reasoning)
... --passthrough-responses true

# efeito "digitando": cada delta SSE vai direto ao cliente
... --stream-per-delta 1        # 0 = agrega em blocos de 256 chars
```

**API.**

```bash
# dry-run: mostra o /v1/chat/completions que SERIA enviado — não chama o modelo
curl -s -X POST http://127.0.0.1:4124/api/translate \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4-build","input":"ping"}' | jq '{requested_model,temperature,temperature_source,message_count}'

# dry-run do bypass: mostra o input SEM os itens de reasoning
curl -s -X POST http://127.0.0.1:4124/api/sanitize \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"input":[{"type":"reasoning","summary":[]},{"type":"message","role":"user","content":"ping"}]}' \
  | jq '{dropped_reasoning,system_normalized,input}'

# dry-run da volta: converte um chat/completions PRONTO em objeto Responses
curl -s -X POST http://127.0.0.1:4124/api/convert \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4","completion":{"choices":[{"message":{"role":"assistant","content":"pong"}}]}}' \
  | jq '.output[0].content[0].text'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var responses = JsonNode.Parse(body)!.AsObject();

// 1. system inicial do input vira instructions (o resto segue como input)
string? leading = ResponsesTranslation.TakeLeadingSystem(responses);

// 2. input -> messages para o upstream de chat
JsonArray messages = ResponsesTranslation.InputToMessages(responses);

// 3. no bypass: quantos itens de reasoning foram removidos
int dropped = ResponsesTranslation.StripReasoningItems(responses);

// 4. de volta: texto e tools declaradas da resposta
string text = ResponsesTranslation.ExtractResponseText(chatNode);
JsonArray toolNames = ResponsesTranslation.ToolNames(chatNode);
```

---

#### 2) Perfil de role pelo sufixo do model id

**Por que existe.** Planejar, construir e executar pedem amostragens diferentes.
Um único `--temperature` global serve mal aos três: quente demais no build gera
arquivo errado, frio demais no plano mata a exploração. Trocar de processo por
role desperdiça VRAM e quebra o keep-alive do cliente.

**Como resolve.** `RoleProfiles` mapeia o sufixo do model id (`-plan`, `-build`,
`-orch`, `-moe`, `-scripter`, `-exec`) para temperatura + modo de thinking, e a
bridge resolve o perfil por request — um processo serve todos os roles. A
precedência é única e vale para toda temperatura: **cliente manda > perfil do
role > global**. `Sampling.Temperature` devolve valor **e procedência**
(`request`/`profile`/`global`), então a telemetria não mente sobre de onde veio.
`--aux-temp` é o análogo global do **turno de retry** e segue a mesma regra
(detalhe na feature 10).

**Console.**

```bash
# global de fallback (usada quando o id não carrega role e o corpo não manda)
... --temperature 0.2

# thinking off por role (execução determinística não deve gastar raciocínio)
... --thinking-off-roles Build,Scripter,Exec
```

Perfis padrão (tabela única do Core, `RoleProfiles.DefaultCopy()`):

| role | temperatura | thinking |
|---|---|---|
| `plan` | 0.5 | on |
| `orch` | 0.5 | on |
| `moe` | 0.5 | on |
| `build` | 0.2 | off |
| `scripter` | 0.1 | off |
| `exec` | 0.1 | off |

**API.**

```bash
# a temperatura efetiva daquele id + a PROCEDÊNCIA do valor
curl -s -X POST http://127.0.0.1:4124/api/translate \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4-build","input":"ping"}' \
  | jq '{requested_model,temperature,temperature_source,thinking}'
# {"requested_model":"Gemma4-E4B-Q4-build","temperature":0.2,"temperature_source":"profile","thinking":"off"}
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var profiles = RoleProfiles.DefaultCopy();

if (RoleProfiles.TryResolve("Gemma4-E4B-Q4-build", profiles, out var profile))
{
    double temp = profile.Temperature;   // 0.2 pelo perfil
    string thinking = profile.Thinking;  // "off"
}

string role = RoleProfiles.RoleOf("Gemma4-E4B-Q4-build", profiles); // "Build"

// precedência: request > profile > global, com a procedência junto
var (value, source) = Sampling.Temperature(
    requestTemp: null, hasProfile: true, profileTemp: 0.2, globalTemp: 0.3);
// value = 0.2, source = "profile"
```

---

#### 3) System único no loop agentivo (main harness + dedup)

**Por que existe.** No loop agentivo o cliente devolve, a cada turno, o system do
turno anterior — que já passou pelo harness. Recomposto por cima, o harness entra
dentro do "system do role", as caudas se sobrepõem e o contexto cresce a cada
volta até estourar o slot. Além disso, o system do cliente costuma trazer o
prompt do opencode, que não ensina o contrato de tool do modelo local.

**Como resolve.** `MainHarness` é dono do system do caminho gerido: troca o texto
do cliente pelo prompt base, preservando o `<env>`, o bloco de skills e a data.
`SystemPromptDedup` (Core) corta harness e caudas já injetados, e
`MainHarness.CollapseSystems` junta `system`/`developer` espalhados num **único**
`system` por request. O resultado é idempotente: compor duas vezes dá o mesmo
texto. A linha do role continua no system (`SYSTEM DO ROLE (…)`) e o array
`tools` é a única fonte do schema — o texto não duplica contrato.

**Console.**

```bash
# raia bridge: harness do nosso lado (obrigatório neste modo)
... --mode bridge --main-harness 1

# raia opencode: o cliente se auto-gerencia, a bridge só repassa
... --mode opencode --main-harness 0

# força o fluxo gerido para TODOS os clientes, inclusive opencode
... --bridge-managed true
```

**API.**

```bash
# o system final do último request, com as provas de composição
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/test/last-system \
  | jq '{path,lane,chars,main_harness,has_tools,tools_tail,tail_scope,temperature,has_profile}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

// 1. junta system/developer espalhados num único system (null = nada a fazer)
string? collapsed = MainHarness.CollapseSystems(messages);

// 2. troca o system do cliente pelo harness, preservando <env>/skills/data
bool replaced = MainHarness.Apply(messages, enabled: true, role: "Build");

// 3. corte idempotente: harness/cauda já presentes não se acumulam
bool hasHarness = SystemPromptDedup.HasHarness(system);
string clean = SystemPromptDedup.Clean(system);   // fora harness + fora caudas
```

---

#### 4) Caudas de harness (comportamento + tools)

**Por que existe.** O prompt base diz o que fazer; falta a **mecânica da linha**:
como chamar a tool certa, com que forma de path, o que fazer quando o alvo não
existe, como validar o script antes de dizer "pronto". Sem isso o modelo de 4–9B
inventa campo, repete a mesma call e entrega prosa em vez de artefato.

**Como resolve.** `HarnessTails` acrescenta dois deltas sobre a base:
**comportamento** (como agir/entregar) e **tools** (como chamar). A cauda de
tools é **composta pelo schema do request** (`ComposeTools`): ela nunca ensina
uma tool que o pedido não declarou. Sem array `tools`, entra
`DefaultBehaviorNoTools` — senão o modelo responde com o JSON da tool como texto.
O reconhecimento é por cabeçalho estável (`HarnessTails.HasToolsTail`), não por
igualdade do texto inteiro: a composição varia com o schema, a sentinela não.
`--tail-scope` decide onde valem (`managed` / `all` / `off`).

**Console.**

```bash
... --harness-tails 1 --tail-scope all \
    --harness-behavior-tail "Valide scripts com bash -n; entregue evidência." \
    --harness-tools-tail "Use a tool dedicada; nunca invente campos."
```

**API.**

```bash
# o que ESTÁ configurado nesta instância (tamanho de cada cauda)
curl -s http://127.0.0.1:4124/api/harness \
  | jq '{tail_scope,harness_tails,tails_managed,behavior_chars,tools_chars}'

# e a prova de que ESTE request carregou a cauda de tools
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/test/last-system \
  | jq '{has_tools,tools_tail,tail_scope,chars}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

// cauda composta pelas tools do request (null = catálogo completo)
string toolsTail = HarnessTails.ComposeTools(new[] { "read", "write", "edit" });

string system = HarnessTails.Compose(
    clientSystem, harness: true,
    behavior: HarnessTails.DefaultBehavior,
    tools: toolsTail,
    hasTools: true);   // false => DefaultBehaviorNoTools

// o portão de fluxo usa a mesma rotina (sentinela, não texto inteiro)
bool ok = HarnessTails.HasToolsTail(system, toolsTail);
```

---

#### 5) Tool calling: catálogo único e coerção de texto

**Por que existe.** Modelo pequeno local ora emite `tool_calls` nativo, ora
escreve a call em prosa (`[Assistant tool call]: write({...})`), ora embrulha em
`<tool_call>` ou no envelope Code Mode `execute{tools.X}`. Cada cliente OpenAI
redeclara o schema do seu jeito e as versões divergem — duas cópias do catálogo
significam duas verdades.

**Como resolve.** `ToolCatalog` é a fonte única do schema (chat e Responses
saem daqui; consumidor não redeclara). `ToolCoercion` resgata a call escrita:
extrai do conteúdo, converte em `tool_calls` no request e remove as tags de
controle da resposta visível. `--upstream-parser` declara a capacidade do
backend (nativo ou texto) e a coerção é decidida por capacidade, não por nome de
modelo. O `execute{tools.X}` do Code Mode é resolvido por `--tool-strategy`.

**Console.**

```bash
# backend sem parser de tool call (linha coagida)
... --upstream-parser none --coerce-text-tools 1

# resgate de call narrada e do envelope Code Mode
... --coerce-narrated 1 --tool-strategy 1

# linha que recusa grammar+tools: envelope JSON via grammar, convertido de volta
... --tool-shim json --tool-shim-max-string 2000 --tool-shim-min-calls 0
```

**API.**

```bash
# o schema canônico que a bridge anuncia (mesma fonte para chat e Responses)
curl -s -X POST http://127.0.0.1:4124/api/translate \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4","input":"ping","tools":[{"type":"function","function":{"name":"read","description":"le arquivo","parameters":{"type":"object","properties":{"path":{"type":"string"}}}}}]}' \
  | jq '{tool_count, tools: [.chat_request.tools[].function.name]}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

// schema canônico — o consumidor NÃO redeclara tools
JsonArray chatTools = ToolCatalog.Chat();
JsonArray responsesTools = ToolCatalog.Responses();
List<string> known = chatTools
    .Select(t => t?["function"]?["name"]?.GetValue<string>() ?? "")
    .Where(n => n.Length > 0).ToList();

// resgate de call escrita em texto
List<(string Name, string Arguments)> calls = ToolCoercion.ExtractTextToolCalls(content, known);
int synthesized = ToolCoercion.CoerceChatCalls(root, known);
string visible = ToolCoercion.StripControlTags(text, out _);
```

---

#### 6) Execução de tools no workspace

**Por que existe.** Quem chama a tool é o modelo, mas **quem executa** precisa ser
uma rotina só. Se cada consumidor (Web, CLI, teste) implementa `read`/`write`/
`edit`/`glob`/`shell`, o path relativo, o limite de saída e a recusa fora da raiz
divergem — e um deles escreve fora do repositório.

**Como resolve.** `BridgeToolExecutor` roda no Core, com `WorkspaceGuard` como
porteiro: path é relativo à raiz (`--workspace`, ou `workspace` por chamada), a
busca de arquivo inexistente devolve o caminho real (para o modelo não repetir a
call), e nada sai da raiz. `POST /api/tools/execute` é a porta única — a Web
delega para cá em vez de manter executor próprio. O role do pedido limita o que
pode ser chamado.

**Console.**

```bash
... --workspace /media/…/Projetos/Git/OpenCodeBridge --tool-output-limit 8000
```

**API.**

```bash
# executa uma tool na raiz do workspace (ou no workspace informado)
curl -s -X POST http://127.0.0.1:4124/api/tools/execute \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"name":"read","arguments":{"path":"README.md"},"role":"Build","timeout":60}' \
  | jq '{ok, chars: (.output|length), error}'

# arquivo inexistente devolve o caminho REAL (o modelo corrige na hora)
curl -s -X POST http://127.0.0.1:4124/api/tools/execute \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"name":"read","arguments":{"path":"readme.md"},"role":"Build"}' | jq '.error'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core.Tools;

var executor = new BridgeToolExecutor(root: "/media/…/repo", upstream: "http://127.0.0.1:4100",
    apiKey: "guest@123", log: null, role: "Build");

ToolResult result = executor.Execute("write", """{"path":"src/a.txt","content":"x"}""", timeoutSec: 60);
if (result.Success) Console.WriteLine(result.Output);
else Console.WriteLine(result.Error);

// o porteiro: resolve path relativo à raiz e recusa fuga do workspace
bool okPath = WorkspaceGuard.TryResolve("/media/…/repo", "src/a.txt", out string full, out string why);
```

---

#### 7) Permissão de tools por role (blacklist e comando de risco)

**Por que existe.** Planear não deve escrever arquivo; um `rm`/`git push` no meio
de um turno autônomo custa caro. Mas esconder a tool do schema piora o loop: o
modelo tenta o que não existe e alucina argumento.

**Como resolve.** A regra do projeto é **bloquear a chamada, não esconder a
tool**: o array `tools` vai completo e a política decide na execução. O system do
role enuncia a proibição **por ação** (`MainHarness.PermissionsLine`), então o
modelo sabe o que não pode antes de tentar. Comando de risco vira **pergunta**
(`--risk-ask`) ou é negado, com janela de aprovação; `--tool-blacklist` bloqueia
por role e `--risk-tools-alerts` casa regex por role. A decisão é **uma**:
`POST /api/permission/check`.

**Console.**

```bash
... --tool-blacklist "Plan=write,edit,exec,install,git;MoE=write,edit;Orch=write,edit" \
    --risk-ask 1 --risk-ask-minutes 30 \
    --risk-tools-alerts "Plan=git\s+push,\brm\b;Orch=\bdd\b"
```

**API.**

```bash
# a Web (e qualquer consumidor) pergunta AQUI antes de executar
curl -s -X POST http://127.0.0.1:4124/api/permission/check \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4-plan","tool":"write","arguments":{"path":"a.txt"}}' \
  | jq '{action,role,tool,reason,notice}'
# {"action":"deny","role":"Plan","tool":"write",...}
```

**C# (consumidor).**

```csharp
// a política é uma só (a da bridge). O consumidor pergunta por HTTP e não
// reimplementa tabela de permissão no próprio código.
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4124") };
http.DefaultRequestHeaders.Authorization = new("Bearer", "guest@123");

string body = """{"model":"Gemma4-E4B-Q4-plan","tool":"write","arguments":{"path":"a.txt"}}""";
using var resp = await http.PostAsync("/api/permission/check",
    new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
var verdict = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
Console.WriteLine($"{verdict["action"]} — {verdict["reason"]}");
```

---

#### 8) Anti-repetição de conteúdo

**Por que existe.** Modelo local pequeno entra em loop verbatim: repete o mesmo
parágrafo, a mesma frase de raciocínio, a mesma explicação. O turno queima tokens
até o teto e o cliente recebe lixo — e o próximo turno repete o padrão.

**Como resolve.** `ContentAntiRepeat` compara a **cauda normalizada** do texto
streamado (janela configurável em chars). Ao detectar, a bridge **aborta o stream
upstream** (não espera os 16k tokens), faz **1 retry** com aviso brando no
histórico e descarta a janela repetida. Se o loop volta, o turno é encerrado com
o aviso. A decisão olha só o texto de conteúdo — não confunde repetição legítima
de código (indentação, `using`) com loop.

**Console.**

```bash
... --anti-repeat 1 --anti-repeat-window 512 \
    --anti-repeat-abort 1 --anti-repeat-retry 1 \
    --anti-repeat-notice "Reformule o raciocínio; não repita o mesmo parágrafo."
```

**API.**

```bash
# quantos abortos/retries aconteceram nesta vida do processo
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics \
  | jq '{anti_repeat_content, anti_repeat_tools, upstream_abort, upstream_abort_retry}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

// detector com estado, um por resposta
var reply = new StreamAntiRepeatReply(window: 512);
string acc = "";
foreach (string delta in deltas)
{
    acc += delta;
    if (reply.IsRepeat(acc)) { /* aborta upstream e reenvia com aviso */ break; }
}

// mesma matemática, sem estado (conferência offline)
string norm  = ContentAntiRepeat.Normalize("  Hello   WORLD ");
double ratio = ContentAntiRepeat.Similarity("abc", "abd");           // ~0.66
int dupAt    = ContentAntiRepeat.TailDuplicateAt(acc, window: 512);  // -1 = sem loop
```

---

#### 9) Breaker de tools (falha no mesmo alvo)

**Por que existe.** O loop mais caro não é de texto: é de **tool no mesmo alvo**
com argumento levemente diferente. `edit` com `oldString` obsoleto repetido 15
vezes queima a bateria inteira e o modelo não muda de plano sozinho.

**Como resolve.** `ToolsBreaker` conta falhas seguidas **no mesmo alvo**
(`ToolSig.Target` remove dígitos e separadores, então `a[1..3]` e `a[2..4]` são o
mesmo alvo). No **soft** injeta um aviso no histórico; no **hard** encerra o
turno. `RepeatedCall` acha chamadas idênticas e `RepeatedWritePath` cobre o caso
de reescrita do mesmo arquivo. O aviso ensina a forma certa e nunca exibe o
literal inválido (o modelo copia o que a dica mostra).

**Console.**

```bash
... --breaker-soft 3 --breaker-hard 6
```

**API.**

```bash
# o veredito do breaker sai no log (evento tool-error-breaker); a política
# também conta recusas e nudges em /api/metrics
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics \
  | jq '{nudge_args, ring2_deny, ring3_deny}'
grep -o '"event":"tool-error-breaker","verdict":"[a-z]*"' /tmp/bridge-gpu.log | tail -3
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var breaker = new ToolsBreaker(soft: 3, hard: 6);
BreakerResult verdict = breaker.Evaluate(messages);

if (verdict.Verdict == BreakerVerdict.Nudge) breaker.ApplyNudge(messages, verdict.Notice);
if (verdict.Verdict == BreakerVerdict.Trip)  return;   // encerra o turno

var repeated = new RepeatedCall(minCount: 2).Find(messages);
string sig    = ToolSig.Of("write", argsJson);       // assinatura canônica
string target = ToolSig.Target("write", argsJson);   // sem dígitos/separadores
bool failed   = ToolSig.IsFailure(toolOutput);
```

---

#### 10) Retry de aborto do upstream e temperatura de desvio

**Por que existe.** `llama-server` aborta a conexão em prefill longo, grammar
incompatível ou contexto estourado. Para o cliente isso vira 500 opaco — e o
turno inteiro (às vezes 375 s de prefill) é perdido. Reenviar o mesmo corpo na
mesma temperatura tende a repetir a mesma saída.

**Como resolve.** `RetryNudge` detecta o aborto e faz **1 retry** com nudge no
histórico ("outra abordagem, menos passos por call, escrita concisa") e
**temperatura de desvio**. A referência do desvio tem a **mesma precedência da
temperatura do turno**: perfil do role manda quando o id carrega role; sem role
vale o valor global do host (`--aux-temp`); sem ele, o perfil de desvio (`plan`)
— e nunca esfria um turno que já estava mais quente. Segunda falha vira
`502 upstream_aborted` estruturado, com contador próprio.

**Console.**

```bash
# valor global do desvio (análogo do --temperature, para quando o id não tem role)
... --aux-temp 0.5

# sem --aux-temp: o desvio vem do perfil `plan` (tabela única do Core)
... --temperature 0.2
```

**API.**

```bash
# referência em vigor (null = o desvio vem do perfil do role) + contadores
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status | jq '{aux_temp,temperature}'
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics | jq '{upstream_abort,upstream_abort_retry}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

// precedência: role resolvido > valor global do host > perfil de desvio
double reference = Sampling.DetourReference(hasRole: false, profiles: null, auxTemp: 0.5);

// nunca abaixo do turno atual
double retryTemp = Sampling.ForRetry(current: 0.1, detourReference: reference); // 0.5

// corpo do retry com nudge + temperatura do desvio (null = sem histórico)
string? retryBody = RetryNudge.WithAbortNudge(bodyJson, currentTemperature: 0.1, reference);

// o transporte só usa o predicado: nada de tabela de role na camada de I/O
bool abort = RetryNudge.IsUpstreamAbort(exception);
```

---

#### 11) Guarda de contexto e orçamento de tokens

**Por que existe.** O llama divide o `-c` total entre `-np` slots: 524288 no
total, 131072 por slot. Passar disso não gera aviso — gera `request exceeds the
available context size` e **core dump** do servidor. E um turno que estoura no
meio do prefill custa minutos de GPU.

**Como resolve.** Duas rotinas, papéis distintos: `ContextGuard.Tripped` mede a
fração do **slot** (não do total) sobre `usage.prompt_tokens` e a bridge devolve
`413` antes do 400 seco; `ContextGuard.Compact` corta o histórico em blocos
antigos preservando os últimos turnos (tool calls e resultados ficam coerentes).
`--context-slots` é o que faz a conta por slot. Orçamento de saída é separado:
`--max-tokens` (linha GPU), `--coerced-max-tokens` (linha coagida) e
`--tool-output-limit` (corte por resultado de tool).

**Console.**

```bash
... --context-tokens 524288 --context-slots 4 --context-threshold 0.95 --context-guard 1 \
    --max-tokens 16384 --coerced-max-tokens 16384 --tool-output-limit 8000
```

**API.**

```bash
# janela/slots em vigor + compactações feitas
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status \
  | jq '{context_tokens,context_slots,context_threshold,context_guard,max_tokens}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var settings = new ContextGuardSettings(Enabled: true, Threshold: 0.95, Tokens: 524288, Slots: 4);

// medido na resposta do upstream: fração do SLOT, não do total
bool trips = ContextGuard.Tripped(upstreamJson, settings, out int promptTokens, out double fraction);

// corte de histórico que preserva os últimos turnos (true = mexeu)
bool compacted = ContextGuard.Compact(messages, contextTokens: 524288, slots: 4, threshold: 0.95);
```

---

#### 12) Auth, usuários e ajuste remoto

**Por que existe.** A bridge escuta em `0.0.0.0` numa máquina da rede. Sem auth,
qualquer um no Wi-Fi executa tool no seu disco. E operar a linha (temperatura,
teto) não pode exigir restart — restart derruba o pool HTTP no meio de um run.

**Como resolve.** `--api-key` é o Bearer exigido do cliente e enviado ao
upstream (vazio = sem auth). JWT (`--jwt-secret`, `--jwt-expiry`) autentica a Web
e o painel; `--users-db` é o SQLite de usuários; `POST /api/auth/login` devolve
token com validade e flag de admin. `POST /api/config` ajusta **parcialmente**
thinking/temperatura/teto sem reiniciar, e devolve o `/api/status` novo.

**Console.**

```bash
... --api-key guest@123 --jwt-secret "0123456789abcdef" --jwt-expiry 720 \
    --users-db /var/lib/opencodebridge/users.db
```

**API.**

```bash
# login -> token (a Web usa o mesmo)
curl -s -X POST http://127.0.0.1:4124/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@local","password":"…"}' | jq '{token: (.token[0:20]), expires_in, admin}'

# ajuste parcial em runtime (só o que vier no corpo muda)
curl -s -X POST http://127.0.0.1:4124/api/config \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"temperature":0.2,"max_tokens":16384}' | jq '{temperature,max_tokens,thinking}'
```

**C# (consumidor).**

```csharp
// o cliente fala com a bridge autenticada; a bridge fala com o upstream com a
// mesma chave. Nenhum consumidor guarda a chave do upstream por conta própria.
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4124") };
http.DefaultRequestHeaders.Authorization = new("Bearer", Environment.GetEnvironmentVariable("BRIDGE_KEY"));

using var status = await http.GetAsync("/api/status");
var snapshot = System.Text.Json.Nodes.JsonNode.Parse(await status.Content.ReadAsStringAsync())!;
Console.WriteLine($"{snapshot["status"]} {snapshot["model"]} {snapshot["temperature"]}");
```

---

#### 13) Observabilidade: status, métricas, sonda e painel

**Por que existe.** Sem telemetria, "o teste falhou" não distingue modelo ruim de
ambiente errado. Contador que só zera com restart força reiniciar a bridge no
meio da bateria — e aí o número do run atual se perde junto.

**Como resolve.** `/api/status` é a foto de config + telemetria (porta, upstream,
modelo, flags efetivas, `aux_temp`, janela/slots, último cliente). `/api/metrics`
traz vazão, tools, arquivos e os contadores de aborto/breaker. A sonda Serilog
(`--diagnostic`, `--diagnostic-level`) guarda amostras em `/api/test/probe`.
`POST /api/diag/reset` **zera sem downtime** (`?probe=1&files=1&panel=1`), então
restart fica reservado a binário novo. `POST /api/test/step` publica o step em
execução (`verdict: RUN`) e o `step_end` sobrescreve com PASS/FAIL/SKIP.

**Console.**

```bash
... --diagnostic 1 --diagnostic-level metrics --log /tmp/bridge-gpu.log
```

**API.**

```bash
# foto da instância
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status \
  | jq '{status,version,model,mode,tail_scope,main_harness,harness_tails,temperature,aux_temp}'

# contadores (requests, tokens/s, tools, arquivos, abortos)
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics | jq 'keys'

# zera sem restart: sonda + contadores de arquivo + painel
curl -s -X POST -H 'Authorization: Bearer guest@123' \
  'http://127.0.0.1:4124/api/diag/reset?probe=1&files=1&panel=1' | jq
# {"ok":true,"event":"diag-reset"}

# publica o step em execução (o painel mostra RUN até o step_end)
curl -s -X POST -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"step":"E-tools-full","mode":"bridge","verdict":"RUN"}' \
  http://127.0.0.1:4124/api/test/step | jq

# lista os steps (war=1 só os que têm mode)
curl -s -H 'Authorization: Bearer guest@123' 'http://127.0.0.1:4124/api/test/steps?war=1' | jq '.[-2:]'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

// contador de domínio: o mesmo snapshot vai para /api/metrics
Metrics.Record("tool", new JsonObject { ["tool"] = "write", ["ok"] = true });
JsonObject snapshot = Metrics.Snapshot();
```

---

#### 14) Web UI (chat, agentes, loop de tools)

**Por que existe.** Testar a linha pelo TUI do opencode não mostra o estado do
agente (agentes, sessões, tool calls, confirmação de comando de risco). E a Web
precisava de um executor de tools — que já existe na bridge.

**Como resolve.** A Web (MVC + SQLite) faz chat com **bypass de Responses**,
roda o loop de tools nativo (`function_call` → executor → `function_call_output`)
e delega as decisões que já são do Core: schema (`ToolCatalog`), política
(`/api/permission/check`), execução (`/api/tools/execute`), métricas
(`/api/metrics`) e harness. Nada disso é reimplementado na Web. Porta via
`--port`/`Web:Port` no `appsettings.json` ou env `BRIDGE_WEB_PORT`.

**Console.**

```bash
BRIDGE_WEB_PORT=4130 BRIDGE_WEB_DB_DIR=/var/lib/opencodebridge \
  dotnet run --project Rochas.OpenCodeBridge.Web -c Release
```

**API.**

```bash
# a Web está de pé? (health do ASP.NET)
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:4130/

# e ela consome a bridge assim (mesma chave, mesma política)
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/harness | jq '.tools_chars'
```

**C# (Core, do lado da Web).**

```csharp
using Rochas.OpenCodeBridge.Core;
using Rochas.OpenCodeBridge.Core.Tools;

// 1. schema vem do Core, nunca de uma cópia local
JsonArray tools = ToolCatalog.Responses();

// 2. execução vem do Core (workspace por request)
var executor = new BridgeToolExecutor(workspaceRoot, upstream, apiKey, log, role);
ToolResult result = executor.Execute("glob", """{"pattern":"**/*.cs"}""", 60);
```

---

#### 15) Subcomando `gate`, testes e WAR

**Por que existe.** Um agente só "termina" quando o artefato existe e compila —
"deve funcionar" não é entrega. E a suíte precisa de dois artefatos distintos: o
rápido (sem modelo, sempre roda) e o E2E (modelo vivo, caro, roda em bateria).

**Como resolve.** O mesmo binário tem o subcomando `gate <repo> "<prompt>"`: ele
roda o agente e devolve exit 0/1 com o veredito. A suíte de console
(`Rochas.OpenCodeBridge.Test`) separa unit de E2E por artefato; o WAR
(`--war-level 1..4`, `--war-all`) organiza os steps em níveis com fail-fast e
saída em `logs/levels`. Teste que usa tools roda com a cauda de tools ligada
(`--harness-tails 1`, `--tail-scope all|managed`) — sem isso o modelo perde o
contrato e falha por motivo errado.

**Console.**

```bash
# unit, sem modelo (segundos)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --api-key guest@123 --skip-e2e

# uma bateria WAR (nível 1..4), fail-fast, saída em logs/levels
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-level 1 --api-key guest@123

# gate: executa o agente e devolve exit 0/1 com veredito
Rochas.OpenCodeBridge gate /caminho/do/repo "implemente X e valide com dotnet build"
```

**API.**

```bash
# os steps publicados pelo driver (o que o painel mostra)
curl -s -H 'Authorization: Bearer guest@123' 'http://127.0.0.1:4124/api/test/steps?war=1' \
  | jq '[.[] | {step,verdict,ms}] | .[-5:]'

# a sonda mais recente do run (diagnóstico ligado)
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/test/probe/latest \
  | jq '.'
```

**C# (Core).**

```csharp
// o gate é do binário (executa o agente e decide), não da biblioteca:
// o Core expõe as peças que ele usa — harness, caudas, anti-repeat, breaker.
string system = HarnessTails.Compose(clientSystem, harness: true,
    behavior: HarnessTails.DefaultBehaviorNoTools, tools: null, hasTools: false);
bool ok = HarnessTails.HasToolsTail(system, HarnessTails.DefaultTools);
```

---

### Referência rápida de argumentos

Flags booleanas aceitam `true/false` e `1/0`. A explicação de cada flag está na
feature correspondente acima; esta tabela é só consulta.

#### A) Rede / processo

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--port` | `4124` | Porta HTTP. | `--port 4125` |
| `--listen` / `--host` | `0.0.0.0` | Interface de escuta (`--host` é alias). | `--listen 127.0.0.1` |
| `--upstream` | `http://127.0.0.1:4100` | URL base do backend OpenAI-compatível. | `--upstream http://127.0.0.1:4110` |
| `--model` | `Gemma4-E4B-Q4` | Id do modelo servido/reportado. | `--model Gemma4-E4B-UD-Q3` |
| `--log` | `/tmp/opencodebridge.log` | Caminho do log JSONL. | `--log /tmp/bridge-gpu2.log` |
| `--workspace` | cwd do processo | Raiz do `POST /api/tools/execute`. | `--workspace /media/…/repo` |

#### B) Auth / admin

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--api-key` | vazio | Bearer exigido do cliente e enviado ao upstream. Vazio = sem auth. | `--api-key guest@123` |
| `--jwt-secret` | embutido | Segredo HMAC do JWT (mín. 16 chars). | `--jwt-secret "0123456789abcdef"` |
| `--jwt-expiry` | `720` | Validade do JWT em minutos. | `--jwt-expiry 60` |
| `--users-db` | app data | Caminho do SQLite de usuários. | `--users-db /tmp/users.db` |
| `--bridge-managed` | `false` | Força o fluxo gerido para todo cliente (inclusive opencode). | `--bridge-managed true` |

#### C) Transporte / compatibilidade

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--passthrough-responses` | `false` | Passthrough nativo de Responses ao llama.cpp (só remove itens reasoning). | `--passthrough-responses true` |
| `--stream-per-delta` | `true` | Emite cada delta SSE em vez de agregar 256 chars. | `--stream-per-delta 0` |
| `--qwen-standard` | `false` | Modo legado Qwen: ModelPrompt sintetizado + nomes Alibaba. | `--qwen-standard 1` |
| `--thinking-format` / `--thinking` | `events` | Formato do reasoning: `events` (itens) ou `off` (descarta). | `--thinking-format off` |
| `--thinking-off-roles` | vazio | Força thinking off por nome de role. | `--thinking-off-roles Build,Scripter,Exec` |

#### D) Harness / prompt

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--main-harness` | `true` | Substitui o system do cliente pelo `Core/MainHarness.Text` no caminho gerido. | `--main-harness 0` |
| `--harness-tails` | `true` | Liga as duas caudas (comportamento + tools). | `--harness-tails 0` |
| `--harness-behavior-tail` | `HarnessTails.DefaultBehavior` | Cauda de comportamento (como agir/entregar). | `--harness-behavior-tail "Valide scripts com bash -n."` |
| `--harness-tools-tail` | `HarnessTails.DefaultTools` | Cauda de tools (como chamar). | `--harness-tools-tail "Use a tool dedicada."` |
| `--tail-scope` | `managed` | Onde as caudas valem: `managed`, `all`, `off`. | `--tail-scope all` |
| `--mode` | auto | `bridge` = MainHarness+tails+anti-repeat+fixes; `opencode` = cliente master + tails (sem MainHarness). | `--mode bridge` |
| `--breaker-soft` | `3` | Falhas seguidas no mesmo alvo que injetam o aviso. | `--breaker-soft 2` |
| `--breaker-hard` | `6` | Falhas seguidas que encerram o turno. | `--breaker-hard 4` |

#### E) Coerção de tools

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--upstream-parser` | `native` | Capacidade do upstream: `native` (tool_calls) ou `none` (texto). | `--upstream-parser none` |
| `--coerce-text-tools` | `false` | Extrai chamada escrita no texto (```json, `<tool_call>`, `<tools>`). | `--coerce-text-tools 1` |
| `--coerce-narrated` | `true` | Resgata `[Assistant tool call]: nome({...})`. | `--coerce-narrated 0` |
| `--tool-strategy` | `true` | Resgate Code Mode: `execute{tools.X}` → tool `X`. | `--tool-strategy 0` |
| `--tool-shim` | vazio | `json` = remove tools, injeta envelope JSON via grammar e converte de volta. | `--tool-shim json` |
| `--tool-shim-max-string` | `2000` | Máx. de chars por string na grammar (`0` = sem cap). | `--tool-shim-max-string 4000` |
| `--tool-shim-min-calls` | `0` | Calls no histórico antes de liberar `{"final":…}`. | `--tool-shim-min-calls 1` |

#### F) Anti-repetição

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--anti-repeat` | `true` | Observa `thinking`/`content`/tool-args para loop verbatim. | `--anti-repeat 0` |
| `--anti-repeat-abort` | `true` | Aborta o stream upstream assim que o detector dispara. | `--anti-repeat-abort 0` |
| `--anti-repeat-retry` | `true` | Um retry com o aviso brando, descartando a janela repetida. | `--anti-repeat-retry 0` |
| `--anti-repeat-window` | `256` | Janela comparada em chars normalizados (`64..2048`). | `--anti-repeat-window 512` |
| `--anti-repeat-notice` | `ContentAntiRepeat.SoftNotice` | Texto do aviso brando do retry. | `--anti-repeat-notice "Reformule o raciocínio."` |

#### G) Orçamento / contexto

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--max-tokens` | `0` | Teto de saída da linha GPU; `0` = upstream/cliente decide. | `--max-tokens 16384` |
| `--coerced-max-tokens` / `--cpu-max-tokens` | `0` | Teto da linha coagida; `0` = sem teto. | `--cpu-max-tokens 8192` |
| `--context-guard` | `false` | Devolve 413 ao atingir o limiar do ctx. | `--context-guard 1` |
| `--context-threshold` | `0.95` | Fração do ctx por slot (`0.5..0.99`). | `--context-threshold 0.90` |
| `--context-tokens` | `0` | Tamanho do ctx em tokens; `0` = guarda inativa. | `--context-tokens 524288` |
| `--context-slots` | `1` | Slots paralelos do llama (`-np`): a conta é por slot. | `--context-slots 4` |

#### H) Geração / artefatos

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--temperature` | `0.3` | Temperatura global quando cliente/perfil não manda. | `--temperature 0.2` |
| `--aux-temp` | — | Global do turno de RETRY (desvio). Mesma precedência da `--temperature`: role resolvido manda; sem role, vale este valor; sem ele, o perfil de desvio (`plan`). | `--aux-temp 0.5` |
| `--tool-output-limit` | `0` | Corta cada resultado de tool em N chars; `0` = integral. | `--tool-output-limit 8000` |
| `--sampling-passthrough` | `false` | Repassa amostragem do cliente (`repeat_penalty`, `dry_*`, `top_p`, `seed`). | `--sampling-passthrough 1` |

#### I) Segurança / permissões

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--risk-ask` | `true` | Comando de risco vira pergunta; `0` = nega. | `--risk-ask 0` |
| `--risk-ask-minutes` | `30` | Janela de aprovação em minutos. | `--risk-ask-minutes 10` |
| `--risk-tools-alerts` | vazio | Regex por role para tools de risco. | `--risk-tools-alerts "Plan=git\\s+push"` |
| `--tool-blacklist` | vazio | Blacklist de chamada por role (a tool segue anunciada). | `--tool-blacklist "Plan=write,edit,exec;MoE=write"` |

#### J) Observabilidade

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--diagnostic` | `false` | Liga a sonda Serilog. | `--diagnostic 1` |
| `--diagnostic-level` | `off` | `off`, `metrics` (sem corpos) ou `dump` (corpos completos). | `--diagnostic-level metrics` |

#### L) Legado (nunca em produção)

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--legacy-harness` | `false` | Reativa guardrail/strip/retry/clamp; pré-requisito da coerção de texto. | `--legacy-harness 1` |

### Endpoints da API

| Método | Rota | Para quê |
|---|---|---|
| `POST` | `/v1/responses` | contrato Responses (o que o OpenCode fala) |
| `POST` | `/v1/chat/completions` | contrato chat/completions |
| `GET` | `/v1/models` | lista o modelo servido |
| `GET` | `/api/status` | foto de config + telemetria |
| `GET` | `/api/metrics` | contadores de vazão, tools, arquivos, abortos |
| `GET` | `/api/harness` | caudas em vigor (chars) e escopo |
| `POST` | `/api/permission/check` | decisão `allow`/`ask`/`deny` por role |
| `POST` | `/api/tools/execute` | executa tool no workspace |
| `POST` | `/api/translate` | dry-run: chat/completions que seria enviado |
| `POST` | `/api/sanitize` | dry-run do bypass: input sem itens de reasoning |
| `POST` | `/api/convert` | dry-run da volta: chat/completions → Responses |
| `POST` | `/api/config` | ajuste parcial em runtime (thinking/temp/teto) |
| `POST` | `/api/auth/login` | login → JWT |
| `POST` | `/api/diag/reset` | zera contadores/sonda/painel sem restart |
| `GET` | `/api/test/last-system` | system final do último request + provas de composição |
| `POST` | `/api/test/step` | publica o step em execução (`RUN`) |
| `GET` | `/api/test/steps` | steps publicados (`?war=1` só com `mode`) |
| `GET` | `/api/test/probe/latest` | última amostra da sonda |

### Testes — quem verifica qual artefato

| Suíte | Dono | Execução | Tempo |
|---|---|---|---|
| `Rochas.OpenCodeBridge.Test` | **Bridge/Core**: tradução, perfis, anti-repeat, breaker, harness, auth, métricas + E2E | `dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --api-key guest@123 --skip-e2e` | segundos (unit) |
| `Rochas.OpenCodeBridge.Web.Test` | **Web**: hash/JWT, agentes, CRUD SQLite, sessões, SSE, tool executor, confirm gate | `dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --web http://127.0.0.1:4130 --units-only` | ~3,4 s |
| Integração Web | endpoints HTTP + stream de chat (exige `:4130`) | `… --integration-only` | ~2–4 min (modelo vivo) |

Dívida conhecida: `Unit/AntiRepeatCoreTests.cs` e `Unit/DedupGuardTests.cs`
exercitam tipos do Core, mas vivem no Web.Test (validam o que a Web consome).
Devem migrar para `Rochas.OpenCodeBridge.Test` na fusão.

```bash
# checagem rápida, sem modelo
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --skip-e2e --api-key guest@123
dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --web http://127.0.0.1:4130 --units-only

# pipeline E2E (llama/bridge vivos)
./scripts/run-e2e-pipeline.sh --phase 0   # health + VRAM
./scripts/run-e2e-pipeline.sh --phase 1   # unit
./scripts/run-e2e-pipeline.sh --phase 2   # science + mission
./scripts/run-e2e-pipeline.sh --phase 3   # opencode standalone
```

### Docs

- `docs/LAB-SETUP.md` — infra: units, flags do llama, TurboQuant, VRAM, cenários A–E, referência completa de flags/appsettings.
- `AGENTS.md` — regras do operador.
- `docs/AGENTS-PROMPTS.md` — system prompts versionados.
- `HISTORY.md` — histórico de construção e medições.
- `docs/RUNBOOK-E2E.md` — fases e estado do ambiente.
- `docs/BENCHTESTS.md` — baterias (`run-battery-dual.sh`, WAR, frontpipe).
- `docs/PENDENCIAS.md` / `docs/PENDINGS.md` — pendências.

---

## English

**C# (.NET 10) HTTP bridge between the OpenCode `/v1/responses` API and any
OpenAI-compatible backend (llama.cpp, vLLM), plus an ASP.NET MVC web UI with its
own agentic tool loop.** The core uses only the BCL (`HttpListener` +
`System.Text.Json`): it translates Responses ⇄ chat/completions, handles
`thinking` and tool calling, resolves role profiles by model-id suffix, and
applies the **main harness** and the **two harness tails** (behavior + tools).

Local reference line: **Gemma 4 E4B** — `Gemma4-E4B-Q4` (**GPU1**, `:4100`/`:4124`)
and `Gemma4-E4B-UD-Q3` (**GPU2**, text-only, `:4110`/`:4125`), both fully on GPU
(`-ngl 99`) with TurboQuant (`turbo3` KV) inside 12 GB of VRAM (RTX 3060).

### How this document is organized

Every feature appears **once**, in the same shape:

1. **Why it exists** — the real pain it solves.
2. **How it solves it** — the mechanism, and where it lives (Core, bridge, Web).
3. **Console** — the flags that enable/tune it at boot.
4. **API** — the HTTP endpoint (and body) when it is operable at runtime.
5. **C#** — how to consume `Rochas.OpenCodeBridge.Core` from your code.

The **quick flag reference** at the end lists every flag for lookup only; the
explanation of each one lives in its feature.

### Components

| Assembly | Responsibility |
|---|---|
| `Rochas.OpenCodeBridge` | Responses ⇄ chat/completions translator, thinking, tool calling, role profiles, harness, `gate` subcommand. |
| `Rochas.OpenCodeBridge.Core` | Pure BCL library: translation, anti-repeat, tool coercion, harness composition, context guard, metrics. |
| `Rochas.OpenCodeBridge.Web` | MVC UI: chat with Responses bypass, native tool loop (`function_call` → executor → `function_call_output`), agents, subagent, scope. |
| `Rochas.OpenCodeBridge.Test` | Console suite for the **bridge/Core**: unit (HTTP, no model) + E2E (live model). Exit 0/1. |
| `Rochas.OpenCodeBridge.Web.Test` | Console suite for the **Web UI**: unit + HTTP integration. Exit 0/1. |

> `dotnet test` is not used: both test projects are console runners
> (`dotnet run --project …`, exit 0 = PASS, 1 = FAIL).

### Topology and ports

| name | model | llama | bridge | notes |
|---|---|---|---|---|
| **GPU1** | `Gemma4-E4B-Q4` (`Q4_K_M` + mmproj Q8_0) | `:4100` | `:4124` | vision + tools; `-c 524288 -np 4` (131072/slot) |
| **GPU2** | `Gemma4-E4B-UD-Q3` (`IQ3_XXS`, text-only) | `:4110` | `:4125` | `-c 114688 -np 2` (2×56k), 100 % GPU |
| **CPU forced** | `Gemma4-E4B-Q4` (`-ngl 0`) | `:4120` | `:4126` | explicit test scenario, not a fallback |
| **Web UI** | — | — | `:4130` | MVC + SQLite; `BRIDGE_WEB_PORT` |

```bash
# GPU1 — bridge lane (harness + tails + anti-repeat), the production profile
dotnet Rochas.OpenCodeBridge.dll --port 4124 --upstream http://127.0.0.1:4100 \
  --model Gemma4-E4B-Q4 --api-key guest@123 --thinking events \
  --mode bridge --main-harness 1 --harness-tails 1 --tail-scope all \
  --temperature 0.2 --max-tokens 16384 \
  --anti-repeat 1 --anti-repeat-window 512 --anti-repeat-retry 1 \
  --breaker-soft 3 --breaker-hard 6 --diagnostic 1 --diagnostic-level metrics
```

> `--mode bridge --main-harness 0` is an **invalid combination**: the process does
> not start and returns `[bridge] ERRO DE ARGUMENTOS: modo bridge exige main
> harness ON` (exit 2). Without our harness only the `--mode opencode` lane exists.

### Install

```bash
dotnet add package Rochas.OpenCodeBridge
```

---

### Features

#### 1) Responses ⇄ chat/completions translation

**Why it exists.** OpenCode speaks `/v1/responses`; llama.cpp and vLLM speak
`/v1/chat/completions`. Three contracts collide in one turn: `input` vs
`messages`, `function_call` vs `tool_calls`, reasoning as items vs
`reasoning_content` in the delta. Without translation the client cannot talk to
the backend; without a faithful round trip the model loses history, tools and
reasoning.

**How it solves it.** `ResponsesTranslation` (Core) is the single translation
routine: a leading `system` in `input` is promoted to `instructions`
(`TakeLeadingSystem`), `input` becomes `messages` (`InputToMessages`), tools are
renamed to the canonical alias (`CanonToolAlias`) and the chat answer is turned
back into a Responses object. In native bypass (`--passthrough-responses`)
`reasoning` items are stripped (`StripReasoningItems`) because llama rejects the
field. The stream is re-emitted delta by delta (or batched at 256 chars) and
`usage`/`timings` are preserved in the right place of each contract.

**Console.**

```bash
... --passthrough-responses true
... --stream-per-delta 1        # 0 = aggregate in 256-char blocks
```

**API.**

```bash
# dry-run: shows the /v1/chat/completions that WOULD be sent — no model call
curl -s -X POST http://127.0.0.1:4124/api/translate \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4-build","input":"ping"}' \
  | jq '{requested_model,temperature,temperature_source,message_count}'

# bypass dry-run: input WITHOUT reasoning items
curl -s -X POST http://127.0.0.1:4124/api/sanitize \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"input":[{"type":"reasoning","summary":[]},{"type":"message","role":"user","content":"ping"}]}' \
  | jq '{dropped_reasoning,system_normalized,input}'

# return dry-run: ready chat/completions -> Responses object
curl -s -X POST http://127.0.0.1:4124/api/convert \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4","completion":{"choices":[{"message":{"role":"assistant","content":"pong"}}]}}' \
  | jq '.output[0].content[0].text'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var responses = JsonNode.Parse(body)!.AsObject();

string? leading = ResponsesTranslation.TakeLeadingSystem(responses); // -> instructions
JsonArray messages = ResponsesTranslation.InputToMessages(responses);
int dropped = ResponsesTranslation.StripReasoningItems(responses);  // native bypass

string text = ResponsesTranslation.ExtractResponseText(chatNode);
JsonArray toolNames = ResponsesTranslation.ToolNames(chatNode);
```

---

#### 2) Role profile by model-id suffix

**Why it exists.** Planning, building and executing need different sampling. A
single global `--temperature` serves all three badly: too hot in build produces
the wrong file, too cold in plan kills exploration. One process per role wastes
VRAM and breaks the client keep-alive.

**How it solves it.** `RoleProfiles` maps the model-id suffix (`-plan`, `-build`,
`-orch`, `-moe`, `-scripter`, `-exec`) to temperature + thinking mode, resolved
per request — one process serves every role. Precedence is single and applies to
all temperature: **client > role profile > global**. `Sampling.Temperature`
returns value **and source** (`request`/`profile`/`global`), so telemetry never
lies about where the number came from. `--aux-temp` is the global analog for the
**retry turn** and follows the same rule (see feature 10).

**Console.**

```bash
... --temperature 0.2
... --thinking-off-roles Build,Scripter,Exec
```

| role | temperature | thinking |
|---|---|---|
| `plan` | 0.5 | on |
| `orch` | 0.5 | on |
| `moe` | 0.5 | on |
| `build` | 0.2 | off |
| `scripter` | 0.1 | off |
| `exec` | 0.1 | off |

**API.**

```bash
curl -s -X POST http://127.0.0.1:4124/api/translate \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4-build","input":"ping"}' \
  | jq '{requested_model,temperature,temperature_source,thinking}'
# {"requested_model":"Gemma4-E4B-Q4-build","temperature":0.2,"temperature_source":"profile","thinking":"off"}
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var profiles = RoleProfiles.DefaultCopy();
if (RoleProfiles.TryResolve("Gemma4-E4B-Q4-build", profiles, out var profile))
{
    double temp = profile.Temperature;   // 0.2 by profile
    string thinking = profile.Thinking;  // "off"
}
string role = RoleProfiles.RoleOf("Gemma4-E4B-Q4-build", profiles); // "Build"

var (value, source) = Sampling.Temperature(null, hasProfile: true, profileTemp: 0.2, globalTemp: 0.3);
// value = 0.2, source = "profile"
```

---

#### 3) A single system per agentic loop (main harness + dedup)

**Why it exists.** In the agentic loop the client echoes back the previous turn's
system — already composed by the harness. Recomposed on top, the harness lands
inside the "role system", tails stack, and context grows every turn until the slot
overflows. On top of that, the client system usually carries the opencode prompt,
which does not teach the local model's tool contract.

**How it solves it.** `MainHarness` owns the managed-path system: it replaces the
client text with the base prompt while keeping `<env>`, the skills block and the
date. `SystemPromptDedup` (Core) strips harness and tails already injected, and
`MainHarness.CollapseSystems` folds scattered `system`/`developer` entries into a
**single** `system` per request. Composition is idempotent: twice equals once. The
role line stays in the system (`SYSTEM DO ROLE (…)`) and the `tools` array is the
only schema source — text never duplicates the contract.

**Console.**

```bash
... --mode bridge --main-harness 1      # bridge lane: our harness (required)
... --mode opencode --main-harness 0    # client self-manages, bridge passes through
... --bridge-managed true               # force managed flow for every client
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/test/last-system \
  | jq '{path,lane,chars,main_harness,has_tools,tools_tail,tail_scope,temperature,has_profile}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

string? collapsed = MainHarness.CollapseSystems(messages);           // one system
bool replaced = MainHarness.Apply(messages, enabled: true, role: "Build");
bool hasHarness = SystemPromptDedup.HasHarness(system);
string clean = SystemPromptDedup.Clean(system);                      // harness + tails out
```

---

#### 4) Harness tails (behavior + tools)

**Why it exists.** The base prompt says what to do; it does not say how this line
works: which tool to call, what path shape is legal, what to do when the target
is missing, how to validate a script before claiming "done". Without it a 4–9B
model invents fields, repeats calls and answers prose instead of an artifact.

**How it solves it.** `HarnessTails` appends two deltas: **behavior** (how to act
and deliver) and **tools** (how to call). The tools tail is **built from the
request schema** (`ComposeTools`), so it never teaches a tool the request did not
declare. Without a `tools` array, `DefaultBehaviorNoTools` is used — otherwise the
model answers with tool JSON as text. Recognition uses a stable header
(`HarnessTails.HasToolsTail`), not whole-text equality: composition varies with
the schema, the sentinel does not. `--tail-scope` decides where tails apply
(`managed`/`all`/`off`).

**Console.**

```bash
... --harness-tails 1 --tail-scope all \
    --harness-behavior-tail "Validate scripts with bash -n; deliver evidence." \
    --harness-tools-tail "Use the dedicated tool; never invent fields."
```

**API.**

```bash
curl -s http://127.0.0.1:4124/api/harness \
  | jq '{tail_scope,harness_tails,tails_managed,behavior_chars,tools_chars}'

curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/test/last-system \
  | jq '{has_tools,tools_tail,tail_scope,chars}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

string toolsTail = HarnessTails.ComposeTools(new[] { "read", "write", "edit" });
string system = HarnessTails.Compose(clientSystem, harness: true,
    behavior: HarnessTails.DefaultBehavior, tools: toolsTail, hasTools: true);

bool ok = HarnessTails.HasToolsTail(system, toolsTail);   // stable sentinel
```

---

#### 5) Tool calling: single catalog and text coercion

**Why it exists.** A small local model sometimes emits native `tool_calls`,
sometimes writes the call as prose (`[Assistant tool call]: write({...})`),
sometimes wraps it in `<tool_call>` or in the Code Mode envelope
`execute{tools.X}`. Each OpenAI client redeclares the schema its own way; two
copies of the catalog mean two truths.

**How it solves it.** `ToolCatalog` is the single schema source (chat and
Responses both come from it; consumers never redeclare). `ToolCoercion` rescues
the written call: extracts it from content, converts it into `tool_calls` on the
request and strips control tags from the visible answer. `--upstream-parser`
declares backend capability (native or text) and coercion is decided by
capability, not by model name.

**Console.**

```bash
... --upstream-parser none --coerce-text-tools 1     # coerced line
... --coerce-narrated 1 --tool-strategy 1             # narrated call + Code Mode
... --tool-shim json --tool-shim-max-string 2000 --tool-shim-min-calls 0
```

**API.**

```bash
curl -s -X POST http://127.0.0.1:4124/api/translate \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4","input":"ping","tools":[{"type":"function","function":{"name":"read","description":"read a file","parameters":{"type":"object","properties":{"path":{"type":"string"}}}}}]}' \
  | jq '{tool_count, tools: [.chat_request.tools[].function.name]}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

JsonArray chatTools = ToolCatalog.Chat();
JsonArray responsesTools = ToolCatalog.Responses();
List<string> known = chatTools
    .Select(t => t?["function"]?["name"]?.GetValue<string>() ?? "")
    .Where(n => n.Length > 0).ToList();

List<(string Name, string Arguments)> calls = ToolCoercion.ExtractTextToolCalls(content, known);
int synthesized = ToolCoercion.CoerceChatCalls(root, known);
string visible = ToolCoercion.StripControlTags(text, out _);
```

---

#### 6) Executing tools inside the workspace

**Why it exists.** The model chooses the tool, but **execution** must be one
routine. If every consumer (Web, CLI, test) implements `read`/`write`/`edit`/
`glob`/`shell`, relative paths, output limits and out-of-root refusal diverge —
and one of them writes outside the repository.

**How it solves it.** `BridgeToolExecutor` runs in Core with `WorkspaceGuard` as
gatekeeper: paths are relative to the root (`--workspace`, or per-call
`workspace`), a missing file returns the real path (so the model corrects itself
instead of repeating the call), and nothing escapes the root.
`POST /api/tools/execute` is the single door — the Web delegates here instead of
keeping its own executor.

**Console.**

```bash
... --workspace /media/…/Projetos/Git/OpenCodeBridge --tool-output-limit 8000
```

**API.**

```bash
curl -s -X POST http://127.0.0.1:4124/api/tools/execute \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"name":"read","arguments":{"path":"README.md"},"role":"Build","timeout":60}' \
  | jq '{ok, chars: (.output|length), error}'

# a missing file returns the REAL path
curl -s -X POST http://127.0.0.1:4124/api/tools/execute \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"name":"read","arguments":{"path":"readme.md"},"role":"Build"}' | jq '.error'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core.Tools;

var executor = new BridgeToolExecutor("/media/…/repo", "http://127.0.0.1:4100",
    "guest@123", log: null, role: "Build");

ToolResult result = executor.Execute("write", """{"path":"src/a.txt","content":"x"}""", 60);
bool okPath = WorkspaceGuard.TryResolve("/media/…/repo", "src/a.txt", out string full, out string why);
```

---

#### 7) Per-role tool permission (blacklist and risky commands)

**Why it exists.** Planning must not write files; an `rm`/`git push` in the
middle of an autonomous turn is expensive. But hiding the tool from the schema
makes the loop worse: the model tries what does not exist and hallucinates
arguments.

**How it solves it.** The project rule is **block the call, not hide the tool**:
the `tools` array goes complete and policy decides at execution time. The role
system states the prohibition **by action** (`MainHarness.PermissionsLine`), so
the model knows before trying. A risky command becomes a **question**
(`--risk-ask`) or is denied, within an approval window; `--tool-blacklist` blocks
by role and `--risk-tools-alerts` matches regex per role. The decision is **one**:
`POST /api/permission/check`.

**Console.**

```bash
... --tool-blacklist "Plan=write,edit,exec,install,git;MoE=write,edit;Orch=write,edit" \
    --risk-ask 1 --risk-ask-minutes 30 \
    --risk-tools-alerts "Plan=git\s+push,\brm\b;Orch=\bdd\b"
```

**API.**

```bash
curl -s -X POST http://127.0.0.1:4124/api/permission/check \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4-plan","tool":"write","arguments":{"path":"a.txt"}}' \
  | jq '{action,role,tool,reason,notice}'
```

**C# (consumer).**

```csharp
// policy lives in one place (the bridge); the consumer asks over HTTP instead of
// reimplementing a permission table locally.
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4124") };
http.DefaultRequestHeaders.Authorization = new("Bearer", "guest@123");
string body = """{"model":"Gemma4-E4B-Q4-plan","tool":"write","arguments":{"path":"a.txt"}}""";
using var resp = await http.PostAsync("/api/permission/check",
    new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
var verdict = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
Console.WriteLine($"{verdict["action"]} — {verdict["reason"]}");
```

---

#### 8) Content anti-repeat

**Why it exists.** A small local model loops verbatim: same paragraph, same
reasoning sentence, same explanation. The turn burns tokens up to the cap and the
client receives garbage — and the next turn repeats the pattern.

**How it solves it.** `ContentAntiRepeat` compares the **normalized tail** of the
streamed text (window in chars). On detection the bridge **aborts the upstream
stream** (it does not wait for 16k tokens), does **1 retry** with a soft notice in
history and drops the repeated window. If the loop returns, the turn ends with the
notice.

**Console.**

```bash
... --anti-repeat 1 --anti-repeat-window 512 \
    --anti-repeat-abort 1 --anti-repeat-retry 1 \
    --anti-repeat-notice "Rephrase the reasoning; do not repeat the same paragraph."
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics \
  | jq '{anti_repeat_content, anti_repeat_tools, upstream_abort, upstream_abort_retry}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var reply = new StreamAntiRepeatReply(window: 512);
string acc = "";
foreach (string delta in deltas)
{
    acc += delta;
    if (reply.IsRepeat(acc)) break;   // abort upstream, retry with notice
}

string norm = ContentAntiRepeat.Normalize("  Hello   WORLD ");
double ratio = ContentAntiRepeat.Similarity("abc", "abd");          // ~0.66
int dupAt = ContentAntiRepeat.TailDuplicateAt(acc, window: 512);    // -1 = no loop
```

---

#### 9) Tool breaker (repeated failure on the same target)

**Why it exists.** The most expensive loop is not text: it is a **tool on the same
target** with slightly different arguments. `edit` with a stale `oldString`
repeated 15 times burns the whole battery and the model does not change plan by
itself.

**How it solves it.** `ToolsBreaker` counts consecutive failures **on the same
target** (`ToolSig.Target` drops digits/separators, so `a[1..3]` and `a[2..4]` are
the same target). **Soft** injects a notice into history; **hard** ends the turn.
`RepeatedCall` finds identical calls and `RepeatedWritePath` covers rewriting the
same file. The notice teaches the right form and never shows the invalid literal
(the model copies what the hint shows).

**Console.**

```bash
... --breaker-soft 3 --breaker-hard 6
```

**API.**

```bash
# the breaker verdict shows up in the log (tool-error-breaker event); policy
# denials and nudges are also counted in /api/metrics
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics \
  | jq '{nudge_args, ring2_deny, ring3_deny}'
grep -o '"event":"tool-error-breaker","verdict":"[a-z]*"' /tmp/bridge-gpu.log | tail -3
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var breaker = new ToolsBreaker(soft: 3, hard: 6);
BreakerResult verdict = breaker.Evaluate(messages);
if (verdict.Verdict == BreakerVerdict.Nudge) breaker.ApplyNudge(messages, verdict.Notice);
if (verdict.Verdict == BreakerVerdict.Trip) return;   // end the turn

var repeated = new RepeatedCall(minCount: 2).Find(messages);
string sig = ToolSig.Of("write", argsJson);
string target = ToolSig.Target("write", argsJson);
bool failed = ToolSig.IsFailure(toolOutput);
```

---

#### 10) Upstream abort retry and detour temperature

**Why it exists.** `llama-server` aborts the connection on long prefill,
incompatible grammar or exceeded context. To the client that is an opaque 500 —
and the whole turn (sometimes 375 s of prefill) is lost. Resending the same body
at the same temperature tends to repeat the same output.

**How it solves it.** `RetryNudge` detects the abort and does **1 retry** with a
nudge in history ("another approach, fewer steps per call, concise writing") and a
**detour temperature**. The detour reference follows the **same precedence as the
turn temperature**: the role profile wins when the id carries a role; without a
role the host global value (`--aux-temp`) applies; without it, the detour profile
(`plan`) — and it never cools a turn that was already hotter. A second failure
becomes a structured `502 upstream_aborted` with its own counter.

**Console.**

```bash
... --aux-temp 0.5     # host global for the detour (no-role case)
... --temperature 0.2  # without --aux-temp, detour comes from the `plan` profile
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status | jq '{aux_temp,temperature}'
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics | jq '{upstream_abort,upstream_abort_retry}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

double reference = Sampling.DetourReference(hasRole: false, profiles: null, auxTemp: 0.5);
double retryTemp = Sampling.ForRetry(current: 0.1, detourReference: reference);   // 0.5
string? retryBody = RetryNudge.WithAbortNudge(bodyJson, 0.1, reference);
bool abort = RetryNudge.IsUpstreamAbort(exception);
```

---

#### 11) Context guard and token budget

**Why it exists.** llama divides total `-c` by `-np` slots: 524288 total, 131072
per slot. Exceeding it does not warn — it returns `request exceeds the available
context size` and **core dumps the server**. A turn that overflows mid-prefill
costs minutes of GPU.

**How it solves it.** Two routines with distinct roles: `ContextGuard.Tripped`
measures the **slot** fraction (not the total) over `usage.prompt_tokens` and the
bridge returns `413` before the hard 400; `ContextGuard.Compact` cuts old history
while keeping recent turns coherent (tool calls and outputs stay together).
`--context-slots` is what makes the math per slot. Output budget is separate:
`--max-tokens`, `--coerced-max-tokens` and `--tool-output-limit`.

**Console.**

```bash
... --context-tokens 524288 --context-slots 4 --context-threshold 0.95 --context-guard 1 \
    --max-tokens 16384 --coerced-max-tokens 16384 --tool-output-limit 8000
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status \
  | jq '{context_tokens,context_slots,context_threshold,context_guard,max_tokens}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var settings = new ContextGuardSettings(Enabled: true, Threshold: 0.95, Tokens: 524288, Slots: 4);
bool trips = ContextGuard.Tripped(upstreamJson, settings, out int promptTokens, out double fraction);
bool compacted = ContextGuard.Compact(messages, contextTokens: 524288, slots: 4, threshold: 0.95);
```

---

#### 12) Auth, users and remote tuning

**Why it exists.** The bridge listens on `0.0.0.0` on a LAN machine. Without auth
anyone on the Wi-Fi executes tools on your disk. And tuning the line (temperature,
cap) must not require a restart — a restart drops the HTTP pool mid-run.

**How it solves it.** `--api-key` is the Bearer required from the client and sent
to the upstream (empty = no auth). JWT (`--jwt-secret`, `--jwt-expiry`)
authenticates the Web and the dashboard; `--users-db` is the users SQLite;
`POST /api/auth/login` returns a token with expiry and an admin flag.
`POST /api/config` adjusts thinking/temperature/caps **partially** without
restart and returns the new `/api/status`.

**Console.**

```bash
... --api-key guest@123 --jwt-secret "0123456789abcdef" --jwt-expiry 720 \
    --users-db /var/lib/opencodebridge/users.db
```

**API.**

```bash
curl -s -X POST http://127.0.0.1:4124/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@local","password":"…"}' | jq '{token: (.token[0:20]), expires_in, admin}'

curl -s -X POST http://127.0.0.1:4124/api/config \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"temperature":0.2,"max_tokens":16384}' | jq '{temperature,max_tokens,thinking}'
```

**C# (consumer).**

```csharp
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4124") };
http.DefaultRequestHeaders.Authorization = new("Bearer", Environment.GetEnvironmentVariable("BRIDGE_KEY"));
using var status = await http.GetAsync("/api/status");
var snapshot = System.Text.Json.Nodes.JsonNode.Parse(await status.Content.ReadAsStringAsync())!;
Console.WriteLine($"{snapshot["status"]} {snapshot["model"]} {snapshot["temperature"]}");
```

---

#### 13) Observability: status, metrics, probe and panel

**Why it exists.** Without telemetry, "the test failed" cannot tell a bad model
from a bad environment. A counter that only resets on restart forces a bridge
restart mid-battery — and the current run numbers are lost with it.

**How it solves it.** `/api/status` is the config + telemetry snapshot (port,
upstream, model, effective flags, `aux_temp`, window/slots, last client).
`/api/metrics` brings throughput, tools, files and abort/breaker counters. The
Serilog probe (`--diagnostic`, `--diagnostic-level`) keeps samples at
`/api/test/probe`. `POST /api/diag/reset` **resets without downtime**
(`?probe=1&files=1&panel=1`), so restarts stay reserved for a new binary.
`POST /api/test/step` publishes the running step (`verdict: RUN`) and `step_end`
overwrites it with PASS/FAIL/SKIP.

**Console.**

```bash
... --diagnostic 1 --diagnostic-level metrics --log /tmp/bridge-gpu.log
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status \
  | jq '{status,version,model,mode,tail_scope,main_harness,harness_tails,temperature,aux_temp}'
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics | jq 'keys'

curl -s -X POST -H 'Authorization: Bearer guest@123' \
  'http://127.0.0.1:4124/api/diag/reset?probe=1&files=1&panel=1' | jq
# {"ok":true,"event":"diag-reset"}

curl -s -X POST -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"step":"E-tools-full","mode":"bridge","verdict":"RUN"}' \
  http://127.0.0.1:4124/api/test/step | jq

curl -s -H 'Authorization: Bearer guest@123' 'http://127.0.0.1:4124/api/test/steps?war=1' | jq '.[-2:]'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

Metrics.Record("tool", new JsonObject { ["tool"] = "write", ["ok"] = true });
JsonObject snapshot = Metrics.Snapshot();
```

---

#### 14) Web UI (chat, agents, tool loop)

**Why it exists.** Testing the line through the opencode TUI does not show agent
state (agents, sessions, tool calls, risky-command confirmation). And the Web
needed a tool executor — which already exists in the bridge.

**How it solves it.** The Web (MVC + SQLite) chats with a **Responses bypass**,
runs the native tool loop (`function_call` → executor → `function_call_output`)
and delegates everything that is already Core: schema (`ToolCatalog`), policy
(`/api/permission/check`), execution (`/api/tools/execute`), metrics
(`/api/metrics`) and harness. None of it is reimplemented in the Web. Port via
`--port`/`Web:Port` in `appsettings.json` or `BRIDGE_WEB_PORT`.

**Console.**

```bash
BRIDGE_WEB_PORT=4130 BRIDGE_WEB_DB_DIR=/var/lib/opencodebridge \
  dotnet run --project Rochas.OpenCodeBridge.Web -c Release
```

**API.**

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:4130/
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/harness | jq '.tools_chars'
```

**C# (Core, from the Web side).**

```csharp
using Rochas.OpenCodeBridge.Core;
using Rochas.OpenCodeBridge.Core.Tools;

JsonArray tools = ToolCatalog.Responses();
var executor = new BridgeToolExecutor(workspaceRoot, upstream, apiKey, log, role);
ToolResult result = executor.Execute("glob", """{"pattern":"**/*.cs"}""", 60);
```

---

#### 15) `gate` subcommand, tests and WAR

**Why it exists.** An agent is only "done" when the artifact exists and builds —
"it should work" is not delivery. And the suite needs two artifacts: the fast one
(no model, always runs) and the E2E one (live model, expensive, runs in battery).

**How it solves it.** The same binary has the `gate <repo> "<prompt>"`
subcommand: it runs the agent and returns exit 0/1 with the verdict. The console
suite (`Rochas.OpenCodeBridge.Test`) separates unit from E2E by artifact; the WAR
(`--war-level 1..4`, `--war-all`) organizes steps in levels with fail-fast and
output under `logs/levels`. Any test that uses tools runs with the tools tail on
(`--harness-tails 1`, `--tail-scope all|managed`).

**Console.**

```bash
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --api-key guest@123 --skip-e2e
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-level 1 --api-key guest@123
Rochas.OpenCodeBridge gate /path/to/repo "implement X and validate with dotnet build"
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' 'http://127.0.0.1:4124/api/test/steps?war=1' \
  | jq '[.[] | {step,verdict,ms}] | .[-5:]'
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/test/probe/latest \
  | jq '.'
```

**C# (Core).**

```csharp
// the gate lives in the binary (runs the agent and decides); Core exposes the
// pieces it uses — harness, tails, anti-repeat, breaker.
string system = HarnessTails.Compose(clientSystem, harness: true,
    behavior: HarnessTails.DefaultBehaviorNoTools, tools: null, hasTools: false);
bool ok = HarnessTails.HasToolsTail(system, HarnessTails.DefaultTools);
```

---

### Quick flag reference

Boolean flags accept `true/false` and `1/0`. Each flag is explained in its feature
above; this table is lookup only.

#### A) Network / process

| Argument | Default | Effect | Example |
|---|---|---|---|
| `--port` | `4124` | HTTP port. | `--port 4125` |
| `--listen` / `--host` | `0.0.0.0` | Listen interface (`--host` is an alias). | `--listen 127.0.0.1` |
| `--upstream` | `http://127.0.0.1:4100` | Base URL of the OpenAI-compatible backend. | `--upstream http://127.0.0.1:4110` |
| `--model` | `Gemma4-E4B-Q4` | Served/reported model id. | `--model Gemma4-E4B-UD-Q3` |
| `--log` | `/tmp/opencodebridge.log` | JSONL log path. | `--log /tmp/bridge-gpu2.log` |
| `--workspace` | process cwd | Root of `POST /api/tools/execute`. | `--workspace /media/…/repo` |

#### B) Auth / admin

| Argument | Default | Effect | Example |
|---|---|---|---|
| `--api-key` | empty | Bearer required from the client and sent upstream. Empty = no auth. | `--api-key guest@123` |
| `--jwt-secret` | built-in | JWT HMAC secret (min. 16 chars). | `--jwt-secret "0123456789abcdef"` |
| `--jwt-expiry` | `720` | JWT lifetime in minutes. | `--jwt-expiry 60` |
| `--users-db` | app data | Users SQLite path. | `--users-db /tmp/users.db` |
| `--bridge-managed` | `false` | Force managed flow for every client (opencode included). | `--bridge-managed true` |

#### C) Transport / compatibility

| Argument | Default | Effect | Example |
|---|---|---|---|
| `--passthrough-responses` | `false` | Native Responses passthrough to llama.cpp (strips reasoning items only). | `--passthrough-responses true` |
| `--stream-per-delta` | `true` | Emit each SSE delta instead of batching 256 chars. | `--stream-per-delta 0` |
| `--qwen-standard` | `false` | Legacy Qwen mode: synthesized ModelPrompt + Alibaba names. | `--qwen-standard 1` |
| `--thinking-format` / `--thinking` | `events` | Reasoning format: `events` (items) or `off` (drop). | `--thinking-format off` |
| `--thinking-off-roles` | empty | Force thinking off by role name. | `--thinking-off-roles Build,Scripter,Exec` |

#### D) Harness / prompt

| Argument | Default | Effect | Example |
|---|---|---|---|
| `--main-harness` | `true` | Replace the client system with `Core/MainHarness.Text` on the managed path. | `--main-harness 0` |
| `--harness-tails` | `true` | Enable both tails (behavior + tools). | `--harness-tails 0` |
| `--harness-behavior-tail` | `HarnessTails.DefaultBehavior` | Behavior tail (how to act/deliver). | `--harness-behavior-tail "Validate scripts with bash -n."` |
| `--harness-tools-tail` | `HarnessTails.DefaultTools` | Tools tail (how to call). | `--harness-tools-tail "Use the dedicated tool."` |
| `--tail-scope` | `managed` | Where tails apply: `managed`, `all`, `off`. | `--tail-scope all` |
| `--mode` | auto | `bridge` = MainHarness+tails+anti-repeat+fixes; `opencode` = client master + tails. | `--mode bridge` |
| `--breaker-soft` | `3` | Consecutive failures on the same target that inject the notice. | `--breaker-soft 2` |
| `--breaker-hard` | `6` | Consecutive failures that end the turn. | `--breaker-hard 4` |

#### E) Tool coercion

| Argument | Default | Effect | Example |
|---|---|---|---|
| `--upstream-parser` | `native` | Upstream capability: `native` (tool_calls) or `none` (text). | `--upstream-parser none` |
| `--coerce-text-tools` | `false` | Extract a call written as text (```json, `<tool_call>`, `<tools>`). | `--coerce-text-tools 1` |
| `--coerce-narrated` | `true` | Rescue `[Assistant tool call]: name({...})`. | `--coerce-narrated 0` |
| `--tool-strategy` | `true` | Code Mode rescue: `execute{tools.X}` → tool `X`. | `--tool-strategy 0` |
| `--tool-shim` | empty | `json` = drop tools, inject JSON envelope via grammar, convert back. | `--tool-shim json` |
| `--tool-shim-max-string` | `2000` | Max chars per grammar string (`0` = no cap). | `--tool-shim-max-string 4000` |
| `--tool-shim-min-calls` | `0` | History calls before allowing `{"final":…}`. | `--tool-shim-min-calls 1` |

#### F) Anti-repeat

| Argument | Default | Effect | Example |
|---|---|---|---|
| `--anti-repeat` | `true` | Watch `thinking`/`content`/tool-args for verbatim loops. | `--anti-repeat 0` |
| `--anti-repeat-abort` | `true` | Abort the upstream stream as soon as the detector fires. | `--anti-repeat-abort 0` |
| `--anti-repeat-retry` | `true` | One retry with the soft notice, dropping the repeated window. | `--anti-repeat-retry 0` |
| `--anti-repeat-window` | `256` | Compared window in normalized chars (`64..2048`). | `--anti-repeat-window 512` |
| `--anti-repeat-notice` | `ContentAntiRepeat.SoftNotice` | Soft retry notice text. | `--anti-repeat-notice "Rephrase the reasoning."` |

#### G) Budget / context

| Argument | Default | Effect | Example |
|---|---|---|---|
| `--max-tokens` | `0` | Output cap on the GPU line; `0` = upstream/client decides. | `--max-tokens 16384` |
| `--coerced-max-tokens` / `--cpu-max-tokens` | `0` | Cap on the coerced line; `0` = no cap. | `--cpu-max-tokens 8192` |
| `--context-guard` | `false` | Return 413 when the ctx threshold is reached. | `--context-guard 1` |
| `--context-threshold` | `0.95` | Per-slot ctx fraction (`0.5..0.99`). | `--context-threshold 0.90` |
| `--context-tokens` | `0` | Ctx size in tokens; `0` = guard disabled. | `--context-tokens 524288` |
| `--context-slots` | `1` | llama parallel slots (`-np`): math is per slot. | `--context-slots 4` |

#### H) Generation / artifacts

| Argument | Default | Effect | Example |
|---|---|---|---|
| `--temperature` | `0.3` | Global temperature when client/profile sends none. | `--temperature 0.2` |
| `--aux-temp` | — | Global temperature of the RETRY turn (detour). Same precedence as `--temperature`: a resolved role wins; without a role this value applies; without it, the detour profile (`plan`). | `--aux-temp 0.5` |
| `--tool-output-limit` | `0` | Trim each tool result to N chars; `0` = full. | `--tool-output-limit 8000` |
| `--sampling-passthrough` | `false` | Forward client sampling (`repeat_penalty`, `dry_*`, `top_p`, `seed`). | `--sampling-passthrough 1` |

#### I) Security / permissions

| Argument | Default | Effect | Example |
|---|---|---|---|
| `--risk-ask` | `true` | Risky command becomes a question; `0` = deny. | `--risk-ask 0` |
| `--risk-ask-minutes` | `30` | Approval window in minutes. | `--risk-ask-minutes 10` |
| `--risk-tools-alerts` | empty | Per-role regex for risky tools. | `--risk-tools-alerts "Plan=git\\s+push"` |
| `--tool-blacklist` | empty | Per-role call blacklist (tool stays announced). | `--tool-blacklist "Plan=write,edit,exec;MoE=write"` |

#### J) Observability

| Argument | Default | Effect | Example |
|---|---|---|---|
| `--diagnostic` | `false` | Enable the Serilog probe. | `--diagnostic 1` |
| `--diagnostic-level` | `off` | `off`, `metrics` (no bodies) or `dump` (full bodies). | `--diagnostic-level metrics` |

#### L) Legacy (never in production)

| Argument | Default | Effect | Example |
|---|---|---|---|
| `--legacy-harness` | `false` | Re-enable guardrail/strip/retry/clamp; prerequisite of text coercion. | `--legacy-harness 1` |

### API endpoints

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/v1/responses` | Responses contract (what OpenCode speaks) |
| `POST` | `/v1/chat/completions` | chat/completions contract |
| `GET` | `/v1/models` | list the served model |
| `GET` | `/api/status` | config + telemetry snapshot |
| `GET` | `/api/metrics` | throughput, tools, files, abort counters |
| `GET` | `/api/harness` | active tails (chars) and scope |
| `POST` | `/api/permission/check` | `allow`/`ask`/`deny` decision by role |
| `POST` | `/api/tools/execute` | execute a tool in the workspace |
| `POST` | `/api/translate` | dry-run: chat/completions that would be sent |
| `POST` | `/api/sanitize` | bypass dry-run: input without reasoning items |
| `POST` | `/api/convert` | return dry-run: chat/completions → Responses |
| `POST` | `/api/config` | partial runtime tuning (thinking/temp/cap) |
| `POST` | `/api/auth/login` | login → JWT |
| `POST` | `/api/diag/reset` | reset counters/probe/panel without restart |
| `GET` | `/api/test/last-system` | final system of the last request + composition proofs |
| `POST` | `/api/test/step` | publish the running step (`RUN`) |
| `GET` | `/api/test/steps` | published steps (`?war=1` only those with `mode`) |
| `GET` | `/api/test/probe/latest` | latest probe sample |

### Testing — who owns which artifact

| Suite | Owner | Run | Time |
|---|---|---|---|
| `Rochas.OpenCodeBridge.Test` | **Bridge/Core**: translation, profiles, anti-repeat, breaker, harness, auth, metrics + E2E | `dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --api-key guest@123 --skip-e2e` | seconds (unit) |
| `Rochas.OpenCodeBridge.Web.Test` | **Web**: hash/JWT, agents, SQLite CRUD, sessions, SSE, tool executor, confirm gate | `dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --web http://127.0.0.1:4130 --units-only` | ~3.4 s |
| Web integration | HTTP endpoints + chat stream (needs `:4130`) | `… --integration-only` | ~2–4 min (live model) |

Known debt: `Unit/AntiRepeatCoreTests.cs` and `Unit/DedupGuardTests.cs` exercise
Core types but live in Web.Test (they validate what the Web consumes). They should
move to `Rochas.OpenCodeBridge.Test` during the fusion.

```bash
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --skip-e2e --api-key guest@123
dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --web http://127.0.0.1:4130 --units-only

./scripts/run-e2e-pipeline.sh --phase 0   # health + VRAM
./scripts/run-e2e-pipeline.sh --phase 1   # unit
./scripts/run-e2e-pipeline.sh --phase 2   # science + mission
./scripts/run-e2e-pipeline.sh --phase 3   # opencode standalone
```

### Docs

- `docs/LAB-SETUP.md` — infra: units, llama flags, TurboQuant, VRAM, scenarios A–E, full flag/appsettings reference.
- `AGENTS.md` — operator rules.
- `docs/AGENTS-PROMPTS.md` — versioned system prompts.
- `HISTORY.md` — build history and measurements.
- `docs/RUNBOOK-E2E.md` — phases and environment state.
- `docs/BENCHTESTS.md` — batteries (`run-battery-dual.sh`, WAR, frontpipe).
- `docs/PENDENCIAS.md` / `docs/PENDINGS.md` — pending items.

---

## Español

**Puente HTTP en C# (.NET 10) entre la API `/v1/responses` de OpenCode y
cualquier backend compatible con OpenAI (llama.cpp, vLLM), más una UI web MVC
con su propio bucle de herramientas agéntico.** El núcleo usa solo la BCL
(`HttpListener` + `System.Text.Json`): traduce Responses ⇄ chat/completions,
trata `thinking` y tool calling, resuelve el perfil por sufijo del model id y
aplica el **main harness** y las **dos colas** (comportamiento + tools).

> **Descontinuación:** este proyecto será descontinuado y el puente se
> **fusionará con `AI.Manager`**. Hasta entonces, este código es la referencia
> y el arnés E2E.

Línea local de referencia: **Gemma 4 E4B** — `Gemma4-E4B-Q4` (**GPU1**,
`:4100`/`:4124`) y `Gemma4-E4B-UD-Q3` (**GPU2**, text-only, `:4110`/`:4125`),
ambos 100 % en GPU (`-ngl 99`) con TurboQuant (`turbo3` en el KV) dentro de
12 GB de VRAM (RTX 3060).

### Cómo está organizado este documento

Cada funcionalidad aparece **en un solo lugar**, con el mismo formato:

1. **Por qué existe** — el dolor real que resuelve.
2. **Cómo lo resuelve** — el mecanismo y dónde vive (Core, bridge, Web).
3. **Consola** — las flags que la activan/ajustan al arrancar.
4. **API** — el endpoint HTTP (y el cuerpo) cuando la funcionalidad es operativa
   en runtime.
5. **C#** — cómo consumir `Rochas.OpenCodeBridge.Core` desde tu código.

Al final está la **referencia rápida de argumentos** (todas las flags en una
tabla, solo para consulta); la explicación de cada una está en su funcionalidad.

### Componentes

| Ensamblado | Responsabilidad |
|---|---|
| `Rochas.OpenCodeBridge` | Traductor Responses ⇄ chat/completions, thinking, tool calling, perfiles de rol, harness, subcomando `gate`. |
| `Rochas.OpenCodeBridge.Core` | Biblioteca BCL pura: traducción, anti-repeat, coerción de tools, composición de harness, guarda de contexto, métricas. |
| `Rochas.OpenCodeBridge.Web` | UI MVC: chat con bypass de Responses, bucle de tools nativo, agentes, subagente, alcance. |
| `Rochas.OpenCodeBridge.Test` | Suite de consola de **bridge/Core**: unit (HTTP, sin modelo) + E2E (modelo vivo). Exit 0/1. |
| `Rochas.OpenCodeBridge.Web.Test` | Suite de consola de **Web**: unit + integración HTTP. Exit 0/1. |

> No se usa `dotnet test`: los dos proyectos de prueba son runners de consola
> (`dotnet run --project …`, exit 0 = PASS, 1 = FAIL).

### Topología y puertos

| nombre | modelo | llama | bridge | notas |
|---|---|---|---|---|
| **GPU1** | `Gemma4-E4B-Q4` (`Q4_K_M` + mmproj Q8_0) | `:4100` | `:4124` | visión + tools; `-c 524288 -np 4` (131072/slot) |
| **GPU2** | `Gemma4-E4B-UD-Q3` (`IQ3_XXS`, text-only) | `:4110` | `:4125` | `-c 114688 -np 2` (2×56k), 100 % GPU |
| **CPU forzada** | `Gemma4-E4B-Q4` (`-ngl 0`) | `:4120` | `:4126` | escenario de prueba explícito, no es fallback |
| **Web UI** | — | — | `:4130` | MVC + SQLite; `BRIDGE_WEB_PORT` |

```bash
# GPU1 — carril bridge (harness + colas + anti-repeat), el perfil de producción
dotnet Rochas.OpenCodeBridge.dll --port 4124 --upstream http://127.0.0.1:4100 \
  --model Gemma4-E4B-Q4 --api-key guest@123 --thinking events \
  --mode bridge --main-harness 1 --harness-tails 1 --tail-scope all \
  --temperature 0.2 --max-tokens 16384 \
  --anti-repeat 1 --anti-repeat-window 512 --anti-repeat-retry 1 \
  --breaker-soft 3 --breaker-hard 6 --diagnostic 1 --diagnostic-level metrics
```

> `--mode bridge --main-harness 0` es una **combinación inválida**: el proceso no
> arranca y devuelve `[bridge] ERRO DE ARGUMENTOS: modo bridge exige main
> harness ON` (exit 2). Sin nuestro harness solo existe el carril
> `--mode opencode`.

### Instalación

```bash
dotnet add package Rochas.OpenCodeBridge
```

---

### Funcionalidades

#### 1) Traducción Responses ⇄ chat/completions

**Por qué existe.** OpenCode habla `/v1/responses`; llama.cpp y vLLM hablan
`/v1/chat/completions`. Son tres contratos en el mismo turno: `input` vs
`messages`, `function_call` vs `tool_calls`, razonamiento en ítems vs
`reasoning_content` en el delta. Sin traducción el cliente no habla con el
backend; sin ida y vuelta fiel el modelo pierde historial, tools y razonamiento.

**Cómo lo resuelve.** `ResponsesTranslation` (Core) es la única rutina de
traducción: el `system` inicial del `input` sube a `instructions`
(`TakeLeadingSystem`), el input se vuelve `messages` (`InputToMessages`), las
tools se renombran al alias canónico (`CanonToolAlias`) y la respuesta de chat
vuelve como objeto Responses. En el bypass nativo (`--passthrough-responses`) los
ítems de `reasoning` se eliminan antes de reenviar (`StripReasoningItems`),
porque llama rechaza el campo. El stream se reemite delta a delta (o agregado en
256 chars) y `usage`/`timings` se conservan en el lugar correcto de cada
contrato.

**Consola.**

```bash
... --passthrough-responses true
... --stream-per-delta 1        # 0 = agrega en bloques de 256 chars
```

**API.**

```bash
# dry-run: muestra el /v1/chat/completions que SERÍA enviado — no llama al modelo
curl -s -X POST http://127.0.0.1:4124/api/translate \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4-build","input":"ping"}' \
  | jq '{requested_model,temperature,temperature_source,message_count}'

# dry-run del bypass: input SIN los ítems de reasoning
curl -s -X POST http://127.0.0.1:4124/api/sanitize \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"input":[{"type":"reasoning","summary":[]},{"type":"message","role":"user","content":"ping"}]}' \
  | jq '{dropped_reasoning,system_normalized,input}'

# dry-run de la vuelta: chat/completions LISTO -> objeto Responses
curl -s -X POST http://127.0.0.1:4124/api/convert \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4","completion":{"choices":[{"message":{"role":"assistant","content":"pong"}}]}}' \
  | jq '.output[0].content[0].text'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var responses = JsonNode.Parse(body)!.AsObject();

string? leading = ResponsesTranslation.TakeLeadingSystem(responses); // -> instructions
JsonArray messages = ResponsesTranslation.InputToMessages(responses);
int dropped = ResponsesTranslation.StripReasoningItems(responses);  // bypass nativo

string text = ResponsesTranslation.ExtractResponseText(chatNode);
JsonArray toolNames = ResponsesTranslation.ToolNames(chatNode);
```

---

#### 2) Perfil de rol por sufijo del model id

**Por qué existe.** Planificar, construir y ejecutar piden muestreos distintos.
Un único `--temperature` global sirve mal a los tres: demasiado caliente en build
genera el archivo equivocado, demasiado frío en plan mata la exploración. Un
proceso por rol desperdicia VRAM y rompe el keep-alive del cliente.

**Cómo lo resuelve.** `RoleProfiles` mapea el sufijo del model id (`-plan`,
`-build`, `-orch`, `-moe`, `-scripter`, `-exec`) a temperatura + modo de thinking,
resuelto por request — un proceso sirve todos los roles. La precedencia es única
y vale para toda temperatura: **cliente > perfil del rol > global**.
`Sampling.Temperature` devuelve valor **y procedencia**
(`request`/`profile`/`global`), así la telemetría no miente sobre el origen del
número. `--aux-temp` es el análogo global del **turno de retry** y sigue la misma
regla (ver funcionalidad 10).

**Consola.**

```bash
... --temperature 0.2
... --thinking-off-roles Build,Scripter,Exec
```

| rol | temperatura | thinking |
|---|---|---|
| `plan` | 0.5 | on |
| `orch` | 0.5 | on |
| `moe` | 0.5 | on |
| `build` | 0.2 | off |
| `scripter` | 0.1 | off |
| `exec` | 0.1 | off |

**API.**

```bash
curl -s -X POST http://127.0.0.1:4124/api/translate \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4-build","input":"ping"}' \
  | jq '{requested_model,temperature,temperature_source,thinking}'
# {"requested_model":"Gemma4-E4B-Q4-build","temperature":0.2,"temperature_source":"profile","thinking":"off"}
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var profiles = RoleProfiles.DefaultCopy();
if (RoleProfiles.TryResolve("Gemma4-E4B-Q4-build", profiles, out var profile))
{
    double temp = profile.Temperature;   // 0.2 por el perfil
    string thinking = profile.Thinking;  // "off"
}
string role = RoleProfiles.RoleOf("Gemma4-E4B-Q4-build", profiles); // "Build"

var (value, source) = Sampling.Temperature(null, hasProfile: true, profileTemp: 0.2, globalTemp: 0.3);
// value = 0.2, source = "profile"
```

---

#### 3) Un único system en el bucle agéntico (main harness + dedup)

**Por qué existe.** En el bucle agéntico el cliente devuelve, en cada turno, el
system del turno anterior — que ya pasó por el harness. Recompuesto encima, el
harness queda dentro del "system del rol", las colas se apilan y el contexto
crece cada vuelta hasta desbordar el slot. Además, el system del cliente suele
traer el prompt de opencode, que no enseña el contrato de tool del modelo local.

**Cómo lo resuelve.** `MainHarness` es dueño del system del camino gestionado:
cambia el texto del cliente por el prompt base, preservando `<env>`, el bloque de
skills y la fecha. `SystemPromptDedup` (Core) corta harness y colas ya inyectados,
y `MainHarness.CollapseSystems` junta `system`/`developer` dispersos en un
**único** `system` por request. La composición es idempotente: dos veces equivale
a una. La línea del rol sigue en el system (`SYSTEM DO ROLE (…)`) y el array
`tools` es la única fuente del schema — el texto no duplica el contrato.

**Consola.**

```bash
... --mode bridge --main-harness 1      # carril bridge: nuestro harness (obligatorio)
... --mode opencode --main-harness 0    # el cliente se autogestiona
... --bridge-managed true               # fuerza el flujo gestionado para todos
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/test/last-system \
  | jq '{path,lane,chars,main_harness,has_tools,tools_tail,tail_scope,temperature,has_profile}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

string? collapsed = MainHarness.CollapseSystems(messages);           // un solo system
bool replaced = MainHarness.Apply(messages, enabled: true, role: "Build");
bool hasHarness = SystemPromptDedup.HasHarness(system);
string clean = SystemPromptDedup.Clean(system);                      // fuera harness + colas
```

---

#### 4) Colas del harness (comportamiento + tools)

**Por qué existe.** El prompt base dice qué hacer; falta la **mecánica de la
línea**: qué tool llamar, qué forma de path es legal, qué hacer cuando el destino
no existe, cómo validar el script antes de decir "listo". Sin eso un modelo de
4–9B inventa campos, repite la misma call y entrega prosa en vez de artefacto.

**Cómo lo resuelve.** `HarnessTails` agrega dos deltas: **comportamiento** (cómo
actuar/entregar) y **tools** (cómo llamar). La cola de tools se **compone del
schema del request** (`ComposeTools`), así nunca enseña una tool que el pedido no
declaró. Sin array `tools` entra `DefaultBehaviorNoTools` — si no, el modelo
responde con el JSON de la tool como texto. El reconocimiento usa un encabezado
estable (`HarnessTails.HasToolsTail`), no la igualdad del texto completo: la
composición varía con el schema, la centinela no. `--tail-scope` decide dónde
valen (`managed`/`all`/`off`).

**Consola.**

```bash
... --harness-tails 1 --tail-scope all \
    --harness-behavior-tail "Valide scripts con bash -n; entregue evidencia." \
    --harness-tools-tail "Use la tool dedicada; nunca invente campos."
```

**API.**

```bash
curl -s http://127.0.0.1:4124/api/harness \
  | jq '{tail_scope,harness_tails,tails_managed,behavior_chars,tools_chars}'

curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/test/last-system \
  | jq '{has_tools,tools_tail,tail_scope,chars}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

string toolsTail = HarnessTails.ComposeTools(new[] { "read", "write", "edit" });
string system = HarnessTails.Compose(clientSystem, harness: true,
    behavior: HarnessTails.DefaultBehavior, tools: toolsTail, hasTools: true);

bool ok = HarnessTails.HasToolsTail(system, toolsTail);   // centinela estable
```

---

#### 5) Tool calling: catálogo único y coerción de texto

**Por qué existe.** El modelo local pequeño a veces emite `tool_calls` nativo, a
veces escribe la call en prosa (`[Assistant tool call]: write({...})`), a veces
la envuelve en `<tool_call>` o en el sobre Code Mode `execute{tools.X}`. Cada
cliente OpenAI redeclara el schema a su manera; dos copias del catálogo son dos
verdades.

**Cómo lo resuelve.** `ToolCatalog` es la fuente única del schema (chat y
Responses salen de aquí; el consumidor no redeclara). `ToolCoercion` rescata la
call escrita: la extrae del contenido, la convierte en `tool_calls` en el request
y quita las etiquetas de control de la respuesta visible. `--upstream-parser`
declara la capacidad del backend (nativo o texto) y la coerción se decide por
capacidad, no por nombre de modelo. El `execute{tools.X}` de Code Mode lo
resuelve `--tool-strategy`.

**Consola.**

```bash
... --upstream-parser none --coerce-text-tools 1     # línea coercida
... --coerce-narrated 1 --tool-strategy 1            # call narrada + Code Mode
... --tool-shim json --tool-shim-max-string 2000 --tool-shim-min-calls 0
```

**API.**

```bash
curl -s -X POST http://127.0.0.1:4124/api/translate \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4","input":"ping","tools":[{"type":"function","function":{"name":"read","description":"lee un archivo","parameters":{"type":"object","properties":{"path":{"type":"string"}}}}}]}' \
  | jq '{tool_count, tools: [.chat_request.tools[].function.name]}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

JsonArray chatTools = ToolCatalog.Chat();
JsonArray responsesTools = ToolCatalog.Responses();
List<string> known = chatTools
    .Select(t => t?["function"]?["name"]?.GetValue<string>() ?? "")
    .Where(n => n.Length > 0).ToList();

List<(string Name, string Arguments)> calls = ToolCoercion.ExtractTextToolCalls(content, known);
int synthesized = ToolCoercion.CoerceChatCalls(root, known);
string visible = ToolCoercion.StripControlTags(text, out _);
```

---

#### 6) Ejecución de tools en el workspace

**Por qué existe.** El modelo elige la tool, pero **quien ejecuta** debe ser una
rutina única. Si cada consumidor (Web, CLI, prueba) implementa `read`/`write`/
`edit`/`glob`/`shell`, el path relativo, el límite de salida y el rechazo fuera
de la raíz divergen — y uno de ellos escribe fuera del repositorio.

**Cómo lo resuelve.** `BridgeToolExecutor` corre en el Core con `WorkspaceGuard`
como portero: el path es relativo a la raíz (`--workspace`, o `workspace` por
llamada), un archivo inexistente devuelve el path real (para que el modelo se
corrija en vez de repetir la call) y nada sale de la raíz.
`POST /api/tools/execute` es la puerta única — la Web delega aquí en vez de
mantener su propio ejecutor.

**Consola.**

```bash
... --workspace /media/…/Projetos/Git/OpenCodeBridge --tool-output-limit 8000
```

**API.**

```bash
curl -s -X POST http://127.0.0.1:4124/api/tools/execute \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"name":"read","arguments":{"path":"README.md"},"role":"Build","timeout":60}' \
  | jq '{ok, chars: (.output|length), error}'

# un archivo inexistente devuelve el path REAL
curl -s -X POST http://127.0.0.1:4124/api/tools/execute \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"name":"read","arguments":{"path":"readme.md"},"role":"Build"}' | jq '.error'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core.Tools;

var executor = new BridgeToolExecutor("/media/…/repo", "http://127.0.0.1:4100",
    "guest@123", log: null, role: "Build");

ToolResult result = executor.Execute("write", """{"path":"src/a.txt","content":"x"}""", 60);
bool okPath = WorkspaceGuard.TryResolve("/media/…/repo", "src/a.txt", out string full, out string why);
```

---

#### 7) Permiso de tools por rol (blacklist y comandos de riesgo)

**Por qué existe.** Planificar no debe escribir archivos; un `rm`/`git push` en
medio de un turno autónomo cuesta caro. Pero esconder la tool del schema empeora
el bucle: el modelo intenta lo que no existe y alucina argumentos.

**Cómo lo resuelve.** La regla del proyecto es **bloquear la llamada, no esconder
la tool**: el array `tools` va completo y la política decide en la ejecución. El
system del rol enuncia la prohibición **por acción**
(`MainHarness.PermissionsLine`), así el modelo sabe antes de intentar. Un comando
de riesgo se vuelve **pregunta** (`--risk-ask`) o se niega, con ventana de
aprobación; `--tool-blacklist` bloquea por rol y `--risk-tools-alerts` casa regex
por rol. La decisión es **una**: `POST /api/permission/check`.

**Consola.**

```bash
... --tool-blacklist "Plan=write,edit,exec,install,git;MoE=write,edit;Orch=write,edit" \
    --risk-ask 1 --risk-ask-minutes 30 \
    --risk-tools-alerts "Plan=git\s+push,\brm\b;Orch=\bdd\b"
```

**API.**

```bash
curl -s -X POST http://127.0.0.1:4124/api/permission/check \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4-plan","tool":"write","arguments":{"path":"a.txt"}}' \
  | jq '{action,role,tool,reason,notice}'
```

**C# (consumidor).**

```csharp
// la política vive en un solo lugar (la bridge); el consumidor pregunta por HTTP
// en vez de reimplementar una tabla de permisos local.
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4124") };
http.DefaultRequestHeaders.Authorization = new("Bearer", "guest@123");
string body = """{"model":"Gemma4-E4B-Q4-plan","tool":"write","arguments":{"path":"a.txt"}}""";
using var resp = await http.PostAsync("/api/permission/check",
    new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
var verdict = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
Console.WriteLine($"{verdict["action"]} — {verdict["reason"]}");
```

---

#### 8) Anti-repetición de contenido

**Por qué existe.** Un modelo local pequeño entra en bucle verbatim: repite el
mismo párrafo, la misma frase de razonamiento, la misma explicación. El turno
quema tokens hasta el techo y el cliente recibe basura — y el siguiente turno
repite el patrón.

**Cómo lo resuelve.** `ContentAntiRepeat` compara la **cola normalizada** del
texto en stream (ventana en chars). Al detectar, la bridge **aborta el stream
upstream** (no espera 16k tokens), hace **1 retry** con un aviso suave en el
historial y descarta la ventana repetida. Si el bucle vuelve, el turno termina con
el aviso.

**Consola.**

```bash
... --anti-repeat 1 --anti-repeat-window 512 \
    --anti-repeat-abort 1 --anti-repeat-retry 1 \
    --anti-repeat-notice "Reformule el razonamiento; no repita el mismo párrafo."
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics \
  | jq '{anti_repeat_content, anti_repeat_tools, upstream_abort, upstream_abort_retry}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var reply = new StreamAntiRepeatReply(window: 512);
string acc = "";
foreach (string delta in deltas)
{
    acc += delta;
    if (reply.IsRepeat(acc)) break;   // aborta upstream y reintenta con aviso
}

string norm = ContentAntiRepeat.Normalize("  Hello   WORLD ");
double ratio = ContentAntiRepeat.Similarity("abc", "abd");          // ~0.66
int dupAt = ContentAntiRepeat.TailDuplicateAt(acc, window: 512);    // -1 = sin bucle
```

---

#### 9) Breaker de tools (fallo en el mismo destino)

**Por qué existe.** El bucle más caro no es de texto: es de **tool en el mismo
destino** con argumento ligeramente distinto. `edit` con `oldString` obsoleto
repetido 15 veces quema la batería entera y el modelo no cambia de plan solo.

**Cómo lo resuelve.** `ToolsBreaker` cuenta fallos seguidos **en el mismo
destino** (`ToolSig.Target` quita dígitos y separadores, así `a[1..3]` y `a[2..4]`
son el mismo destino). En **soft** inyecta un aviso en el historial; en **hard**
termina el turno. `RepeatedCall` encuentra llamadas idénticas y
`RepeatedWritePath` cubre la reescritura del mismo archivo. El aviso enseña la
forma correcta y nunca muestra el literal inválido (el modelo copia lo que la
pista muestra).

**Consola.**

```bash
... --breaker-soft 3 --breaker-hard 6
```

**API.**

```bash
# el veredicto del breaker sale en el log (evento tool-error-breaker); la
# política también cuenta rechazos y nudges en /api/metrics
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics \
  | jq '{nudge_args, ring2_deny, ring3_deny}'
grep -o '"event":"tool-error-breaker","verdict":"[a-z]*"' /tmp/bridge-gpu.log | tail -3
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var breaker = new ToolsBreaker(soft: 3, hard: 6);
BreakerResult verdict = breaker.Evaluate(messages);
if (verdict.Verdict == BreakerVerdict.Nudge) breaker.ApplyNudge(messages, verdict.Notice);
if (verdict.Verdict == BreakerVerdict.Trip) return;   // termina el turno

var repeated = new RepeatedCall(minCount: 2).Find(messages);
string sig = ToolSig.Of("write", argsJson);
string target = ToolSig.Target("write", argsJson);
bool failed = ToolSig.IsFailure(toolOutput);
```

---

#### 10) Retry de aborto del upstream y temperatura de desvío

**Por qué existe.** `llama-server` aborta la conexión en prefill largo, grammar
incompatible o contexto desbordado. Para el cliente eso es un 500 opaco — y el
turno entero (a veces 375 s de prefill) se pierde. Reenviar el mismo cuerpo a la
misma temperatura tiende a repetir la misma salida.

**Cómo lo resuelve.** `RetryNudge` detecta el aborto y hace **1 retry** con nudge
en el historial ("otro enfoque, menos pasos por call, escritura concisa") y una
**temperatura de desvío**. La referencia del desvío sigue la **misma precedencia
que la temperatura del turno**: el perfil del rol manda cuando el id lleva rol;
sin rol vale el valor global del host (`--aux-temp`); sin él, el perfil de desvío
(`plan`) — y nunca enfría un turno que ya estaba más caliente. Un segundo fallo
se vuelve `502 upstream_aborted` estructurado, con contador propio.

**Consola.**

```bash
... --aux-temp 0.5     # global del host para el desvío (caso sin rol)
... --temperature 0.2  # sin --aux-temp el desvío viene del perfil `plan`
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status | jq '{aux_temp,temperature}'
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics | jq '{upstream_abort,upstream_abort_retry}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

double reference = Sampling.DetourReference(hasRole: false, profiles: null, auxTemp: 0.5);
double retryTemp = Sampling.ForRetry(current: 0.1, detourReference: reference);   // 0.5
string? retryBody = RetryNudge.WithAbortNudge(bodyJson, 0.1, reference);
bool abort = RetryNudge.IsUpstreamAbort(exception);
```

---

#### 11) Guarda de contexto y presupuesto de tokens

**Por qué existe.** llama divide el `-c` total entre `-np` slots: 524288 en
total, 131072 por slot. Pasarse no genera aviso — genera `request exceeds the
available context size` y **core dump del servidor**. Un turno que desborda en
medio del prefill cuesta minutos de GPU.

**Cómo lo resuelve.** Dos rutinas con papeles distintos: `ContextGuard.Tripped`
mide la fracción del **slot** (no del total) sobre `usage.prompt_tokens` y la
bridge devuelve `413` antes del 400 seco; `ContextGuard.Compact` corta el
historial antiguo preservando los turnos recientes (tool calls y resultados
quedan coherentes). `--context-slots` es lo que hace la cuenta por slot. El
presupuesto de salida es aparte: `--max-tokens`, `--coerced-max-tokens` y
`--tool-output-limit`.

**Consola.**

```bash
... --context-tokens 524288 --context-slots 4 --context-threshold 0.95 --context-guard 1 \
    --max-tokens 16384 --coerced-max-tokens 16384 --tool-output-limit 8000
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status \
  | jq '{context_tokens,context_slots,context_threshold,context_guard,max_tokens}'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

var settings = new ContextGuardSettings(Enabled: true, Threshold: 0.95, Tokens: 524288, Slots: 4);
bool trips = ContextGuard.Tripped(upstreamJson, settings, out int promptTokens, out double fraction);
bool compacted = ContextGuard.Compact(messages, contextTokens: 524288, slots: 4, threshold: 0.95);
```

---

#### 12) Auth, usuarios y ajuste remoto

**Por qué existe.** La bridge escucha en `0.0.0.0` en una máquina de la red. Sin
auth, cualquiera en el Wi-Fi ejecuta tools en tu disco. Y ajustar la línea
(temperatura, techo) no puede exigir reinicio — el reinicio tira el pool HTTP en
medio de una corrida.

**Cómo lo resuelve.** `--api-key` es el Bearer exigido al cliente y enviado al
upstream (vacío = sin auth). JWT (`--jwt-secret`, `--jwt-expiry`) autentica la
Web y el panel; `--users-db` es el SQLite de usuarios; `POST /api/auth/login`
devuelve token con validez y flag de admin. `POST /api/config` ajusta
**parcialmente** thinking/temperatura/techo sin reiniciar y devuelve el
`/api/status` nuevo.

**Consola.**

```bash
... --api-key guest@123 --jwt-secret "0123456789abcdef" --jwt-expiry 720 \
    --users-db /var/lib/opencodebridge/users.db
```

**API.**

```bash
curl -s -X POST http://127.0.0.1:4124/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@local","password":"…"}' | jq '{token: (.token[0:20]), expires_in, admin}'

curl -s -X POST http://127.0.0.1:4124/api/config \
  -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"temperature":0.2,"max_tokens":16384}' | jq '{temperature,max_tokens,thinking}'
```

**C# (consumidor).**

```csharp
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4124") };
http.DefaultRequestHeaders.Authorization = new("Bearer", Environment.GetEnvironmentVariable("BRIDGE_KEY"));
using var status = await http.GetAsync("/api/status");
var snapshot = System.Text.Json.Nodes.JsonNode.Parse(await status.Content.ReadAsStringAsync())!;
Console.WriteLine($"{snapshot["status"]} {snapshot["model"]} {snapshot["temperature"]}");
```

---

#### 13) Observabilidad: status, métricas, sonda y panel

**Por qué existe.** Sin telemetría, "la prueba falló" no distingue modelo malo de
entorno equivocado. Un contador que solo se reinicia con restart obliga a
reiniciar la bridge en medio de la batería — y los números de la corrida actual se
pierden con ella.

**Cómo lo resuelve.** `/api/status` es la foto de config + telemetría (puerto,
upstream, modelo, flags efectivas, `aux_temp`, ventana/slots, último cliente).
`/api/metrics` trae caudal, tools, archivos y los contadores de aborto/breaker. La
sonda Serilog (`--diagnostic`, `--diagnostic-level`) guarda muestras en
`/api/test/probe`. `POST /api/diag/reset` **reinicia sin downtime**
(`?probe=1&files=1&panel=1`), así el restart queda reservado a binario nuevo.
`POST /api/test/step` publica el step en ejecución (`verdict: RUN`) y el
`step_end` lo sobrescribe con PASS/FAIL/SKIP.

**Consola.**

```bash
... --diagnostic 1 --diagnostic-level metrics --log /tmp/bridge-gpu.log
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status \
  | jq '{status,version,model,mode,tail_scope,main_harness,harness_tails,temperature,aux_temp}'
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/metrics | jq 'keys'

curl -s -X POST -H 'Authorization: Bearer guest@123' \
  'http://127.0.0.1:4124/api/diag/reset?probe=1&files=1&panel=1' | jq
# {"ok":true,"event":"diag-reset"}

curl -s -X POST -H 'Authorization: Bearer guest@123' -H 'Content-Type: application/json' \
  -d '{"step":"E-tools-full","mode":"bridge","verdict":"RUN"}' \
  http://127.0.0.1:4124/api/test/step | jq

curl -s -H 'Authorization: Bearer guest@123' 'http://127.0.0.1:4124/api/test/steps?war=1' | jq '.[-2:]'
```

**C# (Core).**

```csharp
using Rochas.OpenCodeBridge.Core;

Metrics.Record("tool", new JsonObject { ["tool"] = "write", ["ok"] = true });
JsonObject snapshot = Metrics.Snapshot();
```

---

#### 14) Web UI (chat, agentes, bucle de tools)

**Por qué existe.** Probar la línea por la TUI de opencode no muestra el estado
del agente (agentes, sesiones, tool calls, confirmación de comando de riesgo). Y
la Web necesitaba un ejecutor de tools — que ya existe en la bridge.

**Cómo lo resuelve.** La Web (MVC + SQLite) hace chat con **bypass de Responses**,
corre el bucle de tools nativo (`function_call` → ejecutor →
`function_call_output`) y delega lo que ya es Core: schema (`ToolCatalog`),
política (`/api/permission/check`), ejecución (`/api/tools/execute`), métricas
(`/api/metrics`) y harness. Nada de eso se reimplementa en la Web. Puerto vía
`--port`/`Web:Port` en `appsettings.json` o env `BRIDGE_WEB_PORT`.

**Consola.**

```bash
BRIDGE_WEB_PORT=4130 BRIDGE_WEB_DB_DIR=/var/lib/opencodebridge \
  dotnet run --project Rochas.OpenCodeBridge.Web -c Release
```

**API.**

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:4130/
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/harness | jq '.tools_chars'
```

**C# (Core, del lado de la Web).**

```csharp
using Rochas.OpenCodeBridge.Core;
using Rochas.OpenCodeBridge.Core.Tools;

JsonArray tools = ToolCatalog.Responses();
var executor = new BridgeToolExecutor(workspaceRoot, upstream, apiKey, log, role);
ToolResult result = executor.Execute("glob", """{"pattern":"**/*.cs"}""", 60);
```

---

#### 15) Subcomando `gate`, pruebas y WAR

**Por qué existe.** Un agente solo "termina" cuando el artefacto existe y
compila — "debería funcionar" no es entrega. Y la suite necesita dos artefactos:
el rápido (sin modelo, siempre corre) y el E2E (modelo vivo, caro, corre en
batería).

**Cómo lo resuelve.** El mismo binario tiene el subcomando
`gate <repo> "<prompt>"`: corre el agente y devuelve exit 0/1 con el veredicto.
La suite de consola (`Rochas.OpenCodeBridge.Test`) separa unit de E2E por
artefacto; el WAR (`--war-level 1..4`, `--war-all`) organiza los steps en niveles
con fail-fast y salida en `logs/levels`. Toda prueba que use tools corre con la
cola de tools activa (`--harness-tails 1`, `--tail-scope all|managed`).

**Consola.**

```bash
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --api-key guest@123 --skip-e2e
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-level 1 --api-key guest@123
Rochas.OpenCodeBridge gate /ruta/del/repo "implemente X y valide con dotnet build"
```

**API.**

```bash
curl -s -H 'Authorization: Bearer guest@123' 'http://127.0.0.1:4124/api/test/steps?war=1' \
  | jq '[.[] | {step,verdict,ms}] | .[-5:]'
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/test/probe/latest \
  | jq '.'
```

**C# (Core).**

```csharp
// el gate vive en el binario (corre el agente y decide); el Core expone las
// piezas que usa — harness, colas, anti-repeat, breaker.
string system = HarnessTails.Compose(clientSystem, harness: true,
    behavior: HarnessTails.DefaultBehaviorNoTools, tools: null, hasTools: false);
bool ok = HarnessTails.HasToolsTail(system, HarnessTails.DefaultTools);
```

---

### Referencia rápida de argumentos

Las flags booleanas aceptan `true/false` y `1/0`. Cada flag se explica en su
funcionalidad arriba; esta tabla es solo consulta.

#### A) Red / proceso

| Argumento | Default | Efecto | Ejemplo |
|---|---|---|---|
| `--port` | `4124` | Puerto HTTP. | `--port 4125` |
| `--listen` / `--host` | `0.0.0.0` | Interfaz de escucha (`--host` es alias). | `--listen 127.0.0.1` |
| `--upstream` | `http://127.0.0.1:4100` | URL base del backend compatible OpenAI. | `--upstream http://127.0.0.1:4110` |
| `--model` | `Gemma4-E4B-Q4` | Id del modelo servido/reportado. | `--model Gemma4-E4B-UD-Q3` |
| `--log` | `/tmp/opencodebridge.log` | Path del log JSONL. | `--log /tmp/bridge-gpu2.log` |
| `--workspace` | cwd del proceso | Raíz de `POST /api/tools/execute`. | `--workspace /media/…/repo` |

#### B) Auth / admin

| Argumento | Default | Efecto | Ejemplo |
|---|---|---|---|
| `--api-key` | vacío | Bearer exigido al cliente y enviado al upstream. Vacío = sin auth. | `--api-key guest@123` |
| `--jwt-secret` | embebido | Secreto HMAC del JWT (mín. 16 chars). | `--jwt-secret "0123456789abcdef"` |
| `--jwt-expiry` | `720` | Validez del JWT en minutos. | `--jwt-expiry 60` |
| `--users-db` | app data | Path del SQLite de usuarios. | `--users-db /tmp/users.db` |
| `--bridge-managed` | `false` | Fuerza el flujo gestionado para todo cliente. | `--bridge-managed true` |

#### C) Transporte / compatibilidad

| Argumento | Default | Efecto | Ejemplo |
|---|---|---|---|
| `--passthrough-responses` | `false` | Passthrough nativo de Responses (solo quita ítems reasoning). | `--passthrough-responses true` |
| `--stream-per-delta` | `true` | Emite cada delta SSE en vez de agregar 256 chars. | `--stream-per-delta 0` |
| `--qwen-standard` | `false` | Modo legado Qwen: ModelPrompt sintetizado + nombres Alibaba. | `--qwen-standard 1` |
| `--thinking-format` / `--thinking` | `events` | Formato del razonamiento: `events` u `off`. | `--thinking-format off` |
| `--thinking-off-roles` | vacío | Fuerza thinking off por nombre de rol. | `--thinking-off-roles Build,Scripter,Exec` |

#### D) Harness / prompt

| Argumento | Default | Efecto | Ejemplo |
|---|---|---|---|
| `--main-harness` | `true` | Cambia el system del cliente por `Core/MainHarness.Text`. | `--main-harness 0` |
| `--harness-tails` | `true` | Activa las dos colas (comportamiento + tools). | `--harness-tails 0` |
| `--harness-behavior-tail` | `HarnessTails.DefaultBehavior` | Cola de comportamiento. | `--harness-behavior-tail "Valide con bash -n."` |
| `--harness-tools-tail` | `HarnessTails.DefaultTools` | Cola de tools. | `--harness-tools-tail "Use la tool dedicada."` |
| `--tail-scope` | `managed` | Dónde valen las colas: `managed`, `all`, `off`. | `--tail-scope all` |
| `--mode` | auto | `bridge` = MainHarness+colas+anti-repeat; `opencode` = cliente master + colas. | `--mode bridge` |
| `--breaker-soft` | `3` | Fallos seguidos en el mismo destino que inyectan el aviso. | `--breaker-soft 2` |
| `--breaker-hard` | `6` | Fallos seguidos que terminan el turno. | `--breaker-hard 4` |

#### E) Coerción de tools

| Argumento | Default | Efecto | Ejemplo |
|---|---|---|---|
| `--upstream-parser` | `native` | Capacidad del upstream: `native` o `none` (texto). | `--upstream-parser none` |
| `--coerce-text-tools` | `false` | Extrae la llamada escrita como texto. | `--coerce-text-tools 1` |
| `--coerce-narrated` | `true` | Rescata `[Assistant tool call]: nombre({...})`. | `--coerce-narrated 0` |
| `--tool-strategy` | `true` | Rescate Code Mode: `execute{tools.X}` → tool `X`. | `--tool-strategy 0` |
| `--tool-shim` | vacío | `json` = quita tools, inyecta sobre JSON por grammar y convierte de vuelta. | `--tool-shim json` |
| `--tool-shim-max-string` | `2000` | Máx. de chars por string en la grammar (`0` = sin tope). | `--tool-shim-max-string 4000` |
| `--tool-shim-min-calls` | `0` | Calls en el historial antes de liberar `{"final":…}`. | `--tool-shim-min-calls 1` |

#### F) Anti-repetición

| Argumento | Default | Efecto | Ejemplo |
|---|---|---|---|
| `--anti-repeat` | `true` | Observa `thinking`/`content`/tool-args por bucle verbatim. | `--anti-repeat 0` |
| `--anti-repeat-abort` | `true` | Aborta el stream upstream en cuanto dispara el detector. | `--anti-repeat-abort 0` |
| `--anti-repeat-retry` | `true` | Un retry con el aviso suave, descartando la ventana repetida. | `--anti-repeat-retry 0` |
| `--anti-repeat-window` | `256` | Ventana comparada en chars normalizados (`64..2048`). | `--anti-repeat-window 512` |
| `--anti-repeat-notice` | `ContentAntiRepeat.SoftNotice` | Texto del aviso suave del retry. | `--anti-repeat-notice "Reformule el razonamiento."` |

#### G) Presupuesto / contexto

| Argumento | Default | Efecto | Ejemplo |
|---|---|---|---|
| `--max-tokens` | `0` | Techo de salida del carril GPU; `0` = upstream/cliente decide. | `--max-tokens 16384` |
| `--coerced-max-tokens` / `--cpu-max-tokens` | `0` | Techo del carril coercido; `0` = sin techo. | `--cpu-max-tokens 8192` |
| `--context-guard` | `false` | Devuelve 413 al alcanzar el umbral del ctx. | `--context-guard 1` |
| `--context-threshold` | `0.95` | Fracción del ctx por slot (`0.5..0.99`). | `--context-threshold 0.90` |
| `--context-tokens` | `0` | Tamaño del ctx en tokens; `0` = guarda inactiva. | `--context-tokens 524288` |
| `--context-slots` | `1` | Slots paralelos de llama (`-np`): la cuenta es por slot. | `--context-slots 4` |

#### H) Generación / artefactos

| Argumento | Default | Efecto | Ejemplo |
|---|---|---|---|
| `--temperature` | `0.3` | Temperatura global cuando cliente/perfil no manda. | `--temperature 0.2` |
| `--aux-temp` | — | Global del turno de RETRY (desvío). Misma precedencia que `--temperature`: rol resuelto manda; sin rol vale este valor; sin él, el perfil de desvío (`plan`). | `--aux-temp 0.5` |
| `--tool-output-limit` | `0` | Corta cada resultado de tool en N chars; `0` = integral. | `--tool-output-limit 8000` |
| `--sampling-passthrough` | `false` | Reenvía muestreo del cliente (`repeat_penalty`, `dry_*`, `top_p`, `seed`). | `--sampling-passthrough 1` |

#### I) Seguridad / permisos

| Argumento | Default | Efecto | Ejemplo |
|---|---|---|---|
| `--risk-ask` | `true` | Comando de riesgo se vuelve pregunta; `0` = niega. | `--risk-ask 0` |
| `--risk-ask-minutes` | `30` | Ventana de aprobación en minutos. | `--risk-ask-minutes 10` |
| `--risk-tools-alerts` | vacío | Regex por rol para tools de riesgo. | `--risk-tools-alerts "Plan=git\\s+push"` |
| `--tool-blacklist` | vacío | Blacklist de llamada por rol (la tool sigue anunciada). | `--tool-blacklist "Plan=write,edit,exec;MoE=write"` |

#### J) Observabilidad

| Argumento | Default | Efecto | Ejemplo |
|---|---|---|---|
| `--diagnostic` | `false` | Activa la sonda Serilog. | `--diagnostic 1` |
| `--diagnostic-level` | `off` | `off`, `metrics` (sin cuerpos) o `dump` (cuerpos completos). | `--diagnostic-level metrics` |

#### L) Legado (nunca en producción)

| Argumento | Default | Efecto | Ejemplo |
|---|---|---|---|
| `--legacy-harness` | `false` | Reactiva guardrail/strip/retry/clamp; prerrequisito de la coerción. | `--legacy-harness 1` |

### Endpoints de la API

| Método | Ruta | Para qué |
|---|---|---|
| `POST` | `/v1/responses` | contrato Responses (lo que habla OpenCode) |
| `POST` | `/v1/chat/completions` | contrato chat/completions |
| `GET` | `/v1/models` | lista el modelo servido |
| `GET` | `/api/status` | foto de config + telemetría |
| `GET` | `/api/metrics` | caudal, tools, archivos, abortos |
| `GET` | `/api/harness` | colas activas (chars) y alcance |
| `POST` | `/api/permission/check` | decisión `allow`/`ask`/`deny` por rol |
| `POST` | `/api/tools/execute` | ejecuta una tool en el workspace |
| `POST` | `/api/translate` | dry-run: chat/completions que se enviaría |
| `POST` | `/api/sanitize` | dry-run del bypass: input sin reasoning |
| `POST` | `/api/convert` | dry-run de la vuelta: chat → Responses |
| `POST` | `/api/config` | ajuste parcial en runtime |
| `POST` | `/api/auth/login` | login → JWT |
| `POST` | `/api/diag/reset` | reinicia contadores/sonda/panel sin restart |
| `GET` | `/api/test/last-system` | system final del último request + pruebas |
| `POST` | `/api/test/step` | publica el step en ejecución (`RUN`) |
| `GET` | `/api/test/steps` | steps publicados (`?war=1` solo con `mode`) |
| `GET` | `/api/test/probe/latest` | última muestra de la sonda |

### Pruebas — quién verifica qué artefacto

| Suite | Dueño | Ejecución | Tiempo |
|---|---|---|---|
| `Rochas.OpenCodeBridge.Test` | **Bridge/Core**: traducción, perfiles, anti-repeat, breaker, harness, auth, métricas + E2E | `dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --api-key guest@123 --skip-e2e` | segundos (unit) |
| `Rochas.OpenCodeBridge.Web.Test` | **Web**: hash/JWT, agentes, CRUD SQLite, sesiones, SSE, tool executor, confirm gate | `dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --web http://127.0.0.1:4130 --units-only` | ~3,4 s |
| Integración Web | endpoints HTTP + stream de chat (requiere `:4130`) | `… --integration-only` | ~2–4 min (modelo vivo) |

Deuda conocida: `Unit/AntiRepeatCoreTests.cs` y `Unit/DedupGuardTests.cs`
ejercitan tipos del Core, pero viven en Web.Test (validan lo que consume la Web).
Deben migrar a `Rochas.OpenCodeBridge.Test` en la fusión.

```bash
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --skip-e2e --api-key guest@123
dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --web http://127.0.0.1:4130 --units-only

./scripts/run-e2e-pipeline.sh --phase 0   # health + VRAM
./scripts/run-e2e-pipeline.sh --phase 1   # unit
./scripts/run-e2e-pipeline.sh --phase 2   # science + mission
./scripts/run-e2e-pipeline.sh --phase 3   # opencode standalone
```

### Docs

- `docs/LAB-SETUP.md` — infra: units, flags de llama, TurboQuant, VRAM, escenarios A–E, referencia completa.
- `AGENTS.md` — reglas del operador.
- `docs/AGENTS-PROMPTS.md` — system prompts versionados.
- `HISTORY.md` — historial de construcción y mediciones.
- `docs/RUNBOOK-E2E.md` — fases y estado del entorno.
- `docs/BENCHTESTS.md` — baterías (`run-battery-dual.sh`, WAR, frontpipe).
- `docs/PENDENCIAS.md` / `docs/PENDINGS.md` — pendientes.

---

## Français

**Pont HTTP en C# (.NET 10) entre l'API `/v1/responses` d'OpenCode et tout
backend compatible OpenAI (llama.cpp, vLLM), avec une UI web MVC et sa propre
boucle d'outils.**

> **Abandon :** ce projet sera abandonné et le pont sera **fusionné dans
> `AI.Manager`**. Jusque-là, ce code reste la référence et le harnais E2E.

```bash
dotnet add package Rochas.OpenCodeBridge

dotnet Rochas.OpenCodeBridge.dll --port 4124 --upstream http://127.0.0.1:4100 \
  --model Gemma4-E4B-Q4 --api-key guest@123 --mode bridge \
  --main-harness 1 --harness-tails 1 --tail-scope all \
  --anti-repeat 1 --anti-repeat-window 512 --temperature 0.2
```

Chaque fonctionnalité est documentée une seule fois — pourquoi elle existe,
comment elle résout, exemple console, exemple API et exemple C# — dans la
[section portugaise](#português) ; l'équivalent anglais est dans
[English](#english).

Tests : `Rochas.OpenCodeBridge.Test` (pont/Core) et
`Rochas.OpenCodeBridge.Web.Test` (Web). `dotnet test` n'est pas utilisé : les
deux suites sont des exécutables console (sortie 0/1).

---

## Deutsch

**HTTP-Bridge in C# (.NET 10) zwischen der OpenCode-API `/v1/responses` und
jedem OpenAI-kompatiblen Backend (llama.cpp, vLLM), plus MVC-Web-UI mit eigener
agentischer Tool-Schleife.**

> **Einstellung:** Dieses Projekt wird eingestellt und die Bridge wird in
> **`AI.Manager`** integriert. Bis dahin bleibt dieser Code die Referenz und
> das E2E-Harness.

```bash
dotnet add package Rochas.OpenCodeBridge

dotnet Rochas.OpenCodeBridge.dll --port 4124 --upstream http://127.0.0.1:4100 \
  --model Gemma4-E4B-Q4 --api-key guest@123 --mode bridge \
  --main-harness 1 --harness-tails 1 --tail-scope all \
  --anti-repeat 1 --anti-repeat-window 512 --temperature 0.2
```

Jede Funktion steht genau einmal dokumentiert — warum sie existiert, wie sie
löst, Konsolenbeispiel, API-Beispiel und C#-Beispiel — im
[portugiesischen Abschnitt](#português); die englische Entsprechung steht unter
[English](#english).

Tests: `Rochas.OpenCodeBridge.Test` (Bridge/Core) und
`Rochas.OpenCodeBridge.Web.Test` (Web). `dotnet test` wird nicht verwendet;
beide Suiten sind Konsolen-Runner (Exit 0/1).

---

> **Fusão / Fusion / Fusión / Fusion / Integration:** the bridge code, harness
> and tests move to `AI.Manager`; this repository is frozen for the fusion.
> O código da bridge, o harness e os testes migram para o `AI.Manager`; este
> repositório fica congelado até a fusão.

---

## Descontinuidade e agradecimentos / Discontinuation and acknowledgements

> **Este é o último ciclo deste componente como projeto independente.** A bridge
> `Rochas.OpenCodeBridge` e a UI web serão fundidas ao **`AI.Manager`**, que passa
> a concentrar o harness, o loop de tools e a operação. O repositório fica
> congelado como referência histórica e base da fusão.
>
> Agradecimentos à **comunidade OpenCode**, pelo protocolo, pelo harness e pelas
> ferramentas que tornaram esta bridge possível, e à **DeepSeek**, por conceder
> modelos de alta precisão que conduziram todo o ciclo de desenvolvimento da
> propriedade intelectual deste componente — do desenho do protocolo à tradução
> Responses ⇄ chat, do anti-repeat verbatim ao harness e aos testes de aceite.
>
> **This is the final cycle of this component as a standalone project.** The
> `Rochas.OpenCodeBridge` bridge and web UI will be merged into **`AI.Manager`**,
> which becomes the home of the harness, the tool loop and operations. The
> repository is frozen as a historical reference and as the base for the fusion.
>
> Thanks to the **OpenCode community** for the protocol, harness and tools that
> made this bridge possible, and to **DeepSeek** for granting high-precision
> models that drove the entire development cycle of this component's
> intellectual property — from the protocol design to the Responses ⇄ chat
> translation, from verbatim anti-repeat to the harness and acceptance tests.
