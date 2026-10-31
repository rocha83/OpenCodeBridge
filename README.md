# Rochas.OpenCodeBridge

[![NuGet](https://img.shields.io/nuget/v/Rochas.OpenCodeBridge.svg)](https://www.nuget.org/packages/Rochas.OpenCodeBridge)

> **PROJECT STATUS / STATUS DO PROJETO / ESTADO DEL PROYECTO / STATUT DU PROJET / PROJEKTSTATUS**
>
> **This project is being discontinued. The bridge is being merged into `AI.Manager`.**
> **Este projeto será descontinuado. A bridge será fundida ao `AI.Manager`.**
> **Este proyecto será descontinuado. El puente se fusionará con `AI.Manager`.**
> **Ce projet sera abandonné. Le pont sera fusionné dans `AI.Manager`.**
> **Dieses Projekt wird eingestellt. Die Bridge wird in `AI.Manager` integriert.**
>
> The code here remains the reference implementation and the E2E harness until the
> fusion is complete. New features belong to `AI.Manager`.

[English](#english) | [Português](#português) | [Español](#español) | [Français](#français) | [Deutsch](#deutsch)

---

## English

**C# (.NET 10) HTTP bridge between the OpenCode `/v1/responses` API and any
OpenAI-compatible backend (llama.cpp, vLLM), plus an ASP.NET MVC web UI with its
own agentic tool loop.** The core uses only the BCL (`HttpListener` +
`System.Text.Json`): it translates Responses ⇄ chat/completions, handles
`thinking` + tool calling, resolves role profiles by model-id suffix, and applies
the **main harness** and the **two harness tails** (behavior + tools).

Local reference line: **Gemma 4 E4B** — `Gemma4-E4B-Q4` (**GPU1**, `:4100`/`:4124`)
and `Gemma4-E4B-UD-Q3` (**GPU2**, text-only, `:4110`/`:4125`), both fully on GPU
(`-ngl 99`) with TurboQuant (`turbo3` KV) inside 12 GB of VRAM (RTX 3060).

### Components

| Assembly | Responsibility |
|---|---|
| `Rochas.OpenCodeBridge` | Responses ⇄ chat/completions translator, thinking, tool calling, role profiles, harness, `gate` subcommand. |
| `Rochas.OpenCodeBridge.Core` | Pure BCL library: translation, anti-repeat, tool coercion, harness composition, context guard, metrics. |
| `Rochas.OpenCodeBridge.Web` | MVC UI: chat with Responses bypass, native tool loop (`function_call` → `ToolExecutor` → `function_call_output`), agents, subagent, scope gate. |
| `Rochas.OpenCodeBridge.Test` | Console suite for the **bridge/Core**: unit (HTTP, no model) + E2E (live model). Exit 0/1. |
| `Rochas.OpenCodeBridge.Web.Test` | Console suite for the **Web UI**: unit (hash, agents, SQLite CRUD, sessions, SSE, tool executor) + HTTP integration. Exit 0/1. |

> `dotnet test` is not used: both test projects are console runners
> (`dotnet run --project …`, exit 0 = PASS, 1 = FAIL).

### Topology and ports

| name | model | llama | bridge | notes |
|---|---|---|---|---|
| **GPU1** | `Gemma4-E4B-Q4` (`Q4_K_M` + mmproj Q8_0) | `:4100` | `:4124` | vision + tools; `-c 131072 -np 1` |
| **GPU2** | `Gemma4-E4B-UD-Q3` (`IQ3_XXS`, text-only) | `:4110` | `:4125` | `-c 114688 -np 2` (2×56k), 100 % GPU |
| **CPU Forced** | `Gemma4-E4B-Q4` (`Q4_K_M`, `-ngl 0`) | `:4120` | `:4126` | explicit test scenario, not a fallback |
| **Web UI** | — | — | `:4130` | MVC + SQLite; `BRIDGE_WEB_PORT` |

```bash
# GPU1 (managed path, Web/opencode with tools)
dotnet Rochas.OpenCodeBridge.dll --port 4124 --upstream http://127.0.0.1:4100 \
  --model Gemma4-E4B-Q4 --api-key guest@123 --thinking events \
  --main-harness 0 --harness-tails 1 --tail-scope all \
  --anti-repeat 1 --anti-repeat-window 512 --anti-repeat-retry 1 \
  --breaker-soft 3 --breaker-hard 6 --diagnostic 1 --diagnostic-level metrics
```

### Setup

```bash
dotnet add package Rochas.OpenCodeBridge
```

### Full CLI reference

Every flag lives in exactly one group. Boolean flags accept `true/false` and
`1/0`; the default is shown in the **Default** column.

#### A) Network / process

| Flag | Default | Effect | Example |
|---|---|---|---|
| `--port` | `4124` | HTTP listen port. | `--port 4125` |
| `--listen` / `--host` | `0.0.0.0` | Bind address (`--host` is a historical alias). | `--listen 127.0.0.1` |
| `--upstream` | `http://127.0.0.1:4100` | OpenAI-compatible upstream base URL. | `--upstream http://127.0.0.1:4110` |
| `--model` | `Gemma4-E4B-Q4` | Model id reported/served by the upstream. | `--model Gemma4-E4B-UD-Q3` |
| `--log` | `/tmp/opencodebridge.log` | JSONL log path. | `--log /tmp/bridge-gpu2.log` |
| `--workspace` | process cwd | Root for `POST /api/tools/execute`. | `--workspace /media/…/repo` |

#### B) Auth / admin

| Flag | Default | Effect | Example |
|---|---|---|---|
| `--api-key` | empty | Bearer required from clients and sent upstream. Empty = no auth. | `--api-key guest@123` |
| `--jwt-secret` | built-in | HMAC secret for JWT (min 16 chars). | `--jwt-secret "0123456789abcdef"` |
| `--jwt-expiry` | `720` | JWT lifetime in minutes. | `--jwt-expiry 60` |
| `--users-db` | app data | SQLite users database path. | `--users-db /tmp/users.db` |
| `--bridge-managed` | `false` | Force bridge-managed tool flow for every client (including opencode). | `--bridge-managed true` |

#### C) Transport / compatibility

| Flag | Default | Effect | Example |
|---|---|---|---|
| `--passthrough-responses` | `false` | Native Responses passthrough to llama.cpp (only strips reasoning items). | `--passthrough-responses true` |
| `--stream-per-delta` | `true` | Emit every SSE delta (typing effect) instead of aggregating 256 chars. | `--stream-per-delta 0` |
| `--qwen-standard` | `false` | Legacy Qwen mode: synthesized ModelPrompt + Alibaba tool names. | `--qwen-standard 1` |
| `--thinking-format` / `--thinking` | `events` | Reasoning wire format: `events` (reasoning items) or `off` (discard). | `--thinking-format off` |
| `--thinking-off-roles` | empty | Force thinking off per role name. | `--thinking-off-roles Build,Scripter,Exec` |

#### D) Harness / prompt

| Flag | Default | Effect | Example |
|---|---|---|---|
| `--main-harness` | `true` | Replace the client system with `Core/MainHarness.Text` on the managed path. | `--main-harness 0` |
| `--harness-tails` | `true` | Enable the two tails (behavior + tools). | `--harness-tails 0` |
| `--harness-behavior-tail` | `HarnessTails.DefaultBehavior` | Behavior tail text (how to act/deliver). | `--harness-behavior-tail "Validate scripts with bash -n; answer with evidence."` |
| `--harness-tools-tail` | `HarnessTails.DefaultTools` | Tools tail text (how to call tools). | `--harness-tools-tail "Use the dedicated tool; never invent fields."` |
| `--tail-scope` | `managed` | Where tails apply: `managed` (not the opencode CLI), `all`, `off`. | `--tail-scope all` |
| `--mode` | auto | `bridge` = MainHarness+tails+anti-repeat+fixes; `opencode` = client master + tails+anti-repeat+fixes (no MainHarness). | `--mode opencode` |
| `--breaker-soft` | `3` | Consecutive same-target tool failures that inject a nudge. | `--breaker-soft 2` |
| `--breaker-hard` | `6` | Consecutive failures that short-circuit the turn. | `--breaker-hard 4` |

#### E) Tool coercion

| Flag | Default | Effect | Example |
|---|---|---|---|
| `--upstream-parser` | `native` | Upstream capability: `native` (structured tool_calls) or `none` (text only). | `--upstream-parser none` |
| `--coerce-text-tools` | `false` | Extract text-written calls (```json, `<tool_call>`, `<tools>`) into `tool_calls`. | `--coerce-text-tools 1` |
| `--coerce-narrated` | `true` | Rescue `[Assistant tool call]: name({...})` copied from history. | `--coerce-narrated 0` |
| `--tool-strategy` | `true` | Deterministic Code Mode rescue: `execute{tools.X}` → tool `X`. | `--tool-strategy 0` |
| `--tool-shim` | empty | `json` = remove tools, inject a JSON envelope grammar, convert back. | `--tool-shim json` |
| `--tool-shim-max-string` | `2000` | Max chars per string in the shim grammar (`0` = no cap). | `--tool-shim-max-string 4000` |
| `--tool-shim-min-calls` | `0` | History calls required before allowing `{"final":…}`. | `--tool-shim-min-calls 1` |

#### F) Anti-repeat (content and tools)

| Flag | Default | Effect | Example |
|---|---|---|---|
| `--anti-repeat` | `true` | Watch `thinking`/`content`/tool-args for verbatim loops. | `--anti-repeat 0` |
| `--anti-repeat-abort` | `true` | Abort the upstream stream as soon as the detector trips. | `--anti-repeat-abort 0` |
| `--anti-repeat-retry` | `true` | One retry with the soft notice, discarding the repeated window. | `--anti-repeat-retry 0` |
| `--anti-repeat-window` | `256` | Compared tail size in normalized chars (`64..2048`). | `--anti-repeat-window 512` |
| `--anti-repeat-notice` | `ContentAntiRepeat.SoftNotice` | Soft notice text used by the retry. | `--anti-repeat-notice "Please reformulate this reasoning."` |

#### G) Budget / context

| Flag | Default | Effect | Example |
|---|---|---|---|
| `--max-tokens` | `0` | Output cap for the GPU line; `0` = upstream/client decides. | `--max-tokens 16384` |
| `--coerced-max-tokens` / `--cpu-max-tokens` | `0` | Output cap for the coerced (text-parser) line; `0` = no cap. | `--cpu-max-tokens 8192` |
| `--context-guard` | `false` | Return 413 when the prompt reaches the threshold. | `--context-guard 1` |
| `--context-threshold` | `0.95` | Context fraction (`0.5..0.99`). | `--context-threshold 0.90` |
| `--context-tokens` | `0` | Model context size in tokens; `0` = guard inactive. | `--context-tokens 131072` |

#### H) Generation / artifacts

| Flag | Default | Effect | Example |
|---|---|---|---|
| `--temperature` | `0.3` | Global temperature when the client/profile does not send one. | `--temperature 0.2` |
| `--aux-temp` | — | Global temperature of the RETRY turn (detour). Same precedence as `--temperature`: a resolved role wins; without a role this value applies; without it, the detour profile (`plan`). | `--aux-temp 0.5` |
| `--tool-output-limit` | `0` | Cut each tool result at N chars; `0` = full output. | `--tool-output-limit 8000` |
| `--sampling-passthrough` | `false` | Forward client sampling params (`repeat_penalty`, `dry_*`, `top_p`, `seed`). | `--sampling-passthrough 1` |

#### I) Security / permissions

| Flag | Default | Effect | Example |
|---|---|---|---|
| `--risk-ask` | `true` | Risk command becomes a question; `0` = deny. | `--risk-ask 0` |
| `--risk-ask-minutes` | `30` | Approval cache window in minutes. | `--risk-ask-minutes 10` |
| `--risk-tools-alerts` | empty | Per-role regex list for risky tools. | `--risk-tools-alerts "Plan=git\\s+push,\\brm\\b;Orch=\\bdd\\b"` |
| `--tool-blacklist` | empty | Per-role call blacklist (the tool stays announced). | `--tool-blacklist "Plan=write,edit,exec;MoE=write"` |

#### J) Observability

| Flag | Default | Effect | Example |
|---|---|---|---|
| `--diagnostic` | `false` | Enable the Serilog probe. | `--diagnostic 1` |
| `--diagnostic-level` | `off` | `off`, `metrics` (no bodies) or `dump` (full bodies). | `--diagnostic-level metrics` |

#### L) Legacy (never in production)

| Flag | Default | Effect | Example |
|---|---|---|---|
| `--legacy-harness` | `false` | Re-enable the old guardrail/strip/retry/clamp; prerequisite of text coercion. | `--legacy-harness 1` |

### C# API — Core methods with examples

The Core is a plain class library; the snippets below compile against
`Rochas.OpenCodeBridge.Core`.

#### `BridgeProxy` + `BridgeOptions`

`BridgeProxy` is the same HTTP client the bridge uses to talk to llama.cpp: chat
completions, native Responses passthrough and the health probe. `BridgeOptions`
is the plain settings record (upstream URL, served model id, api key). Every call
is async, takes a `CancellationToken` and holds no static state, so a test can
drive several upstreams in the same process.

```csharp
var options = new BridgeOptions
{
    Upstream = "http://127.0.0.1:4100",
    Model = "Gemma4-E4B-Q4",
    ApiKey = "guest@123"
};

// chat/completions
var chat = await BridgeProxy.SendChatAsync(options, new JsonObject
{
    ["model"] = options.Model,
    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "ping" })
}, stream: false, ct: default);

// native Responses passthrough
var responses = await BridgeProxy.SendResponsesAsync(options, new JsonObject
{
    ["model"] = options.Model,
    ["input"] = "ping"
}, stream: true, ct: default);

bool alive = await BridgeProxy.PingAsync(options, default);
```

#### `MainHarness`

`MainHarness` owns the system prompt of the managed path: it replaces whatever
system the client sent with the bridge base prompt, keeping the client `<env>`,
the skills block and the current date. `LooksLikeOpencodeMaster` is the
discriminator used to decide whether the client already self-manages its prompt;
when it does, the opencode lane keeps its own harness and the bridge only passes
the request through.

```csharp
// Managed path: replace the client system, keeping <env>, skills and date.
bool replaced = MainHarness.Apply(messages, enabled: true);

// Does the client already carry opencode's master prompt?
bool isOpencode = MainHarness.LooksLikeOpencodeMaster(clientSystem);

// Compose only (no mutation).
string system = MainHarness.BuildSystem(clientSystem);
```

#### `HarnessTails`

`HarnessTails` appends the bridge deltas on top of the harness base: behavior,
tool mechanics and the F# mini table. `Compose` is pure — it returns a new
string and never mutates the request — and it swaps the behavior tail for
`DefaultBehaviorNoTools` when the request carries no `tools` array (otherwise the
model would answer with tool JSON as text).

```csharp
string system = HarnessTails.Compose(
    clientSystem,
    harness: true,
    behavior: "Deliver evidence before answering.",
    tools: HarnessTails.DefaultTools);
```

#### `ContentAntiRepeat` (content loops)

`ContentAntiRepeat` detects verbatim loops in streamed text: the window is how
many trailing characters are compared. `StreamAntiRepeatReply` is the stateful
detector for one response; the static helpers are the same math for offline
checks (normalize, similarity ratio, tail repetition and the index where the
duplicate starts).

```csharp
var detector = new StreamAntiRepeatReply(window: 512);
bool loop = detector.IsRepeat(accumulatedText); // verbatim tail

// Static helpers
string norm = ContentAntiRepeat.Normalize("  Hello   WORLD ");
double ratio = ContentAntiRepeat.Similarity("abc", "abd");       // 0.66
bool tail = ContentAntiRepeat.TailRepeats(text, window: 512);
int at = ContentAntiRepeat.TailDuplicateAt(text, window: 512);   // -1 when absent
```

#### `ToolsAntiRepeat` (tool loops)

`ToolsAntiRepeat` covers tool loops. `ToolsBreaker` counts consecutive failures
on the same target: soft inserts a nudge into the history and hard ends the
attempt. `RepeatedCall` finds identical calls. `ToolSig` normalizes the arguments so
two calls that differ only in digits or separators count as the same target.

```csharp
var breaker = new ToolsBreaker(soft: 3, hard: 6);
BreakerResult verdict = breaker.Evaluate(messages);

if (verdict.Verdict == BreakerVerdict.Nudge)
    breaker.ApplyNudge(messages, verdict.Notice);
if (verdict.Verdict == BreakerVerdict.Trip)
    return Results.Text(verdict.Notice, statusCode: 409);

var repeated = new RepeatedCall(minCount: 2).Find(messages);
string sig = ToolSig.Of("write", argsJson);       // canonical signature
string target = ToolSig.Target("write", argsJson); // digits/separators removed
bool failed = ToolSig.IsFailure(toolOutput);
```

#### `RoleProfiles`

`RoleProfiles` maps a model suffix (`-plan`, `-build`, `-orch`, `-moe`, `-scripter`,
`-exec`) to the sampling settings of that role: temperature and thinking mode.
The bridge resolves the profile per request, so one process can serve every role;
`Apply` writes the table into configuration at boot and `RoleOf` tells which role
a model id belongs to.

```csharp
var profiles = RoleProfiles.DefaultCopy();
if (RoleProfiles.TryResolve("Gemma4-E4B-Q4-build", profiles, out var profile))
{
    double temp = profile.Temperature;
    string thinking = profile.Thinking; // "events" or "off"
}
string role = RoleProfiles.RoleOf("Gemma4-E4B-Q4-build", profiles); // "Build"
int applied = RoleProfiles.Apply(profiles, configNode, globalTemp: 0.3, globalThinking: "events");
```

#### `ToolCoercion` / `ToolCatalog`

`ToolCatalog` is the single source of tool schemas for both chat and Responses
(consumers never redeclare them). `ToolCoercion` repairs models that emit a tool
call as text: it extracts the call from the content, coerces it into the request
and strips control tags from the visible answer.

```csharp
var known = ToolCatalog.Chat().Select(t => t?["function"]?["name"]?.GetValue<string>()).ToList();
var calls = ToolCoercion.ExtractTextToolCalls(content, known);
int synthesized = ToolCoercion.CoerceChatCalls(root, known);
string clean = ToolCoercion.StripControlTags(text, removed);
```

#### `ContextGuard`, `PermissionPolicy`, `Metrics`

Three small cross-cutting services. `ContextGuard` trips when the prompt
approaches the context budget (threshold over the model window) and reports the
measured fraction. `PermissionPolicy` decides `allow` / `ask` / `deny` from the
role and the tool being called. `Metrics` keeps the counters served by
`/api/metrics` (requests, tokens, throughput, tools, files).

```csharp
bool trips = ContextGuard.Tripped(requestJson,
    new ContextGuardSettings(enabled: true, threshold: 0.95, tokens: 131072),
    out int promptTokens, out double fraction);

var (action, reason) = PermissionPolicy.Check("Gemma4-E4B-Q4-plan", "write", argsJson);
// action: "allow" | "ask" | "deny"

Metrics.Record("tool", new JsonObject { ["tool"] = "write", ["ok"] = true });
var snapshot = Metrics.Snapshot();
```

### Testing — who owns which artifact

The split is by **artifact under test**, with one known debt:

| Suite | Owns | Runs | Time |
|---|---|---|---|
| `Rochas.OpenCodeBridge.Test` | **Bridge/Core**: translation, profiles, anti-repeat, breaker, harness, bypass, auth, metrics + E2E with live model | `dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --api-key guest@123 --skip-e2e` | seconds (unit) |
| `Rochas.OpenCodeBridge.Web.Test` | **Web UI**: hash/JWT, agents, SQLite CRUD, sessions, SSE stream, tool executor, confirm gate, calibration contract | `dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --web http://127.0.0.1:4130 --units-only` | ~3.4 s |
| Web integration | Web HTTP endpoints + chat stream (needs `:4130` up) | `… --integration-only` | ~2–4 min (live model) |

Known debt: `Unit/AntiRepeatCoreTests.cs` and `Unit/DedupGuardTests.cs` exercise
Core types but live in the Web.Test project (they validate what the Web consumes).
They should move to `Rochas.OpenCodeBridge.Test` when the fusion lands.

```bash
# Fast, model-free checks
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --skip-e2e --api-key guest@123
dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --web http://127.0.0.1:4130 --units-only

# E2E pipeline (live llama/bridge)
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
- `docs/PENDENCIAS.md` / `docs/PENDINGS.md` — open items.

---

## Português

**Bridge HTTP em C# (.NET 10) entre a API `/v1/responses` do OpenCode e qualquer
backend OpenAI-compatível (llama.cpp, vLLM), mais UI web MVC com loop de tools
agentivo próprio.** O núcleo usa só a BCL (`HttpListener` + `System.Text.Json`):
traduz Responses ⇄ chat/completions, trata `thinking` + tool calling, resolve
perfil por sufixo do model id e aplica o **main harness** e as **duas caudas**
(comportamento + tools).

> **Descontinuação:** este projeto será encerrado e a bridge será **fundida ao
> `AI.Manager`**. Até a fusão, este código é a implementação de referência e o
> harness E2E. Features novas vão para o `AI.Manager`.

Linha local de referência: **Gemma 4 E4B** — `Gemma4-E4B-Q4` (**GPU1**,
`:4100`/`:4124`) e `Gemma4-E4B-UD-Q3` (**GPU2**, text-only, `:4110`/`:4125`),
os dois 100 % em GPU (`-ngl 99`) com TurboQuant (`turbo3` no KV) em 12 GB de VRAM
(RTX 3060).

### Setup

```bash
dotnet add package Rochas.OpenCodeBridge
```

### Referência completa de argumentos

Flags booleanas aceitam `true/false` e `1/0`.

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
| `--stream-per-delta` | `true` | Emite cada delta SSE (efeito digitando) em vez de agregar 256 chars. | `--stream-per-delta 0` |
| `--qwen-standard` | `false` | Modo legado Qwen: ModelPrompt sintetizado + nomes Alibaba. | `--qwen-standard 1` |
| `--thinking-format` / `--thinking` | `events` | Formato do reasoning: `events` (itens) ou `off` (descarta). | `--thinking-format off` |
| `--thinking-off-roles` | vazio | Força thinking off por nome de role. | `--thinking-off-roles Build,Scripter,Exec` |

#### D) Harness / prompt

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--main-harness` | `true` | Substitui o system do cliente pelo `Core/MainHarness.Text` no caminho gerido. | `--main-harness 0` |
| `--harness-tails` | `true` | Liga as duas caudas (comportamento + tools). | `--harness-tails 0` |
| `--harness-behavior-tail` | `HarnessTails.DefaultBehavior` | Cauda de comportamento (como agir/entregar). | `--harness-behavior-tail "Valide scripts com bash -n; responda com evidência."` |
| `--harness-tools-tail` | `HarnessTails.DefaultTools` | Cauda de tools (como chamar). | `--harness-tools-tail "Use a tool dedicada; nunca invente campos."` |
| `--tail-scope` | `managed` | Onde as caudas valem: `managed` (exceto CLI opencode), `all`, `off`. | `--tail-scope all` |
| `--mode` | auto | `bridge` = MainHarness+tails+anti-repeat+fixes; `opencode` = client master + tails+anti-repeat+fixes (no MainHarness). | `--mode opencode` |
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

#### F) Anti-repetição (conteúdo e tools)

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
| `--context-threshold` | `0.95` | Fração do ctx (`0.5..0.99`). | `--context-threshold 0.90` |
| `--context-tokens` | `0` | Tamanho do ctx em tokens; `0` = guarda inativa. | `--context-tokens 131072` |

#### H) Geração / artefatos

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--temperature` | `0.3` | Temperatura global quando cliente/perfil não manda. | `--temperature 0.2` |
| `--aux-temp` | — | Temperatura global do turno de RETRY (desvio). Mesma precedência da `--temperature`: role resolvido manda; sem role, vale este valor; sem ele, o perfil de desvio (`plan`). | `--aux-temp 0.5` |
| `--tool-output-limit` | `0` | Corta cada resultado de tool em N chars; `0` = integral. | `--tool-output-limit 8000` |
| `--sampling-passthrough` | `false` | Repassa amostragem do cliente (`repeat_penalty`, `dry_*`, `top_p`, `seed`). | `--sampling-passthrough 1` |

#### I) Segurança / permissões

| Argumento | Default | Efeito | Exemplo |
|---|---|---|---|
| `--risk-ask` | `true` | Comando de risco vira pergunta; `0` = nega. | `--risk-ask 0` |
| `--risk-ask-minutes` | `30` | Janela de aprovação em minutos. | `--risk-ask-minutes 10` |
| `--risk-tools-alerts` | vazio | Regex por role para tools de risco. | `--risk-tools-alerts "Plan=git\\s+push,\\brm\\b;Orch=\\bdd\\b"` |
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

### Métodos C# — descritivo e exemplos

#### `BridgeProxy` + `BridgeOptions`

`BridgeProxy` é o mesmo cliente HTTP que a bridge usa para falar com o llama.cpp:
chat completions, passthrough nativo de Responses e a sonda de saúde.
`BridgeOptions` é o registro de configuração (URL do upstream, modelo servido,
api key). Toda chamada é assíncrona, recebe `CancellationToken` e não guarda
estado estático — um teste pode conduzir vários upstreams no mesmo processo.

```csharp
var options = new BridgeOptions
{
    Upstream = "http://127.0.0.1:4100",
    Model = "Gemma4-E4B-Q4",
    ApiKey = "guest@123"
};

// chat/completions
var chat = await BridgeProxy.SendChatAsync(options, new JsonObject
{
    ["model"] = options.Model,
    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "ping" })
}, stream: false, ct: default);

