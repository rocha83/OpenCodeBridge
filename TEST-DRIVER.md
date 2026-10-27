# TEST-DRIVER — TestCircuitDriver, WarTemplate e Execution

Este documento descreve **toda** a lógica do driver de testes da WAR depois da
migração para C#. A orquestração não depende mais de `bash`, `flock(2)`, `/proc`
ou de paths Linux: o caminho canônico é a solução `Rochas.OpenCodeBridge.slnx`.
Os scripts em `scripts/war/` ficam apenas como referência histórica.

## 1. Camadas (quem faz o quê)

```
Program.Main
   │  (args --war*)
   ▼
WarTemplate.RunMain ──────────────► CLI, parse, levels, status, pausa
   │
   │  por level: new TestCircuitDriver(options, template)
   ▼
TestCircuitDriver ────────────────► lock, pausa, steps, build, redeploy,
   │                                  restart, artefatos, gate
   │  Execution.RunAsync(...)
   ▼
Execution ─────────────────────────► ProcessStartInfo, timeout, kill de árvore
```

| Camada | Arquivo | Responsabilidade |
|---|---|---|
| `TestCircuitDriver` | `Rochas.OpenCodeBridge.Test/TestCircuitDriver.cs` | Núcleo genérico e portável: executa um `CircuitTemplate`, controla lock, pausa, steps, recompilação, redeploy, retry/restart, summary/events e gate. Não conhece WAR. |
| `CircuitTemplate` | `Rochas.OpenCodeBridge.Test/TestCircuitDriver.cs` | Contrato do circuito concreto: `BuildSpecs`, `DeploymentSpecs`, `PreflightAsync`, `Steps` (DAG), `NextSteps`, `GatePolicy` e hooks de início/fim. `BuildSpec`/`DeploymentSpec` continuam como atalho de um único serviço. |
| `WarTemplate` | `Rochas.OpenCodeBridge.Test/WarTemplate.cs` | A WAR como template: levels 1–3, preflight de llama/bridge, passos, parser PASS/FAIL, STALE e gate. Também hospeda a CLI `--war*`. |
| `Execution` | `Rochas.OpenCodeBridge.Test/Execution.cs` | Primitiva de processo: resolve executável no `PATH`, roda com timeout, captura stdout/stderr e mata a árvore em timeout/cancelamento. |
| `CircuitLock` | `Rochas.OpenCodeBridge.Test/TestCircuitDriver.cs` | Lock único de runner via `FileShare.None`; portável Windows/Linux. |
| Testes do núcleo | `Rochas.OpenCodeBridge.Test/UnitCircuitTests.cs` | Provas de step/gate, pause, retry/restart, build incremental, deploy, lock e template. |

O fluxo de dependência é sempre **para baixo**:

```
TestCircuitDriver -> WarTemplate -> Execution
```

- `TestCircuitDriver` chama os hooks do template.
- `WarTemplate` descreve comandos (`CircuitCommand`) e vereditos.
- `Execution` é a única camada que cria processo.
- `Execution` não conhece WAR; `WarTemplate` não cria processo; `TestCircuitDriver`
  não conhece bridge/llama/opencode.

### Fronteira Core x consumidor

O driver e a suíte são **consumidores** do Core, como a Web UI, o OpenCode e a
CLI. Catálogo/schema de tools, execução, permissão/workspace guard, process
runner, preflight, harness/tails, sanitização, sampling e parsing ficam em
`Rochas.OpenCodeBridge.Core`. O consumidor faz orquestração, fixture, transporte
HTTP, assertiva e evidência; não cria `ToolCatalog`, `Tools()`, `Exec()`,
`LocalExec()`, `Shell()` de tool nem schema paralelo.

## 2. Ordem de execução de um level

`TestCircuitDriver.RunAsync` executa exatamente esta sequência:

1. **Lock** (`CircuitLock.TryAcquire`), se `CircuitOptions.AcquireLock=true`.
   Se outro runner tem o lock, retorna `BLOCKED` (exit 3) sem executar steps.
2. **Pausa** (`WaitIfPausedAsync`): se o marcador existe, espera em poll de 500 ms.
3. **`Template.OnRunStart`**: a WAR grava `run_start` e os hashes iniciais.
4. **`Template.BuildSpecs`**: para cada build retornado, chama `RecompileAsync`.
   Falha de qualquer build → `BLOCKED` (exit 3). O default encapsula o único
   `BuildSpec`; um pipeline multi-serviço devolve N specs.
5. **`Template.OnBuildCompleted`**: chamado por build; WAR recalcula hashes e
   publica `dlls_ready` se os binários mudaram.
