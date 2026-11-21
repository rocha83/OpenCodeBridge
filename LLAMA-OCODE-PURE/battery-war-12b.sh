#!/usr/bin/env bash
# Bateria WAR pelo TUNEL: opencode -> collector :4199 -> llama :4100.
# So enunciados: nenhuma dependencia de harness externo.
#   tools : casos que EXIGEM ferramenta (tools-full, mission-tools, belts azul..preta em build)
#           -> agent 'tools', permissao completa, prompt original do CLI.
#   ent   : sequencia Plan de conhecimento (produto/customer/saleinvoice/worker/
#           redis/bus/erp/dashboard) em TEXTO puro, sem tool -> agent 'plan'.
# Uso: ./battery-war.sh <chat|responses> <tools|ent> [caso ...]
# Fail-fast: WAR_STOP_ON_FAIL=1 (default) para no primeiro FAIL; =0 mede placar completo.
# Skills do caso: WAR_SKILLS=1 instala harness/skills em <caso>/.opencode/skills (default 0).
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PROTO="${1:-chat}"; MODO="${2:-tools}"
[ $# -ge 2 ] && shift 2 || shift $#
CASES=("$@")
ROOT="/tmp/opencode/llama-pure"
WS="$ROOT/war-$MODO-$PROTO"
LLAMA="${LLAMA:-http://127.0.0.1:4199}"
MODEL="llamapure/Gemma4-12B-IT-Principal-GK-DS-Retrained"                 # plan: thinking ON, temp 0.2
MODEL_BUILD="llamapure/Gemma4-12B-IT-Principal-GK-DS-Retrained-nothink"  # build: thinking OFF, temp 0.1
LOG="$ROOT/war-$MODO-$PROTO.log"
START="${WAR_RUN:-$(date +%s)}"   # rodada inteira: mesma origem para todos os casos
START_TS="$(date +%s)"             # epoch: report.py --since exige float
# Observer em memoria: reset SO no inicio da rodada (WAR_RESET=1); os casos seguintes acumulam.
COLLECTOR="${COLLECTOR:-http://127.0.0.1:4199}"
# exclusao de fontes geradas: projeto na raiz que varre **/*.cs nao pode compilar obj/bin de projeto aninhado
DOTNET_SRC_EXCLUDE=(-p:DefaultItemExcludes='obj/**%3Bbin/**%3B**/obj/**%3B**/bin/**')
[ "${WAR_RESET:-0}" = 1 ] && { curl -s -m 3 -X POST "$COLLECTOR/api/reset" >/dev/null 2>&1 || true; }
pass=0; fail=0; skipped=0
# Fail-fast: o primeiro FAIL encerra a rodada (VRAM/energia/token). Para medir
# placar completo use WAR_STOP_ON_FAIL=0 (consciente, nunca "ver quantos passam").
STOP_ON_FAIL="${WAR_STOP_ON_FAIL:-1}"
ABORT=0; FIRST_FAIL=""
ff_stop() { [ "$STOP_ON_FAIL" = 1 ] && [ "$ABORT" = 1 ]; }
ff_skipped() { local n; for n in $1; do echo "RESULTADO [$n]: SKIP (fail-fast)"; skipped=$((skipped+1)); done; }
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

mkdir -p "$WS"   # sem wipe global: new_case limpa o diretorio do proprio caso (permite rodadas em paralelo)

cat > "$WS/opencode.json" <<JSON
{
  "\$schema": "https://opencode.ai/config.json",
  "experimental": { "subagent_depth": 3, "codemode_vm": false },
  "providers": {
    "llamapure": {
      "name": "Llama pure",
      "package": "$PKG",
      "settings": { "baseURL": "$LLAMA/v1", "apiKey": "guest@123" },
      "models": {
        "Gemma4-12B-IT-Principal-GK-DS-Retrained": {
          "name": "Gemma4 12B GK-DS (plan: thinking on, temp 0.2)",
          "tool_call": true,
          "body": { "temperature": 0.2, "chat_template_kwargs": { "enable_thinking": true } },
          "limit": { "context": 131072, "output": 16384 }
        },
        "Gemma4-12B-IT-Principal-GK-DS-Retrained-nothink": {
          "name": "Gemma4 12B GK-DS (build: thinking off, temp 0.1)",
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
      "system": "Um turno = uma tool call. Os argumentos de cada tool sao JSON puro com exatamente as chaves do schema (path, oldString, newString, command); texto de outra chamada nao entra nos argumentos.",
      "permissions": [ { "action": "question", "resource": "*", "effect": "deny" },
                       { "action": "execute", "resource": "*", "effect": "deny" } ]
    },
    "build": {
      "description": "Build: executa micro-enunciados com tools, thinking off temp 0.1",
      "mode": "all",
      "model": "$MODEL_BUILD",
      "steps": 80,
      "system": "O system desta sessao vem do harness do tunel (harness/*.md montado por collector.py). Em projetos dotnet: .NET 10 e EF 10; 'dotnet new' so ao criar projeto novo; conclua quando a saida do programa corresponder ao enunciado.",
      "permissions": [ { "action": "question", "resource": "*", "effect": "deny" },
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
# guarda: config invalida faz o opencode responder "Model unavailable" (silencioso)
python3 -m json.tool "$WS/opencode.json" >/dev/null 2>&1 || { echo "ABORTO: opencode.json gerado e JSON invalido"; exit 3; }

run_agent() {
  local agent="$1" prompt="$2" secs="${3:-300}"    # teto de 5 min por caso: passou disso e suspeita de laco/divagacao
  # plan pensa (temp 0.2); build/tools com tools nao pensa (temp 0.1)
  local model="$MODEL_BUILD"
  case "$agent" in plan) model="$MODEL" ;; esac
  local t0=$(date +%s) pid w=0 wd=180
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
    echo "(run $agent: ${dur}s > referencia ${EXPECT:-300}s = SUSPEITO, possivel laco)"
  else
    echo "(run $agent: ${dur}s, referencia ${EXPECT:-300}s)"
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
    echo "TEMPO [$1]: ${dur}s (referencia <=${EXPECT:-300}s) SUSPEITO: possivel laco"
  else
    echo "TEMPO [$1]: ${dur}s (referencia <=${EXPECT:-300}s) ok"
  fi
}

new_case() {
  [ -n "${CASE_NAME:-}" ] && tempo_caso "$CASE_NAME"
  CASE_NAME="$1"; CASE_T0=$(date +%s)
  case "$1" in
    mission-tools) EXPECT=240 ;;   # referencia de comportamento (nao corta nada)
    *-verde|*-azul) EXPECT=240 ;;  # referencia unica de comportamento
    *)             EXPECT=240 ;;   # referencia unica: muito acima = possivel laco
  esac
  CASE_DIR="$WS/$1"; rm -rf "$CASE_DIR"; mkdir -p "$CASE_DIR"
  cp "$WS/opencode.json" "$CASE_DIR/opencode.json"
  # Skills do caso: trilha sob demanda, roteada pela descricao (semantic routing).
  # O catalogo chega ao modelo pelo system do cliente e e preservado no tuneis
  # (collector.apply_harness mantem <available_skills>).
  if [ "${WAR_SKILLS:-0}" = "1" ] && [ -d "$HERE/harness/skills" ]; then
    mkdir -p "$CASE_DIR/.opencode"
    cp -r "$HERE/harness/skills" "$CASE_DIR/.opencode/skills"
  fi
  mkdir -p "$ROOT/metrics"
  printf '{"case":"%s","status":"RUN","dir":"%s","run":"%s","ts":%s}\n' \
    "$1" "$CASE_DIR" "$START" "$(date +%s)" > "$ROOT/metrics/current.json"
}

clean_nested() { find "$1" -mindepth 1 -type d \( -name obj -o -name bin \) -prune -exec rm -rf {} + 2>/dev/null; }
# --- erro de compilacao -> exemplo otimista (fonte unica: hints.py) ---
# O harness nao carrega mais a tabela: imprime so os codigos que apareceram.
error_hints() {
  local errs="$1" hints
  [ -z "$errs" ] && return 0
  hints="$(printf '%s\n' "$errs" | python3 "$HERE/hints.py" - 2>/dev/null)"
  [ -z "$hints" ] && hints="$(printf '%s\n' "$errs" \
    | grep -oE '(CS|MSB|NETSDK|NU)[0-9]{4,5}' | sort -u | head -6 | sed 's/^/  /')"
  [ -z "$hints" ] && return 0
  echo "  --- erro -> exemplo que compila (hints.py) ---"
  printf '%s\n' "$hints"
}
build_errors() {
  local d="$1" all src
  all="$(find "$d" -maxdepth 1 -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/.data/*' 2>/dev/null | while IFS= read -r pj; do ( cd "$d" && dotnet build "$(basename "$pj")" -v q "${DOTNET_SRC_EXCLUDE[@]}" 2>&1 | grep -E 'error (CS|MSB|NETSDK|NU)[0-9]+' ); done | sort -u)"
  src="$(printf '%s\n' "$all" | grep -v '/obj/' | grep -v '/bin/')"
  if [ -n "$src" ]; then printf '%s\n' "$src"; else printf '%s\n' "$all"; fi
}
build_err() { clean_nested "$1"; ( cd "$1" || return 1; p="$(ls *.csproj 2>/dev/null | head -1)"; if [ -n "$p" ]; then dotnet build "$p" -v q "${DOTNET_SRC_EXCLUDE[@]}" 2>&1 | grep -m3 -E ': error '; else dotnet build -v q "${DOTNET_SRC_EXCLUDE[@]}" 2>&1 | grep -m3 -E ': error '; fi ); }
build_rc()  { clean_nested "$1"; ( cd "$1" || return 1; p="$(ls *.csproj 2>/dev/null | head -1)"; if [ -n "$p" ]; then dotnet build "$p" -v q "${DOTNET_SRC_EXCLUDE[@]}" >/dev/null 2>&1; else dotnet build -v q "${DOTNET_SRC_EXCLUDE[@]}" >/dev/null 2>&1; fi ); }

# nota parcial por artefato (faixa cheia nao esconde o que ja foi entregue)
partial_report() {
  local caso="$1" key="$2" cs="FAIL" sq="nao" rx="nao" ml="nao" f pj any=0 all=1 projs
  projs="$(find "$CASE_DIR" -maxdepth 1 -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/.data/*' 2>/dev/null)"
  while IFS= read -r pj; do
    [ -z "$pj" ] && continue
    any=1
    clean_nested "$CASE_DIR"
    ( cd "$CASE_DIR" && dotnet build "$(basename "$pj")" -v q "${DOTNET_SRC_EXCLUDE[@]}" >/dev/null 2>&1 ) || all=0
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
    if [ -n "$f" ]; then
      python3 -m py_compile "$f" 2>/dev/null && ml="ok"
    elif grep -rqiE --include='*.cs' --exclude-dir=obj --exclude-dir=bin -- 'Perceptron' "$CASE_DIR" 2>/dev/null; then
      ml="ok"          # perceptron em C# tambem satisfaz ml-nn
    fi
    echo "PARCIAL [$caso]: csharp $cs | sql $sq | ml-nn $ml"
  fi
}

record_case() {
  local caso="$1" status="$2" dur="${3:-0}"
  mkdir -p "$ROOT/metrics"
  printf '{"case":"%s","status":"%s","duration":%s,"proto":"%s","modo":"%s","run":"%s","ts":%s}\n' \
    "$caso" "$status" "$dur" "$PROTO" "$MODO" "$START" "$(date +%s)" >> "$ROOT/metrics/cases.jsonl"
  printf '{"case":"%s","status":"%s","dir":"%s","run":"%s","ts":%s}\n' \
    "$caso" "$status" "${CASE_DIR:-}" "$START" "$(date +%s)" > "$ROOT/metrics/current.json"
}

check() {
  local st dur
  if eval "$2"; then st=PASS; pass=$((pass+1));
  else
    st=FAIL; fail=$((fail+1))
    if [ -z "$FIRST_FAIL" ]; then FIRST_FAIL="$1"; fi
    [ "$STOP_ON_FAIL" = 1 ] && ABORT=1
  fi
  echo "RESULTADO [$1]: $st"
  dur=$(( $(date +%s) - ${CASE_T0:-$(date +%s)} ))
  record_case "$1" "$st" "$dur"
  if [ "$st" = FAIL ] && [ "$STOP_ON_FAIL" = 1 ]; then
    echo "ABORTO [fail-fast]: $1 falhou - rodada encerrada (WAR_STOP_ON_FAIL=0 mede placar completo)"
  fi
}

quer() { [ -z "${CASES[*]:-}" ] && return 0; local c; for c in "${CASES[@]}"; do [ "$c" = "$1" ] && return 0; done; return 1; }

TOOLS_FULL_CRIAR_HELLO="$(cat <<'EOT'
Crie na raiz desta pasta (sem criar subpasta) o arquivo App.csproj de um projeto console C# cujo programa exiba exatamente Hello World. Compile, execute e confirme a saida. Depois inicialize o git aqui e faca um commit com a mensagem 'hello world console'; confirme com git log.
EOT
)"
TOOLS_FULL_AJUSTAR_V2="$(cat <<'EOT'
Na pasta atual esta o app console C# (App.csproj) que exibe Hello World, ja commitado. Troque a mensagem para Hello World v2, recompile, execute e confirme a nova saida. Faca o commit da mudanca com a mensagem 'hello world v2' e confirme com git log.
EOT
)"
MISSION_TOOLS_CRIAR_PROJETO="$(cat <<'EOT'
Crie nesta pasta o projeto console C# em mcs/ (mcs.csproj). O passo termina com o projeto criado e compilando.
EOT
)"
MISSION_TOOLS_ESCREVER_PROGRAMA="$(cat <<'EOT'
Em mcs/ existe o projeto console C# mcs.csproj, compilando. Escreva o programa que imprima, um valor por linha: o fatorial de 10, o MDC de 1071 e 462, e a quantidade de numeros primos entre 1 e 50. Rode e confira a saida.
EOT
)"
P_AZUL="$(cat <<'EOT'
Crie nesta pasta o backend .NET C# de consulta de dados de uma tabela de reembolsos corporativos: dominio e repositorio generico sobre ORM, mais a camada de leitura nomeada Consulta (classe ConsultaService com o metodo que devolve os reembolsos). Sem API e sem frontend. Um projeto na raiz, compilando.
EOT
)"
P_VERDE="$(cat <<'EOT'
Crie nesta pasta o dominio backend C# de um CRUD de reembolsos corporativos (entidades, regras de aprovacao por faixa de valor, validacao de CPF e CNPJ) e o banco SQL (DDL, indices, constraints e seeds). Um projeto na raiz, compilando.
EOT
)"


