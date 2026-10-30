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

## 33. 10ª amostra do `E-mission-algo-fs`: anotação de retorno com nome de campo, `else let` e 15 `edit` com `oldString` obsoleto (2026-10-29)

**Sintoma.** Isolado `logs/isolated/E-mission-algo-fs_-20261029-195002.log`
(750,6 s, `PASS=0 FAIL=1`), duas tentativas de 26 turnos e **1 reparo em 52
turnos** — e o reparo caiu no `T25`, o **último** turno:

1. tentativa 1 — `Program.fs(28,91)` `FS0010: Unexpected symbol ':' in binding.
   Expected ')'`: `let rec hanoi_calc (…) : (count: int * string list) =`. Nome de
   campo dentro da anotação de retorno não existe em F#; **0 reparo** em 5 builds
   seguidos (T1, T10, T15, T20, T25).
2. tentativa 2 — `Program.fs(3,10)` `FS0588: The block following this 'let' is
   unfinished`: `else let limit = int (Math.Floor(float n / 2.0))` na mesma linha
   do `else`. A regra genérica de corpo existia e só saiu no último turno.

Além dos dois erros, o turno foi consumido por retry: **15** `oldString não
encontrado` (9 `edit` + 4 `write` idênticos de 2437 chars com o mesmo defeito),
`anti_repeat_content=4`, **0** `BRIDGE-BREAKER` no log.

**Causa.** (a) nenhuma regra casava `Unexpected symbol ':' in binding`;
(b) o `FS0588` só tinha a dica de corpo (let interno engolindo o resultado), não
o ramo `else` + `let`; (c) a escalada de retry existia para *dica de compilador*
(item 31), não para *args de tool* — e a fase E2E chama `POST /api/tools/execute`
direto, então o breaker do stream (que mora no caminho de chat) nunca vê esse
histórico.

**Correção (mecanismo no Core; aceite intacto).**
1. `FsSyntaxRepair` — regra `FS0010` + `Unexpected symbol ':' in binding` ensina
   só a forma que compila (`: int * string list`, sem `nome:` dentro).
2. `FsSyntaxRepair` — ramo `FS3118/FS0588/FS0528` **com `else let` na fonte**
   ensina o `else` sozinho e o bloco indentado; o genérico continua para o resto.
3. **Retry reusa o controle que já existe** — `ToolsBreaker`/`ToolsAntiRepeat`
   (soft 3 / hard 6, os mesmos números da bridge): `RunAlgoPhase` passou a chamá-lo
   depois de cada resultado de tool. O `POST /api/tools/execute` não passa pelo
   caminho de chat, então o breaker nunca via esses 15 retries. Nada novo no Core:
   o caso já era coberto por `U-breaker-compiler-loop` (agora com a asserção do
   `oldString` obsoleto). A 3ª falha no mesmo alvo injeta o aviso no último
   resultado e a 6ª encerra a tentativa — nudge/trip, nunca gate.
4. `FsMini` — as duas formas entram nas linhas **únicas** de `FS0010` e de
   `FS3118/FS0588/FS0528` (o pin `U-tails-path-simulation-rules` exige uma
   ocorrência por código).

**Prova.** `units` **137 PASS / 0 FAIL SKIP=6** (pins novos
`U-fs-repair-return-tuple` e `U-fs-repair-else-let`; `U-breaker-compiler-loop`
ganhou o caso do `oldString` obsoleto; `U-fs-repair-body-shape` e
`U-tails-path-simulation-rules` seguem verdes), `scripts/diag/fs-repair-probe.fsx`
compilando as duas formas novas (`anotacao de retorno so' o tipo (FS0010 ':')` e
`else + let em linha propria (FS0588)` = OK), build `0 Aviso(s) / 0 Erro(s)`.

## 34. `E-mission-algo-fs` 11ª amostra: FS0003 de aridade e rastro do isolado (2026-10-29)

**Sintoma.** Isolado `logs/isolated/E-mission-algo-fs_-20261029-202053.log`
(704 s, `PASS=0 FAIL=1`): `Program.fs(10,24) FS0003: This value is not a function
and cannot be applied. It has type '(int * string list)'`. O programa estava
completo (crivo, `countPrimes`, `countTwins`, `[<EntryPoint>]`), mas o modelo
declarou `let rec hanoi (n: int) : int * string list` e chamou `hanoi n 1 3 2`
(linhas 10, 15 e 23). O log **não** tinha `[algo:F#]`, `reparo:` nem `breaker:` —
sem rastro, nenhuma mecânica é observável.

**Causa.** (a) O mapa de `FsSyntaxRepair` tratava `FS0003` só como `step` no range;
aridade (declaração com menos parâmetros que a chamada) não tinha dica. (b)
`scripts/run-isolated.sh` não exportava `TCTX_DUMP=1`, então a fase com tools
rodava muda.

**Correção.**
1. `FsSyntaxRepair` — ramo `FS0003` + `not a function and cannot be applied`,
   **depois** do `step` (para não roubar o caso do range): mede na fonte a
   aridade declarada e a quantidade de argumentos da chamada da linha do erro
   (`FindArityMismatch`/`CountParams`/`CountArgs`) e ensina a forma que compila
   (`let rec soma (a: int) (b: int) (c: int) : int = …` chamado `soma 1 2 3`).
   Sem posição/medida a dica se **cala** — nunca chuta aridade.
2. `FsMini` — a regra entra na linha **única** `FS0751/FS0003` (o pin
   `U-tails-path-simulation-rules` exige uma ocorrência por código).
3. `scripts/run-isolated.sh` — `export TCTX_DUMP=1`: todo isolado passa a gravar
   turno, `reparo:` e `breaker:` no log.

**Prova.** `units` **138 PASS / 0 FAIL SKIP=6** (pin novo `U-fs-repair-arity`:
caso medido + aridade coerente em silêncio + sem linha/coluna em silêncio),
`scripts/diag/fs-repair-probe.fsx` com `declaracao e chamada com a mesma aridade
(FS0003)` = OK, build `0 Aviso(s) / 0 Erro(s)`.