// passthrough nativo de Responses
var responses = await BridgeProxy.SendResponsesAsync(options, new JsonObject
{
    ["model"] = options.Model,
    ["input"] = "ping"
}, stream: true, ct: default);

bool alive = await BridgeProxy.PingAsync(options, default);
```

#### `MainHarness`

`MainHarness` é dono do system prompt do caminho gerido: troca o system que o
cliente mandou pelo prompt base da bridge, preservando o `<env>` do cliente, o
bloco de skills e a data atual. `LooksLikeOpencodeMaster` é o discriminador: se o
cliente já se auto-gerencia, a raia opencode mantém o harness próprio e a bridge
só repassa o pedido.

```csharp
// Caminho gerido: troca o system do cliente preservando <env>, skills e data.
bool replaced = MainHarness.Apply(messages, enabled: true);

// O cliente já traz o master do opencode?
bool isOpencode = MainHarness.LooksLikeOpencodeMaster(clientSystem);

// Só compõe (não muta).
string system = MainHarness.BuildSystem(clientSystem);
```

#### `HarnessTails`

`HarnessTails` acrescenta os deltas da bridge sobre a base do harness:
comportamento, mecânica de tools e a tabela F# mini. `Compose` é puro — devolve
uma string nova e nunca muta o request — e troca a cauda de comportamento por
`DefaultBehaviorNoTools` quando o pedido não traz array `tools` (senão o modelo
responde com o JSON da tool como texto).

```csharp
string system = HarnessTails.Compose(
    clientSystem,
    harness: true,
    behavior: "Entregue evidência antes de responder.",
    tools: HarnessTails.DefaultTools);
