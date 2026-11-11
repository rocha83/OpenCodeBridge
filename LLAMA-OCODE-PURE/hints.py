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
import json
import os
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

# --- contrato minimo dos args de tool (espelha o schema do opencode) ---
# Fonte unica, como ToolHelp/ToolCallRepair da bridge C#: o collector valida a
# call ANTERIOR no request e devolve UM aviso com a forma que passa.
TOOL_REQUIRED = {
    "write": ("filePath", "content"),
    "edit": ("filePath", "oldString", "newString"),
    "read": ("filePath",),
    "shell": ("command",),
}
TOOL_ALIAS = {  # como o modelo costuma escrever -> nome do schema
    "path": "filePath", "file": "filePath", "file_path": "filePath",
    "filename": "filePath", "old_string": "oldString", "old_text": "oldString",
    "new_string": "newString", "new_text": "newString",
    "replace_all": "replaceAll", "cmd": "command", "command_line": "command",
}
TOOL_EXAMPLE = {  # exemplo OTIMISTA: a chamada inteira, pronta para reenviar
    "write": ('{"filePath":"src/Dominio/Cliente.cs","content":"namespace Cadastros.Dominio;\\n\\n'
              'public sealed class Cliente { public int Id { get; init; } }"}'),
    "edit": ('{"filePath":"src/Dominio/Cliente.cs","oldString":"public sealed class Cliente { }",'
             '"newString":"public sealed class Cliente { public int Id { get; init; } }"}'),
    "read": '{"filePath":"src/Dominio/Cliente.cs"}',
    "shell": '{"command":"dotnet build Cadastros.csproj"}',
}


def tool_call_problem(name, args):
    """Valida os args de UMA tool call. (kind, problema, exemplo) ou None se ok.

    O `args` pode vir dict (opencode) ou string JSON (formato OpenAI/bridge).
    Cobre o que o 4B erra de fato: JSON cortado, `<tool_call>` colado no texto,
    nome de campo fora do schema (`path`/`old_string`) e campo obrigatorio ausente.
    """
    name = (name or "").strip().lower()
    req = TOOL_REQUIRED.get(name)
    if not req:
        return None
    obj = args
    if isinstance(obj, str):
        if "<tool_call" in obj:
            return ("colado", "o argumento veio com <tool_call> colado no texto", TOOL_EXAMPLE[name])
        try:
            obj = json.loads(obj)
        except ValueError:
            return ("json", "o JSON veio invalido/truncado (chaves e aspas precisam fechar)",
                    TOOL_EXAMPLE[name])
    if not isinstance(obj, dict):
        return ("tipo", "os args precisam ser um objeto JSON {campo: valor}", TOOL_EXAMPLE[name])
    if any("<tool_call" in str(v) for v in obj.values()):
        return ("colado", "um campo veio com <tool_call> colado no texto", TOOL_EXAMPLE[name])
    al = {k: TOOL_ALIAS[k] for k in obj if k in TOOL_ALIAS and TOOL_ALIAS[k] in req}
    if al:
        return ("alias", "campo com nome fora do schema: use " + ", ".join(
            "%s (nao %s)" % (v, k) for k, v in al.items()), TOOL_EXAMPLE[name])
    falta = [k for k in req if k not in obj]
    if falta:
        return ("faltando", "faltam os campos obrigatorios: " + ", ".join(falta),
                TOOL_EXAMPLE[name])
    return None


# modelo canonico de projeto: o harness nunca roda `dotnet new`, o modelo escreve este arquivo
CSPROJ_MIN = """      <Project Sdk=\"Microsoft.NET.Sdk\">
        <PropertyGroup>
          <TargetFramework>net10.0</TargetFramework>
          <ImplicitUsings>enable</ImplicitUsings>
          <Nullable>enable</Nullable>
        </PropertyGroup>
      </Project>
      (rode dotnet build no diretorio do .csproj; so acrescente <OutputType>Exe</OutputType> se o
      enunciado pedir dotnet run, e PackageReference apenas do que o enunciado exigir)"""

CSPROJ_CODES = {"MSB4025", "MSB1003", "MSB1009", "MSB4068", "MSB4040", "NETSDK1004",
                "NETSDK1045", "NU1202", "NU1605", "NU1101", "NU1102", "NU1603"}


