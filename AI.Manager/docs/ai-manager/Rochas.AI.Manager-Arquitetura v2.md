# Rochas.AI.Manager — Arquitetura v2.0

**Data:** 2026-10-20
**Status:** vigente (substitui a organização do v1 como referência de arquitetura; o v1.2 permanece como histórico de intenção e traz o as-built na seção 32).
**Escopo:** arquitetura da plataforma `OpenCodeBridge/AI.Manager` — domínio, aplicação, persistência, harness, bridge/proxy, workflow de aprovação, treinamento, relatórios, segurança, testes e roadmap.
**Documentos relacionados:** `Rochas.AI.Manager-Arquitetura v1.md` (§32 as-built), `REPORT-PENDINGS.md` (catálogo de relatórios), `AI.Manager/docs/ai-manager/ARQUITETURA-CAMADAS.md`, `GUIA-INICIANTE.md`, `GUIA-TUNING-EQUIPE-TECNICA.md`.

---

## 1. Sumário executivo

O Rochas.AI.Manager saiu do papel e hoje é uma aplicação .NET 10 com seis projetos de produção e uma suíte xUnit, cobrindo: catálogo de modelos e datasets, perfis de instrução e padrões, experimentos, jobs de treino com métricas, harness versionado (perfis, versões, anexos, políticas de tool, bindings, runs, capturas), workflow de aprovação com trilha de transições, API REST com Swagger, proxy OpenAI-compatible com saneamento de reasoning e coerção de tool calls, UI MVC com 18 controllers e 22 views, e exportação de relatórios em CSV/XLSX/PDF/HTML.

A v2 consolida o que existe e corrige três distorções entre intenção e código:

1. **Execução de treino e motor numérico.** O job roda dentro da requisição HTTP e o "backend TorchSharp" é, na prática, um MLP em `double[]`; o modo avançado chama `python3`. A v2 trata isso como baseline explícito e condiciona o motor real a uma prova verificável.
2. **Relatórios.** O contrato promete recorte temporal e facetas; a implementação aplica apenas `Status` e pagina em memória. A v2 define o subsistema de relatórios com registry declarativo, SQL whitelisted e semântica de intervalo fechado/aberto.
3. **Chat e dashboard.** A view de chat (1.498 linhas) chama sete rotas que não existem no AI.Manager, e não há dashboard operacional. A v2 decide: portar os endpoints ou retirar a view; e transforma o `/health` em base do painel.

A regra editorial permanece a do v1 (§28.1/§28.14): capacidade só é "concluída" com código, teste e evidência.

---

## 2. O que mudou da v1 para a v2

| Tema | v1 (intenção) | v2 (baseline + alvo) |
|---|---|---|
| Estrutura | 7 projetos incluindo `TrainingWorker` | 6 projetos reais + testes; worker vira hosted service e depois projeto dedicado (ADR-10) |
| UoW/Repositórios | `IRepository`/`IUnitOfWork` no Domain | Contratos do pacote `Rochas.DapperRepository` como baseline; contratos próprios removidos ou implementados (ADR-11) |
| Treinamento | TorchSharp multi-modelo desde o início | MLP é baseline rotulado; TorchSharp só após prova (ADR-06); Python vira adaptador externo opcional (ADR-07) |
| Harness | Seção 31 como requisito | Harness já modelado e semeado; falta Test Bench e vínculo de evidência (ADR-03) |
| Relatórios | "Estatísticas no admin" | Subsistema próprio com catálogo R1–R30, facetas e De/Até (ADR-04, ADR-05) |
| Bridge | Preservar contrato | Proxy `/v1/chat/completions` implementado; `/v1/responses` e flags pendentes |
| Chat | Reaproveitar chat web | View portada sem backend; decisão de portar endpoints (ADR-08) |
| Segurança | Autenticação/autorização genéricas | Chave JWT, seed e proxy precisam de hardening imediato (§15) |
| Documentação | Guideline extenso | Documento único de arquitetura + as-built no v1 + pendências separadas |

---

## 3. Escopo, não-escopo e premissas

**No escopo da v2**

- Consolidar o as-built e impedir regressão silenciosa.
- Relatórios com facetas de FK e recorte temporal confiável.
- Ciclo de vida de job de treino fora da requisição HTTP.
- Evidência de harness ponta a ponta (perfil → versão → run → captura).
- Segurança mínima de produção e observabilidade operacional.
- Contratos de teste para bridge e rotas.

