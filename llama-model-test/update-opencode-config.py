#!/usr/bin/env python3
# update-opencode-config.py
# Adiciona o provider "llama31" (Llama 3.1 8B) apontando para http://127.0.0.1:4120/v1
# no opencode.json do usuário. Idempotente.
import json, os, shutil, sys

CONFIG = os.path.expanduser("~/.config/opencode/opencode.json")
BACKUP = CONFIG + ".bak-llama31"

MODEL_ID = "Llama-3.1-8B-Instruct-Q4_K_M"

provider = {
    "package": "@ai-sdk/openai-compatible",
    "settings": {
        "baseURL": "http://127.0.0.1:4120/v1",
        "apiKey": "guest@123"
    },
    "options": {
        "timeout": 1800000,
        "headerTimeout": 900000,
        "chunkTimeout": 900000
    },
    "models": {
        MODEL_ID: {
            "name": "Llama 3.1 8B Instruct Q4_K_M (porta 4120)",
            "tool_call": True,
            "limit": {
                "context": 131072,
                "output": 8192
            },
            "temperature": 0.3
        }
    }
}

if not os.path.exists(CONFIG):
    print(f"ERRO: config não encontrada: {CONFIG}", file=sys.stderr)
    sys.exit(1)

with open(CONFIG, "r", encoding="utf-8") as f:
    data = json.load(f)

if "providers" not in data:
    data["providers"] = {}

if "llama31" in data["providers"]:
    print("[config] provider 'llama31' já existe; atualizando.")
else:
    print("[config] adicionando provider 'llama31'.")

data["providers"]["llama31"] = provider

# backup uma vez
if not os.path.exists(BACKUP):
    shutil.copy2(CONFIG, BACKUP)
    print(f"[config] backup em {BACKUP}")

with open(CONFIG, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2, ensure_ascii=False)
    f.write("\n")

print(f"[config] provider 'llama31' -> {MODEL_ID} em {CONFIG}")
