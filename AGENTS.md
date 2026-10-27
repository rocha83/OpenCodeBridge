# AGENTS.md — como esta máquina usa o Qwen local

## Garantia: tudo local, nada p/ nuvem
- GPU `llama :4100`: `Qwen3.5-9B Q4_K_M` (drive persistente), ctx `159744` (156k),
  1 slot (`-np 1`), `--prio 2` → bridge `:4124` (`qwen3.5-9b`, saída `32768`).
- GPU2-CPU `llama :4110`: `Qwen3-VL-4B Q3_K_M` + `mmproj Q8_0` (drive persistente),
  ctx `49152` (48k), slot único (`-np 1`), `--prio 0` → bridge `:4125`
  (`qwen3-vl-4b`, saída `4096`). O 4B substitui os modelos pequenos anteriores (ex.: Coder 2.5):
  é o executor da linha GPU2-CPU (roda na GPU por padrão; `-ngl 0` só em benchmark via toggle).
- Web em `:4130` (config `Web:Port` no `appsettings.json`, override via env
  `BRIDGE_WEB_PORT`); `:4150` reservado ao Caddy (Grafana `4800`); banco em `BRIDGE_WEB_DB_DIR` (disco local).
- Verificação: cada chamada aparece em `/tmp/bridge-gpu.log` ou
  `/tmp/bridge-cpu.log` com o `model` correspondente.
  `curl /api/status` nas portas antes de rodar.

## Tuning aplicado (diálogo enxuto, tools intactas)
- `temperature`: agentes Plan 0.5–0.8, Build 0.2; bridges com default 0.2.
- System prompts dos agentes: fonte versionada em `AGENTS-PROMPTS.md` +
  seed em `Rochas.OpenCodeBridge.Web/Data/AgentSeeder.cs` (9B cita ctx
  moderado 160k; 4B cita ctx limitado 16k).
- `stop` **não** é enviado com tools (truncaria o JSON da tool call); a bridge só
  repassa `stop` se o cliente pedir explicitamente (`BuildChatRequest`).
- `presence_penalty`: Responses API não define; ignorado de propósito.

## Ordem de boot p/ testes
1. llama GPU `:4100` (9B Q4 128k, prio high) e GPU2-CPU `:4110` (VL-Q3 48k slot único, prio normal).
2. Bridges `:4124` → `:4100` e `:4125` → `:4110` (`--passthrough-responses`).
3. `curl /api/status` nas portas antes de rodar o agente.

## Segmentação obrigatória p/ modelos locais (2026-10-04)
- Runs longas multi-step degradam em loops. REGRA:
  fatiar toda tarefa em micro-runs (1 artefato + verificação cada),
  leituras limitadas (≤2 por arquivo, blocos grandes), respostas curtas.
- Subagentes do mesmo modelo SÓ onde existem (sessão interativa com Task);
  em `run --standalone` NÃO existem — a segmentação sou eu (runs curtas
  sequenciais, nunca 1 run gigante).
- Sem reescrita de arquivo gigante de uma vez; sem loop de fix infinito
  (máx 3 tentativas por artefato, depois humano assume).
- Históricos: do fim ao começo, blocos de 8k.

## Conteúdo longo + subagentes que só agem (2026-10-04)
- Thinking: GPU com thinking ligado (`--thinking events`); linha 4B/CPU
  conforme o cadastro do agente (`events` p/ Plan, `off` p/ execução direta).
- Conteúdo longo (>10k tokens) NÃO vai inteiro no prompt: fatiar em blocos
  com sobreposição e um índice (arquivo:início:fim) antes de delegar.
- Subagente = Qwen com viés executor: o 9B delega a subagentes DELE MESMO
  ou ao 4B da linha CPU
  instruídos a agir — zero conversa, só ação com tools: recebe
  slice + tarefa + critério, devolve artefato + evidência de execução.
  Sem relatório, sem resumo, sem pergunta de volta. Quem resume sou eu
  (ou o Qwen3 pai) no merge.
- Resposta de subagente sem evidência de execução = falha (repetir 1x,
  depois executar eu mesmo o slice).

## Enunciado rígido p/ subagentes (4B e Qwen-consigo-mesmo)
- Vale para o 4B **e** para subagentes do próprio 9B:
  1 tarefa por prompt, imperativo curto, exemplo de saída incluído,
  1 leitura + 1 escrita, verificação externa, temp 0.0.

## Regra: legacy harness sempre desligado no opencode
- Bridges nunca rodam com `--legacy-harness` servindo opencode (tubo puro).
- Única exceção permanente: `ApplyPatternException` (strip de `pattern`
  não-ancorado), sempre ligada, sem flag.

