using System.Threading.Tasks;

namespace Rochas.OpenCodeBridge.Test.Archived;

/// <summary>
/// ARQUIVADO em 2026-10-30 — trilha de algoritmos em F# (bin, btree, sort).
/// </summary>
/// <remarks>
/// <para><b>Cenário que motiva o arquivamento.</b> As missões F# foram separadas
/// em 2026-10-29 (`E-mission-algo-fs-bin`, `-btree`, `-sort`) para isolar falhas
/// de sintaxe da linguagem. O modelo local (Gemma4 E4B Q4, role build, temp 0.1,
/// thinking off) alterna entre PASS e FAIL no MESMO binário e no MESMO enunciado:
/// um dia `fs-btree` passa em 3 turnos; no dia seguinte o mesmo teste falha com
/// `FS0043` (`==` de C# em F#), `FS0001` (ordem de `List.contains`), `FS0010`
/// (comprehension/`;` em parâmetro) ou entra em loop de `edit` com `oldString`
/// obsoleto. O FsMini e o `FsSyntaxRepair` reduziram a taxa de erro, mas não a
/// zeraram: a variância do modelo continua decidindo o resultado. Cada rodada
/// custa 1–4 minutos de GPU e, no L4, o FAIL de um F# bloqueia belts, frontend e
/// Web UI pelo fail-fast.</para>
/// <para><b>Por que arquivar (e não apagar).</b> Os mesmos quatro algoritmos
/// (busca binária, árvore de busca, K-Means, quick/bubble sort) estão cobertos
/// em C# com aceite equivalente (`E-mission-algo-cs-{bin,btree,kmeans,sort}`).
/// A trilha C# é determinística o suficiente para fechar 5/5 e é a que a WAR e o
/// porte para o AI.Manager.Bridge vão medir. O código F# fica preservado aqui,
/// junto do <see cref="ArchivedFsMini"/> (a tabela de sintaxe é o aprendizado
/// mais valioso e custoso de reproduzir) e dos pins de reparo em
/// <c>UnitToolBreakerTests</c>, para retomada quando houver modelo/tuning que
/// estabilize F#.</para>
/// <para><b>Como reativar.</b> (1) registrar os três nomes no
/// <c>Program.cs</c> do projeto de teste; (2) devolver os steps ao L4 do
/// <c>WarTemplate</c> e ao <c>scripts/war/run-levels.sh</c>; (3) reinjetar o
/// FsMini no system da fase F# (ver <see cref="ArchivedFsMini"/>) e rodar os
/// pins `U-fsmini-covers-repair-codes`; (4) exigir PASS estável em 3 rodadas
/// seguidas antes de promover de volta ao gate.</para>
/// <para><b>Não registrar no runner.</b> Estes métodos compilam de propósito
/// (preservam o enunciado e o aceite reais), mas não são chamados por
/// <c>Program.cs</c>: um teste arquivado não pode consumir GPU nem entrar no
/// placar da WAR.</para>
/// </remarks>
public static class ArchivedFsAlgoMissions
{
    /// <summary>F# arquivado (1/3): busca binária no array ordenado, alvo 13.</summary>
    public static Task Bin() => E2EMissionTests.RunAlgoMission(
        "e2e-mission-algo-fs-bin", "F#", "bin",
        E2EMissionTests.AlgoOkBin, E2EMissionTests.HasHardcodedBin);

    /// <summary>F# arquivado (2/3): árvore de busca com inserção e busca do valor 6.</summary>
    public static Task BTree() => E2EMissionTests.RunAlgoMission(
        "e2e-mission-algo-fs-btree", "F#", "btree",
        E2EMissionTests.AlgoOkBTree, E2EMissionTests.HasHardcodedBTree);

    /// <summary>F# arquivado (3/3): quick sort + bubble sort.</summary>
    public static Task Sort() => E2EMissionTests.RunAlgoMission(
        "e2e-mission-algo-fs-sort", "F#", "sort",
        E2EMissionTests.AlgoOkSort, E2EMissionTests.HasHardcodedSort);
}
