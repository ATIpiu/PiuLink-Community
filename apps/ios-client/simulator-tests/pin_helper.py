"""Submit a simulator-generated PIN to the disposable loopback test host."""
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import os
import re
import subprocess


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path == "/sample":
            from pathlib import Path
            scratch = Path(os.environ["RUNNER_TEMP"]) / "sunshine-test"
            with (scratch / "sample.log").open("wb") as log:
                subprocess.Popen(["bash", str(Path(__file__).with_name("sample-output.sh")), str(scratch)],
                                 stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            self.send_response(202)
            self.end_headers()
            return
        if self.path != "/pin" or self.headers.get("Content-Length") != "4":
            self.send_error(400)
            return
        pin = self.rfile.read(4).decode("ascii")
        if not re.fullmatch(r"[0-9]{4}", pin):
            self.send_error(400)
            return
        result = subprocess.run(
            ["curl", "--noproxy", "*", "-ksSf", "-u",
             "simtest:" + os.environ["PIULINK_TEST_PASSWORD"],
             "-H", "Content-Type: application/json", "--data-binary", "@-",
             "https://127.0.0.1:47990/api/pin"],
            input=json.dumps({"pin": pin, "name": "iPhone Simulator"}).encode(),
            capture_output=True,
        )
        self.send_response(204 if result.returncode == 0 else 502)
        self.end_headers()


HTTPServer(("127.0.0.1", 18589), Handler).serve_forever()
