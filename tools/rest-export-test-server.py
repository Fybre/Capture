#!/usr/bin/env python3
"""Throwaway local server for testing Capture's REST export against.

Not part of the build — run it yourself, point a Rest export's URL at it, and read
its console output to see exactly what Capture posted (headers, Bearer token, custom
headers, and the JSON body). Any attached file (fileContentBase64) is decoded and
saved to disk next to this script so you can open it and confirm PDF/A / text-layer
processing round-tripped correctly.

Usage:
    python3 tools/rest-export-test-server.py [--port 8787]

Then set the export's URL to http://localhost:8787 (or whatever port you chose).
"""

import argparse
import base64
import json
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw_body = self.rfile.read(length)

        print(f"\n=== {self.command} {self.path} ===")
        for name, value in self.headers.items():
            print(f"  {name}: {value}")

        try:
            body = json.loads(raw_body.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError) as ex:
            print(f"  (body is not valid JSON: {ex})")
            self._respond(400, {"status": "error", "message": "invalid JSON body"})
            return

        document_id = body.get("documentId")
        fields = body.get("fields", {})
        file_name = body.get("fileName")
        file_content_base64 = body.get("fileContentBase64")

        print(f"  documentId: {document_id}")
        print(f"  fields: {json.dumps(fields, indent=2)}")

        if file_content_base64:
            out_dir = Path(__file__).parent
            out_name = f"received_{document_id or 'unknown'}_{file_name or 'attachment'}"
            out_path = out_dir / out_name
            out_path.write_bytes(base64.b64decode(file_content_base64))
            print(f"  saved attachment -> {out_path}")
        else:
            print("  (no attachment in this request)")

        self._respond(200, {"status": "received"})

    def _respond(self, status: int, payload: dict):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format, *args):
        pass  # Replaced by the structured print() calls above.


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8787)
    args = parser.parse_args()

    server = HTTPServer(("localhost", args.port), Handler)
    print(f"Listening on http://localhost:{args.port} — point a Rest export here. Ctrl+C to stop.")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
