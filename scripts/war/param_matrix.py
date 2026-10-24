#!/usr/bin/env python3
"""Matriz de amostragem do llama x harness da bridge (cenario de typo/visao).

Mede o defeito real das sessoes: o modelo troca letras do nome de arquivo ao
emitir a tool call (`placa.png` -> `placa.ng`, `001` -> `999`).

Eixos:
  - amostragem do llama, POR REQUEST (repeat_penalty, DRY);
  - temperatura efetiva;
  - harness da bridge (tails on/off, anti-repeat on/off, main harness).

Cenarios:
  chat-vision : /v1/chat/completions com imagem + tool required -> nao passa
                pelas tails (o shim de tools sim); isola a amostragem.
  resp-path   : /v1/responses com tool write -> caminho opencode/Web, onde as
                tails e o anti-repeat atuam; isola o harness.

Saida: <out>/runs.tsv (uma linha por chamada) + <out>/summary.txt e resumo no
console. Nada e inferido: o veredito vem do argumento emitido.

Uso:
  scripts/war/param_matrix.py --stage 1 --reps 5
  scripts/war/param_matrix.py --stage 2 --reps 5 --sampling baseline
  scripts/war/param_matrix.py --stage all --reps 3
"""
import argparse
import base64
import json
import os
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.environ.get("LEVEL_ROOT") or os.environ.get("REPO") or os.path.dirname(os.path.dirname(HERE))
DLL = os.path.join(REPO, "Rochas.OpenCodeBridge", "bin", "Release", "net9.0", "Rochas.OpenCodeBridge.dll")
KEY = os.environ.get("LEVEL_API_KEY", "guest@123")
TESTDATA = os.path.join(REPO, "Rochas.OpenCodeBridge.Test", "TestData")

# ---------------------------------------------------------------- gateways
def http_json(url, body=None, timeout=600):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method="POST" if data else "GET")
    req.add_header("Authorization", "Bearer " + KEY)
    req.add_header("Content-Type", "application/json")
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read().decode()
        return json.loads(raw) if raw.strip() else {}, time.time() - t0
    except urllib.error.HTTPError as e:
        return {"_error": f"HTTP {e.code}: {e.read().decode()[:400]}"}, time.time() - t0
    except Exception as e:  # noqa: BLE001
        return {"_error": repr(e)}, time.time() - t0


class Bridge:
    """Uma bridge efemera por variante de harness."""

    def __init__(self, port, upstream, model, flags, logpath):
        self.port, self.upstream, self.model = port, upstream, model
        self.flags, self.logpath = flags, logpath
        self.proc = None

    def start(self):
        cmd = ["/usr/bin/dotnet", DLL, "--listen", "127.0.0.1", "--port", str(self.port),
               "--upstream", self.upstream, "--model", self.model, "--api-key", KEY,
               "--log", self.logpath, "--diagnostic", "1", "--sampling-passthrough", "1",
               "--context-tokens", "131072"] + self.flags
        self.proc = subprocess.Popen(cmd, stdout=open(self.logpath + ".out", "w"), stderr=subprocess.STDOUT)
        for _ in range(120):
            st, _ = http_json(f"http://127.0.0.1:{self.port}/api/status", timeout=3)
            if st.get("status") == "ok":
                return st
            if self.proc.poll() is not None:
                raise RuntimeError("bridge morreu: " + open(self.logpath + ".out").read()[-500:])
            time.sleep(0.25)
        raise RuntimeError("bridge nao subiu")

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.send_signal(signal.SIGTERM)
            try:
                self.proc.wait(10)
            except subprocess.TimeoutExpired:
                self.proc.kill()


# ---------------------------------------------------------------- cenarios
def img64(name):
    with open(os.path.join(TESTDATA, name), "rb") as f:
        return base64.b64encode(f.read()).decode()


VISION_TARGET = "/tmp/vlimg/placa.png"
PATH_TARGET = "/tmp/warmat/placa-001.png"


def payload_chat_vision(port, model, temp, sampling):
    body = {
        "model": model + "-build",
        "temperature": temp,
        "stream": False,
        "tool_choice": "required",
        "messages": [
            {"role": "system", "content": "Voce e um desenvolvedor senior."},
            {"role": "user", "content": [
                {"type": "text", "text": "Identifique o texto da imagem via visao e CHAME a tool read_file com file_path /tmp/vlimg/placa.png."},
                {"type": "image_url", "image_url": {"url": "data:image/png;base64," + img64("placa.png")}},
            ]},
        ],
        "tools": [{"type": "function", "function": {
            "name": "read_file", "description": "Le arquivo",
            "parameters": {"type": "object", "properties": {"file_path": {"type": "string"}}, "required": ["file_path"]}}}],
    }
    body.update(sampling)
    return f"http://127.0.0.1:{port}/v1/chat/completions", body