**Fora do escopo (nesta versão)**

- Treino real de Gemma 4/Llama/Qwen/DeepSeek com pesos completos.
- Multimodal, áudio, ChatTTS e app mobile (permanecem como horizonte).
- Migração para PostgreSQL.
- Substituição da bridge por outro protocolo.

**Premissas**

- Uma máquina de desenvolvimento com duas bridges locais (`:4124` GPU, `:4125` CPU) e servidores llama em `:4100`/`:4110`.
- SQLite é suficiente para o volume atual (centenas a milhares de registros por tabela).
- O usuário final é técnico; "modo básico" continua sendo o caminho recomendado.

---

## 4. Baseline tecnológico (fixado)

| Item | Versão | Observação |
|---|---|---|
| .NET | 10.0 | `net10.0` em todos os projetos |
| ASP.NET Core | 10.0.13 | MVC + API + JwtBearer + OpenApi |
| Swashbuckle | 10.3.1 | Swagger em Development |
| Dapper / ORM | `Rochas.DapperRepository` 2.0.5 | `GenericRepository<T>`, `IGenericRepository`, `IPersistenceRepository` |
| Especificação | `Rochas.Data.Specification` 1.6.3 | usado como filtro de consulta |
| SQLite | `Microsoft.Data.Sqlite` 10.0.12 | WAL, `Cache=Shared`, `busy_timeout=10000` |
| PDF/Excel | `Rochas.PDFGenerator` 1.3.0 / `Rochas.ExcelToJsonParser` 1.6.4 | exportação de relatórios |
| Telemetria/log | `Serilog` 4.4.0 + `Serilog.AspNetCore` 10.0.0 + `Rochas.Telemetry` 1.1.1 | console estruturado |
| Numérico | `TorchSharp` 0.107.0 | referenciado, **não utilizado** no backend atual |
| Testes | xunit 2.9.3, Test.Sdk 17.14.1, coverlet 6.0.4 | 61 casos |

---

## 5. Estrutura da solução

```
AI.Manager/
  Rochas.AI.Manager.slnx
  src/
    Rochas.AI.Manager.Domain/            # entidades, base, contratos, helpers
    Rochas.AI.Manager.Application/       # serviços, tools handlers, helpers de resposta
    Rochas.AI.Manager.Infrastructure/    # AppDb, ReportService, PasswordHasher
    Rochas.AI.Manager.TrainingEngine/    # backend numérico + runner de processo + engine
    Rochas.AI.Manager.Api/               # REST, Swagger, proxy /v1, tools
    Rochas.AI.Manager.Web/               # MVC, views, seeders, /health
  tests/
    Rochas.AI.Manager.Tests/             # 12 arquivos, 61 casos
  docs/ai-manager/                       # arquitetura de camadas + guias
```

**Alvo incremental (não criar antes de precisar):**

```
src/Rochas.AI.Manager.Worker/            # hosted service de fila (Fase 2)
src/Rochas.AI.Manager.Reporting/         # registry + executor SQL (Fase 1, ou pasta em Application)
src/Rochas.AI.Manager.Inference/         # clientes Ollama/llama-server (Fase 5)
```

---

## 6. Camadas e regras de dependência

**Regra de ouro:** `Domain` não conhece nada; `Application` conhece `Domain`; `Infrastructure` conhece os dois; `Web`/`Api` conhecem `Application`, `Infrastructure` e `TrainingEngine`; `TrainingEngine` conhece `Application` (para relatar progresso), nunca `Web`/`Api`.

| Camada | Contém | Proibido |
|---|---|---|
| Domain | Entidades, base classes, enums, `ReportQuery/Result`, `EntityHashCode` | Dapper, SQLite, ASP.NET, TorchSharp |
| Application | Serviços (regras), tools handlers, `ReasoningSanitizer`, `ToolCallCoercer` | SQL direto, acesso a `HttpContext`, `Process` fora de `ProcessRunner` |
| Infrastructure | `AppDb` (schema), `ReportService`, hasher | Regra de negócio, decisão de fluxo |
| TrainingEngine | `TrainingEngine`, backend, `TrainingRunner` | Controllers, views, banco |
| Web / Api | Controllers finos, views, DI, seed, `/health` | Regra de negócio duplicada, SQL direto |

**Violações atuais (corrigir na Fase 0/1):**

