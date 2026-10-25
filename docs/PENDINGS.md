# PENDINGS — OpenCodeBridge

## SÉRIE CRIATIVIDADE (obrigatória) — só depois de GREEN total no Level 3 / WAR READY

**Obrigação:** subir a criatividade dos modelos em operação ao limite possível **sem**
alucinação, erro de manipulação de tools e sem aumento de repetição. A série só começa
com os três níveis (L1/L2/L3) fechados em verde nas raias Q4 **e** Q3.

**Alvo de temperatura (volta próxima de como era antes):**

| role | temp |
|---|---|
| `plan`, `orch` | 0.5 |
| `moe` | 0.8 |
| `build`, `scripter`, `exec` | 0.2 |

**Escopo da promoção:**

1. **Configs do opencode (TODOS os homes)** — `scripts/war/promote-creativity.sh`
   (dry-run por padrão; `--apply` grava com backup `.bak-temps-<stamp>`). Cobre
   `mint`, `dev-agent` e `moe-agent`, e atualiza também o rótulo
   `(Thinking On, 0.3)` do campo `name`. Modelo base (sem sufixo de role) fica intacto.
2. **Seeder da Web UI** — `Rochas.OpenCodeBridge.Web/Data/AgentSeeder.cs`:
   `PromoteRoleTemperatures` (idempotente; só `Gemma4-E4B%` ativos; preserva agentes
   custom). Exige `dotnet build -c Release` + restart de `bridge-web` para valer no banco.
3. **Aceite:** repetir a bateria que fechou o Level 3 verde com as novas temps e comparar
   com o baseline (repetição, tool-error, nomes/paths preservados). Qualquer regressão de
   alucinação/manipulação volta a temperatura do role afetado — não do conjunto.


## AVALIAÇÃO PENDENTE — parâmetros do llama × anti-repeat em camadas (2026-10-24)

**Origem:** sessões reais de 2026-10-23/24 (uso de tools por modelos locais limitados,
repetição constante). O fix de 2026-10-24 resolveu o que era medível — temperatura do
role vencendo o request (0.5 → 0.1 no `-build`), breaker de loop por falha no mesmo
alvo, cauda de tools nos dois caminhos —, mas **não** provou qual parcela do problema
vem dos parâmetros de amostragem do `llama-server`.

**Estado medido hoje (live):**

| camada | Q4 `:4100` | Q3 `:4110` | onde |
|---|---|---|---|
| `--repeat-penalty` | **1.08** | **1.08** | unit do llama (atual) |
| DRY (`--dry-multiplier`) | **0.0** (`--dry-penalty-last-n 1024`) | **0.0** (`1024`) | unit do llama (atual) |
| anti-repeat textual da bridge | on, janela 512, com retry | idem | `--anti-repeat*` |
| breaker de loop de tool | soft 3 / hard 6 | idem | `appsettings.json` |

**Hipótese a testar (não aplicar correção nova antes da medição):**

1. **Temperatura** é o fator dominante já medido: role `-build` rodava a 0.5 e corrompia
   identificadores (`proj-teste-001` → `999`, `opencodebridge` → `opencodedbidge`).
   O perfil por role (0.1) está ativo; falta medir 0.0/0.1/0.2 no MESMO enunciado de tools.
2. **DRY + repeat-penalty no llama** penalizam dígitos e trechos de caminho — exatamente o
   que precisa ser copiado verbatim numa tool call. Podem *causar* a mutação que o breaker
   tenta conter. Medir DRY off × on e repeat-penalty 1.0 × 1.1.
3. **Anti-repeat em duas camadas** (llama + bridge textual + breaker) pode estar
   sobre-corrigindo: cada camada empurra o modelo para longe do token exato (nome de tool,
   path) e o resultado vira deriva, não repetição. A ponte deve ser a última linha, não a
   primeira.

**Desenho mínimo da medição (mesma bateria, sem mudar código):**

- Enunciado fixo com tools (write/read/edit/glob+grep/git/sqlite) e alvo com dígitos.
- Matriz: `temp {0.0, 0.1, 0.5}` × `DRY {off, 0.8}` × `bridge anti-repeat {on, off}`.
- Métrica: artefato correto em disco (não texto), nº de chamadas por passo, repetições
  exatas do mesmo comando e identificadores corrompidos no comando emitido.
