#!/usr/bin/env python3
"""scripts/diag/verify-antirepeat-args-window.py

Emulacao INDEPENDENTE da regra do Core (ContentAntiRepeat.TailRepeats) para provar
o que o unit `U-antirepeat-args-window` afirma, sem precisar subir a suite:

  - payload de arquivo que repete um trecho a DISTANCIA: dispara na janela do
    conteudo (256) e NAO dispara na janela de args (1024);
  - loop real de args continua detectado na janela de args.

Contexto (L3 de 2026-10-29, mission-tools-bridge): o stream acumulava content +
reasoning + arguments num unico buffer e comparava tudo com window=512. O abort
cortava o `edit` no meio e o salvage recusava os args truncados
(`anti-repeat where=stream-abort window=512 chars=2050` + `salvage-args-rejected`).

Rodar: python3 scripts/diag/verify-antirepeat-args-window.py
Sai com codigo 1 se alguma assercao falhar.
"""

import random
import sys

TEXT_WINDOW = 256      # ContentAntiRepeat.DefaultWindow
ARGS_WINDOW = 1024     # ToolArgsAntiRepeatReply.ArgsDefaultWindow
BLOCK_SIMILARITY = 0.85
UNITS = (24, 32, 40, 48, 64, 80, 96, 128)


def normalize(s: str) -> str:
    """minusculo, espacos colapsados e trim (igual ao Core)."""
    out, space = [], False
    for ch in s:
        if ch.isspace():
            if not space:
                out.append(" ")
                space = True
        else:
            out.append(ch.lower())
            space = False
    return "".join(out).strip()


def similarity(a: str, b: str) -> float:
    n = min(len(a), len(b))
    if n == 0:
        return 0.0
    return sum(1 for i in range(n) if a[i] == b[i]) / n


def tail_repeats(text: str, window: int) -> bool:
    """ContentAntiRepeat.TailRepeats: cauda verbatim + 3 blocos finais similares."""
    norm = normalize(text)
    if len(norm) >= 2 * window and norm[: len(norm) - window].find(norm[-window:]) >= 0:
        return True
    for unit in UNITS:
        if len(norm) < 3 * unit:
            continue
        a, b = norm[-unit:], norm[-2 * unit : -unit]
        c = norm[-3 * unit : -2 * unit]
        if similarity(a, b) >= BLOCK_SIMILARITY and similarity(b, c) >= BLOCK_SIMILARITY:
            return True
    return False


def main() -> int:
    rnd = random.Random(42)  # mesma semente do unit test
    bloco = "".join(chr(ord("a") + rnd.randrange(26)) for _ in range(260))
    miolo = "".join(chr(ord("a") + rnd.randrange(26)) for _ in range(2000))
    payload = bloco + miolo + bloco

    loop = "linha repetida demais no args\n" * 80

    checks = [
        ("payload @256 dispara (janela do texto)", tail_repeats(payload, TEXT_WINDOW), True),
        ("payload @1024 nao dispara (janela de args)", tail_repeats(payload, ARGS_WINDOW), False),
        ("loop real de args @1024 dispara", tail_repeats(loop, ARGS_WINDOW), True),
    ]
    failed = 0
    for label, got, want in checks:
        ok = got == want
        failed += 0 if ok else 1
        print(f"{label:46s} {'OK' if ok else 'FALHOU'} (esperado {want}, veio {got})")
    print(f"\npayload={len(payload)} chars (norm {len(normalize(payload))}), loop={len(loop)} chars")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
