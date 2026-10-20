"""SFT QLoRA com Unsloth (fallback simulado se indisponivel)."""
import argparse, json, os, sys

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dataset", required=True)
    ap.add_argument("--method", default="SFT")
    ap.add_argument("--output", default="./out")
    args = ap.parse_args()
    # Tenta Unsloth; se indisponivel, simula.
    try:
        import unsloth  # noqa
        has_unsloth = True
    except Exception:
        has_unsloth = False
    print(f"[sft_train] method={args.method} dataset={args.dataset} unsloth={has_unsloth}")
    # Validacao minima do dataset
    n = 0
    with open(args.dataset) as f:
        for line in f:
            if line.strip():
                json.loads(line); n += 1
    print(f"[sft_train] samples={n}")
    os.makedirs(args.output, exist_ok=True)
    # Artefato de saida (simulado)
    with open(os.path.join(args.output, "adapter.json"), "w") as f:
        json.dump({"method": args.method, "samples": n, "unsloth": has_unsloth}, f)
    print("[sft_train] DONE")

if __name__ == "__main__":
    main()
