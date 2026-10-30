using System.Threading.Tasks;

namespace Rochas.OpenCodeBridge.Test.Archived;

/// <summary>
/// ARQUIVADO em 2026-10-30 — pin da cauda de sintaxe F# (<see cref="ArchivedFsMini"/>).
/// </summary>
/// <remarks>
/// <para><b>Por que saiu do placar.</b> Este pin nasceu como parte de
/// <c>U-tools-tail-path-rules</c> e garantia que o FsMini ensinava a forma
/// CERTA de cada erro medido (e não exibia o literal inválido que o modelo
/// copia). Com a trilha F# arquivada, o FsMini não é mais injetado em nenhum
/// request: o pin continua compilando e preservado aqui, mas não é registrado
/// no <c>Program.cs</c> — teste arquivado não entra no placar ativo.</para>
/// <para><b>Por que guardar.</b> As assertivas abaixo são a prova de que a
/// tabela de 19 códigos não regrediu para um literal copiável. Se a trilha F#
/// voltar (ver <see cref="ArchivedFsAlgoMissions"/>), basta registrar
/// <c>U-fsmini-covers-repair-codes</c> de novo e rodar.</para>
/// </remarks>
public static class ArchivedFsMiniPin
{
    /// <summary>Prova que o FsMini ensina a forma correta e omite os literais inválidos.</summary>
    public static Task CoversRepairCodes()
    {
        string fsMini = ArchivedFsMini.FsMini;
        TestContext.Check(fsMini.Contains("module Program"), "FsMini explica o entrypoint de F#");
        // Regra que derrubou E-mission-tools (L3-20261028-161849): `;` nao e' passo;
        // o range com passo e' `[start .. step .. finish]`. O literal INVALIDO nao
        // pode aparecer na cauda: medido em #13 do DRIVER-FIXES, o modelo COPIA o
        // literal que a dica mostra. Por isso o teste exige a forma correta E a
        // ausencia da forma errada copiavel.
        TestContext.Check(fsMini.Contains("[for i in 1 .. 2 .. n-1]"), "FsMini ensina range com passo (forma correta)");
        TestContext.Check(fsMini.Contains("FS0751"), "FsMini nomeia o codigo do range com ';'");
        TestContext.Check(!fsMini.Contains("[1; 3 .. n-1]"), "FsMini nao repete o literal invalido (o modelo copia)");
        // 2a forma medida no L3-20261028-163556: o modelo escreveu comprehension
        // de Haskell (`[i | i <- ...]`) e errou FS0010 por 22 turnos.
        TestContext.Check(fsMini.Contains("[for i in 1 .. n-1 -> expr]"), "FsMini ensina comprehension de F#");
        TestContext.Check(fsMini.Contains("LISTA e ARRAY"), "FsMini separa array literal de comprehension de lista");
        // 3a e 4a formas medidas no L3-20261029-030937: `do` na lista sem `yield`
        // (lista tipada unit) e `step` no range (nao existe em F#).
        TestContext.Check(fsMini.Contains("do yield"), "FsMini ensina do+yield como alternativa");
        TestContext.Check(fsMini.Contains("PASSO e' o SEGUNDO termo"), "FsMini ensina a ordem do range (passo no meio)");
        TestContext.Check(fsMini.Contains("Array.sum"), "FsMini separa array de lista (List.* x Array.*)");
        TestContext.Check(!fsMini.Contains("1 .. n - 1 .. 2"), "FsMini nao exibe a ordem trocada (o modelo copia)");
        // Escopo inteiro (2026-10-29): sem ponto flutuante, o bloco MATH e as regras
        // de int/float sairam — cauda menor, sem literal que induza `** 2.0`.
        TestContext.Check(fsMini.Contains("printfn \"%d\""), "FsMini ensina saida inteira");
        TestContext.Check(!fsMini.Contains("System.Math"), "FsMini nao carrega mais regra de Math (escopo inteiro)");
        TestContext.Check(!fsMini.Contains("** 2.0"), "FsMini nao ensina potencia float");
        TestContext.Check(!fsMini.Contains("TIPOS NUMERICOS"), "FsMini nao carrega mais a regra int/float");
        // Estrutura (2026-10-29): `let` aninhado no ramo quebra o offside (FS0058) e
        // funcao recursiva sem `rec` da FS0039 — as duas formas medidas no isolado.
        TestContext.Check(fsMini.Contains("AUXILIAR"), "FsMini ensina onde vive a funcao auxiliar");
        TestContext.Check(fsMini.Contains("let rec"), "FsMini ensina recursao sem `rec` (FS0039)");
        TestContext.Check(fsMini.Contains("FS0039 nome nao definido") && fsMini.Contains("resto de inteiro e' `a % b`"),
            "tabela F# ensina resto real na MESMA linha do FS0039");
        TestContext.Check(fsMini.Split("FS0039").Length - 1 == 1,
            "FS0039 aparece UMA vez (sem linha duplicada)");
        TestContext.Check(fsMini.Contains("FS0037 nome repetido"), "tabela F# ensina nome unico (FS0037)");
        TestContext.Check(fsMini.Contains("no range os dois limites sao do MESMO tipo"), "tabela F# ensina range de tipo unico (FS0001)");
        TestContext.Check(fsMini.Contains("PRIMOS: divisores candidatos de 2"), "FsMini separa intervalo de entrada do de divisores");
        // Sem duplicidade ambigua: cada codigo da tabela tem UMA unica linha.
        foreach (var code in new[] { "FS0010", "FS0751", "FS0003", "FS0001", "FS0039", "FS0037", "FS0058", "FS0988", "FS0020", "FS3563", "FS0752" })
            TestContext.Check(fsMini.Split(code).Length - 1 == 1, $"FsMini cita {code} uma unica vez");
        return Task.CompletedTask;
    }
}
