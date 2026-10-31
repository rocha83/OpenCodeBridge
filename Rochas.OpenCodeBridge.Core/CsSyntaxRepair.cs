using System.Text;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Sintaxe C# em três níveis, nessa ordem: INSTRUÇÃO na cauda
/// (<see cref="HarnessTails.CsErrorTableLine"/>, gerada daqui) → dica in-flow no
/// retry (<see cref="Suggest"/>, aponta a linha e a forma certa) → reparo
/// determinístico só como FALLBACK após 2 entregas da MESMA dica sem sucesso
/// (<see cref="TryRepair"/>). Genérico de propósito: não conhece o algoritmo do
/// pedido, só a forma da linguagem. Único lugar dessa mecânica em C#.
/// </summary>
/// <remarks>
/// Hierarquia medida, não opinativa: instruir a forma certa ANTES do erro resolveu o
/// hanoi do L4 de 2026-10-31 (386s e FAIL num loop de <c>CS1003</c> — um <c>)</c> a
/// mais no fim de um <c>if</c>) na rodada seguinte, onde o modelo acertou de primeira.
/// Empurrar a correção cedo demais tira do modelo a chance de aprender e esconde a
/// fronteira de capacidade; por isso o reparo determinístico só entra quando a
/// instrução prova que não bastou (2 entregas da MESMA dica e o erro persiste).
/// Nasceu do L4 de 2026-10-30: `algo-cs-btree-bridge` FAIL em 345s com
/// <c>Program.cs(101,6): error CS1513: } expected</c> — 26 abre-chave e 25
/// fecha-chave, 26 turnos sem fechar o último bloco.
/// </remarks>
public static class CsSyntaxRepair
{
    /// <summary>
    /// Sugestão concreta para o erro. Vazio quando não há mapa conhecido — nesse caso
    /// o chamador mantém só a linha do compilador (sem inventar correção).
    /// </summary>
    /// <param name="source">Fonte C# atual (texto completo).</param>
    /// <param name="compilerError">Saída do build com o diagnóstico CSxxxx.</param>
    /// <param name="repeatCount">Quantas vezes ESTA MESMA dica já foi entregue antes
    /// (0 na primeira). A partir da 3ª o Core manda reescrever o arquivo inteiro —
    /// dica idêntica repetida não ensina (medido em 2026-10-29 na trilha F#).</param>
    /// <returns>"ERRO: ...\nCERTO: ..." ou string vazia.</returns>
    public static string Suggest(string source, string compilerError, int repeatCount = 0)
    {
        string hint = SuggestForCode(source, compilerError);
        if (hint.Length == 0) return hint;
        hint = FsSyntaxRepair.WithErrorLine(hint, compilerError);
        if (repeatCount < 2) return hint;
        return hint + "\n\nA MESMA dica ja' veio " + (repeatCount + 1)
             + " vezes: pare de editar a mesma linha e reescreva o arquivo inteiro com a tool `write`, aplicando a forma acima.";
    }

    /// <summary>
    /// Há diagnóstico C# com código na saída do build? Vale para `error` e `warning`
    /// (o CS0168/CS0219 saem como warning e o reparo só de `error` não dispararia).
    /// </summary>
    /// <param name="output">Saída do build/execução.</param>
    /// <returns>True quando existe um código CSxxxx na saída.</returns>
    public static bool HasDiagnostic(string output)
        => !string.IsNullOrEmpty(output)
           && Regex.IsMatch(output, @"(?:error|warning) CS\d{4}");

    /// <summary>Linha da tabela de reparo C#: código, causa (pt-BR) e a forma CERTA.</summary>
    public sealed record Rule(string Code, string Cause, string CorrectForm);

    /// <summary>
    /// Tabela ÚNICA dos códigos C# com causa e forma correta. A dica in-flow e a
    /// documentação (<c>docs/DRIVER-FIXES.md</c>) saem daqui — duas cópias divergem na
    /// primeira edição. Cada exemplo é compilável e nunca exibe o literal errado: o
    /// modelo copia o que a dica mostra (regra primordial do projeto).
    /// </summary>
    public static IReadOnlyList<Rule> Rules => RuleTable;

