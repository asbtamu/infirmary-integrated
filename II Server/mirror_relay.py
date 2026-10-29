"""Infirmary Integrated mirror relay: a stdlib-only stand-in for the upstream PHP + MySQL relay.

Runs on the campus relay PC (see Setup.md) as its own service, apart from OSCEtime's relay, so a
fault here can never touch camera privacy. II Command posts each room's current step; room PCs
running II:SIM --receive poll for it every 5 s. Speaks exactly what II Library/Classes/Server.cs
sends and parses, so II needs no change beyond the server address:

    GET /mirror_get.php?accession=&accesshash=                  -> "updated\\npatient\\n", or empty
    GET /mirror_post.php?accession=&key_access=&key_edit=&patient=&updated=
                                                                 -> ENTRY_ADDED | ENTRY_UPDATED | BAD_PASSWORD
    GET /mirror_status.php?accession=&key_edit=                  -> seconds since a room PC last read
                                                                    this accession, "" if never (added here:
                                                                    II Command's "receiving" indicator)
    GET /version.php                                             -> "" (the fork's update check is off)

Values are parsed the way PHP's $_GET does ('+' becomes a space); the client already turns spaces
back into '+' when it reads a step (Server.Get_StepMirror), so this must not "fix" it.

State is memory only. A restart loses nothing that matters: II Command re-posts every 5 s. Rooms not
posted to for 12 h are dropped, so no session's end state is kept past the day (see Setup.md, FERPA).
Nothing is logged that identifies anyone; passwords arrive only as SHA-256 hashes and are never
logged. Plain HTTP on the LAN with II's built-in AES key: fine for synthetic patients, not security.

    py mirror_relay.py --port 8480
"""
from __future__ import annotations

import argparse
import hmac
import logging
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

MAX_ROOMS = 500                   # Accessions held at once; an unbounded store is a memory-exhaustion lever
MAX_FIELD = 512 * 1024            # Per-value cap; a real step is a few KB
STALE_AFTER = 12 * 3600           # Drop a room not posted to in 12 h: no session's end state outlives the day

log = logging.getLogger("ii-mirror")


class Store:
    def __init__(self) -> None:
        self._rows: dict[str, dict] = {}
        self._lock = threading.Lock()

    def _purge(self) -> None:
        cutoff = time.monotonic() - STALE_AFTER
        for acc in [a for a, r in self._rows.items() if r["posted"] < cutoff]:
            del self._rows[acc]
            log.info("dropped stale accession %s", acc)

    def post(self, accession: str, key_access: str, key_edit: str, patient: str, updated: str) -> str:
        with self._lock:
            self._purge()
            row = self._rows.get(accession)
            if row is None:
                if len(self._rows) >= MAX_ROOMS:
                    return "EXCEPTION"
                self._rows[accession] = {"key_access": key_access, "key_edit": key_edit,
                                         "patient": patient, "updated": updated, "read": None,
                                         "posted": time.monotonic()}
                log.info("new accession %s", accession)
                return "ENTRY_ADDED"
            if not hmac.compare_digest(row["key_edit"], key_edit):
                log.warning("bad edit password for %s", accession)
                return "BAD_PASSWORD"
            row.update(key_access=key_access, patient=patient, updated=updated, posted=time.monotonic())
            return "ENTRY_UPDATED"

    def get(self, accession: str, accesshash: str) -> str:
        with self._lock:
            row = self._rows.get(accession)
            if row is None or not hmac.compare_digest(row["key_access"], accesshash):
                return ""
            row["read"] = time.monotonic()
            return "%s\n%s\n" % (row["updated"], row["patient"])

    def status(self, accession: str, key_edit: str) -> str:
        with self._lock:
            row = self._rows.get(accession)
            if row is None or not hmac.compare_digest(row["key_edit"], key_edit) or row["read"] is None:
                return ""
            return str(int(time.monotonic() - row["read"]))


class Handler(BaseHTTPRequestHandler):
    store: Store = Store()
    server_version = "ii-mirror/1"

    def do_GET(self) -> None:
        parts = urlsplit(self.path)
        q = {k: v[0] for k, v in parse_qs(parts.query, keep_blank_values=True).items()}
        if any(len(v) > MAX_FIELD for v in q.values()):
            return self._reply(413, "")

        a = lambda k: q.get(k, "")
        route = parts.path.rstrip("/").rsplit("/", 1)[-1]
        if route == "mirror_post.php":
            body = self.store.post(a("accession"), a("key_access"), a("key_edit"), a("patient"), a("updated"))
        elif route == "mirror_get.php":
            body = self.store.get(a("accession"), a("accesshash"))
        elif route == "mirror_status.php":
            body = self.store.status(a("accession"), a("key_edit"))
        elif route == "version.php":
            body = ""
        else:
            return self._reply(404, "")
        self._reply(200, body)

    def _reply(self, code: int, body: str) -> None:
        data = body.encode("ascii", "replace")
        self.send_response(code)
        self.send_header("Content-Type", "text/plain; charset=us-ascii")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, fmt: str, *args) -> None:
        pass  # Request lines carry password hashes and patient data; log only Store events


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--port", type=int, default=8480)
    ap.add_argument("--bind", default="0.0.0.0")
    ap.add_argument("--log", default=None, help="log file (default: console)")
    args = ap.parse_args()

    logging.basicConfig(filename=args.log, level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s")
    srv = ThreadingHTTPServer((args.bind, args.port), Handler)
    srv.daemon_threads = True
    log.info("listening on %s:%d", args.bind, args.port)
    srv.serve_forever()


if __name__ == "__main__":
    main()
