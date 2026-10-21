#!/usr/bin/env python3
"""Analisa os dumps do stream (SSE ou JSON) procurando repeticao e erro de tool.

- reconstroi `reasoning_content` e `content` do stream;
- mede repeticao: n-grama repetido, taxa de repeticao, maior bloco repetido,
  eco do prompt no thinking, e repeticoes de tool call (mesma assinatura);
- valida cada `tool_calls[].function.arguments` como JSON (truncado/invalido);
- lista tags de controle vazadas (<|channel>, </thought>, <channel|>, etc.).

Uso:
  python3 analyze_dumps.py [--dir dumps] [--json]
"""
from __future__ import annotations

import argparse
import json
import re
from collections import Counter
from pathlib import Path

CTRL_TAGS = re.compile(r"<\|?/?\s*(channel|thought|thinking|think|tool_call|start_of_turn|end_of_turn)\s*\|?>")
NGRAM = 4


def parse_stream(raw: str) -> dict:
    reasoning: list[str] = []
    content: list[str] = []
    tools: dict[int, dict] = {}
    finish: list[str] = []
    events = 0
    done = False
    for block in raw.split("\n\n"):
        for line in block.splitlines():
            if not line.startswith("data:"):
                continue
            payload = line[5:].strip()
            if payload == "[DONE]":
                done = True
                continue
            try:
                chunk = json.loads(payload)
            except ValueError:
                continue
            events += 1
            if chunk.get("error"):
                finish.append("ERROR:" + str(chunk["error"])[:120])
                continue
            for choice in chunk.get("choices") or []:
                delta = choice.get("delta") or {}
                if delta.get("reasoning_content"):
                    reasoning.append(delta["reasoning_content"])
                if delta.get("content"):
                    content.append(delta["content"])
                for call in delta.get("tool_calls") or []:
                    idx = call.get("index", 0)
                    slot = tools.setdefault(idx, {"name": "", "args": "", "id": ""})
                    fn = call.get("function") or {}
                    if fn.get("name"):
                        slot["name"] = fn["name"]
                    if fn.get("arguments"):
                        slot["args"] += fn["arguments"]
                    if call.get("id"):
                        slot["id"] = call["id"]
                if choice.get("finish_reason"):
                    finish.append(choice["finish_reason"])
    return {
        "reasoning": "".join(reasoning),
        "content": "".join(content),
        "tools": [tools[k] for k in sorted(tools)],
        "finish": finish,
        "events": events,
        "done": done,
    }


def parse_nonstream(raw: str) -> dict:
    try:
        data = json.loads(raw)
    except ValueError:
        return {"error": "corpo nao-JSON"}
    msg = ((data.get("choices") or [{}])[0].get("message") or {})
    calls = []
    for c in msg.get("tool_calls") or []:
        fn = c.get("function") or {}
        calls.append({"name": fn.get("name", ""), "args": fn.get("arguments", ""), "id": c.get("id", "")})
    return {
        "reasoning": msg.get("reasoning_content") or "",
        "content": msg.get("content") or "",
        "tools": calls,
        "finish": [c.get("finish_reason") for c in data.get("choices") or []],
        "events": 1,
        "done": True,
    }


