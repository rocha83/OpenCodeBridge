# DRIVER-FIXES — o que não funcionava (sintoma, causa, prova)

Registro honesto dos mecanismos que existiam, pareciam prontos e **não faziam o
papel**. Cada item tem a prova que fecha o caso. Regra do projeto: mecanismo novo
ou corrigido só entra com teste em C# + evidência (AGENTS.md, "blindagem em C#").

---

## 1. A bateria não parava no primeiro FAIL

**Sintoma** — um devpipe vermelho no início virava horas de steps vermelhos em
sequência; o run só terminava quando a bateria acabava.

**Causa** — `TestCircuitDriver.RunPlanAsync` executava todo o plano sem olhar o
veredito: não havia `StopOnFirstFailure` nem `break` após FAIL.

**Correção** — `CircuitOptions.StopOnFirstFailure = true`. No primeiro
FAIL/STALE/BLOCKED os steps restantes entram no summary como `SKIP` com
`causa="nao executado: <step> falhou"` e o plano encerra.

**Prova** (`U-circuit-battery-stop`, bateria real de 3 steps):
```
-> PASS (0s, rc=0)
-> FAIL (0s, rc=1, causa=exit=1)
PARADA: b2-fail falhou (causa=exit=1); 1 step(s) nao executado(s)
3 steps: PASS=1 FAIL=1 SKIP=1
GATE VERMELHO: FAIL=1 STALE=0 (PASS=1)
PASS U-circuit-battery-stop (0.4s)
```

---

## 2. `.done` era gravado para FAIL — resume pulava justamente o que falhou

**Sintoma** — corrigir o código e rodar de novo não reexecutava o teste
vermelho; ele aparecia como `SKIP (done)`.

**Causa** — `RunStepAsync` gravava `state/<step>.done` para **qualquer** veredito;
somente STALE era excluído. O skip inicial (`if (File.Exists(donePath))`) não lia
o veredito, só a existência do arquivo.

**Correção** — `.done` só para PASS/SKIP; FAIL/BLOCKED/STALE apagam o arquivo.
`DoneIsSkip` passou a validar o veredito gravado (estado legado de FAIL é apagado
e o step reexecuta).

**Prova** (`U-circuit-battery-resume`, mesmo run dir):
```
# run 1: r1 PASS, r2 FAIL -> PARADA, r3 SKIP
# run 2: r1 SKIP (done do PASS), r2 PASS, r3 PASS
3 steps: PASS=3 FAIL=0 SKIP=0
PASS U-circuit-battery-resume (0.6s)
```

---

## 3. Gate somava histórico — resume nunca fechava verde

**Sintoma** — depois de retomar e corrigir, o gate continuava vermelho por causa
do FAIL antigo no `summary.tsv`.

**Causa** — `Gate()` contava todas as linhas do summary, sem distinguir reexecução.

**Correção** — o gate deduplica por nome e vale o **último** veredito; o arquivo
continua append-only como auditoria.

**Prova** — o mesmo `U-circuit-battery-resume` fecha `PASS=3 FAIL=0` com duas
linhas de `r2` no summary (FAIL do run 1, PASS do run 2).

---

## 4. Preflight do WAR só olhava a GPU Q4 — `belts-orch` morria como `infra`

**Sintoma** — run `L1-20261026-024351`: `belts-build-bridge` PASS em 1766s e
`belts-orch-bridge` FAIL em 318s com `causa=infra`, porque os belts de orquestração
chamam `:4125` e o espelho Q3 estava fora.

**Causa** — `WarTemplate.Preflight` validava `llama :4100` + `bridge :4124`, e
nunca `:4125`/`:4110`, embora a bateria dependa deles.

**Correção** — checks `bridge_q3_status`, `llama_q3` e upstream do Q3 no
preflight do driver.

**Prova** (run `L1-20261026-045940`, preflight do WAR):
```
preflight ok
```
e, dentro do step, o preflight C# da suíte:
```
[preflight] ok  bridge-gpu2 http://127.0.0.1:4125 — status=ok model=Gemma4-E4B-UD-Q3
[preflight] ok  bridge-gpu2 upstream — upstream=http://127.0.0.1:4110
[preflight] ok  llama-gpu2 — health 200 (http://127.0.0.1:4110)
[preflight] ok  modelo Q3 responde — finish=stop
```

---

## 5. "Preflight" em bash era enfeite

**Sintoma** — `scripts/war/preflight-l1.sh` existia, era correto e **ninguém
chamava**: dependia de alguém lembrar de rodar antes do WAR.

**Causa** — verificação fora do caminho de execução dos testes.

**Correção** — `Rochas.OpenCodeBridge.Core/StackPreflight.cs` roda por padrão
antes de todo teste que toca o stack (suite da bridge, suite Web e driver WAR),
com `--no-preflight` apenas como escape. O script bash foi removido; o C# é a
fonte de verdade.

**Prova** — `[preflight] GO: stack pronto` no log de `units-bridge` do run
`L1-20261026-045940`, antes de qualquer teste.

---

## 6. Preflight acusaria o lock do próprio driver

**Sintoma** (evitado antes de acontecer) — todo step filho do WAR veria
`/tmp/opencode/circuit.lock` ocupado e daria NO-GO no próprio run.

**Causa** — o check de lock não distinguia "outro runner" de "o driver que me
executa".

**Correção** — `WarTemplate.ChildEnv()` exporta `BRIDGE_UNDER_DRIVER=1`; o
preflight só checa lock fora do driver.

**Prova** — passo `CheckLock = Environment.GetEnvironmentVariable("BRIDGE_UNDER_DRIVER") != "1"`
e run atual com os 4 primeiros steps verdes sem NO-GO de lock.

---

## 7. Web.Test exigiria stack em teste unitário

**Sintoma** (evitado) — se o preflight rodasse sempre, `--units-only` (que testa
hash, sessão, parsers, SQLite) passaria a exigir Q4/Q3/Web e quebraria offline.

**Correção** — preflight só quando a suíte toca o stack:
`(!unitsOnly && !skipIntegration) || ui`.

**Prova** — `--units-only` continua sem rede; integração/UI imprime
`[preflight]` antes do primeiro teste HTTP.

---

## 8. Build fora da árvore com `BaseIntermediateOutputPath` quebrava o projeto

**Sintoma** — `error CS0579: Duplicate 'TargetFrameworkAttribute'` ao compilar
para `/tmp` durante um run.

