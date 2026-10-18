"""Fake FET3D backend for the geometry worker loop (BE docs/geometry-worker.md), test only.

Serves one Geometry job for --ifc:
  POST /internal/processing/geometry-jobs/lease   -> claim with a signed-looking ifc_source URL (204 once the job is done)
  POST /internal/processing/jobs/{id}/renew|inputs|upload-urls|outputs|complete|fail
  GET  /storage/in/source.ifc                     -> IFC bytes
  PUT  /storage/out/<attempt>/<path>              -> stores the body, checks x-amz-checksum-sha256 like S3
State goes to <state>/state.json. --expire-first-url makes the first input URL already expired (re-sign path).

    python FakeGeometryBackend.py --port 8090 --ifc model.ifc --state <dir> --key <32+ chars>
"""
import argparse
import base64
import hashlib
import json
import os
import re
import threading
import uuid
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PATH_RE = re.compile(r"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")
ALLOWED_OUTPUTS = {"preview_glb": "fet3d.editor/1", "building_metadata": "fet3d.building/1"}


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def utc(seconds):
    return (datetime.now(timezone.utc) + timedelta(seconds=seconds)).isoformat().replace("+00:00", "Z")


class State:
    def __init__(self, args):
        self.args = args
        self.lock = threading.Lock()
        self.job_id = str(uuid.uuid4())
        self.revision_id = str(uuid.uuid4())
        self.attempt_id = None
        self.lease_token = None
        self.status = "waiting"
        self.renews = 0
        self.resigns = 0
        self.outputs = {}
        self.uploads = {}
        self.completed = None
        self.fail_reason = None
        self.calls = []
        self.source_sha = sha256_file(args.ifc)
        self.source_size = os.path.getsize(args.ifc)

    def prefix(self):
        return f"worker-output/org-test/{self.job_id}/{self.attempt_id}/"

    def source_input(self, expired=False):
        return {"artifactType": "ifc_source", "artifactId": str(uuid.uuid5(uuid.NAMESPACE_URL, self.source_sha)),
                "sha256": self.source_sha, "sizeBytes": self.source_size,
                "url": f"http://localhost:{self.args.port}/storage/in/source.ifc?X-Amz-Signature=fake",
                "expiresAt": utc(-60 if expired else 900)}

    def save(self):
        data = {"jobId": self.job_id, "status": self.status, "renews": self.renews, "resigns": self.resigns,
                "outputs": self.outputs, "uploads": self.uploads, "completed": self.completed, "failReason": self.fail_reason,
                "calls": self.calls}
        with open(os.path.join(self.args.state, "state.json"), "w", encoding="utf-8") as handle:
            json.dump(data, handle, indent=1)