- `Application` referencia `Microsoft.Data.Sqlite` e `Rochas.DapperRepository` (deveria ser só Infrastructure).
- `ReportService` (Infrastructure) monta dicionários por reflexão e pagina em memória — aceitável como ponte, não como alvo.
- `ApprovalsController` consulta repositórios diretamente em vez de serviços.
- Contratos `Domain/Common/IRepository` e `IUnitOfWork` não são resolvidos no DI (código morto).

---

## 7. Modelo de domínio

### 7.1 Base

- `BaseEntity`: `Id`, `ParentId`, `Code`, `Name`, `IsActive`, `CreatedAt` (UTC).
- `AuditableEntity`: + `OwnerLogin`, `UpdatedAt`, `UpdaterId`, `LastUpdate`.
- `MultiEntityReferrer`: + `EntityHashCode` (djb2 do nome completo do tipo).
- `WorkFlowBasedEntity`: + `StatusId`, `NextStatusId`, `RequesterId`, `ApproverId`, `RequestNote`, `ApprovalNote`, `Approved`, com `SetStatus/RequestApproval/Approve/Reject`.

### 7.2 Agregados e invariantes

| Agregado | Raiz | Invariantes |
|---|---|---|
| Catálogo de modelos | `ModelArtifact` | `status` ∈ {Draft, Approved, Deprecated}; aprovar só de Draft; descontinuar só de Approved |
| Dataset | `Dataset` | `format` obrigatório; `is_validated` só após validação; hash para integridade |
| Perfil de instrução | `InstructionProfile` | versionamento textual; aprovar só de Draft |
| Padrão de treino | `TrainingPattern` | `profile_id` e `model_artifact_id` opcionais, mas combinação deve existir quando ambos informados |
| Experimento | `Experiment` | Planned→Running→Completed→{Approved, Rejected}; rejeição exige motivo |
| Job de treino | `TrainingJob` | Queued→Running→{Completed, Failed, Cancelled}; `completed_at` só no fim; `best_accuracy` monotônica |
| Harness | `HarnessProfile` → `HarnessProfileVersion` → `HarnessAttachment` | versão imutável após aprovação; checksum obrigatório para aprovar; anexos por role |
| Política de tool | `ToolPolicy` | `allow`/`deny` disjuntos por role; `max_turns`/`stall_limit` > 0 |
| Binding | `ModelBinding` | provider+model_id únicos; contexto ≥ max_tokens |
| Run de harness | `HarnessRun` | `profile_version_id`, `model_binding_id` e `evidence_id` obrigatórios no alvo |
| Card de aprovação | `TrainingWorkflowCard` | transições conforme `workflow_steps`; aprovação/rejeição registram autor e nota |
| Auditoria | `WorkFlowTransition` | imutável; registra de/para, ação, autor e nota |

### 7.3 Grafo de FKs lógicas (todas sem constraint física)

```
users ──requester_id/approver_id/updater_id──> training_workflow_cards
experiments.base_artifact_id | tuned_artifact_id ──> model_artifacts
training_jobs.model_artifact_id ──> model_artifacts
training_jobs.dataset_id        ──> datasets
training_jobs.experiment_id     ──> experiments
training_patterns.profile_id    ──> instruction_profiles
training_patterns.model_artifact_id ──> model_artifacts
harness_profile_versions.profile_id ──> harness_profiles
harness_attachments.version_id ──> harness_profile_versions
harness_runs.profile_version_id ──> harness_profile_versions
harness_runs.model_binding_id   ──> model_bindings
harness_runs.evidence_id        ──> bridge_captures (convenção, sem FK)
training_workflow_cards.experiment_id ──> experiments
training_workflow_cards.status_id/next_status_id ──> entity_statuses
workflow_steps.status_id/next_status_id ──> entity_statuses
workflow_transitions.(entity_hash_code, entity_id) ──> qualquer entidade
workflow_transitions.approver_id ──> users
* parent_id ──> self
```

### 7.4 Máquinas de estado

- **Artefato/Perfil/Harness:** DRAFT → APPROVED → DEPRECATED.
- **Experimento:** PLANNED → RUNNING → COMPLETED → APPROVED | REJECTED.
- **Job:** QUEUED → RUNNING → COMPLETED | FAILED | CANCELLED (cancelamento a partir de QUEUED/RUNNING).
- **Card:** NOVO → ACEITE | REPROVADO (com passos intermediários declarados em `workflow_steps`).

---