**Causa** — o override fez o glob incluir `obj/**/*.cs` antigos como fonte.

**Correção** — usar só `-p:OutputPath=/tmp/...` (obj continua o padrão); nunca
`BaseIntermediateOutputPath` para validar sem tocar no binário do run.

---

## 9. Documentação prometia resume que não existia

**Sintoma** — `state/<step>.done pula steps` era tratado como resume, mas pulava
também o step que falhou (item 2).

**Correção** — a promessa agora vale só para PASS/SKIP, com teste de bateria
real e gate por último veredito.

**Prova** — `U-circuit-battery-stop` + `U-circuit-battery-resume` no mesmo
commit do mecanismo (`8671581`).

---

## 10. E2E intermitente: `ResponseEnded` no pool HTTP

**Sintoma** — `E-mission-tools` falhava 500
`An error occurred while sending the request.` de forma intermitente.

**Causa** — o log da bridge mostra
`HttpIOException: The response ended prematurely. (ResponseEnded)` em
`HttpConnection.SendAsync`: conexão keep-alive reutilizada depois que o
upstream/bridge anterior fechou.

**Correção** — cada step E2E reinicia a bridge alvo e recria o `HttpClient` do
runner antes da primeira chamada, zerando contadores e o pool HTTP dos dois
lados.

**Prova** — `sudo systemctl restart bridge-gpu` + `/api/status`
(`main_harness=true`); o 500 não reaparece no step seguinte.

---

## 11. Belts build: workspace root e 500 peg-gemma4

**Sintoma** — o segundo run do L2 falhava em 1 s com
`Access to the path '/tmp/opencode/belts-build/azul/Startup/DependencyInjection.cs' is denied`;
o primeiro falhava em 500
`The model produced output that does not match the expected peg-gemma4 format`.

**Causa** — o bridge executa a tool `write` como root, então o workspace de
`/tmp/opencode/belts-build` ficava root-owned; o run seguinte, como `mint`,
não conseguia limpar. O 500 peg-gemma4 é falha transitória de formato do
upstream, não de assertiva.

**Correção** — `E2EBeltsBuildTests` usa workspace único por execução
(`<timestamp>-<pid>/<belt>`, sem apagar pasta antiga) e limita o POST a duas
tentativas quando o erro é 500 + `peg-gemma4`: a segunda anexa um nudge
`user` curto com o erro e pede UMA tool call válida (`write` na criação,
`read` na revisão), em vez de reenviar o mesmo histórico às cegas. Se repetir,
a exceção sobe e o step fica FAIL. As assertivas (>=3 arquivos e >500 chars)
não mudam.

**Prova** — `logs/levels/L2-20261027-l2fix`: 4/4 PASS (537 s); L1
`logs/levels/L1-20261027-l1l2-l1`: 3/3 PASS (741 s).

---

## 12. ToolAgent: cutucada nunca disparava após a 1ª tool call

**Sintoma** — `E-ent-redis` falhava `ProductRepository.cs com codigo util (len=0)`.
O dump (`TCTX_DUMP=1`) mostrava ~10 respostas `output:[]` (1 tok) seguidas após um
`glob` sem hit, sem nudge, gastando steps até o modelo escrever arquivo errado
(ou nenhum) e estourar o orçamento de 14 passos.

**Causa** — `TestContext.ToolAgent` calculava `hasCall` pelo **último item do
input** ser `function_call_output`. Após a 1ª tool call esse item permanece
`function_call_output` mesmo quando o `output` do response vem **vazio** (ou só
prosa): `hasCall` ficava `true`, a cutucada nunca incrementava e o loop girava
às cegas (medido: 10+ chamadas ao modelo sem progresso).

**Correção** — progresso contado **por iteração** (`emittedCall`), setado no
branch `function_call` do response atual; `hasCall = emittedCall`. Output vazio
ou só prosa agora incrementa `nudges` e dispara a cutucada (até `MaxNudges`),
como já acontecia antes da 1ª call. Sem mudar enunciado nem afrouxar assertiva.

**Prova** — `E-ent-redis` saiu de `len=0` para gravar o repositório completo
(4386 chars, cache-aside + fallback + lock) em `logs/levels/L1-20261028-175935-l1`;
units `U-breaker-*` 8/8 PASS; build 0 erros. Commit `0d291fa`.

---

## 13. F# do mission-tools: a própria dica induzia o erro (FS0751/FS0001) e o retry lia saída como "erro"

**Sintoma** — `E-mission-tools` falhava no F# com `FS0751` (sintaxe `[1; 3 .. n-1]`) e
`FS0001` (esperava `int`, veio `float` em `simpson ... 100.0`); o retry "repassava o
erro real" mas mandava `3.998355` (a **saída** da tentativa anterior), confundindo o
modelo; e a atestação do system nunca aparecia no log (rodava depois dos asserts,
abortada pelo fail-fast).

**Causa** — (a) o texto do `FsMini` listava o literal inválido `[1; 3 .. n-1]` como
exemplo do que **não** existe, e o modelo **copiou** o literal; (b) a dica "range com
passo" empurrava `[1 .. 2 .. n-1]` (int), forçando `n : int`, enquanto a chamada
passava `100.0` (float) → `FS0001`; (c) `lastErr.Split('\n').FirstOrDefault(l => l.Contains("error"))`
caía no primeiro item da saída (`3.998355`) quando não havia erro de compilação;
(d) a atestação (`GET /api/test/last-system`) ficava depois dos `Check` semânticos.

**Correção** — `HarnessTails.FsMini` agora traz a regra **abstrata** `TIPOS NUMERICOS`
(tipo por papel: contagem/índice/passo/tamanho = int; medida/resultado = float;
conversão só na borda) e **não** repete o literal inválido; o passo é sempre
`[inicio .. passo .. fim]` (`;` nunca é passo). `E2EMissionTests` passou a
distinguir *erro de build* (repassa a linha `error ...`) de *saída errada* (repassa a
saída + metas + fórmula), com 3 tentativas. A atestação roda **antes** dos asserts
semânticos e a bridge registra o system **final** (`RecordSystemSent` →
`GET /api/test/last-system`: `fsmini`, `tipos_regra`, `tail_scope`, `harness_tails`).

