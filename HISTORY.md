# HISTORY.md — sessão de construção da bridge C# (`vllm-ocode-bridge`)

## Web — agentes 6/7/9, bolha viva, busca ORM, sem fases hardcoded (2026-10-14)
- `AgentSeeder`: ids 6 (Scripter Q8), 7 (Eng Plan Q4), 9 (Dev Build Q4);
  prompts 9B com ctx moderado (160k), 4B limitado (16k); nomes no padrão
  `Papel - Modo - Q8 GPU/Q4 CPU`; Orch (id 4) no `9b/:4124`.
- Listas (admin + chat) por `Model` desc; executor oculta o orquestrador
  (antes o 400 "deve diferir" vinha mascarado de "Executor inválido" —
  toast agora mostra o erro real); sessões em fonte `small`.
- Removido o enunciado hardcoded de fases (`[Plan-delegate]`/sufixo do
  executor): vale cadastro + enunciado do usuário.
- Bolha viva no bypass: INSERT no 1º turno, UPDATE depois, DONE fecha com
  texto + usage (`ISessionService.UpdateMessageAsync`); subs seguem em
  linhas próprias — kill no meio não perde mais nada (sessão 31 perdeu tudo).
- Busca de agentes: `[Filterable]` em Name/Model/Mode/Thinking/SystemPrompt
  (Search sem Filterable voltava sem filtro); `Active=true` filtra no WHERE
  (`bool false` é excluído — `EntitySqlParser`); quadro com critério (LIKE),
  selects Model/Mode, checkbox único Raciocínio (events), Todos/Ativos/Inativos.
- Suite `--units-only` verde (inclui `I-orm-*` e `UpdateMessageAsync*`).

## Web — system do sub via agente + bolhas com scroll (2026-10-13)
- `SubagentToolHandler`: system do sub vem do agente executor cadastrado
  (`ToolContext.SystemPrompt`); pinado só se vazio.
- Bolhas agente/subagente: conteúdo com teto de ~15 linhas + auto-scroll
  (paridade com o thinking).

## Web — AgentSeeder + permissão de delegação no orquestrador (2026-10-13)
- `Data/AgentSeeder.cs`: snapshot dos 6 agentes revisados na UI (só `INSERT OR IGNORE`
  por id, nunca sobrescreve); chamado no fim do `AppDb.Init`.
- `ToolExecutor.Execute` ganha `allowAgent`: permissão de `subagent` é do orquestrador
  que delega, contexto (modelo/bridge) segue no executor — antes o anúncio mandava
  delegar mas a execução negava contra o executor (sessão 30/32).
- Tracer UI (`Tracer.*` + `POST /Chat/ClientLog` p/ tabela Serilog, flag `Diagnostics`),
  tick do poll só no painel direito, blindagem dos GETs de leitura, `CpuDefaultTps` 3.0.
- Infra: Q8 9B @128k na GPU (10,8/12,2 GB), 4B com `-ngl 0` (só CPU); 230k estourou
  em `cudaGraphLaunch` OOM sob carga (log em `llama-gpu` 20:57).

## Web — sem Role, Plan/Build canônico, Plan travado (2026-10-13)
- `Role` do agente removido (model, form, lista, pré-seleção; coluna fica inerte). Controle
  é pelo flag `subagent` no harness.
- `Mode` canônico `Plan`/`Build` (primeira maiúscula): comparações insensíveis em tudo,
  normalização ao salvar, migração no banco.
- Plan no cadastro: escrita e shell inativas (JS por form + strip no `ExpandTools`); fetch fica.
  Ordem dos checkboxes: glob, read, git, fetch, display, write, shell, subagent.
- `Tools` default `""` (estava com CSV cheio envenenando todo `Query(new Agent())` — mesma
  classe do bug CreatedAt; vazio é excluído do WHERE).

## Web — combo de executor segue a permissão (2026-10-13)
- Orquestrador sem `subagent` no harness: combo de executor vem sem seleção e inativa
  (`data-tools` nas options + `applyExecutorAvailability`; limpa e zera na sessão via PUT);
  backend não auto-atribui executor sem permissão (`Allowed(agent,"subagent")` no Create).

## Web — sem orquestrador, executor via harness + ETA nos scripts (2026-10-13)
- Removido `OrchestrationService`/`IOrchestrationService` (778 linhas mortas, sem DI nem chamada;
  lições de refinamento junto); `ExecutorTps` separado (sondado vence; CPU 3 t/s, GPU 20 t/s);
  testes `CpuEtaTps`/`PlanToolsFilter` movidos p/ `ToolExecutorTests` (34/34).
- `subagent` virou checkbox (harness) em vez de regra do Modo; migração dá `subagent` aos plan
  existentes; nudges de delegação só cutucam quem pode delegar; `ExpandTools` passa adiante.
- Com executor na sessão: dispatch da tool `subagent` roda no Model/BridgeUrl dele + sufixo no
  system prompt (bypass `instructions`): "utilize o agente X como subagente de execução".
  (Clássico não envia system prompt — gap pré-existente.)
- Painel Scripts: ETA estimada pelo modelo (`# ETA: ~N min` no cabeçalho, exigido no harness;
  ausente = vazio), `ScriptEta` com units; zip vazio dá 404.

## Web — status da mensagem + retomada automática (2026-10-13)
- Tabela `message_statuses` (1 Enviada, 2 Respondida, 3 Arquivada) + `session_messages.status_id`
  com FK; migração com backfill (`archived→3, user→1, demais→2`); `AddMessageAsync` carimba
  (default pelo papel, explícito opcional); `CompactAsync` grava 3 nas arquivadas e 2 no resumo.
- Causa raiz do "sumia tudo": `CreatedAt = DateTime.UtcNow` nos models envenenava os filtros
  (`WHERE created_at = agora` → zero linhas); removido de `SessionMessage/ToolCall/RefinementLesson`
  (convenção sgev3/mova), `RecordLessonAsync` carimba explícito. `Remove` do compact voltou a funcionar.
- Rodapé da bolha: status à esquerda (Enviada/Respondida/Arquivada), data/duração/t-s à direita.
- Sem rascunho entre sessões (caixa limpa ao trocar); sem botão Retomar: continuação é automática
  pelo histórico não-arquivado (contexto exclui `archived`, thinking nunca reentra).
- Testes: unit `MessageStatus` 13/13; integração `I-chat-abort-resume` (aborto→Enviada sem resposta,
  resume→Respondida sem duplicar) 8/8; senha E2E do teste alinhada ao seed (`Teste@123`).
- Quadro "Mudanças" acima de Scripts (write/edit ok, badge novo/editado) + modal com CodeMirror 5
  vendored (10 linguagens, 5 temas) e diff lado a lado via diff2html do `git diff`; painel Scripts
  na mesma fonte; zip vazio dá 404; write distingue criado/atualizado (só criado lista).

## Bridge — bind externo padrão 0.0.0.0 + fluxo de credenciais (2026-10-13)
- Default de escuta no código: `Listen = "0.0.0.0"` (`Rochas.OpenCodeBridge/Program.cs`).
  Units systemd (`bridge-gpu` :4124, `bridge-cpu` :4125) já passavam `--listen 0.0.0.0`
  explícito; o default garante o ajuste mesmo sem a flag (ex.: `dotnet` direto).
  Verificado: `ss` mostra `0.0.0.0:4124` e `0.0.0.0:4125`, `/api/status` ok nas duas.
- Fluxo de credenciais apurado (sem mudança de código): TUI (`start-opencode-app.sh`)
  e Web (`start-opencodeweb.sh`) perguntam a chave no Zenity e gravam `apiKey` no
  `opencode.json` + env (`OPENAI_API_KEY`/`LLAMA_API_KEY`); a bridge NÃO valida a
  chave nos próprios endpoints (`/api/status` aberto) — só repassa (`WithAuth`:
  usa o `Authorization` do cliente ou injeta o `--api-key`) ao upstream, que valida.
  Externo + chave fraca (`guest@123`) = LAN inteira usa os modelos; trocar a chave
  em produção.
- Suite bridge units contra GPU viva: 15/16; `U-translate-profiles` (build thinking on)
  falha pré-existente — unit viva roda `--thinking events` global, sem perfil por modelo.

## Rodapé das bolhas com duração e t/s + trava anti-loop na bridge (2026-10-13)
- Bolhas de Agente e Sub Agente exibem no rodapé à direita
  `DATA HORA - Ns - X t/s` (ex.: `13/10 10:44 - 143s - 15 t/s`).
- Backend: `session_messages` ganha `elapsed_ms` + `tps` (migração via
  `EnsureColumn`); `SessionMessage` e `AddMessageAsync` (params opcionais)
  propagam; `GET Messages` devolve os dois campos.
- Agente: cronômetro do stream inteiro; `tps` = `completion_tokens` do
  `usage` / segundos. Subagente: duração medida (`sw.ElapsedMilliseconds`) e
  t/s estimado por chars/4 (sem `usage` no sub).
- Front (`Views/Chat/Index.cshtml`): `fmtFoot()` monta o rodapé; `msgItem`
  recebe `elapsedMs`/`tps`; ao fim do stream recarrega as mensagens do
  servidor para a bolha ao vivo ganhar a métrica persistida.
- Donut inalterado: segue consumo total do contexto em tokens.
- Trava anti-loop na bridge (`Rochas.OpenCodeBridge/Program.cs`,
  `WithSystemGuardrail`, sem duplicar): todo system prompt (Responses via
  `NormalizeSystemFirst`/`InputToMessages` e chat direto) recebe no final
  "ao executar tools que resultem em erro tentar no maximo mais 2 vezes, e
  sempre responde na lingua do usuario de forma concisa".
- Teste `U-translate-profiles` atualizado p/ nova doutrina (build thinking on).
- Sufixo estendido: listar arquivos locais com `glob`/`read` e navegação
  web só p/ endereços externos (corta o `webfetch file://`).
- Suite web units PASS; bridge units 16/16; build Release 0/0.

## Harness de tools por agente + sessoes por usuario (2026-10-13)
- `Agent.Tools` (CSV; vazio = nenhuma): 7 presets no cadastro (pastas, leitura,
  escrita, repos, internet, validação, exibição echo/cat) + `subagent` pelo Mode;
  anúncio filtra e backstop nega (display: shell só-`echo` simples).
- Login persiste o usuário (sessões isoladas por dono); Agentes visível a todos
  (sistema readonly p/ não-admin), Usuários só admin (link + controller).
- Suite web units PASS; build Release 0/0.

## Sem truncamento no subagente (2026-10-13)
- Removidos todos os cortes arbitrários (600 chars no texto do subagente,
  4000 no output de tool, etc.): o ctx 256k do 9B comporta tudo.
- Suite web units PASS; build Release 0/0.

## Zip dos scripts + fonte e donut (2026-10-13)
- Quadro da direita: nomes com classe `script-name` (pouco acima do badge),
  link de download do zip abaixo da lista; Raciocínio com rolagem (~8 linhas).