## 35. 12ª amostra do `E-mission-algo-fs`: FS0039 de uso antes da declaração e aviso de salvage com exemplos (2026-10-29)

**Sintoma.** `logs/isolated/E-mission-algo-fs_-20261029-203932.log` (`PASS=0 FAIL=1`,
2 × 26 turnos): 13 builds com **15 ocorrências do MESMO** `FS0039: The value or
constructor 'dest_aux' is not defined` — `dest_aux` usado nas linhas 11 e 15 e
declarado na linha 19 (`let dest_aux = 3`). F# lê o arquivo de cima para baixo.
**`reparo=0` em 52 turnos**: o ramo `FS0039` só cobria `mod` e `let` sem `rec`, então
a dica ficou muda a rodada inteira. Junto: 13 turnos sem tool call (12 deles
`BRIDGE (edit): JSON invalido/truncado`), 6 `oldString não encontrado`, breaker
`nudge` 11× / `trip` 0.

**Causa.** (a) Ordem de declaração no nível do módulo não tinha regra. (b) O aviso
de salvage era uma frase única: dizia "use `\n`" mas não mostrava nenhum args
válido — o modelo copia o literal que vê.

**Correção.**
1. `FsSyntaxRepair` — ramo `FS0039` "valor usado antes da declaração no módulo":
   mede a linha da declaração (`let [rec] nome`) e a do **primeiro uso** (fora de
   comentário) e, quando uso < declaração, ensina a forma que compila
   (`let auxiliar = 3` acima de quem usa; ou receber por parâmetro). Sem declaração
   no fonte, silêncio.
2. `ToolCallRepair` — o aviso de salvage passou a **compor**: cabeçalho + **3
   exemplos de args JSON válidos** (`write` multilinha, `edit` com `oldString`, `shell`)
   + o **help curado da tool recusada** via `ToolHelp.For` (fonte única — nada de
   schema duplicado). Sem nome de tool, cita `write/edit/read/shell`. Vale para a
   bridge (`Program.cs` no salvage), o teste E2E e os belts: todos chamam `Notice`.
3. `ToolHelp` — `For(string)` / `For(IEnumerable)`: o mesmo texto do schema,
   acessível fora do array de tools.
4. `FsMini` — a regra de ordem entra na linha **única** de `FS0039`.

**Prova.** `units` **139 PASS / 0 FAIL SKIP=6** (`U-fs-repair-use-before-def` novo;
`U-toolcall-salvage-notice` agora exige os exemplos — cada um parseado — e o help
curado da tool), `fs-repair-probe.fsx` com `valor declarado antes do primeiro uso
(FS0039)` = OK, build `0 Aviso(s) / 0 Erro(s)`.

## 36. 14ª amostra do `E-mission-algo-fs`: FS0001 com ramo `string` e dica de array (2026-10-29)

**Sintoma.** `logs/isolated/E-mission-algo-fs_-20261029-215110.log` (`PASS=0 FAIL=1`):
11 × `FS0001` e **9 reparos** injetados, mas o programa não converge. O erro central
é `Program.fs(23,17)`: `All branches of an 'if' expression … which here is 'string'.
This branch returns a value of type ''a list'` — o fonte tinha
`subSeq @ "," + nextMove + "," + finalSubSeq` (lista e texto misturados no mesmo `if`).

**Causa.** O ramo `FS0001` "branches of an 'if'" devolvia **sempre** a dica de array
(`Array.append`), qualquer que fosse o tipo nomeado na mensagem. Com ramo `string` a
dica mandava usar `Array.append` — não ensinava nada do defeito real (texto × lista),
e o modelo repetia a linha. Segundo buraco: `FS0001` com `''a list'` × `'string'`
caía na regra genérica de lista/array, que também não fala de texto.

**Correção** (nos dois ramos que já existiam — nada de regra nova):
1. `branches of an 'if'` lê o tipo NOMEADO na mensagem: `'string'` → TEXTO com `+`,
   LISTA com `@`, ARRAY com `Array.append` (e "nao misture lista com texto"); `list`
   → `@`; resto → `Array.append` (como antes).
2. `FS0001` com `''a list'` + `'string'` → "`@` junta LISTA e `+` junta TEXTO".
3. `FsMini`: a linha única de `FS0001` passa a citar os três jeitos de juntar.

**Prova.** `units` **140 PASS / 0 FAIL SKIP=6** (pin novo
`U-fs-repair-if-branches-string`: ramo string não recebe dica de array + ramo lista
continua com `@`), probe com `texto junta com + e lista com @ (FS0001 ramo string)`
= OK, build `0 Aviso(s) / 0 Erro(s)`.




## 37. K-Means F#: `|]` sem `}`, dica de `<...>` fora de contexto e o breaker que nunca fechava (2026-10-30)

**Sintoma.** `logs/isolated/E-mission-algo-fs-kmeans_20261030-001948.log`: 26
turnos, `FS0010 Unexpected symbol '|]' … Expected '}'` em **todas** as reescritas
e o log mostrando `breaker: nudge (4 falhas)` / `nudge (5 falhas)` dezenas de
vezes — **ZERO trip**. O mesmo aconteceu no btree (`FS0001` pedindo
`'int list list'`, 8 turnos, fonte nunca corrigido).

**Causas (três, medidas).**
1. **Furo do gate**: `EvaluateFailureLoop` contava falhas na janela de `2*hard`
   resultados. No loop real cada falha vem cercada de sucessos cosméticos
   (`write ok`, `read ok`, `ls ok`), então a janela de 12 nunca guardava 6 falhas:
   o contador parava em 4-5 e o `hard` nunca era atingido.
2. **Dica errada fora de contexto**: o ramo `FS3156/FS0010 <...>` disparava com
   qualquer `<` no fonte (`list<Point>` é legítimo) e respondia a um erro de
   **outra** linha — e a escalada da dica repetida mandava reescrever o arquivo
   com a mesma falha.
3. **Sem mapa** para os dois erros reais: `List.contains lista valor`
   (argumento invertido) e `^` como potência (FS0101).

