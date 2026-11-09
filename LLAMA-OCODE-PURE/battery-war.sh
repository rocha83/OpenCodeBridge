#!/usr/bin/env bash
# Bateria WAR pelo TUNEL: opencode -> collector :4199 -> llama :4100.
# So enunciados: nenhuma dependencia de harness externo.
#   tools : casos que EXIGEM ferramenta (tools-full, mission-tools, belts azul/verde)
#           -> agent 'tools', permissao completa, prompt original do CLI.
#   ent   : sequencia Plan de conhecimento (produto/customer/saleinvoice/worker/
#           redis/bus/erp/dashboard) em TEXTO puro, sem tool -> agent 'plan'.
# Uso: ./battery-war.sh <chat|responses> <tools|ent> [caso ...]
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PROTO="${1:-chat}"; MODO="${2:-tools}"
[ $# -ge 2 ] && shift 2 || shift $#
CASES=("$@")
ROOT="/tmp/opencode/llama-pure"
WS="$ROOT/war-$MODO-$PROTO"
LLAMA="${LLAMA:-http://127.0.0.1:4199}"
MODEL="llamapure/Gemma4-E4B-GK-Q4"                 # plan: thinking ON, temp 0.2
MODEL_BUILD="llamapure/Gemma4-E4B-GK-Q4-nothink"  # build/tools: thinking OFF, temp 0.1
LOG="$ROOT/war-$MODO-$PROTO.log"
START=$(date +%s)
# Observer em memoria: zera a rodada no coletor e so grava no fim (dump).
COLLECTOR="${COLLECTOR:-http://127.0.0.1:4199}"
# exclusao de fontes geradas: projeto na raiz que varre **/*.cs nao pode compilar obj/bin de projeto aninhado
DOTNET_SRC_EXCLUDE=(-p:DefaultItemExcludes='obj/**%3Bbin/**%3B**/obj/**%3B**/bin/**')
curl -s -m 3 -X POST "$COLLECTOR/api/reset" >/dev/null 2>&1 || true
pass=0; fail=0
export GIT_AUTHOR_NAME="war" GIT_AUTHOR_EMAIL="war@local"
export GIT_COMMITTER_NAME="war" GIT_COMMITTER_EMAIL="war@local"
CASE_DIR="$WS"

AGENTS_FILE="${HOME}/.config/opencode/AGENTS.md"
restore_agents() { [ -f "${AGENTS_FILE}.battery-bak" ] && mv "${AGENTS_FILE}.battery-bak" "$AGENTS_FILE"; }
if [ -f "$AGENTS_FILE" ]; then mv "$AGENTS_FILE" "${AGENTS_FILE}.battery-bak"; fi
tempo_final() { [ -n "${CASE_NAME:-}" ] && tempo_caso "$CASE_NAME"; }
trap 'restore_agents; tempo_final' EXIT INT TERM

case "$PROTO" in
  chat)      PKG="@opencode/ai/providers/openai-compatible" ;;
  responses) PKG="@opencode/ai/providers/openai" ;;
  *) echo "protocolo invalido: $PROTO (chat|responses)" >&2; exit 2 ;;
esac
case "$MODO" in tools|ent) ;; *) echo "modo invalido: $MODO (tools|ent)" >&2; exit 2 ;; esac

rm -rf "$WS"; mkdir -p "$WS"

