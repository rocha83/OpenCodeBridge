#!/usr/bin/env python3
"""Tools direto no llama (:4100) — contrato minimo, nativo, sem bridge e sem help.

Request: /v1/chat/completions com `tools` no formato OpenAI (so name + parameters,
sem description) e `tool_choice: required` no primeiro turno da etapa.
Resposta: `choices[0].message.tool_calls` nativo; o script executa, devolve
`role: tool` e deixa o modelo continuar.

Cenarios: full, mission, azul, verde.

  python3 llama-tools-direct.py --suite full
  python3 llama-tools-direct.py --suite all --steps 2
"""

import argparse
import json
import re
import shlex
import subprocess
import sys
import time
import urllib.request
from datetime import datetime
from pathlib import Path

URL = "http://127.0.0.1:4100/v1/chat/completions"
KEY = "guest@123"
MODEL = "Gemma4-E4B-GK-Q4"

# Dashboard (Grafana -> Infinity -> bridge). O script publica sonda e veredito
# nos mesmos endpoints que o driver de teste usa; os paineis ja' existem.
BRIDGE = "http://127.0.0.1:4124"
BRIDGE_KEY = "guest@123"


def publish(path, payload):
    try:
        req = urllib.request.Request(
            BRIDGE + path, json.dumps(payload).encode(),
            {"Content-Type": "application/json", "Authorization": "Bearer " + BRIDGE_KEY})
        urllib.request.urlopen(req, timeout=5).read()
    except Exception as e:
        print(f"    [dashboard] {type(e).__name__}: {e}")


