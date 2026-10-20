# REPORT-PENDINGS — Relatórios do Rochas.AI.Manager

**Data:** 2026-10-20
**Status:** backlog priorizado (nada aqui está implementado além do que consta em "Estado atual").
**Contexto:** complementa `Rochas.AI.Manager-Arquitetura v2.md` (§13) e a seção 32 do documento v1.
> **Atualização 2026-10-20:** a fundação foi implementada (De/Até inclusivo, bandas, família de modelo, status por código, 400 para filtro inválido, MIME do PDF, menu e testes). R1–R30 continuam pendentes como backlog.

**Escopo:** facetas de relatório derivadas do modelo de domínio (FKs lógicas), recorte temporal De/Até, catálogo R1–R30, contrato técnico e plano de implementação.

---

## 1. Estado atual (verificado em código)

| Item | Existe | Evidência |
|---|---|---|
| Contrato | `ReportQuery` (Entity, Status, DateFrom, DateTo, AccuracyBand/Min/Max, ModelFamily, Mode, UserId, AgentId, Page, PageSize) e `ReportResult` | `Domain/Interfaces/IReportService.cs` |
| Implementação | Mapeia 13 entidades para tabela/tipo, consulta via repositório genérico, monta dicionário por coluna | `Infrastructure/Services/ReportService.cs` |
| Filtro de status | Sim (compara `Status` textual ou `StatusId`) | idem |
| De/Até | **Não** — `DateFrom`/`DateTo` são ignorados | idem |
| Acurácia/família/modo/usuário/agente | **Não** — campos existem, não são aplicados | idem |
| Join/agregação | **Não** | idem |
| Paginação | Em memória (tabela inteira carregada) | idem |
| Exportação | CSV `;`, XLSX, PDF, HTML | `ReportService.ExportAsync` |
| API | `GET /api/reports/preview`, `GET /api/reports/export` | `Api/Controllers/ReportsController.cs` |
| Web | `/Reports` com iframe + botões de export | `Web/Controllers/ReportsController.cs`, `Views/Reports/Index.cshtml` |
| Menu | **Fora do menu lateral** | `Views/Shared/_Layout.cshtml` |
| MIME do PDF | Rotulado como `text/html` | controllers de export |
| Catálogo de definições | **Não existe** | — |

**Consequência:** hoje é possível filtrar por status e exportar; qualquer recorte por período, artefato, dataset, experimento, harness ou usuário retorna o conjunto completo. Os campos `AccuracyBand`, `ModelFamily`, `Mode`, `UserId` e `AgentId` dão falsa impressão de suporte.

---

## 2. Regras transversais (valem para todo relatório)

### 2.1 Semântica temporal

- Intervalo **half-open** em UTC: `[De 00:00:00, Até + 1 dia)`.
- Datas em SQLite são `TEXT`; normalizar para `yyyy-MM-dd HH:mm:ss` antes de comparar.
- "De" vazio = sem limite inferior; "Até" vazio = sem limite superior; ambos vazios = sem filtro (registrado na resposta, não silencioso).
- O eixo temporal é **explícito por relatório**; nunca inferir.
- Relatórios com dois eventos (duração, SLA) declaram os dois eixos e calculam delta no banco.
- Filtro não suportado por uma definição retorna HTTP 400 com o motivo — nunca ignorar.

### 2.2 Eixos temporais disponíveis por tabela

| Tabela | Colunas de data | Uso típico |
|---|---|---|
| `training_jobs` | `created_at`, `started_at`, `completed_at` | volume, fila, duração, falha |
| `experiments` | `created_at` | funil, hipótese, rejeição |
| `datasets` | `created_at` | entrada de dados, validação |
| `model_artifacts` | `created_at` | catálogo, aprovação |
| `instruction_profiles` | `created_at` | versionamento |
| `training_patterns` | `created_at` | combinações perfil×artefato |
| `harness_profiles` | `created_at` | catálogo de harness |
| `harness_profile_versions` | `created_at`, `approved_at` | lead time de aprovação |
| `harness_attachments` | `created_at` | composição de prompt |
| `tool_policies` | `created_at` | configuração por role |
| `model_bindings` | `created_at` | capacidade por provider/modelo |
| `harness_runs` | `created_at` | execução, latência, erro |
| `bridge_captures` | `created_at` | tráfego, duplicidade, perda |
| `training_workflow_cards` | `created_at`, `updated_at` | backlog, SLA |
| `workflow_transitions` | `created_at` | auditoria, tempo em status |
| `entity_statuses` / `workflow_steps` | `created_at` | dicionário (sem recorte útil) |
| `users` / `agents` | `created_at` | cadastro |

### 2.3 Facetas por origem

