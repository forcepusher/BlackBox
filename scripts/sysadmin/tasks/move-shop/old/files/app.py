"""Shop API: the product list from the shop database, as JSON, for the website."""
import http.server
import json
import os

import psycopg2


def connect():
    return psycopg2.connect(host=os.environ["DB_HOST"], dbname=os.environ["DB_NAME"],
                            user=os.environ["DB_USER"], password=os.environ["DB_PASSWORD"])


class Handler(http.server.BaseHTTPRequestHandler):
    def send_json(self, code, value):
        body = json.dumps(value).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.rstrip("/") != "/api/products":
            return self.send_json(404, {"error": "not found"})
        try:
            with connect() as db, db.cursor() as cur:
                cur.execute("SELECT id, name, price_cents FROM products ORDER BY id")
                rows = [{"id": i, "name": n, "price_cents": p} for i, n, p in cur.fetchall()]
        except psycopg2.Error as e:
            print("database error:", e, flush=True)
            return self.send_json(500, {"error": "internal error"})
        self.send_json(200, rows)


http.server.ThreadingHTTPServer(("127.0.0.1", 8000), Handler).serve_forever()