```

#### `ContentAntiRepeat` (loop de conteúdo)

`ContentAntiRepeat` detecta loop verbatim em texto transmitido: a janela é
quantos caracteres finais são comparados. `StreamAntiRepeatReply` é o detector
com estado de uma resposta; os helpers estáticos são a mesma matemática para
conferência offline (normalizar, razão de similaridade, repetição de cauda e o
índice onde a duplicata começa).

```csharp
var detector = new StreamAntiRepeatReply(window: 512);
bool loop = detector.IsRepeat(textoAcumulado); // cauda verbatim

string norm = ContentAntiRepeat.Normalize("  Olá   MUNDO ");
double ratio = ContentAntiRepeat.Similarity("abc", "abd");       // 0.66
bool tail = ContentAntiRepeat.TailRepeats(texto, window: 512);
int at = ContentAntiRepeat.TailDuplicateAt(texto, window: 512);   // -1 se ausente
```

#### `ToolsAntiRepeat` (loop de tools)

`ToolsAntiRepeat` cobre o loop de tools. `ToolsBreaker` conta falhas seguidas no
mesmo alvo: soft insere um aviso no histórico e hard encerra a tentativa.
`RepeatedCall` acha chamadas idênticas. `ToolSig` normaliza os argumentos, então
duas chamadas que diferem só em dígitos/separadores contam como o mesmo alvo.

```csharp
var breaker = new ToolsBreaker(soft: 3, hard: 6);
BreakerResult verdict = breaker.Evaluate(messages);