6. **`Template.DeploymentSpecs`**: para cada deploy retornado, chama
   `RedeployAsync`. Falha de start/health → `BLOCKED` (exit 3).
7. **`Template.OnDeployCompleted`**: chamado por deploy; WAR recalcula hashes.
8. **`Template.PreflightAsync`**: falha → `BLOCKED` (exit 3).
9. **`Template.Steps` + `NextSteps`**: o plano é um DAG. Um step só entra quando
   todos os `DependsOn` já terminaram; `When=false` vira SKIP; `NextSteps` pode
   acrescentar passos depois de um resultado (branch). Com
   `MaxParallelSteps=1` (default) a execução é sequencial; com N>1, até N steps
   independentes rodam em paralelo. Cada step tem timeout, restart, log e
   veredito próprios.
10. **`Gate`**: lê `summary.tsv`, conta PASS/FAIL/SKIP/STALE e pergunta ao
    `Template.GatePolicy` se o resultado é verde.
11. **`Template.OnRunEnd`**: WAR publica `run_end` e imprime o caminho dos artefatos.
12. **Retorno**: exit 0 (verde), 1 (gate vermelho) ou 3 (BLOCKED).

`BLOCKED` sempre executa `Gate()` e `OnRunEnd` para deixar rastro em
`summary.tsv`, `summary.txt` e `events.jsonl`. A exceção é o bloqueio por lock:
nesse caso o driver nem cria/executa o restante do run.

## 3. Controle de execução (lock único)

- Implementação: `CircuitLock.TryAcquire(path)` abre o arquivo com
  `FileMode.OpenOrCreate`, `FileAccess.ReadWrite` e **`FileShare.None`**.
- O SO libera o lock quando o processo morre. Não existe “lock stale” para
  apagar: um arquivo de 0 bytes não bloqueia ninguém.
- O PID/máquina/hora são gravados no arquivo apenas como diagnóstico.
- `CircuitLock.DescribeOwner(path)`:
  - arquivo vazio e sem dono → `livre`;
  - arquivo aberto por outro runner → `lock em uso` (o SO recusa a leitura).
- `--war-status` mostra o lock, a pausa, os binários e os últimos levels.
- `--war-all` mantém **um único lock** para a trilha inteira e passa
  `AcquireLock=false` para os drivers dos levels; assim nenhum outro runner
  entra entre L1 e L3.
- `--war-gate-only` é leitura pura: não adquire lock.

### Exit codes

| Código | Significado |
|---|---|
| `0` | Gate verde (ou comando de controle concluído). |
| `1` | Gate vermelho (`FAIL`/`STALE`) com `RequireGreen=true`. |
| `2` | Erro de CLI/argumento. |
| `3` | `BLOCKED`: lock, build, redeploy ou preflight. |

## 4. Pausa e retomada

A pausa é um **arquivo marcador** (default `logs/levels/pause`, override
`--war-pause-file` ou `WAR_PAUSE`). Ela é verificada em pontos seguros:

- antes do build;
- antes do redeploy;
- antes do preflight;
- antes de cada step;
- antes de cada tentativa de restart.

Comandos:

```bash
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-pause
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-resume
```

Semântica importante: **a pausa não suspende um processo já iniciado**. Se um
step está rodando, ele termina; a pausa entra em vigor antes do próximo step ou
da próxima tentativa. Isso evita matar um teste no meio e produzir falso FAIL.
A API `TestCircuitDriver.Pause()`/`Resume()` existe para uso programático; a CLI
apenas cria/remove o mesmo marcador.

## 5. Steps, estado e artefatos

Cada step é um `CircuitStepSpec`:

```csharp
new CircuitStepSpec(
    Name: "tools-full-bridge",
    Command: new CircuitCommand("dotnet", args, root, env),
    TimeoutSeconds: 7200,
    Mode: "bridge",
    RunTag: gitHash,
    FollowLogPath: bridgeLog,
    FollowLogOutputPath: ".../steps/tools-full-bridge.bridge.log",
    Restart: new CircuitRestartPolicy(...),
    Verdict: run => ...,
    DependsOn: new[] { "build-ok" },     // opcional: DAG
    When: driver => File.Exists(...));  // opcional: condicao
```

`DependsOn` só libera o step quando os nomes citados já têm resultado (PASS,
FAIL, SKIP ou STALE). `When` é avaliado imediatamente antes do step; falso vira
`SKIP` e não entra no `summary.tsv`. Se o predicado lançar, o step vira `FAIL`
com `cause=condicao: ...` e o gate fica vermelho.

### 5.1 Retomada por `.done`