def repetition(text: str, n: int = NGRAM) -> dict:
    if len(text) < n * 2:
        return {"ratio": 0.0, "ngrams": 0, "top": [], "longest_repeat": 0}
    grams = [text[i:i + n] for i in range(len(text) - n + 1)]
    counts = Counter(grams)
    repeated = sum(c - 1 for c in counts.values() if c > 1)
    top = [(g, c) for g, c in counts.most_common(5) if c > 1]
    # maior bloco repetido imediatamente em sequencia (A A A ...)
    best = 0
    for size in range(min(len(text) // 2, 400), 15, -1):
        for start in range(0, len(text) - 2 * size + 1, max(1, size // 4)):
            block = text[start:start + size]
            if text[start + size:start + 2 * size] == block:
                best = size
                break
        if best:
            break
    return {
        "ratio": round(repeated / max(1, len(grams)), 4),
        "ngrams": len(grams),
        "top": top,
        "longest_repeat": best,
    }


def tool_errors(calls: list[dict], finish: list[str]) -> list[str]:
    problems: list[str] = []
    sigs: Counter[str] = Counter()
    for i, call in enumerate(calls):
        name = call.get("name") or "?"
        args = call.get("args") or ""
        if not args.strip():
            problems.append(f"tool[{i}] {name}: arguments vazio")
            continue
        try:
            parsed = json.loads(args)
        except ValueError as exc:
            problems.append(f"tool[{i}] {name}: arguments invalido/truncado ({len(args)} chars): {exc}")
            continue
        if not isinstance(parsed, dict):
            problems.append(f"tool[{i}] {name}: arguments nao e objeto ({type(parsed).__name__})")
        sigs[name + ":" + json.dumps(parsed, sort_keys=True, ensure_ascii=False)] += 1
    for sig, count in sigs.items():
        if count > 1:
            problems.append(f"tool repetida {count}x: {sig[:110]}")
    if "length" in finish and calls:
        problems.append("finish_reason=length com tool_call: arguments pode ter sido cortado pelo teto de tokens")
    return problems


def analyze(path: Path) -> dict:
    raw = path.read_text(errors="replace")
    parsed = parse_stream(raw) if "data:" in raw[:200] or "text/event-stream" in raw[:200] else parse_nonstream(raw)
    rep_r = repetition(parsed.get("reasoning") or "")
    rep_c = repetition(parsed.get("content") or "")
    tags = sorted(set(CTRL_TAGS.findall(parsed.get("reasoning", "") + parsed.get("content", ""))))
    out = {
        "file": path.name,
        "events": parsed.get("events"),
        "done": parsed.get("done"),
        "finish": parsed.get("finish"),
        "bytes": len(raw),
        "reasoning_chars": len(parsed.get("reasoning") or ""),
        "content_chars": len(parsed.get("content") or ""),
        "content_head": (parsed.get("content") or "")[:120].replace("\n", " "),
        "tools": [{"name": t.get("name"), "args_len": len(t.get("args") or "")} for t in parsed.get("tools") or []],
        "tool_problems": tool_errors(parsed.get("tools") or [], parsed.get("finish") or []),
        "reasoning_repeat": rep_r,
        "content_repeat": rep_c,
        "control_tags": tags,
    }
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=str(Path(__file__).resolve().parent / "dumps"))
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    files = sorted(Path(args.dir).glob("*.stream"))
    if not files:
        print(f"nenhum dump em {args.dir}")
        return 2
    reports = [analyze(f) for f in files]
    if args.json:
        print(json.dumps(reports, ensure_ascii=False, indent=2))
    else:
        for r in reports:
            print(f"\n== {r['file']} ({r['bytes']} bytes, {r['events']} eventos, done={r['done']}, finish={r['finish']})")
            print(f"   reasoning={r['reasoning_chars']} chars | content={r['content_chars']} chars :: {r['content_head']!r}")
            print(f"   tools: {r['tools'] or '-'}")
            rr, cr = r["reasoning_repeat"], r["content_repeat"]
            print(f"   repeticao thinking: ratio={rr['ratio']} maior_bloco={rr['longest_repeat']} top={rr['top']}")
            print(f"   repeticao content : ratio={cr['ratio']} maior_bloco={cr['longest_repeat']} top={cr['top']}")
            if r["control_tags"]:
                print(f"   TAGS VAZADAS: {r['control_tags']}")
            for p in r["tool_problems"]:
                print(f"   TOOL ERRO: {p}")
    flagged = [r for r in reports if r["tool_problems"] or r["control_tags"]
               or r["reasoning_repeat"]["ratio"] > 0.05 or r["content_repeat"]["ratio"] > 0.05]
    print(f"\n{len(flagged)}/{len(reports)} dumps com repeticao ou erro de tool")
    return 0 if not flagged else 1


if __name__ == "__main__":
    raise SystemExit(main())
