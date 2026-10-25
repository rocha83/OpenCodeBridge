> **Espelho sincronizado do repositório AI.Manager.**
> Fonte canônica: `AI.Manager/docs/ai-manager/AI-MANAGER-NEXT-STEPS.md`, commit `e85d34b` (2026-10-25, v2 — absorção).
> Este arquivo é somente leitura no OpenCodeBridge; alterações de plano devem ser feitas no AI.Manager e sincronizadas por commit.
> Diagramas espelhados: `docs/ai-manager/diagrams/` (gerador `gen-c4.py`, draw.io 32.6.1).

# AI.Manager Next Steps — absorção do OpenCodeBridge como `AI.Manager.Bridge`

> **Premissa (normativa):** *“AI.Manager é o concentrador da solução de treinamento e testes de modelos locais de I.A. de baixa instrução.”*
>
> **Status:** **v2 (absorção)** — decisão normativa; substitui a v1 “fusão por contrato”.
> A v1 foi publicada em `3b8b0bc` (AI.Manager) e espelhada em `2ea54fd` (OpenCodeBridge); esta v2 é a revisão de escopo pedida pelo autor.
> C4 regenerado em v2 (draw.io + PNG) e espelhado no OpenCodeBridge junto com este documento.
>
> **Data:** 2026-10-25 (revisão v2)
> **Escopo:** absorver o OpenCodeBridge no AI.Manager. Por ora `AI.Manager.Bridge` é um **projeto dentro da solução total**; depois da paridade e do cutover é extraído como componente exclusivo de transporte e controle de fluxo entre cliente e engine de modelos.
> **Dentro do escopo:** migração do motor de protocolo, do `TestCircuitDriver`/fluxo, do gate e dos enunciados hardcoded → dados do workflow do AI.Manager.
> **Fora de escopo nesta fase:** execução remota; alteração do aceite WAR (Q4 `:4124` = gate, Q3 `:4125` = espelho pós-green).

---

## 1. Sumário executivo

A direção correta é **absorver o OpenCodeBridge no AI.Manager**, não manter dois planos equivalentes. O `Rochas.OpenCodeBridge` é doador transitório; o destino é `AI.Manager.Bridge`, um projeto dentro da solução total que depois será extraído como componente exclusivo de transporte e controle de fluxo entre cliente e engine de modelos.

- **AI.Manager = concentrador da solução.** Dono do experimento, do workflow, do enunciado, do manifesto, da aprovação, da evidência e da UI. O experimento de teste reusa o `Experiment` e o workflow existentes; não cria um `TestExperiment` paralelo.
- **AI.Manager.Bridge = sucessor do OpenCodeBridge.** Projeto dentro do AI.Manager por enquanto; concentra host HTTP, tradução Responses ↔ chat, stream, tools, harness, breaker, perfis, permissões, shim, tool loop, telemetria e o controle de fluxo do circuito.
- **OpenCodeBridge = fonte de migração.** O repo separado no Git permanece apenas até o cutover; seu `Program.cs` de ~245 KB, o `TestCircuitDriver`, a WAR e os testes hardcoded são portados em fatias, com paridade, e o repo é arquivado depois.
- **Testes = dados do workflow.** Os cenários e enunciados que hoje são arrays/belts hardcoded viram `ExperimentPlan` + `CircuitStage` + `CircuitStep` + `TestScenario` no AI.Manager, editáveis por dados na UI existente.
- **Contrato = manifesto imutável + resultado/evidência.** O AI.Manager monta o manifesto; o `AI.Manager.Bridge` valida, executa e devolve resultado. O executor não lê o banco do AI.Manager.
- **Gate continua Q4 `:4124`; Q3 `:4125` é espelho pós-green.** A absorção não altera o aceite nem enfraquece a WAR.

Em uma frase: **o AI.Manager manda no “o quê”, o AI.Manager.Bridge manda no “como” e no veredito, e o OpenCodeBridge deixa de existir como runtime separado.**

---

## 2. Evidências do estado atual

1. **`Rochas.AI.Manager.Bridge` é o embrião do sucessor, não um duplicado a aposentar.**  
   Ele já contém tradução, stream, anti-repeat e harness tails. Hoje é referenciado por `Web/Data/BridgeFeatureSeeder.cs` e pelos testes, enquanto o `BindingBridgeController` ainda faz proxy HTTP para `:4124`/`:4125`. Essa é uma lacuna de migração: o motor definitivo precisa nascer nele, não continuar no repo do OpenCodeBridge.

2. **O pipeline real está no host do OpenCodeBridge.**  
   `Rochas.OpenCodeBridge/Program.cs` tem ~245 KB e concentra perfis, harness, breaker, permissões, coerção, shim, stream, tool loop e telemetria. O `Core` tem peças reutilizáveis, mas não o orquestrador. Portanto a absorção é por fatias com paridade, nunca big-bang.

3. **O `TestCircuitDriver` é o kernel de fluxo correto.**  
   Ele suporta DAG (`DependsOn`), condicionais (`When`), ramificação (`NextSteps`), paralelismo limitado, N builds/deploys, retry/restart, lock, pausa/retomada, artefatos e `GatePolicy`. Falta o adaptador de manifesto e a mudança de dono: o driver passa para `AI.Manager.Bridge` e as definições passam a vir do workflow do AI.Manager.

4. **O WAR atual é hardcoded, mas é a melhor especificação executável disponível.**  
   `WarTemplate` fixa 3 níveis, 2 modos (`bridge`/`opencode`), 5 belts, Q4/Q3, filtros `--only`/`--belts`, preflight, build e gate. Ele vira seed de importação e fixture de paridade; os arrays de testes não continuam no runtime.

5. **A evidência atual está incompleta.**  
   O proxy grava `HarnessRun` com `ProfileVersionId=0` e `EvidenceId=0`. O modelo novo deve vincular `Experiment`, `ExperimentPlan`, manifesto, executor, harness, modelo, gate e evidência reais.

---

## 3. Decisão arquitetural

### 3.1 Topologia-alvo

```text
┌─────────────────────────────────────────────────────────────┐
│ AI.Manager — solução total (git AI.Manager)                 │
│                                                             │
│  Web/API ──► Application/Domain ──► SQLite                  │
│      │            │                                         │
│      │            └── Experiment + TrainingWorkflowCard     │
│      │                ExperimentPlan + Circuit/TestScenario │
│      ▼                                                      │
│  AI.Manager.Bridge (in-solution agora; extraível depois)    │
│    Host HTTP ─ Transporte ─ Fluxo/TestCircuitDriver         │
│    Contracts (manifesto/resultado) ─ Client (HTTP/SSE)      │
└───────────────┬─────────────────────────────────────────────┘
                │ manifesto imutável + checksum
                ▼
┌─────────────────────────────────────────────────────────────┐
│ Runtime de modelos locais                                    │
│  Q4 :4124 (gate)   Q3 :4125 (espelho pós-green)              │
│  llama.cpp :4100 / :4110                                     │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ Rochas.OpenCodeBridge — doador transitório (Git separado)   │
│  Program.cs ~245 KB, TestCircuitDriver, WAR, E2E, Web       │
│  → portado em fatias para AI.Manager.Bridge → arquivado      │
└─────────────────────────────────────────────────────────────┘
```

O `AI.Manager.Bridge` não é um segundo motor: é o motor definitivo. O repo OpenCodeBridge permanece separado no Git apenas durante a transição; depois do cutover ele é arquivado e deixa de ser runtime.

### 3.2 Fronteira do `AI.Manager.Bridge`

O `AI.Manager.Bridge` é o componente exclusivo de **transporte e controle de fluxo** entre cliente e engine de modelos. Ele não é dono do domínio de experimentos, não tem UI e não lê o banco do AI.Manager.

| Projeto | Responsabilidade | Depende de |
|---|---|---|
| `Rochas.AI.Manager.Bridge.Contracts` | DTOs, JSON Schema, canonicalização, checksum, versão de contrato | BCL |
| `Rochas.AI.Manager.Bridge` | Host builder, tradução, stream, tools, harness, breaker, perfis, permissões, shim, tool loop, telemetria, `TestCircuitDriver`, `ManifestCircuitTemplate` | Contracts |
| `Rochas.AI.Manager.Bridge.Host` | Console/systemd, CLI, configuração, portas Q4/Q3 | Bridge + Contracts |
| `Rochas.AI.Manager.Bridge.Client` | Cliente HTTP/SSE tipado para o host (usado pelo AI.Manager e por consumidores externos) | Contracts |
| `Rochas.AI.Manager.Bridge.Tests` | Testes de unidade, paridade e conformidade | Bridge + Contracts |