    static readonly Rule[] RuleTable =
    {
        new("CS1513", "falta `}` para fechar um bloco",
            "cada `{` de bloco (namespace, classe, metodo, if, for) fecha com UM `}`; no fim do arquivo feche na ordem inversa em que abriu"),
        new("CS1003", "token de fechamento a mais (ou abertura sem par)",
            "cada `(` fecha com UM `)` e o `]` de um indice fecha ANTES do `)` da condicao:\n"
            + "if (flag[i] && flag[i + 2])\n    count++;"),
        new("CS1026", "parentese aberto sem `)`",
            "feche o par na MESMA linha que o compilador aponta: Somar(a, b)"),
        new("CS1002", "instrucao sem `;`",
            "termine a instrucao com `;`: int x = 1;"),
        new("CS1519", "instrucao solta fora de um metodo",
            "toda instrucao vive DENTRO de um metodo: static void Main() { ... }"),
        new("CS1525", "termo de expressao invalido (operador ou virgula no lugar errado)",
            "cada operador tem um operando de cada lado e argumentos se separam por `,`: Somar(a, b)"),
        new("CS0116", "membro declarado fora de um tipo",
            "metodo e campo vivem DENTRO de `class`/`struct`/`record`, nao direto no `namespace`"),
        new("CS8803", "instrucoes de topo fora de lugar",
            "instrucoes de topo vem ANTES de `namespace`/`class`, ou tudo dentro de static void Main()"),
        new("CS0161", "nem todos os caminhos devolvem valor",
            "todo caminho de um metodo nao-void termina em `return`: if (c) return 1; return 0;"),
        new("CS0103", "nome nao existe no contexto atual",
            "declare o nome antes do uso e no mesmo escopo: int total = Somar(a, b);"),
        new("CS0029", "tipo incompativel na atribuicao",
            "os dois lados tem o MESMO tipo, ou conversao explicita: int x = (int)valor;"),
        new("CS0266", "tipo incompativel na conversao implicita",
            "os dois lados tem o MESMO tipo, ou conversao explicita: int x = (int)valor;"),
        new("CS0165", "variavel local usada sem ser atribuida",
            "atribua antes de ler: int total = 0;"),
        new("CS7036", "chamada sem argumento para parametro obrigatorio",
            "passe TODOS os parametros da declaracao: Somar(a, b)"),
        new("CS0102", "definicao duplicada do mesmo membro",
            "defina o nome UMA vez; apague a repeticao"),
        new("CS0128", "variavel local definida duas vezes",
            "defina o nome UMA vez; apague a repeticao"),
        new("CS1022", "`}` a mais (bloco fechado que nao estava aberto)",
            "cada `}` fecha um bloco que foi aberto; apague o `}` sobrando no fim do arquivo"),
    };