cat > "$WS/opencode.json" <<JSON
{
  "\$schema": "https://opencode.ai/config.json",
  "experimental": { "subagent_depth": 3 },
  "providers": {
    "llamapure": {
      "name": "Llama pure",
      "package": "$PKG",
      "settings": { "baseURL": "$LLAMA/v1", "apiKey": "guest@123" },
      "models": {
        "Gemma4-E4B-GK-Q4": {
          "name": "Gemma4 E4B GK (plan: thinking on, temp 0.2)",
          "tool_call": true,
          "body": { "temperature": 0.2, "chat_template_kwargs": { "enable_thinking": true } },
          "limit": { "context": 131072, "output": 16384 }
        },
        "Gemma4-E4B-GK-Q4-nothink": {
          "name": "Gemma4 E4B GK (build: thinking off, temp 0.1)",
          "tool_call": true,
          "body": { "temperature": 0.1, "chat_template_kwargs": { "enable_thinking": false } },
          "limit": { "context": 131072, "output": 16384 }
        }
      }
    }
  },
  "model": "$MODEL",
  "agents": {
    "tools": {
      "description": "Trilha build: permissao completa, prompt original do CLI",
      "mode": "primary",
      "model": "$MODEL_BUILD",
      "steps": 60,
      "permissions": [ { "action": "question", "resource": "*", "effect": "deny" } ]
    },
    "build": {
      "description": "Build: executa micro-enunciados com tools, thinking off temp 0.1",
      "mode": "all",
      "model": "$MODEL_BUILD",
      "steps": 80,
      "system": "Voce e o Build. Execute o enunciado no diretorio atual com tools, sem narrar plano. Um .csproj valido comeca exatamente com <Project Sdk='Microsoft.NET.Sdk'> e termina com </Project>; nao use xmlns 2003, nao escreva XML dentro de .cs e nunca passe .cs ao dotnet build. Se o .csproj estiver quebrado, reescreva o arquivo inteiro com a tool write (nao tente edit parcial). Antes de concluir, rode dotnet build no .csproj e so responda quando rc=0; se falhar, leia o erro e corrija. Versoes do cache NuGet local: Dapper 2.1.89, Microsoft.Data.Sqlite 10.0.12, Microsoft.Data.SqlClient 5.1.5, System.Text.Json 8.0.4. Nunca use Microsoft.Data.SqlClient 8.x (NU1102): use 5.1.5 ou remova a dependencia. Se CS1061 em SqliteConnection.QueryAsync, adicione using Dapper; no topo do arquivo e edite antes de recompilar; nao repita dotnet build sem alterar o arquivo. Nao pare na primeira etapa nem com um build verde de projeto vazio/parcial: entregue todos os artefatos do escopo (dominio, SQL, repositorios, servicos, API, bus e ML quando pedido) e so conclua com o build rc=0 e os arquivos no disco.",
      "permissions": [ { "action": "question", "resource": "*", "effect": "deny" } ]
    },
    "scripter": {
      "description": "Scripter: compoe scripts shell/bash e construtores de artefatos; valida com bash -n, nao executa",
      "mode": "all",
      "model": "$MODEL_BUILD",
      "steps": 60,
      "system": "Voce e o Scripter. Escreva os arquivos de artefato pedidos, completos e sem placeholder: C# em .cs, DDL em .sql, projeto em .csproj. Cada tipo tem seu verificador: .cs/.sql/.csproj se verificam com 'dotnet build' (o build acusa o erro com arquivo e linha); 'bash -n arquivo.sh' vale apenas para script .sh com shebang (#!/usr/bin/env bash). Exemplo de .csproj que compila: <Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>. Dois projetos que se usam entram como <ProjectReference Include='../Dominio/Dominio.csproj' />. Pacotes com versao que existe: Dapper 2.1.89, Microsoft.Data.Sqlite 10.0.12; ASP.NET Core em .NET 8+ entra como <FrameworkReference Include='Microsoft.AspNetCore.App' />. Todo arquivo .cs precisa de pelo menos uma declaracao de tipo (class, interface, record ou enum): nunca entregue um .cs so com using; se o tipo ja existe em outro arquivo, nao duplique. Responda so com tool calls.",
      "permissions": [ { "action": "question", "resource": "*", "effect": "deny" } ]
    },
    "orch": {
      "description": "Orch: segmenta e delega ao Scripter pela tool subagent, thinking on temp 0.2",
      "mode": "primary",
      "model": "$MODEL",
      "steps": 80,
      "system": "Voce e o Orch: divida o enunciado em tarefas menores (max 5) e delegue cada uma ao Scripter chamando a tool subagent com agent \"scripter\". Exemplo: subagent(agent=\"scripter\", description=\"dominio e DDL\", prompt=\"OBJETIVO: dominio POCO e DDL; ESCOPO: Reembolso.cs e db.sql; ACEITE: dotnet build Reembolsos.csproj ate 0 erro(s)\"). Regra do projeto unico: existe UM so .csproj na raiz (Reembolsos.csproj) e todos os micro-enunciados repetem esse caminho; o Scripter entrega um script shell com shebang que materializa os arquivos. Depois agregue os resultados e confira o aceite de cada um. Arquivos exclusivos: cada micro-enunciado lista os arquivos que pode criar e nenhuma classe pode existir em dois arquivos; um unico .csproj na raiz, sem subprojeto. Se aparecer CS0101 (classe duplicada) ou um segundo .csproj, consolide antes de concluir; rode dotnet build no .csproj da raiz e so conclua com rc=0. Nao encerre com tarefas pendentes: se faltar tipo (CS0246) ou arquivo, delegue de novo ao Scripter e repita ate o build passar. Aja com tool calls, sem narrar plano.",
      "permissions": [ { "action": "question", "resource": "*", "effect": "deny" },
                       { "action": "subagent", "resource": "*", "effect": "deny" },
                       { "action": "subagent", "resource": "scripter", "effect": "allow" },
                       { "action": "write", "resource": "*", "effect": "deny" },
                       { "action": "edit", "resource": "*", "effect": "deny" },
                       { "action": "shell", "resource": "*", "effect": "deny" },
                       { "action": "execute", "resource": "*", "effect": "deny" } ]
    },
    "plan": {
      "description": "Plan: artefato em texto, sem tools",
      "mode": "primary",
      "model": "$MODEL",
      "steps": 2,
      "capabilities": { "tools": false },
      "system": "Voce e um arquiteto e engenheiro de software senior. Entregue o artefato COMPLETO em texto, sem placeholders, sem elipses e sem tool calls: nao existe ferramenta nesta sessao. Responda em portugues, direto, sem narrar plano.",
      "permissions": [ { "action": "*", "resource": "*", "effect": "deny" } ]
    }
  }
}
JSON