def payload_resp_path(port, model, temp, sampling):
    body = {
        "model": model + "-build",
        "temperature": temp,
        "input": [{"type": "message", "role": "user", "content": [
            {"type": "input_text", "text": "Crie o arquivo /tmp/warmat/placa-001.png com o conteudo TESTE 456 usando a tool write_file. Nao escreva texto: chame a tool."}]}],
        "tools": [{"type": "function", "name": "write_file", "description": "Grava arquivo",
                   "parameters": {"type": "object", "properties": {"path": {"type": "string"}, "content": {"type": "string"}},
                                  "required": ["path", "content"]}}],
    }
    body.update(sampling)
    return f"http://127.0.0.1:{port}/v1/responses", body


def extract(resp, scenario):
    """(nome, args_str, n_calls, finish) independente do endpoint."""
    name, args, n, finish = "", "", 0, ""
    if scenario == "chat-vision":
        ch = (resp.get("choices") or [{}])[0]
        finish = ch.get("finish_reason") or ""
        tcs = (ch.get("message") or {}).get("tool_calls") or []
        n = len(tcs)
        if tcs:
            fn = tcs[0].get("function") or {}
            name, args = fn.get("name") or "", fn.get("arguments") or ""
    else:
        finish = resp.get("status") or ""
        for item in resp.get("output") or []:
            if item.get("type") == "function_call":
                n += 1
                if not name:
                    name, args = item.get("name") or "", item.get("arguments") or ""
            if item.get("type") == "message":
                for c in item.get("content") or []:
                    if c.get("type") == "output_text":
                        finish = (finish or "") + "|text"
    return name, args, n, finish


def arg_path(args, key):
    try:
        return json.loads(args).get(key) or ""
    except Exception:  # noqa: BLE001
        return ""


def distance(a, b):
    """Levenshtein simples: mede a mutacao do nome emitido."""
    if a == b:
        return 0
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


# ---------------------------------------------------------------- variantes
SAMPLING = {
    "rp1.1_dry0.8": {"repeat_penalty": 1.1, "dry_multiplier": 0.8, "dry_penalty_last_n": 1024},
    "rp1.0_dry0.8": {"repeat_penalty": 1.0, "dry_multiplier": 0.8, "dry_penalty_last_n": 1024},
    "rp1.1_dry0.0": {"repeat_penalty": 1.1, "dry_multiplier": 0.0, "dry_penalty_last_n": 1024},
    "rp1.0_dry0.0": {"repeat_penalty": 1.0, "dry_multiplier": 0.0, "dry_penalty_last_n": 1024},
}
TAILS_ON = ["--main-harness", "0", "--harness-tails", "1", "--tail-scope", "all",
            "--anti-repeat", "1", "--anti-repeat-retry", "1", "--anti-repeat-window", "512"]