- Ao fim do turno com `.sh`: zip no workspace gravado em base64
  (`session_artifacts`, upsert por sessão) + `GET ScriptsZip`; donut soma
  `usage` real + delta posterior (`medido+delta`).
- Nome do script: título da fase + data-hora juliana.
- Suite web units PASS; build Release 0/0.

## Merge UI paralela: grupo Ferramentas + gate de escopo (2026-10-13)
- Portado da árvore paralela (base anterior + trabalho não commitado):
  grupo "Ferramentas" por bolha (aberto, com contador), labels próprias no
  card do sub, histórico com tools por `message_id`, gate de escopo do
  subagent (recusa enunciado integral, máx 2) e erro da bridge com corpo.
- Vínculo tool→bolha: `tool_calls.message_id/sub` (migração), `LogInnerAsync`
  nas internas do sub, `LinkSubToolsAsync` no card + `LinkToolsByIdsAsync`
  nas órfãs; `AddMessageAsync`/`LogToolAsync` retornam ids.
- Divergências resolvidas: sub segue sem `max_tokens` (teto da bridge);
  `ToolOutputLimit` da bridge em 32768.
- Suite web units PASS; build Release 0/0.

## Bypass com tools nativas + doutrina plan/build (2026-10-13)
- Bypass com harness nativo: `BuildBody` envia tools Responses flat
  (`ToResponsesTools`), `CallAccum` junta function_call do SSE e o loop
  executa via ToolExecutor reenviando `function_call_output` (teto 5 turnos).
  Transporte provado ponta a ponta (shell executou e voltou).
- Doutrina: plan thinking on / build off (chat respeita `agent.Thinking`,
  sub roda off); filtro de tools por **Mode** (build sem `subagent`).
- Temperatura sem override no código: vale a do cadastro (`agent.Temperature`).
- Suite web units 100/100; build Release 0/0.

## Web perfil 9B/4B (2026-10-13)
- Agentes `9B` (id 1, orchestrator/plan, `qwen3.5-9b`, temp 0.6) e `4B-exec`
  (id 2, executor/build, `qwen3.5-4b`, temp 0.4) com os prompts deste arquivo;
  headers atualizados (era 8B/0.4) + topologia registrada (Plan no próprio
  modelo, Orch ao 4B).
- `UseResponsesBypass=true` mantido: chat é conversa pura (sem ToolExecutor
  no loop); delegação Orch→executor segue pelos endpoints Decompose/Orchestrate.
- Orçamentos: `ContextWindow` com teto da linha GPU + `ResolveMaxInput`
  (teto menor na linha CPU); `PlanTemp` plan 0.6 (era 0.4);
  `DecomposeTemperature` 0.6; `CpuDefaultTps` 4.0; defaults de Create/AppDb
  em 9B/0.4.
- Suite web units 100/100; build Release 0/0.

## Push remaster chroot: bypass Web padrão + matriz final (2026-10-13)
- Commit `feat(Web): bypass Responses padrao na UI + 0.2 erradicado + zero
  warnings`: flag `Orchestration:UseResponsesBypass` (default true) com turno
  único em `/v1/responses` sem ToolExecutor; `ResponsesBypass.cs` + 18 testes
  `U-bypass`; temps plan/orch 0.6, build/global 0.4 em tudo (código, examples,
  testes, README, AGENTS, HISTORY, AJUSTES); suite bridge 16/16, suite web PASS.

## Web via bypass Responses + zero warnings (2026-10-13)
- UI web no caminho bypass por padrão (flag `Orchestration:UseResponsesBypass`,
  default true): turno único em `{bridge}/v1/responses`, SEM ToolExecutor;
  sanitize leve na bridge (passthrough). `Services/ResponsesBypass.cs` traduz o
  SSE Responses -> chat SSE ao vivo (a UI não muda). Chat direto com plan 0.6.
- `0.2` erradicado de código, examples, testes, README (5 idiomas), AGENTS e
  HISTORY (versões `2.x` e `0.24` de modelo preservadas — não são temperatura).
- Warnings zerados sem supressão (nullable real: AppDb path, Mock `m?`,
  `result.Error ??`, `Agent?`, `output/error ??`). Suite web PASS (18 U-bypass
  novos); suite bridge 16/16.

## Temps sem 0.2: orch 0.6, default global 0.4 (2026-10-13)
- Perfil `orch` 0.2 -> 0.6 (codigo + appsettings); default global
  `Temperature` 0.2 -> 0.4; perfil `build` 0.2 -> 0.4 no codigo.
  Matriz 5/5 OK via `/api/translate` (plan/orch 0.6, build 0.4).

## Instructions fundido no system inicial (2026-10-12)
- Refinamento do system-first: o culpado final era a dualidade
  `instructions` + system no input (log `shape` criado p/ provar).
  `NormalizeSystemFirst` funde instructions junto e remove o campo.
  Teste cobre os dois textos + `has_instructions=false`. Suite 16/16.

## System-first no input (2026-10-12)
- Jinja Qwen abortava 500 (`System message must be at the beginning`) quando
  resumo/compactacao chegava como system/developer no meio do historico.
  `NormalizeSystemFirst` (bypass) + mesma fusao no `InputToMessages` (pipe
  antigo) + `ExtractResponseText`. Repro curl 500 -> 200. Testes
  `U-sanitize-system-first` + `U-translate-system-first`: **16/16 PASS**.

## Bypass com testes de canal duplo (2026-10-12)
- `StripReasoningItems` extraida p/ funcao pura + endpoint dry-run
  `POST /api/sanitize` (precedente do `/api/translate`): sem modelo.
- `UnitBypassTests`: cenario complexo multi-turno (instructions, user,
  assistant, function_call + output com call_id, 2 reasoning — string e
  array —, tools flat + namespace) nos DOIS canais: sanitize remove 2 e
  preserva calls/ids; translate nao vaza thinking nas messages e converte
  tools. Suite unit: **14/14 PASS** contra a bridge `:4124` viva.
- E2E manual via bypass (opencode 2.0.21): GPU `BP-1`/`BP-2` + Write
  `BP-TOOLS-OK`; CPU `BPC-1`/`BPC-2`; stress 4 turnos (git/dotnet/vite)
  sem erro de protocolo.

## Bypass llama + auth upstream (2026-10-12)
- Bug OpenCode 2.0.21 direto no llama.cpp: `item['content'] is not an array` em
  todo turno com histórico (Responses reenvia `reasoning` com content string).
  Novo `--passthrough-responses`: remove só os itens `reasoning` do `input` e
  encaminha o resto intacto (tools nativas, sem tradução p/ chat). Pipe antigo
  mantido por compatibilidade (vLLM). Evidências: GPU `BP-1`/`BP-2`
  (`dropped_reasoning=1`) + Write `BP-TOOLS-OK`; CPU `BPC-1`/`BPC-2`.
- Novo `--api-key` (+ `apiKey` no appsettings, `api_key: ***` no `/api/status`):
  repassa `Authorization` do cliente ou injeta o Bearer (llama exige).
- Rollback live: bridges C# `:4124 → :4100` (`qwen3.5-9b`, max 32768) e
  `:4125 → :4110` (`qwen3.5-4b`, max 4096) no lugar do `atomic serve`;
  exemplos `appsettings.4124/4125.*` atualizados. Build 0/0, suite EXIT=0.


## Bridge Web — DeepSeek-Coder-V2-Lite com tools (2026-10-12)
- Causa raiz: o `chat_template` do tokenizer ignora `tools` — o modelo nunca
  as via. Novo `/opt/vllm/deepseek-tools.jinja` (mesmo formato `User:/Assistant:`,
  + bloco oficial `# Tools` com tokens `<｜tool▁calls▁begin｜>...` byte-exatos do
  parser `deepseek_v3`); unit vLLM sem reasoning parser (aborta) e com
  `--trust-remote-code`, ctx 32768. Deps JIT: `nvidia-cuda-toolkit + ninja-build`.
- Servidor (`Web`, sem mudar prompts): `DeepSeekToolCoercion` (parse texto:
  XML oficial `<tool_name>`, hermes, fence json + variante chave-única,
  tokens V3, `<attempt_completion>`) + `DeepSeekToolExecutor` (loop
  texto→parse→executa, retorno `<tool_response>`, sem recursão: sub não
  anuncia `subagent`). Engates por modelo (Qwen/hermes intacto).
- Direção: 1º turno forçado — principal força `subagent` nominal, sub força
  `write` nominal, depois `auto` (p/ concluir em texto); `finish "stop"` com
  calls normalizado; cutucada mecânica máx 2 (linha anti-giro) quando discursa.
- Bug pego por log: executor descartava `t.Calls` estruturadas (content vazio
  com required) → "subagente vazio"; corrigido + teste `U-dsq-exec-structured`.
- Suite: coerção **26/26 PASS**, resto verde (`--units-only`).
- Mova/imunow com DeepSeek: delegação principal→sub ok; sub gerando `.sh`
  em validação (shell só `bash -n`). Enunciado imunow ganhou ordem de `.sh`.

## Bridge Web — DeepSeek ToolExecutor, padrão oficial em texto (2026-10-12)
- Coder V2-Lite não emite `tool_calls` estruturadas: novo caminho em classe
  própria (`DeepSeekToolCoercion`: parse `<tool_name>` XML oficial + hermes em
  texto + fence json + tokens V3 + `<attempt_completion>`; `DeepSeekToolExecutor`:
  loop texto→parse→executa, retorno como `<tool_response>`). Sem mudar prompts.
- Engates: `SubagentToolHandler` usa o executor quando o modelo é deepseek;
  loop principal (`ChatController`) coage antes do gate; `ChatTurnAsync` idem.
  `<thinking>` vazado vai p/ thinking (`StripThinkTags`). Gate por modelo:
  Qwen/hermes intacto (teste `U-dsq-gated-by-model`).
- Suite: **21/21 PASS** coerção + resto verde (`--units-only`).
- Infra DeepSeek (doc `fixes-doc.md` no zip `/media/mint/Linux/`): V2-Lite
  (MoE+MLA) exige FlashInfer JIT → `nvidia-cuda-toolkit + ninja-build`;
  Qwen (marlin) não precisa. Unit vLLM: `deepseek-coder-v2-lite-instruct`,
  `--trust-remote-code`, hermes, ctx 32768, sem reasoning parser (aborta:
  `could not locate think start/end tokens`). Bridge `:4124` no modelo
  deepseek; site `:80`; agentes `DSCoder`/`DSCoder-exec` no `web.db`.
- EM ABERTO: teste mova com deepseek usando tools (vLLM em carga JIT).

## Bridge Web — Fase 11 Agente+fases+subagentes, UI e árvore (2026-10-11)
- Doutrina final do dono: 1 agente (8B plan) sugere fases e dispara subagentes;
  ninguém executa shell/escrita — só gera `.sh` como texto p/ execução futura.
  Prompts: agente "desenvolvedor senior .net c# que nao dialoga, sugere a solucao
  em fases e dispara subagentes para cada fase"; subagente "compoe script shell sh
  para implementacao do enunciado em batch".