# enunciados oficiais revisados hoje (faixas roxa/marrom/preta), redacao do inicio do OpenCodeBridge
P_ROXA="$(cat <<'EOT'
Crie nesta pasta o backend .NET C# de cadastro de clientes e fornecedores: dominio, banco SQL (DDL, indices e constraints), repositorios com ORM, servicos de aplicacao e validacao de e-mail, telefone BR com DDD e CEP. Camadas em pastas; um projeto na raiz, compilando.
EOT
)"
P_MARROM="$(cat <<'EOT'
Crie nesta pasta o backend .NET C# de reembolsos corporativos (dominio, banco SQL, repositorios com ORM e API REST) e o frontend React em .tsx. O backend compila sem erro.
EOT
)"
P_PRETA="$(cat <<'EOT'
Crie nesta pasta o console app C# do Portal do Colaborador: dominio, repositorio com ORM, servico de aplicacao, DDL SQL e um perceptron simples. Um projeto na raiz, compilando.
EOT
)"


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
    echo "----- [tools-full:create]"; run_agent tools "$TOOLS_FULL_CRIAR_HELLO" 900
    check tools-full-create "[ -f '$CASE_DIR/App.csproj' ] && grep -qi 'hello world' '$CASE_DIR/Program.cs' && (cd '$CASE_DIR' && git log --format=%s 2>/dev/null | grep -qi 'hello world console')"
    if ff_stop; then ff_skipped "tools-full-adjust tools-full-run"; else
    echo "----- [tools-full:adjust]"; run_agent tools "$TOOLS_FULL_AJUSTAR_V2" 900
    check tools-full-adjust "grep -qi 'Hello World v2' '$CASE_DIR/Program.cs' && [ \$(cd '$CASE_DIR' && git log --oneline | wc -l) -ge 2 ] && (cd '$CASE_DIR' && git log --format=%s | grep -qi 'hello world v2') && (cd '$CASE_DIR' && dotnet build App.csproj -v q >/dev/null 2>&1)"
    if ff_stop; then ff_skipped "tools-full-run"; else
    check tools-full-run "( cd '$CASE_DIR' && dotnet run --project App.csproj -v q 2>&1 | grep -qi 'hello world v2' )"
    fi
    fi
  fi

  if quer mission-tools; then
    new_case mission-tools
    echo "----- [mission-tools:create] (build)"; run_agent build "$MISSION_TOOLS_CRIAR_PROJETO" 900
    check mission-tools-create "[ -f '$CASE_DIR/mcs/mcs.csproj' ] && ( cd '$CASE_DIR/mcs' && dotnet build mcs.csproj -v q >/dev/null 2>&1 )"
    if ff_stop; then ff_skipped "mission-tools-code"; else
    echo "----- [mission-tools:code] (build)"; run_agent build "$MISSION_TOOLS_ESCREVER_PROGRAMA" 900
    nums="$( cd "$CASE_DIR/mcs" && dotnet run --project mcs.csproj 2>/dev/null | grep -E '^-?[0-9]+$' | head -3 | tr '\n' ' ' )"
    [ "$nums" = "3628800 21 15 " ] || echo "(aceite: nums='$nums')"
    check mission-tools-code "[ '$nums' = '3628800 21 15 ' ]"
    fi
  fi

  # matriz 2x2: belts-<agente>-<cor>  (legado belts-azul/verde = build)
  quer_belt() {
    local caso="$1" cor="${1#belts-build-}"
    quer "$caso" && return 0
    [ "$cor" != "$caso" ] && quer "belts-$cor" && return 0
    return 1
  }
  belt_scope() {
    case "$1" in
      *-roxa)   echo "Cliente|Customer Fornecedor|Supplier" ;;
      *-marrom) echo "Reembolso|Refund|Reimbursement" ;;
      *-preta)  echo "Colaborador|Employee" ;;
      *-azul)   echo "Consulta" ;;
      *-verde)  echo "Reembolso|Refund|Reimbursement" ;;
      *) echo "" ;;
    esac
  }

  for belt in azul verde roxa marrom preta; do
      AG=build
      cor="$(printf '%s' "$belt" | tr a-z A-Z)"
      var="P_${cor}"; P="${!var}"
      caso="belts-$AG-$belt"
      quer_belt "$caso" || continue
      if ff_stop; then ff_skipped "$caso"; continue; fi
      new_case "$caso"
      secs=600   # teto unico de 10 min por passo de belt
      echo "----- [$caso] ($AG)"; run_agent "$AG" "$P" "$secs"
      # direcionamento unico: repara o aceite (sem .csproj, sem fonte ou build quebrado) com enunciado + erro + tabela
      missing=""
      if ! find "$CASE_DIR" -maxdepth 1 -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' 2>/dev/null | grep -q .; then missing="sem .csproj na raiz"; fi
      if ! find "$CASE_DIR" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' 2>/dev/null | grep -q .; then missing="$missing sem fonte .cs"; fi
      errs0="$(build_errors "$CASE_DIR")"
      if [ -n "$missing" ] || [ -n "$errs0" ]; then
        echo "----- [$caso] direcionamento build: ${missing:-erro de compilacao}"
        mv=""; case "$missing" in *"csproj na raiz"*) mv=" Se o projeto ja existe em subpasta, mova o conteudo INTEIRO dela para a raiz preservando as fontes (mv pasta/* . ; rm -rf pasta vazia); nunca mova apenas o .csproj e nunca crie um segundo projeto." ;; esac
        hints0="$(error_hints "$errs0")"
        secs2=600  # mesmo teto no reparo
        run_agent "$AG" "ENUNCIADO:
$P

O aceite do enunciado nao foi cumprido ($missing). Corrija o .csproj na raiz e os .cs,$mv compile com dotnet build ate rc=0 e nao repita read/build nem rode dotnet run. Use a tabela abaixo.
ERROS:
$(printf '%s\n' "$errs0" | head -8)
$hints0" "$secs2"
      fi
      # projeto unico na raiz: subprojeto aninhado compila vazio e mascara fonte nao compilada
      # aceite minimo: o .csproj da raiz compila (rc=0) e existe fonte .cs real fora de obj/bin
      src="find '$CASE_DIR' -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | grep -q ."
      rc=1
      root_projs="$(find "$CASE_DIR" -maxdepth 1 -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' 2>/dev/null)"
      nested_projs="$(find "$CASE_DIR" -mindepth 2 -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/.data/*' 2>/dev/null)"
      nroot=$(printf '%s\n' "$root_projs" | grep -c . || true)
      if [ -z "$root_projs" ]; then
        echo "(nenhum .csproj na raiz)"
      elif [ -n "$nested_projs" ]; then
        echo "(csproj aninhado: mantenha um unico .csproj na raiz)"
        printf '%s\n' "$nested_projs" | sed 's/^/  /' | head -3
      elif [ "$nroot" -ne 1 ]; then
        echo "(mais de um .csproj na raiz: $nroot)"
      else
        pj="$(printf '%s\n' "$root_projs" | head -1)"
        clean_nested "$CASE_DIR"
        rc=0
        ( cd "$CASE_DIR" && dotnet build "$(basename "$pj")" -v q "${DOTNET_SRC_EXCLUDE[@]}" >/dev/null 2>&1 ) || rc=1
      fi
      if [ $rc -ne 0 ]; then
        errs="$(build_errors "$CASE_DIR")"
        printf '%s\n' "$errs" | grep -v '^$' | head -3 | sed 's/^/  /'
        error_hints "$errs"
      fi
      # escopo: o artefato tem de citar o dominio do enunciado (compilar outro CRUD nao vale)
      for w in $(belt_scope "$caso"); do
        if ! grep -rqiE --include='*.cs' --include='*.sql' --exclude-dir=obj --exclude-dir=bin -- "$w" "$CASE_DIR" 2>/dev/null; then
          echo "(escopo: falta '$w' nos .cs/.sql)"; rc=1
        fi
      done
      check "$caso" "[ $rc -eq 0 ] && $src"
      case "$belt" in marrom|preta) partial_report "$caso" "$belt" ;; esac
  done