Regras de dependência:

- `Bridge` **não referencia** `Application` nem `Domain`. O vínculo atual com `Application.Helpers` e `Domain.Harness` deve ser movido para `Bridge.Contracts` ou para o próprio `Bridge`.
- `Application` referencia `Bridge.Contracts` e `Bridge.Client`, nunca a implementação `Bridge`; assim o host pode ser extraído sem recompilar o domínio.
- O host pode rodar in-process no AI.Manager durante a transição e standalone/systemd depois da extração. A porta e o contrato são os mesmos.
- `OpenCodeBridge.Client`/`TestContracts`/`Runner`/`Core` são doadores: o conteúdo útil migra para `Bridge.Client`/`Bridge.Contracts`/`Bridge` e os projetos antigos não são referenciados pelo AI.Manager.

### 3.3 Fonte única por fato

| Fato | Fonte única |
|---|---|
| Definição do experimento, workflow, enunciado, circuito, cenário e assertions | AI.Manager: `Experiment` + `TrainingWorkflowCard` + `ExperimentPlan` (banco/UI) |
| Versão aprovada, checksum e assinatura | AI.Manager |
| Manifesto executável | AI.Manager gera; `AI.Manager.Bridge` valida |
| Execução, veredito de step e gate | `AI.Manager.Bridge` (Flow/TestCircuitDriver) |
| Protocolo/tradução | `AI.Manager.Bridge` (Transport) |
| Evidência bruta | `AI.Manager.Bridge` produz; AI.Manager armazena metadados e referências |
| Aceitação/rejeição do experimento | AI.Manager, usando o gate Q4 e o workflow existente |

O executor **nunca lê o banco do AI.Manager**. Ele recebe o manifesto e devolve resultado. Isso evita acoplamento, lock de SQLite, duplicação de regra e permite extrair o Bridge depois.

### 3.4 Q4 primeiro; Q3 é espelho pós-green, não gate

O fluxo oficial desta fase é:

1. **Montar a pipe WAR como dados no AI.Manager**, apontando para o perfil **Q4 `:4124`** (gate).
2. **Rodar o Q4** e exigir gate verde. O Q4 é a única autoridade de aceite.
3. **Só depois do Q4 verde**, executar o **mesmo manifesto** (mesmo `manifestHash`) no perfil **Q3 `:4125`**.
4. O Q3 é um **espelho um pouco menos capaz**: deve acompanhar o green do Q4 na sequência. Ele não aprova, não reprova e não substitui o gate.
5. Se houver divergência entre Q4 e Q3, o AI.Manager abre um item de **workflow de ajuste** (`MirrorDivergence`) com classificação, dono e ação. A correção é feita na pipe/cenário/configuração e revalidada primeiro no Q4; depois o espelho Q3 é repetido.

**Testes manuais desta fase são diagnóstico, não aceite.** Eles ajudam a explicar divergências e a calibrar o espelho, mas não viram critério de green, não substituem assertion automatizada e não podem ser usados para “compensar” um Q4 vermelho. A aceitação continua automatizada: Q4 verde + evidência + política de aprovação.

O Q3 pode divergir por capacidade menor (contexto, tokenizer, tool calling, stream, aderência ao formato). Essas divergências são esperadas em parte; o que importa é que sejam **classificadas e tratadas no workflow**, não escondidas no gate.

### 3.5 Regra de migração: estrangulamento, não big-bang

A absorção segue o padrão *strangler*:

1. **Congelar o OpenCodeBridge como doador.** Nada novo entra nele; correções de gate continuam permitidas.
2. **Criar o `AI.Manager.Bridge` como projeto do AI.Manager** e mover primeiro os contratos e as peças sem estado (tradução, stream, anti-repeat, harness tails).
3. **Portar o host por fatias**: perfis/opções, harness/tails, breaker/permissões, coerção/shim, tool loop, telemetria, endpoints HTTP.
4. **Rodar o novo host em porta alternativa** e comparar com o host antigo usando os mesmos manifestos/golden tests.
5. **Cortar Q4** somente com paridade verde; Q3 depois do Q4. Rollback é o host antigo.
6. **Arquivar o repo OpenCodeBridge** quando o novo host for o único runtime e os artefatos úteis tiverem sido migrados.

Nenhuma fase pode afrouxar assertion, trocar instância do gate ou mudar `max_tokens`/ctx para obter PASS.

---

## 4. Diagrama C4

### 4.1 Nível 1 — Contexto

- **Pessoa:** autor/aprovador/operador gerencia experimentos, circuitos e execuções pela UI do AI.Manager.
- **Sistema AI.Manager:** concentrador da solução; contém a UI, o workflow existente, o domínio de experimentos, o manifesto, a evidência e o `AI.Manager.Bridge`.
- **Sistema legado OpenCodeBridge:** doador transitório. Não é um runtime alvo; o conteúdo é portado para `AI.Manager.Bridge` e o repo é arquivado após o cutover.
- **Sistema externo llama.cpp:** serve o modelo local em Q4 (`:4100`) ou Q3 (`:4110`).
- **Sistema externo GitHub:** versiona os dois repositórios durante a transição; depois, apenas o AI.Manager.

### 4.2 Nível 2 — Contêineres

![C4 — contêineres do AI.Manager com AI.Manager.Bridge](diagrams/ai-manager-next-steps-c4.png)

Arquivos:

- `docs/ai-manager/diagrams/gen-c4.py` — gerador determinístico do XML (fonte do diagrama).
- `docs/ai-manager/diagrams/ai-manager-next-steps-c4.drawio` — especificação editável para draw.io (v2, absorção).
- `docs/ai-manager/diagrams/ai-manager-next-steps-c4.png` — export em alta definição (4002 × 2402, draw.io 32.6.1).

> O diagrama mostra o `AI.Manager.Bridge` **dentro** do AI.Manager (Host/Protocol/Flow/Contracts/Client) e o `Rochas.OpenCodeBridge` como **doador/transitório**, em faixa própria, consumindo `Bridge.Client` apenas durante a transição. Q4 `:4124` (gate) e Q3 `:4125` (espelho pós-green) ficam fora do sistema, junto do llama.cpp.

### 4.3 Contêineres e responsabilidades

| Contêiner | Responsabilidade |
|---|---|
| AI.Manager Web UI | Catálogo de experimentos, wizard do workflow, editor de circuito/cenário, console de runs, comparação e evidência |
| AI.Manager API | CRUD, validação, aprovação, manifesto, fila, ingestão de resultado |
| Application Services | Experiment workflow, Manifest Builder, Validator, Gate Reader, Acceptance, Runner Registry |
| Domain | `Experiment` (kind Training/Test), `TrainingWorkflowCard` (workflow existente), `ExperimentPlan`, `CircuitStage`, `CircuitStep`, `TestScenario`, `TestRun`, `MirrorRun`, `MirrorDivergence`, `Evidence` |
| SQLite | Definições, versões, runs, evidências (metadados), audit |
| `AI.Manager.Bridge.Contracts` | DTOs, schema, canonicalização, checksum e versão do manifesto/resultado |
| `AI.Manager.Bridge` | Host builder, transporte, tradução, stream, tools, harness, breaker, perfis, permissões, shim, tool loop, telemetria, `TestCircuitDriver`, `ManifestCircuitTemplate` |
| `AI.Manager.Bridge.Host` | Console/systemd com CLI e configuração; portas Q4/Q3 |
| `AI.Manager.Bridge.Client` | Cliente HTTP/SSE tipado para o host; sem SQLite e sem domínio |
| Q4 `:4124` | Instância estável; único gate válido |
| Q3 `:4125` | Espelho um pouco menos capaz; roda depois do Q4 verde; nunca é gate |
| llama.cpp `:4100`/`:4110` | Inferência local |
| Legacy OpenCodeBridge | Doador: `Program.cs`, `TestCircuitDriver`, WAR/E2E hardcoded, Web e scripts; congelado e arquivado após cutover |
| Artifacts/Evidence | Logs, `summary.tsv`, `events.jsonl`, capturas, hashes; produzidos pelo Bridge e referenciados pelo AI.Manager |

### 4.4 Relacionamentos principais

