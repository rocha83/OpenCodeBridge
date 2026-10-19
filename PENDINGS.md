# PENDINGS — OpenCodeBridge

## Suíte E2E: trilha standalone (opencode real) + role build — feito 2026-10-19

- Testes complexos com tools **não** rodam com thinking on (rambling/alucinação).
  A suíte usa sempre o **role build** (`gemma-4-12b-build`, thinking off):
  `TestContext.SuiteModel` aplica o sufixo de `--role` (default `build`) ao modelo
  servido; `--model` explícito vence. Science/mission/devpipe usam `SuiteModel`.
- Novo modo **`--standalone`**: desvia cada E2E para o **opencode CLI real**
  (`opencode run`) com o **mesmo enunciado**, em vez de HTTP direto na bridge.
  - Conjunto `E-standalone-{oi,tools,write,devpipe,science,mission-tools}`.
  - `--standalone-agent` (default `build`; `exec` -> `llama/` E2B).
  - `--standalone-model` (provider/model) sobrepõe; senão deriva do agent.
  - Fase 3 do `run-e2e-pipeline.sh` roda build (GPU) + exec (CPU).
- Script auxiliar: `run-opencode-standalone.sh` (mesmas 6 tarefas, direto no CLI).
- `harnessToolsTail` ganhou regras de **F#** e de **formatação numérica**:
  - F#: `System.Math.Sin/Cos/Sqrt/PI` (nunca `open System.Math`); sem `+. *. /.`;
    `printfn "%.6f" valor`; `dotnet run --project`.
  - C#: `value.ToString("F6", InvariantCulture)`; **nunca** `%.6f`.
- Runbook: `RUNBOOK-E2E.md` (seção "Modo standalone").

## Permissao interativa de tools (RiskToolsAlerts) — plano para fechar

**Mudanca de principio (2026-10-18):** manipular tools NAO pode mais ser radical
omissiva. O padrao e PERMISSIVO: deixa a chamada acontecer, e o que esta na
blacklist vira **alerta + pergunta** ao usuario — o humano decide, a bridge nao
recusa por conta propria. `--risk-ask 0` restaura a recusa antiga (fail-closed).

O que ja esta em `main` (build 0 avisos / 0 erros nos 2 projetos):

| caminho | mecanismo |
|---|---|
| opencode (`/v1/responses`) | tool `question` nativa, com motivo `papel` ou `risco` |
| Web UI (2 loops `ChatController`) | `ConfirmGate` (botoes Sim/Nao / aval digitado) |

- **Bridge** — `ToolBlacklist` e `RiskToolsAlerts` caem na MESMA primitive
  `PendingRisks`: emite `question`, espera `function_call_output`.
  - `RiskQuestionArgs(tool, args, role, reason)` — texto por motivo:
    `"papel"` (tool fora do role) / `"risco"` (comando perigoso).
  - `RiskScanAnswers(input)` — SIM: `RiskApprovedOnce[ToolSig]` (1 execucao,
    `TryRemove`, TTL `--risk-ask-minutes`, default 30) + aviso no historico do
    MODELO reemitindo a chamada exata. NAO: `RiskRefused` + aviso "nao execute".
  - `RiskToolsAlerts` por role = regex (`--risk-tools-alerts` /
    appsettings `riskToolsAlerts`) casando `nome + argumentos` normalizados.
  - `IsRiskCall` passa a aceitar papel+risco; precedencia: **avaliacao do
    usuario > blacklist** (a blacklist so pergunta, nao barra).
- **Web** — `ConfirmGate.LoadRiskAlerts(config)` no startup; `NeedsConfirmation`
  e `ConfirmGateAsync` ganharam `agentId`; risco declarado tem precedencia no
  rotulo (`LabelFor`).

### Pendencias para concluir
1. **Config real** nos dois `appsettings.json`:
   - bridge: `riskToolsAlerts` (ex. `Plan`: `git\s+push`, `\brm\b`, `\bdd\b`,
     `\bmkfs\b`, `reset --hard`, `/dev/sd`).
   - Web: `RiskToolsAlerts` (`{ "<agentId>": [ ... ] }`) — id = id do agente.
   - units systemd: `--risk-tools-alerts "Plan=...;Orch=..."` se preferir CLI.
