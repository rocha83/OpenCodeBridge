#!/usr/bin/env python3
"""Auto-teste da sonda de repeticao/devagacao do collector.py.

Nao precisa de llama no ar: monta respostas SSE sinteticas, chama `collector.record`
e confere o que vai para o events.jsonl e para /metrics.

Uso: python3 probe-selftest.py
"""
import json, os, sys, tempfile

TMP = tempfile.mkdtemp(prefix="probe-")
os.environ["EVENTS"] = os.path.join(TMP, "events.jsonl")
os.environ["RESP_DIR"] = os.path.join(TMP, "responses")
os.environ["METRICS_PORT"] = "0"          # o auto-teste nao sobe o servidor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import collector  # noqa: E402

fails = []
oks = 0


def sse(*deltas):
    """Resposta SSE do /v1/chat/completions a partir dos deltas de conteudo."""
    out = []
    for d in deltas:
        out.append(json.dumps({"choices": [{"delta": {"content": d}}]}))
    out.append(json.dumps({"choices": [{"delta": {}, "finish_reason": "stop"}],
                           "usage": {"prompt_tokens": 10, "completion_tokens": 5},
                           "timings": {"predicted_per_second": 42.0}}))
    out.append("[DONE]")
    return ("\n\n".join("data: " + x for x in out) + "\n\n").encode()


def sse_tool(name, args):
    """Resposta SSE com uma tool call (o que o subagente devolve ao opencode)."""
    tc = {"index": 0, "function": {"name": name, "arguments": args}}
    out = [json.dumps({"choices": [{"delta": {"tool_calls": [tc]}}]}),
           json.dumps({"choices": [{"delta": {}, "finish_reason": "tool_calls"}],
                       "usage": {"prompt_tokens": 20, "completion_tokens": 8},
                       "timings": {"predicted_per_second": 40.0}}),
           "[DONE]"]
    return ("\n\n".join("data: " + x for x in out) + "\n\n").encode()


def body(prompt, tools=0):
    b = {"model": "Gemma4-E4B-GK-Q4",
         "messages": [{"role": "system", "content": "sys"},
                      {"role": "user", "content": prompt}]}
    if tools:
        b["tools"] = [{"type": "function", "function": {"name": "write"}}] * tools
    return b


def mv(m, name):
    """Valor numerico de uma metrica no texto do /metrics."""
    for line in m.splitlines():
        if line.startswith(name + " "):
            try:
                return float(line.split()[1])
            except ValueError:
                return None
    return None


def last_event():
    """Observer em memoria: o evento vive em collector.RUN, nao no disco."""
    return collector.RUN["requests"][-1]


def check(name, cond, detail=""):
    global oks
    print(("PASS " if cond else "FAIL ") + name + (f"  [{detail}]" if detail and not cond else ""))
    if cond:
        oks += 1
    else:
        fails.append(name)


LOOP = "Determinando os projetos a serem restaurados... Todos os projetos estao atualizados para restauracao.\n"
# 1) ciclo degenerado: mesma saida repetida muitas vezes
collector.record("/v1/chat/completions", body("build quebrado"),
                 sse(*(LOOP * 12).splitlines(keepends=True)))
e = last_event()
check("loop: dup_lines >= 10", (e["dup_lines"] or 0) >= 10, e["dup_lines"])
check("loop: tail_sim >= 0.85", (e["tail_sim"] or 0) >= 0.85, e["tail_sim"])
check("loop: tail_reps >= 1", (e["tail_reps"] or 0) >= 1, e["tail_reps"])
check("loop: resp_chars > 500", (e["resp_chars"] or 0) > 500, e["resp_chars"])
check("loop: tokens/tps lidos", e["prompt_tokens"] == 10 and e["tps"] == 42.0, e)

# 2) texto limpo: sem repeticao
clean = ("A classe ProductRepository usa cache-aside com IDistributedCache. "
         "No miss ela consulta o Postgres via Dapper e repopula a chave. "
         "O SetAsync e o DeleteAsync invalidam a chave com RemoveAsync.")
collector.record("/v1/chat/completions", body("redis-cache"), sse(clean))
e = last_event()
check("limpo: tail_sim < 0.85", (e["tail_sim"] or 0) < 0.85, e["tail_sim"])
check("limpo: dup_lines <= 1", (e["dup_lines"] or 0) <= 1, e["dup_lines"])
check("limpo: finish lido", e["finish"] == "stop", e["finish"])