## 8. Persistência e dados

### 8.1 Baseline

- SQLite em `AppContext.BaseDirectory/ai.manager.db`, WAL, `synchronous=NORMAL`, `busy_timeout=10s`.
- Schema criado por `AppDb.Init()` com `CREATE TABLE IF NOT EXISTS` (19 tabelas) e migração ad-hoc via `ALTER TABLE ... try/catch`.
- Acesso por `GenericRepository<T>` do pacote, registrado manualmente no DI (≈20 registros por host).
- Datas gravadas como `TEXT` (ISO-like, UTC), o que exige normalização em qualquer filtro temporal.

### 8.2 Política de schema (v2)

1. Introduzir `schema_version` com scripts SQL versionados (`001_init.sql`, `002_...`), substituindo `ALTER TABLE` em `try/catch`.
2. Manter FKs **lógicas** no SQLite (ADR-02) e cobrir integridade com relatórios de órfãos (R29 do catálogo).
3. Índices mínimos por eixo de relatório: `created_at` em `training_jobs`, `harness_runs`, `bridge_captures`, `workflow_transitions`; `(status)` em `training_jobs`/`experiments`; `(profile_version_id)` e `(model_binding_id)` em `harness_runs`; `(entity_hash_code, entity_id)` em `workflow_transitions`.
4. `AppDb.Init` continua idempotente; scripts rodam antes do seed.
5. Toda escrita valida existência das FKs referenciadas (serviço), registrando erro claro em vez de aceitar órfão.

### 8.3 Transações

Operações que tocam duas tabelas (ex.: `Move` do card + `WorkFlowTransition`; `RunAsync` + `HarnessRun`) devem usar transação explícita via conexão compartilhada. Baseline atual não garante atomicidade; a v2 exige helper `IUnitOfWork` real ou `SqliteTransaction` no serviço de infraestrutura.

---

## 9. Harness como produto

### 9.1 Modelo (implementado)

`HarnessProfile` → `HarnessProfileVersion` (`mode`, `checksum`, `approved_by/at`) → `HarnessAttachment` (`kind`, `role`, `content`, `char_count`), com `ToolPolicy` por role e `ModelBinding` fora do texto do prompt.

Modos: `ClientNative` (default; cliente traz o harness), `BridgeBypass`, `BridgeLegacy`, `Hybrid`.

### 9.2 Regra de ouro

**Harness descreve comportamento; infraestrutura fica na configuração.** Modelo, provider, porta, contexto, temperatura, caminho local e limites numéricos não entram no texto. `HarnessLinter` já bloqueia 6 padrões (porta, `NNk`, temperatura, caminho, nome de modelo/provider, número grande).

### 9.3 Ciclo de vida

1. Criar perfil (Draft).
2. Adicionar versão com `mode` e `checksum`.
3. Anexar núcleo/tails por role (contador de caracteres).
4. Lint obrigatório; hardcode bloqueia aprovação.
5. Aprovar versão (`approved_by`, `approved_at`).
6. Registrar runs contra a versão + binding.
7. Comparar versões (diff de texto e tamanho) e promover/reverter.

### 9.4 Pendências para fechar a seção 31 do v1

- Test Bench (enunciado canônico por faixa, evidência de arquivos/rc/tokens/s/duração).
- Diff visual entre versões no admin.
- Matriz de capacidade por modelo (tool calling, thinking, contexto, taxa de sucesso).
- Geração de `agents/*.md` e `opencode.json` a partir do perfil.
- `HarnessRun` com perfil/versão/captura reais (hoje o proxy grava `0`).

---

## 10. Bridge e proxy (contrato real)

### 10.1 O que existe

`Api/Controllers/ProxyController.cs`, rota `POST /v1/chat/completions`:

1. Exige `Authorization: Bearer <qualquer não vazio>` (não valida o token).
2. Escolhe o **primeiro** `ModelBinding` (ignora o `model` do request para o binding).
3. Guarda de contexto: `chars/4 > context_window * 0.95` → HTTP 413.
4. Resolve `Agent` por nome/modelo e injeta `temperature` no body.
5. Encaminha para `http://localhost:4125` (provider `llama`) ou `:4124` (demais), com Bearer fixo `guest@123`.
6. Streaming SSE repassado linha a linha; não-stream passa por `ReasoningSanitizer` e `ToolCallCoercer`.
7. Grava `HarnessRun` (result code, duração, tps estimado por `chars/4`) — com `ProfileVersionId=0` e `EvidenceId=0`.