## Regra: teste WAR se adapta à bridge (não o contrário)
- A bridge tem uma estratégia fixa e estável (flags, ctx, `max_tokens`,
  harness, tails) usada por OpenCode, Web UI e produção. O WAR mede o modelo
  dentro dela; testes não mudam a bridge para passar.
- Proibido para obter PASS: alterar `max_tokens`/ctx por belt, ligar coerção
  (`coerce-text-tools`/`coerce-narrated`/`tool-strategy`), mudar main harness
  ou tails, trocar a instância do gate (Q4 → Q3) ou afrouxar assertivas/aceite.
- Q3 (`:4125`) é diagnóstico; o gate é a instância estável (Q4 `:4124`).
  PASS no Q3 não substitui o gate; Q4 só muda com restart autorizado.
- Orçamento estável: Q4 `131072`/`16384`, Q3 `114688` (2×57344)/`16384`,
  Web `16384`; chat do runner `16384` e review `2048`. Complexidade usa o
  mesmo teto, não cria threshold por belt.
- Schema de teste espelha produção: `write` sem `maxLength`; o cap 1200 do
  teste é artefato e não justifica saída baixa. Alinhar sem afrouxar o aceite.
- Referência operacional completa: `docs/LAB-SETUP.md` §4.5. Registro
  cronológico: `HISTORY.md`.
- Driver C# canônico (lock, pausa, steps, build, redeploy, restart e gate):
  `TEST-DRIVER.md` (`TestCircuitDriver` → `WarTemplate` → `Execution`).
  `scripts/war/` é referência legada; não é o caminho de execução.

## Regra: fluxo persistente -> PROD
- Edição de fonte só na pasta persistente (`/media/.../OpenCodeBridge`).
- Push sempre do persistente para o `origin/main`.
- PROD (`/usr/src/opencodebridge`) só faz pull + rebuild + restart (deploy).
- Em conflito de merge, a prioridade é do persistente (`checkout --ours <arquivo>`).

## Regra: Core é a fonte única; consumidor não reimplementa

Vale para Web, suíte de testes, OpenCode, CLI e qualquer consumidor do Core.
**Não se implementa no consumidor uma lógica que já pertence ao Core.**
Consumidor chama `Rochas.OpenCodeBridge.Core` ou a API da bridge; não cria
uma segunda versão da mesma responsabilidade.

- **No Core:** catálogo/schema de tools, help curado, execução de tools,
  permissão/role, `WorkspaceGuard`, process runner, preflight de stack,
  MainHarness/tails, sanitização/coerção, sampling, métricas e parsing de
  protocolo.
- **No consumidor:** só orquestração do cenário, fixture, transporte HTTP,
  assertivas e evidência. Teste pode montar o workspace e o enunciado; não
  pode criar `ToolCatalog`, `Tools()`, `Exec()`, `LocalExec()`, `Shell()` de
  tool, conversão manual chat→Responses nem schema paralelo.
- **Nome/args de tool são contrato do Core.** Se o teste usa `read_file`,
  `write_file`, `edit_file` ou `list_dir`, isso é bug: o catálogo canônico é
  `read`, `write`, `edit`, `glob` (e `git` quando exposto).
- **Divergência é bug de Core, não do teste.** Corrija a fonte única e todos
  os consumidores passam a usar a mesma definição. Não “adapte” o teste.
- **Proibido:** `TestContext.ToolCatalog`, `E2EMissionTests.Tools/Exec`,
  `E2EFrontPipeTests.FrontTools/LocalExec` e equivalentes. O caminho correto
  é `Core.ToolCatalog.Chat/Responses` + `POST /api/tools/execute`.
- **Exceção legítima:** lógica exclusiva do cenário de teste (gerar
  enunciado, preparar fixture, controlar serviço, checar saída) não é Core;
  ainda assim, não duplique uma responsabilidade que o Core já tem.

## Regra: blindagem em C# — teste + evidência, nunca script decorativo
Vale para gate, preflight, driver, parsing de veredito e qualquer verificação que
decida PASS/FAIL. O padrão do projeto é C# com teste e evidência; bash é
conveniência manual, nunca fonte de verdade.

1. **Toda verificação vive em C#** dentro da própria suíte (`Rochas.OpenCodeBridge.Core`,
   `Rochas.OpenCodeBridge.Test`, `Rochas.OpenCodeBridge.Web.Test`). Script em
   `scripts/` não pode ser o único lugar de um check: se ele não roda
   automaticamente antes do teste, é enfeite e não conta.
2. **Sem recursão de teste-sobre-teste.** O check de verificação é provado pelo
   próprio run, com evidência no log (`GO`/`NO-GO`, `PARADA`, veredito do step).
   Teste unitário só quando o check decide PASS/FAIL da bateria, no máximo um
   caso feliz + um negativo; proibido criar camada de testes para testar o teste.
