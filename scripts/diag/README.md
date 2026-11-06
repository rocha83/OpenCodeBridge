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