`POST /api/tools/execute` (`[Authorize]`) despacha 9 handlers leaf no workspace (`BridgeToolExecutor`): shell, read, write, edit, grep, glob, fetch, git, subagent — com `WorkspaceGuard`, `ShellInspector` e `ProcessRunner` (timeout 300s default).

### 10.2 Invariantes da v2

- A bridge é **protocol-only**: não interpreta dataset, experimento, treino ou perfil.
- Token de entrada validado (JWT ou API key registrada); credencial do upstream vinda de configuração.
- Binding resolvido por `model` do request (nome de agente → role → binding), com fallback explícito e logado.
- `max_tokens` do cliente respeitado no repasse; ausência → valor do binding.
- `HarnessRun` sempre com `profile_version_id`, `model_binding_id`, `request_hash`; `evidence_id` quando a sonda estiver ligada.
- Toda resposta não-stream com `tool_calls` coercíveis é registrada; toda falha de upstream gera run com `result_code` real.
- `/v1/responses` entra como contrato de compatibilidade com testes de contrato (v1 §28.4).

---

## 11. Workflow, aprovação e auditoria

- `TrainingWorkflowCard` é o único agregado workflow-based hoje; `Move` grava transição com `approver_id = 0` (a corrigir: usar usuário autenticado).
- `workflow_steps` define o grafo por `entity_hash_code`; `WorkFlowBasedService<T>` implementa `GetEntitySteps/CanAdvance/Advance/GetTransitions`, mas **não está no DI** e é ignorado pelos serviços — os controllers usam regras `if` locais. A v2 exige um único caminho: serviço workflow resolvido no DI, com validação de passo antes de qualquer `Move`.
- `ApprovalsController` (Admin) agrega pendências de harness, experimentos, artefatos, perfis de instrução e cards; deve passar a consumir serviços, não repositórios.
- Auditoria: toda mudança de estado de agregado workflow-based gera `WorkFlowTransition` (de/para, ação, autor, nota, timestamp). Relatórios R22–R24 dependem disso.

---

## 12. Treinamento

### 12.1 Baseline atual (honesto)

- `POST /api/trainingjobs/{id}/run` executa `ITrainingEngine.RunAsync` **na requisição**.
- Básico: MLP `double[]` (entrada → 16 ocultos ReLU → saída), MSE, Adam, 10 épocas, checkpoint em memória não persistido; sem dataset, 256 amostras sintéticas; `vram_used_mb` sintético.
- Avançado: `python3 sft_train.py --dataset ... --method ...` via `ProcessRunner`.
- `TorchSharp` 0.107.0 referenciado e não usado; nenhum Safetensors, tokenizer, atenção, LoRA ou retomada.

### 12.2 Alvo e gates

| Gate | Critério | Consequência |
|---|---|---|
| G1 | Backend renomeado para refletir o que é (ex.: `MlpTrainingBackend`) e exposto como baseline | Elimina ambiguidade de capacidade |
| G2 | Prova TorchSharp: tensor na GPU, autograd, Adam, loss caindo em dataset sintético | Habilita `TorchSharpTrainingBackend` real |
| G3 | Loader Safetensors + tokenizer + forward textual de 1 modelo | Habilita fine-tuning textual |
| G4 | LoRA com pesos-base congelados, save/restore/resume | Habilita experimento real |
| G5 | Fila persistente + lease + cancelamento cooperativo | Habilita execução assíncrona confiável |

**Decisão pendente (ADR-07):** manter o caminho Python como *adaptador externo opcional* (desligado por default, declarado como backend externo) ou removê-lo. Enquanto não decidido, o rótulo de capacidade deve dizer "experimental/externo".

### 12.3 Fila e worker (alvo)

- Tabela `training_jobs` já serve de fila: `status`, `started_at`, `completed_at`, `error_message`.
- Worker como `BackgroundService` no host: `claim` atômico (`UPDATE ... WHERE id = (SELECT ...) AND status='Queued'`), lease por heartbeat, recuperação de `Running` órfão no boot.
- Um job por GPU: semáforo global + lock por artefato; cancelamento por `CancellationToken` propagado ao backend.
- Sem `TrainingWorker` separado na Fase 2; extração quando houver mais de um host.

---

## 13. Relatórios e analytics

### 13.1 Baseline (o que existe)