**Correção.**
- `ToolsAntiRepeat.FailureKey`: assinatura da falha sem posição (`(linha,col)`) e
  com espaço colapsado; o código `FS####` **fica** (erro diferente = progresso).
  `EvaluateFailureLoop` conta a assinatura no **histórico inteiro** e usa
  `max(streak, doAlvo, mesmaFalha)` — 6 builds com o mesmo diagnóstico cortam o
  turno mesmo com `write` ok intercalado.
- `FsSyntaxRepair.SourceLineAt/SourceLineNumber`: leitura única da linha do erro
  (usada também pelo `FindArityMismatch`, sem duplicar o parse).
  O ramo `<...>` só responde quando a **linha do erro** tem `<` (ou quando o erro
  não traz posição, como nos pins antigos).
- Regras novas, gateadas pela linha do erro: `FS0010 '|]' + '}'` →
  cada registro fecha com `}` antes do `|]`; `FS0001 '... list list'` +
  `List.contains` na linha → valor antes, lista depois; `FS0101` + `^ <n>` →
  `**` float / `pown`.

**Prova.** `U-breaker-same-error` (3 falhas iguais avisam, 6 cortam, erros
distintos não cortam), `U-fs-repair-record-array-close`,
`U-fs-repair-list-contains-order`, `U-fs-repair-power-operator` e
`U-fs-repair-generic-syntax` (negativo: `<` legitimo fora da linha não puxa a
dica) — todos PASS; build `0 Erro(s)`.

## 38. Missões de algoritmo: quatro em C#, três em F# (2026-10-30)

**Decisão.** As quatro missões (busca binária, árvore de busca, K-Means, quick +
bubble sort) passam a existir **em C#** (`E-mission-algo-cs-bin`,
`-cs-btree`, `-cs-kmeans`, `-cs-sort`) e o **K-Means F# foi removido**
(`E-mission-algo-fs-kmeans`): o algoritmo segue coberto em C#, e o F# fica com
as três missões em que o modelo converge (`-fs-bin`, `-fs-btree`, `-fs-sort`).

**Mecânica.** Um único `RunAlgoMission(name, lang, alg, accept, hardcoded)` e um
único `AlgoTaskPrompt(alg, ws, lang)` — o enunciado é o mesmo para as duas
linguagens; só caminho de arquivo/projeto muda. Aceite e recusa de hardcode são
os mesmos helpers (`AlgoOkBin/BTree/KMeans/Sort`, `HasHardcoded*`), sem cópia por
linguagem. O `done` do laço e o corte entre etapas usam o aceite da missão
(`accept ?? AlgoOk`), com o aceite do Hanoi só como default do `E-mission-algo-cs`.

**Prova.** `E-mission-algo-fs-btree` **PASS 27,9 s** (3 turnos: read, edit, shell)
e a bateria isolada das oito missões em `logs/isolated/algo-batch-*.summary`.

## 39. `--only` por prefixo rodava as cinco missões; semente C# colidia com `public class` (2026-10-30)

**Sintoma.** O step `algo-cs-bridge` do WAR (que pedia só `E-mission-algo-cs`)
rodou **cinco** testes: `PASS=4 FAIL=1` dentro do step errado
(`logs/isolated/algo-batch-20261030-004221.summary`), e o FAIL do
`E-mission-algo-cs-sort` bloqueou os steps seguintes pelo rerun-guard.

**Causas (duas, medidas).**
1. `TestContext.MatchesOnly` casa por **substring**: `E-mission-algo-cs` é
   prefixo de `-cs-bin`, `-cs-btree`, `-cs-kmeans` e `-cs-sort`. Com as quatro
   missões novas em C#, o step da missão base passou a ser a bateria inteira.
2. A semente do fonte C# era `Console.WriteLine("ok");` (top-level statement).
   No `E-mission-algo-cs-sort` o modelo escreveu `public class Program` no mesmo
   arquivo → `CS0106: The modifier 'public' is not valid for this item`, e ainda
   rodou `--project <ws>/mcs.csproj` (sem o diretório `mcs/`). O enunciado citava
   o projeto como `mcs.csproj` no cabeçalho e com caminho completo só no comando:
   o modelo copiou a forma curta.

**Correção.**
- Token com `=` na frente em `--only` exige o nome **exato**
  (`--only =E-mission-algo-cs`); substring continua valendo para o resto.
  `WarTemplate` passa a usar `=E-mission-algo-cs` no step da missão base.
- Semente C# da missão passa a ser só o comentário `// resolva o pedido aqui`
  (sem top-level statement para colidir com `public class Program`); o F# segue
  com `module Program` + `printfn "ok"`.
- `AlgoTaskPrompt` cita o projeto **sempre** com caminho completo
  (`<ws>/mcs/mcs.csproj`, `<ws>/mfs/mfs.fsproj`), no cabeçalho e no comando.

**Prova.** `--only =E-mission-algo-cs-sort` roda um teste só; bateria isolada
nova em `logs/isolated/algo-batch2-*.summary` (o breaker cortou o laço em
`trip (6 falhas)` em vez dos 26 turnos da rodada anterior).

## 40. "Aguardando instruções." no painel era o PROBE do preflight, não um agente travado (2026-10-30)

**Sintoma.** O operador viu a linha `00:56:16 Aguardando instruções. assistant`
e suspeitou de cliente externo mandando mensagem ou de gate estimulando o modelo
a "esperar".

**Causa (medida no `/tmp/bridge-gpu.log`, não é palpite).** Nenhum teste da
bateria manda request sem `tools`; todas as entradas `"has_tools":false` com
exatamente `chars=5042` de system saem em **um por start de runner**: `00:52:06`,
`00:52:53`, `00:53:11` e a rajada `00:56:15/17/18/20/21` (as cinco execuções
abortadas pelo rerun-guard). Essa é a assinatura do **probe de liveness do
`StackPreflight`**: `messages:[{role:user, content:"ok"}]`, `max_tokens:8`, sem
tools. Como a bridge está em `--mode bridge`, ela injeta o MainHarness (que manda
agir com tools); recebendo "ok" sem tarefa, o modelo responde exatamente
`Aguardando instruções.`.

