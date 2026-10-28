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