if (verdict.Verdict == BreakerVerdict.Nudge)
    breaker.ApplyNudge(messages, verdict.Notice);
if (verdict.Verdict == BreakerVerdict.Trip)
    return Results.Text(verdict.Notice, statusCode: 409);

var repeated = new RepeatedCall(minCount: 2).Find(messages);
string sig = ToolSig.Of("write", argsJson);        // assinatura canônica
string target = ToolSig.Target("write", argsJson);  // sem dígitos/separadores
bool failed = ToolSig.IsFailure(toolOutput);
```

#### `RoleProfiles`

`RoleProfiles` mapeia o sufixo do modelo (`-plan`, `-build`, `-orch`, `-moe`,
`-scripter`, `-exec`) para a amostragem daquela role: temperatura e modo de
thinking. A bridge resolve o perfil por request, então um processo serve todas as
roles; `Apply` grava a tabela na configuração no boot e `RoleOf` diz a que role
um id de modelo pertence.

```csharp
var profiles = RoleProfiles.DefaultCopy();
if (RoleProfiles.TryResolve("Gemma4-E4B-Q4-build", profiles, out var profile))
{
    double temp = profile.Temperature;
    string thinking = profile.Thinking; // "events" ou "off"
}
string role = RoleProfiles.RoleOf("Gemma4-E4B-Q4-build", profiles); // "Build"
int applied = RoleProfiles.Apply(profiles, configNode, globalTemp: 0.3, globalThinking: "events");
```

#### `ToolCoercion` / `ToolCatalog`

`ToolCatalog` é a fonte única do schema de tools para chat e Responses
(consumidor não redeclara). `ToolCoercion` conserta o modelo que emite a tool
call como texto: extrai a chamada do conteúdo, converte para o request e remove
as tags de controle da resposta visível.

```csharp
var known = ToolCatalog.Chat()
    .Select(t => t?["function"]?["name"]?.GetValue<string>()).ToList();