def hint_for(code):
    """switch hardcode: (descricao curta, exemplo que compila) para o codigo."""
    code = (code or "").strip().upper()
    if code == "CS0080":
        return ("clausula where em algo nao generico",
                "where T : ... so vale em tipo/metodo generico: declare class Repositorio<T> onde T : class "
                "ou apague a clausula where do tipo/metodo nao generico")
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
                "3 causas comuns: (a) Ok/BadRequest/NotFound = ControllerBase do MVC -> use "
                "Results.Ok(...)/Results.NotFound() (Minimal API) ou herde ControllerBase; "
                "(b) QueryAsync/ExecuteAsync = extensao do Dapper -> using Dapper; e conn tipado "
                "IDbConnection; (c) QueryAllAsync/QueryListAsync nao existem no Dapper -> use "
                "conn.QueryAsync<T>(sql, p)")
    if code == "CS1061":
        return ("membro nao existe no tipo (ou faltou o using)",
                "ou o tipo nao tem esse membro, ou falta o using do ORM escolhido: adicione "
                "using <Namespace do seu ORM>; e chame o metodo no tipo certo")
    if code == "CS1674":
        return ("IEnumerable<T> nao e IDisposable",
                "var lista = await conn.QueryAsync<Reembolso>(sql); foreach (var r in lista) { ... } "
                "(using var so para conexao/reader/transacao)")
    if code == "CS1929":
        return ("tipo generico aplicado em metodo que nao e generico",
                "so o QueryAsync do Dapper e generico e so existe em IDbConnection: "
                "conn.QueryAsync<Reembolso>(sql, parametros). No SEU repositorio chame sem <T>: "
                "await repositorio.QueryAsync(sql, parametros);")
    if code == "CS1501":
        return ("nenhuma sobrecarga aceita essa quantidade de argumentos",
                "copie a assinatura declarada: Task<IEnumerable<Reembolso>> QueryAsync(string sql, object? p) "
                "-- a chamada tem que passar exatamente (string, object?)")
    if code == "CS0411":
        return ("argumentos de tipo nao puderam ser inferidos",
                "passe o tipo explicito na chamada generica: await conn.QueryAsync<Reembolso>(sql, p); "
                "e confira a assinatura do SEU metodo: Task<IEnumerable<T>> QueryAsync<T>(string sql, object? p) "
                "-- ao chamar o seu, sem <T>: await repositorio.QueryAsync(sql, p)")
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
                "mantenha exatamente UMA linha 'namespace X;' no topo, com todos os tipos abaixo; "
                "NUNCA apague o namespace: remova so a linha repetida")
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
    if code == "CS1056":
        return ("caractere inesperado no fonte",
                "voce colou cerca de markdown dentro do .cs: apague as linhas com ``` e qualquer texto "
                "fora de codigo; o arquivo tem que conter apenas C#")
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
    if code == "CS0266":
        return ("conversao implicita nao permitida",
                "o valor nao cabe no tipo destino: converta explicitamente ((decimal)x, (int)x, (long)x) "
                "ou declare a variavel com o tipo que a expressao devolve")
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
    if code == "MSB1003":
        return ("dotnet build sem projeto no diretorio atual",
                "rode no diretorio do .csproj: dotnet build MeuProjeto.csproj (ou cd na pasta dele). "
                "Se nao existir .csproj, crie um unico na raiz, com nome")
    if code == "MSB1009":
        return ("projeto/arquivo inexistente",
                "use o caminho real: dotnet build ./MinhaPasta/MeuProjeto.csproj")
    if code == "MSB4025":
        return ("csproj nao e XML de projeto",
                "reescreva o arquivo inteiro: <Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")
    if code == "MSB4068":
        return ("elemento <project> minusculo/atributo errado",
                "<Project Sdk=\"Microsoft.NET.Sdk\"> (P maiusculo, Sdk como atributo)")
    if code == "MSB4040":
        return ("csproj sem SDK",
                "<Project Sdk=\"Microsoft.NET.Sdk\"> ... </Project> (Sdk no Project, sem xmlns)")
    if code == "NETSDK1004":
        return ("faltam os ativos do restore (project.assets.json)",
                "rode dotnet restore antes do build: dotnet restore MeuProjeto.csproj && dotnet build "
                "MeuProjeto.csproj --no-restore (ou dotnet build sem --no-restore)")
    if code == "NETSDK1045":
        return ("SDK nao suporta o alvo",
                "use <TargetFramework>net10.0</TargetFramework> (SDK 10 instalado; net8.0 so com o targeting pack)")
    if code == "NU1101":
        return ("pacote nao encontrado no feed",
                "esse ID de pacote nao existe: System.* do .NET nao sao pacotes (DataAnnotations/ComponentModel "
                "vem do framework). Para ASP.NET use <FrameworkReference Include=\"Microsoft.AspNetCore.App\" /> "
                "e para ORM use Microsoft.EntityFrameworkCore (+ .SqlServer ou .Sqlite)")
    if code == "NU1102":
        return ("versao do pacote nao existe",
                "Microsoft.AspNetCore.* NAO sao pacotes desde o .NET Core 3: no net8.0/net10.0 vem do SDK. "
                "Remova a PackageReference de Microsoft.AspNetCore.Http/App e, se precisar de ASP.NET numa "
                "biblioteca, use <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />. "
                "Para EF escolha a versao maior compativel com o TFM")
    if code == "NU1202":
        return ("pacote incompativel com o TargetFramework do projeto",
                "EF Core 10 exige net10.0: ou ajuste <TargetFramework>net10.0</TargetFramework> no .csproj, "
                "ou use a versao do pacote para o seu TFM (EF Core 8.x para net8.0). Nao misture net8.0 com "
                "pacote 10.x")
    if code == "NU1605":
        return ("downgrade de pacote detectado",
                "um pacote transitivo exige versao maior: alinhe TODAS as referencias do ORM na mesma "
                "versao maior compativel com o TFM (net8.0 -> EF 8.x, net10.0 -> EF 10.x); nao misture "
                "8.x/9.x/10.x e remova a referencia que nao usa")
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


