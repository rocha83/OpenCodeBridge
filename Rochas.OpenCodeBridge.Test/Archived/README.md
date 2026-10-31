# Arquivados — trilha de algoritmos em F# (2026-10-30)

Esta pasta guarda os artefatos da trilha **F#** que saiu do gate ativo. Nada aqui
é registrado no `Program.cs` do projeto de teste, no `WarTemplate` ou no
`scripts/war/run-levels.sh`: teste arquivado **não consome GPU nem entra no
placar**. O código compila de propósito, para preservar enunciado, aceite e
tabela de sintaxe reais.

## Cenário que motiva o arquivamento

As três missões F# foram separadas em 2026-10-29 (`E-mission-algo-fs-bin`,
`-btree`, `-sort`) para isolar falha de sintaxe da linguagem da falha de
algoritmo. O modelo local (Gemma4 E4B Q4, role build, temp 0.1, thinking off)
**alterna PASS e FAIL no mesmo enunciado e no mesmo binário**:

| Amostra | Sintoma medido | Onde |
|---|---|---|
| 1ª–4ª | literal inválido copiado do próprio FsMini (`[1; 3 .. n-1]`), `do` sem `yield`, `do` + `->` juntos | DRIVER-FIXES #13, #33 |
| 5ª | lista literal `[for i in 1 .. n-1 do expr]` sem `yield` | DRIVER-FIXES #33 |
| 6ª | `FS0193` (tipo de lista incompatível) | DRIVER-FIXES #34 |
| 7ª | `FS0751/FS0003` (aridade e passo do range) | DRIVER-FIXES #34 |
| 8ª | `FS0039` (uso antes da declaração) + 15 `edit` com `oldString` obsoleto | DRIVER-FIXES #35 |
| 9ª | `FS0001` (ramo `string` em expressão `int`) | DRIVER-FIXES #36 |
| 10ª | `FS0043` (`==` de C# em F#) e `FS0001` (ordem de `List.contains`) | logs/isolated 2026-10-30 |
| 11ª | `E-mission-algo-fs-btree` PASS em 3 turnos, e o mesmo teste FAIL no dia seguinte com outro código | logs/isolated 2026-10-29/30 |

O `FsMini` (tabela de 19 códigos) e o `FsSyntaxRepair` reduziram a taxa de erro,
mas **não a zeraram**: a variância do modelo continua decidindo o resultado.
Cada rodada custa de 1 a 4 minutos de GPU e, no L4, o FAIL de um F# bloqueia
belts, frontend e Web UI pelo fail-fast — o custo de manter a trilha no gate é
maior que o sinal que ela entrega.

Os mesmos quatro algoritmos estão cobertos em C# com aceite equivalente:

- `E-mission-algo-cs-bin` — busca binária;
- `E-mission-algo-cs-btree` — árvore de busca;
- `E-mission-algo-cs-kmeans` — K-Means;
- `E-mission-algo-cs-sort` — quick sort + bubble sort;
- `E-mission-algo-cs-hanoi` — Hanoi + crivo (missão base, renomeada com sufixo
  para não casar por substring com as demais).

A trilha C# fechou 5/5 PASS em 2026-10-30 (`logs/isolated/algo-batch5-*.log`) e
é a que a WAR e o porte para o AI.Manager.Bridge vão medir.

## Inventário do que está aqui

| Arquivo | O que preserva | Estado |
|---|---|---|
| `FsAlgoMissions.cs` | as três missões F# (`Bin`, `BTree`, `Sort`) chamando o runner real | compila, não registrado |
| `FsMiniTail.cs` | a cauda de sintaxe F# (`ArchivedFsMini.FsMini`), tabela de 19 códigos | compila, não injetada |
| `FsMiniPin.cs` | o pin `U-fsmini-covers-repair-codes` (forma correta presente, literal inválido ausente) | compila, não registrado |
| `README.md` | este cenário, o motivo e o roteiro de reativação | — |

O `E-mission-algo-fs-kmeans` já havia sido removido em 2026-10-30 (antes deste
arquivamento): o algoritmo segue coberto em C# e o F# ficou só com as três em
que o modelo às vezes convergia.

## O que **não** foi arquivado (e por quê)

- `Core/FsSyntaxRepair.cs` e as dicas de `FS*` continuam no Core: são reparo de
  produção para **qualquer** arquivo F# que a bridge venha a atender, não um
  artefato de teste. Se o suporte a F# for removido do produto, este é o próximo
  candidato a arquivamento.
- `Core/HarnessTails.cs` perdeu apenas a const `FsMini`; a cauda default de
  behavior/tools (usada por C# e WAR) segue intacta.

## Como reativar

1. Registrar as três missões no `Program.cs` do projeto de teste
   (`ArchivedFsAlgoMissions.Bin/BTree/Sort`).
2. Devolver os steps `algo-fs-*` ao L4 do `WarTemplate` e ao
   `scripts/war/run-levels.sh`.
3. Reintroduzir o `FsMini` no `HarnessTails` (ou referenciar
   `ArchivedFsMini.FsMini`) e injetá-lo no system da fase F#; registrar
   `U-fsmini-covers-repair-codes` apontando para `ArchivedFsMiniPin`.
4. Exigir **PASS estável em 3 rodadas seguidas** antes de promover de volta ao
   gate; um PASS isolado não reverte o arquivamento.

## Arquivamento complementar (2026-10-31)

Fechamento do que ainda vivia fora desta pasta:

| Artefato | De | Para | Motivo |
|---|---|---|---|
| `FsSyntaxRepair.cs` (684 linhas) | `Rochas.OpenCodeBridge.Core` | aqui (`namespace ...Test.Archived`) | o Core é a fonte da instrução do gate ativo (C#); sintaxe F# no Core confundia a composição — o nudge de tool-call chegou a ensinar `Program.fs`/`printfn` como "copie a forma" e o modelo escreveu `for (var point in _points)` (kmeans do L4, 617s) |
| 25 pins `U-fs-repair-*` | `UnitToolBreakerTests.cs` | `FsRepairPins.cs` (`FsRepairPins`) | teste arquivado não entra no placar; saíram do `Program.cs` |

O que **ficou** no Core: `DiagnosticLine.WithErrorLine` (ponteiro de linha
`arquivo(linha,coluna)`), que é mecânica de diagnóstico compartilhada — o
`CsSyntaxRepair.Suggest` depende dela. Não é sintaxe F#, é o "aponte a linha"
genérico; manter cópia nos dois lados violaria a regra DRY.

Caminho ativo: `CsSyntaxRepair` (tabela na cauda + dica in-flow + reparo como
fallback após 2 entregas da mesma dica).