Reprodução literal na bridge de produção:

```bash
curl -s localhost:4124/v1/chat/completions -H 'Authorization: Bearer guest@123' \
  -H 'Content-Type: application/json' \
  -d '{"model":"Gemma4-E4B-Q4","max_tokens":8,"messages":[{"role":"user","content":"ok"}]}'
# -> {"content":"Aguardando instruções."}
```

**Correção.** `StackPreflight.Probe` passa a mandar instrução fechada
(`Liveness check. Responda apenas: PONG`) — a mesma chamada devolve `PONG`. O
probe continua medindo o que importa (o modelo responde) e deixa de fabricar
texto de espera no console/painel do operador.

**Prova.** `curl` acima antes/depois; `U-preflight-go` e `U-preflight-model-fail`
PASS. O log deixa de ter o texto: a linha `system-sent` do probe segue idêntica
(`has_tools=false`, 5042 chars), mas a resposta é `PONG`.

## 41. `--project` inexistente e conteúdo elidido: as duas causas reais do loop de compilação (2026-10-30)

**Sintoma.** `E-mission-algo-cs-btree` gastou 26+13 turnos (`logs/isolated/_E-mission-algo-cs-btree_-20261030-005310.log`)
alternando `shell` com erro e `edit` recusado, e fechou FAIL.

**Causas (duas, lidas no log).**
1. O modelo chamava `dotnet run --project <ws>/mcs.csproj` (sem o diretório
   `mcs/`) e a tool respondia só `The provided file path does not exist` — sem o
   caminho real. Repetiu 5 vezes, alternando com a forma certa.
2. O modelo emitiu `// ... (restante do código)` como `newString` de `edit`; o
   arquivo passou a ter o marcador no lugar do corpo, o build quebrou e os
   turnos seguintes queimaram em `oldString não encontrado`.

**Correção (Core, fonte única).**
- `ShellToolHandler.ProjectPathBlocked`: `--project`/`-p`/`--project=` com alvo
  inexistente responde `projeto não encontrado: '<p>' (existe em: <real> — use
  esse path)`, reaproveitando `WorkspaceGuard.NotFound` + `WorkspaceGuard.Locate`
  (mesma regra de `read`/`edit`, sem texto novo por tool).
- `ToolContentGuard.Elided` (novo, em `Core/Tools`): `write` (`content`) e `edit`
  (`newString`) recusam texto com marcador de continuação no lugar do corpo e
  ensinam a forma certa (trecho completo, linha por linha), sem exibir o literal
  proibido.

**Prova.** `U-tools-project-path-hint` e `U-tools-elision-refused` PASS (com
negativo: caminho existente não é recusado; conteúdo completo grava o artefato).

## 42. Anti-repeat de args cortava `edit`/`write` no meio e o salvage virava loop de prosa (2026-10-30)

**Sintoma.** `E-mission-algo-cs-sort` gastou 22 turnos: 6 `edit` abortados no
meio do stream, o salvage recusava o args e o modelo repetia o aviso
`BRIDGE (edit): ...` como texto (3 prosas seguidas encerram a tentativa).

**Causa (lida no `/tmp/bridge-gpu.log`).**
```
{"event":"anti-repeat","where":"stream-abort-tool-args","tool":"edit","window":1024,"chars":3957}
{"event":"salvage-args-rejected","tools":"edit"}
{"event":"anti-repeat-salvage","where":"stream-abort","calls":0}
```
O args era o **arquivo inteiro** (o modelo usou `edit` como `write`, com
`oldString` de uma linha). A cauda de 1024 chars já aparecia antes (o próprio
conteúdo duplicado), o anti-repeat abortava no meio, o JSON ficava inválido e o
salvage não podia entregar a call. O modelo recebia o aviso genérico de "JSON
inválido", copiava o texto e repetia.

**Correção (mecanismo, não afrouxamento de aceite).**
- Bridge (`Program.cs`): `edit`/`write` **não abortam** por anti-repeat de args.
  O args dessas tools é o arquivo inteiro e repetir trecho é legítimo; o loop
  delas é contido pelo `ToolsBreaker` (soft 3 / hard 6), pelas guardas de
  conteúdo e pelo compilador. O log `anti-repeat-args` continua (sonda), só não
  corta a call.
- `ToolContentGuard.WholeFileRewrite`: `edit` com `newString` de arquivo inteiro
  (várias declarações) e `oldString` de uma linha é recusado com a tool certa
  (`write`), antes de duplicar cabeçalho/declaração (CS1022).

**Prova.** `U-tools-whole-file-rewrite` e `U-antirepeat-args-window` PASS; no
`/tmp/bridge-gpu.log` o evento `stream-abort-tool-args` deixa de aparecer para
`edit`/`write` (só o probe `anti-repeat-args`). Bateria `logs/isolated/algo-batch4-*.log`
mostra `PASS E-mission-algo-cs`/`cs-bin`/`cs-btree`/`cs-kmeans` antes da correção;
`cs-sort` é o caso medido.

## 43. `mkdir -p` era lido como `--project` do dotnet e matava a cadeia no `cd` (2026-10-30)

**Sintoma.** `U-tool-feedback-shell-cwd` falhou com `cadeia mkdir && cd roda
(err: projeto não encontrado: 'sub' ...)`; a cadeia `mkdir -p sub && cd sub`
morria antes de executar, e a mensagem falava de projeto `.csproj`/`.fsproj` no
meio de um `mkdir`.

**Causa.** `ShellToolHandler.ProjectPathBlocked` varria o argv de **qualquer**
comando procurando `--project`/`-p`; `mkdir -p sub` casava o `-p` e tratava
`sub` como caminho de projeto. O `-p` só é alias de `--project` no `dotnet`.

**Correção.** `ProjectPathBlocked` só examina argv quando `argv[0]` é `dotnet`
(`Path.GetFileName(argv[0]).Equals("dotnet", OrdinalIgnoreCase)`); `mkdir -p`,
`cp -p` e `tar -p` deixam de ser confundidos com projeto.

