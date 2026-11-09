#!/usr/bin/env python3
"""Agente direto na BRIDGE (:4124) — sem WAR, sem suite, sem help de tool.

Contrato do experimento (2026-11-07): a bridge roda com SOMENTE
`--main-harness 1` e a coercao do template nativo (`--coerce-text-tools 1`
`--coerce-narrated 1`). Caudas (behavior/tools) e anti-repeat desligados.
O cliente abaixo e' o "cli": manda o enunciado literal, executa as tool calls
localmente (shell/write/read/edit/git) e devolve `role: tool`.

Uso:
  python3 bridge-agent-direct.py                 # enunciado fixo (abaixo)
  python3 bridge-agent-direct.py --turns 60      # teto de turnos
  python3 bridge-agent-direct.py --prompt-file f # outro enunciado

Saida: /tmp/opencode/direct-<ts>/ (workspace) + transcript.jsonl + run.log
"""

import argparse
import json
import re
import shlex
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

BRIDGE = "http://127.0.0.1:4124"
KEY = "guest@123"
MODEL = "Gemma4-E4B-UCD-GK-Q4"

# Guarda do piloto (mesma ideia da belt do mini-bridge-python): comando
# destrutivo nao roda, volta como FAIL para o modelo.
UNSAFE = re.compile(r"\b(sudo|mkfs|shutdown|reboot)\b|rm\s+-rf\s+/|:\s*\(\s*\)\s*\{")

# Enunciado literal do operador (2026-11-07). Nao editar sem ordem explicita.
ENUNCIADO = (
    "crie uma subpasta na pasta atual e dentro dela... "
    "1. crie um projeto .net console usando o template padrao do dotnet, imprimindo 'hello world' no log do console. "
    "2. inicialize um repositorio git na pasta onde criou o projeto usando git init informando config com dados de nome e email do usuario, "
    "adicione os novos arquivos e efetue um primeiro commit. Feito isto, edite o arquivo principal do programa, mudando a mensagem para "
    "'hello brazil', efetue novo git commit com as alteracoes. "
    "3. imprima o historico git em um arquivo historico. "
    "trabalhe dentro da pasta enunciada acima, faca uso efetivo de tools com dialogo sucinto com o usuario a cada passo antes de executar a tool."
)

TOOLS = [
    {"type": "function", "function": {
        "name": "shell",
        "description": "Executa um comando bash dentro do workspace (dotnet, python, mkdir, ls, cat, rm). "
                       "Use para criar projeto com o template, compilar, rodar e encadear comandos com &&.",
        "parameters": {"type": "object", "properties": {"command": {"type": "string"}},
                       "required": ["command"]}}},
    {"type": "function", "function": {
        "name": "write",
        "description": "Cria ou sobrescreve um arquivo texto no workspace. Envie o conteudo completo do arquivo.",
        "parameters": {"type": "object", "properties": {"path": {"type": "string"}, "content": {"type": "string"}},
                       "required": ["path", "content"]}}},
    {"type": "function", "function": {
        "name": "read",
        "description": "Le um arquivo texto do workspace e devolve ate 4000 caracteres.",
        "parameters": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}}},
    {"type": "function", "function": {
        "name": "edit",
        "description": "Substitui a primeira ocorrencia de oldString por newString no arquivo. "
                       "Use para trocar uma mensagem sem reescrever o arquivo inteiro.",
        "parameters": {"type": "object", "properties": {"path": {"type": "string"}, "oldString": {"type": "string"},
                                                        "newString": {"type": "string"}},
                       "required": ["path", "oldString", "newString"]}}},
    {"type": "function", "function": {
        "name": "git",
        "description": "Executa um comando git (sem a palavra 'git') no workspace, ex.: 'init', "
                       "'config user.name \"Dev\"', 'add .', 'commit -m \"msg\"', 'log --oneline'. "
                       "Nao encadeie com && aqui: para isso use shell.",
        "parameters": {"type": "object", "properties": {"command": {"type": "string"}, "path": {"type": "string"}},
                       "required": ["command"]}}},
]