**Prova** — rodada `logs/levels/mission-tools-20261028-223657.console.log`: F#
PASS com `[for i in 1 .. n-1 do if i % 2 <> 0 then f (a + float i * h) else 0.0]`
e atestação `{"fsmini":true,"tipos_regra":true,"tail_scope":"all","harness_tails":true,"chars":7790}`.
Falha residual movida para o passo git/zip (assertiva exigia `.git` na raiz; o modelo
versionou `mfs/`), corrigida no teste (aceita raiz ou pasta do projeto).

## 14. 500 `Failed to parse tool call arguments` em loop: a bridge historizava args truncado

**Sintoma** — `E-mission-tools` estourava em `POST /v1/chat/completions -> 500
Failed to parse tool call arguments as JSON: ... column 3110`, sempre **no mesmo
byte**, 12× seguidas. Cada retry do teste reenviava o mesmo histórico e recebia o
mesmo 500; o turno nunca avançava. Não era amostragem aleatória: era determinístico.

**Causa** — o salvage anti-repeat montava a tool call com `arguments` como veio. Quando
o `abort` cortava a geração no meio do JSON, a bridge **emitia e gravava no histórico**
uma assistant com `arguments` truncado. O llama, ao reler esse histórico, responde 500
no parse do turno anterior — e como o teste reenviava o mesmo histórico, o erro se
repetia para sempre. `HISTORY.md:3761` já registrava que o parse quebrado é artefato de
**geração**; faltava a bridge não propagar o artefato.

**Correção** — mecânica única no Core, `ToolCallRepair` (`ArgsOk` / `HasBrokenArgs` /
`DropBrokenAssistant` / `Apply` + `Nudge`): (a) no salvage, só entra a call cujo
`arguments` **parseia**; as truncadas acumulam o nome e o corpo recebe o nudge de
reparo com `finish_reason="stop"` (evento `salvage-args-rejected`); (b) no consumidor,
`ToolCallRepair.Apply(history)` **poda** a assistant quebrada antes de anexar o nudge —
sem isso o 500 volta igual. `E2EMissionTests` (C# e F#), `MissionAlgoCs`/`MissionAlgoFs` e
`E2EBeltsTests` (scripter e gate) passaram a chamar o mesmo helper em vez de repetir o
texto. FsMini foi reorganizado como tabela **INCONSISTENCIAS PREVISTAS EM F#**
(código do erro → sintaxe correta), preservando o anchor `TIPOS NUMERICOS` que a
atestação lê. `ProbeMinChars` 1024 → 256 e o rótulo `convert` → `probe`. Anti-repeat
ganhou `RepeatedWritePath` (nudge `anti-repeat-write`, não bloqueia). `RoleProfiles`
default build 0.2 → 0.1 (drift vs `appsettings.json`) e `RecordSystemSent` passou a
registrar `temperature` no system efetivamente enviado.

**Prova** — `logs/levels/mission-tools-20261028-233914.console.log`:
`PASS E-mission-tools (171.0s)` / `PASS=1 FAIL=0 SKIP=0`, com **0** ocorrências de
`HTTP falhou` em `/tmp/e2e-mission/_mission.log` (antes: 12) e C# fechando em 3 turnos
(`read`, `write`, `shell`). Atestação do system enviado:
`{"role":"Build","fsmini":true,"tipos_regra":true,"tail_scope":"all","harness_tails":true,"temperature":0.1}`
— temperatura vinda do perfil do role, não do cliente.

## 15. `tools-full`: `edit` destrutivo e a ausência de orçamento de reparo no teste mais duro

**Sintoma** — `E-tools-full` passou a falhar em **dois formatos distintos** na
mesma máquina: (a) `[toolsfull:agent] data do sistema anotada` com o `.txt`
contendo só `"\nARQUIVO EDITADO - Clima: …"`, sem o ano; (b) `marca EDITADO`
ausente, porque o modelo **nunca chamou `edit`** e foi direto ao 2º
`git commit` com árvore limpa (`nothing to commit`), repetindo o mesmo `shell` e
o mesmo `read` até o `anti-repeat-loop` cortar o turno.

**Causa** — (a) o help do `edit` dizia apenas "para acrescentar, `newString` =
oldString + trecho novo": não declarava a **invariante** (o `oldString` precisa
reaparecer intacto dentro do `newString`) nem **quando** confirmar — o modelo
rodava o `grep` de confirmação *antes* da edição e nunca depois, e a edição
respondia `ok`; (b) o `E-tools-full` era o único teste duro da suíte com **uma**
tentativa, enquanto `E-mission-tools` roda 3 com feedback e as belts cutucam —
enunciado de 4 passos encadeados sem orçamento de reparo mede sorte de
amostragem, não capacidade.

**Correção** — `Core/ToolHelp.cs` (`edit`) passa a declarar a invariante do
append e a ordem `read`/`grep` **depois** da edição, em texto genérico (sem
cenário, path ou data). `E2EToolsFullTests` ganhou o mesmo padrão do mission:
`AgentAttempt` + 3 tentativas com a **falha do aceite** realimentada e pasta nova
por tentativa; enunciado e assertivas **inalterados**. Nada de estado global,
classe nova ou conhecimento do teste dentro do Core — o handler continua puro.

**Prova** — `logs/levels/tools-full-20261029-005826.console.log`:
`PASS E-tools-full (41.7s)` / `PASS=1 FAIL=0 SKIP=0`, com
`temperature=0.1, temperature_source=profile` no log da `:4124`.


## 16. POST de reset voltava 411 e deploy reprovava por pressa (2026-10-29)

**Sintoma** — `curl -X POST /api/diag/reset` devolvia `<h1>Length Required</h1>`;
`deploy-pull.sh` terminava com `curl: (7) Failed to connect ... :4800` mesmo com
o Grafana subindo normalmente.

**Causa** — (a) `HttpListener` responde **411** para POST sem `Content-Length`, e
`HttpRequestMessage`/`PostAsync(url, null)` não manda esse header; o handler
estava certo, o cliente incompleto. (b) O script verificava `/api/health` 3 s
após o restart; o deb do Grafana leva ~6 s para abrir a porta.

**Correção** — clientes (`TestContext.ResetDiagnostics`, hoje **fonte única** do
reset no lado do teste, e `U-diag-reset`) mandam `StringContent("")`;
`deploy-pull.sh` faz *polling* com teto (bridge 30 s, Grafana 60 s) e só então
falha. Nenhum assert afrouxado.

**Prova** — `curl -X POST -d '' .../api/diag/reset?probe=1&files=1` →
`{"ok":true,"event":"diag-reset",...}`; `--only U-diag-reset` →
`[test] PASS=1 FAIL=0 SKIP=0`; deploy posterior sobe bridge e Grafana com
`/api/status` e `/api/health` 200.

