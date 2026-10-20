"""Valida/normaliza datasets para o formato de chat do Gemma (SFT/DPO/RM)."""
import json, sys, os

def validate_sft(path):
    ok = True
    with open(path) as f:
        for i, line in enumerate(f):
            line = line.strip()
            if not line: continue
            obj = json.loads(line)
            if "messages" not in obj:
                print(f"[SFT] linha {i}: falta 'messages'"); ok = False; continue
            for m in obj["messages"]:
                if "role" not in m or "content" not in m:
                    print(f"[SFT] linha {i}: mensagem invalida"); ok = False
    return ok

def validate_dpo(path):
    ok = True
    with open(path) as f:
        for i, line in enumerate(f):
            line = line.strip()
            if not line: continue
            obj = json.loads(line)
            for k in ("prompt", "chosen", "rejected"):
                if k not in obj:
                    print(f"[DPO] linha {i}: falta '{k}'"); ok = False
    return ok

def main():
    if len(sys.argv) < 3:
        print("usage: dataset_prep.py <sft|dpo|rm> <file.jsonl>"); sys.exit(2)
    mode, path = sys.argv[1], sys.argv[2]
    if mode == "sft":
        ok = validate_sft(path)
    elif mode in ("dpo", "rm"):
        ok = validate_dpo(path)
    else:
        print("mode invalido"); sys.exit(2)
    print("OK" if ok else "INVALID")
    sys.exit(0 if ok else 1)

if __name__ == "__main__":
    main()
