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

UP = os.environ.get("UPSTREAM", "http://127.0.0.1:4100")
PORT = int(os.environ.get("PORT", "4199"))
METRICS_PORT = int(os.environ.get("METRICS_PORT", "9091"))
EVENTS = os.environ.get("EVENTS", "/tmp/opencode/llama-pure/metrics/events.jsonl")
RESP_DIR = os.environ.get("RESP_DIR", "/tmp/opencode/llama-pure/metrics/responses")
# Sem request por IDLE_S segundos, as gauges "vivas" voltam a zero (nada rodando
# nao deve aparecer como numero congelado no painel).
IDLE_S = int(os.environ.get("IDLE_S", "60"))
# Anti-repeat enxuto: "nudge" injeta um aviso no request; "stop" tambem corta o
# laco de tool (nao repassa ao llama e devolve uma resposta sintetica).
ANTI = os.environ.get("ANTI_REPEAT", "stop")          # nudge | stop | off
NUDGE_AT = int(os.environ.get("NUDGE_AT", "3"))        # mesma tool+args 3x
STOP_AT = int(os.environ.get("STOP_AT", "6"))          # 6x = corta
NUDGES = STOPS = 0
THINK_BLOCKED = 0   # requests que pediram reasoning e foram forcados a thinking off
RECENT_N = int(os.environ.get("RECENT_N", "30"))

# Sonda de repeticao/devagacao: janela 256 caracteres normalizados (mesma faixa
# que a bridge usa como referencia de observacao) e marcas de prosa de plano.
WIN = 256
META = ("vou ", "agora vou", "em seguida", "proximo passo", "vamos ", "plano:")


LOCK = threading.Lock()
REQ = ERR = MSG = PROMPT_T = OUT_T = RESP_T = TOOL_CALLS = 0
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


def _prompt_of(body):
    msgs = body.get("messages")
    if not isinstance(msgs, list):
        msgs = body.get("input") if isinstance(body.get("input"), list) else []
    if not msgs:
        return "", 1
    last = ""
    for m in msgs:
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



def record(endpoint, body, raw, flags=None):
    global REQ, ERR, MSG, PROMPT_T, OUT_T, TPS, TOOL_CALLS, LAST_PROMPT, STREAK
    global LAST_REQ_TS, IDLED, SEQ, LAST_SIG, SIG_STREAK, SIG_MAX, CYCLE, CYCLE_MAX
    usage, timings = _parse(raw)
    prompt, nmsg = _prompt_of(body)
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
        REQ += 1
        MSG += nmsg
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
        RECENT.append({"seq": SEQ, "prompt": prompt[:60], "streak": streak,
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
          "model": body.get("model"), "messages": nmsg, "prompt": prompt,
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
    """Nudge especifico por ferramenta: o generico ('confira o caminho') nao serve para edit."""
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
                "argumento do comando dotnet build: use 'dotnet build Nome.csproj' (ou 'dotnet build' "
                "na pasta do .csproj). Se o build falha, edite o arquivo que causa o erro antes de repetir.")
    return NUDGE_TOOL.format(sig=_short(sig), n=n)


def _short(sig, n=90):
    sig = (sig or "").replace("\n", " ")
    return sig[:n] + ("..." if len(sig) > n else "")


def _stop_body(body, sig, n, cyc=False):
    """Resposta sintetica de corte (mesmo formato do llama, stream ou nao)."""
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


def anti_repeat(body, raw_body):
    """Anti-repeat de texto e de tool. Devolve (raw, stop_payload, nudged, stopped).

    - laco de tool (mesma assinatura >= NUDGE_AT): injeta um aviso no request;
      em modo "stop", a partir de STOP_AT nem chama o llama (corta o laco).
    - texto repetido (dup_lines >= 4 ou tail_sim >= 0.9): injeta aviso de texto.
    """
    global NUDGES, STOPS
    msgs = body.get("messages")
    if not isinstance(msgs, list) or ANTI == "off":
        return raw_body, None, False, False
    with LOCK:
        sig, streak, cyc = LAST_SIG, SIG_STREAK, CYCLE
        dup, sim = PROBE["dup_lines"], PROBE["tail_sim"]
    loop_tool = bool(sig) and streak >= NUDGE_AT
    loop_cycle = cyc >= NUDGE_AT
    loop_text = (dup >= 4) or (sim >= 0.9)
    if not (loop_tool or loop_cycle or loop_text):
        return raw_body, None, False, False
    if ANTI == "stop" and ((loop_tool and streak >= STOP_AT) or (loop_cycle and cyc >= STOP_AT)):
        with LOCK:
            STOPS += 1
        if loop_tool:
            return raw_body, _stop_body(body, sig, streak), False, True
        return raw_body, _stop_body(body, sig, cyc, cyc=True), False, True
    # nudge: no maximo a cada 2 respostas repetidas, para nao virar spam
    if loop_tool and (streak - NUDGE_AT) % 2:
        return raw_body, None, False, False
    if loop_cycle and not loop_tool and (cyc - NUDGE_AT) % 2:
        return raw_body, None, False, False
    if loop_tool:
        txt = _nudge_for(sig, streak)
    elif loop_cycle:
        txt = _nudge_cycle(cyc)
    else:
        txt = NUDGE_TEXT
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
    return {
        "run": run, "started": round(started, 1), "idle": IDLED,
        # o Infinity nao converte bool para coluna number; o painel usa idle_i (0/1)
        "idle_i": 1 if IDLED else 0,
        "requests": len(rows),
        "messages": sum(r.get("messages") or 0 for r in rows),
        "prompt_tokens": sum(r.get("prompt_tokens") or 0 for r in rows),
        "completion_tokens": sum(r.get("completion_tokens") or 0 for r in rows),
        "tps_last": round(tps[-1], 2) if tps else 0,
        "tps_avg": round(sum(tps) / len(tps), 2) if tps else 0,
        "tps_max": round(max(tps), 2) if tps else 0,
        "errors": ERR,
        "tool_calls": sum(len(r.get("resp_tools") or []) for r in rows),
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
            body = {"reset": True, "run": RUN["id"]}
        elif path == "/api/summary":
            body = summary()
        elif path == "/api/recent":
            if IDLED and not RECENT and LAST_RUN_ROWS:
                body = {"run": RUN["id"], "requests": LAST_RUN_ROWS[-n:], "last_run": True}
            else:
                body = {"run": RUN["id"], "requests": list(RECENT)[-n:]}
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
        nudged = stopped = False
        if body and self.command == "POST" and self.path.startswith("/v1/chat"):
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
    serve(Proxy, PORT)
