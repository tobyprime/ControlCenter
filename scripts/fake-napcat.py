#!/usr/bin/env python3
"""假 napcat：OneBot v11 HTTP 最小实现，用于告警分发链路的本地/用户环境验证。

职责：接收面板 POST /send_private_msg | /send_group_msg，校验 Bearer token（可选），
把收到的消息逐条追加记录到 JSONL 文件（默认 /tmp/fake-napcat-log.jsonl），始终返回 200。
napcat 真实环境里的 QQ 送达不在本脚本职责内；本脚本只证明「面板→napcat HTTP」这一跳。

用法：python3 scripts/fake-napcat.py [--port 39998] [--token xxx] [--log /tmp/fake-napcat-log.jsonl]
停止：Ctrl-C（或 kill）；记录文件每行一条 JSON：{"ts": "...", "path": "...", "payload": {...}}
"""
import argparse
import datetime
import json
import os
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

args = argparse.ArgumentParser()
args.add_argument("--port", type=int, default=int(os.environ.get("FAKE_NAPCAT_PORT", "39998")))
args.add_argument("--token", default=os.environ.get("FAKE_NAPCAT_TOKEN", ""))
args.add_argument("--log", default=os.environ.get("FAKE_NAPCAT_LOG", "/tmp/fake-napcat-log.jsonl"))
parsed = args.parse_args()

lock = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    def _record(self, payload):
        entry = {
            "ts": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "path": self.path,
            "payload": payload,
        }
        with lock, open(parsed.log, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry, ensure_ascii=False) + "\n")

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b"{}"
        try:
            payload = json.loads(raw.decode("utf-8"))
        except Exception:
            payload = {"_raw": raw.decode("utf-8", "replace")}

        if parsed.token:
            auth = self.headers.get("Authorization") or ""
            if auth != f"Bearer {parsed.token}":
                self.send_response(401)
                self.end_headers()
                self.wfile.write(b'{"status":"failed","retcode":401}')
                return

        self._record(payload)
        body = json.dumps({"status": "ok", "retcode": 0, "data": None}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        body = json.dumps({"status": "ok", "retcode": 0}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *log_args):
        print(f"[fake-napcat] {fmt % log_args}", flush=True)


if __name__ == "__main__":
    server = ThreadingHTTPServer(("127.0.0.1", parsed.port), Handler)
    print(f"[fake-napcat] listening on 127.0.0.1:{parsed.port}, log={parsed.log}", flush=True)
    server.serve_forever()