| De | Para | Protocolo | Descrição |
|---|---|---|---|
| Usuário | Web UI | HTTPS | Gerencia experimento, workflow, circuito, enunciado e run |
| Web UI | API | HTTPS/JSON | CRUD, validate, approve, enqueue |
| API | Application | in-process | Casos de uso e invariantes |
| Application | SQLite | Dapper | Persistência |
| Application | Bridge.Contracts | in-process | Monta/valida manifesto imutável e checksum |
| Application | Bridge (Flow) | in-process/HTTP | Executa `ExperimentPlan` via `TestCircuitDriver` |
| Bridge (Flow) | Bridge (Transport) | in-process | Tradução, stream, tools, harness e tool loop |
| Bridge (Transport) | llama.cpp | HTTP | `chat/completions` |
| Bridge (Flow) | Q4/Q3 | processo/HTTP | Executa stages e coleta veredito |
| Bridge (Flow) | Artifacts/Evidence | filesystem | Logs, summary, events, capturas |
| Artifacts/Evidence | API | HTTP/dashed | Upload/ingestão idempotente |
| Legacy OpenCodeBridge | AI.Manager.Bridge | porta/paridade | Migração em fatias; não é runtime alvo |
| Q3 | Q4 | workflow | Espelho só inicia após Q4 verde do mesmo `manifestHash`; divergência vira item de workflow |
| API | Bridge.Client | HTTP/dashed | Usado quando o host for extraído; hoje pode ser in-process |

---

## 5. Modelo de domínio do AI.Manager

### 5.1 Reusar `Experiment` e o workflow existente

Não criar `TestExperiment` paralelo. O `Experiment` atual vira o agregado único de experimento, com `Kind = Training | Test`:

- `Experiment` (tabela `experiments`) recebe `Kind`, `Statement` (enunciado), `Status`, `WorkflowCardId?` e os campos de teste necessários. Os campos de treino (`BaseArtifactId`, `TunedArtifactId`, `MetricsJson`) permanecem opcionais.
- O `TrainingWorkflowCard` existente é o **workflow de aprovação/status** também para experimentos de teste. A UI pode rotular “Workflow do experimento”, mas a tabela/classe não é duplicada.
- `ExperimentPlan` é a versão imutável do circuito (manifesto, hash, schema, runner profile). Um experimento pode ter várias versões; a aprovada é a que o workflow referenciar.
- Um experimento de teste pode referenciar `ModelArtifact`/`ModelBinding` produzido por um experimento de treino.

### 5.2 Agregados

```text
Experiment (existente; Kind = Training | Test)
  ├─ TrainingWorkflowCard (existente; aprovação/status)
  └─ ExperimentPlan (versão imutável após aprovação)
       ├─ CircuitStage
       │    ├─ CircuitStep
       │    │    ├─ PreflightCheck
       │    │    └─ VerdictParser
       │    └─ GatePolicy
       ├─ TestScenario
       │    ├─ ScenarioPipeline
       │    ├─ ToolPolicy / LoopPolicy
       │    └─ Assertion
       └─ manifest_json + manifest_hash + schema_version

TestRun
  ├─ StageRun
  │    ├─ StepRun
  │    └─ ScenarioRun
  │         ├─ AssertionResult
  │         └─ BridgeCapture
  ├─ Evidence
  └─ RunEvent

MirrorRun (Q3, somente após gate Q4 verde)
  ├─ gateRunId + mirrorRunId + manifestHash
  ├─ status: Pending | Aligned | Divergent | Blocked
  ├─ MirrorDivergence[]
  │    ├─ kind, stepKey, q4Value, q3Value
  │    ├─ classification, owner, action
  │    └─ status: Detected | Triaged | Fixing | RevalidateQ4 | RevalidateQ3 | Closed
  └─ evidence + timestamps

RunnerProfile / RunnerRegistration
  ├─ host, versão, contract_version
  ├─ capabilities (dotnet, web, opencode, q4/q3, tools)
  └─ status, last_heartbeat, concorrência
```

### 5.3 Campos essenciais de `TestScenario`

O enunciado não é apenas um texto. Para migrar as belts fielmente, ele precisa de:

- `prompt` e `systemPrompt`;
- `workspace` (template, caminho, política de limpeza);
- `modelRole` / `modelBinding`;
- `temperature`, `thinking`, `maxTokens`;
- `tools` permitidas e `toolChoice`;
- `loopPolicy`: máximo de turnos, nudges, stall limit, review;
- `pipeline`: fases e roles (orch → scripter → exec), quando multiagente;
- `assertions`: arquivos, chars, execução, build, ausência de prosa antes de tool;
- `timeout`, `retries`, `tags`, `weight`.

### 5.4 Mapeamento do WAR atual

| Hoje no OpenCodeBridge | No AI.Manager |
|---|---|
| `WarTemplate` L1/L2/L3 | `ExperimentPlan` + `CircuitStage` |
| `CircuitStepSpec` | `CircuitStep` |
| `E2EBeltsBuildTests.Belts` | `TestScenario` |
| `E2EBeltsTests.Belts` (orch) | `TestScenario` + `ScenarioPipeline` |
| `--only E-belts-build-direct --belts roxa` | `TestSelector` (suite + casos) |
| `mode=bridge/opencode` | variante de transporte |
| Q4 `:4124` / Q3 `:4125` | `RunnerProfile` (gate / espelho pós-green) |
| `CircuitGatePolicy` | política de gate nomeada |
| `summary.tsv` / `events.jsonl` | `StepRun` / `RunEvent` / `Evidence` |
| preflight do `WarTemplate` | `PreflightCheck` |
| `HarnessTails` | `HarnessProfileVersion` + snapshot |
| `HarnessRun` do proxy | `ScenarioRun` + `BridgeCapture` |
| `WarTemplate`/arrays de belts | seed JSON importado para `ExperimentPlan`; não é runtime |

### 5.5 Dimensões, não duplicação

- **Nível** = `CircuitStage` (L1, L2, L3).
- **Belt** = `TestScenario` (azul, verde, roxa, marrom, preta).
- **Modo** = variante de transporte (`bridge`, `opencode`).
- **Runner** = perfil Q4 (gate) ou Q3 (espelho pós-green; nunca gate).
- **Suite/filtro** = `TestSelector` (`E-belts-build-direct`, casos selecionados).

Isso evita a matriz de ~60 casos e mantém a UI compreensível.

### 5.6 Estados

- **Versão:** `Draft → InReview → Approved → Deprecated`.
- **Run:** `Queued → Running → Passed | Failed | Blocked | Cancelled | Stale`.
- **Experimento:** `Pending → Accepted | Rejected | Inconclusive`.
- **Gate do run:** calculado pelo `AI.Manager.Bridge`; armazenado sem recálculo.
- **Espelho Q3:** `NotRun → Queued → Aligned | Divergent | Blocked`; só entra em `Queued` quando o gate Q4 do mesmo `manifestHash` está verde.
- **Divergência do espelho:** `Detected → Triaged → Fixing → RevalidateQ4 → RevalidateQ3 → Closed`; não altera o gate.
- **Aceitação:** política do AI.Manager (ex.: 3 gates verdes consecutivos, sem regressão, **Q4 apenas**). Observação manual é diagnóstico, nunca aceite.

---

## 6. Contrato runner

### 6.1 Manifesto

O AI.Manager monta um manifesto **resolvido**: sem placeholders pendentes, sem defaults escondidos, sem lógica de negócio no runner.

```json
{
  "schemaVersion": "1.0",
  "experiment": {
    "id": 42,
    "version": 3,
    "name": "WAR L2 - roxa",
    "manifestHash": "sha256:..."
  },
  "runner": {
    "id": "local-q4",
    "capabilities": ["dotnet", "web", "q4"]
  },
  "target": {
    "bridgeUrl": "http://127.0.0.1:4124",
    "upstreamUrl": "http://127.0.0.1:4100",
    "modelBinding": { "modelId": "Gemma4-E4B-Q4", "contextWindow": 131072 },
    "isGate": true
  },
  "harness": {
    "profileVersionId": 7,
    "checksum": "sha256:...",
    "mode": "BridgeManaged"
  },
  "stages": [
    {
      "key": "L2",
      "order": 2,
      "maxParallelSteps": 1,
      "gate": { "policy": "default" },
      "onFailure": "continue",
      "builds": [
        {
          "name": "build-slnx",
          "command": "dotnet",
          "args": ["build", "Rochas.OpenCodeBridge.slnx", "-c", "Release"],
          "outputs": [".../Rochas.OpenCodeBridge.dll"],
          "inputs": [".../Rochas.OpenCodeBridge"]
        }
      ],
      "preflight": [
        { "kind": "http-json", "url": "http://127.0.0.1:4100/health" },
        { "kind": "bridge-status", "url": "http://127.0.0.1:4124/api/status" }
      ],
      "steps": [
        {
          "key": "belts-build-roxa-bridge",
          "kind": "dotnet-test",
          "command": "dotnet",
          "args": ["run", "--project", "...", "--", "--only", "E-belts-build-direct", "--belts", "roxa"],
          "timeoutSec": 7200,
          "retry": { "maxAttempts": 2, "retryOn": ["timeout", "crash"] },
          "dependsOn": [],
          "verdictParser": "pass-fail-lines",
          "scenarioSelector": { "suite": "belts-build", "cases": ["roxa"] }
        }
      ]
    }
  ],
  "scenarios": [
    {
      "key": "roxa-build",
      "title": "Cadastro de clientes e fornecedores",
      "prompt": "Trabalhe em {ws}...",
      "workspace": { "template": "belts-build", "clean": true },
      "model": { "role": "build", "temperature": 0.3, "thinking": "events" },
      "tools": { "mode": "required", "allow": ["write"] },
      "loop": { "maxTurns": 10, "maxNudges": 2, "review": true },
      "assertions": [
        { "type": "fileCountAtLeast", "value": 3 },
        { "type": "totalCharsAtLeast", "value": 2000 },
        { "type": "fileExtensionCountAtLeast", "ext": ".cs", "value": 2 }
      ]
    }
  ]
}
```