run_agent() {
  local agent="$1" prompt="$2" secs="${3:-300}"    # teto de 5 min por caso: passou disso e suspeita de laco/divagacao
  # plan pensa (temp 0.2); build/tools com tools nao pensa (temp 0.1)
  local model="$MODEL_BUILD"
  case "$agent" in plan|orch) model="$MODEL" ;; esac
  local t0=$(date +%s) pid w=0 wd=60
  case "$agent" in orch) wd=180 ;; esac   # orch pensa/delega antes do 1o artefato
  (cd "$CASE_DIR" && timeout "$secs" opencode run --standalone --auto \
     --agent "$agent" --model "$model" "$prompt") > "$CASE_DIR/.last.txt" 2>&1 &
  pid=$!
  # watchdog: sem nenhum artefato no caso = rodada perdida, nao deixa rodar ate o fim
  while kill -0 "$pid" 2>/dev/null; do
    sleep 5; w=$((w+5))
    if [ "$w" -ge "$wd" ] && ! find "$CASE_DIR" -maxdepth 4 -type f \
         \( -name '*.cs' -o -name '*.sql' -o -name '*.csproj' -o -name '*.tsx' -o -name '*.py' \) \
         -not -path '*/obj/*' -not -path '*/bin/*' 2>/dev/null | grep -q .; then
      kill "$pid" 2>/dev/null
      echo "(abortado em ${w}s: nenhum artefato no caso)"
      break
    fi
  done
  wait "$pid" 2>/dev/null
  local dur=$(( $(date +%s) - t0 ))
  # Tempo e referencia de COMPORTAMENTO (nao corta nada): muito acima = possivel laco.
  if [ "$dur" -gt "${EXPECT:-300}" ]; then
    echo "(run $agent: ${dur}s > referencia ${EXPECT}s = SUSPEITO, possivel laco)"
  else
    echo "(run $agent: ${dur}s, referencia ${EXPECT}s)"
  fi
  if grep -q 'Model unavailable' "$CASE_DIR/.last.txt" 2>/dev/null; then
    echo "ABORTO: provider/modelo indisponivel (llamapure) - confira o collector :4199"
    tail -2 "$CASE_DIR/.last.txt"
    exit 2
  fi
  tail -8 "$CASE_DIR/.last.txt"
}

tempo_caso() {
  local dur=$(( $(date +%s) - ${CASE_T0:-$(date +%s)} ))
  if grep -q 'MSB1008' "$CASE_DIR/.last.txt" 2>/dev/null; then
    echo "AVISO [$1]: modelo passou .cs ao dotnet build (MSB1008)"
  fi
  if [ "$dur" -gt "${EXPECT:-300}" ]; then
    echo "TEMPO [$1]: ${dur}s (referencia <=${EXPECT}s) SUSPEITO: possivel laco"
  else
    echo "TEMPO [$1]: ${dur}s (referencia <=${EXPECT}s) ok"
  fi
}

new_case() {
  [ -n "${CASE_NAME:-}" ] && tempo_caso "$CASE_NAME"
  CASE_NAME="$1"; CASE_T0=$(date +%s)
  case "$1" in
    mission-tools) EXPECT=300 ;;   # referencia: < 5 min
    *)             EXPECT=480 ;;   # referencia: < 8 min
  esac
  CASE_DIR="$WS/$1"; rm -rf "$CASE_DIR"; mkdir -p "$CASE_DIR"
  cp "$WS/opencode.json" "$CASE_DIR/opencode.json"
}

