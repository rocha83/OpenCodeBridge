import json, os, re, subprocess, sys, time, urllib.request

TAG, URL, MODEL = sys.argv[1], sys.argv[2], sys.argv[3]
KEY = "guest@123"
BASE = "/media/mint/3686C649614854E61/Projetos/Git/OpenCodeBridge/bench-mission"
WS = os.path.join(BASE, "agent-" + TAG)
os.makedirs(WS, exist_ok=True)
LOG = open(os.path.join(BASE, f"mission-{TAG}.jsonl"), "w")
REP = open(os.path.join(BASE, f"REPORT-{TAG}.md"), "w")

TOOLS = [
    {"type": "function", "function": {"name": "shell", "description": "Executa comando shell no diretorio do projeto",
     "parameters": {"type": "object", "properties": {"command": {"type": "string"}}, "required": ["command"]}}},
    {"type": "function", "function": {"name": "write", "description": "Grava arquivo (path relativo ao projeto)",
     "parameters": {"type": "object", "properties": {"path": {"type": "string"}, "content": {"type": "string"}}, "required": ["path", "content"]}}},
    {"type": "function", "function": {"name": "read", "description": "Le arquivo",
     "parameters": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}}},
    {"type": "function", "function": {"name": "edit", "description": "Substitui trecho exato",
     "parameters": {"type": "object", "properties": {"path": {"type": "string"}, "old": {"type": "string"}, "new": {"type": "string"}}, "required": ["path", "old", "new"]}}},
    {"type": "function", "function": {"name": "glob", "description": "Lista arquivos por padrao",
     "parameters": {"type": "object", "properties": {"pattern": {"type": "string"}}, "required": ["pattern"]}}},
    {"type": "function", "function": {"name": "grep", "description": "Busca regex em arquivos",
     "parameters": {"type": "object", "properties": {"pattern": {"type": "string"}}, "required": ["pattern"]}}},
]
DENY = re.compile(r"(rm\s+-rf\s+/( |$)|mkfs|dd\s+|: *\(\) *\{|shutdown|reboot|sudo|chmod +777|/etc/|/usr/src|/opt/)")

def inside(p):
    rp = os.path.normpath(os.path.join(WS, p))
    return rp if rp == WS or rp.startswith(WS + os.sep) else None

def do_shell(cmd):
    if DENY.search(cmd): return "NEGADO (fora do escopo seguro)."
    try:
        r = subprocess.run(cmd, shell=True, cwd=WS, capture_output=True, text=True, timeout=180)
        out = (r.stdout + r.stderr)[-4000:]
        return f"exit={r.returncode}\n{out}"
    except subprocess.TimeoutExpired: return "TIMEOUT 180s."
    except Exception as e: return f"ERRO driver: {e}"

def do_tool(name, args):
    try:
        if name == "shell": return do_shell(args.get("command", ""))
        if name == "write":
            p = inside(args.get("path", "")); 
            if not p: return "NEGADO (path fora do projeto)."
            os.makedirs(os.path.dirname(p) or WS, exist_ok=True)
            open(p, "w").write(args.get("content", "")); return f"gravado {os.path.relpath(p, WS)} ({len(args.get('content',''))} chars)"
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
        if name == "glob":
            import glob as g
            return "\n".join(sorted(os.path.relpath(x, WS) for x in g.glob(os.path.join(WS, args.get("pattern", "*")))[:50])) or "(vazio)"
        if name == "grep":
            import glob as g
            out = []
            for f in g.glob(os.path.join(WS, "**"), recursive=True)[:200]:
                if os.path.isfile(f):
                    try:
                        for i, l in enumerate(open(f, errors="ignore")):
                            if re.search(args.get("pattern", ""), l): out.append(f"{os.path.relpath(f,WS)}:{i+1}:{l.strip()[:120]}")
                    except Exception: pass
                    if len(out) > 30: break
            return "\n".join(out) or "(sem match)"
        return f"TOOL DESCONHECIDA: {name}"
    except Exception as e: return f"ERRO driver: {e}"