### 6.2 Resultado

```json
{
  "runId": "run_...",
  "manifestHash": "sha256:...",
  "status": "Passed",
  "gate": { "policy": "default", "pass": 7, "fail": 0, "skip": 1, "stale": 0, "green": true },
  "stages": [
    {
      "key": "L2",
      "status": "Passed",
      "gate": { "green": true },
      "steps": [
        { "key": "belts-build-roxa-bridge", "status": "Passed", "durationMs": 213000, "cause": "" }
      ],
      "scenarios": [
        {
          "key": "roxa-build",
          "status": "Passed",
          "metrics": { "files": 8, "chars": 16327, "toolCalls": 12, "tokensOut": 16000 },
          "assertions": [
            { "type": "fileCountAtLeast", "expected": 3, "actual": 8, "passed": true }
          ]
        }
      ]
    }
  ],
  "environment": {
    "runnerVersion": "1.0.0",
    "contractVersion": "1.0",
    "bridgeSha": "...",
    "model": "Gemma4-E4B-Q4",
    "nCtx": 131072
  },
  "artifacts": [
    { "kind": "events", "path": "events.jsonl", "sha256": "..." },
    { "kind": "summary", "path": "summary.tsv", "sha256": "..." }
  ]
}
```

### 6.3 Modos de integração

1. **Offline (primeiro):** `--manifest arquivo.json --result resultado.json`. O AI.Manager exporta e importa; não depende de rede.
2. **Online local:** runner consulta `claim`, envia eventos, completa. Só localhost.
3. **Remoto futuro:** mesmo contrato, com lease, token, TLS e upload de artefatos. Nenhum template muda.

### 6.4 Endpoints mínimos

```text
POST /api/experiments?kind=test
POST /api/experiments/{id}/plans
POST /api/experiments/{id}/plans/{v}/validate
POST /api/experiments/{id}/plans/{v}/approve
GET  /api/experiments/{id}/plans/{v}/manifest
POST /api/experiments/{id}/plans/{v}/runs

GET  /api/test-runs/{id}
GET  /api/test-runs/{id}/events
POST /api/test-runs/{id}/pause
POST /api/test-runs/{id}/resume
POST /api/test-runs/{id}/cancel

POST /api/test-runners/register
POST /api/test-runners/{id}/heartbeat
POST /api/test-runners/{id}/claim
POST /api/test-runs/{id}/complete

POST /api/test-runs/{id}/mirror
GET  /api/test-runs/{id}/mirror
GET  /api/mirror-divergences?status=open
POST /api/mirror-divergences/{id}/triage
POST /api/mirror-divergences/{id}/resolve
```

### 6.5 Checksum e versionamento

- Canonicalização determinística do JSON (chaves ordenadas, cultura invariante, sem timestamps voláteis).
- `manifestHash` SHA-256 calculado no AI.Manager e verificado pelo runner.
- Divergência = `Blocked`, sem executar.
- `schemaVersion` com compatibilidade `1.x`; runner anuncia versões suportadas.
- Campos desconhecidos em minor são ignorados; step kind/assertion desconhecidos são rejeitados.
- Assinatura HMAC opcional para gate experiments.

---

## 7. Absorção do OpenCodeBridge no `AI.Manager.Bridge`

### 7.1 Projetos de destino

```text
Rochas.AI.Manager.Bridge.Contracts   (DTOs, schema, canonicalização, checksum)
Rochas.AI.Manager.Bridge             (host builder, transporte, fluxo, driver)
Rochas.AI.Manager.Bridge.Host        (console/systemd, CLI, Q4/Q3)
Rochas.AI.Manager.Bridge.Client      (HTTP/SSE tipado)
Rochas.AI.Manager.Bridge.Tests       (unidade, paridade, conformidade)
```

Mapa de doadores:

| OpenCodeBridge (doador) | AI.Manager.Bridge (destino) |
|---|---|
| `Rochas.OpenCodeBridge/Program.cs` | `Bridge` (Host/Transport) + `Bridge.Host` (CLI) |
| `Rochas.OpenCodeBridge.Core` | `Bridge` (protocolo) ou `Bridge.Contracts` |
| `Rochas.OpenCodeBridge.Test/TestCircuitDriver.cs` | `Bridge` (Flow) |
| `Rochas.OpenCodeBridge.Test/WarTemplate.cs` | seed JSON + `ManifestCircuitTemplate` |
| `Rochas.OpenCodeBridge.Test/E2E*.cs` | `ExperimentPlan`/`TestScenario` seed + testes de conformidade |
| `Rochas.OpenCodeBridge.Web` | UI do AI.Manager (o que for útil) |
| `Rochas.OpenCodeBridge.Client` | `Bridge.Client` |
| `Rochas.OpenCodeBridge.TestContracts` | `Bridge.Contracts` |
| `Rochas.OpenCodeBridge.Runner` | `Bridge.Host`/Flow |

O `Rochas.AI.Manager.Bridge` atual é o embrião; não é removido. A dependência dele com `Application`/`Domain` é eliminada, movendo helpers/opções para `Bridge.Contracts` ou para o próprio `Bridge`.

### 7.2 `ManifestCircuitTemplate` e fluxo

Responsabilidades:

- mapear `ExperimentPlan`/`CircuitStage` → execução de um `TestCircuitDriver`;
- mapear `CircuitStep` → `CircuitStepSpec`;
- resolver `DependsOn`, `When`, `NextSteps`, `MaxParallelSteps`;
- selecionar parser de veredito por nome;
- aplicar `GatePolicy` nomeada;
- validar capabilities do executor;
- reportar eventos estruturados.

Nada de `eval`, nada de C# dinâmico. Tipos de step, parsers e assertions são registrados em código no `AI.Manager.Bridge`; a UI só configura parâmetros. O driver é o mesmo kernel do OpenCodeBridge, agora com dono e namespace do AI.Manager.

### 7.3 Step kinds iniciais

| Kind | Uso |
|---|---|
| `dotnet-test` | Chama o projeto de testes com `--only`, `--belts`, `--mode` |
| `web-test` | Chama o `Web.Test` |
| `build` | `CircuitBuildSpec` |
| `deploy` | `CircuitDeploymentSpec` |
| `http` | Preflight/health |
| `shell` | Allowlist, casos controlados |
| `native` | Executor genérico de cenário (fase posterior) |

### 7.4 Paridade antes de cortar

- Para L1/L2/L3, o manifesto resolve exatamente os mesmos comandos, args, env, timeouts, parsers e gate do `WarTemplate`.
- `--dry-run` mostra o comando resolvido.
- Comparar `summary.tsv` e exit code entre host legado e host novo.
- Rodar o host novo em porta alternativa e comparar com Q4 antes do cutover.
- Só cortar depois de paridade verde e três execuções Q4 consecutivas verdes.

### 7.5 Regras que não mudam

- A bridge tem estratégia fixa (flags, ctx, `max_tokens`, harness, tails).
- Testes se adaptam à bridge; a bridge não se adapta ao teste.
- Q4 `:4124` é gate; Q3 `:4125` é espelho pós-green, nunca gate.
- O espelho Q3 roda o mesmo `manifestHash` depois do Q4 verde; divergência abre workflow, não PASS/FAIL de aceite.
- Nenhum PASS obtido por trocar instância, afrouxar assertiva ou ligar coerção.

### 7.6 `WarTemplate` como oráculo de seed, nunca como runtime

O WAR atual é a melhor descrição executável disponível e serve de **oráculo de bootstrap** temporário:

- **O que o `WarTemplate` é:** fonte do seed inicial, fixture de paridade e adaptador legado.
- **O que ele não é:** fonte de verdade contínua, dependência do runtime, configuração lida em execução ou schema do domínio.
- **Onde o seed fica:** `AI.Manager/Data/Seeds/war-baseline.json`, gerado por `--export-war-manifest`, com `schemaVersion`, `sourceCommit`, `manifestHash` e data.
- **Quem importa:** `WarBaselineSeeder` no AI.Manager (`Infrastructure/TestBench`), idempotente por hash, que cria `Experiment` (`Kind=Test`) + `ExperimentPlan` em `Draft` (ou `Approved` só com flag explícita de bootstrap).
- **O que o seeder não pode fazer:** sobrescrever versão aprovada, importar em todo boot, ler C# do OpenCodeBridge, chamar `WarTemplate` em produção ou copiar arrays de belts para o domínio.
- **O que o OpenCodeBridge expõe:** `--export-war-manifest` e o teste de paridade. O `WarTemplate` continua no projeto de testes legado; o runtime novo não o referencia.
- **Critério de saída do viés:** quando a UI já cria/edita/versiona o circuito e a paridade L1/L2/L3 está verde, o seed é congelado como histórico. A partir daí, o AI.Manager é a fonte e o WAR vira apenas legado de comparação.

O schema do AI.Manager deve ser genérico (`ExperimentPlan`, `CircuitStage`, `TestScenario`, `TestSelector`); L1/L2/L3 e as belts são **dados do seed**, não constantes do modelo. Se o seed ditar o schema, o viés vira dívida.

---

## 8. Mudanças no AI.Manager

### 8.1 Bounded context de experimentos e test bench

Reusar `Domain/Experiments` e o workflow existente; adicionar o que é específico de circuito/execução:

```text
Domain/Experiments/Experiment.cs          (Kind, Statement, WorkflowCardId)
Domain/TestBench/                         (ExperimentPlan, CircuitStage, CircuitStep, TestScenario, TestRun, MirrorRun, Evidence)
Application/TestBench/                    (workflow, ManifestBuilder, Validator, Acceptance, MirrorWorkflow)
Infrastructure/TestBench/                 (canonicalizer, seed reader, evidence store)
Api/Controllers/ExperimentsController.cs  (estender, não duplicar)
Api/Controllers/TestRunsController.cs
Api/Controllers/TestRunnersController.cs
Web/Controllers/TestBenchController.cs
Web/Views/TestBench/
```

### 8.2 `Rochas.AI.Manager.Bridge` vira o motor definitivo

- Não remover `Rochas.AI.Manager.Bridge`; expandi-lo e desacoplá-lo de `Application`/`Domain`.
- Mover `ChatToResponses`/helpers e opções de harness para `Bridge.Contracts` ou para o próprio `Bridge`.
- Portar o host do OpenCodeBridge em fatias, com testes de paridade; não copiar `Program.cs` inteiro.
- `BindingBridgeController` deixa de ser um proxy permanente: passa a chamar o `Bridge` in-process ou `Bridge.Client` quando o host for extraído, gravando `HarnessRun`/`BridgeCapture` com IDs reais.
- A sanitização/tradução de protocolo é responsabilidade do `AI.Manager.Bridge`; não duplicar no controller.

### 8.3 Cliente e contratos

`Rochas.AI.Manager.Bridge.Client` deve oferecer:

- `/v1/responses` e `/v1/chat/completions`;
- streaming SSE;
- `/api/status`, `/api/metrics`, `/api/tools/execute`;
- autenticação, cancelamento, timeout, erros tipados;
- sem dependência de SQLite, tools ou domínio.

O AI.Manager usa esse cliente em `Infrastructure`; `Application` define a porta `IBridgeGateway`; `Domain` não depende de transporte. `Bridge.Contracts` é referenciado por `Application` e pelo `Bridge`, sem ciclo.

### 8.4 Evidência e histórico

- `TestRun` com `experiment_id`, `experiment_plan_id`, `model_binding_id`, `manifest_hash`, `runner_id`, `gate`.
- `BridgeCapture` com `test_run_id`, `scenario_run_id`, `sha256`, `path`.
- `HarnessRun` antigo preservado para o proxy, mas com `EvidenceId`/`ProfileVersionId` reais.
- Artefatos grandes em filesystem/objeto, metadados no SQLite.

### 8.5 Persistência

- Tabelas novas; sem alterar destrutivamente as existentes.
- Dapper pelo padrão atual: `IGenericRepository<T>` + `IPersistenceRepository<T>` (`Rochas.DapperRepository`), registrados por entidade nos dois `Program.cs`.
- `AppDb.Init` continua o ponto de schema (`CREATE TABLE IF NOT EXISTS`) e recebe as migrações idempotentes novas.
- Índices em `created_at`, `status`, `experiment_id`, `experiment_plan_id`, `runner_id`.
- `manifest_json` snapshot na versão aprovada; tabelas normalizadas para edição.
- Export/import JSON para backup, CI e revisão.

---

## 9. UI: guia total do circuito

A UI deve ser o painel de direção, não um editor de código.

### 9.1 Catálogo de experimentos

- nome, tipo (treino/teste), status, versão atual;
- alvo (model binding, harness, runner);
- último gate, duração, data;
- filtros por tipo, status, belt, runner, tag;
- ações: clonar, comparar, abrir runs.

### 9.2 Editor guiado — wizard de 6 passos

1. **Objetivo** — hipótese, descrição, tags, artefato/modelo alvo.
2. **Alvo** — model binding, harness version, runner profile (Q4 gate; Q3 espelho pós-green).
3. **Circuito** — stages, steps, dependências, build/deploy, preflight, timeout/retry, gate.
4. **Enunciados** — cenários, prompts, workspace, ferramentas, loop, assertions.
5. **Revisão** — validação, dry-run, comandos resolvidos, diff contra versão anterior.
6. **Aprovação** — checksum, assinatura, aprovar/deprecar.

### 9.3 Mapa do circuito

- stages em ordem, steps com `DependsOn`;
- indicadores de gate por stage;
- destaque para steps `code-backed` vs `native`;
- botão “ver comando resolvido”;
- aviso de incompatibilidade runner/capability.

### 9.4 Editor de cenário

- prompt e system prompt com preview e contagem de chars;
- placeholders `{ws}`, `{case}`, `{runId}`, `{model}`;
- workspace template e política de limpeza;
- tools permitidas, `toolChoice`, coerção;
- loop: turnos, nudges, stall, review;
- pipeline multi-role (orch/script/exec);
- assertions em builder + modo JSON avançado;
- “duplicar cenário”, “comparar com baseline”.

### 9.5 Console de execução

- fila, runner, lease, heartbeat;
- status por stage/step em tempo real;
- logs e `events.jsonl`;
- pausar/retomar/cancelar;
- retry de step falho;
- artefatos e captura de request/response;
- botão “reproduzir localmente” com o comando exato;
- aba **Espelho Q3**: habilita somente quando o gate Q4 está verde, dispara o mesmo `manifestHash` e mostra `Aligned`/`Divergent`.

### 9.6 Resultado e evidência

- gate do run, contadores e política;
- assertions PASS/FAIL com esperado × real;
- arquivos criados, chars, tool calls, tokens, tps, duração;
- classificação da falha: infra, bridge, fixture, executor, timeout, assertion, modelo;
- comparação entre runs/versões;
- **painel de espelho**: Q4 × Q3 lado a lado, divergências por step/assertion, classificação e ação;
- workflow de divergência: `Detected → Triaged → Fixing → RevalidateQ4 → RevalidateQ3 → Closed`;
- aceitar/rejeitar experimento com motivo, usando **apenas Q4**.

### 9.7 Runners e biblioteca

- runners registrados, capacidades, GPU, versão, status;
- perfis Q4 (gate) e Q3 (espelho pós-green);
- biblioteca de harness, prompts, assertions, workspace templates;
- relatórios de flakiness, taxa de PASS, regressão, tempo e **divergência Q4×Q3**.

### 9.8 Regra de ouro da UI

- UI edita **dados**; não injeta C#.
- Comando novo/assertion nova = release do OpenCodeBridge + versão de contrato.
- Versão aprovada é imutável; editar sempre cria nova versão.
- Q3 é espelho pós-green; nunca conta como gate.
- Observação manual é diagnóstico, não aceite.
- Divergência Q3 não é “FAIL do experimento”: é item de workflow com dono e ação.
- Nenhum botão “passar” altera flags da bridge, ctx, `max_tokens` ou aceite.

