#!/usr/bin/env python3
"""Tabela de apoio a compilacao: codigo -> descricao curta + exemplo que COMPILA.

Fonte unica dos hints (antes era um case gigante dentro do battery-war.sh):

  * collector.py usa `hint_for`/`alert` para devolver ao modelo um alerta pontual
    de retry quando o ultimo tool result traz erro de compilacao;
  * battery-war.sh usa a CLI para imprimir SO os codigos que apareceram no build
    (`printf '%s' "$errs" | python3 hints.py -`).

Os exemplos sao otimistas: mostram o trecho correto, pronto para colar no arquivo,
sem repetir o enunciado nem o resultado esperado do teste.

Uso:
  python3 hints.py CS0246 CS0051     # hints dos codigos dados
  python3 hints.py -                 # le erros do stdin e infere os codigos
  python3 hints.py --codes -         # so os codigos encontrados
  python3 hints.py --alert -         # alerta compacto pronto para injetar
"""
import re
import sys

# codigo do compilador/NuGet/SDK no meio da linha de erro
CODE_RE = re.compile(r"(?<![A-Za-z0-9])((?:CS|MSB|NETSDK|NU)[0-9]{4,5})(?![0-9])")
# linha de erro do dotnet: caminho(linha,col): error CSxxxx: mensagem
ERR_LINE_RE = re.compile(
    r"^(?P<where>[^:\n]*?\(\d+,\d+\)|[^:\n]*?\.(?:cs|csproj|sql))"
    r"\s*:\s*(?:error|erro)\s+(?P<code>(?:CS|MSB|NETSDK|NU)[0-9]{4,5})"
    r"\s*:\s*(?P<msg>.+?)(?:\s*\[[^\]]+\])?\s*$",
    re.IGNORECASE,
)
# erro conhecido do protocolo de tool: args quebrados (texto com <tool_call>, schema errado)
TOOL_ARG_ERR = re.compile(r'Invalid arguments for tool \\?"([A-Za-z_]+)', re.IGNORECASE)


