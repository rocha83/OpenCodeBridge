#!/usr/bin/env python3
"""Sonda de sessao do opencode (live): o que o modelo respondeu, o que ele
chamou de tool e o que a bridge fez com o request.

Le o banco do opencode em modo read-only (WAL nao e tocado) e segue o log da
bridge. Imprime so o delta: mensagem nova da sessao + eventos de harness/
anti-repeat/breaker/shim do log. Nao escreve nada no banco.

Uso:
  scripts/war/probe_session.py --session ses_e9033 --bridge-log /tmp/bridge-gpu2.log
  scripts/war/probe_session.py --session ses_e9033 --backlog 8      # mostra o passado
  scripts/war/probe_session.py --session ses_e9033 --out /tmp/probe.log
"""
import argparse
import json
import os
import sqlite3
import sys
import time

DB = os.path.expanduser("~/.local/share/opencode/opencode.db")
EVENTS = ("main-harness", "tails", "anti-repeat", "breaker", "tool-break", "coerce",
          "tool-shim", "context", "tool-ask", "tools-lost", "response", "config", "error",
          "traffic", "tool-args", "tool-guide", "tool-unwrap", "harness-strip", "context-guard")


def connect():
    return sqlite3.connect(f"file:{DB}?mode=ro", uri=True, timeout=5)


def row_to_line(seq, typ, data):
    d = json.loads(data)
    if typ == "user":
        return f"[{seq}] USER: {(d.get('text') or '')[:400]}"
    if typ != "assistant":
        return None
    out = []
    fin = d.get("finish") or d.get("rawFinish") or ""
    err = d.get("error") or {}
    if err:
        out.append(f"[{seq}] ASSISTANT erro={err.get('type')}: {str(err.get('message'))[:300]}")
    for p in d.get("content") or []:
        if p.get("type") == "text" and (p.get("text") or "").strip():
            out.append(f"[{seq}] TEXTO: {(p.get('text') or '')[:400]}")
        elif p.get("type") == "tool":
            st = p.get("state") or {}
            if (st.get("status") or "") == "streaming":
                out.append(f"[{seq}] TOOL {p.get('name')} (streaming...)")
                continue
            args = json.dumps(st.get("input") or {}, ensure_ascii=False)
            res = ""
            for c in st.get("content") or []:
                if c.get("type") == "text":
                    res = (c.get("text") or "")[:180].replace("\n", " ")
            out.append(f"[{seq}] TOOL {p.get('name')} {args[:300]} -> {res}")
        elif p.get("type") == "reasoning":
            continue
    if fin:
        out.append(f"[{seq}] finish={fin}")
    return "\n".join(out) if out else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--session", required=True)
    ap.add_argument("--bridge-log", default=os.environ.get("PROBE_BRIDGE_LOG", "/tmp/bridge-gpu2.log"))
    ap.add_argument("--backlog", type=int, default=0, help="quantas mensagens antigas mostrar")
    ap.add_argument("--out", default="", help="arquivo de saida (append); vazio = stdout")
    ap.add_argument("--interval", type=float, default=2.0)
    args = ap.parse_args()

    sink = open(args.out, "a", buffering=1) if args.out else sys.stdout

    def emit(text):
        sink.write(text + "\n")
        if args.out:
            sink.flush()

    con = connect()
    cur = con.cursor()
    meta = cur.execute(
        "select title,directory,agent,model from session_v2 where id=?", (args.session,)).fetchone()
    if not meta:
        sys.exit(f"sessao {args.session} inexistente em {DB}")
    last = cur.execute("select coalesce(max(seq),0) from session_message where session_id=?",
                       (args.session,)).fetchone()[0]
    emit(f"# probe {args.session} | {meta[0]} | dir={meta[1]} | agent={meta[2]} | model={meta[3]}")
    emit(f"# bridge-log={args.bridge_log} (eventos: {', '.join(EVENTS)})")

    if args.backlog:
        rows = cur.execute(
            "select seq,type,data from session_message where session_id=? order by seq",
            (args.session,)).fetchall()
        for seq, typ, data in rows[-args.backlog:]:
            line = row_to_line(seq, typ, data)
            if line:
                emit(line)

    blog_offset = 0
    if args.bridge_log and os.path.exists(args.bridge_log):
        blog_offset = os.path.getsize(args.bridge_log)
    emit("--- seguindo ao vivo (ctrl-c para sair) ---")

    try:
        while True:
            time.sleep(args.interval)
            # 1) mensagens novas
            try:
                rows = cur.execute(
                    "select seq,type,data from session_message where session_id=? and seq>? order by seq",
                    (args.session, last)).fetchall()
            except sqlite3.Error as e:  # WAL rotativo: reconecta
                emit(f"# db erro: {e}")
                con = connect()
                cur = con.cursor()
                continue
            for seq, typ, data in rows:
                last = max(last, seq)
                line = row_to_line(seq, typ, data)
                if line:
                    emit(line)
            # 2) eventos da bridge
            if args.bridge_log and os.path.exists(args.bridge_log):
                size = os.path.getsize(args.bridge_log)
                if size < blog_offset:      # log rotacionado
                    blog_offset = 0
                if size > blog_offset:
                    with open(args.bridge_log, "r", errors="replace") as f:
                        f.seek(blog_offset)
                        chunk = f.read()
                        blog_offset = f.tell()
                    for ln in chunk.splitlines():
                        if any(k in ln for k in EVENTS):
                            emit("BRIDGE " + ln[:600])
    except KeyboardInterrupt:
        emit("--- probe encerrado ---")


if __name__ == "__main__":
    main()
