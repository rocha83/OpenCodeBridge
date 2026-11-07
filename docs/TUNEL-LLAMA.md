# Túnel de stream do llama — invariante de arquitetura

**Status: obrigatório. Vale para todo o repositório (código, testes, Web UI, scripts, docs).**

## Regra

Somente a **bridge** (`Rochas.OpenCodeBridge`, camada `Core`) fala com o **túnel de stream do llama-server** (`:4100`).

Tudo o mais é **cliente** ou **observador**:

| Componente | Papel | Pode tocar no túnel? |
|---|---|---|
| `Rochas.OpenCodeBridge` (Core/Program) | dono do túnel | **sim** (único autorizado) |
| `Rochas.OpenCodeBridge.Test` (WAR/E2E) | cliente HTTP + avaliador | **não** |
| `mini-bridge-python` (belts/lab) | cliente/relay de laboratório | **não** |
| Web UI / Grafana / scripts / dashboards | observadores | **não** |
| `llama-server` | servidor do modelo | recebe só o que o core envia |

"Falar com o túnel" = compor/alterar o que chega ao modelo: mensagens, ordem, papéis, `tool_choice`, nudge, coerção, truncamento de histórico, reescrita de turno.

## O que o cliente faz e o que não faz

- **Faz**: monta a requisição (`/v1/chat/completions`, `/v1/responses`), envia o enunciado, recebe a resposta, executa as tools localmente e avalia o artefato.
- **Não faz**: injetar mensagem `user`/`system` extra no meio do loop, "cutucar" o modelo com *continuation prompt*, reordenar/reescrever turnos, truncar histórico, decidir coerção.

O stream que volta do llama é repassado pelo core **sem edição**; qualquer transformação é responsabilidade exclusiva do core (harness/coerção).

## Onde o harness pode viver

Só na bridge/core, e sempre atrás de flag explícita:

- `main_harness`
- `harness_tails` / `behavior_tail` / `tools_tail_flag` / `tail_scope`
- `coerce_text_tools` / `coerce_narrated`
- `anti_repeat`
- `degenerate_retry`

Teste e Web UI **não** têm versão própria desses mecanismos. Se um teste precisa de comportamento diferente, muda-se o **enunciado** ou a **flag da bridge** — nunca o conteúdo do túnel no cliente.

## Histórico da violação (removida em 2026-11-07)

O `TestContext.ToolAgent` injetava mensagens de continuação no túnel quando um turno terminava sem tool call (`TCTX_NUDGES`, default 4), em três variantes:

1. *git-aware* — "O turno parou sem tool call depois de `git commit`. Próximo passo do fluxo: ..."
2. turno vazio — "Você não emitiu nenhuma tool call. Execute a tarefa agora com as tools..."
3. turno só prosa — "A tarefa ainda não está concluída. Continue de onde parou..."

Isso aparecia no Grafana como mensagem extra no request e mascarava o comportamento real do modelo (o cliente insistia até 4 vezes). **Removido**: turno sem tool call agora encerra o loop; o veredito sai do artefato (FAIL honesto), não de insistência do harness de teste. A variável `TCTX_NUDGES` deixou de existir.

## Como verificar

1. O que o cliente envia aparece **íntegro** no `prompt`/`messages` do request registrado pela bridge (`/api/activity`, journey).
2. Nenhuma mensagem extra entre o enunciado e o resultado das tools no transcript.
3. `grep -rn "nudge\|TCTX_NUDGES\|Continue de onde parou" --include=*.cs .` → só docs/comentários históricos.
4. O teste roda com as mesmas flags de harness da bridge (ou menos), nunca com harness próprio.

## Consequência prática

- Teste não "salva" o modelo: se ele para em prosa, o teste reprova.
- O placar do WAR mede o modelo + core, não o modelo + insistência do cliente.
- Web UI é read-only sobre o túnel: só lê métricas e mostra o que o core registrou.