---

## 10. Migração em fases

### Fase 0 — Congelar doador e baseline

- congelar o OpenCodeBridge para novas features; só correções de gate/segurança;
- registrar tail/versão real do Q4, sem restart não autorizado;
- exportar `war-baseline.json` com níveis, steps, belts, variantes, gate e preflight;
- versionar o seed no AI.Manager e registrar `sourceCommit` + `manifestHash`;
- registrar o mapa de doadores (host, Core, TestDriver, WAR, Web, Client, TestContracts).

**Aceite:** baseline L1/L2/L3 documentado; seed reproduzível; nenhuma mudança de comportamento; repo doador marcado como congelado.

### Fase 1 — Desacoplar e ampliar o `AI.Manager.Bridge`

- criar `Bridge.Contracts` e remover a dependência de `Bridge` com `Application`/`Domain`;
- mover tradução, stream, anti-repeat, harness tails e opções para `Bridge`;
- manter a API atual funcionando por proxy enquanto o host novo não existe;
- testes de conformidade do Bridge atual.

**Aceite:** solution AI.Manager build 0/0; testes atuais do Bridge verdes; nenhum projeto do AI.Manager referencia `Rochas.OpenCodeBridge.*`; `Bridge` não referencia `Application`/`Domain`.

### Fase 2 — Portar o host por fatias com paridade

- criar `Bridge.Host` com CLI/config equivalente ao host do OpenCodeBridge;
- portar perfis/opções, harness/tails, breaker/permissões, coerção/shim, tool loop, telemetria, endpoints HTTP;
- rodar o host novo em porta alternativa e comparar com o host legado;
- `--dry-run`, `--export-war-manifest`, golden tests e diff de requisições.

**Aceite:** host novo responde `/api/status` e `/v1/*` com o mesmo contrato; paridade de respostas/stream/tools nos fixtures; nenhum corte de Q4 ainda.

### Fase 3 — Reusar `Experiment` + workflow e versionar o circuito

- estender `Experiment` (`Kind`, `Statement`, `WorkflowCardId`) e usar `TrainingWorkflowCard` existente;
- adicionar `ExperimentPlan`, `CircuitStage`, `CircuitStep`, `TestScenario`, `TestRun`, `MirrorRun`, `Evidence`;
- `ManifestBuilder`/`ManifestCanonicalizer` e `WarBaselineSeeder` idempotente;
- API/UI de catálogo, workflow, circuito e versão; ainda sem executar em produção.

**Aceite:** criar/clonar/aprovar um experimento de teste; manifesto exportado idêntico ao fixture do host; reexecutar o seeder não duplica versão nem sobrescreve aprovada; nenhum `TestExperiment` paralelo.

### Fase 4 — Execução offline e cutover do L1 no Q4

- `Bridge.Host --manifest`/`--result` e `ManifestCircuitTemplate`;
- AI.Manager exporta/importa; UI mostra gate, steps e evidências;
- rodar L1 no Q4 e comparar com o host legado;
- cortar Q4 só com paridade e três gates verdes consecutivos.

**Aceite:** mesmos PASS/FAIL/STALE; Q4 é o gate; evidência rastreável; rollback para o host legado documentado.

### Fase 5 — Espelho Q3 pós-green

- habilitar `MirrorRun` somente quando o gate Q4 do mesmo `manifestHash` estiver verde;
- executar o mesmo manifesto no perfil Q3, sem alterar o gate;
- comparar step/assertion e abrir `MirrorDivergence` quando houver diferença;
- workflow `Detected → Triaged → Fixing → RevalidateQ4 → RevalidateQ3 → Closed`;
- observação manual entra como nota de triagem, nunca como assertion ou aceite.

**Aceite:** Q3 não altera o gate; divergência gera item de workflow; Q4 continua sendo o único critério de aprovação.

### Fase 6 — Migrar L2/L3 e arquivar o OpenCodeBridge

- flag `executor=bridge` vs `executor=legacy`;
- shadow runs e 3 gates verdes consecutivos no Q4 por nível;
- espelho Q3 acompanhando o green do Q4;
- remover arrays de belts hardcoded e o `WarTemplate` do runtime;
- atualizar docs, scripts e units para o host do `AI.Manager.Bridge`;
- arquivar o repo OpenCodeBridge (histórico preservado) e remover a dependência de seus projetos.

**Aceite:** WAR continua com o mesmo aceite; Q4 é o único gate; nenhum PASS obtido por afrouxamento; o OpenCodeBridge não é mais necessário para rodar Q4/Q3.

---

## 11. Segurança e reprodutibilidade

Como a UI passa a direcionar execução, ela vira um plano de controle sensível:

- **manifesto assinado/HMAC + checksum**; runner rejeita divergência;
- **allowlist de step kinds e executáveis**; nada de shell livre por padrão;
- **WorkspaceGuard/ShellInspector** no runner; sem `sudo`, sem `/usr/src`;
- **aprovação em quatro olhos** para experimentos de gate;
- **segredos só por referência** (`secretRef`), nunca no manifesto/DB/log;
- **snapshot** de model binding, harness, runner profile e workspace no manifesto;
- **Q4/Q3 explícitos**: `isGate` no runner profile; Q3 é `isMirror` e não aprova experimento;
- **espelho só pós-green**: Q3 não inicia sem gate Q4 verde do mesmo `manifestHash`;
- **evidência com sha256**, retenção e backup;
- **auditoria** de criação, aprovação, execução, espelho, triagem e alteração.

---

## 12. Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Divergência entre manifesto e WAR | testes de paridade + shadow runs + gate Q4 |
| UI virar shell remoto | allowlist, aprovação, assinatura, workspace guard |
| Big-bang na extração do host | strangler por fatias, porta alternativa, paridade e rollback |
| `Bridge` voltar a depender de `Application`/`Domain` | contratos próprios; regra de dependência no build |
| Duas fontes de verdade durante a transição | `AI.Manager.Bridge` é o destino; OpenCodeBridge congelado e sem feature nova |
| Acoplamento de releases | `Bridge.Contracts` versionado + host standalone após extração |
| Testes longos/flaky | pause/resume, retry policy, artefatos, sem auto-PASS |
| SQLite lock | executor nunca lê DB; API only; WAL/índices |
| Perda de evidência | armazenamento persistente + hash + backup |
| Complexidade de migrar belts multi-role | começar por build belts; manter orch como code-backed |
| Q3 divergir por capacidade menor | espelho pós-green, classificação e workflow; nunca gate |
| “Green” manual virar aceite | observação manual só como diagnóstico; aceite é Q4 automatizado + evidência |
| Perda de conhecimento ao arquivar o OCB | mapa de doadores, testes de paridade e histórico preservado |
| Divergência entre clone local e remoto (docs v1 × v2) | v2 é a fonte; espelho atualizado no mesmo commit; HEADs registrados |
| Token/credencial exposta em chat | revogar, rotacionar e nunca gravar em arquivo/histórico |
| WAR L3 ainda pendente (marrom/preta) | retomar depois do lift do ambiente; não confundir com a fusão |
| Q4 rodando com tail antiga após mudança de harness | conferir `/api/harness` (chars) antes de declarar PASS |
| Migração .NET 10 local não enviada | alinhar clone/PROD antes de cortar o host legado (rollback documentado) |

---

## 13. Critérios de aceite da evolução

1. Build da solution AI.Manager em Release com 0 warnings/0 erros; build do doador OCB preservado até o arquivamento.
2. `AI.Manager.Bridge` desacoplado: sem referência a `Application`/`Domain`; `Bridge.Contracts` compartilhado.
3. Paridade de manifesto vs `WarTemplate` para L1/L2/L3 (comandos, args, env, timeout, parser e gate).
4. L1/L2/L3 no Q4 com o mesmo resultado do legado antes de virar a chave.
5. Q3 nunca contabilizado como gate; espelho roda somente após Q4 verde e divergência vira item de workflow.
6. Observação manual não é critério de aceite; aceite é Q4 automatizado + evidência.
7. Versão aprovada imutável; edição cria nova versão; checksum estável.
8. Executor incompatível ou manifesto adulterado = `Blocked`, sem execução.
9. Run com evidência completa: experimento, plano, manifesto, executor, modelo, harness, gate, steps, artefatos e hashes.
10. UI permite criar, validar, aprovar, executar, pausar/retomar, cancelar, comparar, rodar espelho e aceitar/rejeitar.
11. Nenhum PASS obtido por afrouxamento de assertiva, troca de instância ou mudança de `max_tokens`/ctx.
12. OpenCodeBridge arquivável: nenhum projeto do AI.Manager referencia `Rochas.OpenCodeBridge.*`; Q4/Q3 rodam pelo `AI.Manager.Bridge.Host`.