**Prova.** `U-tool-feedback-shell-cwd` PASS (cadeia roda, cwd `sub` reportado,
aviso de raiz e path `sub/arquivo` presentes); units `PASS=153 FAIL=0 SKIP=6`.

## 44. `edit` de arquivo inteiro recusado virava 3 edits idênticos e CS1513 (2026-10-30)

**Sintoma.** `E-mission-algo-cs-kmeans` falhou com `CS1513: } expected` na linha
122. O `/tmp/bridge-gpu.log` mostra o modelo emitindo **três** `edit` de 8.410
chars (o arquivo inteiro no `newString`, âncora de uma linha), todos com
`tool_exec edit ok:false`, intercalados por `shell` que também falhava; depois
um `HTTP 500` do upstream. O `Program.cs` em disco tinha 3.746 chars e terminava
no `}` do `Main`, sem fechar classe/namespace.

**Causa.** A guarda `WholeFileRewrite` recusava o `edit` e mandava usar `write`.
O modelo não trocava de tool: repetia o mesmo `edit` (a isenção de anti-repeat
para `edit`/`write` não o impedia) e o arquivo truncado nunca era regravado. A
recusa era segura contra duplicação (CS1022), mas criava loop quando o modelo
insistia na mesma forma.

**Correção.** `EditToolHandler` aplica a intenção como **reescrita total** quando
a âncora de uma linha existe no arquivo: grava `newString` inteiro (mesma
semântica de `write`), registra `edit-whole-file-as-write` e devolve sucesso. Se
a âncora não existir, a mensagem aponta o caminho real. O `edit` de trecho
continua igual.

**Prova.** `U-tools-whole-file-rewrite` PASS (arquivo regravado uma vez, sem
duplicar `static void Main`); `U-tools-elision-refused` PASS; build
`0 Warning(s)/0 Error(s)`.

## 45. Artefato escrito pela tool nascia `root:root` e quebrava o fixture no turno seguinte (2026-10-30)

**Sintoma.** L4 restante (`--war-only belts-build-final,belts-orch-final,front,webui`):
`belts-build-final-bridge` PASS (199s), `belts-orch-final-bridge` PASS (477s),
`front-bridge` **FAIL em 23s** com `cause=undetermined` e `webui-integracao-topicos`
SKIP por fail-fast. No log do step:

```
PASS E-front-gen (22.2s)
FAIL E-front-edit: Access to the path '/tmp/opencode/frontpipe/app/wwwroot/css/app.css' is denied.
```

**Causa (medida, não é palpite).** A bridge sobe como root (`systemctl show
bridge-gpu -p User` → `User=root`). O `write` do modelo (E4B-Q4, role build) gravou
os três artefatos dentro do workspace do teste — dono `mint` — e o arquivo ficou
`root:root` modo 644. No turno seguinte o fixture do `E-front-edit` restaurou o
baseline com `File.Copy(gen, target, overwrite: true)` rodando como `mint`: abrir o
alvo para escrita é negado. Não era o modelo, nem o enunciado, nem a bridge HTTP —
era permissão de arquivo herdada de quem escreveu.

**Correção (fonte única, Core).** `Core/Tools/ArtifactOwnership`:
`write` e `edit` adotam, ao final, o dono do **ancestral existente mais próximo**
(o workspace do cliente) via `chown --reference=<dir>`; quando o `write` cria a
cadeia de diretórios, a raiz criada entra com `chown -R`. Sem `euid == 0` a chamada
é no-op (quem escreve já é o dono). Evento `artifact-owner` vai para o log com
`ok`/`detail` (melhor esforço: falha de `chown` não derruba a tool).

**Correção (fixture).** `E2EFrontPipeTests.Replace(src, dest)` apaga antes de
copiar: o UNLINK depende só da permissão do **diretório** (do dono do workspace), não
do arquivo, então a rodada seguinte nunca morre por herança de `root`. Usado na
restauração de baseline do `E-front-edit`.

**Prova.** `U-tools-artifact-owner` (novo, verde): cadeia nova criada pelo `write`,
`OwnerDir` = workspace, dono consegue sobrescrever o artefato. Pins vizinhos verdes
na mesma rodada: `U-tools-elision-refused`, `U-tools-whole-file-rewrite`,
`U-tools-project-path-hint` → `[test] PASS=4 FAIL=0`.

## 46. L1 vermelho em `U-diag-reset-panel`: assert contava linhas do painel de OUTRO step (2026-10-30)

**Sintoma.** Retomada por `--war-resume-stamp` caiu no L1 e o step `units` falhou em
1s: `FAIL U-diag-reset-panel: assert: painel lista o step publicado`
(`PASS=4 FAIL=1`), parando a bateria antes de qualquer nível útil.

**Causa.** O pin publicava `U-panel-probe` e exigia `GET /api/test/steps?war=1`
com **exatamente 1** linha. Dentro da bateria o driver publica `step_start` do step
corrente (`verdict=RUN`) — regra do painel "mostrar o step em EXECUÇÃO" —, então a
lista já tinha a linha `units` e o assert virava falso negativo. Fora de um run
(só o pin) o número coincidia, e o defeito ficou escondido.

**Correção.** Assert ESTRUTURAL no mesmo contrato: (a) a lista contém a linha do
step publicado, (b) toda linha de `war=1` tem `mode` (o filtro do painel). O resto
do pin (reset sem `panel` preserva enunciado/histórico; reset com `panel` zera
enunciado, histórico e lista) continua igual.

**Prova.** `--only U-diag-reset-panel,U-diag-reset,U-status` → `[test] PASS=3 FAIL=0`.

## 47. Portão de fluxo acusava "registro anterior ao teste" em fase que usa a bridge secundária (2026-10-30)

**Sintoma.** `front-bridge` FAIL em 34s com o painel de edição todo verde:
`fluxo bridge incompleto: registro anterior ao teste (nenhum request deste teste
chegou a bridge)`. `E-front-gen` PASS, `E-front-edit` aplicou os 3 enunciados de CSS
(`linhas alteradas=1` cada) e o step morreu no portão P1.