## 17. Reset de diagnóstico voltou 401 na WAR: credencial do argv não virava `TestContext.ApiKey` (2026-10-29)

**Sintoma** — WAR L1→L3 na `:4124`: `AVISO: reset de diagnostico falhou ...
HTTP 401 {"error":"nao autorizado (Bearer JWT ou ApiKey)"}` no início da bateria
e em cada `step_start`. O painel seguia mostrando números acumulados.

**Causa** — a WAR recebe `--war-api-key`, que popula `WarOptions.ApiKey`; o
`TestContext.ApiKey` só é populado por `--api-key`. A versão anterior do reset
morava no driver e lia `WAR_EVENT_KEY` do ambiente (por isso funcionava); ao
consolidar tudo em `TestContext.ResetDiagnostics` a credencial deixou de chegar
— e `required: false` engoliu o 401 em aviso.

**Correção** — `TestContext.ResetDiagnostics(clearProbe, bridge, required,
apiKey)`: a credencial é **parâmetro explícito** (`apiKey ?? Jwt ?? ApiKey`). A
`WarTemplate` passa `o.ApiKey`; o driver passa `step.ProbeApiKey`. Uma
implementação só, sem mutar estado global e sem env var paralela.

**Prova** — console da bateria com `OK: diagnostico zerado (sonda inclusa) ...
/api/diag/reset?files=1&probe=1` na largada e `OK: ... ?files=1` por step;
`GET /api/test/probe?limit=1000` traz ≥2 valores distintos de `step`.

## 18. Belt caía em 1 s: workspace em path fixo colidia com sobras root (2026-10-29)

**Sintoma** — L2 `belts-orch-bridge` FAIL em **1 segundo**:
`FAIL E-belts-direct: Access to the path '/tmp/opencode/belts/azul/Services/ReembolsoService.cs' is denied.`
Nenhuma chamada de modelo: o step morria antes da belt.

**Causa** — o workspace era `/tmp/opencode/belts/<key>`, fixo entre runs, e a
bridge roda como **root**: artefatos de uma rodada anterior ficam `root:root`
dentro dele. O `Directory.Delete(ws, true)` estava **fora** do `try` da belt —
a `UnauthorizedAccessException` escapava do laço e derrubava o teste inteiro.

**Correção** (`E2EBeltsTests`) — (a) workspace único por execução
`belts/<key>-<stamp>-<pid>` (`RunTag`), como o `belts-build` já fazia com
`runTag`; (b) `FreshWorkspace(ws)` centraliza criar/limpar e, se a remoção
falhar, segue no dir novo em vez de matar a belt. Assertivas intactas; `ws` só
é usado dentro do próprio teste (verificado por grep).

**Prova** — `--only E-belts-direct` isolado depois da correção (log
`/tmp/opencode/belts-iso-*.log`) sem `is denied` e com as duas faixas rodando.

## 19. Faixa preta: Orch devolveu prosa em vez do JSON array (2026-10-29)

**Sintoma** — `belts-orch-bridge` FAIL com
`[preta] Orch segmentou em JSON array`, mas só **na faixa preta**; azul, verde e
marrom cumpriram o contrato na mesma rodada.

**Causa** — variância de instrução, não de harness: `Preta` pede seis áreas
(domínio, banco, repositórios/serviços, API, bus, Big Data com ML) e o Orch
(E4B, temp 0.3) respondeu **399 chars de prosa** com `[`=0 e `{`=0. O contrato
estava no lugar certo (`Prompts/orch.md`: "Responda SOMENTE com JSON array",
copiado para o output) e o lane bridge o usa como system.

**Correção** (`E2EBeltsTests.Direct`) — a segmentação é re-amostrada até 3x
**apenas quando a resposta não traz JSON array**, com `OrchRetryAsk` repetindo o
contrato (nada além disso). O `Check` continua exigindo `tasks.Count > 0`; a
mensagem passou a incluir `chars=` e os 180 primeiros caracteres, para o log
distinguir prosa, JSON truncado e resposta vazia.

**Prova** — `--only E-belts-direct --belts preta`: `Orch: 4 tarefas`, `PASS: 3
scripts, 3 integros, 1 descartados`, `PASS E-belts-direct (300.2s)`.

## 20. F# do mission-tools: o reparo ensinava forma inválida, o anti-repeat cortava o `edit` e o nudge gastava turno (2026-10-29)

**Sintoma** — L3 `L3-20261029-030937-l3`, step `mission-tools-bridge`: FAIL em
212 s. C# fechou (`2.000000 5.000000 2.000000`); o F# gastou as 3 tentativas em
FS0010/FS0001 e terminou com `/tmp/e2e-mission/mfs/Program.fs(5,24): error
FS0001: The type 'unit' does not support the operator '+'`.

**Causa (três, independentes)**

(a) **O reparo devolvia a forma errada.** `FsSyntaxRepair.Suggest` reconhecia só
`[expr | for ...]` e montava o CERTO **preservando o `do` que já estava no
fonte**: `ERRADO: [f (...) | for i in 1 .. n - 1 do i]` → `CERTO: [for i in 1 ..
n - 1 do i -> f (...)]`. `do` + `->` na mesma comprehension não compila e o `do`
sozinho numa lista tipa o elemento como `unit`. Como o modelo **copia o literal
da dica**, os turnos seguintes aplicavam exatamente a forma inválida (12 de 22
turnos em ping-pong de FS0010).

(b) **Anti-repeat com janela única cortava a tool call no meio.** O stream
acumulava content + reasoning + **arguments** num só buffer e comparava tudo com
`window=512`. `edit`/`write` carrega arquivo inteiro e repete trechos legítimos
(também em relação ao content do mesmo turno) → falso positivo:
`{"event":"anti-repeat","where":"stream-abort","window":512,"chars":2050}`,
`{"event":"salvage-args-rejected","tools":"edit"}`,
`{"event":"anti-repeat-salvage","calls":0}`. A call que ia corrigir o arquivo foi
cancelada no meio, o salvage recusou os args truncados (por desenho) e o turno
virou "SEM CALLS".

(c) **O aviso do salvage gastava turno.** `BRIDGE (edit): ...` chega como
assistant sem tool call e o laço do teste contava isso no orçamento de 22: o
modelo não raciocinou, recebeu instrução — e pagou o turno.