- Critério de promoção: só sobe para a unit o valor que reduzir chamadas/repetição **sem**
  perder o artefato; nenhuma camada nova na bridge antes disso.

**Regra de ouro até fechar:** estabilidade para os juniores vem de **menos** correção
automática e mais determinismo (temp baixa, contexto curto por tarefa, artefato como
aceite). Não adicionar heurística nova na bridge para compensar parâmetro de amostragem.

**Resultado da medição (2026-10-24, fechado para DRY):** matriz `param_matrix.py` (150
chamadas Q4 + 40 Q3, cenários `chat-vision` e `resp-path`): **DRY 0.8 = 0% de path exato
(0/64); DRY 0.0 = 100%**. `repeat_penalty` 1.0 × 1.1, temperatura 0.0/0.1/0.2, tails e
anti-repeat **não** mudaram nada. DRY desligado nos três units (`scripts/lab/llama-dry-fix.sh`),
`--repeat-penalty 1.1` mantido. Ver `LAB-SETUP.md` §10.

**Aplicado (2026-10-24):** `parallel_tool_calls: false` no request ao upstream —
decisão do lab de 1 call por step (passo determinístico e contável). Campo em
`BuildChatRequest` e nas duas respostas (shell) que emitimos. Referência do flag:
llama.cpp#21375.


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
- Robustez do standalone (sob `sudo`/carga): o harness chama `opencode run --standalone`
  (servidor **privado** — evita `Timed out waiting for the background service to start`)
  e o pipeline exporta `XDG_CONFIG_HOME` do usuário invocador (`SUDO_USER`) para o
  opencode achar o config de modelos/agents (senão: `Model unavailable`).
- Relatório com métricas: `RELATORIO-SPRINT-2026-10-19.md`.
- **Auto-gestão de testes (evolução)**: `BENCHTESTS.md` §11 — supervisor (orch Q4)
  gerencia a fila `E-*` sobre instâncias de si (Q4 build / Q3 exec), com segmentação
  azul→preta e aceite por artefato.

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
- [ ] Documentar flags `--prio`, `--harness-tails`, `--diagnostic`
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



### Riscos abertos (ordem de severidade)

| # | Risco | Evidência | Ação |
|---|---|---|---|
| D-01 | Treino executa dentro da requisição HTTP | `TrainingJobsController.Run` → `TrainingEngine.RunAsync` | fila + worker (`BackgroundService`), claim atômico, cancelamento cooperativo |
| D-02 | "Sem Python" violado no modo avançado | `TrainingEngine.cs` (`QLORA_SCRIPT`, default `sft_train.py`) | decidir: adaptador externo opcional ou remoção |
| D-03 | `TorchSharp` referenciado e não usado; backend é MLP `double[]` | `TrainingEngine.csproj`, `TorchSharpTrainingBackend` | renomear para o que é; provar TorchSharp (GPU, autograd, Adam) antes de prometer treino |
| D-04 | Filtros de relatório eram ignorados | `ReportService.QueryAsync` | **feito nesta rodada**: De/Até, banda/faixa de acurácia, família de modelo, status por código, 400 para filtro inválido |
| D-05 | JWT com chave fixa no código | `Api/Program.cs`, `Web/Program.cs` | mover para configuração/secret store |
| D-06 | Credenciais seed `admin/admin` e `teste@e2e.local/Teste@123` | `Web/Program.cs` | apenas Development; troca forçada |
| D-07 | Proxy aceita qualquer Bearer e envia `guest@123` ao upstream | `ProxyController.cs` | validar token; credencial configurável |
| D-08 | Binding escolhido é sempre o primeiro | `ProxyController.cs` | resolver por `model`/role |
| D-09 | `HarnessRun` gravado com `ProfileVersionId=0`/`EvidenceId=0` | `ProxyController.cs` | vincular perfil/versão/captura |
| D-10 | Chat UI órfã (7 rotas inexistentes) | `Views/Chat/Index.cshtml` × `ChatController` | portar endpoints ou remover a view |
| D-11 | `IRepository`/`IUnitOfWork` mortos | `Domain/Common` sem DI | implementar ou remover |
| D-12 | `Application` referencia SQLite/Dapper | `Application.csproj` | mover dependência para Infrastructure |
| D-13 | Relatórios carregam a tabela inteira e paginam em memória | `ReportService` | paginação/contagem em SQL (Fase 2) |
| D-14 | Sem FKs físicas nem teste de integridade referencial | `AppDb.Init` | relatório R29 + validação de escrita |