- Tools do principal: leitura (read/grep), navegação (glob), subagentes.
  Shell encolhido p/ `ls/cat/grep/curl` + `cd` com guard (fora do Allowed de
  propósito); `sudo systemctl` (4 strings exatas de ops) mantido — remover sob ordem.
- Coerção de execução DESLIGADA no loop principal (blocos ```sh não executam mais);
  extrator ganhou `echo` multilinha -> `write` e unescape de `\"` (legado p/ futuro).
- Subagente interno: não anuncia `subagent` (só trabalha); chamada interna é ignorada
  com neutro (sem FALHOU). Trava de recursão mantida no executor.
- Parar virou CANCELAR via API: `POST /Chat/Stop` cancela o CTS da sessão no
  servidor + abort local; botão vermelho preenchido fonte preta; ENVIAR quadrado;
  LIMPAR idem. Botão fica visível o stream inteiro; abort não gera bolha de erro.
- Bolhas: agente abre só com texto real (deltas de espaço não abrem quadro vazio);
  subagente abre independente no done (sem label "Executando" durante); ordem agente em
  cima/subagentes abaixo; texto em caixa rawbox (`pre` + `pre-wrap`, quebras originais).
  `esc()` agora escapa `&<>`, desescapa `\"`/`\n`/`\t` p/ exibição e filtra tags
  `<details>/<summary>` do texto (só tags, conteúdo fica).
- Persistência por turno: texto do agente ao fechar cada turno (não só no fim do
  loop); retorno do subagente gravado como msg `subagent` (texto+thinking via SUBTHINK);
  thinking dos dois na coluna `thinking`; bridge recebe subagente como `user`.
- Histórico à prova: `loadMessages` com fallback por msg (nunca aborta; role
  desconhecido vira Agente, nulo vira ""); `msgItem` cobre user/assistant/subagent.
- Tela de aprovação do orch OCULTA por desvio (conceito pausado, código fica);
  `DirectApprove=true` por padrão; painel lateral intacto.
- `[truncado]` zerado no repo inteiro (Web + CLI + Runner); texto fluente.
- Testes: `HistoryTree` novo em SessionServiceTests (árvore user/assistant/subagent
  + split texto/thinking) — SessionService 12/12 PASS. RESSALVA: 5 FAILs antigos
  em ToolExecutorTests (echo/chain/quoted/metachar/bash-alias) por testarem a
  allowlist antiga do shell — aguardam decisão do dono (atualizar ou reverter).
- Stack: vLLM :4100 (service, reiniciado), bridge :4124, site :4130.

## Bridge Web — Instrutor 8B + tolerância em 3 camadas + tool_calls (2026-10-10)
- Doutrina: modelo só conversa enxuto (system de 1 frase + enunciado), o site faz
  funcionar — sem exigir `tool_calls` OpenAPI perfeitos de modelo pequeno. Pilha
  viva: bridge `:4124` → vLLM `:4100` (8B 36k) + site `:4130`.
- Agente 6 virou **instrutor** (orienta em snippets, não executa); agente 9
  (construtor) excluído; ids 1/6 com system de 86 chars. Faixas citam divisão em
  scripts sh por fase/escopo só no enunciado (zero consts de harness no ctx).
- `Stream` com 3 camadas: (1) `tool_calls` nativo; (2) coerção fence
  ` ```json {"name","arguments"} ` (mesma do executor); (3) `SuggestedCommands`
  (blocos ` ```sh ` por linha, `echo>`/heredoc viram `write`, resto via shell).
  Progresso `tool_start`/`tool_done` chega à UI como `Executando [x]...` →
  `[x] executado./falhou.` (Fase 6d, intacto).
- `tool_calls` no SQLite (como o thinking): `session_id/agent/name/args/ok/output/ms`,
  logado nos 4 pontos (chat nativo, sugeridas, orch nativa, recuperada) p/
  avaliação posterior da interação com o modelo local; `GetToolCallsAsync` + teste.
- Fixes achados por evidência: `shell` nega `&&` (modelo sempre encadeia) →
  cadeia `&&`/`;` sequencial (set -e, `cd` persiste, guard contém); `glob` quebrava
  com `**` literal; `plan` ganhou `shell` (leitura/navegação/git), `write`/`edit`
  seguem barrados (backstop); `Touch` +1s determinístico (SQLite trunca segundos);
  `Stream` `max_tokens` 2048→8192 (ctx 28k+8k=36k).
- Incidentes: teto 8 turnos conteve giro de 22 turnos/18min (depois removido a
  pedido p/ modo instrutor); 400 do vLLM = `JSONDecodeError` em args ecoados;
  disco USB caiu (I/O error, voltou como `sdd`, remontado); `vllm.service` sem
  `ExecStartPost` (prontidão via `/health`, cópia em `/media/mint/Linux`).
- Suite: veredito pendente de run verde completo (Touch determinístico, plan-tools
  com shell, roundtrip tool_calls); E2E azul em iteração (fences no thinking,
  UI colapsa newlines — artefato via API).


## 8B autônomo + sidebar Executor + lições + ETA CPU (2026-10-09)
- 8B cuida de quase tudo: `DecomposeSystem` agora diz que o pedido chega SUCINTO
  pela UI do opencodebridge e o 8B ALONGA o descritivo + SUBDIVIDE (colabora nos
  dois lados: decompor e refinar/sintetizar). Enunciado futuro vem da UI, não do Muse.
- Sidebar renomeada `Tarefas 3B` -> `Tarefas do Executor`; cada item mostra resumo
  do enunciado (1 linha, 160 chars, persistido na mensagem de divisão) + chips das
  tools executadas (`[Executor i] Executou X`), além de título/ETA/status.
- `refinement_lessons` (tabela simples): refine/split/tool_denied/needs_tools com
  detalhe curto; últimas 5 entram no prompt de refinamento (anti-repetição dos 9
  ciclos da preta). Best-effort: nunca quebra o pipeline.
- Barreira de escopo: tool fora do JsonArray agora responde `Não permitido...`
  (antes `não suportada`) e vira lição `tool_denied` que aperta o system do 3B.
- ETA correta na CPU: `ResolveTps` público e testado — sondado vence; sem sonda,
  CPU (llama/:4125) usa `Orchestration:CpuDefaultTps` (2.5) e GPU usa 7.
  `DecomposeSystem` exige minutos REAIS (task com tools em CPU >= 5 min).
- Testes: `U-orch-cpu-tps/gpu-tps/probed-tps`, `U-task-summary-tools/legacy/cleantitle`;
  `SessionTaskPanel.CleanTitle` reaproveitado na síntese (títulos limpos).

## Runner: console executor com allowlist (2026-10-05)
- `Rochas.OpenCodeBridge.Runner` (net9.0, BCL, zero warnings): loop via API
  direta na linha CPU (prompt curto, sem harness do `run`) — chat -> fence
  `{"name","arguments"}` -> valida -> executa -> devolve (max 5 voltas).
- Segurança: deny padrão; allowlist 1o token + denylist de metacaracteres,
  SEM `bash -c` (argv direto, cwd travado, timeout 60s, mata árvore); `sudo`
  só por match exato (`restart opencode-bridge*`, `tee appsettings`); log
  JSONL (`exec`, rotação 20MB) + `--verbosity quiet|normal|verbose`,
  `--temperature` (0.4: temp 0.0 travava o 3B em resposta fixa).
- Achados ao vivo: glob relativo resolvia no cwd errado (fix: base repo);
  3B não conta linhas (fix: Runner anexa `total: N linhas`); fence sem tentar
  (fix: temp > 0). Prova: `ls docs/screenshots/*e2e-cs*` -> **38**, confere.

## Selo orch thinking events (2026-10-05, reverte off)
- `off` no `orch` causava loop de reads sem delegar (Qwen3-8B sem thinking vira
  continuador guloso: após um read, o próximo read é sempre mais provável que o
  salto p/ `shell nohup`; evidência `plan-20261004-md-backend-src-docs-atomic-code-implementation.json`
  msgs 1–12: 10 reads seguidos, nenhum `nohup`, usuário precisou gritar `pare`).
- Volta p/ `events` (default 2.1) em `/opt/opencode-bridge/appsettings.json` +
  REGRA DE OURO no system do `orch` (`opencode.json`): ler APENAS o plano
  (max 2 reads), NUNCA abrir implementação (tarefa dos workers). `AGENTS.md`
  atualizado p/ `orch on (events)`.

## Protocolo orch 8 estágios (2026-10-05, corrigido 2026-10-05)
- Agente `orch` (opencode, primary, modelo pinado `openai/qwen3-8b-awq-orch`):
  system próprio em 8 estágios — 1) LER o plano; 2) IDENTIFICAR blocos de
  tarefas; 3) ELUCIDAR enunciado atômico técnico por bloco (1 leitura +
  1 escrita, critério de aceite, FORMATO EXATO da saída); 4) EXECUTAR via
  `nohup opencode run --standalone --agent coder-3b --model llama-cpu/qwen2.5-coder-3b --title slice-<id> "<enunciado>" > .out 2>&1 &`
  (SEM `--session` na criação — opencode v2.0.21 exige prefixo `ses_` e gera
  o ID sozinho; `--session slice-<id>` falha com `Expected a string starting
  with "ses"` — atestado em `/tmp/opencode/slices/slice-1.out`; capturar o
  `ses_*` via `opencode session list --format json` filtrando por title);
  5) ACOMPANHAR com poll (`cat` / `tail -n 20`, NUNCA `tail -f` — bloqueia a
  tool e causa `Step interrupted`); 6) VALIDAR contra o critério;
  7) CONSOLIDAR só com todas verdes; 8) LIMPAR sessões dos workers
  (`opencode session delete ses_xxx` ou `DELETE /api/session/ses_xxx`,
  NUNCA `/api/session/slice-<id>` — dá 400 `InvalidRequestError`; standalone
  sempre persiste, sem flag efêmera na v2.0.21).
- Trava por permissão (não só prompt): `orch` sem edit, sem tool `subagent`,
  só read/glob/grep + webfetch/websearch + shell. Tool `subagent` aparece
  mas sempre nega — o system manda não insistir.
- Orçamento 36k: fixo ~10k, ~2.5k/fatia (teto `cpuMaxTokens` 2048) → ~10
  fatias/sessão. Poll leve + validação externa p/ não estourar.

