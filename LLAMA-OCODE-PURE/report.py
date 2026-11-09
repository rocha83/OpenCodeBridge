#!/usr/bin/env python3
"""Gera um relatorio legivel (Markdown) do que passou pelo coletor numa rodada.

Le o JSONL de eventos do collector.py, filtra pela janela da rodada (--since) e
escreve reports/<proto>-<timestamp>.md + reports/latest.md.

Uso:
  report.py --since 1730000000 --proto chat --log /tmp/.../battery.log \
            --title "Bateria CHAT" --out-dir reports
"""
import argparse, datetime as dt, json, os, pathlib, sys


def load(path, since):
    rows = []
    if not os.path.exists(path):
        return rows
    with open(path) as f:
        for line in f:
            try:
                e = json.loads(line)
            except Exception:
                continue
            if e.get("ts", 0) >= since:
                rows.append(e)
    return rows


def fmt_ts(ts):
    return dt.datetime.fromtimestamp(ts).strftime("%H:%M:%S")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--since", type=float, default=0)
    ap.add_argument("--proto", default="chat")
    ap.add_argument("--log", default="")
    ap.add_argument("--title", default="Rodada opencode -> llama")
    ap.add_argument("--events", default="/tmp/opencode/llama-pure/metrics/events.jsonl")
    ap.add_argument("--out-dir", default="reports")
    a = ap.parse_args()

    rows = load(a.events, a.since)
    out_dir = pathlib.Path(a.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = out_dir / f"{a.proto}-{stamp}.md"

    req = len(rows)
    msgs = sum(r.get("messages", 0) for r in rows)
    ptok = sum(r.get("prompt_tokens", 0) for r in rows)
    ctok = sum(r.get("completion_tokens", 0) for r in rows)
    tps = [r["tps"] for r in rows if r.get("tps")]
    avg_tps = round(sum(tps) / len(tps), 2) if tps else 0.0
    max_tps = round(max(tps), 2) if tps else 0.0

    lines = [f"# {a.title}", ""]
    lines += [f"- **protocolo/provedor**: {a.proto}",
              f"- **modelo**: {rows[0].get('model') if rows else 'sem requests'}",
              f"- **gerado em**: {dt.datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
              f"- **eventos**: {a.events}", ""]

    lines += ["## Resumo", "",
              "| metrica | valor |", "| --- | --- |",
              f"| requisicoes | {req} |",
              f"| mensagens | {msgs} |",
              f"| tokens de prompt | {ptok} |",
              f"| tokens de saida | {ctok} |",
              f"| velocidade media | {avg_tps} t/s |",
              f"| velocidade maxima | {max_tps} t/s |",
              f"| maior sequencia do MESMO enunciado | {max((r.get('prompt_streak') or 0) for r in rows) if rows else 0} |",
              f"| maior similaridade de cauda | {max((r.get('tail_sim') or 0) for r in rows) if rows else 0} |",
              f"| maior repeticao de linha | {max((r.get('dup_lines') or 0) for r in rows) if rows else 0} |",
              f"| marcas de prosa de plano (total) | {sum((r.get('meta') or 0) for r in rows)} |",
              f"| maior laco de TOOL (mesma tool+args) | {max((r.get('tool_sig_streak') or 0) for r in rows) if rows else 0} |",
              f"| requests com laco de tool (>=3) | {sum(1 for r in rows if (r.get('tool_sig_streak') or 0) >= 3)} |", ""]

    lines += ["## Requests", "",
              "| hora | endpoint | msgs | tools | system chars | prompt tok | saida tok | t/s | resp chars | streak | tool laco | tail sim | tail reps | dup line | meta | tools chamadas | enunciado |",
              "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"]
    for r in rows:
        p = (r.get("prompt") or "").strip().strip('"')[:70].replace("|", "\\|")
        called = ",".join(r.get("resp_tools") or []) or "-"
        lines.append(f"| {fmt_ts(r['ts'])} | {r.get('endpoint')} | {r.get('messages')} | "
                     f"{r.get('tools')} | {r.get('system_chars')} | {r.get('prompt_tokens')} | "
                     f"{r.get('completion_tokens')} | {round(r.get('tps') or 0, 2)} | "
                     f"{r.get('resp_chars') or 0} | {r.get('prompt_streak') or 0} | "
                     f"{r.get('tool_sig_streak') or 0} | "
                     f"{r.get('tail_sim') or 0} | {r.get('tail_reps') or 0} | "
                     f"{r.get('dup_lines') or 0} | {r.get('meta') or 0} | {called} | {p} |")
    if not rows:
        lines.append("| - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | nenhum request no periodo |")

    # Sonda de repeticao/devagacao: so os requests que mais repetem, para leitura rapida.
    sus = [r for r in rows if (r.get("prompt_streak") or 0) >= 3
           or (r.get("tail_sim") or 0) >= 0.85 or (r.get("dup_lines") or 0) >= 3
           or (r.get("tool_sig_streak") or 0) >= 3]
    if sus:
        sus = sorted(sus, key=lambda r: ((r.get("tool_sig_streak") or 0),
                                         (r.get("prompt_streak") or 0),
                                         (r.get("tail_sim") or 0),
                                         (r.get("dup_lines") or 0)), reverse=True)[:10]
        lines += ["", "## Sinal de repeticao/devagacao (top 10)", "",
                  "| hora | streak | tool laco | tail sim | tail reps | dup line | meta | resp chars | tools | enunciado |",
                  "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"]
        for r in sus:
            p = (r.get("prompt") or "").strip().strip('"')[:80].replace("|", "\\|")
            called = ",".join(r.get("resp_tools") or []) or "-"
            lines.append(f"| {fmt_ts(r['ts'])} | {r.get('prompt_streak') or 0} | "
                         f"{r.get('tool_sig_streak') or 0} | "
                         f"{r.get('tail_sim') or 0} | {r.get('tail_reps') or 0} | "
                         f"{r.get('dup_lines') or 0} | {r.get('meta') or 0} | "
                         f"{r.get('resp_chars') or 0} | {called} | {p} |")

    if a.log and os.path.exists(a.log):
        lines += ["", "## Resultado da bateria", "", "```"]
        for line in open(a.log, errors="ignore"):
            if "RESULTADO" in line or line.startswith("=="):
                lines.append(line.rstrip())
        lines += ["```"]

    out.write_text("\n".join(lines) + "\n")
    (out_dir / "latest.md").write_text(out.read_text())
    print(f"relatorio: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