- `state/<step>.done` contém `rc VERDICT segundos`.
- Se `.done` existe, o step é **SKIP** e **não** entra no `summary.tsv`.
- Para refazer um step, apague o `.done` correspondente ou use um stamp novo.
- `state/<step>.attempt` guarda o número da última tentativa.

### 5.2 Logs

| Caminho | Conteúdo |
|---|---|
| `steps/<step>.test.log` | stdout+stderr do processo de teste. |
| `steps/<step>.bridge.log` | fatia do log da bridge entre o início e o fim do step (quando `FollowLogPath` é informado). |
| `steps/build-slnx.build.log` | saída do `dotnet build`. |
| `steps/bridge-redeploy.deploy.log` | stop/start/health do redeploy. |
| `summary.tsv` | uma linha por step executado: `step`, `verdict`, `secs`, `rc`, `cause`. |
| `summary.txt` | log humano do run (mesmas mensagens do console + tails). |
| `events.jsonl` | fatos estruturados, uma linha JSON por decisão. |

### 5.3 Eventos

`TestCircuitDriver` publica:

- `pause_request`, `pause_enter`, `pause_exit`, `resume_request`;
- `build_skip`, `build_start`, `build_end`;
- `deploy_skip`, `deploy_start`, `deploy_end`;
- `step_start`, `step_restart`, `step_stale`, `step_end`;
- `circuit_blocked`, `gate`.

`WarTemplate` publica:

- `run_start`, `dlls_ready`, `preflight`, `preflight_fail`, `run_end`.

O `summary.tsv` é derivado dos steps; `summary.txt` e `events.jsonl` são os
artefatos de auditoria. O gate nunca “inventa” PASS: ele lê o que foi gravado.

### 5.4 Parser de veredito (WAR)

`WarTemplate.ParseLogVerdict`:

- varre o `steps/<step>.test.log`;
- se encontra `FAIL ` → FAIL e para;
- se encontra `PASS ` → PASS (mas continua procurando um `FAIL` posterior);
- `rc != 0` com PASS vira FAIL;
- sem PASS/FAIL → SKIP;
- FAIL recebe `Classify`:
  - `timeout` (rc 124/137);
  - `infra` (`Connection refused`, `No route to host`, DNS, corpo vazio);
  - `executor` (`calls=)`);
  - `bridge` (`ERRO:`, `ok=false`, `permission`, `negad`);
  - `fixture` (`artefato ... conteudo=''`);
  - `undetermined`.

**STALE:** depois do step, a WAR compara o hash SHA-256 (16 hex) do binário de
teste (ou Web.Test) com o hash do início do run. Se mudou, o veredito vira
`STALE`, o `.done` é apagado e o baseline é atualizado. Um binário trocado no
meio do run não pode valer PASS nem FAIL.

## 6. Recompilação (build)

`TestCircuitDriver.RecompileAsync`:

- recebe `CircuitBuildSpec` com `Command`, `Outputs`, `InputRoots`, `Force`;
- **pula** se todos os outputs existem e nenhum input é mais novo que o output
  mais antigo;
- **roda** se falta output, se `Force=true` ou se algum input mudou;
- inputs considerados: `.cs`, `.csproj`, `.props`, `.targets`, `.slnx`, `.json`,
  `.razor`, `.cshtml`, `.resx`, `.config`;
- diretórios `bin`, `obj`, `.git`, `.vs` e `node_modules` são ignorados;
- falha de build → `Ok=false`; o `TestCircuitDriver` marca `BLOCKED` e o run
  termina em exit 3 sem executar steps.

`CircuitTemplate.BuildSpecs` permite N builds independentes (ex.: API, worker,
frontend); o driver roda na ordem retornada e para no primeiro que falhar. O
`CircuitBuildResult` carrega `Name` para o hook `OnBuildCompleted` saber qual
serviço terminou. `BuildSpec` (um único) continua funcionando via default.

Na WAR, `WarTemplate.BuildSpec` monta:

```bash
dotnet build Rochas.OpenCodeBridge.slnx -c Release --nologo -v:minimal
```

Outputs verificados: `Rochas.OpenCodeBridge.dll`,
`Rochas.OpenCodeBridge.Test.dll` e `Rochas.OpenCodeBridge.Web.Test.dll`.

Flags:

- default: build incremental (automático);
- `--war-rebuild`: força o build;
- `--war-no-build`: não compila; o preflight exige os binários existentes.

## 7. Redeploy

O `TestCircuitDriver` tem `RedeployAsync(CircuitDeploymentSpec)` genérico:

1. `Stop` (opcional); falha de stop é registrada, mas não derruba o redeploy
   (serviço já parado é normal).
2. `Start` (obrigatório quando o spec existe).
3. `Healthy` (opcional): poll até `TimeoutSeconds`, com `IntervalMs`; timeout
   vira `rc=124` e `cause=health timeout`.
4. Log em `steps/<nome>.deploy.log`; eventos `deploy_start`/`deploy_end`.

`CircuitTemplate.DeploymentSpecs` permite N serviços; o driver executa na ordem
retornada e para no primeiro que falhar. `DeploymentSpec` (um único) continua
funcionando via default. O `CircuitDeploymentResult` carrega `Name` para o hook
`OnDeployCompleted`.

Na WAR o redeploy é **opt-in** e desligado por default, porque os testes devem
se adaptar à bridge em execução; a bridge não muda para o teste. Para ligar:

```bash
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- \
  --war-level 2 \
  --war-redeploy \
  --war-redeploy-stop /caminho/stop-bridge \
  --war-redeploy-start /caminho/start-bridge \
  --war-redeploy-timeout 120
```

`--war-redeploy-stop` é opcional; `--war-redeploy-start` é obrigatório.
Cada argumento extra usa `--war-redeploy-stop-arg V` /
`--war-redeploy-start-arg V` (repetível). O health default é
`<bridge>/api/status` com Bearer; override em `--war-health-url`.

O redeploy **não** altera `max_tokens`, `ctx`, `main_harness` ou tails. Ele só
para/sobe o processo configurado pelo operador.

## 8. Restart do teste (retry)

`CircuitRestartPolicy`:

```csharp
new CircuitRestartPolicy(
    MaxAttempts: 2,          // total de tentativas
    DelaySeconds: 5,
    ShouldRetry: (run, outcome) => ...);
```

- `MaxAttempts=1` é o default: nenhum restart.
- Sem `ShouldRetry`, só reinicia **timeout/crash** (`rc=124` ou `137`), nunca um
  FAIL de assertiva.
- `--war-retries N` define o total de tentativas (1..10).
- `--war-retry-delay S` define a espera entre tentativas.
- `--war-retry-all` reinicia também FAIL de assertiva (use com consciência:
  pode mascarar flakiness; o ACEITE não muda).
- Cada tentativa incrementa `state/<step>.attempt` e publica `step_restart`.
- O `summary.tsv` recebe **uma** linha por step (a última tentativa).
- Um PASS em retry é PASS; um FAIL final continua FAIL. Não existe “PASS por
  repetição infinita”.

## 9. Preflight da WAR

`WarTemplate.PreflightAsync` roda antes de qualquer step:

| Check | O que verifica | Falha |
|---|---|---|
| `llama_<porta>` | `GET /health` do upstream com `"ok"`. | BLOCKED |
| `llama_<porta>_props` | `GET /props` e `n_ctx`/`total_slots`/`model_path`. | BLOCKED |
| `bridge_status` | `GET /api/status` com `model`, `upstream`, `temperature`, `main_harness`, `harness_tails`, `context_tokens`. | BLOCKED |
| `bridge_upstream` | upstream da bridge == `--war-upstream`. | BLOCKED |
| `bridge_context_tokens` | `context_tokens` da bridge == `n_ctx` do slot. | BLOCKED |
| `test_binario` | `Rochas.OpenCodeBridge.Test.dll` existe. | BLOCKED |
| `webtest_binario` | `Rochas.OpenCodeBridge.Web.Test.dll` existe. | BLOCKED |
| `opencode_cli` | `opencode` no `PATH`; versão registrada. | BLOCKED |
| `sem_run_orfao` | nenhum processo `Rochas.OpenCodeBridge.Test` vivo. | BLOCKED |
| `disco` | ≥ 2048 MB livres no volume dos artefatos. | BLOCKED |
| `gpu` | `nvidia-smi` se existir; ausente = `n/d`, não bloqueia. | aviso |

Observações:

- O preflight usa `Execution`/`HttpClient`; não usa `curl`, `df`, `pgrep` ou
  `nvidia-smi` obrigatório.
- `nvidia-smi` ausente (Windows sem driver, container etc.) não derruba o run:
  o check é informativo.
- `sem_run_orfao` usa `Process.GetProcesses()` e `ProcessName`; se o runner for
  iniciado como `dotnet exec`, o nome será `dotnet` e a detecção é limitada.
  Isso é documentado como limitação portável, não como bloqueio.
- No L3, `main_harness=false` gera **apenas aviso**; não bloqueia.

## 10. Levels e steps da WAR