---

## 14. Decisões abertas

Fechadas nesta revisão:

1. **Runner no primeiro corte:** **in-process** dentro do AI.Manager, com as interfaces (`IBridgeGateway`, `ManifestBuilder`, `ICircuitExecutor`) preservadas para extração futura. Processo separado só se o isolamento se provar necessário.
2. **Nomes dos projetos:** `Rochas.AI.Manager.Bridge` (motor + Flow), `Rochas.AI.Manager.Bridge.Contracts` e `Rochas.AI.Manager.Bridge.Client` nascem já no primeiro corte; `Bridge.Host` começa como console dentro do projeto Bridge e vira projeto próprio na extração.
3. **Como o OpenCodeBridge consome o Bridge:** durante a transição, `ProjectReference` para o clone local do AI.Manager (caminho configurável, sem submódulo) e, a partir da primeira versão estável, pacote `Rochas.AI.Manager.Bridge.Contracts`/`.Client` no feed NuGet interno (`net10.0`, SemVer).
4. **`TestScenario`:** permanece entidade própria (`test_scenarios`), ligada a `ExperimentPlan`; o enunciado é dado versionado, não texto solto em `Experiment.Statement` (que guarda o resumo exibido na UI).
5. **Contrato público:** `net10.0`, SemVer, `schemaVersion 1.x`; minor aditivo, major para quebra.
6. **Evidência:** artefatos grandes no filesystem do AI.Manager (ou store externo por `EvidenceStore`), metadados e hash no SQLite.

Ainda em aberto:

1. **Cutover:** exigir 3 gates Q4 verdes por nível antes de arquivar o OpenCodeBridge (recomendado) ou um único gate + shadow run?
2. **Escopo inicial:** L1 + belts build (recomendado) ou tentar L3 de imediato?
3. **Local do documento:** este arquivo é a fonte canônica; o OpenCodeBridge mantém cópia sincronizada em `docs/ai-manager/AI-MANAGER-NEXT-STEPS.md` até o arquivamento.

Decidido nesta revisão:

- **`AI.Manager.Bridge` é o sucessor do OpenCodeBridge.** Não é um duplicado a remover; o OpenCodeBridge é doador transitório.
- **Reusar `Experiment` + `TrainingWorkflowCard`.** Não criar `TestExperiment` paralelo.
- **Testes são dados do workflow.** Arrays/belts hardcoded viram `ExperimentPlan`/`TestScenario`; o `TestCircuitDriver` vira kernel de fluxo do Bridge.
- **Q4 é o gate; Q3 é espelho pós-green.** O Q3 não inicia antes do Q4 verde e não altera a aceitação.
- **Divergência Q3 é workflow, não FAIL de experimento.** O AI.Manager abre `MirrorDivergence` com classificação, dono e ação.
- **Teste manual é diagnóstico desta fase.** Não vira assertion, não substitui evidência e não aprova experimento.
- **Não há big-bang no `Program.cs`.** A extração do host é por fatias, com paridade e rollback.

---

## 15. Referências

- `Rochas.OpenCodeBridge.Test/TestCircuitDriver.cs` — kernel de fluxo (doador).
- `Rochas.OpenCodeBridge.Test/WarTemplate.cs` — WAR atual, níveis, preflight, passos (doador).
- `Rochas.OpenCodeBridge.Test/E2EBeltsBuildTests.cs` — enunciados e loop de build (doador).
- `Rochas.OpenCodeBridge.Test/E2EBeltsTests.cs` — pipeline orch/script/exec (doador).
- `Rochas.OpenCodeBridge.Core/BridgeProxy.cs` — proxy in-process e opções (doador).
- `Rochas.OpenCodeBridge/Program.cs` — pipeline real do host (doador; portar em fatias).
- `src/Rochas.AI.Manager.Bridge/` — embrião do motor definitivo; expandir e desacoplar (Host/Protocol/Flow/Contracts/Client).
- `src/Rochas.AI.Manager.Api/Controllers/BindingBridgeController.cs` — proxy temporário.
- `src/Rochas.AI.Manager.Domain/Experiments/Experiment.cs` — agregado reusado (`Kind`).
- `src/Rochas.AI.Manager.Domain/Entities/TrainingWorkflowCard.cs` — workflow existente.
- `src/Rochas.AI.Manager.Domain/Harness/HarnessRun.cs` — run atual com IDs zerados.
- `docs/ai-manager/diagrams/gen-c4.py` — gerador do C4 v2 (absorção).
- `docs/ai-manager/diagrams/ai-manager-next-steps-c4.drawio` / `.png` — C4 v2.
- `docs/ai-manager/Rochas.AI.Manager-Arquitetura v2.md` — baseline e invariantes.
- `docs/ai-manager/REPORT-PENDINGS.md` — pendências de relatórios.
- `TEST-DRIVER.md` (OpenCodeBridge) — documentação do driver e da pipeline (doador).

---

## 16. Próximo passo recomendado

Implementar a **fatia vertical 1** dentro do AI.Manager:

1. Criar `Bridge.Contracts` e desacoplar `AI.Manager.Bridge` de `Application`/`Domain`.
2. Mover tradução, stream, anti-repeat e harness tails para o Bridge, com testes de conformidade.
3. Criar o esqueleto de `Bridge.Host` e rodar em porta alternativa com `/api/status`.
4. Reusar `Experiment` + `TrainingWorkflowCard`; adicionar `ExperimentPlan` e o seed `war-baseline.json`.
5. UI read-only do catálogo/workflow/circuito, sem executar produção.
6. Só depois: porta do host real, paridade L1, execução offline no Q4 e espelho Q3.

Essa fatia começa a absorção sem tocar no gate. O corte do Q4 só acontece com paridade verde e três execuções Q4 consecutivas verdes; o Q3 acompanha na sequência como espelho. O repo OpenCodeBridge só é arquivado quando o host novo for o único runtime.

---

## Apêndice A — Inventário do AI.Manager e fatiamento de implementação

### A.1 Forma atual do repositório

- Solution `Rochas.AI.Manager.slnx`, `net10.0`, projetos: `Api`, `Application`, `Bridge`, `Core`, `Domain`, `Infrastructure`, `TrainingEngine`, `Web` e `Tests`.
- `Bridge` é o embrião do `AI.Manager.Bridge`; hoje contém tradução/stream/anti-repeat, referencia `Application`/`Domain` e é usado pelo seeder/testes. O destino é desacoplá-lo e ampliá-lo, não removê-lo.
- `Experiment` é de treino; será estendido com `Kind=Test` e `Statement`. `TrainingWorkflowCard` é o workflow existente e será reusado. `HarnessRun`/`BridgeCapture` são o registro atual do proxy.
- Persistência: `Rochas.DapperRepository` com `IGenericRepository<T>` (leitura) e `IPersistenceRepository<T>` (escrita), registrados entidade por entidade em `Api/Program.cs` e `Web/Program.cs`.
- Schema: `Infrastructure/Data/AppDb.cs` centraliza `CREATE TABLE IF NOT EXISTS` e migrações idempotentes (`ALTER TABLE ... ADD COLUMN`, com catch de coluna existente).
- API e Web duplicam o bloco de DI; qualquer repositório/serviço novo precisa ser registrado nos **dois** `Program.cs`.
- UI: MVC com uma pasta de controller/view por feature (`Experiments`, `Harness`, `HarnessRuns`, `ModelBindings`, etc.); o menu vive em `Views/Shared/_Layout.cshtml`.
- Testes: xUnit em `tests/Rochas.AI.Manager.Tests`, com testes de serviço, bridge e helpers.

### A.2 Arquivos a criar na Fase 2