- **FK direta:** agrupar por id e exibir nome/código via join (nunca só o id).
- **Coluna categórica:** status, mode, thinking, format, kind, role, action, result_code, is_validated, is_admin, advanced_mode, approved.
- **Banda numérica:** acurácia (`<0.5`, `0.5–0.8`, `≥0.8` — alinhado ao enum `AccuracyBand`), loss, VRAM, tps, duração, tool_calls, char_count, context_window.
- **Derivada:** duração (`completed_at − started_at`), SLA (`updated_at − created_at`), lead time (`approved_at − created_at`), gap de acurácia (`accuracy − target_accuracy`), taxa (sucesso/total).
- **Integridade:** LEFT JOIN + `IS NULL` (órfãos), duplicidade por hash, presença/ausência de payload.

### 2.4 Acurácia: três fontes, uma regra

`training_jobs.best_accuracy`, `training_workflow_cards.accuracy` e `metrics_json` do experimento. Cada relatório declara a fonte; quando comparar card × job, usar o card do experimento e o melhor job do mesmo experimento, com a regra explícita no título do relatório.

---

## 3. Catálogo R1–R30

Legenda de prioridade: **P0** = necessário para operar; **P1** = decisão técnica; **P2** = análise avançada.
Legenda de estado: `NI` não implementado, `P` parcial (só status).

### 3.1 Treino e experimentos

| ID | Relatório | Base | Facetas | Eixo De/Até | Métricas | Prior. | Estado |
|---|---|---|---|---|---|---|---|
| R1 | Jobs por período | `training_jobs` | status, training_method, advanced_mode, artefato, dataset, experimento, profile_name | `created_at` (alt.: `started_at`, `completed_at`) | contagem, duração média, throughput médio | P0 | P |
| R2 | Funil de experimentos | `experiments` | status, artefato base, artefato tuned, motivo de rejeição | `created_at` | contagem por status, conversão, taxa de rejeição | P0 | P |
| R3 | Base × Tuned | `experiments` + `model_artifacts` (2 joins) | família base, família tuned, status | `created_at` | Δ best_accuracy do melhor job | P1 | NI |
| R4 | Qualidade do treino | `training_jobs` | banda de best_accuracy, banda de last_loss, epochs, método | `started_at` → `completed_at` | contagem, acurácia média, loss final | P0 | NI |
| R5 | Falhas de treino | `training_jobs` (status Failed/Cancelled) | dataset, artefato, experimento, mensagem de erro | `completed_at` | contagem, % do total, tempo até falha | P0 | P |
| R6 | Datasets em uso | `datasets` LEFT JOIN `training_jobs` | format, version, is_validated | `created_at` | jobs por dataset, % jobs com dataset não validado, datasets sem uso | P0 | NI |
| R7 | Uso de artefatos | `model_artifacts` LEFT JOIN jobs/experiments/patterns | family+variant, format, status, is_trainable | `created_at` | uso por tipo, artefatos órfãos | P1 | NI |
| R8 | Matriz perfil × artefato | `training_patterns` | perfil, artefato | `created_at` | combinações, contagem | P2 | NI |

### 3.2 Harness e execução

| ID | Relatório | Base | Facetas | Eixo De/Até | Métricas | Prior. | Estado |
|---|---|---|---|---|---|---|---|
| R9 | Execuções por período | `harness_runs` | result_code (2xx/4xx/5xx), versão, binding | `created_at` | volume, latência p50/p95, tps médio | P0 | NI |
| R10 | Confiabilidade por versão | `harness_runs` + `harness_profile_versions` + `harness_profiles` | mode, perfil, versão | `created_at` | taxa de erro, sucesso por versão | P0 | NI |
| R11 | Desempenho por binding | `harness_runs` + `model_bindings` | provider, model_id, thinking, faixa de contexto | `created_at` | tps, latência, erros | P0 | NI |
| R12 | Esforço por run | `harness_runs` | faixas de tool_calls, files_created, duration_ms | `created_at` | distribuição, média, máximos | P1 | NI |
| R13 | Capturas do bridge | `bridge_captures` | mode, request_hash repetido, sem response_json | `created_at` | volume, duplicidade, perda | P0 | NI |
| R14 | Rastreio captura → run | `harness_runs` JOIN `bridge_captures` (`evidence_id`) | versão, binding | `created_at` | runs sem captura, capturas sem run | P1 | NI |
| R15 | Composição do harness | `harness_attachments` + versão/perfil | kind, role | `created_at` | soma de char_count por role (orçamento de prompt), anexos por versão | P1 | NI |
| R16 | Aprovação de versões | `harness_profile_versions` | approved_by, mode, perfil | `created_at` e `approved_at` | lead time, versões pendentes | P1 | NI |
| R17 | Políticas de tool por role | `tool_policies` | role, require_call, faixas de max_turns/stall_limit | `created_at` | roles sem política, deny contendo tool | P1 | NI |

### 3.3 Aprovação, workflow e auditoria