**Causa.** O portão confere `GET /api/test/last-system` na bridge **do gate**
(4124). As fases de edição do front mandam o modelo para o **Q3 (4125)** por
projeto; a 4124 só tinha o registro do `gen`, mais velho que o início do teste →
"registro anterior". Teste só de leitura (`E-front-verify`) cairia no mesmo erro.

**Correção.** `BridgeFlowGate.Probe(bridge, testStartMs, modelRequestsBefore)`:
o `TestContext.Run` lê `model_requests` antes do teste e o portão só julga quando o
contador SUBIU no run. Sem request na bridge do gate ⇒ `INCONCLUSIVO: nenhum
request de modelo nesta bridge durante o teste` (fase em bridge secundária ou teste
de leitura). Com request, o aceite é literal o de antes (main_harness, tails,
tools_tail, lane, idade do registro) — nada afrouxado.

**Prova.** `U-flow-gate` + `U-flow-gate-no-request` → `PASS=2 FAIL=0`; rodada do
front com `[flow] E-front-edit-razor: INCONCLUSIVO: ... [model_requests 5 -> 5]`.

## 48. `oldString` repetido: a dica não dizia o literal certo e o Q3 fechou a fase com "OK" (2026-10-30)

**Sintoma.** `E-front-edit-react` FAIL: `react-painel-2026: react contem Painel de
vendas 2026`. O modelo fez `read` e depois `edit` com `oldString: "Painel de
vendas"`; a tool recusou (`ocorre 2x; use replaceAll ou refine o trecho`) e a
resposta seguinte foi `OK`, sem nenhuma edição.

**Causa (duas).** 1) A dica de duplicidade não enunciava a chamada válida — o modelo
copia o literal da mensagem e ali não havia nenhum (`replaceAll` citado de passagem).
2) O loop do front aceitava "OK" logo após uma tool que falhou: encerrava a fase com
o artefato intacto.

**Correção.** (a) `EditToolHandler`: mensagem passa a trazer a forma certa —
`repita a mesma chamada com "replaceAll": true para trocar as N ocorrencias de uma
vez` (ou trecho maior de uma só ocorrência). (b) `E2EFrontPipeTests.AgentLoop`: se o
turno encerra sem tool call e a última tool falhou, injeta UM empurrão com o erro
exato ("Aplique a correcao agora e so' entao responda OK") antes de aceitar o final.

**Prova.** `U-tools-elision-refused` continua verde; rodada do front reexecutada com
as quatro fases (`gen`, `edit`, `edit-razor`, `edit-react`) + `E-front-verify`.

## 49. `E-front-verify` compara o edit com o estado JÁ editado, e pins do shell exigem stdout exato (2026-10-30)

**Sintoma (front).** Com as quatro fases verdes, `E-front-verify` FAIL:
`#titulo font-size delta 2 antes=20 depois=20 (tol 0.5)` e
`no-regressao #quadro rect_w antes=320 depois=240`.

**Causa.** `EditKind` gravava o retrato da fase em `Ws/gen/before.json` — o MESMO
arquivo que é o baseline do `E-front-gen` e o primeiro elo da cadeia do verify. As
fases rodam em sequência (css → razor → react) e cada uma regravava o arquivo com o
estado corrente, então o "baseline" da cadeia passou a ser o estado DEPOIS dos edits
de CSS: delta 2 virava "antes=20 depois=20" e o quadro "regredia" de 320 para 240.

**Correção.** Baseline por fase em `gen/before-<kind>.json`; `gen/before.json` volta a
ser escrito só pelo `E-front-gen`.

**Sintoma (webui).** `webui-integracao-topicos` FAIL: `U-tool-shell-echo` e
`U-tool-bash-alias` (ToolExecutor 75/77), com o resto da suíte verde.