3. **Driver só muda com teste de bateria real** (`RunStepsAsync` com ≥3 steps):
   provar parada no primeiro FAIL, `.done` só para PASS/SKIP, resume no mesmo
   run dir refazendo o que falhou e gate fechando verde depois da correção.
4. **Evidência no retorno**: trecho do log com `PARADA:`, `[preflight]` e o
   `[test] PASS=n FAIL=0` colados na resposta. "Deve funcionar" não é entrega.
5. **Fail fast honesto**: se um check/porta/modelo não existe, falhe com o nome
   e o detalhe; nunca degrade para aviso silencioso.
6. **Registro do que não funcionou**: toda correção de mecanismo entra em
   `docs/DRIVER-FIXES.md` com sintoma, causa e prova — para não reintroduzir.
7. **Sem duplicação de mecanismo**: quando o check migra para C#, o script
   equivalente é removido (ou vira atalho documentado que chama o C#).

## Regra: harness de subagente só na raia bridge
- No caminho bridge (`/v1/chat/completions` e `/v1/responses`), o system do
  subagente recebe `MainHarness` + tails; o system do role é preservado como
  `SYSTEM DO ROLE (...)`.
- O array `tools` do request é a fonte do schema; não duplicar schema no texto.
- `X-Bridge-Mode: opencode` (ou UA `opencode`) = opencode se auto-gerencia:
  sem `MainHarness` nem tails da bridge. `X-Bridge-Mode: bridge` = raia bridge.
- Segmentação em blocos menores é papel do enunciado/teste; a base não
  codifica hints por role.
- Evidência mínima: evento `main-harness` com `path=chat`, `role` e `tools=N`;
  `U-main-harness` verde.

## Regra: reinício de bateria reinicia a bridge
- Toda bateria E2E, em ambos os modos (`bridge` e `opencode`), começa com o
  restart da bridge alvo: `sudo systemctl restart bridge-gpu` (`:4124`),
  `bridge-gpu2` (`:4125`) ou `bridge-cpu-forced` (`:4126`).
- Validar `/api/status` antes do primeiro step; em `--mode bridge`, exigir
  `main_harness=true`. Falhar rápido se o restart ou o status não subir.
- Motivo: contadores e `ProbeSamples` começam em zero e o pool HTTP dos dois
  lados é renovado.

## Regra: disposição WAR L1 → L4 (ordem obrigatória)

A esteira WAR tem quatro níveis, nesta ordem, tanto no `WarTemplate`
(`--war-level`) quanto em `scripts/war/run-levels.sh` (`--level`):

| Level | Ordem interna |
|---|---|
| **L1** | `units` (uma vez, bridge) → `integração` → `tópicos` |
| **L2** | `E-responses-tools` → `E-tools-full` → belts **azul e verde** (build + orch) |
| **L3** | `E-science-gpu` → `E-mission-tools` → belt **roxa** (build + orch) |
| **L4** | `E-mission-algo` → belts **marrom e preta** (build + orch) → `E-front-*` → `webui-integracao-topicos` |

- **L1 integração**: `E-stream-*`, `E-review-*`, `E-iot-*`, `E-error-*`,
  `E-full-cycle-*`, `E-healthy-*`.
- **L1 tópicos**: `E-senior-*`, `E-ent-*`.
- **L4 web UI**: `E-front-gen`, `E-front-edit`, `E-front-edit-razor`,
  `E-front-edit-react`, `E-front-verify` e `webui-integracao-topicos`.

Cada nível roda sozinho (`--war-level 1..4` / `run-levels.sh --level 1..4`).
Dentro do nível, `--war-only NAME` filtra steps (substring ou lista com
vírgula). Dentro do step, o teste individual é `--only NOME` no runner.
`--war-all` e `run-all-levels.sh` sem argumentos percorrem **1 → 2 → 3 → 4**
e param no primeiro gate vermelho.

- **Modo padrão: `bridge`.** `opencode` só entra com `--war-modes opencode`
  ou `LEVEL_MODES=opencode`; não alternar modos na mesma bateria.
- **Fail-fast explícito:** `--war-stop-on-fail` (padrão do driver) para no
  primeiro FAIL/STALE/BLOCKED; `--war-no-stop-on-fail` só para medir placar
  completo. Não afirmar “fail-fast” sem o `events.jsonl` do run atual mostrar
  a parada.
- Não mover testes entre níveis sem atualizar `WarTemplate`, `run-levels.sh`,
  `docs/BENCHTESTS.md`, `TEST-DRIVER.md` e este arquivo na mesma mudança.