var calls = ToolCoercion.ExtractTextToolCalls(content, known);
int synthesized = ToolCoercion.CoerceChatCalls(root, known);
string clean = ToolCoercion.StripControlTags(text, removed);
```

#### `ContextGuard`, `PermissionPolicy`, `Metrics`

Três serviços transversais pequenos. `ContextGuard` dispara quando o prompt se
aproxima do orçamento de contexto (limiar sobre a janela do modelo) e informa a
fração medida. `PermissionPolicy` decide `allow` / `ask` / `deny` pela role e
pela tool chamada. `Metrics` mantém os contadores servidos em `/api/metrics`
(requests, tokens, throughput, tools, arquivos).

```csharp
bool trips = ContextGuard.Tripped(requestJson,
    new ContextGuardSettings(enabled: true, threshold: 0.95, tokens: 131072),
    out int promptTokens, out double fraction);

var (action, reason) = PermissionPolicy.Check("Gemma4-E4B-Q4-plan", "write", argsJson);
// action: "allow" | "ask" | "deny"

Metrics.Record("tool", new JsonObject { ["tool"] = "write", ["ok"] = true });
var snapshot = Metrics.Snapshot();
```

### Testes — quem verifica qual artefato

A divisão é por **artefato sob teste**, com uma dívida conhecida:

| Suíte | Dono | Execução | Tempo |
|---|---|---|---|
| `Rochas.OpenCodeBridge.Test` | **Bridge/Core**: tradução, perfis, anti-repeat, breaker, harness, bypass, auth, métricas + E2E com modelo | `dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --api-key guest@123 --skip-e2e` | segundos (unit) |
| `Rochas.OpenCodeBridge.Web.Test` | **Web UI**: hash/JWT, agentes, CRUD SQLite, sessões, SSE, tool executor, confirm gate, contrato da calibração | `dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --web http://127.0.0.1:4130 --units-only` | ~3,4 s |
| Integração Web | Endpoints HTTP + stream de chat (exige `:4130` no ar) | `… --integration-only` | ~2–4 min (modelo vivo) |