def publish(path, payload):
    """Publica no painel (Grafana -> Infinity -> bridge), igual ao driver."""
    try:
        req = urllib.request.Request(BRIDGE + path, json.dumps(payload).encode(),
                                     {"Content-Type": "application/json",
                                      "Authorization": "Bearer " + KEY})
        urllib.request.urlopen(req, timeout=5).read()
    except Exception:
        pass


def gpu_stats():
    """VRAM/GPU para o painel; cache de 3s para nao pesar no loop."""
    now = time.time()
    if now - _GPU_CACHE["t"] < 3:
        return _GPU_CACHE["v"]
    try:
        r = subprocess.run(["nvidia-smi", "--query-gpu=memory.used,memory.total,utilization.gpu",
                            "--format=csv,noheader,nounits"],
                           capture_output=True, text=True, timeout=5)
        used, total, util = (int(x.strip()) for x in r.stdout.strip().splitlines()[0].split(","))
        _GPU_CACHE.update(t=now, v=(used, total, util))
    except Exception:
        pass
    return _GPU_CACHE["v"]


_GPU_CACHE = {"t": 0.0, "v": (0, 0, 0)}


def chat(messages, max_tokens=2048, retries=2):
    body = {"model": MODEL, "messages": messages, "tools": TOOLS,
            "tool_choice": "auto", "stream": False, "max_tokens": max_tokens}
    data = json.dumps(body).encode()
    for i in range(retries + 1):
        try:
            req = urllib.request.Request(BRIDGE + "/v1/chat/completions", data, {
                "Content-Type": "application/json", "Authorization": "Bearer " + KEY})
            with urllib.request.urlopen(req, timeout=900) as r:
                return json.loads(r.read())
        except Exception as e:
            if i == retries:
                raise
            print(f"  [retry] {type(e).__name__}: {e}", flush=True)


def safe(root: Path, rel: str) -> Path:
    # Canonicaliza os dois lados: cobre --out relativo e workspace alcancado
    # por symlink (antes os dois davam "fora do workspace" em toda tool).
    base = root.resolve()
    p = (base / rel).resolve()
    if base not in p.parents and p != base:
        raise ValueError(f"fora do workspace: {rel}")
    return p


