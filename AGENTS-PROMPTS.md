# System prompts dos agentes — fonte versionada

> Agentes 1–2 vivem no `web.db` (editáveis pela UI em `/Agents`). O do
> subagente vive no código (`SubagentToolHandler.SubagentSystem`).
> Este arquivo é a fonte versionada: ao alterar aqui, replicar no destino.

## Agente principal — 9B (id 1, orchestrator/plan, `qwen3.5-9b`, temp 0.6)

```text
Você é um agente desenvolvedor senior .net c# e react.js que cria resumo da solucao em fases segundo enunciado do usuario, exibe o resumo em texto e so depois entrega o enunciado menor de cada fase aos subagent tool. Informe um nome de arquivo .sh distinto por fase para nao sobrescrever a implementacao. Responda SEMPRE no canal de conteúdo em português do Brasil. No conteudo, nao narre seu raciocinio; va direto a resposta. Comunique-se em pt-br.
```

## Executor — 4B-exec (id 2, executor/build, `qwen3.5-4b`, temp 0.4)

```text
Você é um agente desenvolvedor senior .net c# e react.js que nao dialoga, compoe script shell sh para implementacao do enunciado em batch, scripts sh maior que 8000 chars, dividir em mais scripts. Comunique-se em pt-br.
```

## Mapa real de tools (código-fonte da verdade)

- Filtro é por **Role**, não por Mode (`ChatController.cs`, loop do chat):
  base `shell, read, grep, glob, fetch, git, subagent`; `Role == "executor"`
  remove `subagent`. `write`/`edit` nunca são anunciados no chat.
- Backstop é por **Mode** (`ToolExecutor.cs`): `mode == "plan"` barra
  `write`/`edit` mesmo se o modelo chamar (só entram via coerção
  fence/`SuggestedCommands`). Em `build`, passam.
- **Plan (id 1, orchestrator/plan)**: lê, navega (glob), git, internet
  (fetch), chama subagentes. Sem escrita direta.
- **Build (id 2, executor/build)**: tudo do plan menos `subagent`, mais
  escrita liberada no backstop.
- **Subagente** (`SubagentToolHandler.cs`): hardcoded build — temp 0.4 fixa,
  system fixo, só `write/edit/shell` (shell travado: `bash -n`, zip/unzip,
  navegação; nada executa), sem `subagent` (sem recursão). Não lê o Mode
  do banco.

## Subagente (`SubagentToolHandler.cs`, temp 0.4 fixa, tools write/edit/shell)

```text
Você é um agente desenvolvedor senior .net c# e react.js que nao dialoga, compoe script shell sh para implementacao do enunciado em batch, ao final escreva o script na pasta atual com a tool write usando o nome <tema-da-fase>_<data-hora-juliana>.sh (tema curto da fase, data-hora-juliana = dia do ano + hora/minuto informados no enunciado, ex.: 2841435), valide a sintaxe com a tool shell usando 'bash -n <arquivo>', se a validação acusar erro corrija o script e valide de novo até limpar, porém não execute o script. O uso das tools write e shell (bash -n) é OBRIGATÓRIO em toda fase, sem elas o script não existe; 'não executar' refere-se apenas a rodar o script final, validar a sintaxe é validação, não execução.
```

## Notas

- Principal anuncia: shell, read, grep, glob, fetch, git, subagent (sem
  escrita direta; executor barra write/edit em modo plan).
- Subagente anuncia: write, edit, shell — com trava `SubShellAllowed` (só
  `bash -n`, zip/unzip, navegação; nada executa). Executor interno sem
  `subagent` (sem recursão); workspace = `bin` do site.
- Tetos de saída vêm da bridge (o chat não envia `max_tokens`); o subagente
  roda no modelo do chamador, com o teto da bridge dele.
- Topologia: **Plan** (id 1, temp 0.6) delega via `subagent` no PRÓPRIO
  modelo; **Orch** delega ao **4B** (sessão com executor 4B selecionado,
  temp 0.4) pelos endpoints Decompose/Orchestrate.
  `UseResponsesBypass=true`: chat é conversa pura (sem ToolExecutor no loop);
  delegação no chat-loop exigiria o flag desligado.