def brace_report(errors_text, limit=2):
    """Contagem deterministica de chaves nos arquivos citados no erro.

    O 4B le "feche um } por bloco" e nao conta; aqui vai o numero exato do arquivo.
    """
    out, seen = [], set()
    for where, _code, _msg in error_lines(errors_text, 12):
        m = re.match(r"(.+?)\(\d+,\d+\)$", where)
        if not m:
            continue
        path = m.group(1)
        if path in seen or not os.path.isfile(path):
            continue
        seen.add(path)
        try:
            src = open(path, encoding="utf-8", errors="replace").read()
        except OSError:
            continue
        op, cl = src.count("{"), src.count("}")
        if op == cl:
            continue
        falta = ("falta %d }" % (op - cl)) if op > cl else ("sobram %d }" % (cl - op))
        out.append("  chaves %s: %d '{' e %d '}' -> %s" % (os.path.basename(path), op, cl, falta))
        if len(out) >= limit:
            break
    return "\n".join(out)


def render(errors_text, max_codes=6):
    """Linhas '  CODIGO  descricao: exemplo' para os codigos presentes no texto."""
    codes = codes_in(errors_text, max_codes)
    if not codes:
        return ""
    rows = []
    for c in codes:
        desc, ex = hint_for(c)
        rows.append("  %-9s %s: %s" % (c, desc, ex))
    br = brace_report(errors_text)
    if br:
        rows.append(br)
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
    # cada linha SEMPRE leva arquivo:linha + erro + o exemplo que compila
    texts = []
    for _where, _code, _msg in lines:
        _desc, _ex = hint_for(_code)
        texts.append(_where + ": error " + _code + ": " + _msg +
                     "\n      corrigir: " + _desc + " -> " + _ex)
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
    br = brace_report(errors_text)
    if br:
        ops.extend("  -" + ln.strip(" ")[0:] if False else "  - " + ln.strip() for ln in br.splitlines())
    if ops:
        body += "\n" + "\n".join(ops[:5])
    # problema no arquivo de projeto: ensina o csproj valido (nada de dotnet new)
    if any(ln[1] in CSPROJ_CODES for ln in all_lines) or "csproj" in (errors_text or "").lower():
        body += "\n  - csproj minimo que compila (escreva com write, nao use dotnet new):\n" + CSPROJ_MIN
    return ("[build-hint] o ultimo build falhou. Corrija EXATAMENTE estes erros e so entao rode "
            "dotnet build de novo na pasta do .csproj (nao crie outro .csproj e nao repita o comando sem editar o arquivo):\n" + body)


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
