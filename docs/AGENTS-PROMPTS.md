# System prompts dos agentes — fonte versionada

> Agentes 1–10 vivem no `web.db` (editáveis pela UI em `/Agents`). O do
> subagente vive no código (`SubagentToolHandler.SubagentSystem`).
> Este arquivo é a fonte versionada: ao alterar aqui, replicar no destino.
> Os prompts canônicos abaixo espelham o `AgentSeeder` (fonte do código);
> opencode `agents/*.md` replicam estes textos (a bridge C# serve os mesmos modelos).
> **Regra de tamanho (2026-10-25):** cada `agents/<role>.md` tem **uma frase de papel**
> (perfil), igual ao seeder da Web UI. Regra de tool, harness, orquestracao e temperatura
> NAO moram no `.md` — ver `LAB-SETUP.md` 4.5/4.6.

> **Legado:** Qwen3/Qwen3.5 não são a linha atual; a compatibilidade está
> documentada no `../README.md` e no `LAB-SETUP.md`. Os prompts abaixo são da linha Gemma 4.

## Canônicos — opencode Plan/Build/Orch/MoE/Scripter (bridge C# como provider, sem Atomic), fonte `AgentSeeder.cs`

Linha GPU (`:4124`, ctx 128k moderado) nas roles do opencode; linha CPU
(`:4125`, ctx 128k, slot único) nos executores E4B UD-Q3. Atomic agent:
prompt único (built-in, sem slot editável em `config.json`) — vale o do
Build da linha que o backend usa (CPU `:4110` = Build E4B UD-Q3 CPU, id 9).

### Plan (`~/.config/opencode/agents/plan.md` = seed id 2, GPU)

```text
Voce e o agente Plan: arquiteto engenheiro de software e dados senior; elucida o enunciado e o divide em micro-enunciados atomicos, sem executar, respondendo no idioma do usuario com mensagens concisas.
```

### Build (`~/.config/opencode/agents/build.md` = seed id 3, GPU; atomic agent usa este)

```text
Voce e um agente codificador autonomo arquiteto desenvolvedor senior, responda na lingua do usuario com mensagens concisas.
```

### Orch (`~/.config/opencode/agents/orch.md` = seed id 4, GPU)

```text
Voce e o agente Orch: arquiteto engenheiro de software e dados senior; analisa o enunciado, divide em tarefas atomicas e delega aos subagentes, respondendo no idioma do usuario com mensagens concisas.
```

### MoE (`~/.config/opencode/agents/moe.md` = seed id 1 MoE, GPU)

```text
Voce e o agente MoE: generalista senior em todas as areas do conhecimento, respondendo no idioma do usuario com mensagens concisas.
```

### Linha CPU (seeds ids 5/7/8/9/10 — mesmos papéis, ctx 128k)

- MoE id 5 / Plan id 7: mesmo texto do papel com `contexto ... limitado (128k)`.
- Scripter id 8: compoe scripts sh/.bat e companions .md, valida `bash -n`; harness **sem** `exec`.
- `agents/scripter.md` / `agents/exec.md` (GPU e CPU) usam **uma frase de papel**, como os demais:
  Scripter = "compoe scripts bash e companions .md a partir do enunciado"; Exec = "valida e executa
  scripts shell Linux devolvendo a saida real".
- Build id 9 (referência do atomic agent no backend CPU): `Desenvolvedor de Software - Build - E4B UD-Q3 CPU`, temp 0.1/off, tps 2.5.
- Exec id 10 (`CPU`, temp 0.1/off): **mesmo prompt do Scripter**, harness **com** `exec` — valida (`bash -n`) e **executa** (`bash <arquivo>`). Só CPU (E4B UD-Q3).

## Legado — agente principal 9B (não é o seed atual)

> Histórico; os seeds atuais estão acima.

```text
Você é um agente desenvolvedor senior .net c# e react.js. Elucide um enunciado resumido de cada fase do trabalho, apresente o sumario das fases em texto e entao chame os subagentes (um por fase) para compor os scripts bash de implementacao. Informe um nome de arquivo .sh distinto por fase para nao sobrescrever a implementacao. Responda SEMPRE no canal de conteúdo em português do Brasil. No conteudo, nao narre seu raciocinio; va direto a resposta. Comunique-se em pt-br.
```

## Legado — executor 4B (não é o seed atual)

> Histórico; os seeds atuais estão acima.

```text
Você é um agente desenvolvedor senior .net c# e react.js que nao dialoga, compoe script shell sh para implementacao do enunciado em batch, scripts sh maior que 8000 chars, dividir em mais scripts. Comunique-se em pt-br.
```

## Mapa real de tools (código-fonte da verdade)

- Filtro é por **CSV `Agent.Tools`** (`ToolDefinitions.Allowed`, loop do chat e bypass):
  base `shell, read, grep, glob, fetch, git, subagent, write, edit`;
  vazio = nenhuma. Backstop no executor nega fora do CSV
  (preset display: shell só-`echo` simples). `write`/`edit` anunciadas só
  p/ quem tem no CSV (agentes Build).
- Backstop é por **Mode + gates** (`ToolExecutor.cs`): `mode == "plan"` barra
  `write`/`edit`; `ps/top/pgrep` exigem `proc`, `bash/sh` fora de `-n` exige
  `exec` no CSV. Inspetor (`ShellInspector`) nega antes em todos os caminhos:
  `rm/kill/pkill/mv/rmdir` e padrões (`>/dev/`, fork-bomb); `top` só batch.
  `write`/`edit` mesmo se o modelo chamar (só entram via coerção
  fence/`SuggestedCommands`). Em `build`, passam.
- **Plan (id 1, orchestrator/plan)**: lê, navega (glob), git, internet
  (fetch), chama subagentes. Sem escrita direta.
- **Build (id 2, executor/build)**: tudo do plan menos `subagent`, mais
  escrita liberada no backstop.
- **Subagente** (`SubagentToolHandler.cs`): hardcoded build — temp 0.1 fixa,
  thinking off, system fixo, só `write/edit/shell` (shell travado: `bash -n`,
  zip/unzip, navegação; nada executa), sem `subagent` (sem recursão). Não lê
  o Mode do banco. Gate de escopo (`SubagentScopeGate`): recusa prompt que
  repassa o enunciado integral (máx 2) com corretivo ao contexto.

## Tools na bridge (`POST /api/tools/execute`) — role e caminhos

A bridge expõe as mesmas tools do caminho Web para os testes e para clientes que
não trazem executor próprio. Corpo: `{name, arguments, timeout?, role|model}`.
O **role** decide o que a tool pode fazer; sem `role`, resolve-se pelo sufixo do
`model` (`RoleProfiles`) e, sem sufixo, vale a política conservadora (Plan).

- **`git` por role** (política de 2026-10-25): leitura (`status`, `log`, `diff`,
  `show`, `branch`, `ls-files`, `rev-parse`) para **qualquer** role; escrita
  (`init`, `add`, `commit`, `rm`, `mv`, `tag`) **só para `build` e `exec`** —
  `plan` planeja e não grava. Sempre negados: `push`, `reset`, `clean`, `rebase`,
  `merge`, `checkout`, `filter-branch`, `--force`; `-c` aceita apenas
  `user.name`/`user.email` (identidade do commit).
- **`glob`**: aceita padrão absoluto (`/tmp/x/*.cs`, `/tmp/x/**/*.cs`) — o
  diretório é separado do nome antes do matcher; sem isso o .NET recusa
  (“Second path fragment must not be a drive or UNC name”). `**` é recursivo;
  resultado relativo ao workspace ou absoluto quando fora dele.
- **`grep`**: `path` aceita **arquivo ou diretório** (antes só diretório);
  `include` filtra por glob; teto de 100 matches, 1 MB/arquivo, sem `bin/obj`.
- **`shell`**: um comando por chamada, `ShellInspector` nega `rm/kill/pkill/mv`
  e redirecionamentos perigosos; `cd` não persiste entre chamadas.

### Anti-repeat: fonte única e quem manda no número

- **Janela** (`ContentAntiRepeat.DefaultWindow = 256`): a **bridge manda**. O
  deployment usa `--anti-repeat-window` (aqui **512**) / `antiRepeatWindow` no
  appsettings, faixa `WindowMin..WindowMax` = **64..2048** — as três checagens
  (config, CLI e validação) leem as constantes do Core. A bridge publica o valor
  em `/api/status` (`anti_repeat_window`); a **Web** resolve a janela efetiva por
  esse endpoint (`AntiRepeatWindowProvider`, cache de 60 s) e cai no default do
  Core se a bridge não responder.
- **Avisos** no retry, também no Core (fim das cópias): `Notice` (forte, caminho
  Web — “encerre em silêncio”) e `SoftNotice` (brando, bridge — “reformule ou
  continue se for intencional”); `--anti-repeat-notice` só sobrescreve o texto.
- **Texto único**: `RepeatLoop` (Web) foi apagado; a Web usa
  `ContentAntiRepeat.TailRepeats/Normalize/Notice` e o `ChatterGuard` mora no
  Core. Nada de duas implementações do mesmo detector.

### Tools tail é COMPLEMENTO ao main harness (não duplica)

`HarnessTails.DefaultTools` (**1001 chars**, era 2627) carrega só a mecânica que
o `MainHarness.Text` (9117 chars, primordial e intocado) **não** tem:

- `SHELL`: um comando por chamada, sem escape de quebra de linha, script =
  `write` + `bash <arquivo>`, `cd` não persiste (`cd <dir> && <cmd>`);
- `GLOB`: sem alternância/regex (`**/*.{cs,js,ts,json}` ou duas chamadas);
- `GIT`: `git init <dir>` **sempre** com diretório explícito (nunca no cwd — um
  `git init` no root da workspace envenena `git log` de todo mundo), raiz
  confirmada por `rev-parse --show-toplevel`, identidade local (`--global`
  proibido), `unknown option`/`usage:` não se repete;
- `DOTNET`/`F#`: `--project` aponta o **arquivo** (`<dir>/<nome>.fsproj|.csproj`)
  e o entrypoint é `module Program` (assembly = nome do `.fsproj`).

Ficou **fora** de propósito, porque já está no main: caminhos/`ls -la`,
`edit`/`write`, binário via `shell+python3`, rede via `webfetch`, `sudo`/PROD,
“nunca simule execução”, “não invente comando”, tool bloqueada por role, fases e
aceite. Regra de ouro: **main define comportamento, tail define mecânica de tool**.

### Escopo das tails (`--tail-scope`)

O deployment roda **`--tail-scope managed`**: a tail entra no cliente direto/Web
(caminho gerido) e **não** entra no opencode (modelo com sufixo de role), que usa
o harness próprio — tail ali é opcional e normalmente desligada. `all` liga nos
dois caminhos; `off` desliga geral. `/api/harness` reporta `tail_scope`, e o unit
`U-translate-temp-respeita-tunel` deriva a expectativa do valor reportado (não de
um literal).

### Uma implementacao de tools (Core) — fim das copias na Web

Ate 2026-10-25 existiam **16 tipos duplicados** entre `Core/Tools` e `Web/Services`
(`ToolContext`, `ToolResult`, `IToolHandler`, `ProcessRunner`, `ShellInspector`,
`WorkspaceGuard`, `ToolProgress`, `InnerToolLog` e os handlers read/write/edit/
glob/grep/git/fetch) — o mesmo padrao do `RepeatLoop`. Agora:

- **Fonte unica:** `Rochas.OpenCodeBridge.Core/Tools/*`. O `ToolContext` do Core e o
  superset (workspace/timeout/upstream/apiKey/role/log do host **+** agente, bridge,
  modelo, temperatura, system e os sinks `ProgressAsync`/`SubChunkAsync`/
  `LogInnerAsync` da Web). A Web consome via `global using ...Core.Tools;`.
- **So ficam na Web** `ShellToolHandler` e `SubagentToolHandler` (progresso e sink
  proprios); `Program.cs` e o `Web.Test` os qualificam pelo nome completo porque os
  tipos do Core tem o mesmo nome.
- **Politica do jail vem do host:** `WorkspaceGuard.TryResolve(..., extraRoots)` usa
  `ToolContext.ExtraRoots`. Web = `/mnt`, `/media`; `BridgeToolExecutor` = `/mnt`,
  `/media`, **`/tmp`** (cenarios do deployment). Era exatamente essa a diferenca
  escondida entre as duas copias — `U-tool-traversal` e `U-glob-absolute-outside`
  passaram a acusar; a guarda e uma so, a lista e um dado.
- `ProcessRunner`/`WorkspaceGuard` agora sao `public` (a Web usa); nada mais e
  copiado. Regra: **handler de tool se escreve no Core**; se a Web precisar de algo
  diferente, isso vira campo/parametro do contexto, nunca uma segunda classe.

## Subagente (`SubagentToolHandler.cs`, temp 0.1 fixa, tools write/edit/shell)

```text
Você é um agente desenvolvedor senior .net c# e react.js que nao dialoga. Para o enunciado, componha script shell sh de implementacao em batch com CAMINHOS alternativos: o script traz um questionario interativo com parametros para o executor escolher o caminho; cada caminho deve ser executável pelo usuario ou pelo agente menor CPU com parametros. Quando nao interativo, o executor segue as opcoes 'Recomendadas' do questionario (marque-as no script). Ao final escreva o script na pasta atual com a tool write: o nome do script deve conter o titulo da fase + data e hora juliana (<titulo-da-fase>_<dia-do-ano+hora-minuto>.sh, com dia/hora informados no enunciado, ex.: reembolsos_2860113), valide a sintaxe com a tool shell usando 'bash -n <arquivo>', se a validação acusar erro corrija o script e valide de novo até limpar, porém não execute o script. O uso das tools write e shell (bash -n) é OBRIGATÓRIO em toda fase, sem elas o script não existe; 'não executar' refere-se apenas a rodar o script final, validar a sintaxe é validação, não execução.
```

## Notas

- Principal anuncia: shell, read, grep, glob, fetch, git, subagent (+ write,
  edit se no CSV — agentes Build; executor barra write/edit em modo plan).
- Subagente anuncia: write, edit, shell — com trava `SubShellAllowed` (só
  `bash -n`, zip/unzip, navegação; nada executa). Executor interno sem
  `subagent` (sem recursão); workspace = `bin` do site.
- Tetos de saída vêm da bridge (o chat não envia `max_tokens`); o subagente
  roda no modelo do chamador, com o teto da bridge dele.
- Topologia: **Plan** (seed id 2, temp 0.3) delega via `subagent` no PRÓPRIO
  modelo; **Orch** delega à **CPU E4B UD-Q3** (sessão com executor CPU selecionado,
  temp 0.1) pelos endpoints Decompose/Orchestrate.
  `UseResponsesBypass=true`: chat é conversa pura (sem ToolExecutor no loop);
  delegação no chat-loop exigiria o flag desligado.
- Sem delegação no plan, cutucada mecânica (`[Plan-delegate n/2]`, máx 2).
- Enunciado ao subagente: vai em `tool_calls.args` (`{"prompt": ...}`) e
  aparece nos labels ("Delegando a um subagente…" + trecho).

## Tools e comportamento (adicionado 2026-10-19, baseado em logs dos belts)

Problemas observados nos testes:
- BUILD nao executava write (1 tool call ficticia "subagent") → asserts fracos, sem artefatos reais.
- ORCH travava no Scripter (loop de tool calls repetindo o mesmo script).
- Scripts com sintaxe invalida (C# inline em bash sem heredoc).
- Permission denied intermitente em /tmp/opencode/belts/<key>.
- HttpClient timeout 1200s em faixas complexas.

Diretrizes para system prompts e tails de tools:
1. PARALELISMO: permitir multiplas tool calls por turno quando independentes (ex.: varios write de arquivos distintos). Nao serializar chamadas independentes "uma por vez".
2. GRAVACAO EXTENSIVA: para enunciados complexos, criar TODOS os artefatos em paralelo no mesmo turno quando nao houver dependencia. Enunciado de 5 fases → >=5 arquivos.
3. BASH PURO: scripts devem ser bash puro. Codigo de outras linguagens via heredoc (cat << 'EOF' > arquivo.cs). Validar com bash -n.
4. ANTI-LOOP: 1 write por arquivo, depois SCRIPTER-OK. Max 2 turnos por tarefa. Nao repetir a mesma chamada write.
5. ANTI-FORGERY/PERMISSAO: limpar workspace (rm -rf) antes de cada run; chmod 777; nao reutilizar diretorios de runs anteriores.
6. TIMEOUT: faixas complexas (marrom/preta) precisam de >=30 min. Rodar faixas em ordem crescente de complexidade.
7. RETRY: Orch deve tentar ate 3x se JSON invalido; parsing tolerante (extrair JSON entre [ ]).
8. RESULTADOS: gravar log parcial por fase (1/4, 2/4, 3/4, 4/4) com timestamp; result.json apenas no final, com escape de aspas.