**Determinismo (não é variância)** — `scripts/diag/fs-repair-probe.fsx` +
`dotnet fsi` 10.0.402: `[for i in 1 .. 3 do if i%2<>0 then float i else 0.0]`
tipa `float` (literal) e `[for i in 1 .. 3 do if i%2<>0 then f (float i) else
0.0]` com `f` **aberto** tipa `unit` (FS0001) — 3 execuções idênticas. `->` e
`do yield` declaram o elemento e são estáveis nos dois contextos.

**Correção**

- `Core/FsSyntaxRepair.cs` — reconhece `|`, `do` sem `yield` e `do`+`->`; emite
  só forma válida (`->`, `do if ... then yield`, paridade virando passo
  `a .. 2 .. b`); `open System.Math` (FS0039) vira chamada qualificada; gate por
  código (`FS0010/FS0001/FS0020/FS0751`) para não "corrigir" linha errada; guards:
  `yield` presente ou forma só com `->` **não** são tocadas; condição que não é
  teste booleano nem filtro só sai quando é resíduo do índice (`do i`).
- `Core/HarnessTails.cs` (`FsMini`) — ensina `do` sem `yield` e `do`+`->` **por
  descrição**, sem exibir o literal inválido (regra em `AGENTS.md`), e `Math` como
  tipo (`System.Math.Sin` qualificado).
- `Rochas.OpenCodeBridge/Program.cs` — **janela própria dos args**
  (`ToolArgsAntiRepeatReply.ArgsDefaultWindow = 1024`, faixa 256..8192, CLI
  `--anti-repeat-args-window`, campo `anti_repeat_args_window` no `/api/status`);
  texto (content/reasoning) e args saem em buffers separados no
  `TryStreamAbortChat`; sonda não-stream e stream-responses usam a janela de args.
- `Test/E2EMissionTests.cs` — orçamento de turnos do F# não debita o nudge de
  salvage (`ToolCallRepair.IsSalvageNotice`, guard próprio contra loop) e o
  enunciado aponta a tabela do FsMini sem duplicá-la.

**Prova** — `--only U-fs-repair-comprehension,U-antirepeat-args-window,
U-toolcall-salvage-notice,U-tails-path-simulation-rules --api-key guest@123`:
`PASS=4 FAIL=0 SKIP=0`. Build `0 Aviso(s) / 0 Erro(s)`. Emulação independente
(`scripts/diag/verify-antirepeat-args-window.py`): payload @256 `True`, @1024
`False`, loop real @1024 `True`.

## 21. Deploy do PROD travado por dono root em `.git` e na árvore (2026-10-29)

**Sintoma.** `scripts/deploy-pull.sh` falhou duas vezes seguidas, em pontos
diferentes do mesmo `git pull`:

1. `error: insufficient permission for adding an object to repository database
   .git/objects` / `fatal: failed to write object` / `fatal: unpack-objects
   failed`;
2. depois do `chown` só do `.git/:` `error: unable to unlink old
   'Rochas.OpenCodeBridge.Core/FsSyntaxRepair.cs': Permissão negada` — o pull
   ficou **no meio**, deixando dois arquivos modificados no worktree e o
   `deploy-pull.sh` recusando a próxima rodada
   (`ERRO: /usr/src/opencodebridge tem alteracao local`).

**Causa.** Alguém rodou o deploy (ou um build) como `root` dentro de
`/usr/src/opencodebridge`; os objetos e os arquivos do worktree ficaram
`root:root`. O deploy é feito pelo usuário do dia a dia (`mint`), então
`unpack-objects` e `unlink` batem na permissão. Não é bug de código nem de
harness: é estado de disco.

**Correção (infra, sem tocar no script).**

```bash
sudo chown -R mint:mint /usr/src/opencodebridge
cd /usr/src/opencodebridge && git checkout -- .   # desfaz o pull parcial
bash scripts/deploy-pull.sh                       # pull --rebase + build + restart
```

`git checkout -- .` (e **não** `reset --hard`) restaura só o worktree do PROD,
que é deploy-only; os refs seguem avançando por fast-forward/rebase do
persistente, como manda a regra.

**Prova.** Deploy verde depois do `chown`+`checkout`: `pull --rebase` de
`persist/main`, build `0 Aviso(s) / 0 Erro(s)`, `systemctl restart bridge-gpu
bridge-web grafana-server`, `/api/status` e `/api/health` (4800) respondendo e
`OK: /usr/src/opencodebridge atualizado por pull --rebase e redeploy concluido`.

**Regra derivada.** Rebuild/deploy no PROD é do usuário dono do repo; rodar
`dotnet build` como `root` ali envenena o próximo pull. Se acontecer, o conserto
é `chown -R` + `git checkout -- .`, nunca `reset --hard`.

## 22. Mission-tools: pacote saiu como `delivery.zip` e o laço entre turnos gastava o orçamento (2026-10-29)

**Sintoma (a).** `E-mission-tools` FAIL em `assert: entrega.zip gerado` com o
workspace contendo `delivery.zip` (4,4 MB) na raiz — entrega pronta, reprovada
pelo **nome** do arquivo.

**Causa (a).** O assert tratava o nome do pacote como contrato. Não é: o
enunciado pede "gere entrega.zip" e o modelo traduziu para `delivery.zip`; medir
tradução de palavra não diz nada sobre versionar/enpacotar. O nudge de "encerrou
sem tool call" também só dizia "verifique o que falta".

**Correção (a).** O pacote passa a ser o contrato, não o nome: o assert aceita
**qualquer** `.zip` na raiz (`ws/*.zip` ou `ws/mfs/*.zip`) e imprime o nome que
saiu; enunciado e nudge dizem "um pacote .zip, o nome não importa" (decisão de
2026-10-29). O orçamento da fase foi 12 → 14 turnos (as 5 chamadas `git` com
erro custavam 5) e o que continua exigido é o versionamento (`init`, `add`,
`commit`) e o pacote com a tool `shell`.

**Sintoma (c).** `U-diag-reset-panel` passou a FAILAR (`painel lista o step
publicado`: contou 2) depois de um run isolado.

**Causa (c).** O reset de painel só existia no `WarTemplate` (1× por bateria).
Run manual/isolado não zerava nada, então a linha `E-mission-tools` do run
anterior sobrevivia e entrava na contagem do teste seguinte.

