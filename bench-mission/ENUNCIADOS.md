# ENUNCIADOS da missao (alternancia TEXTO/TOOLS)

Ferramentas iguais p/ ambos (read/write/edit/glob/grep/shell sandbox na pasta do agente). Modelos: qwen3.5-9b-build e qwen3-vl-4b-build (sufixados = tubo limpo).

```python
STAGES = [
    ("TEXTO", "Responda apenas em TEXTO, sem tool calls: apresente o plano do programa cientifico (integracao de Simpson de sin(x) em [0,pi]; media e desvio-padrao de uma amostra; regressao linear por minimos quadrados) a ser implementado em C# console e F# console."),
    ("TOOLS", "Use tools EXPLICITAMENTE (somente tool calls, sem prosa fora delas): crie o projeto C# com `dotnet new console -n scics` e grave o Program.cs implementando integral de Simpson de sin(x) em [0,pi], media/desvio de {2,4,4,4,5,5,7,9} e regressao linear de [(1,2),(2,4),(3,5),(4,4),(5,6)]. Imprima os 3 resultados."),
    ("TOOLS", "Use tools EXPLICITAMENTE: compile e rode o C# (`dotnet run --project scics`), corrija erros ate rodar e mostrar os 3 numeros."),
    ("TOOLS", "Use tools EXPLICITAMENTE: crie o projeto F# com `dotnet new console -lang F# -n scifs` e porte o mesmo programa (Program.fs)."),
    ("TOOLS", "Use tools EXPLICITAMENTE: compile e rode o F# (`dotnet run --project scifs`), corrija ate rodar."),
    ("TOOLS", "Use tools EXPLICITAMENTE: versione com git (git init, configure user.name/user.email LOCAIS do repo, add, commit)."),
    ("TOOLS", "Use tools EXPLICITAMENTE: gere o zip da pasta (`zip -r entrega.zip scics scifs`) e liste o zip."),
    ("TEXTO", "Responda apenas em TEXTO, sem tool calls: relatorio final do que foi construido, os 3 numeros obtidos e o estado do git/zip."),
]
```


<!-- INVARIANTE-TUNEL-LLAMA:v1 -->
## Invariante — túnel de stream do llama é exclusivo da bridge/core

**Somente a bridge (`Rochas.OpenCodeBridge`, camada Core) fala com o llama-server.** Testes (`Rochas.OpenCodeBridge.Test`), `mini-bridge-python`, Web UI, Grafana e scripts são clientes/observadores: não injetam, não reescrevem, não "cutucam" e não reordenam mensagens dentro do túnel.

- Cliente envia a **requisição** (`/v1/chat/completions`, `/v1/responses`); o stream do modelo volta repassado **sem edição** pelo core.
- Proibido no cliente: nudge/continuation prompt, injeção de "próximo passo", reescrita de turno, reordenação ou truncamento de histórico.
- Harness e coerção (`main_harness`, tails, `coerce_text_tools`, `coerce_narrated`, `degenerate_retry`) vivem **só** na bridge/core, atrás de flag.
- Comportamento de teste se ajusta por **enunciado** ou **flag da bridge** — nunca editando o conteúdo do túnel no cliente.
- Nudges de continuação do `TestContext.ToolAgent` foram **removidos em 2026-11-07** (`TCTX_NUDGES` não existe mais): turno sem tool call encerra o loop e o veredito sai do artefato.
- Fonte única: `docs/TUNEL-LLAMA.md`.

