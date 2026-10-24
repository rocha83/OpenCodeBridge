# BENCHTESTS — bateria 2026-10-16 (9B Q4_K_M + VL-4B Q3_K_M, RTX 3060 12GB)

Setup vivo: 9B `:4100` `-c 114688 -np 2 -ngl 99 -fa on turbo3` (57344/slot),
VL `:4110` `-c 40960 -np 2 -ngl 99` (20480/slot); bridges `:4124` (out 24k)
e `:4125` (out 8k). VRAM ~11GB/12GB com ambos no ar.

## 1. Stress lorem ipsum (stream longo)
- 4B: 4000 tok @ **22.42 t/s**, completo, sem crash.
- 9B: 13.2k tok, **22.7 t/s** sob carga dupla → **31.3 t/s** solo.
- VRAM cravada em 11108 o teste todo (KV pré-alocado, sem vazamento).
- Cliente curl 400s desistiu do 9B; o llama seguiu gerando (server ok).

## 2. Natureza stateless (~10min, 8 temas x 2)
- 9B 8/8: **37–40 t/s**, coerente.
- 4B 6/8: **58–61 t/s**, mas alucina fluente (araucária de "fruta amarela",
  "folhas largas") + **2× HTTP 500** na `:4125` sem rastro no journal do
  llama nem no log da bridge (só sonda `anti-repeat`). Causa raiz aberta.
- Ressalva: perguntas avulsas sem histórico (`in≈25` sempre) — sem subida
  de ctx de verdade.

## 3. ctx80 com histórico (matemática/estatística/QM + 5 armadilhas)
### 3.1 thinking OFF (perfil build)
- 9B (24 rounds, in 76→16374): t/s **26.3→13.7** (KV). Respostas longas e
  rigorosas; armadilhas triângulo esférico, p-valor e Bell-Everett
  **corrigidas com rigor, 0 alucinação**.
- 4B-VL (26 rounds, in 202→16126): t/s 28.6→13.0 porém **100% lixo**:
  `toolcall toolchain toolchain…` até o teto em TODOS os rounds.
### 3.2 thinking ON (perfil build)
- 9B (29 rounds, in 74→932): ~38 t/s plano, 1 stall (6.1 t/s), mas
  **content vazio** — 700 tokens somem no reasoning, 0 resposta.
- 4B-VL (40 rounds, in 202→1951): respostas curtas/pedantes (12–32 tok),
  sem crescimento de ctx (alvo 16k inalcançado).

