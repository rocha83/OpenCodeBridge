# PENDINGS — OpenCodeBridge

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
