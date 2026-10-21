#!/usr/bin/env python3
"""Belts azul→preta pelo opencode, com o enunciado cru como unico prompt.

Porte fiel de E2EBeltsTests (role Orch) e E2EBeltsBuildTests (role Build) do
projeto C#: mesmo enunciado, mesma ordem (Build primeiro), mesmos workspaces
limpos. A diferenca e que aqui quem executa e o opencode (`opencode run`), nao
um loop HTTP proprio — logo as regras de papel vivem no agente (system), nunca
no chat: a mensagem enviada e exatamente o enunciado.

Rubricas (as mesmas das belts C#):
  build: >=3 arquivos de codigo (.cs/.sql/.tsx/.jsx/.ts/.py) e >500 chars
  orch : delegacao via subagent (agente nativo) E entrega por qualquer um dos
         caminhos:
         - implementacao direta: >=3 arquivos de codigo e >500 chars, ou
         - script: >=1 script .sh com `bash -n` OK e >=1 executado com exit 0

Uso:
  python3 belts_opencode.py                      # build (5) e depois orch (5)
  python3 belts_opencode.py --belts azul,verde
  python3 belts_opencode.py --tracks build
  python3 belts_opencode.py --timeout 1800 --verbose
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent
BELTS_DIR = ROOT / "belts"

# Enunciados portados verbatim de Rochas.OpenCodeBridge.Test/E2EBeltsTests.cs
BELTS: list[tuple[str, str]] = [
    ("azul", "Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reembolsos corporativos) usando repositorio generico com ORM. Escopo SOMENTE backend de dados (sem frontend, sem API). Premissas: backend .NET C#."),
    ("verde", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos (entidades, value objects, regras de aprovacao por faixa de valor) e o banco de dados SQL: DDL das tabelas, indices, constraints e seeds. Valide CPF e CNPJ. Escopo: dominio + banco SQL. Premissas: backend .NET C#."),
    ("roxa", "Especifique o dominio backend C# de cadastro de clientes e fornecedores, o banco de dados SQL (DDL, indices, constraints) e os repositorios e servicos: repositorio generico Dapper, servicos de aplicacao, validacao e mascaras de e-mail, telefone BR com DDD e CEP. Escopo: dominio + banco SQL + repositorios e servicos. Premissas: backend .NET C#."),
    ("marrom", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos, o banco de dados SQL, os repositorios e servicos (Dapper), a API REST e o frontend React. Escopo: dominio + banco SQL + repositorios e servicos + API + frontend React. Premissas: backend .NET C#."),
    ("preta", "Especifique a arquitetura de ecossistema: dominio backend C# do Portal do Colaborador, banco de dados SQL, repositorios e servicos, API REST, servicos bus, pipeline de Big Data com ML (regressao, classificacao, rede neural perceptron). Escopo: dominio + banco SQL + repositorios e servicos + API + servicos bus + redes neurais. Premissas: backend .NET C#."),
]

TRACKS: dict[str, dict[str, str]] = {
    "build": {"agent": "build", "model": "alt12b/gemma-4-12b"},
    "orch": {"agent": "build", "model": "alt12b/gemma-4-12b"},
}

CODE_EXT = {".cs", ".sql", ".tsx", ".jsx", ".ts", ".py"}
SKIP_DIRS = {".opencode", ".git", "node_modules", "bin", "obj"}
UNSAFE = re.compile(r"\b(sudo|mkfs|shutdown|reboot)\b|rm\s+-rf\s+/|curl\s|wget\s|:\s*\(\s*\)")


def log(msg: str) -> None:
    print(msg, flush=True)


def clean_dir(path: Path) -> None:
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(parents=True, exist_ok=True)


def walk_files(root: Path) -> list[Path]:
    out: list[Path] = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS and not d.startswith(".")]
        for name in filenames:
            if name.startswith("."):
                continue
            out.append(Path(dirpath) / name)
    return out


def parse_events(log_path: Path) -> dict:
    tools: dict[str, int] = {}
    subagents: list[str] = []
    texts: list[str] = []
    errors: list[str] = []
    for line in log_path.read_text(errors="replace").splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            ev = json.loads(line)
        except ValueError:
            continue
        kind = ev.get("type")
        part = ev.get("part") or {}
        if kind == "tool_use":
            name = part.get("tool") or "?"
            tools[name] = tools.get(name, 0) + 1
            if name == "subagent":
                agent = (part.get("state") or {}).get("input", {}).get("agent")
                if agent:
                    subagents.append(agent)
        elif kind == "text":
            texts.append(part.get("text") or "")
        elif kind == "error":
            errors.append(str(ev.get("error"))[:200])
    return {"tools": tools, "subagents": subagents, "text": "".join(texts), "errors": errors}


def run_opencode(agent: str, model: str, prompt: str, ws: Path, log_path: Path, timeout: int) -> int:
    cmd = [
        "opencode", "run", "--standalone",
        "--agent", agent,
        "--model", model,
        "--auto",
        "--format", "json",
        prompt,
    ]
    started = time.monotonic()
    with log_path.open("w", encoding="utf-8") as fh:
        try:
            proc = subprocess.run(
                cmd, cwd=str(ws), stdout=fh, stderr=subprocess.STDOUT,
                timeout=timeout, text=True,
            )
            code = proc.returncode
        except subprocess.TimeoutExpired:
            fh.write(json.dumps({"type": "harness_timeout", "timeout": timeout}) + "\n")
            code = 124
    log(f"    opencode exit={code} ({time.monotonic() - started:.0f}s)")
    return code


def check_scripts(ws: Path) -> tuple[int, int, list[str]]:
    """Espelha a belt C#: bash -n em cada .sh, depois executa (com guarda)."""
    valid = 0
    executed = 0
    notes: list[str] = []
    for sh in sorted(ws.rglob("*.sh")):
        src = sh.read_text(errors="replace")
        r = subprocess.run(["bash", "-n", str(sh)], capture_output=True, text=True, timeout=60)
        if r.returncode != 0:
            notes.append(f"SYNTAX FAIL {sh.name}: {r.stderr.strip().splitlines()[:1]}")
            continue
        valid += 1
        if UNSAFE.search(src):
            notes.append(f"SKIP EXEC {sh.name}: comando sensivel no script")
            continue
        try:
            r2 = subprocess.run(
                ["bash", sh.name], cwd=str(sh.parent), capture_output=True,
                text=True, timeout=120,
            )
        except subprocess.TimeoutExpired:
            notes.append(f"EXEC TIMEOUT {sh.name}")
            continue
        if r2.returncode == 0:
            executed += 1
            notes.append(f"EXEC OK {sh.name}")
        else:
            last = (r2.stderr.strip().splitlines() or ["?"])[-1]
            notes.append(f"EXEC FAIL {sh.name} exit={r2.returncode}: {last[:120]}")
    return valid, executed, notes