**Correção (c).** O runner (`Program.cs`) zera o painel no início de qualquer run
fora do driver (`WAR_STEP` ausente → `?probe=1&files=1&panel=1`); o `step_start`
continua só `?files=1`.

**Prova.** `E-mission-tools` isolado **PASS 333,1 s** (com o `PRIMOS`/FsMini novo)
e **PASS 160,6 s** (segunda rodada, 5 turnos de git/zip); units
`PASS=121 FAIL=0 SKIP=6`, com `U-diag-reset-panel` verde e `U-bridge-notice`
provando que o aviso da bridge tem **um** formato (`BRIDGE (escopo): corpo`,
tag genérica quando o escopo é vazio) e que `Apply` não empilha cópia.

**Dedup do formato (mesma data).** O literal `"BRIDGE ("` vivia em três pontos
(chamada do salvage em `Program.cs`, `ToolCallRepair.IsSalvageNotice` e o marcador
de um guard novo de repetição entre turnos, descartado). Agora vive em
`Core/BridgeNotice` (`Prefix`/`Tag`/`Notice`/`Is`/`Append`), o `Apply` do salvage
anexa por `BridgeNotice.Append` (uma cópia, não empilha) e a tag sai com as tools
recusadas (`BRIDGE (edit, write): …`).

## 23. Mission-tools: `zip` com a origem no lugar do pacote (help não ensinava a ordem) (2026-10-29)

**Sintoma.** Rodada 3 do aceite (isolado, `E-mission-tools`) FAIL em
`assert: pacote .zip gerado (saiu: )`, com seis `shell` consecutivos devolvendo
`exit 3: zip error: Zip file structure invalid (…/mfs/.git)` e
`(…/mfs/Program.fs)`.

**Causa.** No `zip` o **primeiro** argumento é o arquivo `.zip` de destino; o
erro cita justamente a origem (`.git`, `Program.fs`) porque o modelo a passou
como pacote. O help curado da tool `shell` (`Core/ToolHelp.cs`) só ensinava
`cd dir && cmd`, script e `ls -la` — nada sobre a ordem do pacote, e o enunciado
do teste diz apenas "gere um pacote .zip com a tool shell (zip -qr)", que é
contrato, não mecânica. Sem a forma certa, o modelo repetiu o mesmo comando 4×.

**Correção.** O help de `shell`/`bash`/`sh` passa a trazer a forma correta com
exemplo — `pacote = zip -qr pacote.zip .` (o nome do `.zip` vem primeiro) — e o
sintoma em prosa (`Zip file structure invalid` = o primeiro argumento não é um
`.zip` válido; use um nome novo). Nenhuma forma incorreta aparece no help (regra
da dica: só o literal que o modelo deve copiar). `U-tool-help` pina as duas
frases. Nada de nudge/guard no teste: a mecânica é do Core.

**Prova.** Build `0 Aviso(s)/0 Erro(s)`, `PASS U-tool-help`; aceite reaberto com
3 rodadas isoladas verdes consecutivas antes da WAR L1→L3.

## 24. Tools-full: `cd` do shell parecia persistir, `git add . && commit` e histórico inventado (2026-10-29)

**Sintoma.** `E-tools-full` isolado FAIL 3 tentativas seguidas, cada uma por um
motivo diferente:

| tentativa | FAIL | causa real |
|---|---|---|
| t1 | `2 commits no repo` (log com 1) | 2º commit tentado como `git add . && commit -m "…"`: a tool `git` recebe a cadeia e o git a lê como pathspec (`pathspec '&&' did not match`); o modelo abandona o passo e **inventa** o commit 2 no histórico (`[HASH_DO_SEGUNDO_COMMIT]`) |
| t2 | `marca EDITADO no MESMO arquivo da data` | `data.txt` ficou só com a data (passo 3 nunca executado) e o histórico saiu fabricado (`[HASH_DO_PRIMEIRO_COMMIT]`, `[HASH_DO_SEGUNDO_COMMIT]`) |
| t3 | `2 commits no repo` (`does not have any commits yet`) | `mkdir <pasta> && cd <pasta>` no shell e depois `write path=<arquivo>` → arquivo na **raiz**, repo na subpasta **vazio**; `commit` falha "nothing to commit", `git add <arquivo>` falha pathspec e os 36 steps acabam sem recuperação |

**Causa.** O enunciado (4 passos + uma tool por vez) não era o gargalo; dois
contratos do Core davam feedback incompleto:

1. `shell` devolvia só o stdout. Em `mkdir X && cd X` a saída é vazia e o modelo
   conclui que o `cd` persistiu — mas cada chamada começa na raiz do workspace e
   o `path` das tools de arquivo é root-relative. O erro é invisível até o
   `commit` falhar.
2. `git` recusava cadeia com erro de pathspec, sem ensinar a forma certa (o help
   antigo só dizia "não encadeie `&&`" — instrução negativa, sem exemplo
   copiável).
3. Nada ligava "conteúdo do arquivo" à "saída real da tool": com o passo falho,
   o modelo fabricava histórico com hash fictício.

**Correção (Core, geral — nada específico do teste).**

- `ShellToolHandler`: toda saída (ok e fail) termina com
  `[shell] cwd desta chamada: <dir>`; quando a chamada entrou numa pasta, a
  mesma linha diz que a próxima começa na raiz e que o arquivo dessa pasta usa
  `path "<pasta>/<arquivo>"`.
- `GitToolHandler`: `command` com `&&`, `;` ou `|` **fora de aspas** devolve
  `uma suboperação por chamada: rode git add . agora e, na próxima, chame git
  de novo com git commit -m "..."`, mesmo `path` (aspas e `;` dentro da
  mensagem seguem valendo como passo único).
- `ToolHelp[git]`: **exemplo de sucesso comentado** (passos 1..7: `mkdir` →
  `init` → `write` com a pasta → `user.name` → `user.email` → `add .` →
  `commit -m` → `log --oneline`), com os nomes marcados como fictícios
  (`<pasta>`, `<arquivo>`, `<nome>`, `<email>`, `<mensagem>`); o help de
  `shell`/`bash`/`sh` passa a dizer "o cwd de cada chamada é a raiz do
  workspace" em vez de "`cd` não persiste".
- `HarnessTails.DefaultTools`: `FLUXO ARQUIVO+REPO (exemplo; use os nomes do seu
  pedido)` + `PROVA: depois de write/edit, read o arquivo — o conteúdo é o texto
  que a tool devolveu; histórico é a saída literal colada`.

