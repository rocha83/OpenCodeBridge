#!/usr/bin/env python3
"""Monta o system prompt do tunel a partir de harness/ (parts + examples).

Fonte unica da sequencia: harness/manifest.json.
O teto protege o prefixo fixo (system + catalogo de tools) e NUNCA corta `parts`:
se as parts sozinhas nao couberem, falha alto (BudgetError).

Uso:
  python3 harness_loader.py --report
  python3 harness_loader.py --write /tmp/sys.md

"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_DIR = HERE / "harness"


class BudgetError(RuntimeError):
    """O harness nao cabe no teto configurado."""


def _env_int(name, default):
    try:
        return int(str(os.environ.get(name, "")).strip() or default)
    except ValueError:
        return default


def _env_float(name, default):
    try:
        return float(str(os.environ.get(name, "")).strip() or default)
    except ValueError:
        return default


def load_manifest(harness_dir=None):
    hdir = Path(harness_dir or os.environ.get("HARNESS_DIR") or DEFAULT_DIR).resolve()
    with open(hdir / "manifest.json", "r", encoding="utf-8") as fh:
        man = json.load(fh)
    bud = dict(man.get("budget") or {})
    bud["max_total_tokens"] = _env_int("HARNESS_MAX_TOKENS", bud.get("max_total_tokens", 12000))
    bud["tools_reserve_tokens"] = _env_int("HARNESS_TOOLS_RESERVE", bud.get("tools_reserve_tokens", 3500))
    bud["chars_per_token"] = _env_float("HARNESS_CHARS_PER_TOKEN", bud.get("chars_per_token", 3.5))
    bud["on_overflow"] = str(os.environ.get("HARNESS_ON_OVERFLOW") or bud.get("on_overflow", "drop_examples"))
    return hdir, man, bud


def _read(hdir, rel):
    rel = str(rel).replace("\\", "/")
    if ".." in Path(rel).parts:
        raise ValueError(f"caminho fora do harness: {rel}")
    path = hdir / rel
    if not path.is_file():
        raise FileNotFoundError(f"harness sem arquivo: {rel}")
    return path.read_text(encoding="utf-8").strip()


def _item(group, rel, text, cpt):
    return {"group": group, "path": rel, "chars": len(text),
            "tokens": round(len(text) / cpt, 1), "text": text}


def assemble(stack="base", harness_dir=None):
    """Devolve {text, chars, tokens, limit, files[], dropped[]} respeitando o teto."""
    hdir, man, bud = load_manifest(harness_dir)
    cpt = bud["chars_per_token"]
    limit = bud["max_total_tokens"] - bud["tools_reserve_tokens"]

    parts = [_item("part", rel, _read(hdir, rel), cpt) for rel in man.get("parts") or []]
    used = sum(i["chars"] for i in parts) / cpt
    if used > limit:
        raise BudgetError(
            f"parts sozinhas ocupam {used:.0f} tok > orcamento de system {limit} tok; "
            "reduza parts ou suba HARNESS_MAX_TOKENS")

    examples = [_item("example", rel, _read(hdir, rel), cpt)
                for rel in (man.get("examples") or {}).get(stack) or []]
    def size():
        return sum(i["chars"] for i in parts + examples) / cpt

    dropped = []
    if size() > limit:
        if bud["on_overflow"] == "fail":
            raise BudgetError(f"{size():.0f} tok > orcamento de system {limit} tok")
        while size() > limit and examples:
            dropped.append(examples.pop()["path"])
        if size() > limit:
            raise BudgetError(
                f"parts ocupam {size():.0f} tok > orcamento de system {limit} tok")

    files = parts + examples
    text = "\n\n".join(i["text"] for i in files) + "\n"
    return {
        "text": text,
        "chars": len(text),
        "tokens": round(len(text) / cpt, 1),
        "limit": limit,
        "budget": bud,
        "stack": stack,
        "files": [{k: v for k, v in i.items() if k != "text"} for i in files],
        "dropped": dropped,
    }


def report(res):
    out = [f"HARNESS  stack={res['stack']}"]
    out.append(f"  {'grupo':8s} {'arquivo':34s} {'chars':>6s} {'tok':>6s}")
    for i in res["files"]:
        out.append(f"  {i['group']:8s} {i['path']:34s} {i['chars']:6d} {i['tokens']:6.0f}")
    bud = res["budget"]
    tools_tok = bud["tools_reserve_tokens"]
    total = res["tokens"] + tools_tok
    folga = bud["max_total_tokens"] - total
    out.append("  " + "-" * 58)
    out.append(f"  {'system':8s} {'':34s} {res['chars']:6d} {res['tokens']:6.0f}")
    out.append(f"  {'tools':8s} {'(reserva)':34s} {'':>6s} {tools_tok:6.0f}")
    out.append(f"  TOTAL {total:.0f} tok | teto {bud['max_total_tokens']} | "
               f"folga {folga:.0f} | {'OK' if folga >= 0 else 'ESTOURO'}")
    if res["dropped"]:
        out.append(f"  cortados por teto: {', '.join(res['dropped'])}")
    return "\n".join(out)


def main(argv=None):
    ap = argparse.ArgumentParser(description="monta o system do tunel a partir de harness/")
    ap.add_argument("--stack", default="base")
    ap.add_argument("--report", action="store_true")
    ap.add_argument("--write", metavar="ARQ")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args(argv)
    try:
        res = assemble(stack=args.stack)
    except (BudgetError, FileNotFoundError, ValueError) as exc:
        print(f"HARNESS FAIL: {exc}")
        return 2
    if args.write:
        Path(args.write).write_text(res["text"], encoding="utf-8")
    if args.json:
        print(json.dumps({k: v for k, v in res.items() if k != "text"}, ensure_ascii=False, indent=2))
    if args.report or not (args.write or args.json):
        print(report(res))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
