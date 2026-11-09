import http.server, socketserver, urllib.request, urllib.error

# Tuneu de observacao: repassa HTTP para o llama e grava METHOD + PATH de cada
# request. E' assim que se descobre QUAL protocolo o opencode usa por padrao
# (chat/completions, responses, ...) sem adivinhar e sem bridge no meio.

UP = "http://127.0.0.1:4100"
LOG = "/tmp/opencode/llama-pure/calls.txt"


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _record(self, extra=""):
        with open(LOG, "a") as f:
            f.write(self.command + " " + self.path + extra + "\n")

    def _forward(self):
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n) if n else b""
        self._record()
        req = urllib.request.Request(UP + self.path, data=body if n else None, method=self.command)
        for k, v in self.headers.items():
            if k.lower() in ("host", "content-length", "connection", "accept-encoding"):
                continue
            req.add_header(k, v)
        try:
            resp = urllib.request.urlopen(req, timeout=900)
            self.send_response(resp.status)
            for k, v in resp.headers.items():
                if k.lower() in ("content-length", "connection", "transfer-encoding"):
                    continue
                self.send_header(k, v)
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()
            while True:
                chunk = resp.read(1)
                if not chunk:
                    break
                self.wfile.write(b"%X\r\n" % len(chunk) + chunk + b"\r\n")
                self.wfile.flush()
            self.wfile.write(b"0\r\n\r\n")
            self.wfile.flush()
        except urllib.error.HTTPError as e:
            data = e.read()
            self._record(" -> " + str(e.code))
            self.send_response(e.code)
            self.send_header("Content-Type", e.headers.get("Content-Type", "application/json"))
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        except Exception as e:
            self._record(" ERR " + type(e).__name__)
            try:
                self.send_error(502)
            except Exception:
                pass

    do_GET = _forward
    do_POST = _forward

    def log_message(self, *args):
        pass


socketserver.ThreadingTCPServer.allow_reuse_address = True
with socketserver.ThreadingTCPServer(("127.0.0.1", 4199), Handler) as server:
    server.serve_forever()
