# Pendings — Req Approval (Sim/Não sem mensagem na bolha)

Ponto exato em 2026-10-14 ~22:55 -03. Sessões opencode citadas:
`ses_ec30f3cd3ffe3y4OZwGJ5g0twi`, `ses_ec2f270b7ffejAYCpg09FWN8cO`
(news: coder via `coder-direct`), `ses_ec2deedc0ffe3ft2VRoDLaUJrX`.

## Estado dos serviços
- llama `:4100` = `Qwen2.5-Coder-14B-Instruct-Q4_K_M` 60k, `ok` (~11.3GB VRAM).
- Bridge `:4124` = build atual (diagnostic on). `bridge-gpu.service` systemd
  PARADO de propósito (brigava pela porta com a instância manual).
- Web `:4130` = build atual. Push em dia até `0195788`; trabalho Sim/Não
  abaixo está **não commitado** (de propósito, aguardando nova sessão).

## Análise fechada (últimas mensagens)
1. Coder não chamava tools (só prosa) — nem no opencode nem na Web.
   Causa raiz: llama.cpp não parseia pseudo-calls de volta (`<tools>`,
   `<tool_call>`, `<function>`, fence json, bare) em `tool_calls`, e o
   `/v1/responses` do llama não tem suporte a tools. Direto `:4100` ainda
   quebra no `Pattern must start with '^'...` (só a bridge faz pattern-strip).
2. Mitigação = **ToolsCoercion** (ex-DeepSeekToolCoercion, renomeada e
   model-agnóstica): coage quando o texto parseia, só se tools foram pedidas.
   Testes `ToolsCoercionTests` 26/26 (inclui plural, prefixo, `<function>`).
3. Modelo alucina prefixo `default.*` (imita o Code Mode do harness).
   Registro real usa nomes simples → strip de `default.` sem risco em 3
   pontos (bridge coerce x2, Web `TryBuildCall`).
4. Guardrail da bridge declara FORMA DE USO + catálogo
   (`glob/read/grep/fetch/display`, `write/edit`, `shell`, `git` leitura,
   `subagent`) + `echo` só em scripts. Com isso o coder passou a emitir
   nome exato sem prosa (`<function>{"name":"bash"...}` → `tool_calls`).
5. Segurança: `ShellInspector` já barra destrutivos (fail-closed).
   Adicionado: `reset --hard` sempre negado; `push`/`write`/`edit` exigem
   aval (`ConfirmGate`: "pode fazer" digitado ou botão).
6. AntiRepeat Web = contenção **silenciosa**: detecta (sonda Serilog +
   JSONL), suprime deltas pós-trip e pré-forward, aviso só no input,
   retry sem parar o fluxo (UI fica "Processando..." até o retorno
   diferente). Aviso atual = silêncio ("quando informada a repetição,
   não responda mais nada"). Merge de `assistant` seguidos corrige o
   400 do llama ("Cannot have 2 or more assistant messages...").

## Implementado e NÃO commitado ( Sim/Não )
- `Services/ConfirmGate.cs`: `Pending` (sessão → TCS), `NextId`,
  `WaitAsync` (120s), `Answer`.
- `ChatController`: `ConfirmGateAsync` (null = executa; Fail = nega),
  `WriteProgressAsync` com `approveId`, endpoint `POST /Chat/Approve`
  `{sessionId, approveId, approved}`, 2 sites (chat + bypass).
- `Views/Chat/Index.cshtml`: card `toolConfirm` com botões **Sim/Não**
  → `fetch /Chat/Approve`; clique não cria mensagem (só resolve o pending).
- `pattern-strip` (responses) agora loga `tools` (nomes que o opencode mandou).

## Pendências (retomar na nova sessão)
1. **Gate não disparou na sessão 19**: `write /tmp/aprovado.txt` executou
   direto (jail bloqueou certo: "fora do workspace"), sem `tool_confirm`
   no stream, mesmo com o código do gate no caminho. Diagnosticar
   (suspeita: `args`/nome no ponto do gate, ou DLL desatualizada).
2. E2E completo do Sim: provocar `write` no workspace, clicar Sim,
   conferir execução; clicar Não, conferir negação.
3. Extração tolerante p/ JSON inválido (caso seq 291: `fs` Node com
   quebras literais) — hoje não parseia, vem prosa pura.
4. Decidir destino do `coder-direct` no `opencode.json` (hoje → `:4124`).
