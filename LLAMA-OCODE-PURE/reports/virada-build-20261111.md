# Virada de 11/11/2026 — Scripter abortado, War passa a operar só com build

## Decisão

A estratégia com a role **Scripter** (`.sh` com heredocs → depois `.zip` + `.sh` de resgate) foi
**abortada**. Ela consumiu a madrugada de 10/11 sem nenhum PASS de belt orch: o modelo 4B ou
ignorava o contrato, ou entregava vazio (`run.sh` de 13 bytes = `dotnet build`, `.zip` com 1 de 2
`.cs`), ou delegava ao build quando podia. Cada tentativa nova era um round de 2–13 minutos para
descobrir a mesma coisa.

A partir de 11/11 a esteira War opera com **duas roles**:

| Role | Papel | Aceite |
|---|---|---|
| `build` | implementa o enunciado direto (tools), `dotnet build` até 0 erro | build rc=0 + fonte `.cs` + escopo do domínio |
| `orch` | **apenas** segmenta o enunciado em camadas e delega **cada camada** a um subagente `build` | **segmentação**: ≥ 2 subagentes `build` na rodada |

- O enunciado é o **mesmo** para build e orch (não incita segmentação — segmentar é papel do orch).
- O orch não usa `scripter` nem como fallback: permissão `subagent` só libera o recurso `build`.
- Não há mais entregável `.zip`/`.sh` no aceite, nem `run_scripts` no harness.
- Escopo do projeto continua **túnel puro Python** (bridge → llama.cpp local); nada vai para nuvem.

## Mudanças no harness (`battery-war.sh`)

- bloco `"scripter"` **removido** do `opencode.json` gerado;
- `orch`: system reescrito ("não escreve código, não cria arquivo, não roda shell; segmenta uma
  camada por subagente e delega sempre ao agente build"), descrição atualizada, permissão
  `subagent scripter` removida (fica `deny *` + `allow build`);
- loop das belts: `P_${cor}` para as duas roles (`P_*_ORCH` e `ORCH_ACEITE` deixam de ser usados);
- `run_scripts` e o aceite `.zip`/`.sh` removidos;
- novo `seg_check()`: conta sessões `agent='build'` com `parent_id` não nulo, `directory` = caso e
  `time_created >= SEG_T0` (início da tentativa) — PASS se ≥ 2;
- `rerun.sh` passou a aceitar **vários casos** (roda a sequência inteira numa rodada só).

## Estado

- `Prompts/scripter.md` removido do repositório (role fora de produção).
- Rodada `focus-build-azul-20261111-0011` em curso com as 5 belts build em sequência
  (azul, verde, roxa, marrom, preta).
- Depois das 5 build PASS: rodar as 5 orch com o aceite de segmentação.