2. **Reiniciar** `bridge-gpu bridge-cpu` + o servico da Web apos a config.
3. **Testes ao vivo**:
   - opencode: Plan pede `write`/`git push` -> item `function_call` `question`
     na saida, `event: tool-ask` no log; SIM -> `risk-approved` + chamada real;
     NAO -> `risk-answer approved=false`, nada roda.
   - Web: MESMA tool + padrao -> evento `tool_confirm` no SSE + `/Chat/Approve`;
     `bash -n` nao pergunta (inspetor mantem `Validation` liberado).
4. **Suítes**: `dotnet run --project Rochas.OpenCodeBridge.Test -c Release
   --no-build -- --skip-e2e` (ultimo verde: PASS=23 FAIL=0) + `Web.Test`.
5. **`HISTORY.md`** com a secao nova + **sync do remaster**
   (`units/`, `web-ui/appsettings.json`).

### Nao quebrar
- tools proibidas continuam ANUNCIADAS ao modelo (a chamada que vira pergunta;
  nunca se esconde o schema).
- `question` no `ToolExecutor` da Web segue negada no executor — quem pergunta
  e o `ConfirmGate` APOS a traducao (o executor nao deve perguntar 2x).
- `write`/`edit` do `ConfirmGate` nao deixam de ser cobertos pelo `RiskToolsAlerts`.

## tools-dev-pipe (plano do enunciado de 10 passos)
- Belt `tools-dev-pipe` em `BeltUiTests` + fase `toolsdevpipe`: enunciado via `/Chat/Stream` (agente 8, `AutoApprove=true`, sem delegar).
- Adaptações: sem webdriver no bridge — clima SP via tool `fetch` (`wttr.in`); nome aleatório `devpipe-*.txt` gerado pelo modelo, teste casa por padrão; zip via `shell`.
- Aceite: `ToolCalls` com shell/write/edit/read/grep/fetch + 2 commits no `git log`; `ChangedFiles` com o `.txt`; `File` final com `AWC` + clima; `shell` com `devpipe.zip`.
- Roda com: `-- --ui --belts tools-dev-pipe --phase toolsdevpipe`.

## Calibrando... no frontend (plano)
1. Backend `POST /Chat/Calibrate {agentId}`: busca agente, histórico efêmero (sem sessão/DB/SSE), bridge `/v1/chat/completions` com `enable_thinking:false`; ping + corretivos até 4 + 1 reforço; retorna `{ceded,balanced,attempts,decode}` (tok/s do `timings`).
2. Frontend: badge amarelo pulsante `Calibrando...` junto ao engine; dispara em `agentSel.onchange` e na carga; depois mostra `Equilibrado 30.4 tok/s` (verde) ou `Sem equilíbrio` (vermelho, sem bloquear).
3. CSS `.dot.calib` amarelo blink.
4. Teste integração `calibrate-endpoint` (modelo real, timeout alto): 404 p/ agente inexistente, 200 + `ceded` p/ agente build real.