Cada level roda os steps em série (`MaxParallelSteps=1`), nos dois modos
(`bridge` e `opencode`), quando aplicável:

| Level | Steps |
|---|---|
| **L1** | `units-{bridge,opencode}` (`U-`), `devpipe-{bridge,opencode}` (`E-devpipe`), `tools-full-{bridge,opencode}` (`E-tools-full`, com o ciclo git/arquivo do antigo dual), `belts-build-{bridge,opencode}` (azul,verde), `belts-orch-{bridge,opencode}` (azul,verde). Web UI fica no L3. |
| **L2** | `mission-tools`, `responses-tools`, `science-gpu`, `science-cpu` (se CPU ligada), `belts-build-roxa` e `belts-orch-roxa` nos dois modos. Web UI fica no L3. |
| **L3** | `belts-build-final` (marrom,preta), `belts-orch-final` (marrom,preta) nos dois modos, `webui-integracao-topicos`. |

Modos:

- `bridge`: o teste chama a bridge pura (`/v1/responses` + tools); a bridge monta
  o turno.
- `opencode`: o teste chama o `opencode` real; o cliente traz o master.

O driver passa `--mode` para a suíte. O step `web` roda
`dotnet exec Rochas.OpenCodeBridge.Web.Test.dll` e não usa `--mode`.

Flags de ambiente que o driver repassa aos testes:

- `WAR_UPSTREAM` = `--war-upstream` (default `http://127.0.0.1:4100`);
- `WAR_NO_VISION` = `1` quando o upstream termina em `:4110` ou
  `--war-no-vision`;
- `WAR_CPU_BRIDGE` = `--war-bridge-q3`, quando informado.

## 11. Gate e critério de verde

`Gate()` lê `summary.tsv` e conta:

- PASS = linhas `PASS`;
- FAIL = linhas `FAIL`;
- SKIP = linhas `SKIP`;
- STALE = linhas `STALE`.

O driver monta `CircuitGateResult` com esses contadores e chama
`CircuitTemplate.GatePolicy(driver)`. A política default da WAR é
**`PASS > 0 && FAIL == 0 && STALE == 0`**. Um template pode exigir, por exemplo,
`PASS >= 10`, `SKIP == 0` ou cobertura mínima; o nome da política vai para o
evento `gate` (`policy`).

- SKIP não derruba o gate default, mas não substitui PASS (o `PASS > 0`
  garante que algo realmente rodou).
- `--war-require-green` (default) devolve exit 1 no vermelho.
- `--war-no-require-green` devolve exit 0 mesmo vermelho (só para inspeção).
- `--war-gate-only` recalcula o gate de um run existente sem executar steps.
  Para um level único, usa `--war-stamp`:
  `--war-level 2 --war-gate-only --war-stamp 20261025-130450-l2`.

## 12. Três princípios inegociáveis da WAR

1. **O teste se adapta à bridge; a bridge não muda para o teste.**
   Não altere `max_tokens`, `ctx`, `main_harness`, tails, coerção ou schema para
   obter PASS. O gate é a instância estável (Q4 `:4124`); Q3 `:4125` é
   diagnóstico. PASS obtido trocando a instância, ligando coerção ou afrouxando
   assertiva é PASS indevido.
2. **Orçamento estável.**
   Q4 `131072`/`16384`, Q3 `114688` (2×57344)/`16384`, Web `16384`. O driver não
   oscila esses valores.
3. **ACEITE inalterado.**
   Os belts continuam exigindo ≥ 3 arquivos, > 500 chars e arquivos reais no
   disco. O driver não “facilita” o aceite para ficar verde.

Comportamento repetido/instável em > 30% dos diálogos é elegível para correção
de enunciado/teste, não para afrouxar o gate.

## 13. CLI

```bash
# level único
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-level 3

# trilha inteira
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-all

# levels explícitos
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-levels 1,2

# listar steps sem executar
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-level 3 --war-list

# recalcular gate de um run
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- \
  --war-level 2 --war-gate-only --war-stamp 20261025-130450-l2

# pausa/retomada/status
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-pause
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-resume
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- --war-status

# retomar um level vermelho com o mesmo stamp
dotnet run --project Rochas.OpenCodeBridge.Test -c Release -- \
  --war-level 2 --war-stamp 20261025-130450-l2
```

Opções principais:

