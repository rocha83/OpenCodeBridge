# Rochas.AI.Manager — Guia de Tuning (Equipe Técnica)

## 1. Objetivo
Padronizar o tuning de modelos locais (Gemma 4, Llama, Qwen, DeepSeek) usando os módulos do Rochas.AI.Manager, com foco em:
- aumento de eficiência da pipe de desenvolvimento;
- estabilidade do chat geral;
- reprodutibilidade de resultados.

## 2. Módulos envolvidos
- **ModelArtifact**: registro e aprovação de pesos (Draft → Approved).
- **Dataset**: ingestão e validação de dados (jsonl/csv/parquet).
- **InstructionProfile**: instruções versionadas por caso de uso.
- **TrainingPattern**: combinação perfil + modelo + parâmetros.
- **Experiment**: hipótese, execução, métricas, aprovação.
- **TrainingJob**: ciclo de treino (Queued → Running → Completed/Failed).
- **HarnessProfile**: comportamento do agente (nativo/bypass/legacy/híbrido).
- **ToolPolicy**: allow/deny por role.
- **ModelBinding**: provider, modelo, max_tokens, contexto, thinking, temperatura.
- **HarnessRun / BridgeCapture**: evidência e reprodutibilidade.

## 3. Fluxo de tuning (passo a passo)
1. **Preparar artefato base**
   - Cadastrar `ModelArtifact` (Draft).
   - Validar formato e hash.
2. **Preparar dataset**
   - Cadastrar `Dataset` (jsonl/csv/parquet).
   - Marcar `IsValidated = true` após checagem.
3. **Definir perfil de instrução**
   - Criar `InstructionProfile` (Draft).
   - Aprovar quando estável.
4. **Criar padrão de treino**
   - `TrainingPattern` vinculando perfil + artefato + parâmetros.
5. **Criar experimento**
   - `Experiment` (Planned) com hipótese e artefatos base/tuned.
6. **Executar job**
   - `TrainingJob` (Queued) com configuração JSON.
   - Start → reportar progresso (loss, VRAM) → Complete/Fail.
7. **Avaliar e aprovar**
   - Atualizar métricas do experimento.
   - Aprovar/rejeitar.
8. **Publicar harness**
   - Criar `HarnessProfile` v1 (sem hardcodes).
   - Lint bloqueante.
   - Preview byte-idêntico.
   - Test Bench por faixa.
   - Aprovar e publicar.

## 4. Parâmetros de tuning
- **max_tokens**: explícito por binding (evita respostas gigantes).
- **context_window**: compatível com hardware.
- **temperature**: Plan/Build/Orch 0.5; MoE 0.8; Scripter/Exec 0.3.
- **thinking**: on para Plan/Orch; off para Build/Scripter/Exec.
- **tool_policy**: allow/deny por role, RequireCall quando necessário.
- **harness mode**: Cliente nativo (default), bypass/legacy/híbrido.

## 5. Harness e eficiência da pipe
- Sempre começar pelo **harness nativo do cliente**.
- Sintetizar harness próprio apenas quando o cliente não trouxer.
- Linter bloqueia: modelo, porta, contexto, temperatura, caminho, limites.
- Preview deve ser **byte-idêntico** ao request real.
- Test Bench reproduz a partir da versão do perfil + ModelBinding.
- Registrar `HarnessRun` e `BridgeCapture` para comparar regressões.

## 6. Chat geral (eficiência e estabilidade)
- Usar modo cliente nativo para maior taxa de sucesso de tools.
- Anti-repeat ativo (janela 512).
- max_tokens por binding (ex.: 8192).
- Coerção de texto para modelos sem tool calling nativo.
- Separar roles: Plan (elucida), Build (executa), Orch (agrega).

## 7. Evidência e reprodutibilidade
Cada run deve registrar:
- versão do perfil + checksum do prompt;
- hash do request (system + tools + mensagens);
- modelo, max_tokens, temperatura, thinking;
- tool calls, arquivos criados, rc, duração, tokens/s;
- captura completa (sonda) quando habilitada.

## 8. Checklist de tuning
- [ ] Artefato base aprovado.
- [ ] Dataset validado.
- [ ] Perfil de instrução aprovado.
- [ ] Experimento com métricas.
- [ ] Job concluído sem erro.
- [ ] Harness profile publicado (sem hardcodes).
- [ ] Test Bench verde por faixa.
- [ ] Runs registrados com evidência.

## 9. Referências
- `Rochas.AI.Manager-Arquitetura.md` (seções 28, 29, 30, 31).
- `HISTORY.md` / `LAB-SETUP.md` (OpenCodeBridge).
- Swagger da API: `/swagger`.

## 10. Testes E2E de Belts (exemplo)
Para validar a eficiência do harness e do modelo em tarefas reais:

### Faixas (complexidade crescente)
- Azul → Verde → Roxa → Marrom → Preta.

### Modos
- **Build**: 12B implementa diretamente os artefatos (sem scripts sh).
- **Orch**: 12B segmenta → Scripter compõe scripts → Exec (E2B) executa.

### Critério de aceite
- Solução implementada em arquivos reais (>=3 arquivos).
- Quando aplicável, compilando (`dotnet build` verde).
- Tools usadas de forma suficiente (read/write/edit/glob/grep/shell/git).

### Execução
```bash
# standalone (opencode real)
opencode run --standalone --auto -m openai/gemma-4-12b-build --agent build "<enunciado>"
# ou pipeline
./run-e2e-pipeline.sh --phase 3
```

### Resultado esperado
- 5/5 PASS em ~12 min (harness nativo do opencode).
- Artefatos reais no disco, com extensões esperadas por faixa.

### Observações
- Harness custom (tool_choice=required) não é confiável; preferir harness nativo.
- max_tokens explícito (8192) evita respostas gigantes.
- Anti-repeat ativo (janela 512).