`ReportQuery`/`ReportResult`/`IReportService` + `ReportService` (13 entidades mapeadas) + `GET /api/reports/preview|export` + página `/Reports` com iframe e export CSV/XLSX/PDF/HTML. Filtro efetivo: **apenas `Status`**; paginação em memória; nenhum join.

### 13.2 Alvo (v2)

**Registry declarativo** (`ReportDefinition`), sem SQL montado a partir de entrada do usuário:

```csharp
public sealed record ReportDefinition(
    string Id,                 // "training-jobs-por-status"
    string Title,
    string RootTable,          // "training_jobs"
    IReadOnlyList<JoinSpec> Joins,
    IReadOnlyList<DimensionSpec> Dimensions,
    IReadOnlyList<MeasureSpec> Measures,
    IReadOnlyList<FilterSpec> Filters,
    DateAxis DefaultDateAxis,  // coluna + semântica
    IReadOnlyList<DateAxis> AllowedDateAxes,
    IReadOnlyList<string> ExportFormats);
```

**Semântica temporal (ADR-05)**

- Intervalo **half-open** em UTC: `[from 00:00:00, to+1 dia)`.
- Normalização: `yyyy-MM-dd HH:mm:ss` (SQLite TEXT).
- Eixos permitidos por relatório (nunca um eixo implícito): `created_at` (default), `started_at`, `completed_at`, `updated_at`, `approved_at`.
- "De" vazio = sem limite inferior; "Até" vazio = sem limite superior; ambos vazios = sem recorte (assumido, não silencioso).
- Relatório com dois eventos (ex.: duração) declara `started_at` e `completed_at` separadamente e calcula delta.

**Pipeline**

1. Valida `ReportDefinition` + filtros contra whitelist.
2. Monta SQL com parâmetros (nunca concatenação de valor).
3. `COUNT(*)` + página (`LIMIT/OFFSET`) no banco, não em memória.
4. Projeta `Dictionary<string, object?>` (compatível com `ReportResult`) ou DTO tipado.
5. Exporta via `Rochas.ExcelToJsonParser`/`Rochas.PDFGenerator`; PDF com MIME correto.

**Endpoints**

```
GET /api/reports/catalog                 → definições, dimensões e eixos temporais
GET /api/reports/{id}?from&to&...        → execução paginada
GET /api/reports/{id}/export?format=     → csv|xlsx|pdf
GET /api/reports/preview                 → compatibilidade (mapeia para definição default)
```

**Catálogo**

As 30 definições propostas (R1–R30), com facetas por FK, eixos temporais e métricas, estão em `REPORT-PENDINGS.md`. A v2 trata o catálogo como backlog priorizado, não como promessa de entrega simultânea.

**Regras de qualidade**

- Nenhum filtro é ignorado em silêncio: filtro não suportado pelo relatório retorna 400 com motivo.
- Toda dimensão de FK mostra código e nome (join explícito), nunca só o id.
- Relatórios de integridade (R29) e inventário (R30) são obrigatórios antes de qualquer migração de schema.

---

## 14. Segurança

| Item | Estado | Alvo |
|---|---|---|
| Chave JWT | hardcoded `ai-manager-jwt-secret-change-me` em Web e Api | configuração/env/secret store; rotação |
| Seed de usuários | `admin/admin`, `teste@e2e.local/Teste@123` sempre | somente Development; troca forçada; remover em Release |
| Proxy `/v1` | aceita qualquer Bearer; envia `guest@123` ao upstream | validação real + credencial configurável |
| Autorização | `[Authorize]` em reports/tools; Web Admin em Approvals; demais abertas | política por controller; papéis explícitos |
| CSRF | antiforgery nos POSTs Web | manter; documentar exceções |
| Superfície de tools | `WorkspaceGuard` + `ShellInspector` + timeout | allowlist por role; auditoria de comando |
| Segredos em log | não há mascaramento | nunca logar Authorization/body sensível |

---

## 15. Observabilidade e operação

- Log: Serilog console, nível Information com override `Microsoft=Warning`.
- `/health`: status, timestamp, `DIAGNOSTICS_ENABLED` (memória, uptime); alvo: `/health/ready` com checagem de SQLite e da bridge.
- Métricas mínimas: jobs por status, runs por result_code, duração p95, tps médio, capturas/dia.
- Diagnóstico de banco: caminho do arquivo, tamanho, WAL, última migração.
- Operação local: `dotnet run` em Web (`:5297` conforme `launchSettings`) e Api (Swagger em Development); DB no diretório do assembly.