HARNESS = {
    "tails_ar_on": TAILS_ON,
    "tails_ar_off": ["--main-harness", "0", "--harness-tails", "1", "--tail-scope", "all",
                     "--anti-repeat", "0", "--anti-repeat-retry", "0"],
    "tails_off_ar_on": ["--main-harness", "0", "--harness-tails", "0",
                        "--anti-repeat", "1", "--anti-repeat-retry", "1", "--anti-repeat-window", "512"],
    "main_harness": ["--main-harness", "1", "--harness-tails", "0",
                     "--anti-repeat", "1", "--anti-repeat-retry", "1"],
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--stage", default="1", choices=["1", "2", "3", "all"])
    ap.add_argument("--reps", type=int, default=5)
    ap.add_argument("--llama", default=os.environ.get("MATRIX_LLAMA", "http://127.0.0.1:4100"))
    ap.add_argument("--port", type=int, default=int(os.environ.get("MATRIX_PORT", "4144")))
    ap.add_argument("--model", default=os.environ.get("MATRIX_MODEL", "Gemma4-E4B-Q4"))
    ap.add_argument("--sampling", default="baseline", help="variante fixa das etapas 2/3")
    ap.add_argument("--harness", default="tails_ar_on", help="variante fixa das etapas 1/3")
    ap.add_argument("--temp", default="0.1", help="temperatura fixa das etapas 1/2")
    ap.add_argument("--out", default=os.environ.get("MATRIX_OUT", ""))
    args = ap.parse_args()

    if not os.path.exists(DLL):
        sys.exit(f"bridge nao buildada: {DLL}")
    out = args.out or time.strftime("/tmp/opencode/matrix/%Y%m%d-%H%M%S")
    os.makedirs(os.path.join(out, "logs"), exist_ok=True)
    runs_path = os.path.join(out, "runs.tsv")
    first = not os.path.exists(runs_path)
    runs = open(runs_path, "a")
    if first:
        runs.write("stage\tsampling\ttemp\tharness\tscenario\trep\texact\tname\tpath\tmut\tn_calls\tfinish\tsecs\terror\n")

    stages = [args.stage] if args.stage != "all" else ["1", "2", "3"]
    print(f"== matriz {out} | stages={stages} reps={args.reps} llama={args.llama} model={args.model}")

    for stage in stages:
        if stage == "1":      # amostragem do llama (harness fixo)
            sampling_set, harness_set, temp_set = list(SAMPLING), [args.harness], [args.temp]
        elif stage == "2":    # harness (amostragem fixa)
            key = args.sampling if args.sampling in SAMPLING else "rp1.1_dry0.8"
            sampling_set, harness_set, temp_set = [key], list(HARNESS), [args.temp]
        else:                 # temperatura (resto fixo)
            key = args.sampling if args.sampling in SAMPLING else "rp1.1_dry0.8"
            sampling_set, harness_set = [key], [args.harness]
            temp_set = ["0.0", "0.1", "0.2"]

        for harness in harness_set:
            logpath = os.path.join(out, "logs", f"bridge-{stage}-{harness}.log")
            br = Bridge(args.port, args.llama, args.model, HARNESS[harness], logpath)
            st = br.start()
            print(f"   bridge :{args.port} harness={harness} tails={st.get('harness_tails')} main={st.get('main_harness')} sampling={st.get('sampling_passthrough')}")
            try:
                for sampling in sampling_set:
                    for temp in temp_set:
                        for scenario in ("chat-vision", "resp-path"):
                            for rep in range(1, args.reps + 1):
                                build = payload_chat_vision if scenario == "chat-vision" else payload_resp_path
                                url, body = build(args.port, args.model, float(temp), SAMPLING[sampling])
                                resp, secs = http_json(url, body)
                                target = VISION_TARGET if scenario == "chat-vision" else PATH_TARGET
                                keyarg = "file_path" if scenario == "chat-vision" else "path"
                                err = resp.get("_error", "")
                                name, targs, ncalls, finish = extract(resp, scenario)
                                got = arg_path(targs, keyarg)
                                exact = got == target
                                runs.write("\t".join(map(str, [
                                    stage, sampling, temp, harness, scenario, rep,
                                    int(exact), name, got or "-",
                                    distance(got, target) if got else -1,
                                    ncalls, finish or "-", f"{secs:.1f}", err[:200]])) + "\n")
                                runs.flush()
                                print(f"   [{stage}] {sampling} t={temp} {harness} {scenario} #{rep} "
                                      f"{'OK' if exact else 'TYPO(' + str(distance(got, target)) + ')'} "
                                      f"path={got or '-'} calls={ncalls} {secs:.1f}s {err[:80]}")
            finally:
                br.stop()

    # resumo
    rows = [l.rstrip("\n").split("\t") for l in open(runs_path)][1:]
    hdr = "sampling\ttemp\tharness\tscenario\tn\texact%score_mut\ttokens_med\n"
    agg = {}
    for r in rows:
        k = (r[1], r[2], r[3], r[4])
        a = agg.setdefault(k, [0, 0])
        a[0] += 1
        a[1] += int(r[6])
    lines = [hdr]
    for k in sorted(agg):
        n, ok = agg[k]
        lines.append(f"{k[0]}\t{k[1]}\t{k[2]}\t{k[3]}\t{n}\t{100*ok/n:.0f}%\t-\t-\n")
    summary = os.path.join(out, "summary.txt")
    with open(summary, "w") as f:
        f.writelines(lines)
    print("\n== resumo ==")
    print("".join(lines))
    print("artefatos:", out)


if __name__ == "__main__":
    main()