| ID | Relatório | Base | Facetas | Eixo De/Até | Métricas | Prior. | Estado |
|---|---|---|---|---|---|---|---|
| R18 | Kanban de cards | `training_workflow_cards` JOIN `entity_statuses` | status, experimento, owner | `created_at` | backlog, WIP por status | P0 | NI |
| R19 | SLA de aprovação | cards | approver, approved | `created_at` → `updated_at` | tempo médio até decisão, pendentes > N dias | P1 | NI |
| R20 | Aprovações por aprovador | cards JOIN `users` | approver, approved, motivo | `updated_at` | aprovados/rejeitados por usuário | P1 | NI |
| R21 | Gap acurácia × meta | cards | experimento, faixa de gap | `created_at` | % aprovados abaixo da meta, gap médio | P1 | NI |
| R22 | Trilha de auditoria | `workflow_transitions` | entity_hash_code (tipo), entity_id, action, approver | `created_at` | histórico completo por entidade | P0 | NI |
| R23 | Tempo em cada status | `workflow_transitions` | tipo, status_from/to | `created_at` (delta entre transições) | tempo médio por status, gargalos | P1 | NI |
| R24 | Configuração de workflow | `workflow_steps` JOIN `entity_statuses` | entity_hash_code, sort_order | — (dicionário) | matriz de transições permitidas | P2 | NI |

### 3.4 Cadastros

| ID | Relatório | Base | Facetas | Eixo De/Até | Métricas | Prior. | Estado |
|---|---|---|---|---|---|---|---|
| R25 | Usuários | `users` | is_admin, is_active | `created_at` | cards abertos por usuário, aprovações | P2 | P |
| R26 | Agentes | `agents` | mode, model, thinking, faixa de temperature | `created_at` | agentes por bridge, agentes sem tool de execução | P1 | NI |

### 3.5 Transversais

| ID | Relatório | Base | Facetas | Eixo De/Até | Métricas | Prior. | Estado |
|---|---|---|---|---|---|---|---|
| R27 | Funil ponta a ponta | artefato → experimento → jobs → card | artefato, família, status | `created_at` em cada nível | conversão por etapa, tempo total | P2 | NI |
| R28 | Produtividade diária | jobs + runs + capturas | dia/semana | `created_at` agregado | volume por dia, comparação entre fontes | P2 | NI |
| R29 | Integridade referencial | todas as FKs lógicas | tipo de órfão | — | contagem de órfãos por relação | P0 | NI |
| R30 | Inventário inativo | todas as tabelas | tabela, is_active | `created_at` | registros inativos por tabela | P1 | NI |

---

## 4. Filtros por relatório (contrato de entrada)

Comum a todos: `from`, `to`, `dateAxis`, `page`, `pageSize`, `format`.

| Grupo | Filtros |
|---|---|
| Jobs (R1, R4, R5) | `status`, `method`, `artifactId`, `datasetId`, `experimentId`, `profileName`, `advancedOnly`, `accuracyMin/Max` |
| Experimentos (R2, R3) | `status`, `baseArtifactId`, `tunedArtifactId`, `family` |
| Dados (R6, R7, R8) | `format`, `validatedOnly`, `family`, `trainableOnly`, `profileId`, `artifactId` |
| Harness (R9–R12, R15–R17) | `profileId`, `profileVersionId`, `bindingId`, `provider`, `mode`, `resultClass`, `role`, `kind` |
| Capturas (R13, R14) | `mode`, `hasResponse`, `duplicateOnly`, `linkedToRun` |
| Workflow (R18–R23) | `statusId`, `experimentId`, `approverId`, `requesterId`, `ownerLogin`, `entityType`, `entityId`, `action` |
| Cadastros (R25, R26) | `isAdmin`, `isActive`, `agentMode`, `model`, `thinking` |

Regra: filtro aplicável é validado contra a definição; fora dela → 400.

---

## 5. Contrato técnico proposto

### 5.1 Registry

```csharp
public sealed record ReportDefinition(
    string Id,
    string Title,
    string RootTable,
    IReadOnlyList<JoinSpec> Joins,
    IReadOnlyList<DimensionSpec> Dimensions,
    IReadOnlyList<MeasureSpec> Measures,
    IReadOnlyList<FilterSpec> Filters,
    string DefaultDateAxis,
    IReadOnlyList<string> AllowedDateAxes,
    IReadOnlyList<string> ExportFormats);
```

- Registro em código (`ReportCatalog.All`), sem tabela de metadados na primeira versão.
- Toda coluna referenciada precisa existir na allowlist; nada vem da query string sem validação.
- SQL montado por composição de fragmentos constantes + parâmetros.

### 5.2 Endpoints