Dívida conhecida: `Unit/AntiRepeatCoreTests.cs` e `Unit/DedupGuardTests.cs`
exercitam tipos do Core, mas vivem no Web.Test (validam o que a Web consome).
Devem migrar para `Rochas.OpenCodeBridge.Test` na fusão.

```bash
# Checagem rápida, sem modelo
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --skip-e2e --api-key guest@123
dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --web http://127.0.0.1:4130 --units-only

# Pipeline E2E (llama/bridge vivos)
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

## Español

**Puente HTTP en C# (.NET 10) entre la API `/v1/responses` de OpenCode y
cualquier backend compatible con OpenAI (llama.cpp, vLLM), más una UI web MVC
con su propio bucle de herramientas.**

> **Descontinuación:** este proyecto será descontinuado y el puente se
> **fusionará con `AI.Manager`**. Hasta entonces, este código es la referencia
> y el arnés E2E.

```bash
dotnet add package Rochas.OpenCodeBridge

dotnet Rochas.OpenCodeBridge.dll --port 4124 --upstream http://127.0.0.1:4100 \
  --model Gemma4-E4B-Q4 --api-key guest@123 --main-harness 0 --harness-tails 1 \
  --anti-repeat 1 --anti-repeat-window 512
```

Flags principales: `--port`, `--listen`, `--upstream`, `--model`, `--api-key`,
`--passthrough-responses`, `--main-harness`, `--harness-tails`, `--tail-scope`,
`--mode`, `--anti-repeat*`, `--breaker-soft/hard`, `--temperature`,
`--max-tokens`, `--diagnostic`. La referencia completa, con valores por defecto
y ejemplos, está en [English](#english).

Pruebas: `Rochas.OpenCodeBridge.Test` (bridge/Core) y
`Rochas.OpenCodeBridge.Web.Test` (Web). No se usa `dotnet test`; ambos son
runners de consola (salida 0/1).

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
  --model Gemma4-E4B-Q4 --api-key guest@123 --main-harness 0 --harness-tails 1 \
  --anti-repeat 1 --anti-repeat-window 512
```

