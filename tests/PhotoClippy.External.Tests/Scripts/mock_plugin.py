"""Configurable mock plugin used by PhotoClippy.External.Tests (stdio transport only).

Behaviour is driven by environment variables:
  MOCK_KIND             manifest kind (default: the kind requested in initialize)
  MOCK_ID               manifest id (default: the pluginId requested in initialize)
  MOCK_CONTRACT         manifest contractVersion (default "1.0")
  MOCK_CRASH_AFTER      exit (code 3) on receiving request N+1, counting non-initialize/health requests
  MOCK_IGNORE_SHUTDOWN  "1": ignore shutdown and stdin EOF, keep running until killed
  MOCK_UNHEALTHY        "1": health reports unhealthy
  MOCK_NO_HEALTH        "1": health is not implemented (-32601)
  MOCK_MAX_EDGE         requirements.maxEdge (default 64)
and per request by AnalysisContext.captureId (frame analyzers):
  "sleep:<seconds>"     sleep before answering
  "delay:<ms>"          sleep a few milliseconds (forces out-of-order responses)
  "error"               JSON-RPC error -32000
  "noresult"            response with neither result nor error
  "garbage"             write malformed / unknown-id lines before the real response
"""

import base64
import json
import os
import sys
import threading
import time

ENV = os.environ
write_lock = threading.Lock()
state_lock = threading.Lock()
state = {"served": 0, "inflight": 0, "maxInflight": 0, "kind": "composition", "id": "ext.mock"}


def send(obj):
    send_raw(json.dumps(obj, separators=(",", ":"), allow_nan=False))


def send_raw(text):
    with write_lock:
        sys.stdout.write(text + "\n")
        sys.stdout.flush()


def log(text):
    sys.stderr.write(text + "\n")
    sys.stderr.flush()


class RpcError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


def read_ppm(frame):
    if frame.get("path"):
        with open(frame["path"], "rb") as f:
            data = f.read()
        by_path = 1.0
    else:
        data = base64.b64decode(frame["data"])
        by_path = 0.0
    parts = data.split(b"\n", 3)
    if parts[0] != b"P6" or parts[2] != b"255":
        raise RpcError(-32602, "not a P6/255 PPM")
    width, height = (int(v) for v in parts[1].split())
    pixels = parts[3]
    if len(pixels) != width * height * 3:
        raise RpcError(-32602, "pixel payload has the wrong size")
    mean_r = sum(pixels[0::3]) / (width * height)
    return width, height, mean_r, by_path


def initialize(params):
    state["kind"] = ENV.get("MOCK_KIND", params.get("kind", "composition"))
    state["id"] = ENV.get("MOCK_ID", params.get("pluginId", "ext.mock"))
    log("info: mock initialized pid=%d" % os.getpid())
    log("warn: mock stderr warning")
    result = {
        "manifest": {
            "id": state["id"],
            "name": "Mock plugin",
            "version": "0.0.1",
            "kind": state["kind"],
            "contractVersion": ENV.get("MOCK_CONTRACT", "1.0"),
        },
        "requirements": {"maxEdge": int(ENV.get("MOCK_MAX_EDGE", "64"))},
    }
    if state["kind"] == "metadata":
        result["extensions"] = [".jpg", ".jpeg"]
        result["maxBatch"] = 3
    if state["kind"] == "report":
        result["format"] = "txt"
    return result


def health(params):
    if ENV.get("MOCK_NO_HEALTH") == "1":
        raise RpcError(-32601, "method not found: health")
    if ENV.get("MOCK_UNHEALTHY") == "1":
        return {"healthy": False, "message": "sick"}
    return {"healthy": True, "message": "pid %d" % os.getpid()}


