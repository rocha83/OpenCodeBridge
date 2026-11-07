# scripts/diag — provas manuais (não são fonte de verdade)

Scripts de bancada para **provar uma premissa** antes/depois de mexer no Core.
O aceite continua sendo a suíte em C# (`dotnet run --project Rochas.OpenCodeBridge.Test`);
o que está aqui é diagnóstico reprodutível, citado no `docs/DRIVER-FIXES.md`.

| script | prova |
|---|---|
| `fs-repair-probe.fsx` | as formas F# que `FsSyntaxRepair`/`HarnessTails.FsMini` ensinam **compilam** e dão o valor esperado (`dotnet fsi scripts/diag/fs-repair-probe.fsx`). Também documenta que `do` sem `yield` com variável de tipo aberta quebra (FS0001 'unit') — motivo de o reparo migrar para `->` |
| `verify-antirepeat-args-window.py` | emulação independente de `ContentAntiRepeat.TailRepeats` para a janela de args: payload de arquivo dispara @256 e **não** dispara @1024; loop real de args continua detectado @1024 (`python3 scripts/diag/verify-antirepeat-args-window.py`, sai 1 em falha) |

Regra de convivência: script aqui **não** substitui unit test. Quando o check vira
decisão de PASS/FAIL da bateria, ele mora em `Rochas.OpenCodeBridge.Test` (C#).


<!-- INVARIANTE-TUNEL-LLAMA:v1 -->
## Invariante — túnel de stream do llama é exclusivo da bridge/core

**Somente a bridge (`Rochas.OpenCodeBridge`, camada Core) fala com o llama-server.** Testes (`Rochas.OpenCodeBridge.Test`), `mini-bridge-python`, Web UI, Grafana e scripts são clientes/observadores: não injetam, não reescrevem, não "cutucam" e não reordenam mensagens dentro do túnel.

- Cliente envia a **requisição** (`/v1/chat/completions`, `/v1/responses`); o stream do modelo volta repassado **sem edição** pelo core.
- Proibido no cliente: nudge/continuation prompt, injeção de "próximo passo", reescrita de turno, reordenação ou truncamento de histórico.
- Harness e coerção (`main_harness`, tails, `coerce_text_tools`, `coerce_narrated`, `degenerate_retry`) vivem **só** na bridge/core, atrás de flag.
- Comportamento de teste se ajusta por **enunciado** ou **flag da bridge** — nunca editando o conteúdo do túnel no cliente.
- Nudges de continuação do `TestContext.ToolAgent` foram **removidos em 2026-11-07** (`TCTX_NUDGES` não existe mais): turno sem tool call encerra o loop e o veredito sai do artefato.
- Fonte única: `docs/TUNEL-LLAMA.md`.

