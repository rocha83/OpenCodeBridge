# DOC DE EVOLUÇÃO — Supervisor de testes auto-instância (12B) + filho E2B

> Objetivo: o **próprio modelo** gerencia a pipe full de testes E2E sobre
> **instâncias de si mesmo** (12B) e sobre o **filho E2B**, via subagentes,
> com **segmentação** de tarefas compatível com a capacidade do modelo.
> Status: **plano + aceites definidos**; execução auto-guiada em fases.

## 1. Premissas medidas (nesta base)

- **12B (GPU)** ~**24.3 tok/s** geração, ctx 131072; **E2B (CPU)** ~**56.2 tok/s**, ctx 65536 (2 slots).
- Testes com tools **não** funcionam bem com **thinking on** → a suíte roda no **role build** (thinking off).
- O 12B é **menos capaz** que modelos free online: falha em F# idiomático (FS0039/FS0193/FS0001)
  até o enunciado virar **prescritivo** (loop mutável explícito). Regra: **segmentar + prescrever**.
- Aceite deve ser **verificável por artefato** (arquivo/log/número), nunca "parece bom".

## 2. Composição do supervisor

```
Supervisor (orch 12B)  ── lê o catálogo de testes (E-*) e GERA a fila de micro-enunciados
   ├── Runner build (12B build, GPU)  ── executa micro-tarefas de código multi-arquivo
   ├── Runner exec (E2B exec, CPU)    ── executa/normaliza/valida scripts e micro-tarefas simples
   └── Aceite (test suite)            ── reprova/passa por artefato; retroalimenta o supervisor
```

- **Micro-enunciado** (formato fixo): `OBJETIVO` (1 frase) + `ESCOPO` (arquivos exatos) +
  `PROIBIDO` (o que não tocar) + `ACEITE` (comando exato de verificação).
- **Delegação**: tool nativa `task` (subagente), `model` = `openai/gemma-4-12b-build` (filho GPU)
  ou `llama/gemma-4-e2b-exec` (filho CPU). Um micro por chamada.
- **Supervisor não improvisa**: o plano é o catálogo `E-*` da suíte; ele só **ordena, segmenta e re-despacha**.
- **Feedback curto**: ao subagente só volta o **erro** (trecho), não o log inteiro (poupa ctx).

## 3. Segmentação das belts (azul → preta)

As faixas (`Web.Test --ui --belts`) são **pesadas** (horas se feitas de uma vez). Segmentar:

| faixa | escopo | alvo | micro-tarefas | aceite (artefato) |
|---|---|---|---|---|
| **azul** | aquecimento (1 tool) | E2B exec | 1 | saída de tool no texto |
| **verde** | domínio + SQL (CRUD) | 12B build | 2–3 | DDL + entidades compilam |
| **roxa** | + repositórios/serviços | 12B build | 3–4 | `dotnet build` verde |
| **marrom** | + API + React + Docker | 12B build | 5–6 | build + `compose config` |
| **preta** | full stack + CI | 12B build + E2B exec | 6+ | build + teste do ACEITE |

Cada faixa vira **N micro-enunciados independentes** (cada um cabe no ctx do alvo).
O supervisor despacha micro a micro, **valida o aceite** e só então avança.

## 4. Aceites desta evolução (o que precisa passar)

1. **Plano em texto (agora)**: o 12B elabora o plano de execução das faixas **azul→preta**
   (fases, subagentes, aceites) — sem executar. *Alvo: desbloquear a auto-gestão.*
2. **devpipe em modo opencode**: cadeia real `write → glob → read` com marcador
   (`E-standalone-devpipe`) — já implementado na suíte.
3. **Ciência com tool** e **missão C#** por opencode (`E-standalone-science-tools`,
   `E-standalone-mission-tools`) — observa o comportamento do 12B com tools.
4. **Auto-instância (fase futura)**: supervisor 12B gera a fila a partir do catálogo `E-*`,
   delega aos filhos, agrega e **re-executa o ACEITE** de cada micro.

## 5. Fluxo auto-guiado (fase alvo)

```
1. Supervisor lê o catálogo E-* e emite a fila de micro-enunciados (texto).
2. Para cada micro:
     - escolhe alvo (12B build | E2B exec) pela complexidade
     - delega via `task` (subagente) com o micro-enunciado
     - recebe o resultado + evidência
     - re-executa o ACEITE (grep/build/run) — passa ou devolve o erro ao subagente (≤2 tentativas)
3. Agrega num relatório: PASS/FAIL por micro, tempos, tok/s.
4. Se um micro estoura o ctx/ambiguidade: o supervisor **refatia** em dois menores.
```

## 6. Riscos e mitigação

| risco | mitigação |
|---|---|
| F#/tipos fortes quebram o 12B | enunciados **prescritivos** (loop mutável, tipos explícitos) |
| E2B ctx 64k / 2 slots | fila **serial**; 1 micro por vez no E2B |
| 12B lento (~24 t/s) | micro-enunciados curtos; limite de turnos; timeout |
| aceite "parece bom" | aceite por **artefato** (arquivo/log/número) |
| opencode background service | `opencode run --standalone` (servidor privado) |

## 7. Como reproduzir (hoje)

```bash
# aceites 2–3 (opencode real, criticos de tools)
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- \
  --standalone --only E-standalone --api-key guest@123 --bridge http://127.0.0.1:4124
# E2B filho
dotnet run --project Rochas.OpenCodeBridge.Test -c Release --no-build -- \
  --standalone --standalone-agent exec --only E-standalone-oi --bridge http://127.0.0.1:4125
```