## 4. Achado: parâmetro tool chega ao VL em teste de diálogo
O operador lembrava de "mecanismo que envia parâmetro tool diferente aos
modelos VL" — **procede**. No `Passthrough` (chat), id de modelo SEM sufixo
de perfil (`qwen3-vl-4b`) não é reconhecido como opencode (`IsOpencodeModel`
=false) e, com `Harness=true`, o **system recebe a fórmula**
(`WithFormula`: system + `HarnessToolsTail` = "MODO DE USO DAS TOOLS: uma
tool call por resposta…"). A bateria usou `qwen3-vl-4b` (sem sufixo) + um
SystemPrompt acadêmico — ou seja, todo round do VL levou instrução de tool
junto. O 9B usou `qwen3.5-9b-build` (com sufixo → tubo limpo).
Correlação com a degeneração `toolchain`: o tail empurra o VL p/ o canal de
tool-template; somado a thinking OFF + histórico longo, o Q3 colapsa em
loop. Single-shot curto não reproduz (só viés leve), então é gatilho
combinado, não causa única.
**Recomendação: testes só-diálogo devem usar id COM sufixo**
(`qwen3-vl-4b-build`) = tubo limpo, sem tail. Não alterar o roteamento da
bridge sem ordem (comportamento de produto p/ a Web).

## 5. Curvas e limites honestos
- t/s cai ~50% até 16k de ctx (decode O(n) no KV). Pico solo 31–38 t/s;
  dupla simultânea ~22–23 t/s cada.
- Com 2 slots o ctx DIVIDE: 9B 57344/slot, VL 20480/slot. "80% do total"
  (45k/16k) só vale por slot — 80% de 112k não cabe num slot.
- Compactação automática é do **opencode** (cliente); llama faz ctx-shift
  silencioso; bridge não limitava (agora há `context-guard` 95% → 413).
- VRAM com ambos: 11.0–11.6GB/12GB (90–95%). Margem mínima.

## 6. Pendências do operador
- Thinking por perfil/modelo (VL precisa ON p/ não degenerar; 9B rende
  melhor OFF p/ agir).
- Deploy do binário novo nas units (`context-guard`, JWT, thinking-perfil
  no chat) — implementado e commitado, não deployado.
- Push ao GitHub aguardando key.
- Re-rodar ctx80 no VL com id sufixado + SystemPrompt neutro p/ isolar o
  efeito do tail (previsto, não executado).

## 7. HTTP 500 intermitente do VL — causa raiz (2026-10-16 14h)
Re-teste das requests que falharam (nature r1/r5): 4/6 OK, 2/6 500.
Corpo do 500: `{"code":500,"message":"The model produced output that
does not match the expected peg-native format"}`. Log do llama:
`W common_chat_peg_parse: unparsed peg-native output` + `got exception`
→ **é o próprio llama-server que derruba**: a saída crua do Qwen3-VL-Q3
(vazamento do template de tool no meio do chat) não passa no parser
PEG/jinja do template de chat do llama.cpp na extração do
`message.content`. Intermitente por amostragem: quando o VL formata
limpo, 200; quando vaza template, 500.
Não é anti-repeat da bridge, nem prompt, nem ctx. Mitigações possíveis
(não aplicadas): retry da bridge nesse 500 específico (reamostragem
costuma sair limpa); trocar o template de chat do VL no llama;
parâmetros que reduzam o vazamento de template.

## 8. Bateria dual (puro + opencode) e guardas de tools — 2026-10-24
Origem: análise de duas sessões reais (`an-lise-falha-uso-de-ferramentas-em-opencode-sqlite.json`
e `greeting-and-quick-check-in.json`) com 18 falhas em 30 chamadas de tool. Causas medidas
e o que passou a existir no código:

| Causa medida | Guarda implementada | Onde |
|---|---|---|
| role `-build` rodando em temp 0.5 (o cliente reenviava a temp do modelo base) → identificador corrompido (`proj-teste-001`→`999`, `opencodebridge`→`opencodedbidge`) | perfil do role vence a temp do request; `--request-temp-wins` restaura o antigo; `temperature_source` exposto em `/api/status`, `/api/translate` e no log `response` | `Program.cs` (`BuildChatRequest`, `ForwardResponses`, `Passthrough`) |
| loop de 10 tentativas no mesmo alvo com nomes mutados (anti-repeat textual não pega: cada iteração é texto novo) | `ToolLoopBreaker` (Core): lê `function_call`+resultado do próprio request, normaliza alvo (remove dígitos/separadores), 3 falhas = aviso no último resultado, 6 = curto-circuito sem chamar o modelo (`--breaker-soft/--breaker-hard`) | `Core/ToolLoopBreaker.cs` |
| glob com alternância (`a\|b`), heredoc, `\n` literal, `cd` em nome não criado, build simulado, `sudo` em PROD, `read` em `.db` | regras novas em `HarnessTails.DefaultTools` (caminho absoluto copiado de `ls`, probe obrigatório antes do retry, glob sem `\|`, sem heredoc, sem simulação, sem `sudo`, `git init` antes do commit) | `Core/HarnessTails.cs` |
| `\n` de 2 caracteres no comando shell → "here-document delimitado pelo fim do arquivo" | `FixLiteralEscapes`: vira quebra real quando o comando não tem nenhuma quebra; independe de `ToolStrategy` | `Program.cs` |
| caudas inertes no caminho opencode (`--harness-tails 0`) | `--tail-scope managed\|all\|off` (default `managed`); `all` injeta as caudas nos DOIS caminhos; `/api/harness` reporta `tails_managed`, `tails_opencode` e o efetivo | `Program.cs` + `appsettings.json` |

### Configuração em produção
`Rochas.OpenCodeBridge/appsettings.json` (versionado, copiado para o output) carrega
`tailScope: all`, `profileTempWins: true`, `breakerSoft: 3`, `breakerHard: 6` e os seis
perfis. As caudas de texto moram só em `HarnessTails.cs` — não sobrescrever
`harnessBehaviorTail`/`harnessToolsTail` no appsettings (o parágrafo que existia só no PROD
foi trazido para o código).

### Testes
- **`--dual`**: `E-dual-tools` roda a matriz (write/read/edit, glob+grep, ciclo git, probe
  obrigatório, script com erro de sintaxe sem simulação, SQLite via python3) em dois
  transportes — `pure` (HTTP `/v1/responses` + `/api/tools/execute`) e `opencode`
  (`opencode run --standalone`) — com sufixo `-pure`/`-opencode` e checagem de ARTEFATO em
  disco, não do texto do modelo.
- **`--standalone-provider openai|llama`** escolhe o provider do opencode (Q4 :4124 x Q3 :4125).
- **Unit novos**: `U-breaker-*` (streak, alvo mutado, trip, alvos distintos, normalização,
  marcadores pt/en), `U-toolargs-literal-newline`, `U-tails-path-simulation-rules`,
  `U-translate-profile-temp-wins`, `U-translate-breaker-verdicts`.

### Bateria
`./scripts/run-battery-dual.sh` (background: `nohup ./scripts/run-battery-dual.sh > /tmp/opencode/battery.out 2>&1 &`)
roda Q4 e Q3 em paralelo; por modelo: unit → tools-dual → tópicos → tools-full/responses/devpipe/
mitigação → belts → 1 pipe puro → opencode implícito (`--standalone`) → senior/enterprise/front/
scripter/orch/arquitetura. Logs e `SUMMARY.txt` em `/tmp/opencode/battery/<stamp>/`.

## 9. Bateria WAR v2 (3 sessões simultâneas, A/B de flags, retomada)

Objetivo: provar cada recurso do harness **na marra** (falha sem o arg → funciona com o arg) e
estressar a GPU com até 3 sessões simultâneas, com evidência por step.

```bash
# 3 sessões: Q4 (GPU1) + 2 slots do Q3 (GPU2)
./scripts/war/run-war.sh --fresh
# somente Q4 (uma sessão, sem lanes Q3; pula o teste explícito da ponte CPU)
./scripts/war/run-war.sh --q4 --fresh
# somente Q3 (duas sessões no llama :4110)
./scripts/war/run-war.sh --q3 --fresh
# retomar de onde parou (default) / inspecionar plano e concluídos
./scripts/war/run-war.sh
./scripts/war/run-war.sh --list
```

- **Lanes e bridges**: `q4` → `:4134`→llama `:4100`; `q3a` → `:4135`→`:4110`; `q3b` →
  `:4136`→`:4110` (slots `-np 2`). Cada lane tem bridge e log próprios, então o delta de log
  por step é limpo mesmo com as 3 rodando juntas. `WAR_CPU_BRIDGE` aponta a ponte CPU que o
  teste deve usar (Q3 da lane ou a prod `:4125` no modo Q4).
- **Fila aleatória reproduzível**: `WAR_SEED=42`. O plano fica em `<out>/plan.tsv`.
- **Checkpoint**: `<out>/state/<lane>/<teste>.done`; retomar pula o que já passou (inclusive
  após horas de GPU). `--fresh` zera. Mudar de lanes exige `--fresh`/outro `WAR_OUT`
  (`<out>/lanes.mode` trava o modo).
- **Evidência por step** em `<out>/steps/<lane>-<teste>.*`: log do teste, delta do log da
  bridge (harness/sonda/breaker/coerce/tool-ask/context-guard), `/api/metrics` e
  `/api/harness` antes/depois, `llamacpp:*` do `/metrics` antes/depois e `nvidia-smi`
  antes/depois. Amostrador contínuo em `<out>/gpu-samples.csv`; parciais em `summary.tsv`,
  `summary.txt` e `status.json` (atualizados a cada step).
- **Preflight A/B (stub, sem GPU)**: `E-flag-context-guard`, `E-flag-anti-repeat`,
  `E-flag-coerce-narrated`, `E-flag-tail-scope`. Cada um roda o cenário negativo e o positivo
  contra um upstream stub determinístico; falha se o arg não for o responsável pela diferença.
- **Via dupla**: `E-dual-tools` roda com `--dual` (pure + opencode); `E-standalone-*` com
  `--standalone`.

### Frontpipe sem webdriver

`E-front-*` avalia a skill de criação/edição **direto nos artefatos** (Razor `.cshtml`, JS/TSX e
CSS) por padrão: a sonda estática parseia o CSS e computa os mesmos valores do
`getComputedStyle` (`#titulo`, `#quadro`, `rect_w/rect_h` com `box-sizing`), confere links/DOM e
estrutura TSX (`createRoot`). O caminho de navegador fica atrás de `--front-webdriver`
(Playwright via Web.Test `--front-measure`), com URL configurável por `FRONT_WEBDRIVER_URL` e
`HEADED=1` para o modo visível — gancho pronto para a Web UI `:4130` quando o parâmetro headed
geral for criado.

## 10. Level 1 — ordem das trilhas (`scripts/war/run-levels.sh`)

Level 1 é a subida antes da produção: **units → devpipe → belts Build → belts Orch**, cada
bloco em dois passes (`--mode pure` e `--mode opencode`), fechando com a suíte web
(integração + tópicos). As duas trilhas de belts já existiam como testes separados
(`E-belts-build-direct` = role build implementa direto; `E-belts-direct` = role orch
segmenta → Scripter → Exec); o runner só passou a executá-las **nesta ordem**.

```bash
# bridge própria (para testar o build do repo, não o binário de produção)
cd Rochas.OpenCodeBridge/bin/Release/net9.0
dotnet Rochas.OpenCodeBridge.dll --port 4144 --upstream http://127.0.0.1:4100 \
  --model Gemma4-E4B-Q4 --thinking events --temperature 0.5 --max-tokens 16384 \
  --context-guard 1 --context-tokens 131072 --api-key guest@123 \
  --passthrough-responses true --main-harness 0 --harness-tails 0 \
  --anti-repeat 1 --anti-repeat-retry 1 --anti-repeat-window 512 \
  --log /tmp/bridge-q4-level1.log &

LEVEL_BRIDGE=http://127.0.0.1:4144 LEVEL_BLOG=/tmp/bridge-q4-level1.log \
  ./scripts/war/run-levels.sh --level 1
```

Artefatos: `/tmp/opencode/levels/L1-<stamp>/{summary.tsv,steps/,state/}`; retomada pelo
`STATE` do mesmo stamp (`LEVEL_STAMP=<stamp>`). Sem ordem fixa de faixas: `--belts azul,verde`
filtra.

### Level 2 — bateria de tools + científico

`LEVEL_BRIDGE=http://127.0.0.1:4144 ./scripts/war/run-levels.sh --level 2`

Roda, em `--mode pure` e `--mode opencode`: `E-tools-full`, `E-dual-tools`,
`E-mission-tools`, `E-responses-tools`, `E-science-gpu` (Q4, bridge do level) e
`E-science-cpu` (linha Q3, `WAR_CPU_BRIDGE` ou `:4125`), fechando com a suíte web.
`LEVEL_CPU=0` pula o científico da linha Q3 (útil quando a `:4125` está em uso por
sessão real). Artefatos no mesmo layout do Level 1.

### Level 3 — pacote completo de belts (azul → preta)

`LEVEL_BRIDGE=http://127.0.0.1:4146 ./scripts/war/run-levels.sh --level 3`

Duas trilhas, nas cinco faixas (`azul,verde,roxa,marrom,preta`), em `--mode pure` e
`--mode opencode`: **trilha Build** (`E-belts-build-direct` — role build implementa
direto) e **trilha Orch** (`E-belts-direct` — orch segmenta → Scripter → Exec).
Exige **harness ligado** na bridge; o runner consulta `/api/harness` e loga aviso se
`main_harness` não estiver `true`:

```bash
cd Rochas.OpenCodeBridge/bin/Release/net9.0
dotnet Rochas.OpenCodeBridge.dll --port 4146 --upstream http://127.0.0.1:4100 \
  --model Gemma4-E4B-Q4 --thinking events --temperature 0.5 --max-tokens 16384 \
  --context-guard 1 --context-tokens 131072 --api-key guest@123 \
  --passthrough-responses true --main-harness 1 --harness-tails 1 \
  --anti-repeat 1 --anti-repeat-retry 1 --anti-repeat-window 512 \
  --log /tmp/bridge-q4-harness.log &
```