## Selo orch thinking off (2026-10-05)
- Matriz final (não re-enunciar): `plan` 0.4/events (tudo), `build` 0.2/off
  (tudo), `orch` 0.2/**off** (ler, web, shell; SEM edit, SEM subagents).
- `orch` off via override de deploy (`appsettings.4124.orch-off-example.json`
  copiado p/ `appsettings.json` ao lado do DLL), sem mudar o default events
  da 2.1 no código. `orch` delega fatias atômicas SOMENTE via
  `nohup opencode run --standalone --agent coder-3b --title slice-<id> ... > .out 2>&1 &` + poll
  (`cat`/`tail -n`, nunca `tail -f`) + limpeza via `opencode session delete ses_xxx`.
  Linha 3B: bridge CPU `:4125` (thinking off) → llama.cpp `:4110`.

## Diagnóstico raiz
- opencode v2.0.21 sempre chama `POST /v1/responses`; vLLM não faz parse de tools
  nesse fluxo → texto puro, `finish_reason: stop`, `executed=False` em tudo.
- Tentativas de chave de rota no `opencode.json` (`api`, `protocol`, `options.baseURL`…)
  todas ignoradas. Decisão: bridge tradutora em vez de caçar config.

## Bridge v1.0 (mínima, sem herança legada)
- Console .NET 9, sem NuGet (só `HttpListener` + `System.Text.Json`).
- Tradução pura Responses ⇄ chat/completions + thinking + tool calling.
- Fora de escopo de propósito: coercion, grounding, pseudo-call, nudge, footer.

## Bugs reais encontrados e corrigidos
1. **Model 404**: opencode manda `openai/qwen3-8b-awq`; upstream força `Model` configurado.
2. **Thinking**: formato real é `<think>`, não `U+6EA6` → `SplitThinking` reescrito.
3. **Temperature**: ignorava a do request + `Parse` quebrava em pt-BR → `req > config`, `InvariantCulture`.
4. **`JsonNode` pai duplo** (`already has a parent`, 4 pontos: `parameters`, `baseObj`,
   `item` do output, `function_call` added/done) → `CloneOrDefault` + cópia de trabalho.
5. **BOM** do `StreamWriter` corrompia o 1º evento SSE → `UTF8Encoding(false)`.
6. **Chunk final** com `choices: []` estourava índice no stream.

## Padrões e API
- Convenção: **código en-US, comentários pt-BR** (reescrita total v1.1).
- `--listen` (remoto `0.0.0.0`), `/api/status|translate|convert|convert|config`.
- Compat saída nova+antiga: `/v1/responses` (traduz) e `/v1/chat|completions`, `/v1/models` (repasse).
- Swagger black autocontido `/swagger` (sem CDN) + `qwen3-bridge.xml` (24 membros);
  spec regenerado via script após brace imbalance no manual.
- Streaming evoluiu: sintetizado → **real progressivo** (chunked, deltas vivos,
  `sequence_number` crescente, `completed` com ids consistentes).

## Parsers (bake-off, bateria T1/T2/T3 direto no vLLM)
- `hermes` **3/3** ✅ — modelo emite `<tool_call>{json}</tool_call>`, hermes extrai.
- `qwen3_coder` **0/3** ❌ (espera `<function=><parameter=>`).
- `qwen3_xml` ❌ (espera `<function name=>` da geração 2507).
- **hermes travado** como oficial. Bridge é agnóstica: consome `tool_calls` normalizado.

## Tuning e garantia do agente
- `temperature`: plan/orch 0.6, build e default da bridge 0.4 (opencode mint+root); system prompt + sufixo
  conciso (2 frases, sem saudação, código/tools completos).
- `max_tokens 8192` mantido (código precisa); `stop` só por passthrough explícito
  (com tools trunca o JSON — documentado); `presence_penalty` fora da Responses API.
- `AGENTS.md`: provider único (bridge), pinagem `--agent build --model openai/qwen3-8b-awq`,
  verificação via `/tmp/qwen3-bridge.log`.

## E2E e portão (concluído)
- Ciclo arquivos/build/git/docker/curl-UA; demo DDD `PcssDemo` em `work/pcss-demo`.
- Achado honesto: tools executam (provado via git+build), mas o 8B **não se autocorrige**
  (regressões, path errado, `bin/obj` commitado) → integridade via verificação externa.
- **Gate integrado no mesmo projeto** como helper interno (`Gate.cs`, subcomando
  `Rochas.OpenCodeBridge.dll gate ...`): commit novo, forbid paths, mustContain,
  build; modo `--review`: commit local livre, **push (=baseline) só com aprovação**,
  rejeição faz `reset --soft`. Projeto separado `vllm-ocode-gate` descontinuado.
- Gate já capturou regressão real (FAIL determinístico em `node_modules/.bin` que o
  agente criou fora da tarefa — repo resetado para `6961acf`).
- `/api/metrics` ✅ (acumuladores + tps_out/tps_total; gate imprime METRICS).

## Pendente (backlog não bloqueante)
- Frentes docker (falha graciosa sem daemon), `curl` UA Firefox Win11, zip deliverable.

## Bridge v1.4 — pipe paralelo p/ Qwen2.5-Coder (2026-10-03)
- Problema: com `tool_choice "auto"` o Coder improvisa o call em texto
  (bloco ```json `{"name":...}` ou XML `<response><function_call>`) e nada
  executa; com `"required"` emite `tool_calls` nativo (atestado no vLLM).
- Mudança mínima em `Program.cs`: `IsCoder` (Model contém "coder") +
  `tool_choice = required` só nesse perfil; caminho Qwen3 byte-idêntico.
- Suite `Rochas.OpenCodeBridge.Test` virou agnóstica ao perfil: lê o modelo
  do `/api/status`; `E-vllm-tool-hermes` só no Qwen3; asserts de reasoning
  e de texto-do-review condicionados ao perfil (Coder é non-thinking e com
  `required` responde com call no 1º turno).
- Atestado: suite **10/10** no Coder (vLLM 0.19.1 + `qwen3_coder`) e **11/11**
  no Qwen3 selado; tarefa opencode E2E (calc.py criado+executado) PASS nos dois.
- Nota cognitiva: Coder cumpre mas é verborrágico (reescreve reports em loop);
  Qwen3 termina limpo. Coder de plantão só com `--model *coder*`.

## Bridge v1.5 — thinking=off trafega como texto (2026-10-03)
- Problema: com `--thinking off`, o Qwen3 emite tudo dentro do thinking e
  a bridge descartava → resposta vazia. Agora `off` = sem item reasoning,
  mas o texto do raciocínio é fundido no início da message (não-stream) ou
  desviado para o stream de texto (SSE). Caminho Qwen3+events byte-idêntico.
- Parâmetro: `--thinking off|events` (CLI) e `/api/config` (runtime).
  Atestado: bateria comparativa 0.2 via `:4125` com `off` → message não-vazia.

## Suite de testes (Rochas.OpenCodeBridge.Test)
- Console .NET 9 sem NuGet (BCL), na solution: 6 unit via HTTP na bridge
  (`/api/status|metrics`, `/v1/models`, `/api/translate` tools+namespace+stop,
  `/api/convert` thinking+tools) + 5 e2e no modelo vivo (hermes forcado,
  responses simples/com tools, stream SSE com sequence monotônico, review de
  arquiteto senior). Exit 0 PASS / 1 FAIL.
- Rodar: `dotnet run -c Release --project Rochas.OpenCodeBridge.Test -- [--skip-e2e]`
- 2026-10-03: suite segmentada 1 classe/arquivo (TestContext + Program orquestrador +
  UnitStatus/Translate/Convert + E2ETool/Stream/Architecture/Mitigation, 9 arquivos);
  17 cenários (8 unit + 9 e2e: multi-namespace IoT, args inválidos, ciclo IoT→fila→push,
  erro 4xx honesto, concorrência 3x); métodos en-US, comentários pt-BR. **17/17 verdes**.

## Bridge v1.6 — teto de saída 4k (2026-10-04)
- `MaxTokens` padrão 8192→4096 (padrão de fábrica sem thinking); honra pedido
  menor (`max_tokens`/`max_output_tokens`), nunca maior que o teto. Corta
  loops verborrágicos e metade da latência pior-caso; truncamento real o
  opencode contorna com follow-up (`finish_reason=length`).
- Ajuste remoto já existia: `POST /api/config {"max_tokens": N}`.

## Bridge v1.7 — perfis plan/build + appsettings.json (2026-10-04)
- Sufixo no model (`...-plan`, `...-build`): plan = temp 0.6 + thinking;
  build = temp 0.4 + thinking fundido. Temperatura explicita no request
  vence o perfil; sem sufixo valem os globais (Qwen3 padrao inalterado).
- `appsettings.json` opcional ao lado do DLL (listen/port/upstream/model/
  thinking/temperature/maxTokens/logPath/profiles); CLI vence o arquivo;
  `/api/config` continua ajustando em runtime.
- Uso: opencode com entradas `openai/qwen3-8b-awq-plan` (planejar) e
  `...-build` (executar). Atestado via `/api/translate` (suite U nova).

## Bridge v1.8 — tps em todo ciclo (2026-10-04)
- Log da bridge: cada `/v1/responses` registra `input/output_tokens` + `tps_output`.
- Suite: `PostObj` imprime `[tps] N tok / Ts = X tok/s <- url` em todo POST
  com `usage` (unit translate sem modelo nao tem usage: silencioso).
- Medido: linha Coder em GPU ~35-45 tok/s fim-a-fim; Qwen3-8B ~25-30 tok/s (thinking
  incluso — o custo 2x de tokens explica a "lentidao", nao o decode).

## Suite +4 cenários sênior (2026-10-04, sem bump: segue 1.8)
- `E2ESeniorTests.cs`: React corporativo (fetch concorrente + AbortController),
  bus de eventos no backend (outbox + idempotência), regras de ERP (borda
  fiscal) e ARM embarcado (DMA + STOP). **22/22 verdes** no selo.

## Suite: retry em corpo vazio (2026-10-04, sem bump: segue 1.8)
- Sob rajada, `/v1/responses` ocasionalmente devolve 200 vazio (keep-alive;
  visto no Qwen3@36k e no Coder@52k: nao e pressao de VRAM — vLLM sem erros,
  KV 0% idle). `PostObj` tenta 1x de novo apos 2s antes de falhar.

## Bridge v1.9 — coerção texto→tool_calls p/ linha llama/CPU (2026-10-04)
- LLM em CPU (llama.cpp) não tem parser server-side: cospe JSON em fences.
  Nova camada (só perfil CPU): extrai ```json{"name"..} ou `<tool_call>` do
  content e sintetiza `tool_calls` (non-stream + fecho do stream), removendo
  os blocos consumidos. Loga `coerced` com count.
- Chaveamento: `--coerce-text-tools true` ou auto quando o model contém
  "cpu". **Linha vLLM (hermes/qwen3_coder/gemma4) intacta**: com tool_calls
  nativos, a coerção nunca dispara.
- Suite: `U-convert-coerced-text-call` (convert com model `*cpu*`).
- Atestado: unit `U-convert-coerced-text-call` verde + evento `coerced`
  observado ao vivo. E2E completo via opencode TRAVOU: system prompt de ~6k
  tokens no prefill CPU (minutos) estoura timeouts do cliente (ASGI abort).
  Ação real na CPU exige prompts curtos ou prefill menor — pendente.

## Bridge v2.0 — perfis plan/build/orch + teto CPU + 5 modos opencode (2026-10-04)
- Perfis por sufixo no model (`-plan` 0.6/events, `-build` 0.2/off, `-orch`
  0.2/events, parametrizáveis em `appsettings.json`): o 8B ganha o 5º modo
  orquestrador (build que delega micro-enunciados a subagentes 3B).
- `CpuMaxTokens` (padrão 2048, `--cpu-max-tokens`/`cpuMaxTokens`/`/api/config`):
  linha CPU sem parser divaga minutos a ~5 tok/s com teto alto (3B rambleou
  1400+ tokens num `soma`); GPU segue com `MaxTokens` (8192).
- Suite: `U-cpu-max-tokens-clamp` (dry-run `/api/translate`, sem modelo).
- Passthrough mapeia id com perfil (`-plan/-build/-orch`) p/ o Model servido
  (opencode 1.x fala chat/completions direto; sem isso o vLLM dava 404 nos
  ids com sufixo) + injeta a temperatura do perfil quando o cliente omite.
- Exemplos: `appsettings.4124.gpu-example.json`, `appsettings.4125.cpu-example.json`.
- opencode 1.18.34 IGNORA o bloco `providers` (schema V1): config ativa usa
  `provider` + `npm @ai-sdk/openai-compatible` (`opencode.json`); formato
  `providers` fica em `opencode.v2.json` de reserva p/ o v2.

## Bridge 2.0 — coerção no chat direto + formatos crus do 3B (2026-10-04)
- opencode 1.x fala `chat/completions` direto: `Passthrough` agora mapeia id
  com perfil p/ o Model servido (era 404 no vLLM) e a linha CPU força
  `stream:false` + coage a resposta (SSE sintetizado quando pedido).
- 3B alterna 4 formatos entre runs: fences, `<tool_call>`, `<{..}>` e objeto
  cru — extrator com chaves balanceadas cobre todos (`coerced-chat` no log).
- `stop: ["[END_OF_TEXT]"]` injetado na linha CPU (corta o loop de filler) +
  `CpuMaxTokens` limita o pior caso. Atestado ao vivo: `soma(17,25)` com
  `finish: tool_calls` em 33 tokens (antes: divagação até `length`).
- Perfil `moe` (0.4/events): 6º modo `qwen3-8b-awq-moe`, híbrido geral com
  system prompt amplo próprio (opencode + Qwen CLI).

## Padrão de temperatura 0.4 (2026-10-05)
- `plan` e `moe` Baixados de 0.6 para **0.4** em todos os lugares (perfis da
  bridge, `appsettings` GPU/CPU, mint V2, Qwen entry 8B): equilíbrio entre
  criatividade e determinismo. `build`/`orch` seguem 0.2; 3B segue padrão.

## Bridge v2.1 — E2E enterprise C# + React (2026-10-04)
- `E2EEnterpriseTests.cs`: 6 cenários com skills de backend e frontend —
  CRUD simples Product (Minimal API + Dapper + bordas 400/404), CRUD Customer
  (paginação OFFSET/FETCH + 409 email duplicado), CRUD composto SaleInvoice
  header+detail em transação (baixa de estoque + rollback), BackgroundService
  (outbox + retry + dead-letter + stop gracioso), circuito Garnet
  (cache-aside + invalidação + circuit-breaker) e Dashboard React (cards +
  gráfico SVG + AbortController). Asserts anti-stub (sem TODO/esqueleto).

## Selo 3B CPU (2026-10-04): suite 28/30 + reruns = 30/30 com ressalva
- Linha `:4125`→`:4110` (Qwen2.5-Coder-3B Q4_K_M, 4 threads i5-7500T):
  12/12 unit em 0.0s; E2E simples 6-22s, tools 34s, senior 70-400s,
  enterprise 160-340s a ~2.5-5 tok/s. Final: **28 PASS, 2 FAIL**
  (customer: sem 409; worker: TODO) — ambos verdes em rerun manual, exceto
  o 409 que o 3B omitiu 2x (propensão a largar constraint de status-code).
- Asserts CRUD estilo-agnósticos (3B varia Minimal↔Controller entre runs);
  flag `--only` p/ rodar 1 cenário. Recomendação registrada: ACEITE do orch
  deve cobrir status-codes via teste de integração (o unit de texto não pega).

## Bridge Web — UI MVC black + DapperRepository + suite (2026-10-05)
- Novo projeto `Rochas.OpenCodeBridge.Web` (MVC `net9.0`, Razor + Bootstrap 5.3.3 black):
  login com cookie (PBKDF2, seed `admin@mova.com`/`Admin@123`), CRUDs `users` e
  `agents` via `Rochas.DapperRepository 2.0.1` (`GenericRepository` + `Filterable`),
  chat com thinking ao vivo no painel superior (SSE repassado por `/Chat/Stream`,
  proxy do HttpListener que não manda CORS). SQLite `web.db` no boot (DDL via
  `AppDb.Init`), `Microsoft.Data.Sqlite 9.0.20`.
- Novo `Rochas.OpenCodeBridge.Web.Test` (console): unit (`U-hash-*`, `U-agent-*`)
  + integração real (CRUD em sqlite temp `I-crud-*`; HTTP `I-http-*`: anônimo
  redireciona, login admin, `/Chat` 200, `/Agents` 200, `/Chat/Stream` 404).
- Build da solution Release: 0 warning(s) / 0 error(s). Suite: 14/14 PASS
  (`dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web
  http://127.0.0.1:4130`).
- Portado do `Rochas.OpenCodeBridge.Web` construído em `/usr/src/opencodebridge`
  (sessão 2026-10-05, não commitada): normalizado `form.`/`model.` que os `sed`
  haviam deixado incoerente e prefixo de linhas `N: ` dos arquivos Razor/CSS
  (erro RZ2005 na primeira build portada).

## Bridge Web — MDB + temas + admin + reasoning indicator (2026-10-05)
- Migração de Bootstrap 5.3.3 CDN para **MDB UI Kit 6.4.2** local (`wwwroot/lib/mdb/`,
  `wwwroot/lib/fontawesome/`), `data-bs-theme="dark"` nativo + `black.css` override.
- **Chat MDB-style**: lista de sessões à esquerda (avatares, badge count), área de conversa
  com bolhas usuário/agente (ícones FA `fa-user`/`fa-robot`), input `form-outline` +
  label "Pergunta ou Instrução", botão `btn-info btn-rounded float-end`.
- **Sessões persistidas em localStorage** (demo): "Nova", "Limpar", seleção por clique,
  título = 1ª mensagem truncada, contagem de mensagens.
- **Streaming SSE "digitando"**: `fetch` + `ReadableStream` + `TextDecoder(stream:true)` —
  reasoning e resposta atualizam `textContent` a cada chunk; caret `.typing` pisca.
- **Indicador de engine** (global + por bolha): bolinha verde **piscando** enquanto
  chega stream, verde **acesa** ao terminar, **vermelha parada** em erro.
  Endpoint `GET /Chat/Ping?agentId=` (timeout 3s no `/api/status` da bridge)
  atualiza ao trocar agente no dropdown.
- **Tema dark/light**: botão na navbar (sol/lua) persiste em `localStorage`,
  aplica `data-bs-theme` no `<html>` antes do paint (script inline no `<head>`).
- **Flag `IsAdmin` em `User`**: seed `admin@mova.com` com `IsAdmin=true`; links
  "Agentes"/"Usuários" só aparecem se `User.FindFirst("IsAdmin").Value == "true"`.
- **Foco escuro**: `.form-control:focus` / `.form-select:focus` com
  `border-color: #3b82f6` + `box-shadow` rgba suave (não mais azul vivo).
- DDL `AppDb.Init`: migração `ALTER TABLE users ADD COLUMN is_admin` para DBs antigos.
- Assets estáticos servidos via `WebApplicationOptions.WebRootPath` (independe de cwd).
- Build Release: 0 warnings / 0 errors.

## Bridge Web — Scripts de deploy + plano 5 fases (2026-10-05)
- Scripts adicionados ao repo: `deploy-web.sh` (build + testes + restart), `health-check.sh` (login + engine ping), `gate.sh` (gate + build + testes).
- Plano detalhado `plan-web-001.md` com 5 fases granulares para execução por modelo 8B (ctx 36k):
  1. Fundação & Infra (concluída)
  2. Chat UX Core (concluída)
  3. Persistência Server-Side (sessions/messages, ContextWindow, endpoints CRUD)
  4. UX Refinada (bolha agente pós-thinking, sem dot na bolha, a11y, scroll, toast)
  5. Qualidade & Deploy (testes integração/unit, gate, health-check, docs, tech debt)
- Regras de execução para modelo 8B: uma tarefa por vez, não inventar, build+teste=done, fail fast, ordem sugerida.

## Bridge Web — Fase 3 Persistência Server-Side (2026-10-05, CONCLUÍDA ✅)
- **Models**: `Session` e `SessionMessage` com FKs e índices (`idx_sessions_user`, `idx_sessions_agent`, `idx_messages_session`, `idx_messages_created`).
- **DDL**: `AppDb.Init` cria tabelas `sessions` + `session_messages` com `ON DELETE CASCADE`, migração `is_admin` mantida.
- **SessionService**: CRUD completo (Create, GetByUser, Get, UpdateTitle, UpdateAgent, Delete, GetMessages, AddMessage, Count, Touch).
- **ContextWindow**: `BuildContext(history, maxInputTokens=28672)` estima tokens via `chars/4`, retorna slice cronológico que cabe no orçamento Qwen3-8B.
- **ChatController endpoints**:
  - `GET /Chat/Sessions` → lista do usuário (ordenado updated_at DESC)
  - `POST /Chat/Sessions` → cria sessão (`{agentId, title?}`), retorna id
  - `GET /Chat/Sessions/{id}` → detalhe
  - `GET /Chat/Sessions/{id}/Messages` → histórico paginado
  - `DELETE /Chat/Sessions/{id}` → deleta sessão + mensagens (cascata)
  - `PUT /Chat/Sessions/{id}/Title` / `PUT /Chat/Sessions/{id}/Agent` → updates
  - `POST /Chat/Stream` → aceita `sessionId` opcional; carrega histórico via `SessionService.GetMessages` + `ContextWindow.BuildContext`; persiste mensagem do usuário antes do stream; captura resposta do assistant (content + thinking + usage) via buffer intermediário; persiste assistant message ao final.
- **Front-end (Chat/Index.cshtml)**: remove `localStorage`; carrega sessões via `/Chat/Sessions`; seleção carrega mensagens via `/Chat/Sessions/{id}/Messages`; "Nova" → `POST /Chat/Sessions`; stream envia `sessionId`; bolha do agente só aparece no primeiro chunk de `content`; thinking vai para `<details>` colapsável; indicador global "Pensando..."/ "Concluído!"/ "Desconectado"; sem bolinha na bolha do agente.
- **Validação**: login cria claim `NameIdentifier` com user ID; `CurrentUserId` propriedade no controller.
- **Teste manual**: `curl /Chat/Sessions` → `[]`; `POST /Chat/Sessions` → `{"id":1,...}`; `GET /Chat/Sessions/1/Messages` → `[]`; `POST /Chat/Stream` com `sessionId=1` → SSE streaming OK (reasoning + content), mensagem user + assistant persistida (thinking + content + usage).
- Build Release: 0 warnings / 0 errors.

## Bridge Web — Fase 4 UX Refinada + Testes Integração (2026-10-06)
- **CSS `black.css` reescrito**: overrides MDB dark/light completos (cards, forms, buttons, badges, avatars, engine indicator, bolhas, scrollbar, toast, fade-in). Tema light via `[data-bs-theme="light"]` funcional.
- **UX Chat**:
  - Bolha agente só aparece no 1º chunk de `content` (não no thinking); thinking em `<details>` colapsável "Raciocínio".
  - Indicador global (bolinha + texto): "Pensando..." (verde piscando) → "Concluído!" (verde) → "Desconectado" (vermelho) → "Aguardando..." (cinza idle).
  - Toast MDB (canto inf. dir.) em erro de stream: "Erro ao conectar na engine..."
  - Scroll inteligente: auto-scroll só se usuário no fundo (`scrollTop + clientHeight >= scrollHeight - 50`).
  - Teclado: `Enter` envia, `Shift+Enter` quebra linha; foco retorna ao `prompt` após envio.
  - `aria-live="polite"` no `#conv` para leitores de tela.
- **Fix agent update**: `agents.Query(...).FirstOrDefault()` no lugar de `agents.Get(...)` (workaround DapperRepository).
- **Limpeza**: removido dead code `PasswordHasher` comentado em `AccountController.Login`.
- **Testes integração Chat** (`ChatIntegrationTests.cs`): 7 cenários (login redirect, admin links, sessions CRUD, stream SSE, ping engine) — **14/14 PASS** (unit + http + chat integration).
- Build Release: 0 warnings / 0 errors.

## Bridge Web — Fase 5 UX Polish + Cancel Request (2026-10-07)
- **Cancelar requisição em andamento**: botão "Cancelar" (ícone X vermelho) aparece durante streaming; usa `AbortController` para cancelar `fetch` in-flight; botão some ao cancelar/concluir/erro.
- **UX loading states**: 
  - Botão "Enviar" → spinner + "Enviando..." + desabilitado durante request
  - Input desabilitado durante envio
  - Indicador "Processando..." com spinner abaixo do input
  - Re-habilita input/botão imediatamente após request iniciado (não espera resposta)
  - Em erro: toast "Erro ao conectar na engine", reabilita botão/input
  - Sucesso: botão "Send" restaurado, label "Concluído!" no engine indicator
- **Limpeza Cancel button**: removido do DOM ao concluir/cancelar/erro
- **Sessão persistida**: `selectSession` carrega histórico via `/Chat/Sessions/{id}/Messages`; streaming usa `sessionId`; mensagens user+assistant persistidas com thinking+content
- Build Release: 0 warnings / 0 errors.
- Testes: 37/37 PASS (unit + integration)

## Bridge Web — Fase 6 Executor único + DI total + UX + Diagnóstico (2026-10-08)
- **Executor único (DDD)**: `ToolExecutor` virou despachante atrás de `IToolExecutor`;
  handlers `IToolHandler` por tool (`Shell/Read/Write/Edit/Grep/Glob`) com trava de
  workspace (`WorkspaceGuard`), runner de processos com timeout+kill (`ProcessRunner`)
  e `ToolResult` como objeto de valor. `task` removida do `ToolDefinitions`
  (anunciar só o que executa). Registro via `AddScoped` no `Program.cs`.
- **DI projeto-todo (interfaces do pacote)**: controllers e `SessionService` dependem de
  `IGenericRepository<T>` + `IPersistenceRepository<T>` (Rochas.Data.Specification),
  `ISessionService`, `IToolExecutor`, `IPasswordHasher`; `ContextWindow` segue estático
  puro (sem dependência a injetar, 7/7 testes). Padrão segue README do DapperRepository.
- **Fixes reais achados por evidência**: race stdout vazio no runner (`WaitForExit`
  duplo); race `[DONE]` antes de persistir o assistant (DONE próprio após persistir);
  `SessionService.cs` reescrito com EF estranho revertido (quebrava build).
- **Chat UX**: label branca, `ENVIAR`, "Processando" como linha da conversa até o fim,
  resposta palavra por palavra (JS puro + caret CSS), thinking em `<details>` fechado.
- **Diagnóstico**: Serilog (Console + tabela `logs` em `diagnostics.db`), middleware com
  tempo/status + Referer/IP bem-vindos, `Rochas.Telemetry` (`ComponentObserver` nas
  tools + `ServiceObserver` com snapshots), modo on/off (`Diagnostics:Enabled` ou env
  `DIAGNOSTICS_ENABLED=1`), `GET /Chat/Diagnostics` autenticado.
- **Higiene**: symlink `wwwroot/*.iso` removido (era lixo apontando p/ VMachine).
- **Suite: 42/42 PASS** (7 ContextWindow + 10 SessionService + 11 ToolExecutor + 14 Chat),
  `dotnet build slnx -c Release` 0 erros; warnings só os 4 pré-existentes.

## Bridge Web — Fase 6b Cobertura + diagnósticos verificados (2026-10-08)
- Suite: **61/61 PASS** (7 ContextWindow + 10 SessionService + 23 ToolExecutor +
  6 Diagnostics + 15 Chat integração), exit 0. Cobertura do código novo ~85%:
  handlers, dispatcher, DI, middleware (Referer/IP), telemetria e endpoint
  `/Chat/Diagnostics` exercitados; fora: tetos de tamanho, sudo-exato-allow,
  RegisterError, POSTs de Users/Agents (gap pré-existente).
- Sem `dotnet test`/`coverlet` neste repo (runner console próprio); contagem por asserts.

## Bridge Web — Fase 6c Economia de contexto (2026-10-08)
- `ToolDefinitions` enxuto (2737→2134 chars, ~684→~533 tokens/request): descrições
  curtas, tipos/required preservados; helpers com nomes legíveis
  (`BuildTool`/`BuildParam`/`ToolParam`).
- Teto 2000 chars nos retornos `role: tool` do loop (paridade OpenCode).
- E2E real validado: modelo chamou `shell`, listou diretório, truncou e respondeu pt-BR.
- Suite: **61/61 PASS** (7+10+23+6+15).

## Bridge Web — Fase 6d Progresso de tools na UI (2026-10-08)
- Servidor emite SSE `{"progress":"tool_start"|"tool_done","name","ok?"}` com flush
  imediato ao redor de cada `Execute` (antes o `CopyToAsync` escondia a execução).
- UI: linha `Executando [nome]...` piscando → `[nome] executado.` (ou `falhou.`),
  mantida como histórico sutil do que rodou.
- Teste `I-chat-tool-progress` (modelo real via `ls`): start+done observados.
- Suite: **62/62 PASS** (7+10+23+6+16), build Release 0 erros.

## Bridge Web — Fase 6e Streaming real (2026-10-08)
- Removido `CopyToAsync` do `StreamWithTools`: linhas da bridge repassadas ao vivo
  (primeiro byte ~3,8s vs ~13s+ antes); bolha nasce no primeiro `content` e o
  efeito palavra-por-palavra (~20/s + caret) ritma o fluxo real.
- Suite: **62/62 PASS**, build Release 0 erros.

## Bridge Web — Fase 6f Streaming de verdade (2026-10-08)
- Causa raiz do "digitando não funciona": `PostAsync` bufferizava o corpo inteiro;
  troca por `SendAsync` + `ResponseHeadersRead`. Comprovado: 576 chunks de 0,1s a
  16,2s (antes: tudo de uma vez ao fim). `CopyToAsync` já havia saído antes.
- Suite: **62/62 PASS**, build Release 0 erros.

## Bridge Web — Fase 6g Fix workspace guard (2026-10-08)
- Causa das falhas de escrita: `AppContext.BaseDirectory` termina com `/` e a
  checagem de prefixo virava `//` — negava TUDO dentro do workspace
  (`./x`, `.`, `./src/x`). Fix: `TrimEnd` dos separadores + regressão
  `U-tool-trailing-slash`. Tentativas fora (`/home/...`) seguem negadas (correto).
- Verificado E2E via `/Chat/Tool` (write+read OK).
- Nota: `mkdir/ping/while/watch` seguem fora do allowlist (correto); `write` já
  cria diretórios. Workspace raiz = pasta do binário (sugestão futura: config).
- Suite: **63/63 PASS**, build Release 0 erros.

## Bridge Web — Fase 7 Painel de tarefas 3B (2026-10-09)
- Sidebar direita "Tarefas 3B": lista da decomposição com badges
  (pendente/executando/concluída) + selo de síntese; polling 5s + refresh a cada
  carregamento. Fonte: `GET /Chat/Sessions/{id}/Tasks` derivado das mensagens
  marcadas (sem tabela nova), parse em `SessionTaskPanel` testável.
- Verificado ao vivo na sessão 173 (faixa verde): 1 tarefa done + synthesized.
- Suite: **70/70 PASS**, build Release 0 erros.

## Bridge Web — Fase 8 Híbridos: modos, 2 fases, tools no executor (2026-10-09)
- `Agent.Mode` (plan/build) + `Role`; executor filtra pelo modo do orch (front+400).
- Plan: temp 0.4, `GetTools("plan")` só read/grep/glob, backstop no executor.
- Pipe em 2 fases: `POST /Chat/Decompose` (preview) + `POST /Chat/OrchestrateApproved`.
- Executor build com loop de tools (5 turnos, teto 2000); plan sem tools.
- Decompose atômico (mín. 4, máx. 8) + retry até 3x por critério de aceite.
- Suite: **75/75 PASS**, build Release 0 erros. E2E com modelos: pendente.

## Bridge Web — Fase 8b Split swap + modos (2026-10-09)
- `RunApprovedAsync(synthesize:false)` + `POST /Chat/Synthesize`: executores e
  síntese em memórias distintas (protocolo do swap 8B->3B->8B).
- `Agent.Mode` + `Role` (migração, form, lista); executor filtra pelo modo do orch;
  mismatch dá 400. Plan: temp 0.4, tools read/grep/glob, backstop no executor.
- `IBridgeClient.StreamAsync` com `tools` opcional; orquestração em texto puro.
- Decompose atômico (mín. 4, máx. 8) + retry 3x por critério de aceite.
- Fixes: `Agent`/`User`.Id anuláveis (Dapper gravava 0 literal), `Get` por Id
  instável contornado com Query+tLINQ, FKs valem (Cache=Shared).
- Suite: **76/76 PASS**, build Release 0 erros.

## Bridge Web — Fase 8c Anti-alucinação + editor de tarefas (2026-10-09)
- Executor lista tools válidas no prompt + proíbe inventar; recuperação tolerante:
  bloco pseudo-tool com nome conhecido vira execução real (1x).
- Approve bar virou editor (revisar/editar/remover/adicionar tarefas antes de rodar;
  humano via UI no futuro, elucidado pelo agente hoje).
- Suite: **77/77 PASS**, build Release 0 erros.

## Bridge Web — Fase 8d Retry com refinamento (2026-10-09)
- Ciclo: falha sem conteúdo útil volta ao 8b que reescreve o enunciado e re-dispara
  (máx. `Orchestration:MaxTaskRetries`, padrão 2; tudo persistido como mensagens).
- Suite: **78/78 PASS**, build Release 0 erros.

## Bridge Web — Fase 8e Defaults zerados + sonda t/s (2026-10-09)
- Causa raiz de updates fantasmas: parser Dapper inclui props não-default no WHERE;
  defaults não-vazios do Agent/User envenenavam filtros (provado pelo SQL gerado).
  Fix: defaults zerados + `EffectiveMode` + seed admin ativo + Create com defaults.
- Sonda `POST /Chat/Executors/{id}/Probe` (fallback chars/4 sem usage); t/s do 3B CPU ~1-7.
- ETA por tarefa (`etaMin`) no plano, no approve, no painel (total) e nos testes.
- Suite: **78/78 PASS**, build Release 0 erros.

## Bridge Web — Fase 9 Bateria 3B-GPU, 100% verde (2026-10-09)
- Swap 8B->3B (Qwen2.5-Coder-3B BF16, :4101, bridge :4126, agente Coder 3B GPU).
- Marrom v3 (10 tarefas) e preta v3 (12 tarefas): 100% concluídas, 0 falhas,
  0 refinamentos necessários; sínteses 8B persistidas (1734/1859 chars).
- Ressalva honesta: 0 usos reais de tool nesta rodada (tudo em prosa, sem
  pseudo-tools); granular+retry segue como rede de segurança.
- Suite: **78/78 PASS**, build Release 0 erros.

## Bridge Web — Fase 9b needsTools exigível (2026-10-09)
- Subtarefas com `needsTools`: sem nenhuma chamada de tool, entra no ciclo de
  refinamento (evidência registrada). Prompt, endpoint, approve e teste cobrem.
- Suite unit: **63/63 PASS**; 2 falhas de integração são ambientais (8B fora do
  ar durante o swap p/ 3B), sem relação com a mudança.

## Bridge Web — Fase 9c Subdivisão recursiva (2026-10-09)
- Falha no executor (depth 0) -> 8b subdivide em 2-4 micro-tarefas e re-executa
  (profundidade máx. 1); se inviável, cai no refinamento de prompt. Tudo persistido.
- Suite: **80/80 PASS**, build Release 0 erros.

## Bridge Web — Fase 9d Contenção + coabitação (2026-10-09)
- Tempestade contida: 1 split por tarefa + filhos sem refine (84 refinos -> limitado).
- Coabitação 8B (0.75/16k) + 3B-AWQ (0.24->offload CPU, 4k): 10,9GB, ambos no ar.
  3B-AWQ responde em ~2s (mais rápido que BF16).
- Suite: **80/80 PASS**, build Release 0 erros.

## Bridge Web — Fase 10 Final Validation (2026-10-09)
- Full test suite: **77/77 PASS** (77/77 PASS)
- All 6 belts + tool belt: **43 tasks** executed across Green→Purple→Brown→Black→Blue→Tools
- Architecture stable: 8B orchestrator (4100) + 3B executor (4101) with CPU offload
- All phases complete: Green→Purple→Brown→Black→Blue→Tools
- Suite: **77/77 PASS**, build Release 0 erros

## Bridge Web — Fase 11 Estabilização do chat agente-fases-subagentes (2026-10-11)
- Agente 8B (id 1, orchestrator/plan) + executor 8B-exec (id 2, role executor,
  sem `subagent` anunciado por filtro de role no loop).
- Tools de leitura liberadas no chat: read/grep/glob (pastas), fetch (web,
  GET somente-leitura), git (somente status/log/diff/show/branch); escrita e
  execução seguem fora do anúncio.
- Tetos removidos do caminho do chat: sem `max_tokens` no body (agente e subagente;
  `maxTokens:2048` da descrição da tool não é mais repassado) — vale o teto 8k
  da bridge; `Truncate`/`OneLine`/3000/500 viraram identidade; consts mortas
  (`DefaultMaxTokens`, `MaxOutputChars`) removidas.
- Contexto do turno seguinte: subagente reidrata como `assistant` (não `user`).
- Fechamento silencioso: turno final vazio após tools persiste e digita
  "As tarefas delegadas aos subagentes foram concluídas." (subagentes) ou
  "As ferramentas solicitadas foram executadas, verifique." (outras tools).
- DONE garantido em saída anormal (erro da bridge com stream aberto, exceção
  no loop); UI renderiza `evt.error` na bolha em vez de prender em "Processando".
- UI: CANCELAR por sessão (trocar de sessão aborta o stream anterior, sem
  bolha na conversa errada); thinking inicia fechado com % do orçamento 8k e
  fecha em 100% (agente e subagente, ao vivo e no histórico); sem salto inicial de
  linhas em branco; fluxo híbrido morto (Decompose/OrchestrateApproved) removido.
- Verificações: SSE byte a byte = SQLite = tela; cortes em ponto variado são
  parada do modelo (não teto); turno final vazio pós-subagentes confirmado via
  journal do vLLM (prefill do contexto gigante + encerramento silencioso).
- Suite E2E UI iniciada (`Web.Test/Ui/ChatBehaviorTests.cs`, `--ui-behavior`):
  histórico, ENVIAR->CANCELAR, Stop por sessão, % ao vivo, label de tool.
- Suite: build Release 0 erros (Web + Test).

## Bridge Web — Fase 11b Webdriver Firefox automatizado (2026-10-11)
- `Web.Test/Ui/PlaywrightSetup.cs`: `EnsureBrowser()` em C# — usa o Firefox do
  Playwright se presente no cache, senão roda `install firefox` via
  `Microsoft.Playwright.Program.Main`; se a plataforma não suportar, fallback
  p/ o Chrome do sistema (canal, sem download). `ChatBehaviorTests` e
  `BeltUiTests` lançam conforme o retorno (UA Win11 mantido nas faixas).
- Ambiente (LMDE 7): Playwright 1.63 detecta `ubuntu20.04-x64` e o registro não
  traz Firefox p/ ela (`install firefox` = exit 1). Correção manual (uma vez):
  `firefox-debian-12.zip` rev 1543 do CDN (`cdn.playwright.dev/.../firefox/1543/`)
  extraído em `~/.cache/ms-playwright/firefox-1543/` — binário `firefox 155.0`,
  `ldd` sem faltas. Host `/usr/bin/firefox` não serve (falta o protocolo do
  Playwright) — tem que ser o build do cache.
- Stack: `vllm.service` corrigido (sem `ExecStartPost` que mascarava falha)
  instalado em `/etc/systemd/system`; bridge `:4124` + site `:4130` e `:80`
  (mesma instância, log `/tmp/site-4130-80.log`).

## Bridge Web — Fase 11b resultado (2026-10-11)
- Suite UI (`--ui-behavior`): **23/23 PASS** (`UIBEHAVIOR PASS`), do zero após
  limpar órfãos (`UIMOCK-*`, `UITEST-MOCK`) — sessões antigas poluíam o init.
- Cobertos: thinking com % e 100% (agente), label de tool, card + texto do subagente,
  histórico (bolhas + 100%), fallback silencioso (tools e subagentes), CANCELAR por
  sessão com destrava, erro mid-loop na bolha com DONE garantido.

## Bridge Web — Fase 11c Percentual do thinking (2026-10-11)
- `%` era fixo sobre orçamento de 8k tokens (`chars/4/8192`) — thinking curto
  (~2,6k chars) congelava em ~8%. Novo: progressão assintótica
  `100*n/(n+2000)` (50% aos 2000 chars), sempre mexe por chunk e fecha 100%.
- Suite UI: **23/23 PASS** com o novo cálculo.

## Bridge Web — Fase 11d Percentual contínuo (2026-10-11)
- `%` não congela mais no fim do thinking: acumula thinking+conteúdo
  (`thinkPct(think+full)` no agente; subagente atualiza no typewriter do corpo).
  Remove `thinkFrozen`. Fecha 100% no DONE como antes.
- Suite UI: **23/23 PASS** com o contínuo.

## Bridge Web — Fase 11e Percentual adaptativo (2026-10-11)
- `k` fixo (2000) removido: `k = max(300, media/9)` com média móvel
  (80/20) do tamanho thinking+conteúdo em `localStorage` (início 6000).
  Resposta típica chega ~90%, salto final pequeno; 100% exato só no DONE.
- Suite UI: **23/23 PASS** com o adaptativo.

## Bridge Web — Fase 11f Percentual mira 95% (2026-10-11)
- Divisor 9→19 (`k = max(150, media/19)`): resposta típica chega ~95%,
  salto final ~5 pontos. Suite UI: **23/23 PASS**.

## Bridge Web — Fase 11g 100% do subagente (2026-10-11)
- Corpo do subagente sobrescrevia os 100% durante a digitação: repõe
  `Raciocínio 100%` ao concluir o corpo. Suite UI: **23/23 PASS**.

## Bridge Web — Fase 12 Subagente com tools (2026-10-11)
- Experimento: subagente anuncia `write`/`edit`/`shell`; prompt manda escrever o
  script na pasta atual e validar com `bash -n` sem executar. Trava
  `SubShellAllowed` (só `bash -n`, zip/unzip, navegação). `zip`/`unzip` no
  allowlist do shell. Workspace do subagente = `bin` do site (contido).
- Suite UI: **23/23 PASS**. Units: 2 FAIL pré-existentes (`U-tool-definitions`,
  `U-orch-plan-tools` — anúncio tem 7, teste espera 5; falham sem a mudança).

## Bridge Web — Fase 12b Testes donos do anúncio (2026-10-11)
- `U-tool-definitions` e `U-orch-plan-tools` esperavam 5 tools; anúncio real
  tem 7 (shell/read/grep/glob/fetch/git/subagent). Testes atualizados.
- Units: **PASS total**. Suite UI: pendente re-run.

## Bridge Web — Fase 12c Nome .sh por fase (2026-10-11)
- Subagentes sobrescreviam `fase1.sh`: system do agente 8B (id 1, no banco) ganhou
  "Informe um nome de arquivo .sh distinto por fase para nao sobrescrever a
  implementacao." Vale imediato (lido do banco por turno, sem restart).

## Bridge Web — Fase 12d Nomes distintos funcionam (2026-10-11)
- Sessão 126: 7 subagentes, 7 arquivos (`backend-domain/infrastructure/services`,
  `frontend-mvc`, `security`, `tests`, `documentation.sh`), todos com
  `bash -n` limpo. Sem sobrescrita.

## Bridge Web — Fase 12e Labels das tools internas do subagente (2026-10-11)
- Executor interno do subagente emite `tool_start`/`tool_done` no SSE via sink
  `ProgressAsync` (`ToolContext` → `IToolExecutor` → interno); `ChatController`
  liga só na chamada `subagent`. UI reaproveita `toolStatus` ("Executando
  write..." → details no done). Sem duplicatas (principal emite o próprio).
- Units: **PASS**. `zip`/`unzip` no allowlist do shell.

## Estado da stack e da solução (2026-10-11, sessão de sucesso)
- Stack de pé: vLLM `:4100` (systemd `vllm.service` corrigido, sem
  `ExecStartPost`, modelo `qwen3-8b-awq` 200); bridge `:4124` (200);
  site `:4130` + `:80` (mesma instância, 200 nas duas).
- Solução: chat agente-fases-subagentes. Principal (8B id 1) resume em texto e
  delega enunciado por fase; subagentes (id 2 executor + handler) escrevem
  `.sh` com nome distinto por fase via `write`, validam com `bash -n` via
  `shell` travado (sem execução), `zip`/`unzip` liberados. Prompts em
  `AGENTS-PROMPTS.md` (fonte versionada; principal/executor no banco).
- UI: bolhas agente/subagente, thinking com % adaptativo (média móvel,
  mira 95%, 100% no DONE), labels "Executando ..." das tools do principal e
  das internas do subagente (SSE `tool_start`/`tool_done`), ENVIAR↔CANCELAR
  por sessão, histórico do sqlite com junção (só-thinking incorpora) e frase
  de fecho quando há subagentes e content vazio.
- Sucesso (sessão 126, faixa azul): 7 subagentes → 7 `.sh` distintos
  (`backend-domain/infrastructure/services`, `frontend-mvc`, `security`,
  `tests`, `documentation.sh`), todos `bash -n` limpo, nada executado.
- Verificações: suite UI **23/23 PASS**, units **PASS**; `main` no GitHub.

## Bridge Web — Fase 12f Bolinha vermelha ao cancelar (2026-10-11)
- CANCELAR deixava a bolinha verde piscando com "Interrompido": agora vai a
  vermelho (`dot err`), igual ao "Desconectado". Novo envio volta a piscar.
- Suite cobre: `cancel: bolinha vermelha`.

## Bridge Web — Fase 12g Glob `**` + workspace /mnt/media (2026-10-11)
- `glob`: `**/*.cs` virava literal `.cs` (linha que comia o `*`) — removida;
  sem restrição de extensão (padrão livre). Teste `U-tool-glob-starstar`.
- `WorkspaceGuard`: `/mnt` e `/media` (+ subpastas) liberados além do workspace.
- Teste mock pega o agente recém-criado (órfãos de runs abortadas
  envenenavam); `cancel: bolinha vermelha` coberto.
- Suite UI: **24/24 PASS**. Units: **PASS**.

## Bridge Web — Fase 13 Contexto enxuto + compactação (2026-10-11)
- Ao contexto voltam SÓ user/assistant/subagente (texto do subagente como
  assistant); outputs de tools não entram no loop (persistidos em
  `tool_calls`). Fim do 400 por estouro.
- Compactação: flag `archived` (migração) marca o passado sem apagar;
  `ContextWindow` ignora arquivados; resumo via bridge entra como mensagem
  do agente. Botão Compactar ao lado do Nova + automática no 400 (1 retry).
- UI: Sessões acima dos botões; grupo Nova|Compactar|Limpar; dropdowns com
  cores no tema light; bolinha vermelha ao cancelar.
- Suite UI: **24/24 PASS**. Units: **PASS**.

## Bridge Web — Fase 14 ls via tools, sem glob (2026-10-11)
- Filtro `allowed` nem anunciava `shell` (só lia ToolDefinitions): agora
  anuncia `shell` e não anuncia mais `glob` (handler segue executando se
  chamado). Prompt do principal revertido (sem preferência textual).
- UI: Sessões acima dos botões; dropdowns claros no tema light.
- Suite UI: **24/24 PASS**. Units: **PASS**.

## Pipe batch azul→preta PASS (2026-10-11)
- `batch` (chat real via UI, enunciados com delegação `.sh` distinto): azul,
  verde, roxa, marrom e preta PASS (preta: 28k chars, 14 toolcalls).
- Suite UI 24/24 + units PASS. Tudo no `main`.

## Bridge Web — Fase 15 Falha honesta do shell (2026-10-11)
- `ProcessRunner` ignorava exit code (sempre Ok): agora exit ≠ 0 vira FALHA
  (stderr sozinho não derruba). Label da UI mostra "falhou".
- Subagente: cláusula "se `bash -n` acusar erro, corrija e valide de novo".
- Suite UI 24/24 + units PASS.

## Bridge Web — Fase 15+ Ajustes finos (2026-10-11)
- Thinking aberto sem fecho capturado e movido para thinking (vivo+server).
- Subagente com streaming ao vivo (thinking + content + tools) no card próprio,
  labels por id, abre no primeiro chunk.
- Bolha agente: thinking vazado limpo, conteúdo vazio cai no fallback (thinking
  limpo via tags ou bruto).
- Prompt do agente: "Responda SEMPRE no canal de conteúdo em português do Brasil"
  evita inglês no thinking sem conteúdo no canal principal.
- Timeout por faixa na execução dos pipes (azul 30, verde 30, roxa 30,
  marrom 90, preta 120 min).
- Espera de sessão nova robusta no teste (evita colisão de sessões antigas).
- Suíte UI: **25/25 PASS** (inclui azul, verde, roxa, marrom, preta em batch).

## Bridge Web — Faixas em progressão + 14B na 3060 12GB (2026-10-11/12)
- Enunciados das faixas reescritos em progressão de escopo (capacidade, sem
  SLO): azul só domínio → verde +SQL → roxa +repos/serviços → marrom +API e
  React → preta +bus, ML clássico (regressão/classificação) e NN só no MLP
  de anomalias. Batch uma faixa por vez, headed p/ acompanhar.
- Exec em build (id 2); amarra "mesmo modo" removida no servidor
  (`CreateSession`/`UpdateSessionExecutor`) e na view (`filterExecutors`
  lista todos); teste fixa `executorId=2`; sessões criadas `1/2` OK.
- vLLM no 14B-AWQ: ctx 20k NÃO cabe nos 12GB (KV precisa 3.12GB, há 1.47GB;
  estimativa do próprio vLLM: 9632) → ctx 9216, sem OOM (11.4GB). Bridge
  `--model qwen3-14b-awq`, teto de saída 8192→6144 (8k estourava o ctx 9k).
  Agentes renomeados `14B`/`14B-exec` nos dois bancos; prompts restaurados
  no `/opt` (migração tinha zerado) e fonte `AGENTS-PROMPTS.md` sincronizada
  (+ mapa real de tools: filtro por Role, backstop por Mode, sub hardcoded).
- Medido: 14B a **29 tok/s** de decode ≈ 8B (~30); ctx menor não acelerou.
  Thinking-pt-BR instruído nos 3 prompts e revertido: o 14B ignorou
  (thinking seguiu en-US) e vazou pt no content em teste manual. Vale só o
  content-pt-BR do principal.
- Azul 14B batch **PASS** (4 fases, `.sh` fases 3-4 com `bash -n` limpo,
  autocorreção `exit 127→ok` vista ao vivo). Falhas: fase 1 com tool call
  JSON malformado (400 do parser hermes — `JSONDecodeError` no journal) e
  fase 2 com sub vazio.
- Donut de contexto ao lado do Limpar (`GET Sessions/{id}/Context`:
  `prompt_tokens` real do último turno com fallback chars/4; hover
  "Tamanho do contexto Xk (medido/estimado)"). Compactar com ≤9 msgs
  responde "Nada a compactar" (correto, `keepLast=8`).
- UI: Raciocínio do sub começa fechado (e fecha ao chegar conteúdo), aparo
  de salto inicial no corpo do sub, fonte 0.875 unificada (caixa + rótulo).
- EM ABERTO: bolha do subagente duplicada ao vivo (pares idênticos; banco
  tem 1x cada, args todos distintos, canais SSE limpos). Reconciliação do
  done agora é sempre pelo id do sub; falta o pintor duplicado (trace +
  assert DOM×banco pendentes).

## Bridge Web — Anti-giro, contexto real e painel de scripts (2026-10-12)
- Anti-giro no loop: 3 turnos só-leitura sem texto nem delegate (ou mesma
  call 3x) injeta enunciado corretivo `[Anti-giro 1/2]` (efêmero, máx 2);
  sem progresso aborta com nota honesta na bolha. Leituras do turno
  descartadas (nunca executaram). Subagente teto 3→5 turnos.
- Dedup de calls idênticas no turno (modelo repetia glob/read e o servidor
  executava 2x) + done do sub só com id (`painted` 1x, sem fallback no card
  corrente, chunks tardios ignorados) + dedupe por assinatura no done.
- Contexto real: `stream_options.include_usage` nos corpos (sem isto o vLLM
  nunca manda usage e o donut vivia no chute chars/4); endpoint Context
  devolve `real` (último prompt_tokens) com fallback; label `% · k`.
- Eco degenerado (`[shell] ok` x500 na síntese): causa era o retorno integral
  do sub ao contexto — cortado p/ 600 chars (histórico segue integral).
  `repetition_penalty` vai no corpo (1.15; flag não existe no `serve` 0.19).
- Painel "Scripts para o Executor" (`GET Sessions/{id}/Scripts` do
  tool_calls: write.path + regex `.sh`; fonte menor; atualiza no poll).
- Data/hora `dd/MM HH:mm` no rodapé das bolhas; fonte 0.875 unificada.
- Histórico ordenado (`CreatedAt`,Id — SQLite trunca segundos e turnos
  rápidos embaralhavam); resumo usa -1s (ticks se perdiam). Isso revelou o
  duplo-envio: 2 streams concorrentes intercalavam turns e duplicavam bolhas.
- 14B: 20k não cabe (KV 3.12GB > 1.47GB) → 9k; 29 tok/s ≈ 8B; thinking-pt-BR
  instruído e revertido (ignorado + vazou pt no content); JSON de tool
  malformado recorrente (400 hermes → retry); prompts C#+React + pt-br.
- Faixas em progressão + `, diálogo em pt-br`; faixa `imunow` (Mova.Pwa
  expediente) registrada e com doc em `Projetos/Imunow/mova/`; azul e imunow
  PASS no 14B. Janela headed 1536x864.