    static string SuggestForCode(string source, string compilerError)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(compilerError)) return "";
        // Despacha pelo PRIMEIRO diagnóstico: o `dotnet` devolve vários e decidir pelo
        // blob fazia a dica de um código responder a outro (medido em 2026-10-29).
        switch (FirstCode(compilerError))
        {
            case "CS1513": return FixMissingBrace(source);
            case "CS1003": return FixUnexpectedToken(source, compilerError);
            case "CS1022": return FixExtraBrace(source);
            default: return FromTable(FirstCode(compilerError));
        }
    }

    /// <summary>Dica estática da tabela para um código, ou "" quando não há mapa.</summary>
    /// <param name="code">Código <c>CSxxxx</c>.</param>
    /// <returns>"ERRO: ...\nCERTO: ..." ou string vazia.</returns>
    static string FromTable(string code)
    {
        foreach (var r in RuleTable)
            if (r.Code == code) return "ERRO: " + r.Cause + ".\nCERTO: " + r.CorrectForm;
        return "";
    }

    /// <summary>
    /// Reparo DETERMINÍSTICO, fallback de ÚLTIMA HORA: só entra depois de 2 entregas
    /// da MESMA dica sem o modelo resolver. Antes disso a instrução (tabela na cauda +
    /// dica in-flow) tem que dar conta sozinha — empurrar a correção cedo demais tira
    /// do modelo a chance de aprender e esconde a fronteira de capacidade (medido no
    /// L4 de 2026-10-31: instruir bastou e o hanoi passou de FAIL a PASS).
    /// </summary>
    /// <remarks>
    /// A dica (<see cref="Suggest"/>) só ENSINA a forma; modelo fraco ignora (medido
    /// em 2026-10-30: o Q4 Base repetiu o mesmo <c>CS1513</c> por 26 turnos, arquivo
    /// com 22 abre-chave e 21 fecha-chave). O harness aplica este reparo quando o build
    /// falha e a MESMA dica já foi dada 2x: a chave que falta é mecânica, não lógica —
    /// se o algoritmo também estiver errado, o rebuild falha e o aceite segue honesto.
    /// </remarks>
    /// <param name="source">Fonte C# atual (texto completo).</param>
    /// <param name="compilerError">Saída do build com o diagnóstico CSxxxx.</param>
    /// <param name="repaired">Fonte corrigido quando o reparo se aplica.</param>
    /// <param name="repeatCount">Quantas vezes ESTA MESMA dica já foi entregue antes
    /// (0 na primeira). Abaixo de 2 o reparo não entra: ainda é papel da instrução.</param>
    /// <returns>True quando há correção determinística, o threshold foi atingido e o fonte foi devolvido em <paramref name="repaired"/>.</returns>
    public static bool TryRepair(string source, string compilerError, out string repaired, int repeatCount = 0)
    {
        repaired = source;
        // Fallback só depois de a instrução falhar 2x: antes disso empurrar seria
        // o modelo entregando o resultado do HARNESS, não dele (fronteira escondida).
        if (repeatCount < 2) return false;
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(compilerError)) return false;
        string code = FirstCode(compilerError);
        // CS1513: o bloco mais externo não fechou no fim.
        if (code == "CS1513") return TryRepairMissingBrace(source, out repaired);
        // CS1003: fechamento a mais na linha do erro (medido no hanoi de 2026-10-31:
        // `)` duplicado no fim do `if` — o Roslyn esperava o `]` do índice seguinte).
        if (code == "CS1003") return TryRepairExtraClose(source, compilerError, out repaired);
        return false;
    }

    /// <summary>
    /// CS1513: acrescenta as linhas de <c>}</c> que faltam no fim (bloco mais externo
    /// não fechou). Mecânico e não ambíguo — a chave que falta não é lógica.
    /// </summary>
    /// <param name="source">Fonte C# atual.</param>
    /// <param name="repaired">Fonte com os <c>}</c> finais acrescentados.</param>
    /// <returns>True quando havia desbalanceamento e o reparo foi aplicado.</returns>
    static bool TryRepairMissingBrace(string source, out string repaired)
    {
        repaired = source;
        int open = CountChar(source, '{'), close = CountChar(source, '}');
        if (open <= close) return false;
        int missing = open - close;
        var sb = new StringBuilder(source);
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
        for (int i = 0; i < missing; i++) sb.Append("}\n");
        repaired = sb.ToString();
        return true;
    }

    /// <summary>
    /// CS1003: remove o fechamento EXCEDENTE da linha do erro. Só age quando o
    /// desbalanceamento é LOCAL e igual ao GLOBAL do arquivo (a linha é a única
    /// culpada) e os fechamentos sobrando estão no FIM da linha — a forma medida.
    /// Fora dessa medida, não mexe: remover parêntese legítimo de bloco multilinha
    /// quebraria um fonte que só precisava da dica.
    /// </summary>
    /// <param name="source">Fonte C# atual.</param>
    /// <param name="compilerError">Saída do build (traz a linha do diagnóstico).</param>
    /// <param name="repaired">Fonte com o fechamento excedente removido.</param>
    /// <returns>True quando o reparo se aplica.</returns>
    static bool TryRepairExtraClose(string source, string compilerError, out string repaired)
    {
        repaired = source;
        int lineNo = SourceLineNumber(compilerError);
        if (lineNo < 1) return false;
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        if (lineNo > lines.Length) return false;
        string line = lines[lineNo - 1];
        // Desbalanceamento da LINHA: qual fechamento sobra (parêntese ou colchete).
        char opener, closer;
        int local = CountChar(line, ')') - CountChar(line, '(');
        if (local > 0) { opener = '('; closer = ')'; }
        else
        {
            local = CountChar(line, ']') - CountChar(line, '[');
            if (local <= 0) return false;
            opener = '['; closer = ']';
        }
        // A linha precisa ser a ÚNICA culpada: o desbalanceamento global é o mesmo.
        if (CountChar(source, closer) - CountChar(source, opener) != local) return false;
        // Os fechamentos sobrando têm que estar no FIM da linha (não no meio).
        int trailing = 0;
        for (int i = line.Length - 1; i >= 0 && line[i] == closer; i--) trailing++;
        if (trailing < local) return false;
        lines[lineNo - 1] = line[..(line.Length - local)];
        repaired = string.Join("\n", lines);
        return repaired != source;
    }

    /// <summary>
    /// CS1003 (<c>Syntax error, '&lt;tok&gt;' expected</c>): o compilador achou um
    /// fechamento a mais ANTES do token que esperava. O caso medido no L4 de
    /// 2026-10-31 (hanoi, 386s e FAIL) foi um <c>)</c> duplicado no fim de um
    /// <c>if</c>: a condição fechava duas vezes e o Roslyn esperava o <c>]</c> do
    /// índice seguinte. A dica descreve por extenso e mostra só a forma CERTA.
    /// </summary>
    /// <param name="source">Fonte C# atual.</param>
    /// <param name="compilerError">Saída do build.</param>
    /// <returns>Dica com a contagem local, ou a dica genérica da tabela.</returns>
    static string FixUnexpectedToken(string source, string compilerError)
    {
        string line = SourceLineAt(source, compilerError);
        int lineNo = SourceLineNumber(compilerError);
        int extraParen = CountChar(line, ')') - CountChar(line, '(');
        if (extraParen > 0)
            return "ERRO: a linha " + lineNo + " tem " + extraParen
                 + " `)` a mais do que `(`.\n"
                 + "CERTO: cada `(` fecha com UM `)` so', e o `]` de um indice fecha ANTES do `)` da condicao:\n"
                 + "if (flag[i] && flag[i + 2])\n"
                 + "    count++;";
        int extraBracket = CountChar(line, ']') - CountChar(line, '[');
        if (extraBracket > 0)
            return "ERRO: a linha " + lineNo + " tem " + extraBracket
                 + " `]` a mais do que `[`.\n"
                 + "CERTO: cada `[` fecha com UM `]` so': flag[i]";
        return FromTable("CS1003");
    }

    /// <summary>
    /// CS1022 (<c>Type or namespace definition, or end-of-file expected</c>): o
    /// arquivo fechou um bloco que não estava aberto — há <c>}</c> a mais. A dica
    /// aponta a contagem real e a forma certa (um <c>}</c> por bloco aberto).
    /// </summary>
    /// <param name="source">Fonte C# atual.</param>
    /// <returns>Dica com a contagem, ou "" quando não há excesso.</returns>
    static string FixExtraBrace(string source)
    {
        int open = CountChar(source, '{'), close = CountChar(source, '}');
        if (close <= open) return "";
        int extra = close - open;
        return "ERRO: " + extra + " `}` a mais — o arquivo tem " + open + " abre-chave e "
             + close + " fecha-chave.\n"
             + "CERTO: cada `}` fecha um bloco que foi aberto; apague o(s) " + extra
             + " `}` sobrando no fim do arquivo.";
    }

    /// <summary>Número da linha que o compilador aponta (<c>arquivo(linha,coluna)</c>) ou 0.</summary>
    /// <param name="compilerError">Saída do build.</param>
    static int SourceLineNumber(string compilerError)
    {
        var where = Regex.Match(compilerError, @"\((\d+),(\d+)\)");
        return where.Success && int.TryParse(where.Groups[1].Value, out int lineNo) ? lineNo : 0;
    }

    /// <summary>Texto da linha apontada pelo compilador (vazio quando não há posição).</summary>
    /// <param name="source">Fonte C# atual.</param>
    /// <param name="compilerError">Saída do build.</param>
    static string SourceLineAt(string source, string compilerError)
    {
        int lineNo = SourceLineNumber(compilerError);
        if (lineNo < 1) return "";
        var lines = source.Replace("\r\n", "\n").Split('\n');
        return lineNo <= lines.Length ? lines[lineNo - 1] : "";
    }

    /// <summary>Quantas vezes <paramref name="c"/> aparece em <paramref name="text"/>.</summary>
    /// <param name="text">Texto a contar.</param>
    /// <param name="c">Caractere.</param>
    static int CountChar(string text, char c)
    {
        int n = 0;
        foreach (char ch in text) if (ch == c) n++;
        return n;
    }

    /// <summary>
    /// CS1513 (<c>} expected</c>): diz QUANTOS blocos faltam fechar, com a contagem
    /// real do arquivo. A forma ensinada é sempre válida (linhas com `}` no fim), e a
    /// dica nunca mostra um literal inválido copiável.
    /// </summary>
    /// <param name="source">Fonte C# atual.</param>
    /// <returns>Dica com a contagem, ou "" quando não há desbalanceamento simples.</returns>
    static string FixMissingBrace(string source)
    {
        int open = 0, close = 0;
        foreach (char c in source)
        {
            if (c == '{') open++;
            else if (c == '}') close++;
        }
        if (open <= close) return "";
        int missing = open - close;
        return "ERRO: " + missing + " bloco(s) aberto(s) sem fechar — o arquivo tem "
             + open + " abre-chave e " + close + " fecha-chave.\n"
             + "CERTO: acrescente " + missing + " linha(s) contendo só `}` no fim do arquivo, "
             + "uma para cada bloco que ficou aberto (a última fecha a declaração mais externa, "
             + "namespace ou classe).";
    }

    static string FirstCode(string compilerError)
    {
        var m = Regex.Match(compilerError, @"error (CS\d{4})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(compilerError, @"warning (CS\d{4})");
        return m.Success ? m.Groups[1].Value : "";
    }
}