def validate(track: str, ws: Path, subagents: list[str] | None = None) -> dict:
    files = walk_files(ws)
    code = [f for f in files if f.suffix.lower() in CODE_EXT]
    chars = sum(len(f.read_text(errors="replace")) for f in code)
    scripts = [f for f in files if f.suffix == ".sh"]
    companions = [f for f in files if f.suffix == ".md"]
    valid, executed, notes = check_scripts(ws) if scripts else (0, 0, [])
    if track == "build":
        ok = len(code) >= 3 and chars > 500
    else:
        # Orch: exige delegacao via subagent (agente nativo) + entrega por
        # qualquer caminho (implementacao direta ou script executado).
        delegated = list(subagents or [])
        build_path = len(code) >= 3 and chars > 500
        script_path = valid >= 1 and executed >= 1
        ok = bool(delegated) and (build_path or script_path)
    return {
        "ok": ok,
        "delegated_roles": sorted(set(subagents or [])),
        "code_files": len(code),
        "code_chars": chars,
        "scripts": len(scripts),
        "scripts_valid": valid,
        "scripts_executed": executed,
        "companions": len(companions),
        "files": [str(f.relative_to(ws)) for f in files][:40],
        "notes": notes,
    }


def main() -> int:
    ap = argparse.ArgumentParser(description="belts azul->preta via opencode")
    ap.add_argument("--belts", default=None, help="azul,verde,roxa,marrom,preta (default: todas)")
    ap.add_argument("--tracks", default="build,orch", help="build,orch (default: build primeiro)")
    ap.add_argument("--timeout", type=int, default=1800, help="segundos por belt (default 1800)")
    ap.add_argument("--dry", action="store_true", help="so lista o plano")
    args = ap.parse_args()

    wanted = {b.strip() for b in args.belts.split(",")} if args.belts else None
    belts = [b for b in BELTS if wanted is None or b[0] in wanted]
    unknown = (wanted or set()) - {b[0] for b in BELTS}
    if unknown:
        log(f"belt invalida: {', '.join(sorted(unknown))}")
        return 2
    tracks = [t.strip() for t in args.tracks.split(",")]
    for t in tracks:
        if t not in TRACKS:
            log(f"track invalida: {t} (use build|orch)")
            return 2

    log(f"belts: {', '.join(k for k, _ in belts)}")
    log(f"ordem: {' -> '.join(tracks)}  (workspaces em {BELTS_DIR})")
    if args.dry:
        for t in tracks:
            for key, prompt in belts:
                log(f"  [{t}/{key}] {TRACKS[t]['agent']} <- {prompt[:70]}...")
        return 0

    results: list[dict] = []
    BELTS_DIR.mkdir(parents=True, exist_ok=True)
    for track in tracks:
        agent = TRACKS[track]["agent"]
        model = TRACKS[track]["model"]
        for key, prompt in belts:
            ws = BELTS_DIR / track / key
            clean_dir(ws)
            (ws / "opencode.json").write_text(
                (ROOT / "opencode.json").read_text(), encoding="utf-8"
            )
            log_path = BELTS_DIR / track / f"{key}.jsonl"
            log(f"\n[{track}/{key}] START agent={agent} model={model} ws={ws}")
            started = time.monotonic()
            code = run_opencode(agent, model, prompt, ws, log_path, args.timeout)
            events = parse_events(log_path)
            checks = validate(track, ws, events["subagents"])
            elapsed = round(time.monotonic() - started, 1)
            status = "PASS" if (code == 0 and checks["ok"]) else "FAIL"
            detail = {
                "belt": key,
                "track": track,
                "agent": agent,
                "model": model,
                "status": status,
                "exit": code,
                "seconds": elapsed,
                "subagents": events["subagents"],
                "tools": events["tools"],
                "errors": events["errors"],
                "transcript": str(log_path),
                "when": time.strftime("%Y-%m-%dT%H:%M:%S"),
                **checks,
            }
            (BELTS_DIR / track / f"{key}_result.json").write_text(
                json.dumps(detail, ensure_ascii=False, indent=2)
            )
            results.append(detail)
            log(f"[{track}/{key}] {status} files={checks['code_files']} chars={checks['code_chars']} "
                f"sh={checks['scripts']} valid={checks['scripts_valid']} exec={checks['scripts_executed']} "
                f"roles={','.join(checks['delegated_roles']) or '-'} {elapsed}s")
            for note in checks["notes"][:6]:
                log(f"    {note}")

    log("\n===== RESUMO =====")
    for r in results:
        log(f"  {r['track']:5} {r['belt']:7} {r['status']:4} "
            f"files={r['code_files']:2} chars={r['code_chars']:6} "
            f"sh={r['scripts']}/{r['scripts_valid']}/{r['scripts_executed']} "
            f"sub={','.join(sorted(set(r['subagents']))) or '-'} {r['seconds']}s")
        if r["errors"]:
            log(f"        erro: {r['errors'][0]}")
    fails = [r for r in results if r["status"] != "PASS"]
    log(f"\n{len(results) - len(fails)}/{len(results)} PASS")
    return 0 if not fails else 1


if __name__ == "__main__":
    raise SystemExit(main())
