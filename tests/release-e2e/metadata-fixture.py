"""Deterministic metadata fixture for the release-tier browser E2E (compose.release-e2e.yaml). Never use it for a real deployment.

It answers the Open Library request shapes Family Librarian uses, over HTTPS on the Compose-network alias the application
already calls, so the deployed queue test does not depend on a live public service. Every searched text becomes one
fictional Work whose title is exactly that text and whose id is derived from it, so a test that searches a unique title gets
its own record and its own request on every run. Each request is logged as one JSON line for diagnosis.
"""
import hashlib
import json
import re
import ssl
import sys
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

AUTHOR_KEY = "OL1000001A"
AUTHOR_NAME = "Fixture Author"
TITLES: dict[str, str] = {}


def work_id(title: str) -> str:
    digits = int(hashlib.sha256(title.casefold().encode("utf-8")).hexdigest()[:12], 16) % 1_000_000_000
    return f"OL{digits + 1_000_000_000}W"


def document(key: str, title: str) -> dict:
    edition = f"OL{key[2:-1]}M"
    return {
        "key": f"/works/{key}",
        "title": title,
        "author_name": [AUTHOR_NAME],
        "author_key": [AUTHOR_KEY],
        "first_publish_year": 2001,
        "language": ["eng"],
        "editions": {"numFound": 1, "docs": [
            {"key": f"/books/{edition}", "title": title, "language": ["eng"], "publish_date": ["2001"]}]},
    }


def page(rows: list) -> dict:
    return {"numFound": len(rows), "start": 0, "numFoundExact": True, "docs": rows}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def reply(self, status: int, body: dict) -> None:
        payload = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)
        print(json.dumps({"t": time.time(), "target": self.path, "status": status}), flush=True)

    def do_GET(self) -> None:
        url = urllib.parse.urlsplit(self.path)
        query = urllib.parse.parse_qs(url.query)
        text = (query.get("q") or [""])[0].strip()

        if url.path == "/search.json":
            match = re.fullmatch(r"key:/works/(OL\d+W)", text)
            if match:
                key = match.group(1)
                return self.reply(200, page([document(key, TITLES[key])] if key in TITLES else []))
            if not text or text.startswith(("isbn:", "series_key:")) or "author" in query:
                return self.reply(200, page([]))
            key = work_id(text)
            TITLES[key] = text
            return self.reply(200, page([document(key, text)]))

        match = re.fullmatch(r"/works/(OL\d+W)/editions\.json", url.path)
        if match and match.group(1) in TITLES:
            key = match.group(1)
            title = TITLES[key]
            return self.reply(200, {"size": 1, "entries": [
                {"key": f"/books/OL{key[2:-1]}M", "title": title, "languages": [{"key": "/languages/eng"}],
                 "publish_date": "2001"}]})

        if url.path == f"/authors/{AUTHOR_KEY}.json":
            return self.reply(200, {"key": f"/authors/{AUTHOR_KEY}", "name": AUTHOR_NAME})

        return self.reply(404, {"error": "notfound"})


if __name__ == "__main__":
    certificate, key_file = sys.argv[1], sys.argv[2]
    server = ThreadingHTTPServer(("0.0.0.0", 443), Handler)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(certificate, key_file)
    server.socket = context.wrap_socket(server.socket, server_side=True)
    print(json.dumps({"event": "ready"}), flush=True)
    server.serve_forever()