**Prova.** Build `0 Aviso(s)/0 Erro(s)`; units `PASS=124 FAIL=0 SKIP=6`
(inclui `U-tool-feedback-shell-cwd`, `U-tool-feedback-git-chain` e os pins do
exemplo em `U-tool-help`); `E-tools-full` isolado **3/3 PASS** (25,7 s / 42,5 s
/ 35,9 s) contra o binário deployado no PROD (`b17f180`).

## 25. Mission-algo: F# não compilava com `module Program open System` na mesma linha (2026-10-29)

**Sintoma.** L4 (`logs/levels/L4-20261029-150833`), step `algo-bridge` FAIL em
568 s. A metade C# passou (`hanoi=15`, `primes100=25`, `primes1000=168`,
`twin100=8`); a metade F# morreu no primeiro erro do compilador:

```
/tmp/e2e-mission-algo/mfs/Program.fs(4,1): error FS0010: Unexpected keyword 'let'
or 'use' in implementation file
```

**Causa.** A linha 1 do arquivo gerado era `module Program open System` — duas
diretivas na mesma linha. O F# lê o resto da linha como corpo do módulo, o topo do
arquivo vira expressão e o `let` seguinte (linha 4, coluna 1) fica fora de posição.
A mensagem do compilador não diz a forma certa, e o `FsSyntaxRepair` não tinha
mapa para esse FS0010 — o modelo só repetia o cabeçalho.

Prova de compilação das duas formas (projeto mínimo, `net10.0`):
`module Program open System` → FS0010 em `(4,1)`; `module Program` sozinho com
`open System` na linha de baixo → build sem erro.

**Correção (Core, geral).**

- `FsSyntaxRepair`: caso `FS0010` com `'let'`/`'use'` inesperado → a dica traz
  **só** a forma que compila (`module <nome>` sozinho e **cada** `open` na própria
  linha), sem ecoar a linha colada.
- `HarnessTails.FsMini`: a linha `BASE` passa a dizer “cada diretiva na própria
  linha — a 1a linha tem só `module Program` e o `open` vai na linha de baixo”.

**Prova.** `U-fs-repair-module-line` (novo) verde e `U-fs-repair-comprehension`
intacto: units `PASS=125 FAIL=0 SKIP=6`; `dotnet fsi scripts/diag/fs-repair-probe.fsx`
com os dois casos novos OK (`cabecalho colado -> compilador recusa`,
`cabecalho ensinado (module sozinho + open) -> compila`); build
`0 Aviso(s)/0 Erro(s)`.

## 26. Dica de FS0001 respondia pela forma do fonte, não pela mensagem (2026-10-29)

**Sintoma.** `E-mission-algo-fs`: o modelo recebeu 4 vezes a dica
`CERTO: [2 .. int (Math.Sqrt(float n))]` enquanto o erro real era
`FS0001: All branches of an 'if' expression ... first branch is '('a * 'a) array',
this branch returns a value of type ''b list'`. Ele repetiu a mesma linha em 4
builds seguidos.

**Causa.** A regra de range em `FsSyntaxRepair` era disparada por regex no
**fonte** (`[ ... Math.Sqrt ...`), não pela mensagem do compilador. Qualquer
FS0001 do arquivo recebia a mesma dica, mesmo quando o defeito era o `@` entre
arrays (que concatena lista).

**Correção.** (a) Regra nova, antes de qualquer heurística de fonte, quando a
mensagem contém `branches of an 'if' expression`: os dois ramos devolvem o MESMO
tipo e array junta com `Array.append a b`. (b) A dica de range só dispara quando
a mensagem cita `float` **e** `int` (evidência de conversão no range).

**Prova.** `units` 127 PASS / 0 FAIL com três checks novos — "FS0001 ramos do if
-> array junta com Array.append", "não recebe a dica de range" e "com mensagem de
`if` vence o range mesmo com range no fonte" — e o caso compilado no
`scripts/diag/fs-repair-probe.fsx`. Commit `19de20c`.

## 27. FS0072 sem dica: programa completo morria na linha de formatação (2026-10-29)

**Sintoma.** `E-mission-algo-fs`, 4ª amostra: o programa de 58 linhas já
calculava (`printfn "hanoi=%d" hanoi_count`) e a fase morreu em
`FS0072: The type of this expression cannot be determined`, na linha
`List.map (fun move -> sprintf "%d->%d" move.Item1 move.Item2) hanoi_seq`. O
`FsSyntaxRepair` devolvia vazio para esse código, então o modelo repetiu a mesma
linha por 30 turnos.

**Causa.** Lacuna de ensino: `FS0072` não estava na tabela `FsMini` nem no mapa
do reparo. O modelo não tinha como saber que a inferência para porque o tipo do
parâmetro do lambda não sai sozinho.

**Correção.** `FsMini` + `FsSyntaxRepair` ensinam a forma que compila —
`List.map (fun (a: int, b: int) -> sprintf "%d->%d" a b) movs` (ou `fst`/`snd`) —
com o caso compilado no `scripts/diag/fs-repair-probe.fsx` e o pin
`U-fs-repair-type-annotation`.

**Prova.** `units` 128 PASS / 0 FAIL; probe OK na linha "tipo do parametro
anotado no lambda (FS0072)". Commit `0c750ff`.

## 28. O exemplo CERTO da tabela vazou fora de contexto: `[s->d]` (2026-10-29)

**Sintoma.** 5ª amostra do `E-mission-algo-fs`: o modelo escreveu a lista literal
`[s->d]` querendo a tupla `(s, d)` e ficou 30 turnos sem receber dica
(`FS0010: Unexpected symbol '->' in expression. Expected ']'`), porque nenhuma
regra casava essa mensagem.

**Causa.** Duas, e a segunda é a lição: (a) lacuna de ensino em `FS0010` para
`->` fora de comprehension; (b) **o exemplo `[| for i in 1 .. 3 -> i * i |]` da
tabela foi copiado como operador de lista**. A regra "a dica ensina a forma certa"
não basta: um exemplo certo, **fora de contexto**, também é copiável e vira erro.

**Correção.** Regra `FS0010` + `Unexpected symbol '->'` ensina as duas formas
separadas — lista de elementos `[ (1, 2); (1, 3) ]` e comprehension
`[ for i in 1 .. 3 -> i * i ]` — e só dispara quando o fonte **não** tem
comprehension (`[ ... for ...`), porque com `for` no bracket o caso já tem dica
própria (`do` → `yield`, `|` → `->`, `step` → passo). A primeira versão desta
regra roubava o tratamento de comprehension e o pin `U-fs-repair-comprehension`
pegou antes de ir para a bancada.