| Opção | Default | Função |
|---|---|---|
| `--war-bridge` | `http://127.0.0.1:4124` | bridge alvo (gate Q4). |
| `--war-upstream` | `http://127.0.0.1:4100` | llama upstream. |
| `--war-bridge-q3` | vazio | bridge da raia Q3 (`WAR_CPU_BRIDGE`). |
| `--war-blog` | `TMP/bridge-gpu.log` | log da bridge para a fatia por step. |
| `--war-lock` | `TMP/opencode/war.lock` | lock único. |
| `--war-out` | `logs/levels` | base dos artefatos. |
| `--war-step-timeout` | `7200` | timeout por step em segundos. |
| `--war-rebuild` | auto | força build da `.slnx`. |
| `--war-no-build` | — | não compila. |
| `--war-retries` | `1` | tentativas totais por step. |
| `--war-retry-all` | — | reinicia também FAIL de assertiva. |
| `--war-redeploy` | desligado | liga stop/start/health. |
| `--war-no-vision` | auto em `:4110` | pula testes de visão. |
| `--war-no-cpu` | CPU ligada | desliga `science-cpu`. |

## 14. Estrutura de artefatos

```
logs/levels/
  L2-20261025-130450-l2/
    summary.tsv
    summary.txt
    events.jsonl
    state/
      tools-full-bridge.done
      tools-full-bridge.attempt
    steps/
      tools-full-bridge.test.log
      tools-full-bridge.bridge.log
      build-slnx.build.log
      webui-integracao-topicos.test.log
  all-levels-20261025-130450.console.log
  driver-summary-20261025-130450.tsv
  pause
```

`summary.tsv`:

```
step	verdict	secs	rc	cause
units-bridge	PASS	42	0	
tools-dual-bridge	FAIL	310	1	undetermined
```

`events.jsonl` é a fonte de fatos; `summary.tsv` é derivado dos `step_end`.
Nunca edite `summary.tsv` à mão para “ficar verde”: o gate lê exatamente o que
está lá, e o `events.jsonl` preserva a auditoria.

## 15. Fluxo completo no caso WAR (passo a passo)

Exemplo: `--war-level 2` com Q4 em `:4124` e Q3 em `:4125`.

1. `Program.Main` vê `--war-level` e chama `WarTemplate.RunMain` antes de rodar
   a suíte normal.
2. `WarTemplate` resolve root, `.slnx`, DLLs, bridge, upstream, blog, lock,
   pause e flags.
3. `RunLevel` cria `WarTemplate(level=2)` e `TestCircuitDriver` com
   `AcquireLock=true`.
4. `TestCircuitDriver.RunAsync` adquire o lock (ou BLOCKED), verifica pausa e
   chama `OnRunStart`: a WAR grava `run_start` com `git` e hashes.
5. `BuildSpec` retorna o build da `.slnx`; `RecompileAsync` pula se está tudo
   atualizado ou roda `dotnet build`. Falha → BLOCKED/3.
6. `OnBuildCompleted` recalcula hashes; se mudaram, publica `dlls_ready`.
7. `PreflightAsync` valida llama, bridge, DLLs, opencode, órfãos, disco e GPU.
   Falha → `preflight_fail` + BLOCKED/3.
8. `Steps` gera, em ordem, os steps do L2 para `bridge` e `opencode`.
9. Cada step:
   - verifica `.done` (retoma);
   - verifica pausa;
   - executa `dotnet run --project ... -- --api-key ... --bridge ... --only ... --mode ...`;
   - grava `test.log`, fatia `bridge.log`;
   - classifica PASS/FAIL/SKIP, checa STALE;
   - grava `state/*.done`, `summary.tsv`, `step_end`.
10. Ao final, `Gate` conta os vereditos. Se `PASS>0 && FAIL==0 && STALE==0`,
    exit 0; senão exit 1.
11. `OnRunEnd` publica `run_end` e imprime os artefatos.

Se `--war-all`, o `WarTemplate` mantém **um** lock para L1→L3, cria um
`TestCircuitDriver` por level e continua após um level vermelho, registrando o
rc em `driver-summary-<stamp>.tsv`. No fim, exit 1 se qualquer level falhou.
`--war-resume-stamp`/`--war-resume-level` reutilizam o stamp de um level
específico.

## 16. Portabilidade (Windows/Linux)

O driver foi escrito para não depender de infra Linux:

- **Sem shell:** `Execution` usa `ProcessStartInfo` + `ArgumentList`, não
  `bash -c`. No Windows, `.cmd`/`.bat` são embrulhados em `cmd.exe /c`.
- **Sem `flock(2)`:** `CircuitLock` usa `FileShare.None`, que o .NET implementa
  no SO hospedeiro.
- **Sem `/proc`:** órfãos usam `Process.GetProcesses()`/`ProcessName`.
- **Sem paths fixos:** `Path.GetTempPath()`, `Path.Combine`,
  `Path.DirectorySeparatorChar`; o blog default é `TMP/bridge-gpu.log`.