class Handler(BaseHTTPRequestHandler):
    state: State = None

    def log_message(self, *args):
        pass

    def _send(self, status, body=None):
        payload = b"" if body is None else json.dumps(body).encode()
        self.send_response(status)
        if body is not None:
            self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _problem(self, status, code, **extra):
        self._send(status, {"code": code, "title": code, **extra})

    def _json(self):
        length = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(length) or b"{}")

    def _authorised(self):
        if self.headers.get("X-Worker-Key") != self.state.args.key:
            self._problem(401, "UNAUTHORIZED")
            return False
        return True

    def _fenced(self, body):
        s = self.state
        if s.status != "leased" or body.get("attemptId") != s.attempt_id or body.get("leaseToken") != s.lease_token:
            self._problem(409, "ATTEMPT_STALE")
            return False
        return True

    def do_GET(self):
        if self.path.startswith("/storage/in/source.ifc"):
            with open(self.state.args.ifc, "rb") as handle:
                data = handle.read()
            self.send_response(200)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        self._send(404, {"code": "NOT_FOUND"})

    def do_PUT(self):
        s = self.state
        match = re.match(r"^/storage/out/([^/]+)/(.+?)(\?.*)?$", self.path)
        if not match:
            return self._send(404, {"code": "NOT_FOUND"})
        length = int(self.headers.get("Content-Length") or 0)
        data = self.rfile.read(length)
        expected = self.headers.get("x-amz-checksum-sha256")
        actual = base64.b64encode(hashlib.sha256(data).digest()).decode()
        if expected != actual or self.headers.get("x-amz-sdk-checksum-algorithm") != "SHA256":
            return self._send(400, {"code": "BadDigest"})
        target = os.path.join(s.args.state, "storage", match.group(1), match.group(2))
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, "wb") as handle:
            handle.write(data)
        with s.lock:
            s.uploads[match.group(2)] = {"sha256": hashlib.sha256(data).hexdigest(), "size": len(data)}
            s.save()
        self._send(200, {})

    def do_POST(self):
        s = self.state
        if not self._authorised():
            return
        body = self._json()
        with s.lock:
            s.calls.append(self.path)
            try:
                self._route(body)
            finally:
                s.save()

    def _route(self, body):
        s = self.state
        if self.path == "/internal/processing/geometry-jobs/lease":
            if body.get("toolchainVersion") != s.args.toolchain:
                return self._problem(400, "VALIDATION_ERROR")
            if s.status != "waiting":
                return self._send(204)
            s.attempt_id, s.lease_token, s.status = str(uuid.uuid4()), str(uuid.uuid4()), "leased"
            return self._send(200, {
                "jobId": s.job_id, "kind": "Geometry", "attemptId": s.attempt_id, "leaseToken": s.lease_token, "leaseUntil": utc(600),
                "receiptId": str(uuid.uuid4()), "revisionId": s.revision_id, "outputPrefix": s.prefix(), "inputHash": s.source_sha,
                "replayed": False, "inputs": [s.source_input(expired=s.args.expire_first_url)],
            })
        match = re.match(r"^/internal/processing/jobs/([^/]+)/([a-z-]+)$", self.path)
        if not match or match.group(1) != s.job_id:
            return self._problem(404, "JOB_NOT_FOUND")
        action = match.group(2)
        if not self._fenced(body):
            return
        if action == "renew":
            s.renews += 1
            return self._send(200, {"leaseUntil": utc(600)})
        if action == "inputs":
            s.resigns += 1
            return self._send(200, {"inputs": [s.source_input()], "outputPrefix": s.prefix()})
        if action == "upload-urls":
            files = body.get("files") or []
            out = []
            for f in files:
                path = f.get("path", "")
                if not PATH_RE.match(path) or not re.fullmatch(r"[0-9a-f]{64}", f.get("sha256", "")):
                    return self._problem(400, "VALIDATION_ERROR")
                checksum = base64.b64encode(bytes.fromhex(f["sha256"])).decode()
                out.append({"path": path, "url": f"http://localhost:{s.args.port}/storage/out/{s.attempt_id}/{path}?X-Amz-Signature=fake",
                            "headers": {"x-amz-checksum-sha256": checksum, "x-amz-sdk-checksum-algorithm": "SHA256"}, "expiresAt": utc(1800)})
            return self._send(200, {"outputPrefix": s.prefix(), "files": out})
        if action == "outputs":
            output = body.get("output") or {}
            kind = output.get("artifactType")
            problems = []
            if ALLOWED_OUTPUTS.get(kind) != output.get("schemaVersion"):
                problems.append("schemaVersion")
            metadata = output.get("metadata") or {}
            if metadata.get("schemaVersion") != output.get("schemaVersion"):
                problems.append("metadata.schemaVersion")
            if not str(output.get("objectKey", "")).startswith(s.prefix()):
                problems.append("objectKey")
            if output.get("outcome") not in ("Passed", "Failed"):
                problems.append("outcome")
            for issue in output.get("issues") or []:
                if issue.get("severity") not in ("Info", "Warning", "Error", "Critical") or not issue.get("code") or len(issue.get("ifcGlobalIds") or []) > 100:
                    problems.append("issue")
                    break
            if problems:
                return self._problem(422, "OUTPUT_INVALID", issues=problems)
            s.outputs[kind] = {k: v for k, v in output.items() if k != "metadata"}
            s.outputs[kind]["metadataKeys"] = sorted(metadata.keys())
            digest = hashlib.sha256(json.dumps(s.outputs, sort_keys=True).encode()).hexdigest()
            return self._send(200, {"artifactId": str(uuid.uuid4()), "outputHash": digest})
        if action == "complete":
            if set(s.outputs) != set(ALLOWED_OUTPUTS):
                return self._problem(409, "GEOMETRY_OUTPUT_INCOMPLETE")
            for output in s.outputs.values():
                name = output["objectKey"][len(s.prefix()):]
                stored = s.uploads.get(name)
                if not stored or stored["sha256"] != output["sha256Hash"] or stored["size"] != output["sizeBytes"]:
                    return self._problem(409, "GEOMETRY_STORAGE_MISMATCH")
            s.status, s.completed = "succeeded", body.get("outputHash")
            return self._send(200, {"status": "Succeeded"})
        if action == "fail":
            s.status, s.fail_reason = "failed", body.get("reason")
            return self._send(200, {"status": "Failed"})
        return self._problem(404, "NOT_FOUND")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8090)
    parser.add_argument("--ifc", required=True)
    parser.add_argument("--state", required=True)
    parser.add_argument("--key", required=True)
    parser.add_argument("--toolchain", default="fet3d-geometry-worker-1")
    parser.add_argument("--expire-first-url", action="store_true")
    args = parser.parse_args()
    if len(args.key) < 32:
        raise SystemExit("--key must be at least 32 characters")
    os.makedirs(args.state, exist_ok=True)
    Handler.state = State(args)
    Handler.state.save()
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
