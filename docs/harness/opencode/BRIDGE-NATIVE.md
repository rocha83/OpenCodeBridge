# Harness nativo da bridge x harness nativo do opencode

Base: dump operacional em `docs/harness/opencode` (`system.md` 14.010 chars,
`tools.json` com 12 tools, `req-chat.json` 28.477 chars), capturado pela sonda
(`--diagnostic 1 --harness 0 --legacy-harness 0`) rodando
`opencode run --standalone --auto --agent build`.

## 1. O que o opencode manda (medido)

| Bloco | Linhas | Chars | % | Origem |
|---|---|---|---|---|
| A. Papel do agente build + regras de tools | 1-10 | 1.236 | 8% | nosso (`agents/build.md`) |
| B. Your Model | 11-14 | 111 | 0% | nativo (metadado de modelo) |
| C. Code Mode + catálogo `opencode.*` | 15-132 | 4.476 | 31% | **nativo** (cliente) |
| D. available_skills (opencode, report) | 133-144 | 859 | 6% | nativo |
| E. AGENTS.md | 146-190 | 6.679 | 47% | nosso |
| F. Environment (data, cwd, plataforma) | 193-203 | 641 | 4% | nativo |

Nativo total ≈ 6.087 chars (43%). Nosso (A+E) ≈ 7.915 chars (57%).

## 2. Veredito sobre o `execute` (a pergunta do loop)

`execute` **não é do modelo nem da bridge**: é a tool #12 do cliente opencode
(`tools.json`), descrita como *"Run JavaScript in a confined Code Mode runtime
to script tool calls and HTTP requests"*, e o bloco `# Code Mode` (4.476 chars)
instrui o modelo a empacotar chamadas ali dentro. A bridge não tem sandbox JS —
o modelo só vê `execute` porque o **cliente** o anuncia no request.

Consequência prática: em enunciado multi-passo o 12B escolhe `execute` para
"resolver tudo num script" e entra em loop (caso `tools-dev-pipe`: 12+ chamadas
idênticas, 55.932 chars, zero artefatos).

Mitigação implementada na bridge (`--tool-strategy`, default on):

| Camada | O que faz | Evento no log |
|---|---|---|
| Catálogo | remove a tool `execute` do array de tools (grammar do llama não pode escolhê-la) | `tool-strip` |
| Prompt | troca o bloco `# Code Mode` pela nota "tools diretas, sem sandbox JS" (preserva skills e o AGENTS.md) | `harness-strip` |
| Resgate | se o modelo ainda emitir `execute{tools.X(...)}` (script com N chamadas), converte em N tool calls na ordem (cap 8) | `tool-unwrap` |
| Guia | script puro de Node (`require`/`fs`) vira um `shell` cujo resultado manda refazer com tools diretas | `tool-guide` |

## 3. Mapa: o que portar do opencode para o harness nativo

| Seção nativa | Decisão | Motivo |
|---|---|---|
| Code Mode / catálogo `opencode.*` | **não portar** (e remover do request) | infra do cliente; sem sandbox na bridge |
| available_skills | **não portar** | catálogo da versão do opencode |
| Your Model | **não portar** | metadado; já existe em `appsettings`/perfil |
| Environment | **não portar** | cwd/data/plataforma vêm da API |
| Núcleo de comportamento (SUMMARY §1) | **portar** (adaptado) | é o que descreve "como trabalhar" |
| Tails por role (SUMMARY §2) | já cobertos por `agents/*.md` | duplicar engorda o prompt |

## 4. Harness nativo da bridge (o que temos em código)

- `HarnessToolsTail` (2.006 chars): tools, shell/redirect, fetch vs curl, git,
  zip, N passos, retry variando, build fix, `dotnet new`, arquivos grandes, math
  qualificada, F#.
- `HarnessGuardTail` (opcional, `appsettings`): guarda extra por instalação.
- `WithFormula`: aplica as tails **só** em modo Harness para cliente sem sufixo
  de perfil (web). OpenCode (model com sufixo) continua tubo puro — com o strip
  do Code Mode acima.
- Sem hardcodes de infra no texto (modelo, porta, ctx, temperatura e caminhos
  ficam na config) — regra do `CUSTOM.md`.

### Núcleo agregado agora (faltava em relação ao SUMMARY §1)

`QUALIDADE E EVIDENCIA`: contexto primeiro (docs/AGENTS + git log) · gravar
todos os artefatos de uma vez, sem stub/TODO/placeholder · correção por
`edit`/`sed` no trecho exato · ACEITE executado com evidência anexada ·
build/teste vermelho = não concluído · fail-fast honesto (tool/modelo/comando
inexistente não se inventa) · idioma do usuário.

## 5. Números

| Cenário | Chars de harness por request |
|---|---|
| opencode nativo (A-F) | ~14.010 |
| opencode + bridge atual | 14.010 − 4.476 (Code Mode) + ~700 (nota) ≈ **10.2k** |
| web/bridge nativo (HarnessToolsTail + núcleo) | 2.006 + ~640 ≈ **2.6k** |

## 6. Como verificar

```bash
# dump de referência
ls docs/harness/opencode

# strip ativo (bridge :4124)
curl -s localhost:4124/api/status | grep tool_strategy

# evidencias no log da bridge
grep -o '"event":"\(tool-strip\|harness-strip\|tool-unwrap\|tool-guide\)"[^}]*' /tmp/bridge-gpu.log | tail

# belt headless (sem UI)
dotnet Rochas.OpenCodeBridge.Web.Test/bin/Release/net9.0/Rochas.OpenCodeBridge.Web.Test.dll \
  --opencode-run --belts tools-dev-pipe --workdir /tmp/opencode/belts
```