def execute(root: Path, name: str, args: dict):
    try:
        if name == "write":
            p = safe(root, args["path"])
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(args.get("content", ""), encoding="utf-8")
            return True, f"ok: {args['path']} ({len(args.get('content',''))} chars)"
        if name == "read":
            p = safe(root, args["path"])
            return True, p.read_text(encoding="utf-8", errors="replace")[:4000]
        if name == "edit":
            p = safe(root, args["path"])
            cur = p.read_text(encoding="utf-8")
            old = args.get("oldString", "")
            new = args.get("newString", "")
            if old not in cur:
                return False, f"oldString nao encontrado em {args['path']}"
            p.write_text(cur.replace(old, new, 1), encoding="utf-8")
            return True, f"ok: {args['path']} editado"
        if name == "shell":
            cmd = args["command"]
            if UNSAFE.search(cmd):
                return False, "bloqueado: comando sensivel (guarda do piloto)"
            r = subprocess.run(["bash", "-lc", cmd], cwd=str(root),
                               capture_output=True, text=True, timeout=600)
            out = (r.stdout + r.stderr)[:4000]
            return r.returncode == 0, out
        if name == "git":
            cmd = args.get("command", "").strip()
            if cmd.startswith("git "):
                cmd = cmd[4:]
            cwd = safe(root, args.get("path") or ".")
            r = subprocess.run(["git"] + shlex.split(cmd), cwd=str(cwd),
                               capture_output=True, text=True, timeout=300)
            out = (r.stdout + r.stderr)[:4000]
            return r.returncode == 0, out
        return False, "tool desconhecida: " + name
    except Exception as e:
        return False, f"{type(e).__name__}: {e}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--turns", type=int, default=50)
    ap.add_argument("--prompt-file")
    ap.add_argument("--out", default="")
    ap.add_argument("--enunciado-step", default="",
                    help="step do painel de enunciado/respostas (compartilhe entre rodadas paralelas)")
    ap.add_argument("--stop-on-repeat", type=int, default=0,
                    help="encerra apos N tool calls identicas seguidas (0 = desligado)")
    ap.add_argument("--check-run", type=int, default=1,
                    help="executa o artefato no fim e exige 'hello brazil' (1 = ligado)")
    a = ap.parse_args()

    prompt = Path(a.prompt_file).read_text(encoding="utf-8").strip() if a.prompt_file else ENUNCIADO
    root = Path(a.out or f"/tmp/opencode/direct-{time.strftime('%Y%m%d-%H%M%S')}")
    root.mkdir(parents=True, exist_ok=True)
    root = root.resolve()
    transcript = (root.parent / (root.name + ".transcript.jsonl")).open("w", encoding="utf-8")
    logf = (root.parent / (root.name + ".run.log")).open("w", encoding="utf-8")

    def log(s: str):
        print(s, flush=True)
        logf.write(s + "\n")
        logf.flush()

    log(f"workspace: {root}")
    log(f"bridge: {BRIDGE} model: {MODEL}")
    log(f"enunciado: {prompt}\n")

    step_name = "direct-cli/" + root.name
    enun_step = a.enunciado_step or step_name
    publish("/api/test/enunciado", {"step": enun_step, "prompt": prompt})

    messages = [{"role": "user", "content": prompt}]
    transcript.write(json.dumps({"role": "user", "content": prompt}, ensure_ascii=False) + "\n")
    calls_total = 0
    error = ""
    last_sig, repeat = "", 0
    t0 = time.time()

    for turn in range(a.turns):
        t_call = time.time()
        try:
            out = chat(messages)
        except Exception as e:
            error = f"{type(e).__name__}: {e}"
            log(f"!! falha na chamada ao modelo: {error}")
            break
        msg = ((out.get("choices") or [{}])[0].get("message") or {})
        content = (msg.get("content") or "").strip()
        calls = msg.get("tool_calls") or []
        finish = (out.get("choices") or [{}])[0].get("finish_reason")
        usage = out.get("usage") or {}
        turn_secs = max(time.time() - t_call, 1e-6)
        tps = float(((out.get("timings") or {}).get("predicted_per_second")) or 0.0)
        if not 0.0 < tps < 500.0:
            # fallback: tokens de saida / tempo de parede do turno
            tps = (usage.get("completion_tokens") or 0) / turn_secs
        g = gpu_stats()
        publish("/api/test/probe", {
            "step": step_name, "mode": "direct", "phase": "run",
            "ts": int(time.time() * 1000),
            "tools_calls": calls_total, "model_requests": turn + 1,
            "tps_output": round(tps, 1), "tps_total": round(tps, 1),
            "vram_used_mb": g[0], "vram_total_mb": g[1], "gpu_util": g[2]})
        log(f"--- turno {turn + 1} | calls={len(calls)} finish={finish} "
            f"tok_out={usage.get('completion_tokens')} ---")
        if content:
            log("modelo: " + content[:1200].replace("\n", "\n  "))

        keep = {"role": "assistant", "content": msg.get("content") or ""}
        if calls:
            keep["tool_calls"] = calls
        messages.append(keep)
        transcript.write(json.dumps(keep, ensure_ascii=False) + "\n")

        if not calls:
            # Sem nudge no cliente (docs/TUNEL-LLAMA.md): turno sem tool call
            # encerra o loop; o veredito sai do artefato.
            log("(sem tool call: encerrando sem nudge)")
            break

        for c in calls:
            fn = c.get("function") or {}
            name = fn.get("name") or ""
            try:
                args = json.loads(fn.get("arguments") or "{}")
            except json.JSONDecodeError:
                args = {}
            ok, detail = execute(root, name, args)
            calls_total += 1
            log(f"  {name}({json.dumps(args, ensure_ascii=False)[:160]}) -> "
                f"{'ok' if ok else 'FAIL'} {detail[:220].strip()}")
            tool_msg = {"role": "tool", "tool_call_id": c.get("id", ""), "name": name,
                        "content": detail}
            messages.append(tool_msg)
            transcript.write(json.dumps(tool_msg, ensure_ascii=False) + "\n")

        if a.stop_on_repeat > 0:
            sig = json.dumps([[(c.get("function") or {}).get("name"),
                               (c.get("function") or {}).get("arguments")] for c in calls],
                             sort_keys=True)
            repeat = repeat + 1 if sig == last_sig else 1
            last_sig = sig
            if repeat >= a.stop_on_repeat:
                log(f"(mesma tool call {repeat}x seguida: encerrando por --stop-on-repeat)")
                break

    dt = time.time() - t0
    log(f"\n=== fim: {calls_total} tool calls em {dt:.0f}s ===")
    # verificacao objetiva do enunciado
    subs = [p for p in root.iterdir() if p.is_dir()]
    projs = sorted(root.rglob("*.csproj"))
    hist = sorted(root.rglob("*histor*"))
    log(f"subpastas: {[p.name for p in subs]}")
    log(f"projetos: {[str(p.relative_to(root)) for p in projs]}")
    log(f"arquivo de historico: {[str(p.relative_to(root)) for p in hist]}")
    for p in projs:
        src = p.parent / "Program.cs"
        if src.exists():
            log(f"{src.relative_to(root)}: {src.read_text(encoding='utf-8').strip()[:200]}")
        r = subprocess.run(["git", "log", "--format=%h %s"], cwd=str(p.parent),
                           capture_output=True, text=True)
        log(f"git log ({p.parent.relative_to(root)}): {(r.stdout or r.stderr).strip()}")
    checks = []
    proj = projs[0] if projs else None
    checks.append(("subpasta com projeto .csproj", proj is not None and proj.parent != root))
    if proj is not None:
        src = proj.parent / "Program.cs"
        checks.append(("Program.cs com 'hello brazil'",
                       src.exists() and "hello brazil" in src.read_text(encoding="utf-8", errors="replace").lower()))
        rl = subprocess.run(["git", "log", "--format=%s"], cwd=str(proj.parent),
                            capture_output=True, text=True)
        n = len([m for m in rl.stdout.splitlines() if m.strip()])
        checks.append((f"2+ commits ({n})", n >= 2))
    checks.append(("arquivo de historico",
                   any(x.is_file() and "histor" in x.name.lower() for x in root.rglob("*"))))
    # Prova de execucao (ideia da belt do mini-bridge-python): o artefato tem
    # que rodar e imprimir 'hello brazil', nao so existir no disco.
    run_out = ""
    if a.check_run:
        cmd = None
        cwd = str(root)
        if proj is not None:
            cmd = ["dotnet", "run", "--project", str(proj)]
            cwd = str(proj.parent)
        else:
            mains = sorted(root.rglob("main.py"))
            if mains:
                cmd = ["python3", str(mains[0])]
                cwd = str(mains[0].parent)
        if cmd is not None:
            try:
                rr = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=180)
                run_out = (rr.stdout + rr.stderr).strip()[:600]
                log(f"execucao {' '.join(cmd[:2])}: exit={rr.returncode}\n  {run_out[:400]}")
            except Exception as e:
                run_out = f"{type(e).__name__}: {e}"
                log(f"execucao falhou: {run_out}")
        checks.append(("artefato executa e imprime 'hello brazil'",
                       "hello brazil" in run_out.lower()))
    ok = all(v for _, v in checks) and not error
    if error:
        log(f"erro nao tratado no loop: {error}")
    log("\n=== verificacao ===")
    for label, v in checks:
        log(f"  [{'x' if v else ' '}] {label}")
    publish("/api/test/step", {"step": step_name, "level": "direct",
                               "verdict": "PASS" if ok else "FAIL",
                               "secs": round(dt), "mode": "direct"})
    transcript.close()
    logf.close()
    print(f"\nTRANSCRIPT={root.parent / (root.name + '.transcript.jsonl')}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
