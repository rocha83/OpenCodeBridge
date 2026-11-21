#!/usr/bin/env python3
"""Coletor: proxy opencode -> llama (:4100) que mede o que o Grafana mostra.

- Repassa qualquer request e, ao mesmo tempo, extrai: endpoint, model, numero de
  mensagens do request, enunciado (ultima mensagem do usuario), tokens de prompt
  e de saida e a velocidade (tokens/s) que o proprio llama reporta em `timings`.
- Expoe tudo em Prometheus em :9091/metrics e grava um JSONL com os enunciados.

Nao e' bridge: nao altera payload, nao inventa argumento, nao faz retry.

REGRA IRREFUTAVEL (tunel python): REASONING PROIBIDO. Todo POST /v1/chat* sai com
chat_template_kwargs.enable_thinking=False, mesmo que o cliente peca true — sem o
kwarg o llama-server LIGA o thinking por padrao. Contador: thinking_blocked.
"""
import atexit, difflib, http.server, json, os, re, signal, socketserver, sys, threading, time, urllib.error, urllib.request
from collections import Counter, deque

try:                                  # tabela de hints compartilhada (hints.py)
    import hints as _hints
except Exception:                     # ausente/quebrada: o tunel continua passthrough
    _hints = None

try:                                  # harness montado a partir de harness/*.md
    import harness_loader as _harness_loader
except Exception:                     # ausente: o tunel segue com o system do cliente
    _harness_loader = None

# assembled = substitui o system do cliente pelo texto montado (default);
# off = repassa o system do cliente intacto.
HARNESS_MODE = str(os.environ.get("HARNESS_MODE", "assembled") or "assembled").strip().lower()
HARNESS_STACK = os.environ.get("HARNESS_STACK", "base")

UP = os.environ.get("UPSTREAM", "http://127.0.0.1:4100")
PORT = int(os.environ.get("PORT", "4199"))
METRICS_PORT = int(os.environ.get("METRICS_PORT", "9091"))
EVENTS = os.environ.get("EVENTS", "/media/mint/FT/logs/collector/metrics/events.jsonl")
CASES = os.environ.get("CASES", "/media/mint/FT/logs/collector/metrics/cases.jsonl")
RESP_DIR = os.environ.get("RESP_DIR", "/media/mint/FT/logs/collector/metrics/responses")
CURRENT = os.environ.get("CURRENT", "/media/mint/FT/logs/collector/metrics/current.json")
# Sem request por IDLE_S segundos, as gauges "vivas" voltam a zero (nada rodando
# nao deve aparecer como numero congelado no painel).
IDLE_S = int(os.environ.get("IDLE_S", "60"))


GEN_SKIP_DIRS = {".git", "obj", "bin", "node_modules", ".data", ".battery-bak", "__pycache__"}
GEN_SKIP_FILES = {"opencode.json", ".last.txt"}


def _read_current():
    try:
        with open(CURRENT, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:
        return {}


def _count_generated(root):
    """Conta arquivos do workspace do caso atual, sem artefatos de harness/build."""
    if not root or not os.path.isdir(root):
        return 0
    total = 0
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in GEN_SKIP_DIRS]
        for name in filenames:
            if name in GEN_SKIP_FILES or name.endswith(".pyc"):
                continue
            total += 1
    return total


