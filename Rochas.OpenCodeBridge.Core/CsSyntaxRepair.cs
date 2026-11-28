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
        hint = DiagnosticLine.WithErrorLine(hint, compilerError);
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
           && Regex.IsMatch(output, @"(?:error|warning)\s+(?:CS|MSB|NETSDK|NU)\d{4}");

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
            "cada `{` de bloco (namespace, classe, metodo, if, for, foreach) fecha com UM `}`; no fim do arquivo feche na ordem inversa em que abriu"),
        new("CS1003", "token inesperado: `)` a mais, `for..in`, ou `?` depois do nome do parametro",
            "cada `(` fecha com UM `)` e o `]` de um indice fecha ANTES do `)` da condicao:\n"
            + "if (flag[i] && flag[i + 2])\n    count++;\n"
            + "para percorrer colecao use `foreach` — C# nao tem `for` com `in`:\n"
            + "foreach (var item in colecao)\n    Console.WriteLine(item);\n"
            + "Em tipo anulavel o `?` vai DEPOIS DO TIPO, nunca depois do nome: "
            + "`public record Reembolso(PessoaJuridica? Beneficiario, ...)`."),
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
        new("CS0103", "nome nao existe no contexto atual (variavel de `for` usada fora do laco)",
            "declare o nome no escopo onde ele e' lido: `int total = Somar(a, b);` antes do uso; " +
            "a variavel criada dentro do `for` so vive no laco — para usa-la depois declare ANTES:\n"
            + "int i;\nfor (i = 0; i < n; i++)\n    Soma += v[i];\nConsole.WriteLine(i);\n"
            + "ou percorra com `foreach (var item in colecao)` quando o indice nao for necessario"),
        new("CS0019", "operador aplicado a tipo generico `T` sem restricao (`==`, `!=`, `<`)",
            "compare valor generico por igualdade com `EqualityComparer<T>.Default.Equals(a, b)` " +
            "(negacao: `!EqualityComparer<T>.Default.Equals(a, b)`) e ordem com restricao no metodo:\n"
            + "static bool Igual<T>(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);\n"
            + "static bool Menor<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) < 0;"),
        new("CS0050", "tipo de retorno/parametro menos acessivel que o metodo",
            "o tipo da assinatura publica tem a MESMA acessibilidade do metodo: use `public sealed class Ponto { public int X; }` " +
            "com `public IReadOnlyList<Ponto> Calcular()`; se o tipo for `internal`, o metodo tambem e' `internal`"),
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
        new("CS8954", "duas declaracoes de `namespace` file-scoped no mesmo arquivo",
            "o `namespace X;` file-scoped aparece UMA vez, na primeira linha do arquivo, e tudo o mais (classes, metodos, `Main`) vem depois dele sem repetir a declaracao; " +
            "para varios blocos no mesmo arquivo use o formato com chaves: `namespace App { ... }`, uma vez por bloco"),
        new("CS1056", "caractere inesperado (cerca markdown/` de bloco colada no arquivo)",
            "o arquivo recebe SO' codigo: nada de cerca ``` , nada de `backtick` e nada de texto de explicacao; " +
            "remova a linha com o caractere e mantenha a instrucao C# normal:\n"
            + "var itens = conn.Query<Reembolso>(\"SELECT id, valor FROM reembolsos\").ToList();"),
        new("CS1001", "identificador esperado (cabecalho do arquivo recomecado dentro do corpo)",
            "`using`, `namespace` e `class` ficam UMA vez, no TOPO do arquivo, e dentro de um metodo so entram instrucoes — " +
            "o arquivo nao recomeca no meio; para trocar tudo, mande o arquivo COMPLETO com `write`, do topo ao ultimo `}`, uma unica vez"),
        new("CS0246", "tipo/namespace nao encontrado (falta `using` do projeto, falta `PackageReference`, ou o tipo nao existe)",
            "para TIPO DO PROPRIO PROJETO: ABRA o .cs que define o tipo, copie o namespace EXATO dele e coloque esse using no topo do arquivo que usa o tipo. "
            + "Se o erro citar varios tipos, adicione UM using para CADA namespace. Exemplo: `IRepositorio.cs` com `namespace Reembolsos.Domain;` exige "
            + "`using Reembolsos.Domain;` em quem escreve `IRepositorio<Reembolso>`. Confira a grafia do nome no arquivo. "
            + "Para API externa use so o que existe no pacote referenciado; sem EF Core, Dapper + Microsoft.Data.Sqlite:\n"
            + "using Dapper;\nusing Microsoft.Data.Sqlite;\n"
            + "var conn = new SqliteConnection(\"Data Source=app.db\");\n"
            + "var itens = conn.Query<Reembolso>(\"SELECT id, valor FROM reembolsos\").ToList();\n"
            + "e no `.csproj` declare `<PackageReference Include=\"Dapper\" Version=\"2.1.89\" />` e " +
            "`<PackageReference Include=\"Microsoft.Data.Sqlite\" Version=\"10.0.12\" />` (versoes do cache NuGet local)"),
        new("CS0535", "classe declara a interface mas nao implementa todos os membros dela",
            "implemente TODO membro da interface com a MESMA assinatura (nome, parametros e retorno):\n"
            + "public sealed class RepositorioItem : IRepositorio<Item>\n{\n"
            + "    public Task<List<Item>> BuscarAsync() => Task.FromResult(new List<Item>());\n}"),
        new("CS0105", "using repetido no mesmo arquivo/namespace",
            "deixe UM `using Namespace;` no topo do arquivo, antes de `namespace`; remova as linhas repetidas com `edit`"),
        new("CS0101", "o mesmo tipo foi declarado duas vezes (dois arquivos com a mesma classe)",
            "ACAO: leia os DOIS arquivos que o compilador citou, escolha UMA declaracao e REMOVA a outra com `edit` " +
            "(se o arquivo inteiro estiver duplicado, apague o arquivo repetido). Depois recompila. "
            + "Cada tipo tem UM arquivo e UMA declaracao; para alterar, edite o arquivo que ja existe — "
            + "`write` num arquivo novo com a mesma classe duplica o tipo"),
        new("CS1061", "membro inexistente no tipo (ex.: `IsMatch` no `string`, `ExecuteNonQueryAsync` na conexao)",
            "`IsMatch` pertence ao `Regex`, nao ao `string`:\n"
            + "using System.Text.RegularExpressions;\n"
            + "bool ok = Regex.IsMatch(texto, @\"^\\d{3}\\.\\d{3}\\.\\d{3}-\\d{2}$\");\n"
            + "Para SQLite, `ExecuteNonQueryAsync` e' do `SqliteCommand`, nao da conexao:\n"
            + "using var cmd = conn.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync();\n"
            + "Ou use Dapper com `using Dapper;`: `await conn.ExecuteAsync(sql);`"),
        new("CS1503", "argumento com tipo incompativel (ex.: `string` onde o generico `T` e' esperado)",
            "no codigo generico nao passe literal `string` para `T`: crie o item com o tipo concreto ou construa o objeto inteiro:\n"
            + "var itens = new List<Reembolso> { new Reembolso { Id = 1 } };"),
        new("CS1950", "inicializador de colecao com argumento invalido para `Add` do tipo",
            "dentro de `{ }` va o VALOR do tipo da colecao, nao um texto:\n"
            + "var itens = new List<Reembolso> { new Reembolso { Id = 1, Valor = 10m } };"),
        new("MSB1003", "`dotnet build` nao achou arquivo de projeto no diretorio",
            "rode o build apontando o projeto existente, com nome: `dotnet build Reembolsos.csproj` (build NAO aceita `--project`); " +
            "se nao existir, crie o `.csproj` com nome primeiro"),
        new("MSB4025", "`.csproj` malformado/truncado (XML sem fechar)",
            "o `.csproj` e' XML completo, do `<Project Sdk=\"Microsoft.NET.Sdk\">` ao `</Project>`:\n"
            + "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>"),
        new("MSB5010", "arquivo de solucao `.sln` inventado, sem cabecalho de formato",
            "o `.sln` e' artefato GERADO, nao texto colado: gere com o SDK (`dotnet new sln -n App` e " +
            "`dotnet sln App.sln add App.csproj`) ou apague o `.sln` e construa o projeto (`dotnet build App.csproj`); " +
            "o cabecalho `Microsoft Visual Studio Solution File, Format Version 12.00` tem de ser a PRIMEIRA linha do arquivo"),
        new("MSB1011", "mais de um `.csproj`/`.sln` na pasta; `dotnet build` sem alvo ficou ambiguo",
            "mantenha UM projeto na raiz: mova o conteudo da subpasta para a raiz (`mv mcs/* . && rm -rf mcs`) e rode " +
            "`dotnet build App.csproj`; se os dois projetos sao intencionais, gere a solucao (`dotnet new sln` + " +
            "`dotnet sln add`) e construa pela `.sln`; nunca rode `dotnet build` sem argumento"),
        new("NETSDK1045", "TargetFramework acima do SDK instalado",
            "use `net10.0` (SDK da maquina) no `.csproj`: <TargetFramework>net10.0</TargetFramework>"),
        // Tabela do tunel Python (hints.py) migrada para a fonte unica do Core.
        // Cada CERTO e' copiavel e compila; a dica nunca mostra o literal errado.
        new("CS0080", "clausula where em tipo/metodo nao generico",
            "`where T : ...` so vale em generico: `public sealed class Repositorio<T> where T : class { ... }` ou apague a clausula"),
        new("MSB1001", "opcao invalida na linha de comando do dotnet",
            "rode `dotnet build App.csproj -v q`; flags inventadas como `--no-warn` nao existem — use `<NoWarn>CS0168</NoWarn>` no `.csproj`"),
        new("CS0305", "tipo generico usado sem argumento de tipo",
            "declare `public sealed class Result<T> { ... }` e use `Result<Reembolso>`; se nao precisa de generico, use uma classe `Result` simples"),
        new("CS0234", "namespace/assembly ausente",
            "declare `namespace X;` uma vez no topo, use `using Cadastros.Dominio;` para types de outra camada e `<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />` no `.csproj` quando for ASP.NET"),
        new("CS0122", "membro inacessivel por nivel de protecao",
            "use o membro publico: `public sealed class Cliente { public int Id { get; init; } }`"),
        new("CS1674", "IEnumerable<T> nao e IDisposable",
            "`var lista = await conn.QueryAsync<Reembolso>(sql); foreach (var r in lista) { ... }` — `using var` so para conexao/reader/transacao"),
        new("CS1929", "tipo generico aplicado em metodo nao generico",
            "so `conn.QueryAsync<Reembolso>(sql, p)` e generico; no SEU repositorio chame `await repositorio.QueryAsync(sql, p)` sem `<T>`"),
        new("CS1501", "nenhuma sobrecarga aceita essa quantidade de argumentos",
            "copie a assinatura: `Task<IEnumerable<Reembolso>> QueryAsync(string sql, object? p)` — a chamada passa exatamente `(string, object?)`"),
        new("CS0411", "argumentos de tipo nao puderam ser inferidos",
            "`await conn.QueryAsync<Reembolso>(sql, p);` com `Task<IEnumerable<T>> QueryAsync<T>(string sql, object? p)`; ao chamar o seu metodo, sem `<T>`"),
        new("CS0426", "nome de tipo usado como namespace (ou namespace com grafia errada)",
            "depois de `using Reembolsos.Domain;` use o tipo direto: `IEnumerable<Reembolso>` "
            + "(nunca `Reembolso.Domain.Reembolso`). Confira no arquivo que define o tipo se e' "
            + "`Reembolsos.Domain` ou outro namespace e copie o nome exato"),
        new("CS1013", "literal numerico invalido",
            "o `_` so vai entre digitos: `100_000m` (nunca `100_m`)"),
        new("CS8124", "tupla com 1 elemento",
            "use `(int Id, decimal Valor)` com 2+ campos, ou apenas `decimal Valor` sem parenteses"),
        new("CS0111", "membro duplicado na mesma classe",
            "uma assinatura por metodo: mantenha `public Task<int> SalvarAsync(Fornecedor f) { ... }` e remova a repeticao"),
        new("CS8863", "declaracao partial/reord conflitante",
            "uma unica declaracao, sem `partial`: `public sealed record Fornecedor(int Id, string Nome);`"),
        new("CS8955", "namespace normal e file-scoped no mesmo arquivo",
            "escolha um: `namespace Cadastros;` no topo OU `namespace Cadastros { ... }`"),
        new("CS4032", "await em Main sincrono",
            "`static async Task Main(string[] args) { await ...; }`"),
        new("CS4033", "await exige metodo async",
            "`public async Task<bool> ExisteAsync(int id) { return await conn.ExecuteScalarAsync<int>(sql) > 0; }`"),
        new("CS0542", "membro com o mesmo nome do tipo",
            "renomeie um dos dois: `public sealed class Program { public static void Main() { ... } }`"),
        new("CS5001", "sem ponto de entrada",
            "uma das duas: `public static void Main()` na classe ou `<OutputType>Library</OutputType>` no `.csproj`"),
        new("CS0201", "expressao sem efeito",
            "atribua ou retorne: `var n = await conn.ExecuteScalarAsync<int>(sql);`"),
        new("CS1529", "using depois de namespace/tipo",
            "todos os `using` no topo do arquivo, antes do namespace e das classes"),
        new("CS0579", "atributo/assembly duplicado",
            "`rm -rf obj bin && dotnet build MeuProjeto.csproj`"),
        new("CS0051", "acessibilidade inconsistente no parametro",
            "o tipo do parametro e' tao publico quanto o metodo: `public interface IReembolsoRepository { }` + `public sealed class ReembolsoService(IReembolsoRepository repo) { ... }`"),
        new("MSB1008", "dotnet build recebeu arquivo .cs",
            "passe o projeto: `dotnet build MeuProjeto.csproj`"),
        new("MSB1009", "projeto/arquivo inexistente",
            "use o caminho real: `dotnet build ./MinhaPasta/MeuProjeto.csproj`"),
        new("MSB4068", "elemento <project> minusculo/atributo errado",
            "`<Project Sdk=\"Microsoft.NET.Sdk\">` com P maiusculo e Sdk como atributo"),
        new("MSB4040", "csproj sem SDK",
            "`<Project Sdk=\"Microsoft.NET.Sdk\"> ... </Project>` (Sdk no Project, sem xmlns)"),
        new("MSB4025", "tag XML do .csproj com abertura e fechamento diferentes",
            "copie o arquivo inteiro com pares idênticos (fechar ItemGroup com </ItemGroup>, "
            + "nao com </PackageReference>): "
            + "`<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
            + "<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><OutputType>Library</OutputType>"
            + "</PropertyGroup><ItemGroup><PackageReference Include=\"Dapper\" Version=\"2.1.89\" />"
            + "<PackageReference Include=\"Microsoft.Data.Sqlite\" Version=\"10.0.12\" /></ItemGroup></Project>`"),
        new("NETSDK1004", "faltam os ativos do restore",
            "`dotnet restore MeuProjeto.csproj && dotnet build MeuProjeto.csproj --no-restore`"),
        new("NU1101", "pacote nao encontrado no feed",
            "System.* do .NET nao sao pacotes; para ASP.NET use `<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />` e para ORM `Microsoft.EntityFrameworkCore`"),
        new("NU1102", "versao do pacote nao existe",
            "Microsoft.AspNetCore.* NAO sao pacotes desde o .NET Core 3: use `<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />`; para EF escolha a versao maior compativel com o TFM"),
        new("NU1202", "pacote incompativel com o TargetFramework",
            "EF Core 10 exige `net10.0`: ajuste `<TargetFramework>net10.0</TargetFramework>` ou use EF 8.x para net8.0"),
        new("NU1605", "downgrade de pacote detectado",
            "aline todas as referencias do ORM na mesma versao maior: net8.0 -> EF 8.x, net10.0 -> EF 10.x"),
        new("NU1301", "feed NuGet indisponivel (transitorio)",
            "repita `dotnet build MeuProjeto.csproj`; se persistir, e rede: `curl -I https://api.nuget.org/v3/index.json`"),
        new("CS0117", "membro inexistente no tipo",
            "chame um membro que o tipo possui: `var linhas = File.ReadAllLines(caminho);` "
            + "e `int n = int.Parse(texto);` — confirme o nome antes de chamar"),
        new("CS0120", "membro de instancia usado como estatico",
            "crie a instancia e chame: `var svc = new PedidoService(); svc.Gravar(pedido);` "
            + "ou declare `public static void Gravar(Pedido p)` quando nao ha estado de instancia"),
        new("CS0121", "chamada ambigua entre overloads",
            "dê o tipo exato ao argumento: `double valor = 1.5; Calcular(valor);` "
            + "ou nomeie o parametro: `Calcular(valor: 1.5)`"),
        new("CS0136", "nome redeclarado no mesmo escopo",
            "use nomes distintos: `int total = 0; foreach (var item in itens) { int subtotal = item.Valor; total += subtotal; }`"),
        new("CS0191", "campo somente-leitura atribuido fora do construtor",
            "atribua no construtor: `public sealed class Ponto { public int X { get; } public Ponto(int x) { X = x; } }`"),
        new("CS0841", "variavel lida antes da declaracao",
            "declare antes do uso: `int total = 0; total += 1; Console.WriteLine(total);`"),
        new("CS8600", "conversao possivelmente nula para tipo nao anulavel",
            "aceite nulo explicitamente e verifique: `string? nome = ObterNome(); if (nome is not null) Console.WriteLine(nome);`"),
        new("CS8602", "desreferencia possivelmente nula",
            "proteja com checagem: `if (pedido is not null) Console.WriteLine(pedido.Id);` "
            + "ou use o operador de propagacao: `var id = pedido?.Id;`"),
        new("CS8604", "argumento possivelmente nulo em parametro nao anulavel",
            "verifique antes de passar: `if (texto is not null) Processar(texto);` "
            + "ou aceite `string? texto` no parametro"),
        new("CS8625", "literal nulo em parametro nao anulavel",
            "passe um valor concreto ou torne o parametro anulavel: `Processar(\"\");` "
            + "ou `void Processar(string? texto)`"),
        new("CS1998", "metodo async sem await",
            "remova `async` e devolva `Task.FromResult(valor)`, ou aguarde uma operacao real: "
            + "`public async Task<string> Buscar() => await http.GetStringAsync(url);`"),
        new("CS4014", "chamada assincrona nao aguardada",
            "use await: `await GravarAsync(pedido);` — ou atribua a tarefa: `Task t = GravarAsync(pedido);`"),
        new("CS8370", "recurso de linguagem indisponivel no TargetFramework",
            "declare no .csproj: `<PropertyGroup><LangVersion>latest</LangVersion></PropertyGroup>` "
            + "ou use sintaxe equivalente mais antiga"),
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
            case "CS7036": return FixMissingArgs(source, compilerError);
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
        // CS1022 com bloco vazio solto no fim da linha (`class X { ... } { }`).
        if (code == "CS1022" && TryRepairTrailingEmptyBlock(source, compilerError, out repaired)) return true;
        if (code == "CS1022") return TryRepairMissingBrace(source, out repaired);
        // CS1003 tem duas formas: `for (x in y)` (C# nao tem for..in) e fechamento a
        // mais na linha do erro (medido no hanoi de 2026-10-31: `)` duplicado no fim
        // do `if` — o Roslyn esperava o `]` do índice seguinte).
        if (code == "CS1003")
        {
            if (TryRepairForIn(source, compilerError, out repaired)) return true;
            return TryRepairExtraClose(source, compilerError, out repaired);
        }
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
    static bool TryRepairTrailingEmptyBlock(string source, string compilerError, out string repaired)
    {
        repaired = source;
        int lineNo = SourceLineNumber(compilerError);
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        if (lineNo < 1 || lineNo > lines.Length) return false;
        string line = lines[lineNo - 1];
        int at = line.LastIndexOf("{ }", StringComparison.Ordinal);
        if (at < 0 || line[(at + 3)..].Trim().Length != 0) return false;
        lines[lineNo - 1] = line[..at].TrimEnd();
        repaired = string.Join("\n", lines);
        return repaired != source;
    }

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
    /// Detecta cabeçalho <c>for (x in y)</c>. C# não tem <c>for..in</c> — o Roslyn
    /// responde <c>CS1003: Syntax error, 'foreach' expected</c>. Um <c>for</c>
    /// legítimo sempre tem <c>;</c> no cabeçalho (início; condição; passo), então
    /// "sem <c>;</c> e com <c>in</c>" identifica a forma sem ambiguidade.
    /// </summary>
    /// <param name="line">Linha apontada pelo compilador.</param>
    /// <returns>True quando a linha é um <c>for</c> sem <c>;</c> e com <c>in</c>.</returns>
    static bool IsForInHeader(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        var m = Regex.Match(line, @"\bfor\s*\(");
        if (!m.Success) return false;
        string body = ParenBody(line, m.Index + m.Length - 1);
        if (body.Length == 0) return false;
        if (body.Contains(';')) return false;
        return Regex.IsMatch(body, @"\bin\b");
    }

    /// <summary>
    /// CS1003 na forma <c>for (x in y)</c>: a linha percorre uma coleção com a
    /// sintaxe de Python/JS. A dica mostra SÓ a forma certa, montada da própria
    /// linha (o modelo copia o literal que a dica mostra): <c>foreach</c> com a
    /// mesma cabeça. Nunca exibe o <c>for</c> errado como exemplo.
    /// </summary>
    /// <param name="line">Linha apontada pelo compilador.</param>
    /// <param name="lineNo">Número da linha (para a mensagem).</param>
    /// <returns>"ERRO: ...\nCERTO: ..." com a linha corrigida.</returns>
    static string FixForInHeader(string line, int lineNo)
    {
        var m = Regex.Match(line, @"\bfor\s*\(");
        int open = m.Index + m.Length - 1;
        string body = ParenBody(line, open);
        string corrected = line[..m.Index] + "foreach (" + body + ")" + line[(open + body.Length + 2)..];
        return "ERRO: a linha " + lineNo + " percorre uma colecao com `for` e `in`; "
             + "C# nao tem `for` com `in` (o compilador espera `foreach`).\n"
             + "CERTO: use `foreach` com a mesma cabeca:\n"
             + corrected.TrimEnd();
    }

    /// <summary>
    /// CS1003 na forma <c>for (x in y)</c>: troca o <c>for</c> por <c>foreach</c> na
    /// linha apontada. Mecânico e não ambíguo — C# não tem <c>for..in</c>, então o
    /// texto corrigido é sempre válido e o corpo do laço não é tocado.
    /// </summary>
    /// <param name="source">Fonte C# atual.</param>
    /// <param name="compilerError">Saída do build (aponta a linha do <c>for</c>).</param>
    /// <param name="repaired">Fonte com <c>foreach</c> no lugar do <c>for</c>.</param>
    /// <returns>True quando o reparo se aplica.</returns>
    static bool TryRepairForIn(string source, string compilerError, out string repaired)
    {
        repaired = source;
        int lineNo = SourceLineNumber(compilerError);
        if (lineNo < 1) return false;
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        if (lineNo > lines.Length) return false;
        string line = lines[lineNo - 1];
        if (!IsForInHeader(line)) return false;
        var m = Regex.Match(line, @"\bfor\s*\(");
        if (!m.Success) return false;
        lines[lineNo - 1] = line[..m.Index] + "foreach" + line[(m.Index + 3)..];
        string candidate = string.Join("\n", lines);
        if (candidate == source) return false;
        repaired = candidate;
        return true;
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
        // `for (x in y)` vem ANTES dos casos de fechamento: C# nao tem for..in e o
        // Roslyn responde CS1003 esperando `foreach` (medido no kmeans do L4 de
        // 2026-10-31: 617s de loop porque a dica ensinava parenteses).
        if (IsForInHeader(line)) return FixForInHeader(line, lineNo);
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

    /// <summary>
    /// CS7036 (<c>no argument given that corresponds to the required parameter</c>):
    /// a chamada passa MENOS argumentos do que a declaração exige. A dica conta os
    /// dois lados com o valor real (nós medimos: o hanoi do L4 chamou
    /// <c>SolveHanoi(4, 1, 3)</c> com declaração de 4 parâmetros). Descreve a forma
    /// certa — um valor por parâmetro, na ordem — mas nunca exibe a chamada errada
    /// nem inventa uma chamada correta com nomes fora de escopo (seria literal
    /// copiável que não compila).
    /// </summary>
    /// <param name="source">Fonte C# atual.</param>
    /// <param name="compilerError">Saída do build (aponta a linha da chamada).</param>
    /// <returns>Dica com as contagens, ou a dica genérica da tabela.</returns>
    static string FixMissingArgs(string source, string compilerError)
    {
        string line = SourceLineAt(source, compilerError);
        int lineNo = SourceLineNumber(compilerError);
        if (line.Length == 0 || lineNo < 1) return FromTable("CS7036");
        var call = Regex.Match(line, @"\b([A-Za-z_]\w*)\s*\(");
        if (!call.Success) return FromTable("CS7036");
        string name = call.Groups[1].Value;
        // Declaracao: tipo + nome + (params). Exige tipo com espaco interno para nao
        // confundir com outra chamada.
        var decl = Regex.Match(source,
            @"\b[A-Za-z_]\w*(?:<[^>]*>)?(?:\[\])?\s+" + Regex.Escape(name) + @"\s*\(([^)]*)\)");
        if (!decl.Success) return FromTable("CS7036");
        string parms = decl.Groups[1].Value.Trim();
        if (!Regex.IsMatch(parms, @"\w+\s+\w")) return FromTable("CS7036");
        var needed = SplitTopLevel(parms);
        // Conta os argumentos da chamada apontada (na linha do erro).
        string afterLine = line[(call.Index + call.Groups[1].Length)..];
        int open = afterLine.IndexOf('(');
        var given = SplitTopLevel(ParenBody(afterLine, open));
        if (needed.Count == 0) return FromTable("CS7036");
        // So' os NOMES dos parametros (mais limpo p/ o modelo que a lista com tipo).
        var paramNames = new List<string>();
        foreach (var p in needed)
        {
            var parts = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            paramNames.Add(parts[^1]);
        }
        return "ERRO: a linha " + lineNo + " chama " + name + " com " + given.Count
             + " argumento(s), mas a declaracao exige " + needed.Count
             + " parametro(s): " + string.Join(", ", paramNames) + ".\n"
             + "CERTO: cada parametro da declaracao precisa de UM argumento — passe "
             + needed.Count + " valores na chamada, na ordem da declaracao, "
             + "cada um produzido pelo seu algoritmo antes de chamar.";
    }

    /// <summary>Corpo entre parênteses que casam, a partir de <paramref name="open"/> (índice do <c>(</c>).</summary>
    /// <param name="text">Texto de onde ler.</param>
    /// <param name="open">Índice do parêntese de abertura.</param>
    /// <returns>Conteúdo interno, ou "" quando não há parêntese válido.</returns>
    static string ParenBody(string text, int open)
    {
        if (open < 0 || open >= text.Length) return "";
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0) return text[(open + 1)..i];
            }
        }
        return "";
    }

    /// <summary>
    /// Divide uma lista (parâmetros de declaração ou argumentos de chamada) nos
    /// elementos de nível mais alto, respeitando parênteses/colchetes/ângulos
    /// aninhados — <c>int[] a, Func&lt;int,int&gt; b</c> vira 2 itens.
    /// </summary>
    /// <param name="list">Lista separada por vírgulas.</param>
    /// <returns>Itens aparados, sem itens vazios.</returns>
    static List<string> SplitTopLevel(string list)
    {
        var items = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i <= list.Length; i++)
        {
            char c = i < list.Length ? list[i] : ',';
            if (c is '(' or '[' or '<') depth++;
            else if (c is ')' or ']' or '>') depth--;
            else if (c == ',' && depth == 0)
            {
                string item = list[start..i].Trim();
                if (item.Length > 0) items.Add(item);
                start = i + 1;
            }
        }
        return items;
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
        // O Python do tunel puro lê QUALQUER código de diagnóstico do build
        // (CS, MSB, NETSDK, NU). Limitava-se a CS e por isso MSB1011 — a causa
        // de 66/71 falhas na tabela mestra — nunca chegava à dica in-flow.
        var m = Regex.Match(compilerError, @"(?:error|warning)\s+((?:CS|MSB|NETSDK|NU)\d{4})");
        return m.Success ? m.Groups[1].Value : "";
    }
}