clean_nested() { find "$1" -mindepth 1 -type d \( -name obj -o -name bin \) -prune -exec rm -rf {} + 2>/dev/null; }
# --- tabela de erros -> exemplo otimista (mesmo padrao do tails das tools) ---
error_hints() {
  local errs="$1" c ex
  local codes
  codes="$(printf '%s\n' "$errs" | grep -oE '(CS|MSB|NETSDK|NU)[0-9]{4,5}' | sort -u | head -6)"
  [ -z "$codes" ] && return 0
  echo "  --- tabela de erros (codigo -> exemplo que compila) ---"
  for c in $codes; do
    case "$c" in
      CS1674)  ex='IEnumerable<T> nao e IDisposable: var lista = await conn.QueryAsync<Reembolso>(sql); foreach (var r in lista) { ... }';;
      CS0246)  ex='tipo sem assembly no projeto que compila o arquivo: using Dapper; + <PackageReference Include="Dapper" Version="2.1.89" />';;
      CS0234)  ex='namespace/tipo sem assembly: projeto separado -> <ProjectReference Include="..\Dominio\Dominio.csproj" /> no .csproj que consome; pacote NuGet -> <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.12" /> (using Microsoft.Data.Sqlite) ou dotnet add package Microsoft.Data.Sqlite --version 10.0.12; ou um unico .csproj com tudo';;
      CS0103)  ex='nome fora de escopo: declare antes de usar -> var parametros = new { Id = id };';;
      CS1013)  ex='numero invalido: o _ vai entre digitos -> 100_000m (nao 100_m)';;
      CS8124)  ex='tupla com 1 elemento: use (int Id, decimal Valor) ou so decimal Valor, sem parenteses';;
      CS1022)  ex='falta fechar o tipo/namespace: } no fim do arquivo';;
      CS0101)  ex='tipo declarado duas vezes: dois arquivos com a mesma class -> renomeie ou apague a duplicata';;
      CS0111)  ex='membro duplicado: dois metodos com a mesma assinatura na mesma class -> mantenha um';;
      CS8863)  ex='partial conflitante: so uma declaracao parcial pode ter parametros -> deixe uma class unica';;
      CS8803)  ex='top-level statements antes de namespace: mova o namespace para depois ou use class Program { static void Main() { ... } }';;
      CS8954)  ex='namespace repetido: declare uma vez no topo -> namespace Dominio; e escreva os tipos abaixo';;
      CS1525)  ex='parentese/operador fora de lugar: return (await conn.ExecuteScalarAsync<int>(sql)) > 0;';;
      CS1026)  ex='falta fechar a chamada antes de comparar: return (await conn.ExecuteScalarAsync<int>(sql)) > 0;';;
      CS1002)  ex='falta ponto e virgula: var total = await conn.ExecuteScalarAsync<int>(sql);';;
      CS1513)  ex='falta fechar bloco: } no fim do metodo e da classe';;
      CS1519)  ex='token invalido no membro: declare dentro da classe -> public async Task<int> ContarAsync() { ... }';;
      CS0201)  ex='expressao sem efeito: atribua ou retorne -> var n = await conn.ExecuteScalarAsync<int>(sql);';;
      CS4033)  ex='await exige async: public async Task<bool> ExisteAsync(int id) { return await conn.ExecuteScalarAsync<int>(sql) > 0; }';;
      CS0116)  ex='membro solto: top-level statements nao combinam com namespace -> tire namespace X; e deixe var builder = ... no topo, ou ponha o codigo dentro de class Program';;
      CS0161)  ex='nem todo caminho retorna: feche com return default; ou throw new InvalidOperationException();';;
      CS0535)  ex='classe nao implementa a interface: implemente todos os membros ou remova a interface';;
      CS1061)  ex='membro inexistente: confira o using da extensao -> using Dapper; para conn.QueryAsync';;
      MSB1008) ex='dotnet build recebeu .cs: passe o projeto -> dotnet build MeuProjeto.csproj';;
      MSB1009) ex='projeto inexistente: dotnet build ./MinhaPasta/MeuProjeto.csproj';;
      MSB4025) ex='csproj nao e XML de projeto: reescreva o arquivo inteiro -> <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>';;
      MSB4068) ex='elemento <project> minusculo/atributo errado: <Project Sdk="Microsoft.NET.Sdk"> (P maiusculo, Sdk como atributo)';;
      MSB4040) ex='csproj sem SDK (sem destino): <Project Sdk="Microsoft.NET.Sdk"> ... </Project> — o Sdk fica no Project, sem xmlns';;
      NETSDK1045) ex='SDK nao suporta o alvo: use <TargetFramework>net8.0</TargetFramework>';;
      NU1101)  ex='pacote fora do cache: dotnet add package Dapper --version 2.1.89';;
      NU1102)  ex='versao nao existe no feed: Microsoft.AspNetCore.Http 2.4.0 -> em .NET 8+ e framework, use <FrameworkReference Include="Microsoft.AspNetCore.App" />; Microsoft.Data.SqlClient 8.0.4 nao existe, use 5.1.5';;
      NU1605)  ex='downgrade de pacote: alinhe ao target -> <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />';;
      NU1301)  ex='falha de conexao com o feed (transitoria): repita o build -> dotnet build MeuProjeto.csproj; se persistir, teste curl -I https://api.nuget.org/v3/index.json';;
      NU1101)  ex='pacote inexistente no feed: use o nome real do pacote (ex.: FluentValidation 11.9.0) ou um que ja esteja no cache';;
      *)       ex='abre o arquivo na linha:coluna do erro e corrige ali (o compilador aponta o token exato)';;
    esac
    printf '  %-9s %s\n' "$c" "$ex"
  done
}
build_errors() { find "$1" -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/.data/*' 2>/dev/null | while IFS= read -r pj; do ( cd "$(dirname "$pj")" && dotnet build "$(basename "$pj")" -v q "${DOTNET_SRC_EXCLUDE[@]}" 2>&1 | grep -E 'error (CS|MSB|NETSDK|NU)[0-9]+' ); done; }
build_err() { clean_nested "$1"; ( cd "$1" || return 1; p="$(ls *.csproj 2>/dev/null | head -1)"; if [ -n "$p" ]; then dotnet build "$p" -v q "${DOTNET_SRC_EXCLUDE[@]}" 2>&1 | grep -m3 -E ': error '; else dotnet build -v q "${DOTNET_SRC_EXCLUDE[@]}" 2>&1 | grep -m3 -E ': error '; fi ); }
build_rc()  { clean_nested "$1"; ( cd "$1" || return 1; p="$(ls *.csproj 2>/dev/null | head -1)"; if [ -n "$p" ]; then dotnet build "$p" -v q "${DOTNET_SRC_EXCLUDE[@]}" >/dev/null 2>&1; else dotnet build -v q "${DOTNET_SRC_EXCLUDE[@]}" >/dev/null 2>&1; fi ); }

# nota parcial por artefato (faixa cheia nao esconde o que ja foi entregue)
partial_report() {
  local caso="$1" key="$2" cs="FAIL" sq="nao" rx="nao" ml="nao" f pj any=0 all=1 projs
  projs="$(find "$CASE_DIR" -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/.data/*' 2>/dev/null)"
  while IFS= read -r pj; do
    [ -z "$pj" ] && continue
    any=1
    clean_nested "$CASE_DIR"
    ( cd "$(dirname "$pj")" && dotnet build "$(basename "$pj")" -v q "${DOTNET_SRC_EXCLUDE[@]}" >/dev/null 2>&1 ) || all=0
  done <<< "$projs"
  [ "$any" -eq 1 ] && [ "$all" -eq 1 ] && cs="ok"
  f="$(find "$CASE_DIR" -name '*.sql' -not -path '*/.data/*' | head -1)"
  [ -n "$f" ] && grep -qiE 'create[[:space:]]+table' "$f" && sq="ok"
  if [ "$key" = marrom ]; then
    f="$(find "$CASE_DIR" -name '*.tsx' -not -path '*/.data/*' -not -path '*/node_modules/*' | head -1)"
    [ -n "$f" ] && grep -qE 'export default function|export function|useState' "$f" && rx="ok"
    echo "PARCIAL [$caso]: csharp $cs | sql $sq | react $rx"
  else
    f="$(find "$CASE_DIR" -name '*.py' -not -path '*/.data/*' | head -1)"
    [ -n "$f" ] && python3 -m py_compile "$f" 2>/dev/null && ml="ok"
    echo "PARCIAL [$caso]: csharp $cs | sql $sq | ml-nn $ml"
  fi
}

check() {
  if eval "$2"; then echo "RESULTADO [$1]: PASS"; pass=$((pass+1));
  else echo "RESULTADO [$1]: FAIL"; fail=$((fail+1)); fi
}

quer() { [ -z "${CASES[*]:-}" ] && return 0; local c; for c in "${CASES[@]}"; do [ "$c" = "$1" ] && return 0; done; return 1; }

P_TF1="$(cat <<'EOT'
Trabalhe na pasta atual, que ja tem um projeto console em C# chamado App.csproj com o app. O codigo esta no arquivo Program.cs na raiz desta pasta (nao crie subpasta nem outro projeto). Compile e execute com o dotnet e confirme a mensagem exibida. O Program.cs deve exibir exatamente Hello World, com a linha Console.WriteLine("Hello World"); — nao troque a capitalizacao nem reescreva o arquivo com outra mensagem. Inicialize um repositorio git nesta mesma pasta, adicione os arquivos e faca o commit com a mensagem 'hello world console' (git commit -m 'hello world console'); confirme com git log. A ULTIMA tool call da resposta deve ser o commit: nao encerre antes do repositorio ter o commit. Aja com ferramentas: uma tool call por vez, sem narrar plano, sem repetir o enunciado e sem descrever o que ja fez.
EOT
)"
P_TF2="$(cat <<'EOT'
Na pasta atual existe um app console em C# (App.csproj) que exibe Hello World, ja compilado e commitado. O codigo esta no arquivo Program.cs na raiz desta pasta. Nao escreva codigo C# dentro de App.csproj (ele e o XML do projeto; codigo so no Program.cs). Leia esse arquivo e troque a mensagem exibida para Hello World v2, mantendo o resto do programa: a linha deve ficar Console.WriteLine("Hello World v2"); (mesma capitalizacao). Recompile e execute de novo, confirmando a nova mensagem na saida. Adicione a mudanca e faca o commit com a mensagem 'hello world v2'; confirme com git log. A ULTIMA tool call da resposta deve ser o commit. Aja com ferramentas: uma tool call por vez, sem narrar plano, sem repetir o enunciado e sem descrever o que ja fez.
EOT
)"
P_MT="$(cat <<EOT
Em $WS/mission-tools/mcs existe o projeto mcs.csproj. Escreva Program.cs para calcular e imprimir, um valor por linha e sem rotulos, tres numeros inteiros: o fatorial de 10; o maximo divisor comum de 1071 e 462; e quantos numeros primos existem de 1 a 50. O programa tem que compilar e rodar com \`dotnet run --project $WS/mission-tools/mcs/mcs.csproj\` sem erro e imprimir exatamente esses tres numeros, nessa ordem (calcule, nao copie os numeros). Responda somente com tool calls, sem prosa.
EOT
)"
P_AZUL="$(cat <<'EOT'
Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reembolsos corporativos) usando repositorio generico com ORM. Escopo SOMENTE backend de dados (sem frontend, sem API).
EOT
)"
P_VERDE="$(cat <<'EOT'
Implemente o dominio backend C# de um CRUD de reembolsos corporativos (entidades, value objects, regras de aprovacao por faixa de valor) e o banco de dados SQL: DDL das tabelas, indices, constraints e seeds. Valide CPF e CNPJ. Escopo: dominio + banco SQL. Premissas: backend .NET C#. Aja com tool calls, uma por vez; nao narre plano nem repita o enunciado. Critério de aceite, solução implementada, codificada em arquivos no disco e compilando: crie um .csproj com nome (ex.: Dominio.csproj, net10.0, nao .csproj sem nome) e os .cs com implementacao efetiva (classes concretas com logica real, nao apenas interfaces) e os .sql (DDL); compilar usando dotnet build ate rc=0. Dominio POCO sem atributos de ORM (sem [Table], sem EF Core): o SQL vai nos arquivos .sql.
EOT
)"


# enunciados ORIGINAIS da trilha orch (Rochas.OpenCodeBridge.Test/E2EBeltsTests.cs)
P_AZUL_ORCH="$(cat <<'EOT'
Aplicacao backend .NET C# que realize uma consulta de dados em uma tabela (ex.: reembolsos corporativos) usando repositorio generico com ORM. Escopo SOMENTE backend de dados (sem frontend, sem API). Premissas: backend .NET C#.
EOT
)"
# enunciados ORIGINAIS das belts roxa/marrom/preta (trilha build: E2EBeltsBuildTests.cs)
P_ROXA="$(cat <<'EOT'
Trabalhe em {ws}. Implemente o cadastro de clientes e fornecedores no backend .NET C#: dominio (entidades e value objects), banco SQL (DDL, indices e constraints), repositorios com Dapper, servicos de aplicacao e validacao/mascaras de e-mail, telefone BR com DDD e CEP. Crie os arquivos .cs e .sql com caminho relativo a raiz do workspace, usando a tool write. Comece pelo dominio, depois o SQL e depois repositorios/servicos; nao escreva plano, va direto ao write. Inclua um .csproj com nome (ex.: Cadastros.csproj, net10.0, nao .csproj sem nome) e implemente classes concretas com logica real (nao apenas interfaces); compilar usando dotnet build ate rc=0. Ao terminar responda apenas BUILD-OK com a lista dos arquivos.
EOT
)"
P_MARROM="$(cat <<'EOT'
Implemente o dominio backend C# de um CRUD de reembolsos corporativos, o banco de dados SQL, os repositorios e servicos (Dapper), a API REST e o frontend React. Escopo: dominio + banco SQL + repositorios e servicos + API + frontend React. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br. Cada arquivo deve conter UMA responsabilidade (um artefato por arquivo: um .cs de dominio, um .sql de DDL, um .tsx de frontend); nao misture dominio, banco, API e frontend no mesmo arquivo. Critério de aceite, solução implementada, codificada em arquivos no disco e compilando: crie um .csproj com nome (ex.: Reembolsos.csproj, net10.0, nao .csproj sem nome) para o backend .NET, os .cs com implementacao efetiva (classes concretas, nao apenas interfaces), .sql e .tsx/.jsx; compilar usando dotnet build ate rc=0 no backend.
EOT
)"
P_PRETA="$(cat <<'EOT'
Implemente a arquitetura de ecossistema: dominio backend C# do Portal do Colaborador, banco de dados SQL, repositorios e servicos, API REST, servicos bus, pipeline de Big Data com ML (regressao, classificacao, rede neural perceptron). Escopo: dominio + banco SQL + repositorios e servicos + API + servicos bus + redes neurais. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br. Cada arquivo deve conter UMA responsabilidade (um artefato por arquivo: um .cs de dominio, um .sql de DDL, um .py de treino); nao misture dominio, banco, API, bus e ML no mesmo arquivo. Critério de aceite, solução implementada, codificada em arquivos no disco e compilando: crie um .csproj com nome (ex.: Portal.csproj, net10.0, nao .csproj sem nome) para o backend .NET, os .cs com implementacao efetiva (classes concretas, nao apenas interfaces), .sql e scripts de treino; compilar usando dotnet build ate rc=0 no backend .NET.
EOT
)"
# enunciados ORIGINAIS das belts roxa/marrom/preta (trilha orch: E2EBeltsTests.cs)
P_ROXA_ORCH="$(cat <<'EOT'
Trabalhe no workspace da tarefa. Implemente o cadastro de clientes e fornecedores no backend .NET C#: dominio (entidades e value objects), banco SQL (DDL, indices e constraints), repositorios com Dapper, servicos de aplicacao e validacao/mascaras de e-mail, telefone BR com DDD e CEP. Entregue a implementacao completa no workspace da tarefa (backend .NET C# + banco SQL).
EOT
)"
P_MARROM_ORCH="$(cat <<'EOT'
Especifique o dominio backend C# de um CRUD de reembolsos corporativos, o banco de dados SQL, os repositorios e servicos (Dapper), a API REST e o frontend React. Escopo: dominio + banco SQL + repositorios e servicos + API + frontend React. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br.
EOT
)"
P_PRETA_ORCH="$(cat <<'EOT'
Especifique a arquitetura de ecossistema: dominio backend C# do Portal do Colaborador, banco de dados SQL, repositorios e servicos, API REST, servicos bus, pipeline de Big Data com ML (regressao, classificacao, rede neural perceptron). Escopo: dominio + banco SQL + repositorios e servicos + API + servicos bus + redes neurais. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br.
EOT
)"

P_VERDE_ORCH="$(cat <<'EOT'
Especifique o dominio backend C# de um CRUD de reembolsos corporativos (entidades, value objects, regras de aprovacao por faixa de valor) e o banco de dados SQL: DDL das tabelas, indices, constraints e seeds. Valide CPF e CNPJ. Escopo: dominio + banco SQL. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br.
EOT
)"
# aceite levado ao orch pelo harness (o enunciado do orch nao traz criterio de aceite)
ORCH_ACEITE=" Criterio de aceite: um unico .csproj na raiz do workspace (nao crie um segundo projeto), .cs com implementacao efetiva e DDL em .sql quando o escopo pedir banco; compilar com dotnet build ate rc=0. Persistencia com Dapper 2.1.89 e Microsoft.Data.Sqlite 10.0.12 (versoes do cache NuGet local)."

SUF=" Implementacao completa, sem placeholders. ENTREGUE O ARTEFATO COMPLETO EM TEXTO, SEM TOOL CALLS."
P_PROD="C# .NET 9 Minimal API + Dapper + Postgres: CRUD completo de Product (Id, Name, Price, Stock). Entregue: DDL da tabela, GET /api/products (lista), GET /api/products/{id} (404 se ausente), POST (400 se Name vazio ou Price <= 0), PUT (404 se ausente) e DELETE (204), com log por operacao. Entregue o codigo completo + 3 bullets de borda.$SUF"
P_CUST="C# .NET 9 Minimal API + Dapper + Postgres: CRUD de Customer (Id, Name, Email, CreatedAt). Entregue: GET /api/customers com paginacao SQL usando LIMIT ? OFFSET ? (?page, ?pageSize, max 100) e filtro ?q por nome/email (ILIKE), POST rejeitando email duplicado pela constraint UNIQUE(email) respondendo 409 (Results.Conflict) e email invalido respondendo 400, PUT e DELETE com 404, indice no email. So codigo + 3 bullets de borda.$SUF"
P_SALE="C# .NET 9 + Dapper + Postgres: POST /api/saleinvoices cria SaleInvoice (header: CustomerId, Date) + Items (detail: ProductId, Qty, UnitPrice) em UMA transacao (BEGIN/COMMIT/ROLLBACK): total = soma(Qty*UnitPrice), valida estoque suficiente de cada produto (409 se faltar, com rollback total) e da baixa no estoque no mesmo commit. GET /api/saleinvoices/{id} retorna header + items. Entregue o codigo COMPLETO: DDL das 2 tabelas + o handler POST inteiro (BeginTransaction/Commit/Rollback) + o SELECT do GET; nao resuma nem use elipses. Mostre tambem o que acontece se o 3o item estourar o estoque. So codigo + 3 bullets.$SUF"
P_WORKER="C# .NET 9 + Postgres: entregue UMA classe OutboxWorker que HERDA BackgroundService (Microsoft.Extensions.Hosting) e sobrescreve protected override async Task ExecuteAsync(CancellationToken stoppingToken): drena a tabela outbox (payload jsonb) em lote de 50 a cada 5s, publica no barramento com retry exponencial + jitter (max 5 tentativas, depois dead-letter), passa o stoppingToken em TODO await (stop gracioso sem perder a mensagem da vez) e registra throughput no log a cada minuto. NAO use loop proprio em classe IAsyncDisposable nem Task.Run solto: o host controla o ciclo de vida. Entregue o worker completo + DDL da dead-letter. So codigo + 3 bullets.$SUF"
P_REDIS="C# .NET 9 + Redis (StackExchange.Redis): repositorio ProductRepository com cache-aside (IDistributedCache, sliding 5min): GetAsync le do cache, em miss cai no Postgres (Dapper) e repopula; SetAsync e DeleteAsync do repositorio INVALIDAM a chave com cache.RemoveAsync; stampede controlado por lock por chave e circuit-breaker simples (3 falhas) quando o Redis cai. Entregue SO O BLOCO (1): a classe do repositorio COMPLETA (usings, construtor, GetAsync, SetAsync, DeleteAsync com RemoveAsync, lock e breaker) + 3 bullets do que acontece quando o Redis esta fora.$SUF"
P_DI_HEAD="CONTINUACAO do step anterior. O bloco (1) ja esta pronto (repositorio ProductRepository com IDistributedCache e cache.RemoveAsync) e vai abaixo como CONTEXTO — NAO reescreva, NAO repita e NAO resuma esse repositorio. Entregue SO O BLOCO (2): o trecho de Program.cs de uma Minimal API .NET 9 que registra Redis e repositorio no DI, exatamente com builder.Services.AddStackExchangeRedisCache(options => { options.Configuration = builder.Configuration.GetConnectionString(\"Redis\"); options.InstanceName = \"ProductCache:\"; }) e builder.Services.AddScoped<IProductRepository, ProductRepository>(); (o construtor recebe IDistributedCache e a connection string do Postgres). Formato: comecar a resposta com \`\`\`csharp e a linha var builder = WebApplication.CreateBuilder(args);, sem explicacao antes do codigo, e terminar com 3 bullets de configuracao/ambiente (ConnectionStrings:Redis, InstanceName, Redis fora).$SUF"
P_DASH="React 19 + TS + Tailwind: Dashboard que consome GET /api/saleinvoices?days=30 do ASP.NET e exibe: 3 cards (faturamento, ticket medio, itens vendidos), grafico de barras por dia (SVG proprio, sem lib), tabela das 10 maiores notas com link p/ detalhe, skeleton durante load, erro com retry por widget independente e AbortController no filtro de periodo. Entregue o codigo .tsx completo + 3 bullets.$SUF"
P_BUS="C# .NET 9 + Dapper: endpoint POST /api/schedule publica Clinic.Schedule no barramento via outbox transacional (mesma transacao do INSERT). Desenhe: tabela outbox (colunas), publisher em background com retry exponencial + jitter, consumidor idempotente por message-id (tabela processed_messages), e o que acontece quando o broker cai por 10 minutos. Responda em bullets curtos + DDL da outbox.$SUF"
P_ERP="ERP: regra de preco final = base - desconto + imposto, onde desconto e min(desconto_pedido, teto_categoria) e imposto incide sobre (base - desconto) com aliquota por UF, exceto itens isentos (NCM inicia com 8471). Dê: funcao pura em C# ou Python + tabela com 4 casos de borda (desconto acima do teto, UF sem aliquota, item isento, base zero) e o valor esperado de cada um.$SUF"

{ if [ "$MODO" = tools ]; then

  if quer tools-full; then
    new_case tools-full
    ( cd "$CASE_DIR" && dotnet new console -n App -o . --force >/dev/null 2>&1
      printf 'Console.WriteLine("Hello World");\n' > Program.cs )
    echo "----- [tools-full:create]"; run_agent tools "$P_TF1" 900
    check tools-full-create "[ -f '$CASE_DIR/App.csproj' ] && grep -qi 'hello world' '$CASE_DIR/Program.cs' && (cd '$CASE_DIR' && git log --format=%s 2>/dev/null | grep -qi 'hello world console')"
    echo "----- [tools-full:adjust]"; run_agent tools "$P_TF2" 900
    check tools-full-adjust "grep -q 'Hello World v2' '$CASE_DIR/Program.cs' && [ \$(cd '$CASE_DIR' && git log --oneline | wc -l) -ge 2 ] && (cd '$CASE_DIR' && git log --format=%s | grep -qi 'hello world v2') && (cd '$CASE_DIR' && dotnet build App.csproj -v q >/dev/null 2>&1)"
    check tools-full-run "( cd '$CASE_DIR' && dotnet run --project App.csproj -v q 2>&1 | grep -qi 'hello world v2' )"
  fi

  if quer mission-tools; then
    new_case mission-tools; mkdir -p "$CASE_DIR/mcs"
    ( cd "$CASE_DIR/mcs" && dotnet new console -n mcs -o . --force >/dev/null 2>&1 )
    echo "----- [mission-tools] (build)"; run_agent build "$P_MT" 900
    nums="$( cd "$CASE_DIR/mcs" && dotnet run --project mcs.csproj 2>/dev/null | grep -E '^-?[0-9]+$' | head -3 | tr '\n' ' ' )"
    [ "$nums" = "3628800 21 15 " ] || echo "(aceite: nums='$nums')"
    check mission-tools "[ '$nums' = '3628800 21 15 ' ]"
    check mission-tools-hardcode "! grep -rq --include='*.cs' '3628800' '$CASE_DIR'"
  fi

  # matriz 2x2: belts-<agente>-<cor>  (legado belts-azul/verde = build)
  quer_belt() {
    local caso="$1" cor="${1#belts-build-}"
    quer "$caso" && return 0
    [ "$cor" != "$caso" ] && quer "belts-$cor" && return 0
    return 1
  }
  for belt in azul verde roxa marrom preta; do
    for AG in build orch; do
      cor="$(printf '%s' "$belt" | tr a-z A-Z)"
      if [ "$AG" = orch ]; then var="P_${cor}_ORCH"; P="${!var}$ORCH_ACEITE"
      else var="P_${cor}"; P="${!var}"; fi
      P="${P//\{ws\}/.}"   # enunciado original usa {ws}: aqui o cwd ja e' o workspace do caso
      caso="belts-$AG-$belt"
      quer_belt "$caso" || continue
      new_case "$caso"
      echo "----- [$caso] ($AG)"; run_agent "$AG" "$P" 300
      # projeto unico: dois .csproj na mesma pasta fazem o build compilar tudo e falhar sem sentido
      # aceite minimo: todo .csproj da arvore compila (rc=0) e existe fonte .cs real fora de obj/bin
      src="find '$CASE_DIR' -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | grep -q ."
      rc=1
      projs="$(find "$CASE_DIR" -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/.data/*' 2>/dev/null)"
      if [ -z "$projs" ]; then
        echo "(nenhum .csproj na arvore)"
      else
        rc=0
        while IFS= read -r pj; do
          [ -z "$pj" ] && continue
          clean_nested "$CASE_DIR"
          ( cd "$(dirname "$pj")" && dotnet build "$(basename "$pj")" -v q "${DOTNET_SRC_EXCLUDE[@]}" >/dev/null 2>&1 ) || rc=1
        done <<< "$projs"
      fi
      if [ $rc -ne 0 ]; then
        errs="$(build_errors "$CASE_DIR")"
        printf '%s\n' "$errs" | grep -v '^$' | head -3 | sed 's/^/  /'
        error_hints "$errs"
      fi
      check "$caso" "[ $rc -eq 0 ] && $src"
      case "$belt" in marrom|preta) partial_report "$caso" "$belt" ;; esac
    done
  done

else

  ent_case() {
    local id="$1" p="$2" extra="${3:-true}"
    quer "$id" || return 0
    CASE_DIR="$WS"
    echo "----- [$id]"; run_agent plan "$p" 600
    cp "$CASE_DIR/.last.txt" "$WS/$id.txt"
    check "$id" "[ \$(wc -c < '$WS/$id.txt' ) -gt 400 ] && ! grep -qE 'NotImplementedException|TODO[[:space:]:]' '$WS/$id.txt' && $extra"
  }

  ent_case crud-product "$P_PROD" "grep -qE 'MapPost|HttpPost' '$WS/crud-product.txt'"
  ent_case crud-customer "$P_CUST" "grep -qiE 'LIMIT|OFFSET|pageSize' '$WS/crud-customer.txt' && grep -qE '409|Conflict' '$WS/crud-customer.txt'"
  ent_case crud-saleinvoice "$P_SALE" "grep -qiE 'BeginTransaction|Transaction' '$WS/crud-saleinvoice.txt' && grep -qiE 'Items|ProductId' '$WS/crud-saleinvoice.txt'"
  ent_case worker "$P_WORKER" "grep -q 'BackgroundService' '$WS/worker.txt' && grep -q 'ExecuteAsync' '$WS/worker.txt'"
  ent_case redis-cache "$P_REDIS" "grep -qE 'IDistributedCache|ConnectionMultiplexer' '$WS/redis-cache.txt' && grep -qE 'RemoveAsync|Remove\(|KeyDelete' '$WS/redis-cache.txt'"
  if quer redis-di; then
    ctx="$(cat "$WS/redis-cache.txt" 2>/dev/null)"
    ent_case redis-di "REPOSITORIO DO STEP ANTERIOR (bloco 1, CONTEXTO)
$ctx
FIM DO CONTEXTO.

$P_DI_HEAD" "grep -qE 'AddStackExchangeRedisCache|AddDistributedRedisCache|IConnectionMultiplexer' '$WS/redis-di.txt' && grep -q 'AddScoped' '$WS/redis-di.txt'"
  fi
  ent_case bus "$P_BUS" "grep -qi 'outbox' '$WS/bus.txt' && grep -qi 'idempotente' '$WS/bus.txt'"
  ent_case erp "$P_ERP" "grep -q '8471' '$WS/erp.txt'"
  ent_case dashboard "$P_DASH" "grep -q 'AbortController' '$WS/dashboard.txt' && grep -qE 'useEffect|useLayoutEffect' '$WS/dashboard.txt'"

fi
  echo "== RESUMO: PASS=$pass FAIL=$fail  (log: $LOG)"
} 2>&1 | tee "$LOG"

curl -s -m 10 -X POST "$COLLECTOR/api/dump" >/dev/null 2>&1 || true

python3 "$HERE/report.py" --since "$START" --proto "$PROTO" --log "$LOG" \
  --title "Bateria WAR ($MODO) - opencode -> llama (tunel)" --out-dir "$HERE/reports"