else

  ent_case() {
    local id="$1" p="$2" extra="${3:-true}"
    quer "$id" || return 0
    if ff_stop; then ff_skipped "$id"; return 0; fi
    new_case "$id"
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
  ent_case bus "$P_BUS" "grep -qi 'outbox' '$WS/bus.txt' && grep -qi 'idempot' '$WS/bus.txt'"
  ent_case erp "$P_ERP" "grep -q '8471' '$WS/erp.txt'"
  ent_case dashboard "$P_DASH" "grep -q 'AbortController' '$WS/dashboard.txt' && grep -qE 'useEffect|useLayoutEffect' '$WS/dashboard.txt'"

fi
  echo "== RESUMO: PASS=$pass FAIL=$fail SKIP=$skipped  (log: $LOG)"
  [ "$ABORT" = 1 ] && echo "== ABORTADO (fail-fast) no primeiro FAIL: $FIRST_FAIL"
} 2>&1 | tee "$LOG"

curl -s -m 10 -X POST "$COLLECTOR/api/dump" >/dev/null 2>&1 || true

python3 "$HERE/report.py" --since "$START_TS" --proto "$PROTO" --log "$LOG" \
  --title "Bateria WAR ($MODO) - opencode -> llama (tunel)" --out-dir "$HERE/reports"