# 3) streak: mesmo enunciado reenviado em sequencia
check("streak: 1a chamada = 1", e["prompt_streak"] == 1, e["prompt_streak"])
collector.record("/v1/chat/completions", body("redis-cache"), sse(clean))
check("streak: repeticao = 2", last_event()["prompt_streak"] == 2, last_event()["prompt_streak"])
collector.record("/v1/chat/completions", body("outro enunciado"), sse(clean))
check("streak: enunciado novo volta a 1", last_event()["prompt_streak"] == 1,
      last_event()["prompt_streak"])

# 4) tool call do modelo (inclusive subagente) e registrada
collector.record("/v1/chat/completions", body("delegue", tools=3),
                 sse_tool("subagent", '{"agent":"scripter","prompt":"OBJETIVO: x"}'))
e = last_event()
check("tool: nome capturado", e["resp_tools"] == ["subagent"], e["resp_tools"])
check("tool: finish=tool_calls", e["finish"] == "tool_calls", e["finish"])
check("tool: tools do request contadas", e["tools"] == 3, e["tools"])
check("tool: argumentos capturados", "scripter" in (e.get("tool_args") or ""), e.get("tool_args"))

# 4b) laco de TOOL: mesma tool+args em sequencia (o que a prosa nao mostra)
for _ in range(3):
    collector.record("/v1/chat/completions", body("loop de tool"),
                     sse_tool("shell", '{"command":"cd src/src/Data"}'))
e = last_event()
check("tool-loop: mesma assinatura soma", e["tool_sig_streak"] >= 3, e["tool_sig_streak"])
check("tool-loop: assinatura gravada", "shell" in (e.get("tool_sig") or ""), e.get("tool_sig"))
check("tool-loop: max no summary", collector.summary()["tool_loop_max"] >= 3,
      collector.summary()["tool_loop_max"])
collector.record("/v1/chat/completions", body("loop de tool"),
                 sse_tool("shell", '{"command":"ls"}'))
check("tool-loop: args diferente reseta", last_event()["tool_sig_streak"] == 1,
      last_event()["tool_sig_streak"])
collector.record("/v1/chat/completions", body("resposta sem tool"), sse(clean))
check("tool-loop: resposta sem tool zera", last_event()["tool_sig_streak"] == 0,
      last_event()["tool_sig_streak"])
check("tool-loop: hits contados no summary", collector.summary()["tool_loop_hits"] >= 1,
      collector.summary()["tool_loop_hits"])

# 4c) anti-repeat: nudge no laco de tool (texto e tool) e corte em modo stop
collector.ANTI, collector.NUDGE_AT, collector.STOP_AT = "nudge", 3, 6
for _ in range(3):
    collector.record("/v1/chat/completions", body("loop"),
                     sse_tool("shell", '{"command":"cd src/src/Data"}'))
b = body("loop")
b["messages"] = [{"role": "user", "content": "loop"}]
b["stream"] = True
raw = json.dumps(b).encode()
out, stop, nudged, stopped = collector.anti_repeat(b, raw)
check("anti-repeat: nudge injetado no laco de tool", nudged and stop is None and not stopped)
nj = json.loads(out)
check("anti-repeat: aviso vai como mensagem extra",
      "[anti-repeat]" in nj["messages"][-1]["content"], nj["messages"][-1]["content"][:50])
check("anti-repeat: aviso cita a tool repetida", "shell" in nj["messages"][-1]["content"])
check("anti-repeat: request original preservado", nj["messages"][0]["content"] == "loop")
check("anti-repeat: contador de nudge", collector.NUDGES >= 1, collector.NUDGES)

collector.ANTI = "stop"
for _ in range(4):
    collector.record("/v1/chat/completions", body("loop"),
                     sse_tool("shell", '{"command":"cd src/src/Data"}'))
out, stop, nudged, stopped = collector.anti_repeat(b, raw)
check("anti-repeat: stop corta o laco", stopped and stop is not None and not nudged)
check("anti-repeat: stop responde SSE valido", b"data: [DONE]" in stop and b"finish_reason" in stop)
check("anti-repeat: stop conta", collector.STOPS >= 1, collector.STOPS)
check("anti-repeat: stop no summary", collector.summary()["stops"] >= 1)

collector.ANTI = "nudge"
collector.LAST_SIG, collector.SIG_STREAK = "", 0
collector.SIG_HIST.clear()
collector.CYCLE = 0
collector.PROBE.update({"dup_lines": 0, "tail_sim": 0.0})
b2 = body("enunciado novo")
b2["messages"] = [{"role": "user", "content": "enunciado novo"}]
raw2 = json.dumps(b2).encode()
out2, stop2, nudged2, stopped2 = collector.anti_repeat(b2, raw2)
check("anti-repeat: sem laco nao mexe no request",
      not nudged2 and not stopped2 and out2 == raw2)

