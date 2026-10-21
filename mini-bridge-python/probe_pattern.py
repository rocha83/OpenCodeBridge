#!/usr/bin/env python3
"""Sonda: prova (ou nao) a rejeicao de `pattern` por um endpoint OpenAI-like.

Uso:
  python3 probe_pattern.py --url http://127.0.0.1:4100/v1/chat/completions --pattern '*.cs'
  python3 probe_pattern.py --url http://127.0.0.1:4300/v1/chat/completions --pattern '*.cs'
  python3 probe_pattern.py --anchored            # ^[a-z]+$ (controle)
"""
from __future__ import annotations

import argparse
import json
import urllib.error
import urllib.request


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://127.0.0.1:4100/v1/chat/completions")
    ap.add_argument("--key", default="guest@123")
    ap.add_argument("--model", default="gemma-4-12b")
    ap.add_argument("--pattern", default="*.cs")
    ap.add_argument("--anchored", action="store_true", help="usa ^[a-z]+$ (controle negativo)")
    ap.add_argument("--required", action="store_true", help="tool_choice=required (forca a gramatica)")
    ap.add_argument("--timeout", type=float, default=180.0)
    args = ap.parse_args()

    pattern = "^[a-z]+$" if args.anchored else args.pattern
    payload = {
        "model": args.model,
        "messages": [{"role": "user", "content": "chame a tool glob com pattern=*.cs"}],
        "max_tokens": 40,
        "tools": [
            {
                "type": "function",
                "function": {
                    "name": "glob",
                    "description": "lista arquivos por padrao",
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "pattern": {"type": "string", "description": "glob"},
                            "sessionID": {"type": "string", "pattern": pattern},
                        },
                        "required": ["pattern"],
                    },
                },
            }
        ],
    }
    if args.required:
        payload["tool_choice"] = "required"

    req = urllib.request.Request(
        args.url,
        data=json.dumps(payload).encode(),
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {args.key}",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=args.timeout) as resp:
            body = resp.read().decode("utf-8", "replace")
            print(f"HTTP={resp.status} pattern={pattern!r} bytes={len(body)}")
            print(body[:300])
            return 0
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", "replace")
        print(f"HTTP={exc.code} pattern={pattern!r}")
        print(body[:500])
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
