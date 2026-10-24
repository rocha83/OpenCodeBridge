# Captura do harness principal do opencode

> **Harness atual (2026-10-24):** o system base hardcoded e os switches
> `mainHarness`/`harnessTails` estão em [`MAIN-HARNESS.md`](MAIN-HARNESS.md).
> Os arquivos `system.md`/`req-chat.json` abaixo são a captura histórica do
> master do opencode, mantida como referência.

Artefato gerado pela **sonda** da bridge (`--diagnostic 1`), rodando um devpipe
simples (`opencode run --standalone --auto -m openai/gemma-4-12b-build --agent build`).

## Arquivos
- `system.md` — system prompt consolidado enviado ao modelo (~14.010 chars / ~13,7 KB).
- `tools.json` — 12 tools enviadas (edit, glob, grep, question, read, shell,
  skill, subagent, webfetch, websearch, write, execute).
- `req-chat.json` — request chat completo (system + user + assistant + tool), 28.477 chars.

## Como reproduzir
1. Bridge com sonda e sem harness proprio:
   `--diagnostic 1 --main-harness 0 --harness-tails 0 --legacy-harness 0`
   (sem `--tool-blacklist`). O antigo `--harness 0` hoje desliga apenas as
   tails; para o tubo puro use os dois switches.
2. Rodar o devpipe apontando para a bridge (provider `openai`, :4124).
3. Extrair do `/tmp/bridge-gpu.log` o evento `traffic` com `mode=translate-stream-req`
   e o maior campo `req` (é o chat com system+tools completo).

## Separação: nativo x custom
- `NATIVE.md` — o que é do opencode (Code Mode, catálogo de tools, skills, env,
  metadados do modelo). **Não portar** para a bridge.
- `CUSTOM.md` — o que é nosso (agente build + AGENTS.md) e a **tabela de hardcodes**
  a remover (modelo, porta, contexto, temperatura, caminhos).
- `SUMMARY.md` — resumo proposto: núcleo universal + tails por role, sem hardcodes
  (~1.600 chars vs 14.010).

## Composição do harness (14.010 chars)
| Secao | Linhas | Chars | % |
|---|---|---|---|
| A. Papel do agente build + regras de tools | 1-10 | 1.236 | 8% |
| B. Your Model (provider/model id) | 11-14 | 111 | 0% |
| C. Code Mode (`execute` + catalogo de tools opencode) | 15-132 | 4.476 | 31% |
| D. available_skills (opencode, report) | 133-144 | 859 | 6% |
| E. Instrucoes globais AGENTS.md | 146-190 | 6.679 | 47% |
| F. Ambiente (cwd/platform) | 193-203 | 641 | 4% |
