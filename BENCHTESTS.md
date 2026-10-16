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
