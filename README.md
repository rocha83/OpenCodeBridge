# OpenCodeBridge (+ Web)

Bridge C# (.NET 9) entre o OpenCode (`POST /v1/responses`) e backends
OpenAI-compatíveis, + UI web MVC com harness agentivo próprio. Sem SDKs no
núcleo da bridge (só BCL).

## Componentes

- `Rochas.OpenCodeBridge` — tradutora Responses ⇄ chat/completions + thinking
  + tool calling; perfis por sufixo no model; subcomando `gate`.
- `Rochas.OpenCodeBridge.Web` — chat com bypass Responses + loop de tools
  nativas (`function_call` → `ToolExecutor` → `function_call_output`),
  agentes plan/build, subagente no mesmo modelo, gate de escopo, grupo
  "Ferramentas" por bolha com vínculo `tool_calls.message_id/sub`.
- `Rochas.OpenCodeBridge.Test` / `.Web.Test` — suites console (exit 0/1).

## Modelos compatíveis

- Qwen3-8B AWQ, Qwen3.5 9B / 4B (perfis `-plan` 0.6/events, `-build` 0.4/off,
  `-orch` 0.6/events), Qwen2.5-Coder-3B (coerção texto→tool na linha CPU),
  DeepSeek Coder V2-Lite (executor textual no padrão oficial).

## Caminho bypass

Por padrão a UI vai em turno único a `{bridge}/v1/responses` (passthrough:
só remove itens `reasoning` e funde `instructions`/system no inicial —
`NormalizeSystemFirst` + `StripReasoningItems`, com dry-run em
`/api/sanitize`). O parse do OpenCode é aproveitado ao máximo: tools no
formato Responses padrão, `tool_choice` do chamador, deltas de
`function_call` traduzidos ao vivo, `usage` real no stream.

## Build e testes

```sh
dotnet build Rochas.OpenCodeBridge.slnx -c Release
dotnet run -c Release --project Rochas.OpenCodeBridge.Test -- --skip-e2e
dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --units-only
# E2E das faixas pela UI (navegador + engine viva):
dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --ui --belts azul --phase batch
```

## Endpoints (bridge)

`/v1/responses`, `/v1/chat/completions`, `/v1/models`,
`/api/status|translate|convert|sanitize|config|metrics`, `/swagger`.

## Docs

- `AGENTS.md` — regras do operador.
- `AGENTS-PROMPTS.md` — system prompts versionados (fonte dos agentes).
- `HISTORY.md` — histórico de construção (fonte de continuidade).
- `PENDENCIAS.md` — só o que ainda não foi feito.

## English (short)

C# bridge between OpenCode (`/v1/responses`) and OpenAI-compatible
backends, plus an MVC web UI with its own agentic tool loop. Bypass ships
native Responses tools end-to-end; profiles `plan`/`build`/`orch` tune
temperature and thinking. Build with `dotnet build
Rochas.OpenCodeBridge.slnx -c Release`; details in `HISTORY.md`.