def _latest_cases():
    latest = {}
    try:
        with open(CASES, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    row = json.loads(line)
                except Exception:
                    continue
                case = row.get("case")
                if case:
                    latest[case] = row
    except OSError:
        pass
    return latest

# Anti-repeat enxuto: no modo "nudge" injeta um aviso no request; "stop" tambem corta o
# laco de tool (nao repassa ao llama e devolve uma resposta sintetica).
ANTI = os.environ.get("ANTI_REPEAT", "stop")          # nudge | stop | off
NUDGE_AT = int(os.environ.get("NUDGE_AT", "3"))        # mesma tool+args 3x
NUDGE_MAX = int(os.environ.get("NUDGE_MAX", "5"))      # teto de nudges por conversa
STOP_AT = int(os.environ.get("STOP_AT", "6"))          # 6x = corta
# Porte do ToolsBreaker do OpenCodeBridge (C#): conta FALHAS no historico de
# resultados que o cliente reenvia, nao a assinatura da chamada. 3 = aviso,
# 6 = curto-circuito (nao chama o modelo). Ver EvaluateFailureLoop.
BREAKER_SOFT = int(os.environ.get("BREAKER_SOFT", "3"))
BREAKER_HARD = int(os.environ.get("BREAKER_HARD", "6"))
# Reescrita do MESMO arquivo (`write` varias vezes, sempre "sucesso") fura o anti-repeat
# de assinatura porque a sig inclui o conteudo: 1 byte diferente zera o streak.
# Aqui a contagem e por PATH (o que de fato importa) e corta no churn.
REWRITE_STOP = int(os.environ.get("REWRITE_STOP", "5"))   # 5 writes no mesmo path = corta
NUDGES = STOPS = 0
NUDGE_TOTAL = {}   # conv -> nudges injetados (teto NUDGE_MAX)
BREAKER_TRIPS = BREAKER_NUDGES = 0   # ToolsBreaker (porte do C#)
HINTS = 0             # alertas [build-hint] injetados (erro de compilacao + exemplo)
TOOL_HINTS = 0        # alertas [tool-hint] injetados (args fora do schema)
HINT_SEEN = {}        # conversa -> assinaturas de erro ja alertadas (anti-spam)
BUILD_HINT = os.environ.get("BUILD_HINT", "on").strip().lower()
HINT_MAX = int(os.environ.get("HINT_MAX", "30"))  # alertas por conversa (hints distintos)
HINT_LAST = {}        # conversa -> (assinatura atual, repeticoes seguidas)
HINT_TOTAL = {}       # conversa -> total de alertas injetados
THINK_BLOCKED = 0   # requests que pediram reasoning e foram forcados a thinking off
RECENT_N = int(os.environ.get("RECENT_N", "30"))

# Sonda de repeticao/devagacao: janela 256 caracteres normalizados (mesma faixa
# que a bridge usa como referencia de observacao) e marcas de prosa de plano.
WIN = 256
META = ("vou ", "agora vou", "em seguida", "proximo passo", "vamos ", "plano:")


LOCK = threading.Lock()
REQ = ERR = MSG = PROMPT_T = OUT_T = RESP_T = TOOL_CALLS = 0
# Mensagens de conversa (user + assistant), sem tool calls/resultados:
# contadas por DELTA da lista de mensagens do request; tools ficam em TOOL_CALLS.
USER_MSGS = ASSIST_MSGS = 0
PREV_MSGS = {}  # conversa -> assinaturas ja vistas (para contar so o delta)
TPS = 0.0
LAST_SIM = 0.0
LAST = {}  # prompt -> (ts_seen, n_messages)
# Repeticao de ENUNCIADO: mesma prompt repetida em sequencia = ciclo degenerado
# (o llama devolve o mesmo erro, o opencode reenvia o mesmo enunciado, e assim vai).
LAST_PROMPT = ""
STREAK = 0
# Repeticao de TOOL CALL: mesma tool com os mesmos args em sequencia. Num modelo
# que responde so tool_calls (texto vazio) a devagacao aparece AQUI, nao na prosa.
LAST_SIG = ""
SIG_STREAK = 0
SIG_MAX = 0
# Ciclo de 2 assinaturas (A,B,A,B): a assinatura ALTERNA, entao SIG_STREAK nunca
# passa de 1 e o laco escapa do contador consecutivo. SIG_HIST guarda o sufixo
# recente; CYCLE conta quantos requests seguidos usam no maximo 2 assinaturas.
SIG_HIST = deque(maxlen=12)
CYCLE = 0
CYCLE_MAX = 0
DUMP_N = 0
PROBE = {"tail_sim": 0.0, "tail_reps": 0, "dup_lines": 0, "meta": 0, "chars": 0}
# Observer em memoria: a rodada inteira fica em RUN e so vai para o disco no
# dump (fim da rodada, POST /api/dump, SIGTERM ou atexit). Nada de escrever por
# request.
RUN = {"id": time.strftime("%Y%m%d-%H%M%S"), "started": time.time(), "requests": []}
# Janela dos ultimos requests (tabela do painel) e controle de ociosidade.
RECENT = deque(maxlen=200)
# Ultima rodada congelada: quando o collector fica ocioso, RECENT e' limpo (gauges
# vivas zeradas) mas a TABELA do painel continua mostrando a ultima rodada.
LAST_RUN_ROWS = []
SEQ = 0
LAST_REQ_TS = 0.0
IDLED = True


def _text(v):
    if isinstance(v, str):
        return v
    if isinstance(v, list):
        return " ".join(_text(x) for x in v)
    if isinstance(v, dict):
        return _text(v.get("content") or v.get("text") or "")
    return ""


def _msg_sig(m):
    """Assinatura estavel de uma mensagem para achar o delta entre requests."""
    if not isinstance(m, dict):
        return ("", hash(_text(m)))
    role = str(m.get("role") or "")
    return (role, hash(_text(m.get("content"))))


def _conv_key(msgs):
    """Identifica a conversa (system + primeiro user) para nao misturar casos."""
    system, first_user = "", ""
    for m in msgs:
        if not isinstance(m, dict):
            continue
        role = m.get("role") or ""
        if role in ("system", "developer") and not system:
            system = _text(m.get("content"))[:200]
        elif role == "user" and not first_user:
            first_user = _text(m.get("content"))[:200]
            break
    return (system + "\x00" + first_user)[:500]


def _new_user_count(msgs):
    """Quantas mensagens *novas* de role=user entraram neste request.

    A lista do request e' cumulativa; comparar com a anterior evita contar o
    contexto inteiro a cada chamada (o bug do painel 'Mensagens').
    """
    if not isinstance(msgs, list) or not msgs:
        return 0
    key = _conv_key(msgs)
    sigs = [_msg_sig(m) for m in msgs]
    prev = PREV_MSGS.get(key, [])
    k = 0
    for a, b in zip(prev, sigs):
        if a != b:
            break
        k += 1
    if not prev:
        new = msgs
    elif len(sigs) >= len(prev) and k == len(prev):
        new = msgs[len(prev):]
    elif k:
        new = msgs[k:]
    else:
        # contexto compactado/trocado: nao inventa mensagens novas
        new = []
    PREV_MSGS[key] = sigs
    while len(PREV_MSGS) > 64:
        PREV_MSGS.pop(next(iter(PREV_MSGS)))
    return sum(1 for m in new if isinstance(m, dict) and (m.get("role") == "user")
               and not m.get("x-injected"))


def _prompt_of(body):
    msgs = body.get("messages")
    if not isinstance(msgs, list):
        msgs = body.get("input") if isinstance(body.get("input"), list) else []
    if not msgs:
        return "", 1
    last = ""
    for m in msgs:
        if isinstance(m, dict) and m.get("x-injected"):
            continue          # alerta sintetico nao e' o enunciado do caso
        t = _text(m.get("content") if isinstance(m, dict) else m)
        if t.strip():
            last = t
    return last.strip().replace("\n", " ")[:120], len(msgs)


def _parse(raw):
    txt = raw.decode("utf-8", "ignore")
    usage, timings = {}, {}
    for line in txt.splitlines():
        line = line.strip()
        if line.startswith("data:"):
            line = line[5:].strip()
        if not line or line == "[DONE]":
            continue
        try:
            obj = json.loads(line)
        except Exception:
            continue
        if isinstance(obj, dict):
            timings = obj.get("timings") or timings
            usage = obj.get("usage") or usage
            if obj.get("type") == "response.completed":
                usage = obj.get("response", {}).get("usage") or usage
    if not usage and not timings:
        try:
            obj = json.loads(txt)
            timings, usage = obj.get("timings") or {}, obj.get("usage") or {}
        except Exception:
            pass
    return usage, timings


def _norm(s):
    return re.sub(r"\s+", " ", (s or "").lower()).strip()


def _response_of(raw):
    """Texto do assistente, tools chamadas, argumentos das tools e finish/status
    (stream SSE ou JSON)."""
    txt = raw.decode("utf-8", "ignore").strip()
    text, tools, calls, think, finish = "", [], "", "", ""
    objs = []
    if txt.startswith("{"):
        try:
            objs = [json.loads(txt)]
        except Exception:
            objs = []
    else:
        for line in txt.splitlines():
            line = line.strip()
            if line.startswith("data:"):
                line = line[5:].strip()
            if not line or line == "[DONE]":
                continue
            try:
                objs.append(json.loads(line))
            except Exception:
                pass
    for o in objs:
        if not isinstance(o, dict):
            continue
        for ch in (o.get("choices") or []):
            d = ch.get("delta") or ch.get("message") or {}
            if d.get("content"):
                text += _text(d.get("content"))
            for k in ("reasoning_content", "reasoning"):
                if d.get(k):
                    think += _text(d.get(k))
            for tc in (d.get("tool_calls") or []):
                fn = (tc.get("function") or {})
                if fn.get("name"):
                    tools.append(fn["name"])
                if fn.get("arguments"):
                    calls += fn["arguments"]
            if ch.get("finish_reason"):
                finish = ch["finish_reason"]
        if o.get("type") == "response.output_text.delta":
            text += o.get("delta") or ""
        elif o.get("type") in ("response.reasoning_summary_text.delta", "response.reasoning_text.delta"):
            think += o.get("delta") or ""
        for item in ((o.get("response") or {}).get("output") or o.get("output") or []):
            if item.get("type") == "message":
                text += _text(item.get("content"))
            elif item.get("type") == "reasoning":
                think += _text(item.get("summary") or item.get("content"))
            elif item.get("type") == "function_call":
                tools.append(item.get("name") or "?")
                if item.get("arguments"):
                    calls += item["arguments"]
        if o.get("status"):
            finish = o["status"]
    return text, tools, calls, think, finish


def _probe(resp):
    """Observacao (nao contem nada): cauda vs janela anterior, repeticoes da cauda,
    linha mais repetida e marcas de prosa de plano (devagacao)."""
    t = _norm(resp)
    sim, reps = 0.0, 0
    if len(t) >= 2 * WIN:
        sim = difflib.SequenceMatcher(None, t[-WIN:], t[-2 * WIN:-WIN]).ratio()
        reps = t[:-WIN].count(t[-WIN:])
    lines = [l.strip() for l in (resp or "").splitlines() if len(l.strip()) >= 8]
    # Janela por LINHAS: imune a fase da janela de caracteres (loop de build, por ex.)
    blk = 4
    if len(lines) >= 2 * blk:
        sim = max(sim, difflib.SequenceMatcher(None, "\n".join(lines[-blk:]),
                                               "\n".join(lines[-2 * blk:-blk])).ratio())
    return {"tail_sim": round(sim, 3), "tail_reps": reps,
            "dup_lines": Counter(lines).most_common(1)[0][1] if lines else 0,
            "meta": sum(t.count(m) for m in META)}


def _dump(text, kind):
    """Guarda o texto do modelo (inclusive de subagente) para inspecao posterior."""
    global DUMP_N
    try:
        os.makedirs(RESP_DIR, exist_ok=True)
        with LOCK:
            DUMP_N += 1
            n = DUMP_N
        name = f"{int(time.time() * 1000)}-{n:05d}-{kind}.txt"
        with open(os.path.join(RESP_DIR, name), "w") as f:
            f.write(text[:400_000])
    except Exception:
        pass



REQ_DIR = os.environ.get("REQ_DIR", "")
REQ_N = 0


def _dump_req(path, body, orig_system=""):
    """Sonda: grava o request (system do cliente + corpo montado); so leitura."""
    global REQ_N
    if not REQ_DIR:
        return
    try:
        os.makedirs(REQ_DIR, exist_ok=True)
        with LOCK:
            REQ_N += 1
            n = REQ_N
        name = "%d-%04d.json" % (int(time.time() * 1000), n)
        with open(os.path.join(REQ_DIR, name), "w") as f:
            json.dump({"path": path, "orig_system": orig_system, "body": body}, f,
                      ensure_ascii=False)
    except Exception:
        pass


def record(endpoint, body, raw, flags=None):
    global REQ, ERR, MSG, PROMPT_T, OUT_T, TPS, TOOL_CALLS, LAST_PROMPT, STREAK
    global LAST_REQ_TS, IDLED, SEQ, LAST_SIG, SIG_STREAK, SIG_MAX, CYCLE, CYCLE_MAX
    global USER_MSGS, ASSIST_MSGS
    usage, timings = _parse(raw)
    prompt, nmsg = _prompt_of(body)
    msgs = body.get("messages") or body.get("input") or []
    pt = usage.get("prompt_tokens") or usage.get("input_tokens") or 0
    ct = usage.get("completion_tokens") or usage.get("output_tokens") or 0
    tps = float(timings.get("predicted_per_second") or 0)
    # llama-server devolve 1e6 quando predicted_ms=0 (resposta vazia/sintetica):
    # nao e' velocidade, e' sentinela. Descarta para nao poluir media/maximo.
    if not (0 < tps < 500):
        tps = 0.0
    resp, resp_tools, resp_calls, think, finish = _response_of(raw)
    probe = _probe(resp)
    probe_t = _probe(think)
    streak = 0
    with LOCK:
        new_user = _new_user_count(msgs)
        assistant_msg = 1 if resp.strip() else 0
        REQ += 1
        USER_MSGS += new_user
        ASSIST_MSGS += assistant_msg
        MSG += new_user + assistant_msg
        PROMPT_T += int(pt)
        OUT_T += int(ct)
        TOOL_CALLS += len(resp_tools)
        if tps:
            TPS = tps
        if prompt:
            LAST[prompt] = (time.time(), nmsg)
            while len(LAST) > 30:
                LAST.pop(min(LAST, key=lambda k: LAST[k][0]))
            # ciclo degenerado: mesmo enunciado reenviado em sequencia
            STREAK = STREAK + 1 if prompt == LAST_PROMPT else 1
            LAST_PROMPT = prompt
            streak = STREAK
        PROBE.update(probe)
        PROBE["chars"] = len(resp)
        PROBE["think_chars"] = len(think)
        PROBE["think_sim"] = probe_t["tail_sim"]
        PROBE["think_dup"] = probe_t["dup_lines"]
        PROBE["think_meta"] = probe_t["meta"]
        LAST_REQ_TS = time.time()
        IDLED = False
        SEQ += 1
        cur = _read_current()
        RECENT.append({"seq": SEQ, "prompt": prompt[:60], "streak": streak,
                       "case": cur.get("case", "") or "",
                       "status": cur.get("status", "") or "",
                       "response": resp[:500],
                       "resp_chars": len(resp), "think_chars": len(think),
                       "tail_sim": probe["tail_sim"], "dup": probe["dup_lines"],
                       "meta": probe["meta"], "tools": ",".join(resp_tools[:3]),
                       "tsig": SIG_STREAK, "finish": finish})
    payload = (resp + (("\n[thinking] " + think) if think else "")
               + (("\n[args] " + resp_calls) if resp_calls else ""))
    if payload.strip():
        _dump(payload, endpoint.strip("/").replace("/", "_") or "resp")
    os.makedirs(os.path.dirname(EVENTS), exist_ok=True)
    sys_chars = 0
    msgs = body.get("messages") or body.get("input") or []
    if isinstance(body.get("instructions"), str):
        sys_chars = len(body["instructions"])
    if isinstance(msgs, list):
        for m in msgs:
            if isinstance(m, dict) and m.get("role") in ("system", "developer"):
                sys_chars = max(sys_chars, len(_text(m.get("content"))))
    # assinatura da chamada de tool (nome + args normalizados): o laco de tool
    # repete a MESMA assinatura com o mesmo erro de retorno.
    sig = ("|".join(resp_tools) + "::" + re.sub(r"\s+", " ", resp_calls).strip()) if resp_tools else ""
    with LOCK:
        if sig and sig == LAST_SIG:
            SIG_STREAK += 1
        else:
            SIG_STREAK = 1 if sig else 0
        LAST_SIG = sig
        SIG_MAX = max(SIG_MAX, SIG_STREAK)
        if sig:
            SIG_HIST.append((sig, bool(ERR_HINT.search(prompt or ""))))
            CYCLE = _cycle_len()
        else:
            CYCLE = 0
        CYCLE_MAX = max(CYCLE_MAX, CYCLE)
    ev = {"ts": round(time.time(), 3), "endpoint": endpoint,
          "model": body.get("model"), "messages": nmsg,
          "user_messages": new_user, "assistant_messages": assistant_msg,
          "prompt": prompt,
          "tools": len(body.get("tools") or []), "system_chars": sys_chars,
          "prompt_tokens": pt, "completion_tokens": ct, "tps": tps,
          # sonda de repeticao/devagacao (observacao, nao contencao)
          "prompt_streak": streak, "resp_chars": len(resp),
          "resp_tools": resp_tools[:8], "finish": finish,
          "tool_args": resp_calls[:300],
          "think_chars": len(think), "think_tail_sim": probe_t["tail_sim"],
          "think_dup_lines": probe_t["dup_lines"], "think_meta": probe_t["meta"],
          "tail_sim": probe["tail_sim"], "tail_reps": probe["tail_reps"],
          "dup_lines": probe["dup_lines"], "meta": probe["meta"],
          # sonda de laco de tool (o que a prosa nao mostra)
          "tool_sig": sig[:200], "tool_sig_streak": SIG_STREAK,
          "tool_sig_max": SIG_MAX}
    if flags:
        ev.update(flags)
    with LOCK:
        RUN["requests"].append(ev)   # so memoria; disco apenas no dump


NUDGE_TOOL = ("[anti-repeat] A chamada {sig} ja se repetiu {n}x seguidas com o MESMO resultado. "
              "NAO repita essa chamada. Confira o caminho/estado real (ls, find, cat) e siga por "
              "outro caminho; se o comando ja falhou assim, ele nao vai funcionar de novo.")
NUDGE_TEXT = ("[anti-repeat] A resposta anterior repetiu o mesmo trecho/plano. NAO repita o que ja "
              "foi dito. Va direto ao proximo passo concreto.")


# Marcas de FALHA no retorno da tool (o que chega como ultima mensagem no proximo request).
# read<->write alternado e' edicao normal; build<->edit alternado COM erro e' laco.
ERR_HINT = re.compile(r"MSB\d{4}|Could not find oldString|error CS\d+|Exited with code [1-9]|"
                      r"tool.execution")

# ---------------------------------------------------------------------------
# Porte do ToolsBreaker (Rochas.OpenCodeBridge.Core.ToolsAntiRepeat).
# O vies do collector antigo: comparar a ASSINATURA da chamada (tool+args) e so
# a ultima (consecutiva). Isso perde 3 lacoes reais medidos:
#   1. "Found 9 matches for oldString" (args minimos, mudam a cada volta)
#   2. mesmo erro de compilacao com a LINHA mudando (assinatura diferente)
#   3. edit<->build alternado (streak nunca passa de 1)
# O breaker olha o RESULTADO da tool no historico e conta falhas por alvo/erro.
# ---------------------------------------------------------------------------
FAIL_RE = re.compile(r"(?i)(error\s+[A-Z]{2}\d{4}|MSB\d{4}|NU\d{4}|Could not find oldString|"
                     r"Found \d+ matches|\bInvalid arguments\b|tool\.execution|"
                     r"permission denied|access denied|no such file or directory|"
                     r"n[aã]o encontrado|could not find|command not found|"
                     r"\bfailed\b|Exited with code [1-9])")
OK_RE = re.compile(r"(?i)(0\s+Erro\(s\)|0\s+Error\(s\)|Build succeeded|Compila[çc][aã]o com [êe]xito)")


def _looks_failure(txt):
    """Falha de tool: marca de erro presente e sem sinal de sucesso (0 Erro(s))."""
    t = txt or ""
    if not t.strip():
        return False
    return bool(FAIL_RE.search(t)) and not bool(OK_RE.search(t))


def _norm_target(name, args):
    """Alvo estavel: mesmo arquivo/comando com numeros e espacos normalizados."""
    a = re.sub(r"\d+", "#", args or "")
    a = re.sub(r"\s+", "", a)
    return (name or "").strip().lower() + "::" + a[:140]


def _failure_key(txt):
    """Assinatura do ERRO sem caminho/posicao/numero: 'mesmo erro, outra linha' agrupa."""
    k = re.sub(r"\([^)\r\n]*\)", "()", txt or "")
    k = re.sub(r"[A-Za-z]:[\\/][^\s:;]+", "<path>", k)
    k = re.sub(r"(?:\.{0,2}/)?(?:[\w.\-]+/)+[\w.\-]+\.(?:cs|fs|sql|tsx?|jsx?|py|json|csproj)", "<file>", k)
    k = re.sub(r"[A-Za-z0-9_\-]*\d[A-Za-z0-9_\-]*", "<id>", k)
    k = re.sub(r"\s+", " ", k).strip()
    return k[:180]


def _tool_history(msgs):
    """[(tool, args, resultado)] na ordem do historico, tolerante a OpenAI e a partes."""
    hist, pending = [], []
    for m in msgs or []:
        if not isinstance(m, dict):
            continue
        role = str(m.get("role") or "").lower()
        if role == "assistant":
            for tc in (m.get("tool_calls") or []):
                fn = (tc or {}).get("function") or {}
                pending.append((fn.get("name") or "", fn.get("arguments") or ""))
            cont = m.get("content")
            if isinstance(cont, list):
                for p in cont:
                    if not isinstance(p, dict):
                        continue
                    t = p.get("type")
                    if t in ("tool-call", "tool_call", "function_call", "tool_use"):
                        pending.append((p.get("name") or p.get("toolName") or "",
                                        json.dumps(p.get("input") or p.get("args") or {},
                                                   ensure_ascii=False)))
                    elif t in ("tool_result", "tool-result", "tool_result_part"):
                        nm, ar = pending.pop(0) if pending else ("", "")
                        hist.append((nm, ar, _text(p.get("content") or p.get("result") or p.get("output"))))
        elif role in ("tool", "function"):
            nm, ar = pending.pop(0) if pending else ("", "")
            hist.append((nm, ar, _text(m.get("content"))))
    return hist


def _breaker_info(msgs):
    """Diagnostico do loop: contagem max(streak, mesmo erro, mesmo alvo) + alvos + falhas."""
    hist = _tool_history(msgs)
    info = {"cont": 0, "alvos": 0, "fails": [], "hist": hist, "mesma": 0, "streak": 0}
    if not hist:
        return info
    streak = 0
    for _n, _a, c in reversed(hist):
        if not _looks_failure(c):
            break
        streak += 1
    keys, fails = {}, []
    for nm, ar, c in hist:
        if not _looks_failure(c):
            continue
        fails.append((nm, ar, c))
        k = _failure_key(c)
        if k:
            keys[k] = keys.get(k, 0) + 1
    mesma = max(keys.values()) if keys else 0
    janela = max(BREAKER_HARD, BREAKER_SOFT) * 2
    alvo, do_alvo = None, 0
    for _nm, _ar, c in reversed(hist[-janela:]):
        if not _looks_failure(c):
            continue
        tg = _norm_target(_nm, _ar)
        if alvo is None:
            alvo = tg
        if tg == alvo:
            do_alvo += 1
    alvos = len({_norm_target(nm, ar) for nm, ar, _c in fails})
    info.update({"cont": max(streak, mesma, do_alvo), "alvos": alvos, "fails": fails,
                 "mesma": mesma, "streak": streak})
    return info


def _breaker_scan(msgs):
    """(contagem, alvos) — atalho de `_breaker_info`."""
    i = _breaker_info(msgs)
    return i["cont"], i["alvos"]


def _degenerate_edit(fails):
    """Direcionamento cirurgico quando o `edit` falhou com oldString curto/ambiguo (ex.: ';')."""
    for tool, args, content in reversed(fails):
        if (tool or "").strip().lower() not in ("edit", "edit_file"):
            continue
        if not re.search(r"(?i)(Found \d+ matches|Could not find oldString)", content or ""):
            continue
        try:
            obj = json.loads(args or "{}")
        except (TypeError, ValueError):
            return ""
        olds = obj.get("oldString") or obj.get("old_string") or ""
        if not olds or "\n" in olds or len(olds.strip()) > 8:
            return ""
        return ("[anti-repeat] o edit falhou porque o oldString (%r) casa em mais de um ponto. "
                "Copie do arquivo a LINHA INTEIRA com a indentacao (ou duas linhas) e edite so ela; "
                "nao repita esse trecho curto." % olds.strip()[:12])
    return ""


def _write_paths(hist, min_count=3):
    """Paths de `write` repetidos: reescrita do mesmo arquivo fura o anti-repeat de assinatura."""
    counts = {}
    for tool, args, _c in hist:
        if (tool or "").strip().lower() not in ("write", "write_file"):
            continue
        try:
            obj = json.loads(args or "{}")
        except (TypeError, ValueError):
            continue
        p = obj.get("filePath") or obj.get("path") or ""
        if p:
            counts[p] = counts.get(p, 0) + 1
    return {p: n for p, n in counts.items() if n >= min_count}


def _write_noext(hist):
    """Paths de `write` sem extensao de arquivo (ex.: `Pasta/Pasta`) — o write devolve
    'sucesso' e o modelo reescreve: o alvo tem que ser arquivo com extensao."""
    out = []
    for tool, args, _c in hist:
        if (tool or "").strip().lower() not in ("write", "write_file"):
            continue
        try:
            obj = json.loads(args or "{}")
        except (TypeError, ValueError):
            continue
        p = (obj.get("filePath") or obj.get("path") or "").strip()
        if not p:
            continue
        base = re.split(r"[\\/]", p)[-1]
        if base and "." not in base and p not in out:
            out.append(p)
    return out


def _rewrite_text(path, n):
    return (f"[anti-repeat] {n} writes no MESMO arquivo ({_short(path, 60)}) nesta rodada, todos "
            "'sucesso'. Nao escreva de novo: o arquivo ja esta gravado. Rode o build e, se houver "
            "erro, corrija com edit no arquivo:linha indicado.")


def _noext_text(path):
    return (f"[tool] o write foi para '{path}', que nao tem extensao de arquivo. Arquivo de projeto "
            "ou de fonte precisa de extensao: <Nome>.csproj na raiz, <Nome>.cs, .json, .sql.")


def _breaker_text(n):
    return (f"[anti-repeat] {n} falhas sobre o mesmo alvo/erro nesta rodada. Repetir a mesma chamada "
            "nao resolve: troque de abordagem (outro caminho, outra forma de escrever, outra "
            "ferramenta) ou relate o erro exato e o que ja tentou.")


def _cycle_len():
    """Sufixo com <=2 assinaturas distintas contando so as chamadas que FALHARAM."""
    seen, n = [], 0
    for sg, err in reversed(list(SIG_HIST)):
        if sg not in seen:
            if len(seen) >= 2:
                break
            seen.append(sg)
        n += 1 if err else 0
    return n


def _nudge_cycle(n):
    return (f"[anti-repeat] ciclo de 2 chamadas alternadas QUE FALHAM repetiu {n}x sem sair do lugar. "
            "Pare de alternar: leia o arquivo com read, corrija o comando de uma vez e siga; "
            "se o build falha igual, troque de estrategia em vez de repetir a mesma dupla.")


def _nudge_for(sig, n):
    """Direcionamento especifico por ferramenta: o generico ('confira o caminho') nao serve para edit."""
    t = (sig or "").lstrip()
    if t.startswith("edit"):
        return (f"[anti-repeat] edit repetiu {n}x com o mesmo oldString e falhou. O trecho nao existe "
                "no arquivo como escrito: NAO repita o mesmo edit. Releia com read e copie o texto exato, "
                "ou reescreva o arquivo inteiro com write.")
    if t.startswith("write"):
        return (f"[anti-repeat] write repetiu {n}x e falhou. Se o arquivo ja existe, use edit; se o erro "
                "e de caminho, crie a pasta (mkdir -p) e escreva de novo uma unica vez.")
    if "dotnet build" in t and re.search(r"\.cs(?![A-Za-z])", t):
        return (f"[anti-repeat] o comando dotnet build repetiu {n}x. NUNCA passe arquivos .cs como "
                "argumento do comando dotnet build: use 'dotnet build' na pasta do .csproj "
                "na pasta do .csproj). Se o build falha, edite o arquivo que causa o erro antes de repetir.")
    if t.startswith("dotnet build") or t.startswith("dotnet restore"):
        return (f"[anti-repeat] dotnet build repetiu {n}x e o erro NAO mudou. Nao compile de novo: "
                "abra o arquivo:linha do erro, corrija com edit e so entao rode o build. "
                "Se o erro e de pacote/csproj, edite o .csproj e faca o restore antes de compilar.")
    return NUDGE_TOOL.format(sig=_short(sig), n=n)


def _short(sig, n=90):
    sig = (sig or "").replace("\n", " ")
    return sig[:n] + ("..." if len(sig) > n else "")


def _stop_body(body, sig, n, cyc=False, txt=None):
    """Resposta sintetica de corte (mesmo formato do llama, stream ou nao)."""
    if txt:
        txt = "Interrompido. " + txt
    else:
        txt = ("[anti-repeat] Interrompido. " + (_nudge_cycle(n) if cyc else _nudge_for(sig, n)))
    model = body.get("model")
    if body.get("stream"):
        ev = [{"id": "anti-repeat", "object": "chat.completion.chunk", "created": int(time.time()),
               "model": model,
               "choices": [{"index": 0, "delta": {"role": "assistant", "content": txt},
                            "finish_reason": None}]},
              {"id": "anti-repeat", "object": "chat.completion.chunk", "created": int(time.time()),
               "model": model,
               "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}]}]
        return ("\n\n".join("data: " + json.dumps(e, ensure_ascii=False) for e in ev)
                + "\n\ndata: [DONE]\n\n").encode()
    return json.dumps({"id": "anti-repeat", "object": "chat.completion",
                       "created": int(time.time()), "model": model,
                       "choices": [{"index": 0, "finish_reason": "stop",
                                    "message": {"role": "assistant", "content": txt}}],
                       "usage": {"prompt_tokens": 0, "completion_tokens": 0,
                                 "total_tokens": 0}}, ensure_ascii=False).encode()


# THINK_FORCE=on sobrepoe a regra e forca enable_thinking=true em todo request.
THINK_FORCE = os.environ.get("THINK_FORCE", "off").strip().lower()


def force_nothink(body):
    """REGRA IRREFUTAVEL: reasoning proibido neste tunel.

    Injeta chat_template_kwargs.enable_thinking=False sempre (o llama-server liga o
    thinking quando o kwarg nao vem). Devolve True se o cliente tinha pedido true.
    """
    kw = body.get("chat_template_kwargs")
    kw = dict(kw) if isinstance(kw, dict) else {}
    asked = bool(kw.get("enable_thinking"))
    kw["enable_thinking"] = asked   # respeita o cliente; ausente = off
    body["chat_template_kwargs"] = kw
    return asked


def force_thinking(body):
    """Aplica a regra de reasoning: off por padrao; THINK_FORCE=on forca true."""
    if THINK_FORCE != "on":
        return force_nothink(body)
    kw = dict(body.get("chat_template_kwargs") or {})
    changed = not bool(kw.get("enable_thinking"))
    kw["enable_thinking"] = True
    body["chat_template_kwargs"] = kw
    return changed


def _tail_tool_text(msgs, n=3):
    """Texto das ultimas mensagens que NAO sao do assistente (tool results)."""
    parts = []
    tail = msgs[-6:] if isinstance(msgs, list) else []
    for m in reversed(tail):
        if not isinstance(m, dict):
            continue
        if str(m.get("role")) == "assistant":
            continue
        if m.get("x-injected"):
            break            # ja alertamos neste ponto da conversa
        t = _text(m.get("content"))
        if t.strip():
            parts.append(t)
        if len(parts) >= n:
            break
    return "\n".join(reversed(parts))

def build_hint(body):
    """Detecta erro de compilacao no ultimo tool result e ANEXA o alerta pontual.

    O tool result original (mensagem do compilador) fica intacto; o modelo
    recebe alem dele uma mensagem com arquivo:linha + codigo + o exemplo
    otimista do `hints.py`.
    """
    global HINTS
    if _hints is None or BUILD_HINT == "off":
        return False
    msgs = body.get("messages")
    if not isinstance(msgs, list) or not msgs:
        return False
    txt = _tail_tool_text(msgs)
    if not txt or "error" not in txt.lower():
        return False
    if "tabela de erros" in txt or "[build-hint]" in txt:
        return False
    sigs = {ln[1] + "@" + ln[0] for ln in _hints.error_lines(txt, 6)}
    if not sigs:
        sigs = set(_hints.codes_in(txt, 6))
    if not sigs:
        te = _hints.TOOL_ARG_ERR.search(txt)
        if te:
            sigs = {"toolargs@" + (te.group(1) or "").lower()}
    if not sigs:
        return False
    key = _conv_key(msgs)
    with LOCK:
        cur = "|".join(sorted(sigs))
        last_sig, rep = HINT_LAST.get(key, ("", 0))
        rep = rep + 1 if cur == last_sig else 1
        if rep > 5:                      # mesmo hint 6x seguido: para
            return False
        if HINT_TOTAL.get(key, 0) >= HINT_MAX:   # teto total (hints distintos)
            return False
        HINT_LAST[key] = (cur, rep)
        HINT_TOTAL[key] = HINT_TOTAL.get(key, 0) + 1
        HINTS += 1
        while len(HINT_LAST) > 64:
            HINT_LAST.pop(next(iter(HINT_LAST)))
        while len(HINT_TOTAL) > 64:
            HINT_TOTAL.pop(next(iter(HINT_TOTAL)))
    msg = _hints.alert(txt)
    if not msg:
        return False
    msgs.append({"role": "user", "content": msg, "x-injected": True})
    return True


def tool_arg_hint(body):
    """Valida os args da ULTIMA tool call do request e injeta UM aviso otimista.

    Espelha o ToolCallRepair/ToolHelp da bridge C#: quando o args veio cortado,
    com `<tool_call>` colado, campo fora do schema (`path`) ou faltando, o modelo
    recebe a forma EXATA que passa antes do proximo turno — sem esperar o
    "Invalid arguments" virar laco. Anti-spam por (tool, tipo do defeito).
    """
    global TOOL_HINTS
    if _hints is None or BUILD_HINT == "off":
        return False
    msgs = body.get("messages")
    if not isinstance(msgs, list) or not msgs:
        return False
    call = None
    for m in reversed(msgs[-8:]):
        if isinstance(m, dict) and str(m.get("role")) == "assistant":
            calls = m.get("tool_calls") or []
            call = calls[-1] if isinstance(calls, list) and calls else None
            break
    if not isinstance(call, dict):
        return False
    fn = call.get("function") if isinstance(call.get("function"), dict) else {}
    name = (fn.get("name") or call.get("name") or "").lower()
    raw = fn.get("arguments", fn.get("input", call.get("arguments")))
    prob = _hints.tool_call_problem(name, raw)
    if not prob:
        return False
    kind, problema, exemplo = prob
    key = _conv_key(msgs)
    with LOCK:
        seen = HINT_SEEN.setdefault(key, set())
        sig = "toolarg@%s:%s" % (name, kind)
        if sig in seen or len(seen) >= HINT_MAX:
            return False
        seen.add(sig)
        TOOL_HINTS += 1
        while len(HINT_SEEN) > 64:
            HINT_SEEN.pop(next(iter(HINT_SEEN)))
    msg = ('[tool-hint] a ultima tool call nao casa com o schema do opencode: %s. '
           'Reenvie UMA chamada valida, com os args COMPLETOS:\n  %s\n'
           "O exemplo e' FORMA, nao conteudo: troque caminho e texto pelos do seu pedido "
           'e nunca escreva <tool_call> dentro do argumento.' % (problema, exemplo))
    msgs.append({"role": "user", "content": msg, "x-injected": True})
    return True


def anti_repeat(body, raw_body):
    """Anti-repeat de texto e de tool. Devolve (raw, stop_payload, nudged, stopped).

    - laco de tool (mesma assinatura >= NUDGE_AT): injeta um aviso no request;
      em modo "stop", a partir de STOP_AT nem chama o llama (corta o laco).
    - texto repetido (dup_lines >= 4 ou tail_sim >= 0.9): injeta aviso de texto.
    """
    global NUDGES, STOPS, BREAKER_TRIPS, BREAKER_NUDGES
    msgs = body.get("messages")
    if not isinstance(msgs, list) or ANTI == "off":
        return raw_body, None, False, False
    with LOCK:
        sig, streak, cyc = LAST_SIG, SIG_STREAK, CYCLE
        dup, sim = PROBE["dup_lines"], PROBE["tail_sim"]
    # ToolsBreaker (porte do C#): conta FALHAS por alvo/erro no historico do request.
    bi = _breaker_info(msgs)
    brk, alvos = bi["cont"], bi["alvos"]
    rewrites = _write_paths(bi["hist"])
    churn = max(rewrites.values()) if rewrites else 0
    noext = _write_noext(bi["hist"])
    loop_tool = bool(sig) and streak >= NUDGE_AT
    loop_cycle = cyc >= NUDGE_AT
    loop_text = (dup >= 4) or (sim >= 0.9)
    # 3+ alvos distintos com falha = progresso real, nao laco: nao avisa.
    loop_break = brk >= BREAKER_SOFT and (alvos <= 2 or brk >= BREAKER_HARD)
    loop_rewrite = churn >= NUDGE_AT
    if not (loop_tool or loop_cycle or loop_text or loop_break or loop_rewrite or noext):
        return raw_body, None, False, False
    if ANTI == "stop" and (brk >= BREAKER_HARD
                           or (loop_tool and streak >= STOP_AT)
                           or (loop_cycle and cyc >= STOP_AT)
                           or (churn >= REWRITE_STOP)):
        with LOCK:
            STOPS += 1
        if brk >= BREAKER_HARD:
            BREAKER_TRIPS += 1
            return raw_body, _stop_body(body, sig, brk, txt=_breaker_text(brk)), False, True
        if churn >= REWRITE_STOP:
            p, n = max(rewrites.items(), key=lambda kv: kv[1])
            return raw_body, _stop_body(body, sig, n, txt=_rewrite_text(p, n)), False, True
        if loop_tool:
            return raw_body, _stop_body(body, sig, streak), False, True
        return raw_body, _stop_body(body, sig, cyc, cyc=True), False, True
    # direcionamento: no maximo a cada 2 respostas repetidas, para nao virar spam
    if loop_tool and (streak - NUDGE_AT) % 2:
        return raw_body, None, False, False
    if loop_cycle and not loop_tool and (cyc - NUDGE_AT) % 2:
        return raw_body, None, False, False
    txt = ""
    if loop_break:
        BREAKER_NUDGES += 1
    if loop_break and not loop_tool and not loop_cycle:
        txt = _degenerate_edit(bi["fails"]) or _breaker_text(brk)
    elif loop_tool:
        txt = _nudge_for(sig, streak)
    elif loop_cycle:
        txt = _nudge_cycle(cyc)
    if not txt and noext:
        txt = _noext_text(noext[0])
    if not txt and loop_rewrite:
        p, n = max(rewrites.items(), key=lambda kv: kv[1])
        txt = _rewrite_text(p, n)
    if not txt:
        txt = NUDGE_TEXT
    key = _conv_key(msgs)
    with LOCK:
        if NUDGE_TOTAL.get(key, 0) >= NUDGE_MAX:
            return raw_body, None, False, False
        NUDGE_TOTAL[key] = NUDGE_TOTAL.get(key, 0) + 1
        while len(NUDGE_TOTAL) > 64:
            NUDGE_TOTAL.pop(next(iter(NUDGE_TOTAL)))
    nb = dict(body)
    nb["messages"] = list(msgs) + [{"role": "user", "content": txt}]
    with LOCK:
        NUDGES += 1
    return json.dumps(nb, ensure_ascii=False).encode(), None, True, False


def dump_run():
    """Dump da rodada: memoria -> events.jsonl + runs/<id>.jsonl (atômico)."""
    with LOCK:
        rows = list(RUN["requests"])
        rid = RUN["id"]
    if not rows:
        return 0
    base = os.path.dirname(EVENTS)
    os.makedirs(os.path.join(base, "runs"), exist_ok=True)
    body = "\n".join(json.dumps(r, ensure_ascii=False) for r in rows) + "\n"
    for path in (EVENTS, os.path.join(base, "runs", f"{rid}.jsonl")):
        tmp = path + ".tmp"
        with open(tmp, "w") as f:
            f.write(body)
        os.replace(tmp, path)
    return len(rows)


def _startup_reset():
    """A cada restart: arquiva o events.jsonl anterior e comeca do zero."""
    global IDLED, LAST_REQ_TS
    try:
        if os.path.exists(EVENTS) and os.path.getsize(EVENTS) > 0:
            dst = EVENTS.replace(".jsonl", f".{time.strftime('%Y%m%d-%H%M%S')}.jsonl")
            os.rename(EVENTS, dst)
    except Exception:
        pass
    IDLED = True
    LAST_REQ_TS = 0.0


def _go_idle():
    """Zera o que e "vivo" (gauges + janela de requests); contadores ficam."""
    global IDLED, TPS, STREAK, LAST_PROMPT, LAST_SIG, SIG_STREAK, CYCLE
    with LOCK:
        IDLED = True
        TPS = 0.0
        STREAK = 0
        LAST_PROMPT = ""
        LAST_SIG = ""
        SIG_STREAK = 0
        CYCLE = 0
        SIG_HIST.clear()
        LAST.clear()
        LAST_RUN_ROWS.clear()
        LAST_RUN_ROWS.extend(list(RECENT))
        RECENT.clear()
        for k in PROBE:
            PROBE[k] = 0


def _idle_loop():
    """Nada rodando por IDLE_S: gauges vivas zeradas e tabela de requests vazia."""
    while True:
        time.sleep(5)
        if IDLED:
            continue
        with LOCK:
            last = LAST_REQ_TS
        if last and time.time() - last > IDLE_S:
            _go_idle()


def summary():
    """Agregado da rodada a partir do observer em memoria (RUN)."""
    with LOCK:
        rows = list(RUN["requests"])
        run = RUN["id"]
        started = RUN["started"]
    tps = [r.get("tps") or 0 for r in rows if (r.get("tps") or 0) > 0]
    cur = _read_current()
    current_case = cur.get("case", "") or ""
    current_status = cur.get("status", "") or ""
    current_started = cur.get("ts", 0) or 0
    files_generated = _count_generated(cur.get("dir", ""))
    return {
        "run": run, "started": round(started, 1), "idle": IDLED,
        # o Infinity nao converte bool para coluna number; o painel usa idle_i (0/1)
        "idle_i": 1 if IDLED else 0,
        "requests": len(rows),
        # Mensagens de conversa: user + assistant, sem tool calls/resultados.
        "messages": sum((r.get("user_messages") or 0) + (r.get("assistant_messages") or 0) for r in rows),
        "user_messages": sum(r.get("user_messages") or 0 for r in rows),
        "assistant_messages": sum(r.get("assistant_messages") or 0 for r in rows),
        "context_messages": sum(r.get("messages") or 0 for r in rows),
        "prompt_tokens": sum(r.get("prompt_tokens") or 0 for r in rows),
        "completion_tokens": sum(r.get("completion_tokens") or 0 for r in rows),
        "tps_last": round(tps[-1], 2) if tps else 0,
        "tps_avg": round(sum(tps) / len(tps), 2) if tps else 0,
        "tps_max": round(max(tps), 2) if tps else 0,
        "errors": ERR,
        "tool_calls": TOOL_CALLS,
        "files_generated": files_generated,
        "current_case": current_case,
        "current_status": current_status,
        "current_started": current_started,
        "streak_max": max((r.get("prompt_streak") or 0) for r in rows) if rows else 0,
        "tail_sim_max": max((r.get("tail_sim") or 0) for r in rows) if rows else 0,
        "dup_max": max((r.get("dup_lines") or 0) for r in rows) if rows else 0,
        "meta_total": sum((r.get("meta") or 0) for r in rows),
        "think_chars": sum((r.get("think_chars") or 0) for r in rows),
        "thinking_blocked": THINK_BLOCKED,
        "think_force": THINK_FORCE,
        "tool_loop_max": SIG_MAX,
        "tool_loop_last": 0 if IDLED else SIG_STREAK,
        "tool_cycle": 0 if IDLED else CYCLE,
        "tool_cycle_max": CYCLE_MAX,
        "tool_loop_hits": sum(1 for r in rows if (r.get("tool_sig_streak") or 0) >= 3),
        "nudges": NUDGES, "stops": STOPS, "anti_repeat": ANTI,
        "breaker_soft": BREAKER_SOFT, "breaker_hard": BREAKER_HARD,
        "breaker_nudges": BREAKER_NUDGES, "breaker_trips": BREAKER_TRIPS,
        "build_hints": HINTS, "tool_hints": TOOL_HINTS, "build_hint": BUILD_HINT,
        "nudge_at": NUDGE_AT, "stop_at": STOP_AT,
    }


def _lbl(v, n=60):
    """Valor de label Prometheus: sem quebra de linha, sem aspas cruas."""
    return re.sub(r'[\r\n"]', " ", str(v))[:n]


def _recent_lines():
    """Ultimos requests como series rotuladas (a tabela do painel)."""
    if IDLED or not RECENT:
        return []
    out = ["# TYPE oc_recent_requests gauge"]
    for e in list(RECENT)[-RECENT_N:]:
        lbl = (f'seq="{e["seq"]}",prompt="{_lbl(e["prompt"])}",streak="{e["streak"]}",'
               f'tail_sim="{e["tail_sim"]}",dup="{e["dup"]}",meta="{e["meta"]}",tsig="{e["tsig"]}",'
               f'resp="{e["resp_chars"]}",think="{e["think_chars"]}",'
               f'tools="{_lbl(e["tools"], 40)}",finish="{_lbl(e["finish"], 20)}"')
        out.append(f"oc_recent_requests{{{lbl}}} 1")
    return out


def metrics():
    # Ocioso (nada rodando ha IDLE_S): gauges "vivas" zeradas e tabela vazia.
    # Os contadores acumulados ficam (sao o total da rodada e zeram no restart).
    idle = IDLED
    tps = 0.0 if idle else TPS
    p = {k: 0 for k in PROBE} if idle else PROBE
    streak = 0 if idle else STREAK
    out = ["# HELP oc_requests_total Requests repassados ao llama",
           "# TYPE oc_requests_total counter", f"oc_requests_total {REQ}",
           "# TYPE oc_errors_total counter", f"oc_errors_total {ERR}",
           "# TYPE oc_messages_total counter", f"oc_messages_total {MSG}",
           "# TYPE oc_user_messages_total counter", f"oc_user_messages_total {USER_MSGS}",
           "# TYPE oc_assistant_messages_total counter", f"oc_assistant_messages_total {ASSIST_MSGS}",
           "# TYPE oc_prompt_tokens_total counter", f"oc_prompt_tokens_total {PROMPT_T}",
           "# TYPE oc_completion_tokens_total counter", f"oc_completion_tokens_total {OUT_T}",
           "# TYPE oc_tokens_per_second gauge", f"oc_tokens_per_second {tps}",
           "# TYPE oc_idle gauge", f"oc_idle {1 if idle else 0}",
           "# TYPE oc_tool_calls_total counter", f"oc_tool_calls_total {TOOL_CALLS}",
           "# TYPE oc_prompt_repeat_streak gauge", f"oc_prompt_repeat_streak {streak}",
           "# TYPE oc_tail_similarity gauge", f"oc_tail_similarity {p['tail_sim']}",
           "# TYPE oc_tail_repetitions gauge", f"oc_tail_repetitions {p['tail_reps']}",
           "# TYPE oc_dup_lines_max gauge", f"oc_dup_lines_max {p['dup_lines']}",
           "# TYPE oc_meta_phrases gauge", f"oc_meta_phrases {p['meta']}",
           "# TYPE oc_thinking_blocked_total counter", f"oc_thinking_blocked_total {THINK_BLOCKED}",
           "# TYPE oc_tool_sig_streak gauge", f"oc_tool_sig_streak {0 if idle else SIG_STREAK}",
           "# TYPE oc_tool_sig_max gauge", f"oc_tool_sig_max {SIG_MAX}",
           "# TYPE oc_anti_repeat_nudges_total counter", f"oc_anti_repeat_nudges_total {NUDGES}",
           "# TYPE oc_anti_repeat_stops_total counter", f"oc_anti_repeat_stops_total {STOPS}",
           "# TYPE oc_response_chars gauge", f"oc_response_chars {p['chars']}",
           "# TYPE oc_think_chars gauge", f"oc_think_chars {p.get('think_chars', 0)}",
           "# TYPE oc_think_tail_similarity gauge", f"oc_think_tail_similarity {p.get('think_sim', 0.0)}",
           "# TYPE oc_think_dup_lines_max gauge", f"oc_think_dup_lines_max {p.get('think_dup', 0)}",
           "# TYPE oc_think_meta_phrases gauge", f"oc_think_meta_phrases {p.get('think_meta', 0)}"]
    if not idle:
        now = time.time()
        out.append("# TYPE oc_prompt_age_seconds gauge")
        for pr, (ts, n) in LAST.items():
            out.append(f'oc_prompt_age_seconds{{prompt="{_lbl(pr, 120)}",messages="{n}"}} {round(now - ts, 1)}')
    out += _recent_lines()
    return ("\n".join(out) + "\n").encode()


HARNESS = {"text": None, "report": None}
ENV_BLOCK = re.compile(r"<env>.*?</env>", re.S)
# Catálogo de skills do cliente (opencode): o harness não pode engolir a lista,
# senão o modelo vê a tool `skill` sem nenhuma skill para rotear.
SKILL_BLOCK = re.compile(
    r"(?:Skills provide specialized instructions.*?\n)?<available_skills>.*?</available_skills>", re.S)
# Instrução de trilha: o catálogo sozinho não faz o 4B escolher (medido: 0/45
# chamadas nos dumps). A dica ensina a forma certa sem citar skill do caso.
SKILL_NUDGE = (
    "TRILHA SOB DEMANDA: se o pedido for de .NET/SQL/EF/CQRS/bus/React/ML e a "
    "conversa ainda nao mostrou a forma, chame a tool `skill` com o id da skill "
    "cuja descricao casa (ex.: dotnet-api) ANTES de escrever o primeiro arquivo. "
    "A trilha e forma, nao codigo a copiar: escreva o que o pedido pede."
)
SKILL_NUDGE_ON = os.environ.get("SKILL_NUDGE", "1") != "0"
ENV_LINES = ("Working directory:", "Workspace root folder:", "Is directory a git repo:",
             "Platform:", "Today's date:")


def _harness_build():
    """Monta uma vez; o texto fica byte-identico entre requests (cache de prefixo)."""
    res = _harness_loader.assemble(stack=HARNESS_STACK)
    HARNESS["text"] = res["text"]
    HARNESS["report"] = {k: v for k, v in res.items() if k != "text"}
    return HARNESS["report"]


def harness_report():
    with LOCK:
        if HARNESS["report"] is not None and HARNESS["text"] is not None:
            return HARNESS["report"]
        if HARNESS_MODE != "assembled":
            HARNESS["report"] = {"mode": HARNESS_MODE, "active": False}
            return HARNESS["report"]
        try:
            if _harness_loader is None:
                raise RuntimeError("harness_loader.py ausente")
            rep = _harness_build()
        except Exception as exc:      # nunca derruba o tunel: cai no system do cliente
            HARNESS["text"] = None
            HARNESS["report"] = {"mode": HARNESS_MODE, "active": False, "error": str(exc)}
            rep = HARNESS["report"]
        rep["mode"] = HARNESS_MODE
        rep["active"] = HARNESS["text"] is not None
        return rep


def harness_reload():
    with LOCK:
        HARNESS["text"] = None
        HARNESS["report"] = None
    return harness_report()


def apply_harness(body):
    """Troca o system do cliente pelo harness montado, preservando o bloco <env>."""
    if HARNESS_MODE != "assembled":
        return False
    msgs = body.get("messages")
    if not isinstance(msgs, list) or not msgs:
        return False
    idx = next((i for i, m in enumerate(msgs) if m.get("role") == "system"), None)
    if idx is None:
        return False
    harness_report()
    base = HARNESS["text"]
    if not base:
        return False
    original = msgs[idx].get("content") or ""
    found = ENV_BLOCK.search(original)
    if found:
        env = found.group(0)
    else:
        keep = [ln for ln in original.splitlines() if ln.startswith(ENV_LINES)]
        env = "<env>\n" + "\n".join(keep) + "\n</env>" if keep else ""
    text = base + ("\n\n" + env if env else "")
    skills = SKILL_BLOCK.search(original)
    if skills:
        if SKILL_NUDGE_ON:
            text += "\n\n" + SKILL_NUDGE
        text += "\n\n" + skills.group(0)
    if original == text:
        return False
    msgs[idx] = {**msgs[idx], "content": text}
    return True


class Proxy(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _send(self, code, ctype, data):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _local(self):
        """Endpoints proprios do coletor (nao vao para o llama)."""
        global TOOL_CALLS, SIG_MAX, SIG_STREAK, CYCLE, CYCLE_MAX, NUDGES, STOPS, LAST_SIG, LAST_PROMPT, STREAK, ERR, HINTS, BREAKER_TRIPS, BREAKER_NUDGES
        path = self.path.split("?")[0].rstrip("/")
        qs = self.path.split("?")[1] if "?" in self.path else ""
        n = RECENT_N
        m = re.search(r"(?:^|&)n=(\d+)", qs)
        if m:
            n = max(1, min(200, int(m.group(1))))
        if path == "/api/dump":
            body = {"dumped": dump_run(), "run": RUN["id"], "events": EVENTS}
        elif path == "/api/reset":
            _go_idle()
            with LOCK:
                RUN["requests"].clear()
                RUN["id"] = time.strftime("%Y%m%d-%H%M%S")
                RUN["started"] = time.time()
                TOOL_CALLS = SIG_MAX = SIG_STREAK = CYCLE = CYCLE_MAX = 0
                NUDGES = STOPS = 0
                BREAKER_TRIPS = BREAKER_NUDGES = 0
                HINTS = 0
                HINT_SEEN.clear()
                HINT_LAST.clear()
                HINT_TOTAL.clear()
                LAST_SIG = ""
                LAST_PROMPT = ""
                STREAK = 0
                ERR = 0
                SIG_HIST.clear()
                LAST.clear()
                PREV_MSGS.clear()
            body = {"reset": True, "run": RUN["id"]}
        elif path == "/api/summary":
            body = summary()
        elif path == "/api/recent":
            if IDLED and not RECENT and LAST_RUN_ROWS:
                rows = [dict(r) for r in list(LAST_RUN_ROWS)[-n:]]
                last_run = True
            else:
                rows = [dict(r) for r in list(RECENT)[-n:]]
                last_run = False
            latest = _latest_cases()
            for row in rows:
                final = latest.get(row.get("case", ""))
                if final and final.get("status"):
                    row["status"] = final["status"]
            body = {"run": RUN["id"], "requests": rows, "last_run": last_run}
        elif path == "/api/cases":
            # SOMENTE a rodada corrente (run de metrics/current.json), mais recente
            # primeiro e com data/hora legivel. `latest` = ultimo registro por caso.
            rows = []
            try:
                with open(CASES, "r", encoding="utf-8") as fh:
                    for line in fh:
                        line = line.strip()
                        if not line:
                            continue
                        try:
                            row = json.loads(line)
                        except Exception:
                            continue
                        if row.get("case"):
                            rows.append(row)
            except OSError:
                pass
            run = str(_read_current().get("run") or "")
            if not run:
                runs = [str(r.get("run")) for r in rows if r.get("run")]
                run = runs[-1] if runs else ""
            mine = [r for r in rows if str(r.get("run") or "") == run] if run else []
            seq = []
            for row in mine[-n:]:
                r = dict(row)
                try:
                    r["ts"] = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(float(r.get("ts"))))
                except (TypeError, ValueError):
                    r["ts"] = "-"
                seq.append(r)
            latest = {}
            for r in seq:
                latest[r["case"]] = r
            body = {"run": run,
                    "cases": list(reversed(seq)),
                    "latest": [latest[k] for k in sorted(latest)],
                    "count": len(seq),
                    "run_total": len(mine)}
        elif path == "/api/harness":
            body = harness_report()
        elif path == "/api/harness/reload":
            body = harness_reload()
        elif path == "/api/status":
            body = {"run": RUN["id"], "requests": len(RUN["requests"]),
                    "idle": IDLED, "events": EVENTS, "idle_s": IDLE_S}
        else:
            return self._send(404, "application/json", b'{"error":"rota desconhecida"}')
        return self._send(200, "application/json",
                          json.dumps(body, ensure_ascii=False).encode())

    def _forward(self):
        if self.path.startswith("/api/"):
            return self._local()
        # Request em voo conta como ATIVO: stream longo (thinking) nao pode
        # aparecer como "ocioso" no painel.
        with LOCK:
            global LAST_REQ_TS, IDLED
            LAST_REQ_TS = time.time()
            IDLED = False
        global ERR, THINK_BLOCKED
        n = int(self.headers.get("Content-Length") or 0)
        raw_body = self.rfile.read(n) if n else b""
        body = {}
        try:
            body = json.loads(raw_body) if raw_body else {}
        except Exception:
            pass
        harnessed = False
        orig_system = ""
        if body:
            for m in body.get("messages") or []:
                if m.get("role") == "system":
                    orig_system = m.get("content") or ""
                    break
            try:
                harnessed = bool(apply_harness(body))
            except Exception:
                pass                  # harness quebrado nao pode derrubar o repasse
        if harnessed and raw_body:
            # o system montado so chega ao llama se o corpo for re-serializado
            raw_body = json.dumps(body, ensure_ascii=False).encode()
        if raw_body:
            _dump_req(self.path, body, orig_system)
        nudged = stopped = False
        if body and self.command == "POST" and self.path.startswith("/v1/chat"):
            if tool_arg_hint(body) or build_hint(body):
                raw_body = json.dumps(body, ensure_ascii=False).encode()
            raw_body, stop, nudged, stopped = anti_repeat(body, raw_body)
            if stop is not None:
                # laco de tool cortado: nao vai ao llama, resposta sintetica
                try:
                    record(self.path, body, stop, {"stopped": True})
                except Exception:
                    pass
                ctype = "text/event-stream" if body.get("stream") else "application/json"
                return self._send(200, ctype, stop)
        # REGRA IRREFUTAVEL: reasoning proibido — reescreve o kwarg em todo /v1/chat*.
        if self.command == "POST" and self.path.startswith("/v1/chat") and raw_body:
            try:
                _b = json.loads(raw_body)
            except Exception:
                _b = None
            if isinstance(_b, dict):
                if force_thinking(_b) and THINK_FORCE != "on":
                    with LOCK:
                        THINK_BLOCKED += 1
                raw_body = json.dumps(_b, ensure_ascii=False).encode()
        req = urllib.request.Request(UP + self.path, data=raw_body or None, method=self.command)
        for k, v in self.headers.items():
            if k.lower() not in ("host", "content-length", "connection", "accept-encoding"):
                req.add_header(k, v)
        try:
            with urllib.request.urlopen(req, timeout=1800) as r:
                data = r.read()
                ctype = r.headers.get("Content-Type", "application/json")
                code = r.status
        except urllib.error.HTTPError as e:
            data, ctype, code = e.read(), e.headers.get("Content-Type", "application/json"), e.code
            with LOCK:
                ERR += 1
        except Exception:
            with LOCK:
                ERR += 1
            self._send(502, "application/json", b'{"error":"upstream indisponivel"}')
            return
        if self.command == "POST" and body:
            try:
                record(self.path, body, data, {"nudged": True} if nudged else None)
            except Exception:
                pass
        self._send(code, ctype, data)

    do_GET = _forward
    do_POST = _forward

    def log_message(self, *a):
        pass


class MetricsHandler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        data = metrics()
        self.send_response(200)
        self.send_header("Content-Type", "text/plain; version=0.0.4")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


def serve(handler, port):
    socketserver.ThreadingTCPServer.allow_reuse_address = True
    srv = socketserver.ThreadingTCPServer(("127.0.0.1", port), handler)
    srv.daemon_threads = True
    srv.serve_forever()


def _bye(*_):
    dump_run()


if __name__ == "__main__":
    _startup_reset()
    atexit.register(_bye)
    signal.signal(signal.SIGTERM, lambda *a: (_bye(), sys.exit(0)))
    signal.signal(signal.SIGINT, lambda *a: (_bye(), sys.exit(0)))
    threading.Thread(target=serve, args=(MetricsHandler, METRICS_PORT), daemon=True).start()
    threading.Thread(target=_idle_loop, daemon=True).start()
    print(f"proxy :{PORT} -> {UP} | metrics :{METRICS_PORT}/metrics | events {EVENTS}"
          f" | idle reset {IDLE_S}s", flush=True)
    _rep = harness_report()
    if _rep.get("active"):
        print(f"harness: {_rep['mode']} stack={_rep['stack']} "
              f"system={_rep['chars']} ch ~{_rep['tokens']:.0f} tok "
              f"teto={_rep['budget']['max_total_tokens']} folga="
              f"{_rep['budget']['max_total_tokens'] - (_rep['tokens'] + _rep['budget']['tools_reserve_tokens']):.0f}"
              + (f" cortados={len(_rep['dropped'])}" if _rep.get("dropped") else ""), flush=True)
    else:
        print(f"harness: inativo ({_rep.get('error') or _rep.get('mode')}); system do cliente",
              flush=True)
    serve(Proxy, PORT)