- **Sem `nvidia-smi` obrigatório:** se não existir no `PATH`, o check é `n/d`.
- **Sem `curl`/`df`/`pgrep`:** HTTP é `HttpClient`; disco é `DriveInfo`;
  processos são `Process`.
- **Resolução de executável:** `Execution.Resolve` procura no `PATH` e, no
  Windows, respeita `PATHEXT` (`.EXE`, `.CMD`, `.BAT`).
- **`.slnx`:** build via `dotnet build Rochas.OpenCodeBridge.slnx`; se a solução
  não existir, cai para o `.csproj` do projeto de teste.

Limitações honestas:

- Detecção de órfão por nome de processo não vê `dotnet exec Test.dll` (nome
  `dotnet`). Em Windows, o apphost normalmente aparece como
  `Rochas.OpenCodeBridge.Test`.
- Pausa é por arquivo e em pontos seguros; não há suspensão de processo em
  execução (não é portável).
- Redeploy executa exatamente os comandos informados; o driver não descobre
  sozinho como reiniciar o llama/bridge.
- **Execução é local por contrato.** `Execution` cria processo na máquina do
  runner. Execução remota (SSH, container, agente em outro host) fica
  deliberadamente de fora desta versão; quando entrar, deve ser um adapter
  atrás da mesma abstração, sem mudar o contrato dos templates nem o gate.

## 17. Testes do próprio driver

`UnitCircuitTests` (rodam no L1 e com `--only U-circuit`) cobrem:

| Teste | Prova |
|---|---|
| `U-circuit-step-gate` | step PASS grava summary e gate verde. |
| `U-circuit-fail-gate` | step FAIL deixa gate vermelho. |
| `U-circuit-resume-done` | `.done` evita reexecução e não duplica summary. |
| `U-circuit-restart` | `CircuitRestartPolicy` reexecuta e mantém uma linha. |
| `U-circuit-pause` | marcador bloqueia o step até `Resume`. |
| `U-circuit-lock` | `CircuitLock` é exclusivo e reutilizável após dispose. |
| `U-circuit-build` | build incremental pula e `Force` executa. |
| `U-circuit-deploy` | spec vazio é `Skipped` e ok. |
| `U-circuit-deploy-health` | start + health poll até ficar saudável. |
| `U-circuit-template` | `TestCircuitDriver` executa um `CircuitTemplate` e chama os hooks. |
| `U-circuit-conditional` | `When=false` vira SKIP sem entrar no summary; `When=true` roda. |
| `U-circuit-dag` | `DependsOn` respeita a ordem (A antes de B). |
| `U-circuit-parallel` | `MaxParallelSteps=2` executa dois steps independentes. |
| `U-circuit-next` | `NextSteps` acrescenta um passo dinâmico ao plano. |
| `U-circuit-gate-policy` | `GatePolicy` customizado pode reprovar um run com PASS. |
| `U-circuit-multi` | `BuildSpecs`/`DeploymentSpecs` executam N builds e N deploys. |

Esses testes não precisam de modelo, bridge, GPU ou opencode; usam
`dotnet --version` como processo barato.

## 18. Migração dos scripts shell

`scripts/war/run-levels.sh` e `scripts/war/run-all-levels.sh` não são mais o
caminho de execução. Eles ficam como referência de semântica (lock, preflight,
steps, gate). O equivalente C# é:

| Shell antigo | C# atual |
|---|---|
| `run-levels.sh --level 2` | `--war-level 2` |
| `run-all-levels.sh` | `--war-all` |
| `--gate-only` | `--war-gate-only` |
| `LEVEL_STAMP` | `--war-stamp` |
| `LEVEL_BRIDGE` | `--war-bridge` |
| `LEVEL_UPSTREAM` | `--war-upstream` |
| `LEVEL_BRIDGE_Q3` | `--war-bridge-q3` |
| `LEVEL_BLOG` | `--war-blog` |
| `WAR_LOCK` | `--war-lock` |
| `LEVEL_OUT` | `--war-out` |
| `LEVEL_STEP_TIMEOUT` | `--war-step-timeout` |

## 19. Troubleshooting