def ask(messages, tools, mx=1500):
    body = {"model": MODEL, "messages": messages, "max_tokens": mx}
    if tools: body["tools"] = tools
    req = urllib.request.Request(URL, data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json", "Authorization": "Bearer " + KEY})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.load(r), time.time() - t0

STAGES = [
    ("TEXTO", "Responda apenas em TEXTO, sem tool calls: apresente o plano do programa cientifico (integracao de Simpson de sin(x) em [0,pi]; media e desvio-padrao de uma amostra; regressao linear por minimos quadrados) a ser implementado em C# console e F# console."),
    ("TOOLS", "Use tools EXPLICITAMENTE (somente tool calls, sem prosa fora delas): crie o projeto C# com `dotnet new console -n scics` e grave o Program.cs implementando integral de Simpson de sin(x) em [0,pi], media/desvio de {2,4,4,4,5,5,7,9} e regressao linear de [(1,2),(2,4),(3,5),(4,4),(5,6)]. Imprima os 3 resultados."),
    ("TOOLS", "Use tools EXPLICITAMENTE: compile e rode o C# (`dotnet run --project scics`), corrija erros ate rodar e mostrar os 3 numeros."),
    ("TOOLS", "Use tools EXPLICITAMENTE: crie o projeto F# com `dotnet new console -lang F# -n scifs` e porte o mesmo programa (Program.fs)."),
    ("TOOLS", "Use tools EXPLICITAMENTE: compile e rode o F# (`dotnet run --project scifs`), corrija ate rodar."),
    ("TOOLS", "Use tools EXPLICITAMENTE: versione com git (git init, configure user.name/user.email LOCAIS do repo, add, commit)."),
    ("TOOLS", "Use tools EXPLICITAMENTE: gere o zip da pasta (`zip -r entrega.zip scics scifs`) e liste o zip."),
    ("TEXTO", "Responda apenas em TEXTO, sem tool calls: relatorio final do que foi construido, os 3 numeros obtidos e o estado do git/zip."),
]

msgs = []
stats = {"tool_calls": 0, "tool_ok": 0, "stages": 0, "tps": []}
for si, (mode, prompt) in enumerate(STAGES):
    msgs.append({"role": "user", "content": prompt})
    LOG.write(json.dumps({"stage": si, "mode": mode, "prompt": prompt}, ensure_ascii=False) + "\n"); LOG.flush()
    for turn in range(12):
        try:
            d, dt = ask(msgs, TOOLS if mode == "TOOLS" else None)
        except Exception as e:
            LOG.write(json.dumps({"stage": si, "error": str(e)}) + "\n"); LOG.flush(); break
        ch = d["choices"][0]
        msg = ch["message"]
        u = d.get("usage", {})
        ct = u.get("completion_tokens", 0)
        if dt > 0: stats["tps"].append(ct / dt)
        LOG.write(json.dumps({"stage": si, "turn": turn, "finish": ch.get("finish_reason"),
            "content": (msg.get("content") or "")[:2000], "tool_calls": msg.get("tool_calls"),
            "usage": u, "elapsed": round(dt, 1)}, ensure_ascii=False) + "\n"); LOG.flush()
        print(f"{TAG} S{si} t{turn} fin={ch.get('finish_reason')} out={ct} {dt:.0f}s", flush=True)
        calls = msg.get("tool_calls") or []
        if not calls:
            if msg.get("content"): msgs.append({"role": "assistant", "content": msg["content"]})
            break
        msgs.append({"role": "assistant", "content": msg.get("content") or "",
                     "tool_calls": [{"id": c.get("id"), "type": "function",
                                     "function": {"name": c["function"]["name"], "arguments": c["function"].get("arguments", "{}")}} for c in calls]})
        for c in calls:
            fn = c["function"]["name"]
            try: args = json.loads(c["function"].get("arguments", "{}"))
            except Exception: args = {}
            stats["tool_calls"] += 1
            res = do_tool(fn, args)
            if not res.startswith(("NEGADO", "ERRO", "TIMEOUT")): stats["tool_ok"] += 1
            LOG.write(json.dumps({"stage": si, "exec": fn, "args": str(args)[:500], "result": res[:1500]}, ensure_ascii=False) + "\n"); LOG.flush()
            msgs.append({"role": "tool", "tool_call_id": c.get("id"), "content": res})
    stats["stages"] += 1
tps = stats["tps"]
REP.write(f"# REPORT-{TAG} ({MODEL})\n\n- stages: {stats['stages']}/8, tool_calls: {stats['tool_calls']} (ok {stats['tool_ok']})\n- t/s medio: {sum(tps)/len(tps):.1f} (n={len(tps)})\n")
REP.write(f"- ws: {WS}\n- log: mission-{TAG}.jsonl\n")
REP.close(); LOG.close()
print(f"FIM-{TAG}", flush=True)