## Rule: source code en-US, comments pt-BR (project-wide)
- Code identifiers (functions/methods/classes/vars) MUST be en-US (C# + JS).
- Comments YES pt-BR. UI strings, prompts, docs also pt-BR.
- Backlog: rename PT method names (ex.: `CalibrationProbeTests.CedeAposCorretivo`, `DesisteSeNuncaCeder`, `ContrasteChatterAbortaDelirio`, `EquilibrioAposPrimeiroDone`, `RecaiNoReforcoNaoEquilibra` → en-US equivalents) + sweep for other PT identifiers, with build + suites green per file.

## Concluído nas últimas 10 solicitações (2026-10-15)

### 1. OCR/Vision no ReadToolHandler ✅
- **Problema**: `ReadToolHandler` só lia texto; mentia dizendo "suporta imagens/PDFs" mas não tratava.
- **Feito**: Reescrito `ReadToolHandler.cs` com:
  - Detecção de imagem por extensão (jpg, png, gif, webp, bmp, tiff)
  - Conversão para base64 + data URL (`data:mime;base64,...`)
  - Retorno no formato **multimodal OpenAI** (`{"type":"image_url","image_url":{"url":"data:...","detail":"high"}}`)
  - Suporta JPG, PNG, GIF, WebP, BMP, TIFF (até 20MB)
  - PDF: mantido "não implementado" (precisa PDFium/PdfPig)
  - Build verde ✅

### 2. Configuração de modelos Qwen2.5 no opencode.json ✅
- Atualizado `opencode.json`:
  - 9B (build/orch/plan): ctx 262k / out 32k
  - 4B (build/plan): ctx 81920 / 16k
  - Agents 9B/4B alinhados com `AgentSeeder.cs`

### 3. Prioridade de execução por modelo ✅
- **9B Q4 (GPU :4100)**: `--prio 2` (high) — modelo principal
- **4B Q3 (GPU :4111)**: `--prio 0` (normal) — executor secundário
- Flags `--prio` (-1/0/1/2) aplicadas nas systemd units

### 4. Histórico documentado no HISTORY.md ✅
- Commit `a760f6e`: "docs: prioridade de execução por modelo (9B high, 4B normal)"
- Stress concorrente documentado: 9B ~15 tok/s, 4B ~20 tok/s
- Prioridades: 9B `--prio 2` (high), 4B `--prio 0` (normal)

### 5. ReadToolHandler com suporte multimodal ✅
- Imagens → base64 → formato OpenAI multimodal: `{"type":"image_url","image_url":{"url":"data:mime;base64,...","detail":"high"}}`
- Extensões suportadas: jpg/jpeg/png/gif/webp/bmp/tiff (até 20MB)
- PDF: mantido "não implementado" (precisa PDFium/PdfPig)
- Build verde ✅

### 6. Configuração Qwen2.5 9B/4B no opencode.json ✅
- 9B (build/orch/plan): ctx 262k / 32k, temp 0.4
- 4B (build/plan): ctx 81920 / 16k, temp 0.5
- Agents 9B/4B alinhados com `AgentSeeder.cs`

### 6. Prioridade de execução por modelo (--prio) ✅
- **9B Q4 (GPU :4100)**: `--prio 2` (high) — modelo principal
- **4B Q3 (GPU :4111)**: `--prio 0` (normal) — executor secundário
- Flags `--prio` (-1/0/1/2 = low/normal/medium/high) aplicadas nas systemd units

### 7. ReadToolHandler multimodal completo ✅
- Imagens → base64 → formato OpenAI multimodal: `{"type":"image_url","image_url":{"url":"data:mime;base64,...","detail":"high"}}`
- Extensões suportadas: jpg/jpeg/png/gif/webp/bmp/tiff (até 20MB)
- PDF: mantido "não implementado" (precisa PDFium/PdfPig)
- Build verde ✅

### 8. Configuração Qwen2.5 9B/4B no opencode.json ✅
- 9B (build/orch/plan): ctx 262k / 32k, temp 0.4
- 4B (build/plan): ctx 81920 / 16k, temp 0.5
- Agents 9B/4B alinhados com `AgentSeeder.cs`

### 9. Prioridade de execução no systemd ✅
- **9B Q4 (GPU :4100)**: `--prio 2` (high) — modelo principal
- **4B Q3 (GPU :4111)**: `--prio 0` (normal) — executor secundário
- Flags `--prio` (-1/0/1/2 = low/normal/medium/high) aplicadas nas systemd units

### 10. Histórico documentado e commitado ✅
- Commit `a760f6e`: "docs: prioridade de execução por modelo (9B high, 4B normal)"
- Commit `a760f6e` feito localmente

---

## O QUE FALTA (PENDING)

### 1. Testar OCR real end-to-end ❌
- [ ] Passar imagem via tool `read` e validar passagem multimodal pela bridge → llama
- [ ] Validar que o modelo (Qwen) consegue ler texto da imagem
- [ ] Testar diferentes formatos: PNG, JPG, WebP, TIFF

### 2. Suporte a PDF real (OCR) ❌
- [ ] Integrar PDFium ou PdfPig para extração de texto de PDF
- [ ] Alternativa: converter PDF → imagem → OCR via ReadToolHandler

### 3. Testes automatizados do ReadToolHandler ❌
- [ ] Unit tests: ReadToolHandlerTests (imagem válida, arquivo grande, extensão inválida, PDF)
- [ ] Integration test: devpipe com imagem no enunciado

### 4. Otimização VRAM (ainda crítico: 11.6GB/12GB) ⚠️
- [ ] Testar Q4 192k ctx vs 128k para aliviar VRAM
- [ ] Avaliar Q3 128k vs 96k para 4B
- [ ] Testar 2 slots 9B + 4B concorrentes com ctx menor

### 5. Sincronização PROD ❌
- [ ] Push commits locais para origin/main (DEV: a760f6e, 1af0510, 28733d5, etc)
- [ ] Sincronizar PROD (`/usr/src/opencodebridge`) com DEV
- [ ] Deploy web + bridge com flags atuais

### 6. Testes de integração completos ❌
- [ ] Rodar suite completa: `--ui --belts azul,verde,roxa,marrom,preta,tools-dev-pipe`
- [ ] Validar faixas Azul → Preta com enunciados reais
- [ ] Testar devpipe standalone + UI

### 7. Documentação pendente ❌
- [ ] Atualizar README com instruções de OCR/Vision
- [ ] Documentar flags `--prio`, `--harness`, `--diagnostic`
- [ ] Atualizar CHANGELOG.md

---

## PRÓXIMO PASSO SUGERIDO

1. **Testar OCR real**: rodar `opencode run --standalone --agent build --auto "read imagem.png"` e validar resposta multimodal
2. Se OK → commitar tudo e push para origin/main
3. Sincronizar PROD e rodar suite completa de belts

---

*Última atualização: 2026-10-15*

## Politica de tools em endpoint unico (`/api/permission/check`) — estado no commit

**Feito e verificado nesta rodada (sem build novo no push):**
- Bridge e a dona da politica: `PermissionOf(modelId, tool, args)` -> `allow` /
  `ask` / `deny`, com motivo `risco` (RiskToolsAlerts) ou `papel` (ToolBlacklist).
- `POST /api/permission/check` (irmao de `GET /api/harness`, mesmo padrao
  "fonte unica: a web consulta aqui") devolve `{action, reason, role, tool, notice}`.
- Web: `ConfirmGate` perdeu a politica local (`LoadRiskAlerts`, `IsRisk`,
  `RiskLabel`, `LabelFor`, o `agentId` condicional). `ConfirmGateAsync` consulta
  a bridge; falha de rede = `ask` (fail-closed).
- Config real: `riskToolsAlerts` por role no `appsettings.json` (precedencia
  sobre a CLI), `riskAsk: true`, `riskAskMinutes: 30`. Plan/Orch/MoE **leem**
  git; `git push`, `rm`, `dd`, `mkfs`, `shutdown`, `reset --hard`, `/dev/sd*`
  alertam. Units perderam `git,push,sync` da blacklist.
- Matriz medida no endpoint: 8/8 (plan shell allow, plan write ask/papel,
  plan `git log` allow, plan `git push` ask/risco, build write allow,
  build `git push` ask/risco, build `rm -rf` ask/risco, build `ls` allow).

**PENDENTE 1 — a pergunta da bridge precisa ser APRESENTADA pela Web**
(achado depois do commit; patch pronto em `/tmp/opencode/patch_question_present.py`,
**nao aplicado**): no caminho padrao (`Orchestration:UseResponsesBypass =
/v1/responses`), a bridge **ja** emite a tool nativa `question` (com
`PendingRisks[call_id]`); a Web hoje manda essa chamada ao `ToolExecutor`, que a
nega ("Sem interacao com o usuario neste fluxo") — ou seja, no caminho padrao a
permissao interativa **nao** chega a aparecer. O patch: `ConfirmGate.IsQuestion`,
`AskText`, `RiskHeader`, `AnswerFor` (Sim = primeiro rotulo, Nao = ultimo ->
"Autorizar"/"Negar", que e o que `RiskAnswerYes` reconhece) e
`PresentQuestionAsync` devolvendo o rotulo no MESMO `call_id`;
`ConfirmGateAsync` ganha `bridgeDecided: true` no call site do bypass (a bridge
filtrou: nao re-decidir) e remove o `NeedsConfirmation` morto.

**PENDENTE 2 — bateria de testes com cobertura >= 80%**: falta (a) testes de
integracao do endpoint na suite da bridge (matriz role x tool x args) e
(b) instrumentacao de cobertura. **Bloqueio honesto: nao ha ferramenta de
cobertura disponivel offline** — `coverlet`/`Microsoft.CodeCoverage` nao existem
no cache NuGet (cache vazio), sem `dotnet tool` global e sem .nupkg no disco;
instalar depende de rede. Alternativas se a rede voltar: `coverlet.msbuild` nos
2 projetos de teste, ou `dotnet-coverage` como tool global.