```text
src/Rochas.AI.Manager.Bridge.Contracts/
  BridgeManifest.cs
  BridgeResult.cs
  BridgeOptionsContract.cs
  ManifestSchema.cs
  ManifestCanonicalizer.cs
  BridgeContractVersion.cs

src/Rochas.AI.Manager.Bridge/                  (expandir o projeto existente)
  Host/BridgeHostBuilder.cs
  Transport/ResponsesToChat.cs
  Transport/ChatToResponses.cs
  Transport/ResponsesStream.cs
  Transport/ToolLoop.cs
  Transport/HarnessTails.cs
  Transport/Breaker.cs
  Transport/Permissions.cs
  Flow/TestCircuitDriver.cs
  Flow/ManifestCircuitTemplate.cs
  Flow/CircuitStepSpec.cs
  Flow/GatePolicy.cs
  Telemetry/BridgeTelemetry.cs

src/Rochas.AI.Manager.Bridge.Host/
  Program.cs
  BridgeHostOptions.cs

src/Rochas.AI.Manager.Bridge.Client/
  BridgeHttpClient.cs
  BridgeSseClient.cs

src/Rochas.AI.Manager.Domain/Experiments/
  Experiment.cs                         (Kind, Statement, WorkflowCardId)

src/Rochas.AI.Manager.Domain/TestBench/
  ExperimentPlan.cs
  CircuitStage.cs
  CircuitStep.cs
  TestScenario.cs
  ScenarioPipeline.cs
  AssertionSpec.cs
  TestRun.cs
  StageRun.cs
  StepRun.cs
  ScenarioRun.cs
  MirrorRun.cs
  MirrorDivergence.cs
  Evidence.cs
  RunnerProfile.cs
  RunnerRegistration.cs

src/Rochas.AI.Manager.Application/TestBench/
  IExperimentWorkflowService.cs
  ExperimentWorkflowService.cs
  IManifestBuilder.cs
  ManifestBuilder.cs
  IManifestValidator.cs
  ManifestValidator.cs
  IAcceptanceService.cs
  AcceptanceService.cs
  IWarBaselineSeeder.cs
  WarBaselineSeeder.cs
  IMirrorWorkflowService.cs
  MirrorWorkflowService.cs

src/Rochas.AI.Manager.Infrastructure/TestBench/
  ManifestCanonicalizer.cs       (JSON canônico + SHA-256)
  WarBaselineReader.cs           (lê Data/Seeds/war-baseline.json)
  EvidenceStore.cs

src/Rochas.AI.Manager.Api/Controllers/
  ExperimentsController.cs       (estender com Kind=Test)
  TestRunsController.cs
  TestRunnersController.cs
  MirrorDivergencesController.cs

src/Rochas.AI.Manager.Web/Controllers/
  TestBenchController.cs

src/Rochas.AI.Manager.Web/Views/TestBench/
  Index.cshtml
  Edit.cshtml
  Circuit.cshtml
  Runs.cshtml
  RunDetails.cshtml
  Mirror.cshtml

src/Rochas.AI.Manager.Web/Views/Shared/_TestBenchNav.cshtml

Data/Seeds/war-baseline.json

tests/Rochas.AI.Manager.Tests/TestBench/
  ExperimentPlanTests.cs
  ManifestBuilderTests.cs
  ManifestValidatorTests.cs
  WarBaselineSeederTests.cs
  AcceptanceServiceTests.cs
  MirrorWorkflowTests.cs

tests/Rochas.AI.Manager.Bridge.Tests/
  TranslationParityTests.cs
  StreamParityTests.cs
  TestCircuitDriverTests.cs
  ManifestCircuitTemplateTests.cs
```

### A.3 Tabelas iniciais

```text
experiments                              (tabela existente; ALTER idempotente)
  + kind, statement, workflow_card_id
  id, code, name, hypothesis, status, owner_login, created_at, updated_at

training_workflow_cards                 (tabela existente; reusar)
  id, experiment_id, title, description, status_id, next_status_id, ...
  -- nenhuma tabela test_workflow_cards

experiment_plans                         (versão imutável do circuito)
  id, experiment_id, version, status, model_binding_id, harness_profile_version_id,
  runner_profile_id, manifest_json, manifest_hash, schema_version, approved_by,
  approved_at, created_at

circuit_stages
  id, plan_id, key, title, sort_order, max_parallel_steps, gate_policy, on_failure

circuit_steps
  id, stage_id, key, kind, command, args_json, env_json, timeout_sec,
  retry_json, depends_on_json, when_json, next_steps_json, selector_json

test_scenarios
  id, plan_id, key, title, prompt, system_prompt, workspace_json,
  model_role, temperature, thinking, max_tokens, tools_json, loop_json,
  assertions_json, tags, weight

test_runs
  id, experiment_id, plan_id, runner_id, manifest_hash, status,
  started_at, finished_at, gate_json, environment_json, created_at

test_stage_runs / test_step_runs / test_scenario_runs
  ids, parent run, key, status, duration_ms, cause, metrics_json, artifact_root

test_mirror_runs
  id, gate_run_id, mirror_run_id, manifest_hash, status, created_at

test_mirror_divergences
  id, mirror_run_id, step_key, kind, q4_value, q3_value, classification,
  owner, action, status, created_at, updated_at

test_evidence
  id, run_id, scenario_run_id, kind, path, sha256, size_bytes, created_at

runner_profiles
  id, key, name, is_gate, is_mirror, bridge_url, upstream_url,
  model_binding_id, context_window, max_tokens, capabilities_json

runner_registrations
  id, profile_id, host, version, contract_version, status,
  last_heartbeat, capabilities_json
```

Índices mínimos:

```text
experiments(kind, status)
experiment_plans(experiment_id, version) UNIQUE
test_runs(experiment_id, plan_id, created_at)
test_runs(manifest_hash)
test_runs(runner_id, status)
test_mirror_runs(gate_run_id) UNIQUE
test_mirror_divergences(mirror_run_id, status)
test_evidence(run_id, kind)
```

### A.4 Contratos de aplicação

- `IExperimentWorkflowService`: CRUD, clone, diff, validate, approve e deprecate do `Experiment` (`Kind=Training|Test`) e das versões (`ExperimentPlan`).
- `IManifestBuilder`: resolve versão aprovada + runner profile + harness + model binding em `manifest_json`; calcula `manifest_hash`; nunca consulta `WarTemplate`.
- `IManifestValidator`: schema, capabilities do runner, step kinds, assertions, placeholders e limites.
- `IAcceptanceService`: lê o gate do `AI.Manager.Bridge`, aplica política (Q4 apenas, 3 verdes, sem regressão) e aceita/rejeita.
- `IWarBaselineSeeder`: importa `Data/Seeds/war-baseline.json` de forma idempotente por `sourceCommit + manifestHash`; cria `Draft`; nunca sobrescreve `Approved`.
- `IMirrorWorkflowService`: cria `MirrorRun` apenas após Q4 verde; compara Q4×Q3; abre/fecha `MirrorDivergence`.

### A.5 Rotas e UI

- API: `/api/experiments?kind=test`, `/plans`, `/validate`, `/approve`, `/manifest`, `/runs`, `/mirror`, `/mirror-divergences`.
- Web: `/Experiments` (catálogo único, filtro por `Kind`), `/Experiments/Edit/{id}`, `/TestBench/Circuit/{planId}`, `/TestBench/Runs`, `/TestBench/RunDetails/{id}`, `/TestBench/Mirror/{runId}`.
- Menu: mantém **Experimentos** em `_Layout.cshtml` e acrescenta **Test Bench** apenas para as views de circuito/run/espelho; o cadastro e o workflow são os mesmos do experimento.
- O editor de circuito usa o mapa de stages/steps e o builder de assertions; o modo JSON avançado é somente para inspeção/importação, não para injetar C#.
- O console de run mostra Q4 e Q3 em abas separadas; a aba Q3 fica desabilitada até o gate Q4 verde.

### A.6 Ordem de implementação segura

1. Entidades + tabelas + repositórios + DI nos dois `Program.cs`.
2. `ManifestBuilder` + `ManifestCanonicalizer` + testes de hash.
3. `WarBaselineSeeder` + `Data/Seeds/war-baseline.json` + teste de idempotência.
4. API de experimentos/versões/manifesto (sem executar).
5. UI read-only do catálogo e do mapa do circuito.
6. Editor de cenário/assertions + validação.
7. Run offline + ingestão de resultado.
8. `MirrorRun`/`MirrorDivergence` + workflow.
9. Cutover do runner para o `Bridge.Host` do `AI.Manager.Bridge` (o projeto **não** é removido; ele vira o motor definitivo) e arquivamento do OpenCodeBridge.

### A.7 O que não fazer

- Não manter o motor de protocolo no OpenCodeBridge: o motor é do `AI.Manager.Bridge`; `Bridge.Client` é o cliente HTTP/SSE (host legado e consumidores externos).
- Não referenciar `WarTemplate` em runtime do AI.Manager; o seed é JSON versionado.
- Não colocar lógica de C# no banco/UI; step kind novo exige release do `AI.Manager.Bridge`.
- Não deixar Q3 influenciar aceitação, gate ou status do experimento.
- Não criar tabela/consulta que o runner precise ler; o contrato é o manifesto.
- Não commitar `EVOLUTION.md`, `HISTORY.md` ou `Properties/launchSettings.json` como parte desta frente sem revisão explícita.