---

## 16. Testes e qualidade

| Tipo | Baseline | Alvo |
|---|---|---|
| Unitário | AccuracyEvaluator, EntityHashCode, helpers, linter, workflow | Cobrir invariantes de agregado e máquinas de estado |
| Integração | SQLite em `SequentialCollection` | Repositórios, transações, seed idempotente |
| Contrato | — | Proxy `/v1/chat/completions` e `/v1/responses` (golden files) |
| Web/API | — | `WebApplicationFactory` para rotas e exportação |
| Relatórios | 4 casos | Golden CSV/XLSX por definição; De/Até inclusivo/exclusivo |
| Regressão de harness | — | Preview byte-idêntico ao request (hash) |

Regra: bug corrigido = teste que falha antes e passa depois. Teste que depende de GPU é marcado e roda em job separado.

---

## 17. ADRs (decisões registradas)

- **ADR-01 — Persistência:** SQLite + `Rochas.DapperRepository` como baseline. EF Core fora. Revisitar em PostgreSQL.
- **ADR-02 — FKs lógicas:** sem constraint física no SQLite; integridade por validação de escrita + relatório de órfãos.
- **ADR-03 — Harness versionado:** perfil/versão/anexos imutáveis após aprovação; bridge protocol-only; infraestrutura fora do prompt.
- **ADR-04 — Relatórios declarativos:** registry em código + SQL whitelisted; proibido SQL dinâmico a partir de query string.
- **ADR-05 — Intervalo temporal:** half-open em UTC, "Até" inclusivo no dia.
- **ADR-06 — Motor numérico:** MLP é baseline rotulado; TorchSharp só após prova (G2).
- **ADR-07 — Python:** adaptador externo opcional e desligado, ou remoção. Decisão do dono do produto.
- **ADR-08 — Chat:** portar `/Chat/{Stream,Sessions,Config,Approve,Calibrate,Stop,ClientLog}` para o AI.Manager ou remover a view; proibido segundo chat.
- **ADR-09 — Segurança:** segredos só em configuração; seed apenas em Development.
- **ADR-10 — Worker:** `BackgroundService` no host na Fase 2; projeto dedicado só com múltiplos hosts.
- **ADR-11 — Contratos de domínio:** `IRepository`/`IUnitOfWork` implementados ou removidos (sem código morto).
- **ADR-12 — Schema:** scripts versionados + `schema_version`; proibido `ALTER TABLE` em `try/catch`.

---

## 18. Riscos

| Risco | Prob. | Impacto | Sinal de alerta | Resposta |
|---|---|---|---|---|
| Declarar treino funcional sem prova | Alta | Alto | Job "Completed" sem loss/gradiente verificável | Gate G1/G2; rótulo honesto |
| Relatório entregar número errado | Alta | Alto | Filtro De/Até ausente; total ≠ filtrado | ADR-04/05 + testes golden |
| Chat quebrado em produção | Média | Médio | 404 em `/Chat/Stream` | ADR-08 |
| Vazamento de credencial | Média | Alto | Chave fixa em repo | ADR-09 |
| Perda de evidência de experimento | Média | Alto | Run sem perfil/captura | §9.4 |
| Crescimento do banco sem índice | Média | Médio | Consultas lentas em `harness_runs` | §8.2 |
| Divergência harness nativo × custom | Média | Médio | Resultado muda sem mudança de versão | Diff + Test Bench |

---

## 19. Roadmap v2

**Fase 0 — Verdade e segurança (1 sprint)**
Renomear backend para o que ele é; decidir Python (ADR-07); chave JWT e seed configuráveis; menu com Reports/Approvals; corrigir MIME do PDF; corrigir `approver_id` nas transições.
*Aceite:* build verde; nenhum segredo no código; página de relatórios acessível pelo menu.

**Fase 1 — Relatórios confiáveis (1–2 sprints)**
`ReportDefinition`, registry, filtros De/Até, facetas de FK, paginação em SQL, catálogo R1–R13 + R29/R30; export com MIME correto; testes golden.
*Aceite:* `from/to` altera o total; nenhum filtro ignorado; R29 acusa zero órfãos no seed.