```
GET  /api/reports/catalog
GET  /api/reports/{id}?from=2026-01-01&to=2026-10-20&dateAxis=created_at&page=1&pageSize=50
GET  /api/reports/{id}/export?format=csv|xlsx|pdf
GET  /api/reports/preview            (compatibilidade: mapeia para definição default)
GET  /Reports                        (UI)
```

### 5.3 Saída

- `items`: dicionário coluna→valor (compatível com a UI atual) ou DTO tipado por definição.
- `total`: contagem no banco com os mesmos filtros.
- `page`, `pageSize`, `dateAxis`, `appliedFilters`: devolvidos para auditoria da consulta.
- Export: CSV `;`, XLSX, PDF (MIME `application/pdf`) e HTML como fallback.

### 5.4 Performance

- `COUNT(*)` + `LIMIT/OFFSET` no banco.
- Índices: `created_at` nas tabelas de evento; `(profile_version_id)`, `(model_binding_id)` em runs; `(entity_hash_code, entity_id)` em transições; `(status)` em jobs/experiments.
- Consultas com mais de 2 joins devem ter plano documentado e limite de período default (ex.: 90 dias).

---

## 6. Pendências e riscos específicos de relatório

| # | Pendência | Impacto | Ação |
|---|---|---|---|
| RP-01 | `DateFrom`/`DateTo` ignorados | número errado sem aviso | implementar §2.1 |
| RP-02 | `AccuracyBand/ModelFamily/Mode/UserId/AgentId` ignorados | falsa impressão de filtro | implementar ou remover do contrato |
| RP-03 | `AgentId` sem FK no modelo | faceta impossível | adicionar `agent_id` em runs/capturas ou remover o campo |
| RP-04 | `evidence_id` sem FK | rastreio quebrado | validar na escrita + R29 |
| RP-05 | `approved_by` textual | faceta por usuário impossível | normalizar para id ou mapear por login |
| RP-06 | Datas TEXT sem normalização | recorte errado em borda | normalizar ISO + teste de virada de dia |
| RP-07 | PDF com MIME de HTML | arquivo não abre como PDF | corrigir controllers |
| RP-08 | Página fora do menu | descoberta zero | adicionar ao layout |
| RP-09 | Carga total em memória | latência/crash com volume | SQL paginado |
| RP-10 | Duas fontes de acurácia | comparação inconsistente | regra explícita por definição |
| RP-11 | `Status` textual × `status_id` | filtro ambíguo | tradução via `entity_statuses` por definição |
| RP-12 | Sem catálogo de definições | cada tela vira código novo | registry (§5.1) |

---

## 7. Plano de implementação

### Fase 1 — Fundação (P0)

1. `ReportDefinition` + `ReportCatalog` com R1, R2, R4, R5, R9, R13, R18, R22, R29, R30.
2. Semântica De/Até half-open + normalização.
3. Paginação/contagem em SQL; 400 para filtro inválido.
4. Corrigir MIME do PDF; incluir `/Reports` no menu.
5. Testes: golden CSV por definição; De/Até inclusivo/exclusivo; R29 com base semeada.

*Aceite:* alterar `from/to` altera `total`; filtro desconhecido retorna 400; export PDF abre; R29 = 0 órfãos no seed.

### Fase 2 — Facetas de negócio (P0/P1)

R6, R7, R10, R11, R15, R16, R17, R19, R20, R21, R26, R30.
*Aceite:* toda faceta de FK exibe nome; toda banda tem limite documentado.

### Fase 3 — Análise (P2)

R3, R8, R12, R14, R23, R24, R25, R27, R28.
*Aceite:* cada relatório tem dicionário de métricas e exemplo de leitura.

### Fase 4 — Operação

Agendamento (export periódico), cache de definições, comparação entre períodos, alertas (ex.: taxa de erro > X).

---

## 8. Decisões abertas

1. **`AgentId`**: criar vínculo (`agent_id` em `harness_runs`/`bridge_captures`) ou remover do contrato?
2. **Acurácia**: qual fonte é canônica para "modelo aprovado" — card ou job?
3. **Status de cards**: expor `status_id` cru ou sempre traduzido por `entity_statuses.code`?
4. **Período default**: sem filtro retorna tudo ou aplica janela de 90 dias com aviso?
5. **Export assíncrono**: necessário acima de quantas linhas?
6. **`approved_by`**: normalizar para `users.id` (migração) ou manter login e mapear?

---

## 9. Definição de pronto (para qualquer relatório)

- Definição registrada no catálogo, com título, base, facetas, eixos e métricas.
- `from/to` aplicado no banco, com teste de borda.
- Filtros inválidos retornam 400; nenhum filtro é ignorado em silêncio.
- Faceta de FK exibe código **e** nome.
- Export CSV/XLSX/PDF verificado (MIME e conteúdo).
- Consulta com índice apropriado e `EXPLAIN QUERY PLAN` registrado.
- Documentação atualizada neste arquivo (estado e data).
