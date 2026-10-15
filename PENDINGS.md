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