Options principales : `--port`, `--listen`, `--upstream`, `--model`, `--api-key`,
`--passthrough-responses`, `--main-harness`, `--harness-tails`, `--tail-scope`,
`--mode`, `--anti-repeat*`, `--breaker-soft/hard`, `--temperature`,
`--max-tokens`, `--diagnostic`. Référence complète (valeurs par défaut et
exemples) en [English](#english).

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
  --model Gemma4-E4B-Q4 --api-key guest@123 --main-harness 0 --harness-tails 1 \
  --anti-repeat 1 --anti-repeat-window 512
```

Wichtigste Flags: `--port`, `--listen`, `--upstream`, `--model`, `--api-key`,
`--passthrough-responses`, `--main-harness`, `--harness-tails`, `--tail-scope`,
`--mode`, `--anti-repeat*`, `--breaker-soft/hard`, `--temperature`,
`--max-tokens`, `--diagnostic`. Vollständige Referenz (Defaults und Beispiele)
unter [English](#english).

Tests: `Rochas.OpenCodeBridge.Test` (Bridge/Core) und
`Rochas.OpenCodeBridge.Web.Test` (Web). `dotnet test` wird nicht verwendet;
beide Suiten sind Konsolen-Runner (Exit 0/1).

---

> **Fusion / Fusão / Fusión / Fusion / Integration:** the bridge code, harness
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