def hint_for(code):
    """switch hardcode: (descricao curta, exemplo que compila) para o codigo."""
    code = (code or "").strip().upper()
    if code == "CS0246":
        return ("tipo/interface nao encontrado neste arquivo",
                "se o tipo existe em outro namespace: using Cadastros.Dominio; no topo (nome exato do "
                "namespace do tipo); se nao existe: declare no mesmo .csproj -> "
                "public interface IClienteRepository { Task<Cliente?> ObterAsync(int id); } "
                "public sealed class Cliente { ... }")
    if code == "CS0234":
        return ("namespace/assembly ausente",
                "duas causas: pai NamespaceA.NamespaceB = namespace declarado 2x no arquivo -> deixe "
                "namespace X; uma vez e use using Cadastros.Dominio; para tipo de outra camada; "
                "AspNetCore/Extensions ausente = <FrameworkReference Include=\"Microsoft.AspNetCore.App\" /> no .csproj")
    if code == "CS0122":
        return ("membro inacessivel por nivel de protecao",
                "use o membro public/internal compativel: public sealed class Cliente { public int Id { get; init; } }")
    if code == "CS0103":
        return ("nome nao existe no escopo",
                "QueryAsync e extensao de IDbConnection: using Dapper; "
                "using var conn = new SqliteConnection(cs); var lista = await conn.QueryAsync<T>(sql);")
    if code == "CS1061":
        return ("membro nao existe no tipo",
                "o metodo e extensao do Dapper: using Dapper; + PackageReference Dapper 2.1.89; "
                "chame conn.QueryAsync<T>(sql) com conn tipado como IDbConnection")
    if code == "CS1674":
        return ("IEnumerable<T> nao e IDisposable",
                "var lista = await conn.QueryAsync<Reembolso>(sql); foreach (var r in lista) { ... } "
                "(using var so para conexao/reader/transacao)")
    if code == "CS1013":
        return ("literal numerico invalido",
                "o _ so vai entre digitos: 100_000m (nao 100_m)")
    if code == "CS8124":
        return ("tupla com 1 elemento",
                "use (int Id, decimal Valor) com 2+ campos, ou so decimal Valor sem parenteses")
    if code == "CS1022":
        return ("bloco/namespace sem fechar",
                "feche um } por bloco aberto: public sealed class Fornecedor { } no fim do arquivo")
    if code == "CS0101":
        return ("tipo declarado duas vezes",
                "declarar UMA vez: mantenha public sealed class Fornecedor { ... } e apague a "
                "segunda definicao (ou use o tipo ja existente)")
    if code == "CS0111":
        return ("membro duplicado na mesma classe",
                "uma assinatura por metodo: mantenha public Task<int> SalvarAsync(Fornecedor f) { ... } "
                "e remova a repeticao")
    if code == "CS8863":
        return ("partial/reord record conflitante",
                "uma unica declaracao, sem partial e sem parametros repetidos: "
                "public sealed record Fornecedor(int Id, string Nome);")
    if code == "CS8803":
        return ("top-level statements antes de namespace",
                "namespace sempre antes ou codigo dentro de class Program: "
                "namespace Cadastros; public static class Program { static void Main() { ... } }")
    if code == "CS8954":
        return ("namespace declarado duas vezes no arquivo",
                "namespace file-scoped UMA vez, na primeira linha: namespace Cadastros; "
                "sem repetir a linha e sem segundo namespace no mesmo arquivo")
    if code == "CS8955":
        return ("namespace normal + file-scoped no mesmo arquivo",
                "escolha um: namespace Cadastros; no topo (file-scoped) OU namespace Cadastros { ... }")
    if code == "CS4032":
        return ("await em Main sincrono",
                "static async Task Main(string[] args) { await ...; }")
    if code == "CS4033":
        return ("await exige async",
                "public async Task<bool> ExisteAsync(int id) { return await conn.ExecuteScalarAsync<int>(sql) > 0; }")
    if code == "CS0542":
        return ("membro com o mesmo nome do tipo",
                "renomeie um dos dois: public sealed class Program { public static void Main() { ... } }")
    if code == "CS1525":
        return ("parentese/operador fora de lugar",
                "return (await conn.ExecuteScalarAsync<int>(sql)) > 0;")
    if code == "CS1026":
        return ("falta fechar a chamada",
                "return (await conn.ExecuteScalarAsync<int>(sql)) > 0;")
    if code == "CS1002":
        return ("falta ponto e virgula",
                "var total = await conn.ExecuteScalarAsync<int>(sql);")
    if code == "CS1513":
        return ("falta fechar bloco",
                "feche metodo e classe: public void F() { ... } } no fim do arquivo")
    if code == "CS1519":
        return ("token/metodo fora da classe",
                "metodo sempre dentro de um tipo: public sealed class Program { static async Task Buscar() { ... } }")
    if code == "CS1529":
        return ("using depois de namespace/tipo",
                "todos os using no topo do arquivo, antes do namespace e das classes")
    if code == "CS0579":
        return ("atributo/assembly duplicado",
                "apague obj e bin e rode de novo: rm -rf obj bin && dotnet build MeuProjeto.csproj")
    if code == "CS5001":
        return ("sem ponto de entrada",
                "uma das duas: public static void Main() na classe, ou "
                "<OutputType>Library</OutputType> no .csproj")
    if code == "CS0201":
        return ("expressao sem efeito",
                "atribua ou retorne: var n = await conn.ExecuteScalarAsync<int>(sql);")
    if code == "CS0116":
        return ("membro solto fora de tipo",
                "sem top-level statements com namespace: use public sealed class Program { static void Main() { ... } }")
    if code == "CS0161":
        return ("nem todo caminho retorna",
                "feche com return default; ou throw new InvalidOperationException();")
    if code == "CS0535":
        return ("classe nao implementa a interface",
                "implemente todos os membros da interface (ou remova a interface da declaracao)")
    if code == "CS0051":
        return ("acessibilidade inconsistente no parametro",
                "tipo do parametro tao publico quanto o metodo: public interface IReembolsoRepository { } "
                "+ public sealed class ReembolsoService(IReembolsoRepository repo) { ... }")
    if code == "CS0029":
        return ("tipos incompativeis na atribuicao",
                "converta no ponto do erro: int id = Convert.ToInt32(await conn.ExecuteScalarAsync<long>(sql));")
    if code == "CS0165":
        return ("variavel usada sem atribuicao",
                "atribua antes do uso: string nome = string.Empty; nome = await LerAsync();")
    if code == "CS7036":
        return ("argumento obrigatorio ausente",
                "passe os parametros do construtor/metodo: new ReembolsoService(repo, logger)")
    if code == "MSB1008":
        return ("dotnet build recebeu arquivo .cs",
                "passe o projeto: dotnet build MeuProjeto.csproj (ou dotnet build na pasta dele)")
    if code == "MSB1009":
        return ("projeto/arquivo inexistente",
                "use o caminho real: dotnet build ./MinhaPasta/MeuProjeto.csproj")
    if code == "MSB4025":
        return ("csproj nao e XML de projeto",
                "reescreva o arquivo inteiro: <Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                "<TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>")
    if code == "MSB4068":
        return ("elemento <project> minusculo/atributo errado",
                "<Project Sdk=\"Microsoft.NET.Sdk\"> (P maiusculo, Sdk como atributo)")
    if code == "MSB4040":
        return ("csproj sem SDK",
                "<Project Sdk=\"Microsoft.NET.Sdk\"> ... </Project> (Sdk no Project, sem xmlns)")
    if code == "NETSDK1045":
        return ("SDK nao suporta o alvo",
                "use <TargetFramework>net8.0</TargetFramework> (ou o alvo que o SDK instalado suporta)")
    if code == "NU1101":
        return ("pacote nao encontrado no feed",
                "nome/versao reais: dotnet add package Dapper --version 2.1.89 "
                "(Microsoft.Data.Sqlite 10.0.12, Microsoft.Data.SqlClient 5.1.5)")
    if code == "NU1102":
        return ("versao do pacote nao existe",
                "ASP.NET no .NET 8+ e framework: <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />; "
                "Microsoft.Data.SqlClient 5.1.5 (nao 8.0.4)")
    if code == "NU1605":
        return ("downgrade de pacote",
                "alinhe a versao ao target: <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" Version=\"9.0.0\" />")
    if code == "NU1301":
        return ("feed NuGet indisponivel (transitorio)",
                "repita depois de corrigir: dotnet build MeuProjeto.csproj; se persistir, "
                "curl -I https://api.nuget.org/v3/index.json -- e rede, nao codigo")
    return ("erro de compilacao apontado pelo compilador",
            "abra o arquivo na linha:coluna exata do erro e corrija ali; rode dotnet build de novo "
            "so depois de editar (nao repita o mesmo comando sem mudar o arquivo)")


def codes_in(text, limit=6):
    """Codigos distintos na ordem em que aparecem."""
    out = []
    for m in CODE_RE.finditer(text or ""):
        code = m.group(1)
        if code not in out:
            out.append(code)
        if len(out) >= limit:
            break
    return out


def error_lines(text, limit=3):
    """(where, code, msg) das linhas de erro reais do dotnet, sem duplicar."""
    out, seen = [], set()
    for line in (text or "").splitlines():
        m = ERR_LINE_RE.match(line.strip())
        if not m:
            continue
        key = (m.group("code"), m.group("where"), m.group("msg")[:60])
        if key in seen:
            continue
        seen.add(key)
        out.append((m.group("where"), m.group("code"), m.group("msg")[:180]))
        if len(out) >= limit:
            break
    return out


def render(errors_text, max_codes=6):
    """Linhas '  CODIGO  descricao: exemplo' para os codigos presentes no texto."""
    codes = codes_in(errors_text, max_codes)
    if not codes:
        return ""
    rows = []
    for c in codes:
        desc, ex = hint_for(c)
        rows.append("  %-9s %s: %s" % (c, desc, ex))
    return "\n".join(rows)


def alert(errors_text, max_codes=3):
    """Alerta compacto de retry: o erro exato + o exemplo que compila."""
    all_lines = error_lines(errors_text, 12)
    tool_err = TOOL_ARG_ERR.search(errors_text or "")
    # uma linha por (codigo,arquivo): 4 CS8954 do mesmo arquivo viram 1 exemplo
    uniq, seen_keys = [], set()
    for where, code, msg in all_lines:
        m = re.match(r"(.+?)\(\d+,\d+\)", where)
        key = (code, m.group(1) if m else where)
        if key in seen_keys:
            continue
        seen_keys.add(key)
        uniq.append((where, code, msg))
    lines = uniq[:max_codes]
    texts = [ln[0] + ": error " + ln[1] + ": " + ln[2] for ln in lines]
    if tool_err and not texts:
        texts.append('tool.execution: "Invalid arguments for tool \\"%s\\"" (os args chegaram '
                     'quebrados; o texto nao pode conter <tool_call>)' % tool_err.group(1))
    if not texts:
        for c in codes_in(errors_text, max_codes):
            desc, ex = hint_for(c)
            texts.append(c + ": " + desc + " -> " + ex)
    elif len(texts) < max_codes:
        have = {ln[1] for ln in lines}
        for c in codes_in(errors_text, max_codes):
            if c in have or len(texts) >= max_codes:
                continue
            desc, ex = hint_for(c)
            texts.append(c + ": " + desc + " -> " + ex)
    if not texts:
        return ""
    body = "\n".join("  - " + t for t in texts)
    # acao objetiva: CS8954 repete a MESMA linha de namespace -> apaga todas de uma vez
    ns_lines, dups, ops = {}, [], []
    for where, code, _msg in all_lines:
        m = re.match(r"(.+?)\((\d+),\d+\)", where)
        if not m:
            continue
        arq, ln = m.group(1), int(m.group(2))
        if code == "CS8954":
            ns_lines.setdefault(arq, [])
            if ln not in ns_lines[arq]:
                ns_lines[arq].append(ln)
        elif code in ("CS0101", "CS0111", "CS8863"):
            dups.append("  - apague a segunda declaracao em %s:%s (nao reescreva o arquivo)" % (arq, ln))
    for arq, lns in list(ns_lines.items())[:2]:
        sel = ";".join("%dd" % n for n in sorted(lns))
        ops.append("  - apague as linhas de namespace repetido: sed -i '%s' %s" % (sel, arq))
    ops.extend(dups[:2])
    # erro conhecido do protocolo de tool (args invalidos): o texto veio quebrado
    for m in TOOL_ARG_ERR.finditer(errors_text or ""):
        tool = (m.group(1) or "").lower()
        if tool == "edit":
            ops.append("  - edit exige {filePath, oldString, newString} e o texto NAO pode conter "
                       "<tool_call>; se o trecho nao casa, use write com o arquivo inteiro")
        elif tool == "write":
            ops.append("  - write exige {filePath, content} completos (sem <tool_call> no conteudo)")
        elif tool == "shell":
            ops.append("  - shell exige {command:\"...\"} com UM comando por chamada")
        else:
            ops.append("  - args do tool %s invalidos: repita a chamada com o schema {filePath,...}" % tool)
        break
    if ops:
        body += "\n" + "\n".join(ops[:4])
    return ("[build-hint] o ultimo build falhou. Corrija EXATAMENTE estes erros e so entao rode "
            "dotnet build Nome.csproj de novo (nao repita o comando sem editar o arquivo):\n" + body)


def _main(argv):
    args = [a for a in argv[1:] if a]
    if not args:
        print(__doc__.strip().splitlines()[0])
        return 0
    mode = "render"
    if args[0] in ("--codes", "--alert", "--hint"):
        mode, args = args[0], args[1:]
    text = ""
    if not args or args == ["-"]:
        text = sys.stdin.read()
        args = []
    if args and all(CODE_RE.fullmatch(a.strip().upper()) for a in args):
        text = "\n".join(args)
    if mode == "--codes":
        print("\n".join(codes_in(text)))
        return 0
    if mode == "--alert":
        msg = alert(text)
        if msg:
            print(msg)
        return 0
    if mode == "--hint":
        for c in codes_in(text):
            desc, ex = hint_for(c)
            print("%s\t%s\t%s" % (c, desc, ex))
        return 0
    out = render(text)
    if out:
        print(out)
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv))