def analyze(params, color):
    capture = params["context"]["captureId"]
    if capture.startswith("sleep:"):
        time.sleep(float(capture[6:]))
    if capture.startswith("delay:"):
        time.sleep(int(capture.split(":")[1]) / 1000.0)
    if capture == "error":
        raise RpcError(-32000, "mock failure")
    width, height, mean_r, by_path = read_ppm(params["frame"])
    extras = {
        "mock.width": width,
        "mock.height": height,
        "mock.meanR": mean_r,
        "mock.byPath": by_path,
        "mock.maxInflight": state["maxInflight"],
    }
    tag = capture.split(":")[-1]
    if tag.startswith("c") and tag[1:].isdigit():
        extras["mock.index"] = int(tag[1:])
    if color:
        return {"meanLuma": mean_r / 255.0, "key": "low", "extras": extras}
    return {
        "thirdsScore": 0.5,
        "genres": {"Night": 0.9, "portrait": 0.6, "not-a-genre": 0.4},
        "extras": extras,
    }


def extract(params):
    files = params["files"]
    if any("explode" in f["path"] for f in files):
        raise RpcError(-32000, "extract exploded")
    results = [
        {"path": f["path"], "make": "MockCam", "model": "M1", "fNumber": 2.8, "iso": 100}
        for f in files
        if "missing" not in f["path"]
    ]
    results.reverse()
    return {"results": results}


def recommend(params):
    return {"headline": "mock headline", "sampleCount": len(params.get("samples", []))}


def render(params):
    target = params["target"]
    name = target.get("baseName", "photo-report") + ".txt"
    with open(os.path.join(target["outputDirectory"], name), "w", encoding="utf-8") as f:
        f.write(params["model"].get("title", ""))
    return {"files": [name]}


METHODS = {
    "health": health,
    "composition.analyze": lambda p: analyze(p, False),
    "color.analyze": lambda p: analyze(p, True),
    "metadata.extract": extract,
    "recommend.run": recommend,
    "report.render": render,
}


def handle(message):
    request_id = message.get("id")
    with state_lock:
        state["inflight"] += 1
        state["maxInflight"] = max(state["maxInflight"], state["inflight"])
    try:
        params = message.get("params") or {}
        capture = (params.get("context") or {}).get("captureId", "")
        if capture == "noresult":
            send({"jsonrpc": "2.0", "id": request_id})
            return
        if capture == "garbage":
            send_raw("this is not json {")
            send_raw('{"jsonrpc":"2.0","id":987654321,"result":{}}')
            send_raw('["not","an","object"]')
        handler = METHODS.get(message.get("method"))
        if handler is None:
            raise RpcError(-32601, "method not found: %s" % message.get("method"))
        send({"jsonrpc": "2.0", "id": request_id, "result": handler(params)})
    except RpcError as e:
        send({"jsonrpc": "2.0", "id": request_id, "error": {"code": e.code, "message": e.message}})
    except Exception as e:  # noqa: BLE001
        send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32603, "message": str(e)}})
    finally:
        with state_lock:
            state["inflight"] -= 1


def main():
    crash_after = int(ENV["MOCK_CRASH_AFTER"]) if "MOCK_CRASH_AFTER" in ENV else None
    ignore_shutdown = ENV.get("MOCK_IGNORE_SHUTDOWN") == "1"
    stdin = sys.stdin.buffer
    while True:
        raw = stdin.readline()
        if not raw:
            break
        try:
            message = json.loads(raw.decode("utf-8"))
        except ValueError:
            send({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "parse error"}})
            continue
        method = message.get("method")
        if "id" not in message:
            if method == "shutdown" and not ignore_shutdown:
                break
            continue
        if method == "initialize":
            send({"jsonrpc": "2.0", "id": message["id"], "result": initialize(message.get("params") or {})})
            continue
        if method != "health":
            state["served"] += 1
            if crash_after is not None and state["served"] > crash_after:
                log("error: mock crashing on purpose")
                os._exit(3)
        threading.Thread(target=handle, args=(message,), daemon=True).start()
    if ignore_shutdown:
        while True:
            time.sleep(1)


if __name__ == "__main__":
    main()
