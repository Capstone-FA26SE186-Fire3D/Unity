"""Fake FET3D backend for the package worker loop (BE docs/unity-package-worker.md), test only.

Serves one package job from an input folder (building.glb, building_metadata.json, scenario.json):
  POST /internal/processing/package-jobs/lease      -> claim with signed-looking input URLs (or 204 after the job is done)
  POST /internal/processing/jobs/{id}/renew|inputs|upload-urls|outputs|complete|fail
  GET  /storage/in/<name>                            -> input bytes
  PUT  /storage/out/<attempt>/<path>                 -> stores the body, checks x-amz-checksum-sha256 like S3

State is written to <state>/state.json so the test runner can assert on it. Requires the X-Worker-Key header to equal --key.

    python FakeBackend.py --port 8089 --input <dir> --state <dir> --key <32+ chars>
"""
import argparse
import base64
import hashlib
import json
import os
import re
import sys
import threading
import uuid
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PATH_RE = re.compile(r"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def utc(seconds):
    return (datetime.now(timezone.utc) + timedelta(seconds=seconds)).isoformat().replace("+00:00", "Z")


class State:
    def __init__(self, args):
        self.args = args
        self.lock = threading.Lock()
        self.job_id = str(uuid.uuid4())
        self.attempt_id = None
        self.lease_token = None
        self.output_prefix = None
        self.status = "waiting"  # waiting | leased | succeeded | failed
        self.renews = 0
        self.outputs = []
        self.completed = None
        self.fail_reason = None
        self.uploads = {}
        self.calls = []
        self.scenario = json.load(open(os.path.join(args.input, "scenario.json"), encoding="utf-8"))

    def base(self):
        return f"http://localhost:{self.args.port}"

    def inputs(self):
        entries = []
        for name, artifact in (("building.glb", "preview_glb"), ("building_metadata.json", "building_metadata")):
            path = os.path.join(self.args.input, name)
            if not os.path.exists(path):
                continue
            entries.append({
                "artifactType": artifact, "artifactId": str(uuid.uuid5(uuid.NAMESPACE_URL, name)),
                "sha256": sha256_file(path), "sizeBytes": os.path.getsize(path),
                "schemaVersion": "fet3d.building/1" if artifact == "building_metadata" else None,
                "url": f"{self.base()}/storage/in/{name}?X-Amz-Signature=fake", "expiresAt": utc(900),
            })
        return entries

    def claim(self):
        self.attempt_id = str(uuid.uuid4())
        self.lease_token = str(uuid.uuid4())
        self.output_prefix = f"packages/{self.job_id}/{self.attempt_id}/"
        self.status = "leased"
        return {
            "jobId": self.job_id, "kind": "PlaytestPackage", "attemptId": self.attempt_id, "leaseToken": self.lease_token,
            "leaseUntil": utc(self.args.lease), "outputPrefix": self.output_prefix, "inputHash": "0" * 64,
            "inputSnapshot": {"scenarioVersionId": str(uuid.uuid4()), "scenarioHash": "1" * 64, "rubricHash": "2" * 64, "buildTarget": self.args.target},
            "scenarioSnapshot": self.scenario, "geometryAttemptId": str(uuid.uuid4()), "inputs": self.inputs(),
        }

    def save(self):
        os.makedirs(self.args.state, exist_ok=True)
        doc = {
            "jobId": self.job_id, "attemptId": self.attempt_id, "status": self.status, "renews": self.renews,
            "outputs": self.outputs, "completed": self.completed, "failReason": self.fail_reason,
            "uploads": self.uploads, "calls": self.calls, "outputPrefix": self.output_prefix,
        }
        with open(os.path.join(self.args.state, "state.json"), "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=2)


STATE = None


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *a):
        sys.stdout.write("%s %s\n" % (self.command, self.path.split("?")[0]))
        sys.stdout.flush()

    def send_json(self, status, doc=None):
        body = b"" if doc is None else json.dumps(doc).encode("utf-8")
        self.send_response(status)
        if body:
            self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def problem(self, status, code, title, extra=None):
        doc = {"status": status, "code": code, "title": title}
        if extra:
            doc.update(extra)
        self.send_json(status, doc)

    def read_body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def authorized(self):
        key = self.headers.get("X-Worker-Key")
        if key != STATE.args.key:
            self.problem(401, "UNAUTHORIZED", "Worker proof is invalid.")
            return False
        return True

    def live(self, body):
        return body.get("attemptId") == STATE.attempt_id and body.get("leaseToken") == STATE.lease_token and STATE.status == "leased"

    def do_GET(self):
        path = self.path.split("?")[0]
        m = re.match(r"^/storage/in/([A-Za-z0-9._-]+)$", path)
        if not m:
            return self.send_json(404)
        file = os.path.join(STATE.args.input, m.group(1))
        if not os.path.exists(file):
            return self.send_json(404)
        size = os.path.getsize(file)
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(size))
        self.end_headers()
        with open(file, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                self.wfile.write(chunk)

    def do_PUT(self):
        path = self.path.split("?")[0]
        m = re.match(r"^/storage/out/([0-9a-f-]+)/(.+)$", path)
        data = self.read_body()
        if not m:
            return self.send_json(404)
        attempt, rel = m.group(1), m.group(2)
        expected = self.headers.get("x-amz-checksum-sha256")
        algo = self.headers.get("x-amz-sdk-checksum-algorithm")
        digest = hashlib.sha256(data).hexdigest()
        if algo != "SHA256" or expected != base64.b64encode(bytes.fromhex(digest)).decode():
            return self.problem(400, "BadDigest", "The SHA256 you specified did not match the calculated checksum.")
        if attempt != STATE.attempt_id or not PATH_RE.match(rel):
            return self.problem(403, "AccessDenied", "Signature does not match this attempt.")
        dest = os.path.join(STATE.args.state, "out", attempt, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(dest, "wb") as f:
            f.write(data)
        with STATE.lock:
            STATE.uploads[rel] = {"sha256": digest, "size": len(data)}
            STATE.save()
        self.send_json(200)

    def do_POST(self):
        path = self.path.split("?")[0]
        if not self.authorized():
            return
        raw = self.read_body()
        try:
            body = json.loads(raw.decode("utf-8")) if raw else {}
        except ValueError:
            return self.problem(400, "VALIDATION_ERROR", "Invalid JSON.")
        with STATE.lock:
            STATE.calls.append(path)
            if path == "/internal/processing/package-jobs/lease":
                if body.get("toolchainVersion") != STATE.args.toolchain:
                    return self.problem(400, "VALIDATION_ERROR", "Lease request validation failed.", {"errors": {"toolchainVersion": ["The toolchain must be explicitly allowed by this deployment."]}})
                if STATE.status in ("succeeded", "failed"):
                    return self.send_json(204)
                if STATE.status == "leased" and not STATE.args.release:
                    return self.send_json(204)
                claim = STATE.claim()
                STATE.save()
                return self.send_json(200, claim)

            m = re.match(r"^/internal/processing/jobs/([0-9a-f-]+)/(renew|inputs|upload-urls|outputs|complete|fail)$", path)
            if not m:
                return self.send_json(404)
            job, action = m.group(1), m.group(2)
            if job != STATE.job_id or not self.live(body):
                return self.problem(409, "ATTEMPT_STALE", "Use the current attempt and lease token of a running package job.")
            if action == "renew":
                STATE.renews += 1
                STATE.save()
                return self.send_json(200, {"leaseUntil": utc(STATE.args.lease)})
            if action == "inputs":
                return self.send_json(200, {"inputs": STATE.inputs(), "outputPrefix": STATE.output_prefix})
            if action == "upload-urls":
                files = body.get("files") or []
                if not files or len(files) > 2001:
                    return self.problem(400, "VALIDATION_ERROR", "List 1-2001 files.")
                signed = []
                for f in files:
                    p = f.get("path")
                    if not PATH_RE.match(p or "") or ".." in p.split("/"):
                        return self.problem(400, "VALIDATION_ERROR", "Upload request validation failed.", {"errors": {"files.path": [p]}})
                    checksum = base64.b64encode(bytes.fromhex(f["sha256"])).decode()
                    signed.append({"path": p, "url": f"{STATE.base()}/storage/out/{STATE.attempt_id}/{p}?X-Amz-Signature=fake",
                                   "headers": {"x-amz-checksum-sha256": checksum, "x-amz-sdk-checksum-algorithm": "SHA256"}, "expiresAt": utc(1800)})
                return self.send_json(200, {"outputPrefix": STATE.output_prefix, "files": signed})
            if action == "outputs":
                out = body.get("output") or {}
                for name in ("artifactType", "objectKey", "sha256Hash", "sizeBytes", "schemaVersion", "metadata", "validatorVersion", "outcome"):
                    if name not in out:
                        return self.problem(400, "VALIDATION_ERROR", f"output.{name} is required.")
                if not out["objectKey"].startswith(STATE.output_prefix):
                    return self.problem(400, "VALIDATION_ERROR", "objectKey must be within outputPrefix.")
                if out["artifactType"] == "manifest":
                    issues = validate_manifest(out["metadata"])
                    if issues:
                        return self.problem(422, "PACKAGE_MANIFEST_INVALID", "Package manifest is invalid.", {"issues": issues})
                if out["artifactType"] == "unity_package":
                    meta = out["metadata"]
                    for name in ("buildTarget", "protocolVersion", "manifestSchemaVersion", "minRuntimeVersion", "requiredCapabilities"):
                        if name not in meta:
                            return self.problem(400, "VALIDATION_ERROR", f"output.metadata.{name} is required.")
                STATE.outputs.append(out)
                h = hashlib.sha256(json.dumps([o["objectKey"] + o["sha256Hash"] for o in STATE.outputs]).encode()).hexdigest()
                STATE.save()
                return self.send_json(200, {"artifactId": str(uuid.uuid4()), "outputHash": h})
            if action == "complete":
                h = hashlib.sha256(json.dumps([o["objectKey"] + o["sha256Hash"] for o in STATE.outputs]).encode()).hexdigest()
                if body.get("outputHash") != h:
                    return self.problem(409, "OUTPUT_CONFLICT", "outputHash differs.")
                manifest = next((o for o in STATE.outputs if o["artifactType"] == "manifest"), None)
                package = next((o for o in STATE.outputs if o["artifactType"] == "unity_package"), None)
                if not manifest or not package or manifest["objectKey"] != package["objectKey"]:
                    return self.problem(422, "PACKAGE_MANIFEST_INVALID", "The two outputs do not name the same object.")
                # The real release/publish gates refuse a package whose metadata differs from the manifest's.
                if package["metadata"] != manifest["metadata"]:
                    return self.problem(409, "PACKAGE_METADATA_MISMATCH", "unity_package metadata differs from the manifest metadata.")
                mismatch = storage_mismatch(manifest)
                if mismatch:
                    return self.problem(409, "PACKAGE_STORAGE_MISMATCH", mismatch)
                STATE.status = "succeeded"
                STATE.completed = body
                STATE.save()
                return self.send_json(200, {"status": "Succeeded"})
            if action == "fail":
                STATE.status = "failed"
                STATE.fail_reason = body.get("reason")
                STATE.save()
                return self.send_json(200, {"status": "Failed"})
        return self.send_json(404)


def validate_manifest(m):
    issues = []
    if m.get("schemaVersion") != "fet3d.package/1":
        issues.append({"path": "schemaVersion", "message": "expected fet3d.package/1"})
    for name in ("buildTarget", "protocolVersion", "manifestSchemaVersion", "unityVersion", "materialLibraryVersion"):
        if not isinstance(m.get(name), str) or not m[name] or len(m[name]) > 100:
            issues.append({"path": name, "message": "required string"})
    for name in ("runtimeVersion", "minRuntimeVersion"):
        if not re.match(r"^\d+\.\d+\.\d+$", str(m.get(name, ""))):
            issues.append({"path": name, "message": "three-part version"})
    if not isinstance(m.get("requiredCapabilities"), list):
        issues.append({"path": "requiredCapabilities", "message": "array"})
    files = m.get("files")
    paths = set()
    if not isinstance(files, list) or not files:
        issues.append({"path": "files", "message": "1-2000 files"})
    else:
        for i, f in enumerate(files):
            if set(f.keys()) != {"path", "sha256", "size"}:
                issues.append({"path": f"files[{i}]", "message": "only path, sha256, size"})
            p = f.get("path")
            if not PATH_RE.match(p or "") or p == "manifest.json" or p in paths:
                issues.append({"path": f"files[{i}].path", "message": "invalid or duplicate"})
            paths.add(p)
            if not re.match(r"^[0-9a-f]{64}$", str(f.get("sha256"))):
                issues.append({"path": f"files[{i}].sha256", "message": "lowercase hex"})
            if not isinstance(f.get("size"), int) or f["size"] <= 0:
                issues.append({"path": f"files[{i}].size", "message": "positive"})
    floors = m.get("floors")
    if not isinstance(floors, list) or not floors:
        issues.append({"path": "floors", "message": "1-500 floors"})
    else:
        for i, fl in enumerate(floors):
            if fl.get("bundle") not in paths:
                issues.append({"path": f"floors[{i}].bundle", "message": "must be a listed file"})
            if fl.get("lightingBundle") is not None and fl["lightingBundle"] not in paths:
                issues.append({"path": f"floors[{i}].lightingBundle", "message": "must be a listed file"})
    return issues


def storage_mismatch(manifest_output):
    up = STATE.uploads
    key = manifest_output["objectKey"][len(STATE.output_prefix):]
    stored = up.get(key)
    if not stored or stored["sha256"] != manifest_output["sha256Hash"] or stored["size"] != manifest_output["sizeBytes"]:
        return "manifest.json is not in storage with the registered hash"
    uploaded = json.load(open(os.path.join(STATE.args.state, "out", STATE.attempt_id, key), encoding="utf-8"))
    if uploaded.get("files") != manifest_output["metadata"].get("files") or uploaded.get("floors") != manifest_output["metadata"].get("floors"):
        return "uploaded manifest.json differs from the registered metadata"
    for f in manifest_output["metadata"]["files"]:
        s = up.get(f["path"])
        if not s or s["sha256"] != f["sha256"] or s["size"] != f["size"]:
            return f"{f['path']} is missing or differs in storage"
    return None


def main():
    global STATE
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8089)
    ap.add_argument("--input", required=True)
    ap.add_argument("--state", required=True)
    ap.add_argument("--key", required=True)
    ap.add_argument("--toolchain", default="unity-6000.6.6f1-worker-1")
    ap.add_argument("--target", default="StandaloneWindows64")
    ap.add_argument("--lease", type=int, default=300)
    ap.add_argument("--release", action="store_true", help="hand the job out again on every lease call (tests ATTEMPT_STALE)")
    args = ap.parse_args()
    if len(args.key) < 32:
        sys.exit("--key must be at least 32 characters")
    STATE = State(args)
    STATE.save()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"fake backend on http://127.0.0.1:{args.port} job {STATE.job_id}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