**Causa.** Os dois pins exigiam `r.Output == "ola"` / `== "via-bash"`, mas o `shell` do
Core anexa a linha `[shell] cwd desta chamada: <rel>` em toda resposta (contrato
pinado por `U-tool-feedback-shell-cwd`, regra "a falha de path devolve o caminho
real"). O pin era anterior à cauda de cwd.

**Correção.** Contrato estrutural: o STDOUT do comando inicia a resposta
(`Output.StartsWith("ola")` / `StartsWith("via-bash")`); a linha de cwd continua sendo
verificada pelo pin próprio dela.

**Prova.** front: `PASS=5 FAIL=0` (`E-front-gen/edit/edit-razor/edit-react/verify`);
webui: `ToolExecutor Unit: 77/77 PASS, 0 FAIL`.

## 50. Turno VAZIO do Q3 encerrava a fase de edição e o `E-front-edit` aceitava `diff=0` (2026-10-30)

**Sintoma.** L4 completo: 7 steps verdes (hanoi/bin/btree/kmeans/sort + belts build e
orch) e `front-bridge` FAIL em 38s, desta vez em `E-front-verify`:
`titulo-2px #titulo font-size delta 2 antes=18 depois=18`.

**Causa.** No `titulo-2px` o Q3 fez `read` e, no turno seguinte, respondeu **vazio**
(1 token, sem tool call) — artefato intacto. O `E-front-edit` aceitava, porque para
`kind=css` a fase só conferia `diff <= 3` (0 linhas passava) e não havia
`must_contain` (o valor alvo é relativo ao gen, que varia). O delta só aparecia no
verify, tarde e com mensagem de "modelo não cumpriu".

**Correção (duas, sem afrouxar o aceite).**
1. `E-front-edit`: `diff >= 1` (aplicou a mudança pedida) **e** `diff <= 3` (só o
   trecho do enunciado). Artefato intacto é falha explícita na fase, não no verify.
2. `AgentLoop`: além do caso "tool falhou", empurra também quando o turno vem
   vazio/sem tool call e o alvo continua igual (`stillPending`), com a instrução
   fechada ("aplique a mudanca com `edit` no trecho exato ou `write` com o arquivo
   inteiro e so' entao responda OK"). Um empurrão por fase.

**Prova.** `--war-level 4 --war-only front,webui` → `exit=0`:
`front-bridge PASS (40s)`, `webui-integracao-topicos PASS (55s)`; verify com
`delta 2: antes=18 depois=20`, `rect_w eq 320: antes=240 depois=320`,
`padding-left eq 16: antes=8 depois=16`; webui `ToolExecutor 77/77`,
`Chat Integration 10/10`.

## 51. Hanoi do L4: contrato da sequência não estava enunciado (e o guard de rebuild barrou o re-run) (2026-10-30)

**Sintoma.** L4 completo: `algo-cs-hanoi-bridge` FAIL em 177s, com
`hanoi=15` correto e `hanoi_seq=1->3,1->3,3->3,...` — contagem certa, movimentos
ilegais (`3->3`, disco saindo do lugar errado). O modelo hardcodou a haste auxiliar
como 3 ("Assuming destination=3 is the auxiliary peg"). Os 7 steps seguintes foram
SKIP por fail-fast.

**Causa.** O enunciado pedia "a sequencia completa ... no formato 1->2,1->3,..." mas
não declarava o contrato que o teste confere: a sequência canônica é a de movimentos
LEGAIS (origem ≠ destino, disco do topo, torre inteira na haste 3). O modelo validou
o próprio resultado só pela contagem (`hanoi=15`).

**Correção (enunciado, sem receita).** Estágio 1 do hanoi passa a declarar o contrato
observável: "cada item move o disco do topo de uma haste para OUTRA (origem e destino
nunca iguais) e a torre termina inteira na haste 3 — o teste confere movimento por
movimento". Nada de passo a passo de recursão; o aceite (`AlgoOk`) é o mesmo.

**Guard de rebuild (comportamento correto, registrado para não confundir).** Depois
do FAIL, `--war-only algo-cs-hanoi` com o MESMO binário foi BLOCKED no preflight
(`rerun_sha512`/`guard_ativo_fail_fast`): re-rodar binário idêntico após vermelho é
proibido. A rodada válida é depois de mudança real + rebuild.

**Prova.** Com o enunciado novo e binário novo: `algo-cs-hanoi-bridge PASS (84s)`,
`hanoi=15 · primes100=25 · primes1000=168 · twin100=8`.

**Resíduo de dono (limpo).** `/tmp/e2e-mission-*` com `obj/` root-owned fazia
`FreshWorkspace` cair em `<dir>-<stamp>-<pid>` (`cleanup ignorado ... UnauthorizedAccess`).
Os diretórios canônicos foram removidos; a herança de dono do §45 evita a recorrência
para o que as tools escrevem (artefatos de `dotnet build` via shell ainda nascem root
— o fallback segue como rede de segurança e é logado).

## 52. Trilha C# não tinha reparo de sintaxe in-flow: `btree` gastou 26 turnos e fechou com `}` faltando (2026-10-30)

**Sintoma.** L4 completo: `algo-cs-hanoi` PASS (118s), `algo-cs-bin` PASS (16s) e
`algo-cs-btree` **FAIL em 345s** (`causa=bridge`), com o aceite lendo:
`ERRO: exit 1: .../Program.cs(101,6): error CS1513: } expected`. Arquivo entregue com
**26 abre-chave e 25 fecha-chave** (a classe mais externa ficou aberta). Os 6 steps
seguintes SKIP por fail-fast.

**Causa.** O reparo in-flow de sintaxe existia **só** para F# (`FsSyntaxRepair`, com a
tabela FsMini). A trilha F# foi arquivada em 2026-10-30 e a C# é a válida — mas o laço
de missão não tinha nenhum mapa C#: o modelo recebia o texto cru do compilador a cada
turno, repetia `edit` no mesmo alvo e terminava o orçamento de 26 turnos com o bloco
aberto.

**Correção (Core, fonte única).** `Core/CsSyntaxRepair` com o MESMO contrato do
`FsSyntaxRepair` (`Suggest(source, compilerError, repeatCount)` +
`HasDiagnostic(output)`) e o ponteiro de linha compartilhado
(`FsSyntaxRepair.WithErrorLine`, agora `internal`):