| Sintoma | Causa provável | Ação |
|---|---|---|
| `BLOCKED: outro runner ativo` | Outro `TestCircuitDriver`/script segura o lock. | Aguarde ou finalize o runner; `--war-status` mostra o dono. |
| `PAUSA: aguardando remocao` | Marcador `pause` presente. | `--war-resume`. |
| `BUILD -> FAIL` | Erro de compilação na `.slnx`. | Leia `steps/build-slnx.build.log`; corrija; rode com `--war-rebuild`. |
| `preflight FALHOU: llama_...` | Upstream não responde. | Suba o llama na porta esperada; confira `/health`. |
| `preflight FALHOU: bridge_status` | Bridge não responde ou sem modelo. | Confira `/api/status`; não troque a instância do gate sem autorização. |
| `preflight FALHOU: bridge_context_tokens` | `ctx` da bridge ≠ slot do llama. | Alinhe a configuração estável; não “conserte” o teste. |
| `STALE` | Binário mudou durante o run. | Rode de novo com o build estável; veja `step_stale` no `events.jsonl`. |
| Gate vermelho com FAIL `undetermined` | Assertiva falhou sem marcador de infra/bridge. | Leia `steps/*.test.log`; corrija enunciado/teste, não o gate. |

## 21. Pipelines arbitrários (sem execução remota)

O núcleo é linear+DAG, não é específico da WAR. Um novo pipeline se encaixa
implementando `CircuitTemplate`:

| Necessidade | Como |
|---|---|
| Vários builds/serviços | `override BuildSpecs` → N `CircuitBuildSpec`; `OnBuildCompleted` recebe `result.Name`. |
| Vários deploys | `override DeploymentSpecs` → N `CircuitDeploymentSpec`; `OnDeployCompleted` recebe `result.Name`. |
| DAG / dependências | `CircuitStepSpec.DependsOn = new[] { "outro-step" }`. |
| Paralelismo | `CircuitOptions.MaxParallelSteps = N` (default 1; WAR fixa 1). |
| Branch depois do resultado | `override NextSteps(driver, result)` devolve passos novos. |
| Step condicional | `CircuitStepSpec.When = driver => ...` (falso = SKIP). |
| Gate próprio | `override GatePolicy` devolve `CircuitGatePolicy(nome, g => ...)`. |
| Pausa/lock/retry/artefatos | herdados do `TestCircuitDriver`. |

Regras do plano:

- nomes de step são únicos; duplicata falha antes de executar;
- dependência ausente ou cíclica falha explicitamente com a lista de espera;
- `DependsOn` libera quando o step tem **resultado** (inclusive FAIL/STALE);
  se o pipeline exige “só depois de PASS”, o template deve codificar isso em
  `When`/`NextSteps` ou no `GatePolicy`;
- `MaxParallelSteps>1` só é seguro quando os steps não compartilham recurso
  (GPU, bridge, arquivo de saída). A WAR mantém 1 porque os belts disputam o
  mesmo modelo/servidor;
- build/deploy são sequenciais e param no primeiro erro; paralelismo de build
  não é objetivo desta versão.

**Fora de escopo por decisão:** execução remota. Não há SSH, container ou
agente externo; `Execution` é local. O contrato já está isolado o suficiente
para um adapter remoto futuro, mas ele não existe e não deve ser simulado.

## 22. Não objetivos

- O driver **não** inicia llama/bridge por default; ele valida e bloqueia.
- O driver **não** altera `max_tokens`, `ctx`, `main_harness` ou tails.
- O driver **não** liga coerção para transformar prosa em tool call.
- O driver **não** troca Q4 por Q3 para obter PASS.
- O driver **não** muda o ACEITE dos belts.
- O driver **não** depende de `scripts/war/` para rodar.

Qualquer mudança nesses pontos é mudança de contrato da WAR, não correção de
driver.

## 14. Variáveis de ambiente da esteira

| variável | default | efeito |
|---|---|---|
| `LEVEL_MODES` | `bridge opencode` | modos rodados, em sequência |
| `LEVEL_STOP_ON_FAIL` | `1` | para no primeiro `FAIL`/`STALE` (evento `stop_on_fail`, exit 1) |
| `LEVEL_STAMP` | timestamp | congela/retoma o stamp do run |
| `LEVEL_BRIDGE` | `http://127.0.0.1:4124` | instância do gate (Q4) |
| `LEVEL_BRIDGE_Q3` | — | liga `science-cpu` na Q3 (diagnóstico) |
| `LEVEL_STEP_TIMEOUT` | `7200` | timeout por passo (s) |
| `LEVEL_REQUIRE_GREEN` | `1` | gate derruba o exit code em FAIL/BLOCKED/STALE |

Com `LEVEL_STOP_ON_FAIL=0` o nível continua e o placar completo aparece no
`summary.tsv`; o gate final continua valendo. Retomada: passo com `.done` é
pulado — um passo vermelho **não** é retomado, use stamp novo.
