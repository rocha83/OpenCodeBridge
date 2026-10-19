# Rochas.AI.Manager — Guia do Iniciante

## 1. Visão geral
O Rochas.AI.Manager é uma plataforma para **gerenciar, treinar e orquestrar modelos locais** (Gemma 4, Llama, Qwen, DeepSeek), reutilizando a base do Gemma Forge e a bridge OpenCode. O objetivo é transformar tentativas de harness em **perfis versionados, testáveis e observáveis**.

## 2. Conceitos básicos
- **Model Artifact**: arquivo/peso do modelo (Draft → Approved → Deprecated).
- **Dataset**: dados de treino (jsonl/csv/parquet), validado antes do uso.
- **Instruction Profile**: instruções/perfis de comportamento versionados.
- **Training Pattern**: padrão de treino vinculando perfil + modelo.
- **Experiment**: hipótese → execução → métricas → aprovação/rejeição.
- **Harness Profile**: perfil de comportamento (nativo/bypass/legacy/híbrido) versionado, com tails por role.
- **Tool Policy**: allow/deny de tools por role.
- **Model Binding**: provider, modelo, max_tokens, contexto, thinking, temperatura.

## 3. Modos de harness
- **Cliente nativo (recomendado)**: o cliente (OpenCode) traz o harness; a bridge apenas transporta.
- **Bridge bypass/legacy**: a plataforma sintetiza o harness quando o cliente não trouxer.
- **Híbrido**: cliente traz harness + plataforma anexa tails curtas e explícitas.

Regra de ouro: **harness descreve comportamento; infraestrutura fica na configuração** (modelo, porta, contexto, temperatura, caminho nunca entram no texto).

## 4. Passos para eficiência na pipe de desenvolvimento
1. **Formalize o perfil**: crie `HarnessProfile` v1 a partir do núcleo + tails, sem hardcodes.
2. **Lint bloqueante**: rode o linter para bloquear modelo, porta, contexto, temperatura, caminho e limites numéricos no prompt.
3. **Preview byte-idêntico**: confira o system prompt final exatamente como será enviado (mesmo hash).
4. **Test Bench**: execute um enunciado canônico por faixa, colete evidência (arquivos, rc, tokens/s, duração).
5. **Aprove e publique**: versão imutável, checksum e aprovação.
6. **Registre runs**: cada execução grava perfil, modelo, hash e resultado para comparação de regressões.

## 5. Chat para assuntos gerais
- Use o modo **cliente nativo** para máxima taxa de sucesso de tools.
- Mantenha `max_tokens` explícito por binding.
- Ative anti-repeat e thinking conforme o role.
- Para modelos sem tool calling nativo, use coerção de texto (legacy mode).

## 6. Fluxo de treinamento (alto nível)
1. Cadastrar `ModelArtifact` (Draft).
2. Cadastrar/validar `Dataset`.
3. Criar `InstructionProfile` e `TrainingPattern`.
4. Criar `Experiment` (Planned → Running → Completed → Approved/Rejected).
5. Criar `TrainingJob` (Queued → Running → Completed/Failed/Cancelled).
6. Reportar progresso (loss, VRAM) e concluir.
7. Aprovar artefato treinado e registrar métricas.

## 7. Boas práticas
- Nunca hardcode de infraestrutura no harness.
- Sempre termine com build de integridade verde.
- Evidência real (logs/testes) em cada run.
- Segmentação em micro-enunciados para modelos menores.
- Versionamento imutável e aprovação explícita.

## 8. Referências
- `Rochas.AI.Manager-Arquitetura.md` (seção 31 — Harness).
- `HISTORY.md` / `LAB-SETUP.md` (OpenCodeBridge).
- Swagger da API: `/swagger`.
