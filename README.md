# vllm-ocode-bridge

🌐 **Idiomas / Languages / Idiomas / Langues / Sprachen:**
[Português](#português) · [English](#english) · [Español](#español) · [Français](#français) · [Deutsch](#deutsch)

---

## Português

Bridge HTTP leve em C# (.NET 9) que permite ao [OpenCode](https://opencode.ai) usar um
servidor local [vLLM](https://github.com/vllm-project/vllm) (ex.: Qwen3-8B) com
**tool calling funcional**, **thinking visível** e **streaming progressivo real**.

Sem pacotes NuGet externos — apenas a BCL (`HttpListener`, `System.Text.Json`).

### Por que existe

O OpenCode v2 fala a API moderna **`/v1/responses`**, enquanto o vLLM implementa o
protocolo legado **`/v1/chat/completions`**. Apontar o OpenCode direto para o vLLM
resulta em texto puro, `finish_reason: stop` e nenhuma tool executada. Esta bridge
traduz entre os dois protocolos nos dois sentidos.

### Como funciona

```
opencode ──POST /v1/responses──▶ bridge :4124 ──POST /v1/chat/completions──▶ vLLM :4100
         ◀── eventos SSE response.* ──          ◀── chunks chat.completion ──
```

- **Descida:** a bridge converte a requisição Responses (messages, tools, `enable_thinking`,
  temperature, max_tokens) em requisição chat completions.
- **Subida:** separa `reasoning`/`reasoning_content` (ou blocos `<think>…</think>` embutidos)
  do content, normaliza `tool_calls` e reemite eventos Responses corretos
  (`response.output_item.added`, `response.reasoning_summary_text.delta`,
  `response.output_text.delta`, `response.completed`, …) com `sequence_number` crescente
  e ids consistentes.

### Início rápido

```bash
# 1. vLLM (parser de tools hermes + parser de reasoning)
vllm serve /caminho/para/Qwen3-8B-AWQ \
  --served-model-name qwen3-8b-awq --port 4100 \
  --enable-auto-tool-choice --tool-call-parser hermes \
  --reasoning-parser deepseek_r1

# 2. Bridge
dotnet run -- --port 4124 --upstream http://127.0.0.1:4100 --model qwen3-8b-awq

# 3. Provider do OpenCode (opencode.json)
{
  "providers": {
    "openai": {
      "api": "openai-chat",
      "settings": { "baseURL": "http://127.0.0.1:4124/v1" },
      "models": {
        "qwen3-8b-awq": {
          "tool_call": true,
          "compatibility": { "reasoningField": "reasoning_content" }
        }
      }
    }
  }
}
```

### Opções de CLI

| Flag | Padrão | Descrição |
|---|---|---|
| `--port` | `4124` | Porta de escuta |
| `--listen` / `--host` | `127.0.0.1` | `0.0.0.0` expõe a bridge na rede |
| `--upstream` | `http://127.0.0.1:4100` | URL base do vLLM (GPU) ou llama.cpp (CPU) |
| `--model` | `qwen3-8b-awq` | Nome do modelo forçado upstream |
| `--thinking` | `events` | `events` = repassa deltas de reasoning; `off` = sem item reasoning (texto fundido) |
| `--temperature` | `0.2` | Valor do request vence a config; InvariantCulture |
| `--max-tokens` | `4096` | Teto de saída (GPU); pedido menor honrado, nunca maior |
| `--cpu-max-tokens` | `2048` | Teto da linha CPU (llama.cpp ~5 tok/s; teto alto = divagação) |
| `--coerce-text-tools` | auto (`true` se model contém `cpu`) | Coage JSON em texto → `tool_calls` (só linha CPU; vLLM intacta) |
| `--log` | `/tmp/qwen3-bridge.log` | Log JSONL de eventos (`coerced`, `tps_output`) |

### Perfis por sufixo no model

| Sufixo | Temp | Thinking | Uso |
|---|---|---|---|
| `-plan` | `0.4` | `events` | Planejar |
| `-build` | `0.2` | `off` | Executar |
| `-orch` | `0.2` | `off` no deploy `:4124` (`events` no default 2.1) | Orquestrar workers 3B via `standalone` (sem edit, só ler/web/shell) |
| `-moe` | `0.4` | `events` | Uso geral híbrido |
| (sem sufixo) | `0.2` | `events` | Padrão Qwen3 |

Temperatura explícita no request vence o perfil. `appsettings.json` ao lado do DLL (`port/upstream/model/thinking/temperature/maxTokens/cpuMaxTokens/logPath/profiles`) + exemplos `appsettings.4124.gpu-example.json` e `appsettings.4125.cpu-example.json`; `POST /api/config` ajusta em runtime.

### Duas linhas (GPU + CPU)

| Linha | Bridge | Upstream | Modelo |
|---|---|---|---|
| GPU | `:4124` | vLLM `:4100` (`hermes` + `deepseek_r1`) | `qwen3-8b-awq` (Qwen3-8B-AWQ) |
| CPU | `:4125` | llama.cpp `:4110` | `qwen25-coder-3b-cpu` (Qwen2.5-Coder-3B) |

### Endpoints

| Rota | Finalidade |
|---|---|
| `POST /v1/responses` | Tradução Responses ⇄ chat completions (thinking + tools + streaming) |
| `POST /v1/chat/completions`, `/v1/completions` | Passthrough ao upstream; renomeia `reasoning` → `reasoning_content`; na CPU força `stream:false` + `stop: ["[END_OF_TEXT]"]` |
| `GET /v1/models` | Passthrough |
| `GET /api/status` | Contadores e config em tempo real |
| `GET /api/metrics` | Throughput de tokens (tps_out / tps_total) |
| `POST /api/translate` | Dry-run Responses → chat (usado pela suite, sem chamar o modelo) |
| `POST /api/convert` | Dry-run de conversão de resposta (thinking + tools + coerção) |
| `POST /api/config` | Ajuste em runtime (`thinking`, `temperature`, `max_tokens`, `cpu_max_tokens`) |
| `GET /swagger`, `GET /api/swagger.json` | Swagger UI autocontido (sem CDN) |

### Subcomando gate (integridade pós-tarefa)

```bash
Rochas.OpenCodeBridge.dll gate <repo> "<prompt>" [texto-obrigatório ...] [--config gate.json] [--review]
```

Roda o agente pinado, exige commit novo, executa o build configurado e verifica
`forbidPaths` / `mustContain` do `gate.json`. No modo `--review`: commits locais são
livres, push (= nova baseline) só com aprovação; rejeição faz `git reset --soft`.

### Setup testado

- vLLM `:4100` com `--tool-call-parser hermes --reasoning-parser deepseek_r1` (o hermes
  venceu um bake-off 3/3; os parsers `qwen3_coder` e `qwen3_xml` falharam neste modelo).
- OpenCode fixado com `--agent build --model openai/qwen3-8b-awq`.
- Cada requisição gera uma linha JSON no log (`"model":"qwen3-8b-awq"`) — sem linha, sem modelo local.

### Suite de testes (Qwen3-8B + Qwen2.5-Coder-3B)

- Console .NET 9 sem NuGet (`Rochas.OpenCodeBridge.Test`): unit via HTTP na bridge
  (`/api/status|metrics`, `/v1/models`, `/api/translate`, `/api/convert`) + E2E no
  modelo vivo (`/v1/responses` com tools hermes, stream SSE, review sênior/enterprise).
- A suite é agnóstica ao perfil: lê o modelo do `/api/status` e roda nas duas linhas —
  **Qwen3-8B (GPU `:4124`→`:4100`)** e **Qwen2.5-Coder-3B (CPU `:4125`→`:4110`)**.
  Selo: **28–30/30 na CPU** (reruns; `409` flaky no 3B) e verde cheia na GPU.
- Rodar: `dotnet run -c Release --project Rochas.OpenCodeBridge.Test -- [--skip-e2e|--only <nome>]`.
- Detalhes e protocolo orch de 8 estágios em `HISTORY.md`.

---

## English

Lightweight C# (.NET 9) HTTP bridge that lets [OpenCode](https://opencode.ai) use a
local [vLLM](https://github.com/vllm-project/vllm) server (e.g. Qwen3-8B) with
**working tool calling**, **visible thinking** and **real progressive streaming**.

No external NuGet packages — only the BCL (`HttpListener`, `System.Text.Json`).

### Why it exists

OpenCode v2 speaks the modern **`/v1/responses`** API, while vLLM implements the
legacy **`/v1/chat/completions`** format. Pointing OpenCode straight at vLLM yields
plain text, `finish_reason: stop` and no tool execution. This bridge translates between
the two protocols in both directions.

### How it works

```
opencode ──POST /v1/responses──▶ bridge :4124 ──POST /v1/chat/completions──▶ vLLM :4100
         ◀── response.* SSE events ──          ◀── chat.completion chunks ──
```

- **Down:** converts the Responses request (messages, tools, `enable_thinking`,
  temperature, max_tokens) into a chat completions request.
- **Up:** splits `reasoning`/`reasoning_content` (or inline `<think>…</think>` blocks)
  out of the content, normalizes `tool_calls`, and re-emits proper Responses events
  (`response.output_item.added`, `response.reasoning_summary_text.delta`,
  `response.output_text.delta`, `response.completed`, …) with incrementing
  `sequence_number`s and consistent ids.

### Quick start

```bash
# 1. vLLM (hermes tool parser + reasoning parser)
vllm serve /path/to/Qwen3-8B-AWQ \
  --served-model-name qwen3-8b-awq --port 4100 \
  --enable-auto-tool-choice --tool-call-parser hermes \
  --reasoning-parser deepseek_r1

# 2. Bridge
dotnet run -- --port 4124 --upstream http://127.0.0.1:4100 --model qwen3-8b-awq

# 3. OpenCode provider (opencode.json)
{
  "providers": {
    "openai": {
      "api": "openai-chat",
      "settings": { "baseURL": "http://127.0.0.1:4124/v1" },
      "models": {
        "qwen3-8b-awq": {
          "tool_call": true,
          "compatibility": { "reasoningField": "reasoning_content" }
        }
      }
    }
  }
}
```

### CLI options

| Flag | Default | Description |
|---|---|---|
| `--port` | `4124` | Listen port |
| `--listen` / `--host` | `127.0.0.1` | `0.0.0.0` exposes the bridge on the LAN |
| `--upstream` | `http://127.0.0.1:4100` | Upstream base URL (vLLM on GPU, llama.cpp on CPU) |
| `--model` | `qwen3-8b-awq` | Model name forced upstream |
| `--thinking` | `events` | `events` = forward reasoning deltas; `off` = no reasoning item (text fused) |
| `--temperature` | `0.2` | Request value wins over config; parsed with InvariantCulture |
| `--max-tokens` | `4096` | Output cap (GPU); smaller requests honored, never larger |
| `--cpu-max-tokens` | `2048` | CPU-line cap (llama.cpp ~5 tok/s; high cap = rambling) |
| `--coerce-text-tools` | auto (`true` when model contains `cpu`) | Coerce text JSON → `tool_calls` (CPU line only; vLLM untouched) |
| `--log` | `/tmp/qwen3-bridge.log` | JSONL event log (`coerced`, `tps_output`) |

### Profiles by model suffix

| Suffix | Temp | Thinking | Use |
|---|---|---|---|
| `-plan` | `0.4` | `events` | Planning |
| `-build` | `0.2` | `off` | Executing |
| `-orch` | `0.2` | `off` on `:4124` deploy (`events` in 2.1 default) | Orchestrating 3B workers via `standalone` (read/web/shell only) |
| `-moe` | `0.4` | `events` | General hybrid use |
| (no suffix) | `0.2` | `events` | Qwen3 default |

Explicit request temperature wins over the profile. `appsettings.json` next to the DLL (`port/upstream/model/thinking/temperature/maxTokens/cpuMaxTokens/logPath/profiles`) + `appsettings.4124.gpu-example.json` and `appsettings.4125.cpu-example.json` samples; `POST /api/config` tunes at runtime.

### Two lines (GPU + CPU)

| Line | Bridge | Upstream | Model |
|---|---|---|---|
| GPU | `:4124` | vLLM `:4100` (`hermes` + `deepseek_r1`) | `qwen3-8b-awq` (Qwen3-8B-AWQ) |
| CPU | `:4125` | llama.cpp `:4110` | `qwen25-coder-3b-cpu` (Qwen2.5-Coder-3B) |

### Endpoints

| Route | Purpose |
|---|---|
| `POST /v1/responses` | Responses ⇄ chat completions translation (thinking + tools + streaming) |
| `POST /v1/chat/completions`, `/v1/completions` | Passthrough to upstream; renames `reasoning` → `reasoning_content`; on CPU forces `stream:false` + `stop: ["[END_OF_TEXT]"]` |
| `GET /v1/models` | Passthrough |
| `GET /api/status` | Live counters and config |
| `GET /api/metrics` | Token throughput (tps_out / tps_total) |
| `POST /api/translate` | Responses → chat dry-run (used by the suite, no model call) |
| `POST /api/convert` | Response-conversion dry-run (thinking + tools + coercion) |
| `POST /api/config` | Runtime tuning (`thinking`, `temperature`, `max_tokens`, `cpu_max_tokens`) |
| `GET /swagger`, `GET /api/swagger.json` | Self-contained Swagger UI (no CDN) |

### Gate subcommand

```bash
Rochas.OpenCodeBridge.dll gate <repo> "<prompt>" [required-text ...] [--config gate.json] [--review]
```

Runs the pinned agent, requires a new commit, runs the configured build, checks
`forbidPaths` / `mustContain` from `gate.json`. `--review` mode: local commits are free,
push (= new baseline) only on explicit approval; rejection performs `git reset --soft`.

### Tested setup

- vLLM `:4100` with `--tool-call-parser hermes --reasoning-parser deepseek_r1` (hermes won
  a 3/3 bake-off; `qwen3_coder` and `qwen3_xml` parsers failed against this model).
- OpenCode pinned with `--agent build --model openai/qwen3-8b-awq`.
- Each request is logged as a JSON line (`"model":"qwen3-8b-awq"`) — no log line, no local model.

### Test suite (Qwen3-8B + Qwen2.5-Coder-3B)

- NuGet-free .NET 9 console (`Rochas.OpenCodeBridge.Test`): HTTP unit tests against the
  bridge (`/api/status|metrics`, `/v1/models`, `/api/translate`, `/api/convert`) + live-model
  E2E (`/v1/responses` with hermes tools, SSE stream, senior/enterprise review).
- The suite is profile-agnostic: it reads the model from `/api/status` and runs on both
  lines — **Qwen3-8B (GPU `:4124`→`:4100`)** and **Qwen2.5-Coder-3B (CPU `:4125`→`:4110`)**.
  Seal: **28–30/30 on CPU** (reruns; `409` flaky on 3B) and full green on GPU.
- Run: `dotnet run -c Release --project Rochas.OpenCodeBridge.Test -- [--skip-e2e|--only <name>]`.
- Details and the 8-stage orch protocol in `HISTORY.md`.

---

## Español

Bridge HTTP ligero en C# (.NET 9) que permite a [OpenCode](https://opencode.ai) usar un
servidor local [vLLM](https://github.com/vllm-project/vllm) (p. ej. Qwen3-8B) con
**tool calling funcional**, **thinking visible** y **streaming progresivo real**.

Sin paquetes NuGet externos — solo la BCL (`HttpListener`, `System.Text.Json`).

### ¿Por qué existe?

OpenCode v2 habla la API moderna **`/v1/responses`**, mientras que vLLM implementa el
protocolo heredado **`/v1/chat/completions`**. Apuntar OpenCode directamente a vLLM
produce texto plano, `finish_reason: stop` y ninguna tool ejecutada. Este bridge
traduce entre ambos protocolos en ambas direcciones.

### ¿Cómo funciona?

```
opencode ──POST /v1/responses──▶ bridge :4124 ──POST /v1/chat/completions──▶ vLLM :4100
         ◀── eventos SSE response.* ──          ◀── chunks chat.completion ──
```

- **Bajada:** convierte la solicitud Responses (messages, tools, `enable_thinking`,
  temperature, max_tokens) en una solicitud chat completions.
- **Subida:** separa `reasoning`/`reasoning_content` (o bloques `<think>…</think>` embebidos)
  del content, normaliza `tool_calls` y reemite eventos Responses correctos
  (`response.output_item.added`, `response.reasoning_summary_text.delta`,
  `response.output_text.delta`, `response.completed`, …) con `sequence_number` creciente
  e ids consistentes.

### Inicio rápido

```bash
# 1. vLLM (parser de tools hermes + parser de reasoning)
vllm serve /ruta/a/Qwen3-8B-AWQ \
  --served-model-name qwen3-8b-awq --port 4100 \
  --enable-auto-tool-choice --tool-call-parser hermes \
  --reasoning-parser deepseek_r1

# 2. Bridge
dotnet run -- --port 4124 --upstream http://127.0.0.1:4100 --model qwen3-8b-awq

# 3. Proveedor de OpenCode (opencode.json)
{
  "providers": {
    "openai": {
      "api": "openai-chat",
      "settings": { "baseURL": "http://127.0.0.1:4124/v1" },
      "models": {
        "qwen3-8b-awq": {
          "tool_call": true,
          "compatibility": { "reasoningField": "reasoning_content" }
        }
      }
    }
  }
}
```

### Opciones de CLI

| Flag | Por defecto | Descripción |
|---|---|---|
| `--port` | `4124` | Puerto de escucha |
| `--listen` / `--host` | `127.0.0.1` | `0.0.0.0` expone el bridge en la red |
| `--upstream` | `http://127.0.0.1:4100` | URL base de vLLM |
| `--model` | `qwen3-8b-awq` | Nombre de modelo forzado upstream |
| `--thinking` | `events` | `events` = reenvía deltas; `off` = sin item reasoning (texto fundido) |
| `--temperature` | `0.2` | El valor del request vence a la config; InvariantCulture |
| `--max-tokens` | `4096` | Techo de salida (GPU); `--cpu-max-tokens` `2048` (CPU) |
| `--log` | `/tmp/qwen3-bridge.log` | Log JSONL de eventos |

### Endpoints

| Ruta | Propósito |
|---|---|
| `POST /v1/responses` | Traducción Responses ⇄ chat completions (thinking + tools + streaming) |
| `POST /v1/chat/completions`, `/v1/completions` | Passthrough a vLLM; renombra `reasoning` → `reasoning_content` |
| `GET /v1/models` | Passthrough |
| `GET /api/status` | Contadores y config en vivo |
| `GET /api/metrics` | Throughput de tokens (tps_out / tps_total) |
| `GET /api/config` | Config actual |
| `GET /swagger` | Swagger UI autocontenido (sin CDN) |

### Subcomando gate

```bash
Rochas.OpenCodeBridge.dll gate <repo> "<prompt>" [texto-obligatorio ...] [--config gate.json] [--review]
```

Ejecuta el agente fijado, exige commit nuevo, corre el build configurado y verifica
`forbidPaths` / `mustContain` de `gate.json`. En modo `--review`: commits locales son
libres, push (= nueva baseline) solo con aprobación; el rechazo hace `git reset --soft`.

### Setup probado

- vLLM `:4100` con `--tool-call-parser hermes --reasoning-parser deepseek_r1` (hermes ganó
  un bake-off 3/3; los parsers `qwen3_coder` y `qwen3_xml` fallaron con este modelo).
- OpenCode fijado con `--agent build --model openai/qwen3-8b-awq`.
- Cada solicitud genera una línea JSON en el log (`"model":"qwen3-8b-awq"`) — sin línea, sin modelo local.

---

## Français

Bridge HTTP léger en C# (.NET 9) permettant à [OpenCode](https://opencode.ai) d'utiliser
un serveur local [vLLM](https://github.com/vllm-project/vllm) (ex. Qwen3-8B) avec
**tool calling fonctionnel**, **thinking visible** et **streaming progressif réel**.

Aucun package NuGet externe — uniquement la BCL (`HttpListener`, `System.Text.Json`).

### Pourquoi ce projet existe

OpenCode v2 parle la nouvelle API **`/v1/responses`**, tandis que vLLM implémente
l'ancien protocole **`/v1/chat/completions`**. Pointer OpenCode directement sur vLLM
produit du texte brut, `finish_reason: stop` et aucune tool exécutée. Ce bridge traduit
entre les deux protocoles dans les deux sens.

### Fonctionnement

```
opencode ──POST /v1/responses──▶ bridge :4124 ──POST /v1/chat/completions──▶ vLLM :4100
         ◀── événements SSE response.* ──          ◀── chunks chat.completion ──
```

- **Descente :** conversion de la requête Responses (messages, tools, `enable_thinking`,
  temperature, max_tokens) en requête chat completions.
- **Montée :** séparation de `reasoning`/`reasoning_content` (ou blocs `<think>…</think>`
  inclus) du content, normalisation des `tool_calls` et réémission des événements
  Responses corrects (`response.output_item.added`, `response.reasoning_summary_text.delta`,
  `response.output_text.delta`, `response.completed`, …) avec `sequence_number` croissant
  et ids cohérents.

### Démarrage rapide

```bash
# 1. vLLM (parser de tools hermes + parser de reasoning)
vllm serve /chemin/vers/Qwen3-8B-AWQ \
  --served-model-name qwen3-8b-awq --port 4100 \
  --enable-auto-tool-choice --tool-call-parser hermes \
  --reasoning-parser deepseek_r1

# 2. Bridge
dotnet run -- --port 4124 --upstream http://127.0.0.1:4100 --model qwen3-8b-awq

# 3. Provider OpenCode (opencode.json)
{
  "providers": {
    "openai": {
      "api": "openai-chat",
      "settings": { "baseURL": "http://127.0.0.1:4124/v1" },
      "models": {
        "qwen3-8b-awq": {
          "tool_call": true,
          "compatibility": { "reasoningField": "reasoning_content" }
        }
      }
    }
  }
}
```

### Options CLI

| Flag | Défaut | Description |
|---|---|---|
| `--port` | `4124` | Port d'écoute |
| `--listen` / `--host` | `127.0.0.1` | `0.0.0.0` expose le bridge sur le réseau |
| `--upstream` | `http://127.0.0.1:4100` | URL de base de vLLM |
| `--model` | `qwen3-8b-awq` | Nom de modèle forcé upstream |
| `--thinking` | `events` | `events` = transmet les deltas ; `off` = sans item reasoning (texte fusionné) |
| `--temperature` | `0.2` | La valeur de la requête prime sur la config ; InvariantCulture |
| `--max-tokens` | `4096` | Plafond de sortie (GPU) ; `--cpu-max-tokens` `2048` (CPU) |
| `--log` | `/tmp/qwen3-bridge.log` | Journal d'événements JSONL |

### Endpoints

| Route | Rôle |
|---|---|
| `POST /v1/responses` | Traduction Responses ⇄ chat completions (thinking + tools + streaming) |
| `POST /v1/chat/completions`, `/v1/completions` | Passthrough vers vLLM ; renomme `reasoning` → `reasoning_content` |
| `GET /v1/models` | Passthrough |
| `GET /api/status` | Compteurs et config en direct |
| `GET /api/metrics` | Débit de tokens (tps_out / tps_total) |
| `GET /api/config` | Config actuelle |
| `GET /swagger` | Swagger UI autonome (sans CDN) |

### Sous-commande gate

```bash
Rochas.OpenCodeBridge.dll gate <repo> "<prompt>" [texte-obligatoire ...] [--config gate.json] [--review]
```

Lance l'agent figé, exige un nouveau commit, exécute le build configuré et vérifie
`forbidPaths` / `mustContain` de `gate.json`. En mode `--review` : les commits locaux
sont libres, le push (= nouvelle baseline) uniquement sur approbation ; le rejet exécute
`git reset --soft`.

### Configuration testée

- vLLM `:4100` avec `--tool-call-parser hermes --reasoning-parser deepseek_r1` (hermes a
  gagné un bake-off 3/3 ; les parsers `qwen3_coder` et `qwen3_xml` ont échoué sur ce modèle).
- OpenCode figé avec `--agent build --model openai/qwen3-8b-awq`.
- Chaque requête produit une ligne JSON dans le log (`"model":"qwen3-8b-awq"`) — pas de
  ligne, pas de modèle local.

---

## Deutsch

Leichte C#-HTTP-Bridge (.NET 9), die [OpenCode](https://opencode.ai) erlaubt, einen
lokalen [vLLM](https://github.com/vllm-project/vllm)-Server (z. B. Qwen3-8B) mit
**funktionierendem Tool Calling**, **sichtbarem Thinking** und **echtem progressivem
Streaming** zu nutzen.

Keine externen NuGet-Pakete — nur die BCL (`HttpListener`, `System.Text.Json`).

### Warum es existiert

OpenCode v2 spricht die moderne **`/v1/responses`**-API, während vLLM das alte
Protokoll **`/v1/chat/completions`** implementiert. OpenCode direkt auf vLLM zu zeigen
liefert reinen Text, `finish_reason: stop` und keine Tool-Ausführung. Diese Bridge
übersetzt zwischen beiden Protokollen in beide Richtungen.

### Funktionsweise

```
opencode ──POST /v1/responses──▶ bridge :4124 ──POST /v1/chat/completions──▶ vLLM :4100
         ◀── response.* SSE-Events ──          ◀── chat.completion-Chunks ──
```

- **Abwärts:** wandelt die Responses-Anfrage (messages, tools, `enable_thinking`,
  temperature, max_tokens) in eine Chat-Completions-Anfrage um.
- **Aufwärts:** trennt `reasoning`/`reasoning_content` (oder eingebettete
  `<think>…</think>`-Blöcke) vom Content, normalisiert `tool_calls` und gibt korrekte
  Responses-Events zurück (`response.output_item.added`,
  `response.reasoning_summary_text.delta`, `response.output_text.delta`,
  `response.completed`, …) mit steigenden `sequence_number`s und konsistenten ids.

### Schnellstart

```bash
# 1. vLLM (Hermes-Tool-Parser + Reasoning-Parser)
vllm serve /pfad/zu/Qwen3-8B-AWQ \
  --served-model-name qwen3-8b-awq --port 4100 \
  --enable-auto-tool-choice --tool-call-parser hermes \
  --reasoning-parser deepseek_r1

# 2. Bridge
dotnet run -- --port 4124 --upstream http://127.0.0.1:4100 --model qwen3-8b-awq

# 3. OpenCode-Provider (opencode.json)
{
  "providers": {
    "openai": {
      "api": "openai-chat",
      "settings": { "baseURL": "http://127.0.0.1:4124/v1" },
      "models": {
        "qwen3-8b-awq": {
          "tool_call": true,
          "compatibility": { "reasoningField": "reasoning_content" }
        }
      }
    }
  }
}
```

### CLI-Optionen

| Flag | Standard | Beschreibung |
|---|---|---|
| `--port` | `4124` | Hörport |
| `--listen` / `--host` | `127.0.0.1` | `0.0.0.0` macht die Bridge im Netzwerk erreichbar |
| `--upstream` | `http://127.0.0.1:4100` | Basis-URL von vLLM |
| `--model` | `qwen3-8b-awq` | Upstream erzwungener Modellname |
| `--thinking` | `events` | `events` = Deltas weiterreichen; `off` = ohne Reasoning-Item (Text fusioniert) |
| `--temperature` | `0.2` | Request-Wert hat Vorrang vor der Konfiguration; InvariantCulture |
| `--max-tokens` | `4096` | Ausgabelimit (GPU); `--cpu-max-tokens` `2048` (CPU) |
| `--log` | `/tmp/qwen3-bridge.log` | JSONL-Ereignislog |

### Endpunkte

| Route | Zweck |
|---|---|
| `POST /v1/responses` | Übersetzung Responses ⇄ Chat Completions (Thinking + Tools + Streaming) |
| `POST /v1/chat/completions`, `/v1/completions` | Passthrough zu vLLM; benennt `reasoning` → `reasoning_content` um |
| `GET /v1/models` | Passthrough |
| `GET /api/status` | Live-Zähler und Konfiguration |
| `GET /api/metrics` | Token-Durchsatz (tps_out / tps_total) |
| `GET /api/config` | Aktuelle Konfiguration |
| `GET /swagger` | Selbstenthaltenes Swagger UI (ohne CDN) |

### Gate-Subcommand

```bash
Rochas.OpenCodeBridge.dll gate <repo> "<prompt>" [pflichttext ...] [--config gate.json] [--review]
```

Führt den gepinnten Agenten aus, verlangt einen neuen Commit, führt den konfigurierten
Build aus und prüft `forbidPaths` / `mustContain` aus `gate.json`. Im `--review`-Modus:
lokale Commits sind frei, Push (= neue Baseline) nur nach Freigabe; Ablehnung führt
`git reset --soft` aus.

### Getestetes Setup

- vLLM `:4100` mit `--tool-call-parser hermes --reasoning-parser deepseek_r1` (Hermes
  gewann ein 3/3-Bake-off; die Parser `qwen3_coder` und `qwen3_xml` scheiterten mit diesem Modell).
- OpenCode gepinnt mit `--agent build --model openai/qwen3-8b-awq`.
- Jede Anfrage erzeugt eine JSON-Zeile im Log (`"model":"qwen3-8b-awq"`) — keine Zeile, kein lokales Modell.

---

## License / Licença / Licencia / Licence / Lizenz

MIT (adicione o arquivo `LICENSE` antes de publicar).