- `CS1513` (`} expected`): conta `{` e `}` do arquivo e enuncia quantos blocos faltam
  fechar, com a forma sempre válida ("acrescente N linhas contendo só `}` no fim, a
  última fecha a declaração mais externa"). Fonte já balanceada não gera dica.
- `CS1026`/`CS1002`: fecha o parêntese / termina a instrução **na linha apontada**.
- `HasDiagnostic` aceita `error` e `warning` (`CS0168`/`CS0219` saem como warning).
- A partir da 3ª vez da MESMA dica o Core manda reescrever o arquivo com `write`
  (mesma escalada do F#: dica idêntica repetida não ensina).

`E2EMissionTests.RunAlgoPhase` passou a escolher o provedor por linguagem
(`lang switch`), sem duplicar a mecânica de contagem de dica: o teste só conta
repetições e o Core devolve a forma.

**Prova.** `U-cs-repair-missing-brace` (novo) + `U-fs-repair-diagnostic-detection`:
`[test] PASS=2 FAIL=0` — dica com "1 bloco(s) aberto(s) sem fechar", "3 abre-chave e
2 fecha-chave", ponteiro "linha 10", escalada na 3ª, fonte balanceada sem dica e erro
sem código CS sem reparo.

## 53. Portão P1 julgava teste de CAMINHO DE ERRO (nenhum `system-sent` a conferir) (2026-10-30)

**Sintoma.** L1: `units` PASS (8s), `integration-bridge` **FAIL em 10s** e
`topics-bridge` SKIP. No log do step, depois de `E-iot-event-queue` PASS:

```
FAIL E-error-mitigation: fluxo bridge incompleto: registro anterior ao teste
(nenhum request deste teste chegou a bridge) [path=responses ... main_harness=true]
```

**Causa.** `E-error-mitigation` é o caminho de ERRO por definição: manda corpo sem
`input` e exige 4xx. A bridge responde antes de montar o system, então **não grava
`system-sent`** — só conta a tentativa em `model_requests`. O delta do contador
(§47) subiu, o portão julgou, e o único registro da bridge era do teste anterior →
"registro anterior ao teste". Falso negativo: não havia fluxo a conferir.

**Correção (declaração do teste, sem heurística).** `TestContext.NoModelFlow`:
o teste declara que não serviu request de modelo (caminho de erro ou só leitura de
`/api/status`), o `Run` zera a marca antes de cada teste e o portão sai do
julgamento. A decisão virou função pura `BridgeFlowGate.Applies(mode, name,
noModelFlow)` — testável, sem espalhar condição pelo laço. Aplicado em
`E-error-mitigation` e `E-healthy-concurrency`.

**Nada afrouxado:** todo E2E em modo bridge que serve request de modelo continua
julgado com o aceite literal (main_harness, tails, tools_tail, lane, idade).

**Prova.** `U-flow-gate` + `U-flow-gate-no-request` + `U-flow-gate-applies` +
`U-cs-repair-missing-brace` → `[test] PASS=4 FAIL=0`.

## 54. `--war-webdriver` vazava para o `front-bridge`: janela aberta sem demonstrar nada e baseline em `Timeout 30000ms` (2026-10-30)

**Sintoma.** L4 `front-bridge` vermelho em 53s com a app respondendo 200 em
`http://127.0.0.1:5199/`:
`[front] measure exit=1: ... status=200 titulo= quadro_w= erro=TimeoutException: Timeout 30000ms exceeded`.
Antes disso o step abria uma janela de navegador que não acompanhava nada do
teste (o "demonstrar" só faz sentido no step de Web UI).

**Causa.** O commit que ligou o WebDriver na esteira (3ea6887) passou
`--webdriver --webdriver-url` também para o `E-front`, e o
`E2EFrontPipeTests.Gen` troca a sonda estática (`StaticSnapshot` de
Razor/CSS/TSX) pela medição de navegador (`Measure`) quando
`TestContext.WebDriver` está ligado. Com `HEADED=1` isso virava Chromium
headed no meio da esteira; o `#root` não montava dentro dos 30s e o aceite
"React montou no #root" caía por motivo errado.

**Correção (escopo da flag, sem afrouxar aceite).** O `--webdriver` pertence
**só** ao último step (`webui-integracao-topicos`). O `front-bridge` voltou a
`--only E-front` (sonda estática, a mesma das rodadas verdes). O aceite do
front não mudou: continua exigindo build + baseline com `#titulo`/`#quadro`
aplicados.

**Prova.** `front-bridge` verde com `[front] sonda estatica (Razor/CSS/TSX),
sem webdriver` no log do step, e a janela headed aparecendo apenas no
`webui-integracao-topicos` (`[webui] Playwright em ... headed=1`).

## 55. Web UI: janela Plan recebia enunciado de implementação e a Web rodava sem o harness da esteira (2026-10-30)

**Sintoma.** L4 `webui-integracao-topicos` vermelho (attempt 4, run `ff076b5`,
490s) com duas causas somadas:

```
[ui] azul: agente Plan (reasoning na UI) = 2|Engenheiro de Software - Plan (GPU) — Gemma4-E4B-Q4 sub=nenhum
[ui] FAIL azul: conversa 2794 chars, msgs=1, toolcalls=10, toolsUi=True, raciocinio=0 chars, cobertura 3/8
[simple] FAIL harness: TargetClosedException Target page, context or browser has been closed
```

**Causa 1 (perfil errado na janela).** A face "cognitiva" da faixa reenviava o
`belt.Prompt` — o enunciado de *implementação* do Orch ("divida a tarefa de
implementacao usando subagent tool"). O perfil Plan não executa: com tools no
array ele gastou 10 tool calls, o raciocínio não apareceu (o turno virou
execução) e a matéria nunca foi respondida como texto. Regra do harness: pedido
sem tools termina com `ENTREGUE O ARTEFATO COMPLETO EM TEXTO, SEM TOOL CALLS`.

**Causa 2 (harness da Web diferente da esteira).** O `ChatController` montava as
opções do turno com `Harness = false`: sem `MainHarness` e sem as caudas, o
caminho Web não era o caminho medido pelo WAR (main harness + tails +
anti-repeat). Medido no `status` da ponte e no evento `main-harness` da raia
bridge (que a Web não emitia).

**Correção.**

1. `BeltUiTests.PlanTextFor`: enunciado TEXTO PURO por faixa (mesma matéria,
   sem tool, sem delegação) com o fecho canônico; o aceite da face Plan exige
   `calls == 0`, texto ≥ 2000 chars e o bloco "Raciocínio" na bolha.
2. `ChatController.BridgeOptionsFor`: `Harness = true` (paridade com a raia
   bridge). `Core/BridgeProxy` passou a decidir a cauda de tools pelo corpo
   (`tools` presente), nos dois formatos — pedido texto-puro recebe só a cauda
   de comportamento. `ToolCallRepair.ArgsOk` (tools salvage) entrou nos dois
   laços: args truncado não chega ao executor, a call recebe o aviso canônico.
3. Faixa `moe` nova: tópicos gerais (NASA / esoterismo Mesopotâmia / cordas),
   um subagente por tema, composição final; aceite exige ≥2 `subagent` e ≥2
   temas na conversa. Subagente segue a regra do projeto: **mesmo tipo do
   chamador e mesma linha** (MoE→MoE Q4, Build→Build Q4, Plan→Plan);
   **só o Orch delega ao Scripter** — nenhuma preferência cruza Q4 (:4124) com
   GPU2/Q3 (:4125).
4. `WarTemplate.StepWeb`: faixas default `azul,moe` (`--war-webui-belts`).

**Prova (sonda direta na Web, sessão agentId=2 com o enunciado texto-puro).**
`POST /Chat/Stream` → 282 deltas de `reasoning_content`, 1141 de `content`,
**0** `tool_calls` (`/tmp/opencode/sse.txt`) — a bolha recebe o raciocínio e o
turno sai em texto, que é o comportamento medido pela faixa.