# 4d) ciclo A,B,A,B: a assinatura ALTERNA, o streak consecutivo fica em 1 e o laco
# escapa do contador; o detector de ciclo (<=2 assinaturas no sufixo) tem que pegar.
collector.ANTI, collector.NUDGE_AT, collector.STOP_AT = "stop", 3, 6
collector.SIG_HIST.clear()
collector.CYCLE = 0
for _ in range(3):
    collector.record("/v1/chat/completions", body("MSBUILD : error MSB1001: Opcao desconhecida"),
                     sse_tool("shell", '{"command":"dotnet build x.cs"}'))
    collector.record("/v1/chat/completions", body("Could not find oldString in x.cs"),
                     sse_tool("edit", '{"filePath":"x.cs"}'))
check("ciclo A,B: streak consecutivo fica 1", last_event()["tool_sig_streak"] == 1,
      last_event()["tool_sig_streak"])
check("ciclo A,B: ciclo detectado", collector.CYCLE >= 6, collector.CYCLE)
out, stop, nudged, stopped = collector.anti_repeat(b, raw)
check("ciclo A,B: corte no ciclo", stopped and stop is not None and not nudged,
      (nudged, stopped))
# edicao normal (read<->write sem erro) NAO pode ser cortada
collector.SIG_HIST.clear()
collector.CYCLE = 0
collector.STOPS = 0
for _ in range(4):
    collector.record("/v1/chat/completions", body("Wrote file successfully: x.cs"),
                     sse_tool("write", '{"filePath":"x.cs"}'))
    collector.record("/v1/chat/completions", body("x.cs"),
                     sse_tool("read", '{"filePath":"x.cs"}'))
check("ciclo: read<->write sem erro nao conta", collector.CYCLE == 0, collector.CYCLE)
out, stop, nudged, stopped = collector.anti_repeat(b, raw)
check("ciclo: read<->write sem erro nao corta", not stopped, (nudged, stopped))
collector.SIG_HIST.clear()
collector.CYCLE = 0
collector.ANTI = "nudge"

# 5) texto do modelo fica em disco para inspecao (inclusive subagente)
dumps = os.listdir(os.environ["RESP_DIR"])
check("dump: arquivos de resposta gravados", len(dumps) >= 5, len(dumps))

# 5b) memoria-primeiro: nada de escrever por request; disco so no dump
check("memoria: requests acumulados em RUN", len(collector.RUN["requests"]) >= 5,
      len(collector.RUN["requests"]))
check("memoria: events.jsonl intocado antes do dump",
      not os.path.exists(os.environ["EVENTS"]), os.path.exists(os.environ["EVENTS"]))
n = collector.dump_run()
check("dump: escreve a rodada inteira", n >= 5 and os.path.exists(os.environ["EVENTS"]), n)
check("dump: uma linha por request",
      sum(1 for _ in open(os.environ["EVENTS"])) == n, n)
check("dump: copia em runs/<id>.jsonl",
      os.path.exists(os.path.join(os.path.dirname(os.environ["EVENTS"]), "runs",
                                  collector.RUN["id"] + ".jsonl")))

# 5c) REGRA IRREFUTAVEL: reasoning proibido neste tunel
b5 = {"messages": [{"role": "user", "content": "x"}], "chat_template_kwargs": {"enable_thinking": True}}
check("nothink: cliente pedindo true e respeitado (role plan)",
      collector.force_nothink(b5) is True and b5["chat_template_kwargs"]["enable_thinking"] is True,
      b5["chat_template_kwargs"])
b6 = {"messages": [{"role": "user", "content": "x"}]}
check("nothink: kwarg injetado quando ausente",
      collector.force_nothink(b6) is False and b6["chat_template_kwargs"]["enable_thinking"] is False,
      b6.get("chat_template_kwargs"))

# 5d) sentinela do llama: predicted_per_second=1e6 (predicted_ms=0) nao e' velocidade
raw_bad = ("data: " + json.dumps({"choices": [{"delta": {"content": "ok"},
           "finish_reason": "stop"}], "usage": {"prompt_tokens": 5, "completion_tokens": 1},
           "timings": {"predicted_per_second": 1000000.0}}) + "\n\ndata: [DONE]\n\n").encode()
collector.record("/v1/chat/completions", body("tps sentinela"), raw_bad)
check("tps: sentinela 1e6 descartada", last_event()["tps"] == 0, last_event()["tps"])

# 5e) THINK_FORCE=on sobrepoe a regra e forca reasoning
collector.THINK_FORCE = "on"
b7 = {"messages": [{"role": "user", "content": "x"}]}
check("think force on: injeta true", collector.force_thinking(b7) is True
      and b7["chat_template_kwargs"]["enable_thinking"] is True, b7.get("chat_template_kwargs"))
