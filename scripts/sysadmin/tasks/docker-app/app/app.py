"""Status page: records checks in /data/checks.log and shows how many there are."""
import http.server
import os
import time

DATA = "/data/checks.log"
BUILD = "__TOKEN__"


def count():
    try:
        with open(DATA, encoding="utf-8") as f:
            return sum(1 for _ in f)
    except FileNotFoundError:
        return 0


class Handler(http.server.BaseHTTPRequestHandler):
    def send_text(self, code, text):
        body = text.encode()
        self.send_response(code)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/":
            self.send_text(200, f"Status page (build {BUILD}): {count()} checks recorded\n")
        else:
            self.send_text(404, "not found\n")

    def do_POST(self):
        if self.path != "/checks":
            return self.send_text(404, "not found\n")
        length = int(self.headers.get("Content-Length") or 0)
        note = self.rfile.read(length).decode(errors="replace").strip() or "ok"
        os.makedirs(os.path.dirname(DATA), exist_ok=True)
        with open(DATA, "a", encoding="utf-8") as f:
            f.write(f"{time.strftime('%Y-%m-%dT%H:%M:%S')} {note}\n")
        self.send_text(201, f"recorded, {count()} checks\n")


http.server.ThreadingHTTPServer(("", 8080), Handler).serve_forever()
