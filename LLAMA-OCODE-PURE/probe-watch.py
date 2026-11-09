#!/usr/bin/env python3
"""Observa a sonda do collector em tempo real (nao contem nada, so mostra).

Le /api/summary e /api/recent do collector (:4199) e imprime, a cada N segundos,
o agregado da rodada e os ultimos requests com os sinais de repeticao/devagacao:

  streak   mesma prompt (enunciado) reenviada em sequencia
  tool     mesma tool com os MESMOS args em sequencia  <-- laco de tool
  tail     similaridade da cauda do texto (0..1)
  dup      maior numero de linhas identicas na resposta
  prosa    marcas de "vou fazer / proximo passo" (texto + thinking)

Uso:
  python3 probe-watch.py            # loop, 5s, ultimos 12
  python3 probe-watch.py -n 20 -i 2
  python3 probe-watch.py --once
"""
import argparse
import json
import sys
import time
import urllib.request

UP = "http://127.0.0.1:4199"
RED, YEL, DIM, OFF = "\033[31m", "\033[33m", "\033[2m", "\033[0m"


def get(path):
    with urllib.request.urlopen(UP + path, timeout=5) as r:
        return json.load(r)


def color(v, warn, bad):
    if v >= bad:
        return f"{RED}{v}{OFF}"
    if v >= warn:
        return f"{YEL}{v}{OFF}"
    return str(v)


def render(n):
    try:
        s = get("/api/summary")
        recent = get(f"/api/recent?n={n}")
    except Exception as e:
        print(f"{RED}collector fora? {e}{OFF}", flush=True)
        return
    estado = "OCIOSO (zerado)" if s.get("idle") else "ATIVO"
    print(f"\n== run {s['run']} | {estado} | {s['requests']} req | "
          f"{s['messages']} msgs | t/s {s['tps_last']} (med {s['tps_avg']}, max {s['tps_max']}) | "
          f"erros {s['errors']} | tool calls {s['tool_calls']} ==")
    print(f"   laco de tool: agora {color(s.get('tool_loop_last') or 0, 3, 6)} | "
          f"maior {color(s.get('tool_loop_max') or 0, 3, 6)} | "
          f"requests em laco {color(s.get('tool_loop_hits') or 0, 1, 3)} | "
          f"streak max {color(s.get('streak_max') or 0, 3, 6)} | "
          f"prosa {s.get('meta_total')} | thinking chars {s.get('think_chars')}")
    print(f"   anti-repeat {s.get('anti_repeat')} (nudge>={s.get('nudge_at')}, stop>={s.get('stop_at')}): "
          f"nudges {color(s.get('nudges') or 0, 1, 3)} | lacos cortados {color(s.get('stops') or 0, 1, 3)}")
    rows = recent.get("requests") or []
    if not rows:
        print(f"   {DIM}(nenhum request na memoria — collector ocioso ou recem-reiniciado){OFF}")
        return
    print(f"   {'#':>3} {'streak':>6} {'tool':>4} {'tail':>5} {'dup':>3} {'prosa':>5} "
          f"{'resp':>5} {'think':>5} {'finish':<11} {'tools':<12} enunciado")
    for r in rows[-n:]:
        ts = r.get("tsig") or 0
        flag = f"{RED}LACO{OFF}" if ts >= 6 else (f"{YEL}laco{OFF}" if ts >= 3 else "    ")
        if r.get("stopped"):
            flag += f" {RED}CORTADO{OFF}"
        elif r.get("nudged"):
            flag += f" {YEL}nudge{OFF}"
        print(f"   {r['seq']:>3} {color(r.get('streak') or 0, 3, 6):>6} {color(ts, 3, 6):>4} "
              f"{color(r.get('tail_sim') or 0, 0.7, 0.9):>5} {color(r.get('dup') or 0, 3, 5):>3} "
              f"{r.get('meta') or 0:>5} {r.get('resp_chars') or 0:>5} {r.get('think_chars') or 0:>5} "
              f"{(r.get('finish') or '-')[:11]:<11} {(r.get('tools') or '-')[:12]:<12} "
              f"{(r.get('prompt') or '')[:46]} {flag}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=12, help="quantos requests mostrar (default 12)")
    ap.add_argument("-i", type=float, default=5.0, help="intervalo em segundos (default 5)")
    ap.add_argument("--once", action="store_true", help="uma leitura e sai")
    a = ap.parse_args()
    while True:
        render(a.n)
        if a.once:
            return 0
        time.sleep(a.i)


if __name__ == "__main__":
    sys.exit(main() or 0)