### Pendências de relatório (RP)

| # | Pendência | Situação |
|---|---|---|
| RP-01 | `DateFrom`/`DateTo` ignorados | resolvido (inclusivo por dia, UTC) |
| RP-02 | `AccuracyBand/Min/Max`, `ModelFamily`, `Mode`, `UserId` ignorados | resolvido para entidades que possuem a coluna/vinculo; demais retornam 400 |
| RP-03 | `AgentId` sem FK | 400 explícito até existir vínculo |
| RP-04 | `evidence_id` sem FK | pendente (validação + R29) |
| RP-05 | `approved_by` textual em versões de harness | pendente |
| RP-06 | Datas TEXT sem normalização | resolvido na comparação por dia |
| RP-07 | PDF rotulado como HTML | resolvido (`application/pdf`) |
| RP-08 | Página `/Reports` fora do menu | resolvido (menu + `/Approvals`) |
| RP-09 | Paginação em memória | pendente |
| RP-10 | Duas fontes de acurácia | pendente (regra por definição) |
| RP-11 | `Status` textual × `status_id` | resolvido para cards (`entity_statuses.code` ou id numérico) |
| RP-12 | Sem catálogo de definições | pendente (R1–R30) |

### Decisões abertas

1. `AgentId`: criar `agent_id` em runs/capturas ou remover do contrato.
2. Acurácia canônica: card ou melhor job do experimento.
3. Período default: sem filtro retorna tudo ou janela de 90 dias com aviso.
4. Export assíncrono a partir de quantas linhas.
5. `approved_by`: normalizar para `users.id` ou manter login.

### Bateria WAR v2 e frontpipe (2026-10-24)

- **WAR v2** (`scripts/war/run-war.sh`): 3 lanes simultâneas (Q4 `:4134` + Q3 `:4135`/`:4136`
  nos 2 slots do llama `:4110`), fila aleatória com `WAR_SEED`, checkpoint por
  `state/<lane>/<teste>.done` e retomada default (`--fresh` zera, `--list` inspeciona).
  Modos: `--q4` (só Q4, pula `E-science-cpu`), `--q3` (2 sessões Q3), default `all`.
  Evidência por step: log do teste + delta do log da bridge + `/api/metrics`/`/api/harness` +
  `llamacpp:*` + `nvidia-smi` antes/depois; `gpu-samples.csv` contínuo; parciais em
  `summary.tsv`/`status.json`.
- **A/B de flags** (`E-flag-*`, stub upstream): `context-guard` (200→413
  `context_compact_required`), `anti-repeat-retry` (1→2 chamadas e segunda geração),
  `coerce-narrated` (tool narrada não vira call → vira `read_file` + evento `coerced`),
  `tail-scope` (system prompt sem → com `MODO DE USO DAS TOOLS`). Pendente cobrir
  `--coerce-text-tools`, `--tool-strategy`, `--legacy-harness`, `--thinking-format`,
  `--stream-per-delta`, `--upstream-parser`, `--anti-repeat-abort/notice`,
  `--context-threshold` isolado e `--passthrough-responses`.
- **Frontpipe**: avaliação default sem webdriver, direto nos artefatos (Razor/TSX/CSS) com
  sonda estática equivalente ao `getComputedStyle`; `--front-webdriver` reativa o Playwright.
  Pendente: parâmetro geral para execução headed na Web UI `:4130` (hoje já aceita
  `FRONT_WEBDRIVER_URL` e `HEADED=1`; falta o runner/CLI unificado).
- **Execução**: bateria completa ainda não rodada nesta rodada (regra: testes só no final).
  Build Release verde e `bash -n` OK no script.
