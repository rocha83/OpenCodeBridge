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