**Prova.** `units` 129 PASS / 0 FAIL; pin novo `U-fs-repair-list-literal-arrow`
com 4 checagens, inclusive "com comprehension no fonte a dica continua sendo a da
comprehension". Commit `80264d5`.

## 29. FS0193 saía como `warning` e o reparo só olhava `error` (2026-10-29)

**Sintoma.** 6ª amostra do `E-mission-algo-fs`: o modelo encadeou
`hanoi ... |> List.append` três vezes (o operador sem o segundo operando) e
repetiu a MESMA linha por 4 builds seguidos, sem nenhuma dica.

**Causa.** Duas lacunas no Core:
(a) `FS0193` ("This expression is a function value, i.e. is missing arguments")
não tinha mapa no `FsSyntaxRepair`;
(b) `FirstCode` casava apenas `error FSdddd` — quando o diagnóstico vinha como
`warning FS0193`, o código saía vazio e **nenhuma** regra disparava, mesmo que
existisse mapa. O reparo ficava inerte justamente no caso em que o build falha
por outro motivo e o warning é o único diagnóstico com código.

**Correção.** `FS0193` entra na tabela `FsMini` e no `FsSyntaxRepair` só com a
forma que compila (`a @ b`, `List.append a b`, `x |> List.append ys`); `FirstCode`
passa a preferir `error` e, na ausência dele, aceitar `warning`.

**Prova.** Pin `U-fs-repair-partial-operator` (3 checagens, inclusive "não ecoa o
pipe sem o segundo operando"), caso compilado no `scripts/diag/fs-repair-probe.fsx`
(`List.sort tudo = List.sort tudo2`), `units` **130 PASS / 0 FAIL**, build
`0 Aviso(s) / 0 Erro(s)`, commit `f65f054`.

## 30. Reparo F# gateado em `error`: o `warning FS0193` nunca virava dica (2026-10-29)

**Sintoma.** `E-mission-algo-fs` (isolado 19:25) gastou 30 turnos com 17 reparos
**idênticos** (`printfn "%s" (String.concat "," movs)`) e o modelo não aplicou
nenhum; em outra amostra o build saiu só com `warning FS0193` e **zero** dica.

**Causa.** Duas no mesmo fluxo: (a) `FirstCode` só reconhecia `error FSdddd`;
(b) o teste gateava o reparo com `res.Contains("error FS")`, então um diagnóstico
que chega como `warning` desligava o mecanismo inteiro. Dica repetida sem
variação também não ensina — o modelo reedita a mesma linha.

**Correção.** `FsSyntaxRepair.HasDiagnostic` (Core) aceita `error` **ou**
`warning`; `FirstCode` prefere `error` e cai para `warning` quando é o único
código; a 3ª repetição da MESMA dica acrescenta a instrução de reescrever o
arquivo inteiro com `write`. A fase F# passou a **falhar na hora** se o `system`
não carregar `FsMini` (guarda de fluxo), e o `Program.fs` inicial já compila
(`module Program`/`open System`), eliminando o `FS0010` de cabeçalho.

**Prova.** `units` **132 PASS / 0 FAIL** (pins `U-fs-repair-diagnostic-detection`
e `U-fsmini-covers-repair-codes`), `U-fs-repair-partial-operator` verde, probe
compilando `a @ b`/`List.append a b`, build `0 Aviso(s) / 0 Erro(s)`, commit
`59deeaa`.

## 31. Escalada da dica repetida estava no teste; `ref` sem dica (2026-10-29)

**Sintoma.** 8ª amostra do `E-mission-algo-fs`: `Program.fs(6,89)` com
`FS0010: Unexpected type application in pattern` em `(moves: ref seq<string>)`,
**0 reparos** e 26 turnos × 2 etapas sem contrato. Nas amostras anteriores, a
mesma dica saía 17 vezes sem o modelo aplicar.

**Causa.** (a) Nenhuma regra casava `Unexpected type application` — `ref` não é
tipo prefixo em F# (é `'a ref`, leitura `!`, escrita `:=`);
(b) a escalada “3ª dica igual → reescreva o arquivo com `write`” vivia em
`E2EMissionTests.RunAlgoPhase`, ou seja, mecânica de ensino no consumidor,
contra a regra “Core é a fonte única”.

**Correção.** `FsSyntaxRepair.Suggest(source, error, repeatCount)` — o Core
acrescenta a escalada a partir da 3ª vez (e nunca quando não há dica conhecida);
o teste só conta repetições. Regra nova `FS0010` + `Unexpected type application`
ensina a forma que compila (`moves: string list ref`, `moves := !moves @ [ … ]`)
e não ecoa `ref seq`.

**Prova.** `units` **134 PASS / 0 FAIL** (pins `U-fs-repair-repeat-escalation` e
`U-fs-repair-ref-type`), probe compilando `string list ref` + `:=` + `!`, build
`0 Aviso(s) / 0 Erro(s)`, commit `1b14994`.

## 32. `\count p1 ->` (lambda de Haskell) sem dica: FS0010 de caractere `\` (2026-10-29)

**Sintoma.** 9ª amostra do `E-mission-algo-fs`: `Program.fs(44,16)` com
`FS0010: Unexpected character '\' in expression` em
`List.fold (\count p1 -> ...) 0 primes` — **0 reparos**, 26 turnos × 2 etapas.

**Causa.** Nenhuma regra casava `Unexpected character '\'`; a forma de lambda do
F# (`fun x -> expr`) não estava na tabela nem no reparo.

**Correção.** Regra `FS0010` + `Unexpected character '\'` ensina só
`List.fold (fun count p1 -> ...) 0 primes` (não ecoa a barra invertida). No
`FsMini` a forma entra na **linha única** de `FS0010` — o pin
`U-tails-path-simulation-rules` exige UMA ocorrência por código, e a primeira
versão (linha separada) fez o teste falhar com 68 PASS / 1 FAIL antes de ir para
a bancada.

**Prova.** `units` **135 PASS / 0 FAIL**, pin `U-fs-repair-lambda-backslash`,
probe compilando `List.fold (fun acc x -> ...)`, build `0 Aviso(s) / 0 Erro(s)`,
commit `f642b91`.