# contrato minimo: nome + parametros, nada de description
TOOLS = [
    {"type": "function", "function": {"name": "write", "parameters": {
        "type": "object", "properties": {"path": {"type": "string"}, "content": {"type": "string"}},
        "required": ["path", "content"]}}},
    {"type": "function", "function": {"name": "read", "parameters": {
        "type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}}},
    {"type": "function", "function": {"name": "shell", "parameters": {
        "type": "object", "properties": {"command": {"type": "string"}}, "required": ["command"]}}},
    {"type": "function", "function": {"name": "webfetch", "parameters": {
        "type": "object", "properties": {"url": {"type": "string"}}, "required": ["url"]}}},
]


def chat(messages, tools=True, max_tokens=1024, choice="auto", retries=2):
    body = {"model": MODEL, "messages": messages, "max_tokens": max_tokens,
            "temperature": 0.1, "chat_template_kwargs": {"enable_thinking": False}}
    if tools:
        body["tools"] = TOOLS
        body["tool_choice"] = choice
    data = json.dumps(body).encode()
    for i in range(retries + 1):
        try:
            req = urllib.request.Request(URL, data, {
                "Content-Type": "application/json", "Authorization": "Bearer " + KEY})
            with urllib.request.urlopen(req, timeout=600) as r:
                return json.loads(r.read())
        except Exception as e:
            if i == retries:
                raise
            print(f"  [retry] {e}")


def execute(root, name, args):
    try:
        if name == "write":
            p = (root / args["path"]).resolve()
            assert root in p.parents or p == root, "fora do workspace"
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(args.get("content", ""), encoding="utf-8")
            return True, f"ok {args['path']}"
        if name == "read":
            p = (root / args["path"]).resolve()
            return True, p.read_text(encoding="utf-8", errors="replace")[:6000]
        if name == "shell":
            r = subprocess.run(["bash", "-lc", args["command"]], cwd=str(root),
                               capture_output=True, text=True, timeout=600)
            return r.returncode == 0, (r.stdout + r.stderr)[:6000]
        if name == "webfetch":
            req = urllib.request.Request(args["url"], headers={"User-Agent": "direct/1"})
            with urllib.request.urlopen(req, timeout=20) as r:
                return True, r.read(20000).decode(errors="replace")[:6000]
        return False, "tool desconhecida: " + name
    except Exception as e:
        return False, f"{type(e).__name__}: {e}"


def converse(root, messages, prompt, max_turns=6, log=None, tag="direct"):
    """Uma etapa: ordem -> calls -> resultados -> continua ate' o modelo parar."""
    messages.append({"role": "user", "content": prompt + "\n\nUse a tool agora."})
    calls = []
    for turn in range(max_turns):
        out = chat(messages, choice="required" if turn == 0 else "auto")
        tps = float((out.get("timings") or {}).get("predicted_per_second") or 0.0)
        if not 0.0 < tps < 500.0:
            tps = 0.0
        msg = (out.get("choices") or [{}])[0].get("message") or {}
        calls_now = msg.get("tool_calls") or []
        publish("/api/test/probe", {
            "step": tag, "ts": int(time.time() * 1000),
            "tools_calls": len(calls) + len(calls_now), "model_requests": 1,
            "tps_output": round(tps, 1), "tps_total": round(tps, 1),
        })
        keep = {"role": "assistant", "content": msg.get("content") or ""}
        if msg.get("tool_calls"):
            keep["tool_calls"] = msg["tool_calls"]
        messages.append(keep)
        if not msg.get("tool_calls"):
            if keep["content"]:
                print(f"    prosa: {keep['content'][:160]}")
            break
        for c in msg["tool_calls"]:
            fn = c.get("function") or {}
            name = fn.get("name") or ""
            try:
                args = json.loads(fn.get("arguments") or "{}")
            except json.JSONDecodeError:
                args = {}
            ok, detail = execute(root, name, args)
            calls.append((name, args, ok))
            print(f"    {name}({json.dumps(args, ensure_ascii=False)[:120]}) -> "
                  f"{'ok' if ok else 'FAIL'} {detail[:100].strip()}")
            messages.append({"role": "tool", "tool_call_id": c.get("id", ""),
                             "name": name, "content": detail})
        if (out.get("choices") or [{}])[0].get("finish_reason") == "length":
            print("    [truncado em max_tokens]")
            break
    return calls


FULL = [
    "inicializar o git nesta pasta",
    "criar data.txt com a data de hoje e adicionar ao git",
    "fazer o commit com a mensagem 'data de hoje' (configure user.name/user.email locais se precisar)",
    "criar a subpasta dados",
    "buscar o clima de Sao Paulo em https://api.open-meteo.com/v1/forecast?latitude=-23.55&longitude=-46.63&current=temperature_2m e gravar a temperatura em dados/clima.txt",
    "gravar a data e a hora do sistema em dados/hora.txt",
    "criar o app console .NET hello com 'dotnet new console -o hello' e deixar o Program.cs imprimindo Hello World",
    "compilar e publicar o hello e juntar o publicado num hello.zip, conferindo com unzip -t",
]
MISSION = [
    "criar o projeto C# scics com 'dotnet new console -n scics' e gravar o Program.cs com integral de Simpson de sin(x) em [0,pi], media/desvio de {2,4,4,4,5,5,7,9} e regressao linear de [(1,2),(2,4),(3,5),(4,4),(5,6)]",
    "compilar e rodar o C# com 'dotnet run --project scics' e corrigir ate' mostrar os 3 numeros",
    "criar o projeto F# scifs com 'dotnet new console -lang F# -n scifs' e portar o mesmo programa",
    "compilar e rodar o F# com 'dotnet run --project scifs' e corrigir ate' rodar",
    "versionar com git: init, user.name/user.email locais, add e commit",
    "gerar entrega.zip com scics e scifs e listar o zip",
]
AZUL = ["Crie no backend .NET C# uma consulta de dados em uma tabela (reembolsos corporativos) "
        "usando repositorio generico com ORM. Escopo: backend de dados. Crie os .cs com classes, "
        "interfaces e repositorios."]
VERDE = ["Implemente o dominio backend C# de um CRUD de reembolsos corporativos (entidades, value "
         "objects, regras de aprovacao por faixa de valor) e o banco SQL: DDL, indices, constraints "
         "e seeds. Valide CPF e CNPJ. Crie os .cs e .sql."]

SUITES = {"full": FULL, "mission": MISSION, "azul": AZUL, "verde": VERDE}


def build_ok(root):
    projs = sorted(root.rglob("*.csproj"))
    if not projs:
        return "sem .csproj"
    r = subprocess.run(["dotnet", "build", str(projs[0]), "-v", "q", "--nologo"],
                       cwd=str(root), capture_output=True, text=True, timeout=600)
    return ("build ok" if r.returncode == 0 else "build FAIL: " +
            ((r.stdout + r.stderr).strip().splitlines() or [""])[-1][:120])


def checks(name, root):
    txt = lambda p: p.read_text(encoding="utf-8", errors="replace") if p.exists() else ""
    log = subprocess.run(["git", "-C", str(root), "log", "--oneline"],
                         capture_output=True, text=True).stdout
    hoje = datetime.now().strftime("%Y-%m-%d")
    if name == "full":
        zipf = root / "hello.zip"
        return [
            ("git", (root / ".git").exists()),
            ("data.txt", hoje in txt(root / "data.txt")),
            ("commit", "data de hoje" in log),
            ("dados/", (root / "dados").is_dir()),
            ("clima", re.search(r"-?\d+([.,]\d+)?\s*(°|C\b|graus)", txt(root / "dados/clima.txt"), re.I) is not None),
            ("hora", hoje in txt(root / "dados/hora.txt") and re.search(r"\d{1,2}:\d{2}", txt(root / "dados/hora.txt")) is not None),
            ("hello.csproj", (root / "hello/hello.csproj").exists()),
            ("Program.cs Hello", "Hello" in txt(root / "hello/Program.cs")),
            ("publish", len(list((root / "publish").glob("hello*.dll"))) > 0),
            ("hello.zip", zipf.exists() and subprocess.run(
                ["unzip", "-t", str(zipf)], capture_output=True, text=True).returncode == 0),
        ]
    if name == "mission":
        return [
            ("scics", (root / "scics/scics.csproj").exists()),
            ("scifs", (root / "scifs/scifs.csproj").exists()),
            ("git", len(log.splitlines()) > 0),
            ("entrega.zip", (root / "entrega.zip").exists()),
        ]
    cs = list(root.rglob("*.cs"))
    sql = list(root.rglob("*.sql"))
    out = [(">=3 .cs", len(cs) >= 3), (">2000 chars", sum(len(txt(p)) for p in cs) > 2000)]
    if name == "verde":
        out.append((">=1 .sql", len(sql) >= 1))
    ok = build_ok(root)
    out.append((ok, True))
    return out


def main():
    global BRIDGE, BRIDGE_KEY
    ap = argparse.ArgumentParser()
    ap.add_argument("--suite", default="full")
    ap.add_argument("--out", default="/tmp/opencode/llama-direct")
    ap.add_argument("--steps", type=int, default=0, help="0 = todas")
    ap.add_argument("--max-tokens", type=int, default=1024)
    ap.add_argument("--bridge", default=BRIDGE, help="para publicar no dashboard")
    ap.add_argument("--bridge-key", default=BRIDGE_KEY)
    a = ap.parse_args()

    BRIDGE, BRIDGE_KEY = a.bridge.rstrip("/"), a.bridge_key

    names = list(SUITES) if a.suite == "all" else [a.suite]
    root_out = Path(a.out) / datetime.now().strftime("%Y%m%d-%H%M%S")
    total_ok = True
    for name in names:
        root = root_out / name
        root.mkdir(parents=True, exist_ok=True)
        steps = SUITES[name][: a.steps] if a.steps else SUITES[name]
        print(f"\n=== {name} ({len(steps)} etapa(s)) em {root} ===")
        t0 = time.time()
        messages = [{"role": "system", "content": "Voce e' um agente que executa com tools. "
                                                  "Responda em pt-br, curto."}]
        calls = []
        for i, p in enumerate(steps, 1):
            print(f"  [{i}/{len(steps)}] {p[:90]}")
            calls += converse(root, messages, p, tag=f"direct/{name}/{i}")
        res = checks(name, root)
        ok = all(c[1] for c in res)
        total_ok &= ok
        secs = round(time.time() - t0, 1)
        publish("/api/test/step", {
            "step": f"llama-direct/{name}", "level": "direct",
            "verdict": "PASS" if ok else "FAIL", "secs": secs, "mode": "direct",
        })
        print(f"  resultado: {'PASS' if ok else 'FAIL'} ({len(calls)} calls, {secs}s)")
        for c in res:
            print(f"    [{'x' if c[1] else ' '}] {c[0]}")
        (root / "result.json").write_text(json.dumps(
            {"suite": name, "passed": ok, "calls": calls, "checks": res,
             "elapsed_s": round(time.time() - t0, 1)}, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"\n{'PASS' if total_ok else 'FAIL'} — {root_out}")
    return 0 if total_ok else 1


if __name__ == "__main__":
    sys.exit(main())
