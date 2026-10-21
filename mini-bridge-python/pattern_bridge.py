#!/usr/bin/env python3
"""Mini bridge: passthrough puro para llama-server com UM unico filtro.

Filtro: remove chaves JSON-Schema `pattern` que o llama.cpp rejeita
("Unable to generate parser ... Pattern must start with '^'"). O gramatica
aceita somente regex ancorada literalmente em ^...$, entao `^(ses)` (ancorada
por grupo, termina em `)`) tambem cai. Todo o resto do payload passa intacto.

Sem harness, sem traducao, sem anti-repeat: e so o patch de `pattern`.
"""
from __future__ import annotations

import argparse
import http.client
import json
import os
import re
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

HOP_BY_HOP = {
    "connection",
    "keep-alive",
    "proxy-authenticate",
    "proxy-authorization",
    "te",
    "trailer",
    "transfer-encoding",
    "upgrade",
    "host",
    "content-length",
}
# Caminhos que carregam schemas de tool (chat/completions e responses).
TOOL_CONTAINERS = ("tools", "functions")

_log_lock = threading.Lock()


class Config:
    upstream: str = "http://127.0.0.1:4100"
    api_key: str | None = None
    log_path: str | None = None
    dump_dir: str | None = None
    verbose: bool = False
    timeout: float = 1800.0


CFG = Config()


def log_event(event: dict) -> None:
    if not CFG.log_path:
        return
    line = json.dumps(event, ensure_ascii=False)
    with _log_lock:
        with open(CFG.log_path, "a", encoding="utf-8") as fh:
            fh.write(line + "\n")


DUMP_CAP = 64 * 1024 * 1024  # 64 MiB por resposta; acima disso marca truncado
_dump_seq = 0
_dump_lock = threading.Lock()


def write_dump(path: str, method: str, status: int, ms: int, raw: bytes,
               chunks: list[bytes], dropped: int, hits: list[str],
               note: str) -> str | None:
    """Grava o stream completo em disco no fim da request (ok, erro ou queda).

    O corpo inteiro fica em memoria durante o relay; nada e escrito antes do
    fim, entao uma resposta cortada no meio tambem sai inteira no dump.
    """
    if not CFG.dump_dir:
        return None
    global _dump_seq
    with _dump_lock:
        _dump_seq += 1
        seq = _dump_seq
    directory = Path(CFG.dump_dir)
    directory.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    safe = re.sub(r"[^A-Za-z0-9]+", "-", path).strip("-") or "root"
    base = directory / f"{stamp}-{seq:04d}-{safe}"
    body = b"".join(chunks)
    base.with_suffix(".stream").write_bytes(body)
    meta = {
        "ts": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "path": path,
        "method": method,
        "status": status,
        "ms": ms,
        "bytes_in": len(raw),
        "bytes_out": len(body),
        "pattern_strip": dropped,
        "hits": hits,
        "note": note,
        "stream": str(base.with_suffix(".stream")),
    }
    base.with_suffix(".meta.json").write_text(
        json.dumps(meta, ensure_ascii=False, indent=1), encoding="utf-8"
    )
    return meta["stream"]


def _is_kept(pattern: str) -> bool:
    """Mantem so o que o llama.cpp sabe compilar: ^...$ literais."""
    return pattern.startswith("^") and pattern.endswith("$") and len(pattern) >= 2


def strip_unanchored_patterns(node, path: str = "$", hits: list[str] | None = None) -> int:
    """Remove recursivamente `pattern` que nao seja ^...$ (muta no lugar)."""
    dropped = 0
    if isinstance(node, dict):
        pat = node.get("pattern")
        if isinstance(pat, str) and pat and not _is_kept(pat):
            node.pop("pattern", None)
            if hits is not None:
                hits.append(f"{path}.pattern")
            dropped += 1
        for key, value in node.items():
            dropped += strip_unanchored_patterns(value, f"{path}.{key}", hits)
    elif isinstance(node, list):
        for idx, value in enumerate(node):
            dropped += strip_unanchored_patterns(value, f"{path}[{idx}]", hits)
    return dropped


