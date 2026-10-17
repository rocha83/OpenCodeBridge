import json, os, re, subprocess, sys, time, urllib.request

TAG, URL, MODEL = sys.argv[1], sys.argv[2], sys.argv[3]
KEY = "guest@123"
BASE = "/media/mint/3686C649614854E61/Projetos/Git/OpenCodeBridge/bench-mission"
WS = os.path.join(BASE, "adv-" + TAG)
os.makedirs(WS, exist_ok=True)
LOG = open(os.path.join(BASE, f"adv-{TAG}.jsonl"), "w")
REP = os.path.join(BASE, f"ADV-{TAG}.md")

TOOLS = [
    {"type": "function", "function": {"name": "shell", "description": "Executa comando shell no diretorio do projeto",
     "parameters": {"type": "object", "properties": {"command": {"type": "string"}}, "required": ["command"]}}},
    {"type": "function", "function": {"name": "write", "description": "Grava arquivo (path relativo ao projeto)",
     "parameters": {"type": "object", "properties": {"path": {"type": "string"}, "content": {"type": "string"}}, "required": ["path", "content"]}}},
    {"type": "function", "function": {"name": "read", "description": "Le arquivo",
     "parameters": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}}},
    {"type": "function", "function": {"name": "edit", "description": "Substitui trecho exato",
     "parameters": {"type": "object", "properties": {"path": {"type": "string"}, "old": {"type": "string"}, "new": {"type": "string"}}, "required": ["path", "old", "new"]}}},
]
DENY = re.compile(r"(rm\s+-rf\s+/( |$)|mkfs|dd\s+|:\(\)\s*\{|shutdown|reboot|sudo|chmod +777|/etc/|/usr/src|/opt/)")

def inside(p):
    rp = os.path.normpath(os.path.join(WS, p))
    return rp if rp == WS or rp.startswith(WS + os.sep) else None

def do_shell(cmd):
    if DENY.search(cmd): return "NEGADO."
    try:
        r = subprocess.run(cmd, shell=True, cwd=WS, capture_output=True, text=True, timeout=240)
        return f"exit={r.returncode}\n" + (r.stdout + r.stderr)[-4000:]
    except subprocess.TimeoutExpired: return "TIMEOUT."
    except Exception as e: return f"ERRO: {e}"

def do_tool(name, args):
    try:
        if name == "shell": return do_shell(args.get("command", ""))
        if name == "write":
            p = inside(args.get("path", ""));
            if not p: return "NEGADO."
            os.makedirs(os.path.dirname(p) or WS, exist_ok=True)
            open(p, "w").write(args.get("content", "")); return "gravado."
        if name == "read":
            p = inside(args.get("path", ""));
            if not p or not os.path.isfile(p): return "NEGADO/inexistente."
            return open(p).read()[:4000]
        if name == "edit":
            p = inside(args.get("path", ""));
            if not p or not os.path.isfile(p): return "NEGADO/inexistente."
            s = open(p).read()
            if args.get("old", "") not in s: return "TRECHO NAO ACHADO."
            open(p, "w").write(s.replace(args["old"], args["new"], 1)); return "editado."
        return "TOOL DESCONHECIDA."
    except Exception as e: return f"ERRO: {e}"

def ask(messages):
    body = {"model": MODEL, "messages": messages, "max_tokens": 1500, "tools": TOOLS}
    req = urllib.request.Request(URL, data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json", "Authorization": "Bearer " + KEY})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.load(r), time.time() - t0

SYS = ("Voce e engenheiro senior C# e F#. Responda SOMENTE com tool calls "
       "(uma por vez), sem prosa fora das calls. Codigo avancado e idiomatico.")

STAGES = [
    "Use tools: crie projeto C# (dotnet new console -n adcs) e grave Program.cs com: Simpson adaptativo de sin[0,pi]; Monte Carlo de pi com 200k pontos (Random(42)); estatisticas genericas Media/Desvio (IEnumerable<double>); regressao linear retornando tupla (a,b,r2). Main imprime os 5 numeros com 6 casas.",
    "Use tools: compile e rode o C# (dotnet run --project adcs), corrija erros ate sair os 5 numeros.",
    "Use tools: crie projeto F# (dotnet new console -lang F# -n adfs) e porte: Simpson via List.fold, Monte Carlo via Seq, estatisticas com pipeline |> e match, regressao retornando Result<(float*float*float),string>. Main imprime os 5 numeros.",
    "Use tools: compile e rode o F# (dotnet run --project adfs), corrija ate sair os 5 numeros.",
    "Use tools: git init, configure user.name/user.email LOCAIS, add, commit com mensagem.",
    "Use tools: gere entrega.zip (apenas *.cs *.fs *.csproj *.fsproj *.sln, sem bin/obj) e grave RELATORIO.md com os numeros e o estado do git/zip.",
]

msgs = [{"role": "system", "content": SYS}]
stats = {"calls": 0, "ok": 0}
for si, prompt in enumerate(STAGES):
    msgs.append({"role": "user", "content": prompt})
    LOG.write(json.dumps({"stage": si, "prompt": prompt[:80]}) + "\n"); LOG.flush()
    for turn in range(14):
        try: d, dt = ask(msgs)
        except Exception as e:
            LOG.write(json.dumps({"stage": si, "error": str(e)}) + "\n"); LOG.flush(); break
        ch = d["choices"][0]; m = ch["message"]
        LOG.write(json.dumps({"stage": si, "turn": turn, "finish": ch.get("finish_reason"),
            "content": (m.get("content") or "")[:300], "tools": bool(m.get("tool_calls"))}) + "\n"); LOG.flush()
        print(f"{TAG} S{si} t{turn} fin={ch.get('finish_reason')}", flush=True)
        calls = m.get("tool_calls") or []
        if not calls:
            msgs.append({"role": "user", "content": "Sem prosa: emita a proxima tool call agora."})
            if turn >= 3:
                LOG.write(json.dumps({"stage": si, "skip": "sem calls apos 4 turnos"}) + "\n"); LOG.flush(); break
            continue
        msgs.append({"role": "assistant", "content": m.get("content") or "",
                     "tool_calls": [{"id": c.get("id"), "type": "function",
                                     "function": {"name": c["function"]["name"], "arguments": c["function"].get("arguments", "{}")}} for c in calls]})
        for c in calls:
            fn = c["function"]["name"]
            try: args = json.loads(c["function"].get("arguments", "{}"))
            except Exception: args = {}
            stats["calls"] += 1
            res = do_tool(fn, args)
            if not res.startswith(("NEGADO", "ERRO", "TIMEOUT")): stats["ok"] += 1
            LOG.write(json.dumps({"stage": si, "exec": fn, "res": res[:800]}, ensure_ascii=False) + "\n"); LOG.flush()
            msgs.append({"role": "tool", "tool_call_id": c.get("id"), "content": res})
        if ch.get("finish_reason") == "stop" and not calls: break
open(os.path.join(BASE, f"ADV-{TAG}.md"), "w").write(
    f"# ADV-{TAG} ({MODEL})\n\n- tool_calls: {stats['calls']} (ok {stats['ok']})\n- ws: {WS}\n- log: adv-{TAG}.jsonl\n")
LOG.close()
print(f"FIM-{TAG} calls={stats['calls']} ok={stats['ok']}", flush=True)