b8 = {"messages": [{"role": "user", "content": "x"}], "chat_template_kwargs": {"enable_thinking": True}}
check("think force on: true se mantem", collector.force_thinking(b8) is False
      and b8["chat_template_kwargs"]["enable_thinking"] is True, b8.get("chat_template_kwargs"))
collector.THINK_FORCE = "off"

# 6) /metrics expoe as gauges da sonda
m = collector.metrics().decode()
for gauge in ("oc_tail_similarity", "oc_tail_repetitions", "oc_dup_lines_max",
              "oc_meta_phrases", "oc_prompt_repeat_streak", "oc_tool_calls_total",
              "oc_response_chars", "oc_thinking_blocked_total"):
    check(f"metrics: {gauge}", gauge in m)

# 6b) tabela do painel: ultimos requests como series rotuladas
m = collector.metrics().decode()
check("metrics: oc_recent_requests presente", "oc_recent_requests{" in m)
check("metrics: tabela tem streak, tsig e finish",
      'streak="' in m and 'tsig="' in m and 'finish="' in m)

# 6c) ocioso: gauges vivas zeradas e tabela vazia (nada rodando = zero no painel)
collector._go_idle()
m = collector.metrics().decode()
check("idle: oc_idle=1", mv(m, "oc_idle") == 1, mv(m, "oc_idle"))
check("idle: tps zerado", mv(m, "oc_tokens_per_second") == 0, mv(m, "oc_tokens_per_second"))
check("idle: similaridade zerada", mv(m, "oc_tail_similarity") == 0, mv(m, "oc_tail_similarity"))
check("idle: streak zerado", mv(m, "oc_prompt_repeat_streak") == 0, mv(m, "oc_prompt_repeat_streak"))
check("idle: tabela vazia", "oc_recent_requests{" not in m)
collector.record("/v1/chat/completions", body("volta"), sse("resposta depois do idle"))
m = collector.metrics().decode()
check("idle: volta ao ativo no request seguinte", mv(m, "oc_idle") == 0, mv(m, "oc_idle"))

# 6d) /api/summary: agregado da rodada vem do observer em memoria
sm = collector.summary()
check("summary: requests = tamanho do RUN",
      sm["requests"] == len(collector.RUN["requests"]), sm["requests"])
check("summary: completion_tokens somados",
      sm["completion_tokens"] == sum(r["completion_tokens"] for r in collector.RUN["requests"]))
check("summary: tps_max >= tps_avg", sm["tps_max"] >= sm["tps_avg"], (sm["tps_max"], sm["tps_avg"]))
check("summary: tem run id", bool(sm["run"]) and "-" in sm["run"], sm["run"])

# 7) devagacao: prosa de plano contada
collector.record("/v1/chat/completions", body("devagou"),
                 sse("Vou primeiro analisar. Agora vou escrever o arquivo. Em seguida, "
                     "vou rodar o build. Proximo passo: corrigir. Plano: 1) criar 2) compilar."))
check("meta: prosa de plano >= 4", (last_event()["meta"] or 0) >= 4, last_event()["meta"])

# 8) thinking (reasoning_content): o Gemma4 GK pensa antes de responder — a devagacao
#    aparece no thinking, entao a sonda mede os dois separadamente.
def sse_think(*deltas):
    out = [json.dumps({"choices": [{"delta": {"reasoning_content": x}}]}) for x in deltas]
    out.append(json.dumps({"choices": [{"delta": {"content": "ok"}}]}))
    out.append(json.dumps({"choices": [{"delta": {}, "finish_reason": "stop"}],
                           "usage": {"prompt_tokens": 5, "completion_tokens": 3},
                           "timings": {"predicted_per_second": 30.0}}))
    return ("\n\n".join("data: " + x for x in out) + "\n\n").encode()


collector.record("/v1/chat/completions", body("pense"),
                 sse_think("Vou analisar o pedido. ", "Agora vou escrever o codigo. ",
                           "Em seguida vou compilar. "))
e = last_event()
check("thinking: capturado", (e["think_chars"] or 0) > 40, e["think_chars"])
check("thinking: prosa de plano contada", (e["think_meta"] or 0) >= 3, e["think_meta"])
check("thinking: resposta separada", e["resp_chars"] == 2, e["resp_chars"])
dumps = [open(os.path.join(os.environ["RESP_DIR"], f)).read()
         for f in os.listdir(os.environ["RESP_DIR"])]
check("thinking: vai para o dump", any("[thinking]" in d for d in dumps))

print(f"\n== RESUMO: PASS={oks} FAIL={len(fails)}"
      f"  (eventos: {os.environ['EVENTS']})")
sys.exit(1 if fails else 0)
