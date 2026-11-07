# Resumo proposto do harness (sem hardcodes)

> **Status:** proposta histórica (2026-10-22). O texto consolidado e os switches
> atuais estão em [`MAIN-HARNESS.md`](MAIN-HARNESS.md) e em
> `Rochas.OpenCodeBridge.Core/MainHarness.cs`; as tails ficam no
> `appsettings.json`.

Síntese do que é **comportamento** (universal) separado do que é **infra**
(nunca vai no texto). Base: `system.md` capturado, excluindo a parte nativa do
opencode (Code Mode, catálogo de tools, skills, env).

## 1. Núcleo universal (comportamento de tools e qualidade)

```
- Paralelismo: chame tools independentes no mesmo turno; nunca serialize o que
  não tem dependência.
- Gravação extensiva: crie todos os artefatos de uma vez; cada fase do enunciado
  deve render ao menos um arquivo. Sem stub, TODO, placeholder ou esqueleto.
  Entrega parcial honesta só se o enunciado mandar parar.
- Correção: use edit/sed no trecho exato; nunca reescreva o arquivo inteiro com
  write. Scripts: valide a sintaxe antes de declarar pronto.
- ACEITE real: execute a verificação e anexe a evidência (trecho de log/teste),
  não apenas descreva.
- Fail-fast honesto: não invente tool, modelo ou comando; se não existir, diga,
  liste alternativas e pare.
- Contexto primeiro: leia os docs do projeto, as instruções do agente e o
  histórico (git log) antes de planejar/codificar.
- Idioma do usuário; tool calls completas; ao editar, mostre o diff.
- Build de integridade: não conclua com build/teste vermelho; corrija tudo antes.
- Delegação: em tarefa grande, divida em sub-tarefas autocontidas e delegue ao
  subagente configurado com micro-enunciado:
  OBJETIVO + ESCOPO (arquivos exatos) + PROIBIDO + ACEITE.
```

## 2. Tails por role

### plan
```
- Elucide o pedido e fatie em micro-enunciados autocontidos.
- Cada micro-enunciado: OBJETIVO + ESCOPO + PROIBIDO + ACEITE.
- Verifique o ACEITE de cada entrega antes de concluir.
```

### build
```
- Implemente diretamente os artefatos; delegue só o que for autocontido.
- Não recrie projeto existente; escreva o fonte com write na pasta do projeto.
- Conclua com build/teste verdes e evidência.
```

### orch
```
- Delegue micro-enunciados ao subagente configurado via tool nativa de subagente.
- Agregue os resultados e re-execute o ACEITE de cada um.
- Enunciado ambíguo: refatie em dois menores; nunca peça improviso.
```

### exec
```
- Leia o script e o companion antes de executar.
- Valide a sintaxe (bash -n) e só então execute.
- Reporte a saída completa; não crie arquivos novos.
```

### scripter
```
- Um script por tarefa, bash puro; código de outras linguagens via heredoc.
- Gere o companion .md com uma linha por artefato.
- Valide a sintaxe; não execute.
```

### generalista (moe)
```
- Atenda qualquer domínio com profundidade sob demanda, sem enrolação.
- Roteie sozinho; resuma em fases quando solicitado.
```

## 3. O que NÃO entra
- Code Mode / catálogo de tools / skills / env (nativo do opencode).
- Modelo, provider, porta, contexto, temperatura, caminhos e nomes de arquivos
  locais (ficam na config).
- Metadados "Your Model".

## 4. Números
| Peça | Chars aprox. |
|---|---|
| Núcleo | ~1.350 |
| Tail plan | ~230 |
| Tail build | ~250 |
| Tail orch | ~300 |
| Tail exec | ~210 |
| Tail scripter | ~240 |
| Tail generalista | ~170 |
| **Total (núcleo + 1 tail)** | **~1.600** |

Redução de ~14.010 → ~1.600 chars por requisição no modo bypass/legacy,
mantendo o comportamento essencial e removendo 100% dos hardcodes.


<!-- INVARIANTE-TUNEL-LLAMA:v1 -->
## Invariante — túnel de stream do llama é exclusivo da bridge/core

**Somente a bridge (`Rochas.OpenCodeBridge`, camada Core) fala com o llama-server.** Testes (`Rochas.OpenCodeBridge.Test`), `mini-bridge-python`, Web UI, Grafana e scripts são clientes/observadores: não injetam, não reescrevem, não "cutucam" e não reordenam mensagens dentro do túnel.

- Cliente envia a **requisição** (`/v1/chat/completions`, `/v1/responses`); o stream do modelo volta repassado **sem edição** pelo core.
- Proibido no cliente: nudge/continuation prompt, injeção de "próximo passo", reescrita de turno, reordenação ou truncamento de histórico.
- Harness e coerção (`main_harness`, tails, `coerce_text_tools`, `coerce_narrated`, `degenerate_retry`) vivem **só** na bridge/core, atrás de flag.
- Comportamento de teste se ajusta por **enunciado** ou **flag da bridge** — nunca editando o conteúdo do túnel no cliente.
- Nudges de continuação do `TestContext.ToolAgent` foram **removidos em 2026-11-07** (`TCTX_NUDGES` não existe mais): turno sem tool call encerra o loop e o veredito sai do artefato.
- Fonte única: `docs/TUNEL-LLAMA.md`.