def patch_body(raw: bytes) -> tuple[bytes, int, list[str], list[str]]:
    """Aplica o filtro so nos containers de tools. Retorna bytes originais se nada mudou."""
    try:
        payload = json.loads(raw)
    except (ValueError, UnicodeDecodeError):
        return raw, 0, [], []
    if not isinstance(payload, dict):
        return raw, 0, [], []

    hits: list[str] = []
    dropped = 0
    for key in TOOL_CONTAINERS:
        if key in payload:
            dropped += strip_unanchored_patterns(payload[key], f"$.{key}", hits)
    if dropped == 0:
        return raw, 0, [], []

    names = tool_names(payload)
    return (
        json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
        dropped,
        hits,
        names,
    )


def tool_names(payload: dict) -> list[str]:
    names: list[str] = []
    for key in TOOL_CONTAINERS:
        arr = payload.get(key)
        if not isinstance(arr, list):
            continue
        for item in arr:
            if not isinstance(item, dict):
                continue
            fn = item.get("function") if isinstance(item.get("function"), dict) else item
            name = fn.get("name")
            if isinstance(name, str):
                names.append(name)
    return names


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "pattern-bridge/1.0"

    def log_message(self, fmt: str, *args) -> None:  # noqa: A003
        if CFG.verbose:
            sys.stderr.write("[%s] %s\n" % (self.log_date_time_string(), fmt % args))

    def do_GET(self) -> None:
        if self.path.rstrip("/") == "/health":
            body = json.dumps(
                {
                    "ok": True,
                    "upstream": CFG.upstream,
                    "dump_stream": CFG.dump_dir,
                    "filter": "tools[].function.parameters.pattern !^...$",
                }
            ).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        self.forward("GET")

    def do_POST(self) -> None:
        self.forward("POST")

    def do_PUT(self) -> None:
        self.forward("PUT")

    def do_PATCH(self) -> None:
        self.forward("PATCH")

    def do_DELETE(self) -> None:
        self.forward("DELETE")

    def do_OPTIONS(self) -> None:
        self.forward("OPTIONS")

    def _read_body(self) -> bytes:
        length = self.headers.get("Content-Length")
        if length is None:
            if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
                chunks = []
                while True:
                    size_line = self.rfile.readline().strip()
                    size = int(size_line.split(b";")[0] or b"0", 16)
                    if size == 0:
                        self.rfile.readline()
                        break
                    chunks.append(self.rfile.read(size))
                    self.rfile.readline()
                return b"".join(chunks)
            return b""
        return self.rfile.read(int(length))

    def forward(self, method: str) -> None:
        started = time.monotonic()
        raw = self._read_body() if method in ("POST", "PUT", "PATCH", "DELETE") else b""

        body = raw
        dropped = 0
        hits: list[str] = []
        names: list[str] = []
        if raw and "json" in (self.headers.get("Content-Type") or "").lower():
            body, dropped, hits, names = patch_body(raw)

        headers = {
            k: v for k, v in self.headers.items() if k.lower() not in HOP_BY_HOP
        }
        headers.pop("Accept-Encoding", None)
        if CFG.api_key:
            headers["Authorization"] = f"Bearer {CFG.api_key}"
        if body:
            headers["Content-Length"] = str(len(body))

        parts = urlsplit(CFG.upstream)
        conn = http.client.HTTPConnection(
            parts.hostname, parts.port or 80, timeout=CFG.timeout
        )
        status = 0
        resp_chunks: list[bytes] = []
        dump_note = ""
        try:
            conn.request(method, self.path, body=body or None, headers=headers)
            resp = conn.getresponse()
            status = resp.status
            is_sse = "event-stream" in (resp.getheader("Content-Type") or "").lower()

            # Erro: bufferiza para preservar o corpo exato (diagnostico) e responder
            # com Content-Length. Sucesso: passa em fluxo, sem tocar no payload.
            if status >= 400:
                data = resp.read()
                resp_chunks.append(data)
                error_body = data.decode("utf-8", "replace")[:1200]
                self.send_response_only(status, resp.reason)
                for key, value in resp.getheaders():
                    if key.lower() in HOP_BY_HOP or key.lower() == "content-encoding":
                        continue
                    self.send_header(key, value)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
                self.wfile.flush()
                log_event(
                    {
                        "ts": time.strftime("%Y-%m-%dT%H:%M:%S"),
                        "path": self.path,
                        "method": method,
                        "status": status,
                        "error_body": error_body,
                        "bytes_in": len(raw),
                        "pattern_strip": dropped,
                        "hits": hits,
                    }
                )
                return

            self.send_response_only(resp.status, resp.reason)
            for key, value in resp.getheaders():
                low = key.lower()
                if low in HOP_BY_HOP:
                    continue
                if low == "content-encoding":
                    continue
                self.send_header(key, value)
            if not is_sse:
                # tamanho desconhecido: fecha a conexao ao final
                self.send_header("Transfer-Encoding", "chunked")
            else:
                # SSE tambem vai chunked: sem isso o cliente HTTP/1.1 nao ve o fim
                # do corpo e fica pendurado mesmo apos o [DONE].
                self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()

            tail = b""
            while True:
                chunk = resp.read(4096)
                if not chunk:
                    break
                if sum(len(c) for c in resp_chunks) < DUMP_CAP:
                    resp_chunks.append(chunk)
                elif not dump_note:
                    dump_note = f"truncado em {DUMP_CAP} bytes"
                self.wfile.write(b"%x\r\n" % len(chunk))
                self.wfile.write(chunk)
                self.wfile.write(b"\r\n")
                self.wfile.flush()
                # llama pode manter a conexao aberta depois do [DONE] (keep-alive).
                # Sem encerrar aqui, o cliente fica pendurado esperando o fim do corpo.
                if is_sse:
                    tail = (tail + chunk)[-128:]
                    if b"data: [DONE]" in tail:
                        break
            self.wfile.write(b"0\r\n\r\n")
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            status = status or 499
            dump_note = dump_note or "cliente desconectou (broken pipe)"
        except Exception as exc:  # noqa: BLE001
            dump_note = dump_note or f"excecao: {exc}"
            if not status:
                status = 502
                payload = json.dumps({"error": {"message": f"pattern-bridge: {exc}"}}).encode()
                try:
                    self.send_response_only(502, "Bad Gateway")
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(payload)))
                    self.end_headers()
                    self.wfile.write(payload)
                except OSError:
                    pass
        finally:
            conn.close()
            dump_path = write_dump(
                self.path, method, status,
                round((time.monotonic() - started) * 1000),
                raw, resp_chunks, dropped, hits,
                dump_note or ("timeout" if status == 0 else ""),
            )

        log_event(
            {
                "ts": time.strftime("%Y-%m-%dT%H:%M:%S"),
                "path": self.path,
                "method": method,
                "status": status,
                "ms": round((time.monotonic() - started) * 1000),
                "bytes_in": len(raw),
                "pattern_strip": dropped,
                "hits": hits,
                "tools": names,
                "dump": dump_path,
            }
        )
        if dropped and CFG.verbose:
            sys.stderr.write(f"pattern-strip: {dropped} {hits}\n")


def main() -> int:
    ap = argparse.ArgumentParser(description="mini bridge: pattern-strip + passthrough")
    ap.add_argument("--listen", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=4300)
    ap.add_argument("--upstream", default="http://127.0.0.1:4100")
    ap.add_argument("--api-key", default=None)
    ap.add_argument("--log", default=None, help="JSONL de cada request")
    ap.add_argument("--dump-stream", default=None, metavar="DIR",
                    help="grava o stream completo (memoria->disco) no fim de cada request")
    ap.add_argument("--timeout", type=float, default=1800.0)
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    CFG.upstream = args.upstream.rstrip("/")
    CFG.api_key = args.api_key
    CFG.log_path = args.log
    CFG.dump_dir = args.dump_stream
    CFG.verbose = args.verbose
    CFG.timeout = args.timeout

    httpd = ThreadingHTTPServer((args.listen, args.port), Handler)
    httpd.daemon_threads = True
    print(
        f"pattern-bridge :{args.port} -> {CFG.upstream} (filtro: pattern !^...$)",
        flush=True,
    )
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
