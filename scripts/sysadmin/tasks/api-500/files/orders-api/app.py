#!/usr/bin/env python3
"""orders-api: the shop's order list, as JSON over HTTP on 127.0.0.1:8000 (nginx serves it as /api/)."""
import json
import os
import sys
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

DATA = "/var/lib/orders-api/orders.json"


class Handler(BaseHTTPRequestHandler):
    def send_json(self, code, obj):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def fail(self):
        traceback.print_exc()
        sys.stderr.flush()
        self.send_json(500, {"error": "internal error"})

    def do_GET(self):
        if self.path == "/health":
            return self.send_json(200, {"ok": True})
        if self.path == "/orders":
            try:
                with open(DATA) as f:
                    return self.send_json(200, json.load(f))
            except Exception:
                return self.fail()
        self.send_json(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/orders":
            return self.send_json(404, {"error": "not found"})
        try:
            order = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
            with open(DATA) as f:
                orders = json.load(f)
            orders.append(order)
            # Written to a new file and renamed over the old one, so a crash never leaves half a list.
            tmp = DATA + ".tmp"
            with open(tmp, "w") as f:
                json.dump(orders, f)
            os.replace(tmp, DATA)
            self.send_json(201, order)
        except Exception:
            self.fail()

    def log_message(self, fmt, *args):
        sys.stderr.write("%s %s\n" % (self.address_string(), fmt % args))


ThreadingHTTPServer(("127.0.0.1", 8000), Handler).serve_forever()
