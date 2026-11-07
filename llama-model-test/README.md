# Llama 3.1 8B — teste local

Artefatos para baixar, servir e configurar o modelo Llama 3.1 8B Instruct (GGUF Q4_K_M) nesta máquina.

## Conteúdo
- `download-llama31-8b.sh` — baixa o GGUF para `/media/mint/2a295c1a-afad-44fb-84dc-053406a7bbfb/opt/llama`.
- `llama-llama31-8b.service` — unit systemd que serve `llama-server` na porta 4120 (CPU, ctx 8192).
- `install-service.sh` — instala/inicia a unit e valida a porta.
- `update-opencode-config.py` — adiciona o provider `llama31` no `~/.config/opencode/opencode.json` apontando para `http://127.0.0.1:4120/v1`.
- `test-llama31.sh` — valida `/v1/models` e um chat completion.

## Uso
1. `bash download-llama31-8b.sh`
2. `bash install-service.sh`
3. `python3 update-opencode-config.py` (já executado)
4. `bash test-llama31.sh`


<!-- INVARIANTE-TUNEL-LLAMA:v1 -->
## Invariante — túnel de stream do llama é exclusivo da bridge/core

**Somente a bridge (`Rochas.OpenCodeBridge`, camada Core) fala com o llama-server.** Testes (`Rochas.OpenCodeBridge.Test`), `mini-bridge-python`, Web UI, Grafana e scripts são clientes/observadores: não injetam, não reescrevem, não "cutucam" e não reordenam mensagens dentro do túnel.

- Cliente envia a **requisição** (`/v1/chat/completions`, `/v1/responses`); o stream do modelo volta repassado **sem edição** pelo core.
- Proibido no cliente: nudge/continuation prompt, injeção de "próximo passo", reescrita de turno, reordenação ou truncamento de histórico.
- Harness e coerção (`main_harness`, tails, `coerce_text_tools`, `coerce_narrated`, `degenerate_retry`) vivem **só** na bridge/core, atrás de flag.
- Comportamento de teste se ajusta por **enunciado** ou **flag da bridge** — nunca editando o conteúdo do túnel no cliente.
- Nudges de continuação do `TestContext.ToolAgent` foram **removidos em 2026-11-07** (`TCTX_NUDGES` não existe mais): turno sem tool call encerra o loop e o veredito sai do artefato.
- Fonte única: `docs/TUNEL-LLAMA.md`.