**Fase 2 — Execução assíncrona (1–2 sprints)**
Fila em `training_jobs`, claim atômico, hosted service, cancelamento cooperativo, recuperação no boot, endpoint de status.
*Aceite:* `POST /run` retorna 202 imediato; job sobrevive a restart; cancelar interrompe em < 5s.

**Fase 3 — Evidência de harness (1–2 sprints)**
`HarnessRun` com perfil/versão/captura; Test Bench; diff de versões; matriz de capacidade.
*Aceite:* todo run rastreia perfil→versão→request→resultado; preview byte-idêntico.

**Fase 4 — Motor numérico (2–4 sprints)**
G2 (TorchSharp na GPU), loader Safetensors, forward textual de um modelo, LoRA, resume.
*Aceite:* loss cai em dataset sintético na GPU com gradientes finitos; adaptador salvo/restaurado.

**Fase 5 — Operação e catálogo (1–2 sprints)**
Dashboard operacional, estatísticas agregadas, `/health/ready`, índices, scripts de migração, integridade referencial contínua.
*Aceite:* painel mostra jobs/runs/erros com origem e timestamp; migração idempotente.

**Fase 6 — Horizonte**
`/v1/responses` com testes de contrato, clientes de inferência (Ollama/llama-server), multimídia/áudio, app mobile, PostgreSQL.

---

## 20. Critérios de aceitação da v2

1. Nenhuma capacidade anunciada sem teste e evidência (v1 §28.14).
2. Todo relatório respeita `from/to` e retorna 400 para filtro não suportado.
3. Job de treino nunca roda dentro da requisição HTTP.
4. Todo `HarnessRun` tem perfil, versão, binding e hash de request.
5. Nenhum segredo, credencial ou caminho local no código ou no prompt.
6. Bridge permanece protocol-only, com testes de contrato verdes.
7. Chat tem endpoints no AI.Manager ou deixa de existir como view.
8. Schema evolui por script versionado, com `schema_version`.
9. Integridade referencial verificável por relatório (R29) a qualquer momento.
10. Build e suíte verdes no CI antes de qualquer release.

---

## 21. Anexos

### 21.1 Mapa de arquivos-chave

| Área | Arquivo |
|---|---|
| Schema | `src/Rochas.AI.Manager.Infrastructure/Data/AppDb.cs` |
| Repositórios/DI | `src/*/Program.cs` |
| Contratos de relatório | `src/Rochas.AI.Manager.Domain/Interfaces/IReportService.cs` |
| Execução de relatório | `src/Rochas.AI.Manager.Infrastructure/Services/ReportService.cs` |
| Proxy/bridge | `src/Rochas.AI.Manager.Api/Controllers/ProxyController.cs` |
| Tools | `src/Rochas.AI.Manager.Application/Tools/*` |
| Helpers de resposta | `src/Rochas.AI.Manager.Application/Helpers/{ReasoningSanitizer,ToolCallCoercer}.cs` |
| Harness | `src/Rochas.AI.Manager.Domain/Harness/*` + `Application/Services/Harness*` |
| Workflow | `src/Rochas.AI.Manager.Domain/Entities/Management/*` + `Application/Services/{TrainingWorkflowService,WorkFlowBasedService}.cs` |
| Treino | `src/Rochas.AI.Manager.TrainingEngine/*` |
| Seed | `src/Rochas.AI.Manager.Web/Data/*` |
| UI | `src/Rochas.AI.Manager.Web/Views/**` + `Controllers/**` |

### 21.2 Glossário

- **Harness:** conjunto versionado de instruções, tails e políticas que molda o comportamento do agente.
- **Binding:** combinação provider + modelo + limites (contexto, max_tokens, thinking, temperatura).
- **Run:** execução observada de um harness contra um binding, com resultado e métricas.
- **Captura:** request/response completos coletados pela sonda.
- **Card:** unidade de aprovação no fluxo de treino.
- **Transição:** registro imutável de mudança de estado de entidade workflow-based.
- **Faceta:** dimensão de agrupamento/filtro derivada de coluna ou FK.
- **Eixo temporal:** coluna de data usada no recorte De/Até de um relatório.

### 21.3 Change log

| Versão | Data | Mudança |
|---|---|---|
| v1.2 | 2026-10-19 | Guideline original + seção 31 (harness) |
| v1.3 | 2026-10-20 | Seção 32 (as-built) e nota de revisão |
| v2.0 | 2026-10-20 | Consolidação do as-built, subsistema de relatórios, ADRs, roadmap e critérios de aceitação |
