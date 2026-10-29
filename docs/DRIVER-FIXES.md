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
sem isso o 500 volta igual. `E2EMissionTests` (C# e F#), `MissionAlgo` e
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
