#!/usr/bin/env python3
"""photo-clippy reference plugin: a tiny composition analyzer.

Standard library only (Python 3.9+). Speaks JSON-RPC 2.0 as described in ../PROTOCOL.md.

    python3 plugin.py                               # stdio transport (spawned by the host)
    python3 plugin.py --http 7070 [--host 0.0.0.0]  # http transport (POST http://127.0.0.1:7070/)

What it computes (deliberately simple: this file is a template, not a model):
    * ext.python.meanLuma     mean Rec.709 luma of the frame, 0..1
    * ext.python.brightCorner how much brighter the brightest corner quadrant is than the whole frame, 0..1
    * ext.python.aspect       source aspect ratio (width / height)
    * genres                  a toy prior: very dark frames get {"night": nightPrior}

Settings (the "settings" object of the plugin's config entry):
    nightThreshold  mean luma below which a frame counts as "very dark" (default 0.15)
    nightPrior      score given to the "night" genre for very dark frames (default 0.2)
"""

import argparse
import base64
import json
import sys
import threading
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PLUGIN_ID = "ext.python-composition"
PLUGIN_VERSION = "1.0.0"
CONTRACT_MAJOR = "1"
MAX_EDGE = 256  # the host downsamples frames so that max(width, height) <= MAX_EDGE

PARSE_ERROR = -32700
INVALID_REQUEST = -32600
METHOD_NOT_FOUND = -32601
INVALID_PARAMS = -32602
INTERNAL_ERROR = -32603

DEFAULT_SETTINGS = {"nightThreshold": 0.15, "nightPrior": 0.2}


class RpcError(Exception):
    """Raise from a handler to send a JSON-RPC error response."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


def log(level, message):
    """stderr is the log channel: the host forwards every line, using the prefix as the log level."""
    sys.stderr.write("%s: %s\n" % (level, message))
    sys.stderr.flush()


# --------------------------------------------------------------------------------------------------
# Frame decoding
# --------------------------------------------------------------------------------------------------

WHITESPACE = b" \t\r\n"


def parse_ppm(data):
    """Parses a binary PPM (P6, maxval 255). Returns (width, height, rgb) where rgb is RGB24 bytes."""
    fields, pos, end = [], 0, len(data)
    while len(fields) < 4:
        while pos < end and data[pos] in WHITESPACE:
            pos += 1
        if pos < end and data[pos] == ord("#"):
            while pos < end and data[pos] not in b"\r\n":
                pos += 1
            continue
        start = pos
        while pos < end and data[pos] not in WHITESPACE:
            pos += 1
        if start == pos:
            raise ValueError("truncated PPM header")
        fields.append(data[start:pos])
    magic, width, height, maxval = fields[0], int(fields[1]), int(fields[2]), int(fields[3])
    if magic != b"P6" or maxval != 255:
        raise ValueError("expected a P6 PPM with maxval 255")
    pos += 1  # exactly one whitespace byte separates the header from the raster
    rgb = data[pos:pos + width * height * 3]
    if len(rgb) != width * height * 3:
        raise ValueError("truncated PPM raster")
    return width, height, rgb


def load_frame(frame):
    """stdio passes frame.path (a temp file the host deletes after our response); http passes base64 frame.data."""
    if not isinstance(frame, dict) or frame.get("encoding", "ppm") != "ppm":
        raise RpcError(INVALID_PARAMS, "params.frame must be an object with encoding 'ppm'")
    try:
        if frame.get("path"):
            with open(frame["path"], "rb") as handle:
                data = handle.read()
        elif frame.get("data"):
            data = base64.b64decode(frame["data"])
        else:
            raise RpcError(INVALID_PARAMS, "params.frame needs 'path' or 'data'")
        return parse_ppm(data)
    except (OSError, ValueError) as error:
        raise RpcError(INVALID_PARAMS, "cannot read frame: %s" % error)


# --------------------------------------------------------------------------------------------------
# Analysis
# --------------------------------------------------------------------------------------------------

def region_luma(rgb, width, x0, y0, x1, y1):
    """Mean Rec.709 luma (0..1) of the rectangle [x0, x1) x [y0, y1). Uses C-speed slicing, no per-pixel loop."""
    r = g = b = 0
    for y in range(y0, y1):
        row = rgb[(y * width + x0) * 3:(y * width + x1) * 3]
        r += sum(row[0::3])
        g += sum(row[1::3])
        b += sum(row[2::3])
    count = max(1, (x1 - x0) * (y1 - y0))
    return (0.2126 * r + 0.7152 * g + 0.0722 * b) / (255.0 * count)


def analyze(width, height, rgb, source_width, source_height, settings, genres):
    mean_luma = region_luma(rgb, width, 0, 0, width, height)
    half_w, half_h = max(1, width // 2), max(1, height // 2)
    corners = [
        region_luma(rgb, width, 0, 0, half_w, half_h),
        region_luma(rgb, width, width - half_w, 0, width, half_h),
        region_luma(rgb, width, 0, height - half_h, half_w, height),
        region_luma(rgb, width, width - half_w, height - half_h, width, height),
    ]
    # 0 = no corner is brighter than the frame; 1 = the brightest corner is >= 0.25 luma above the mean.
    bright_corner = min(1.0, max(0.0, (max(corners) - mean_luma) * 4.0))
    aspect = float(source_width) / float(source_height) if source_height else 1.0

    features = {
        "extras": {
            "ext.python.meanLuma": round(mean_luma, 6),
            "ext.python.aspect": round(aspect, 6),
            "ext.python.brightCorner": round(bright_corner, 6),
        }
    }
    # Only emit genre ids that exist in the host taxonomy (the host drops unknown ids anyway).
    if mean_luma < settings["nightThreshold"] and "night" in genres:
        features["genres"] = {"night": settings["nightPrior"]}
    return features


# --------------------------------------------------------------------------------------------------
# JSON-RPC methods
# --------------------------------------------------------------------------------------------------

class CompositionPlugin:
    def __init__(self):
        self.settings = dict(DEFAULT_SETTINGS)
        self.genres = set()
        self.initialized = False
        self.stopping = False
        self.analyzed = 0
        self.lock = threading.Lock()  # the http server is multi-threaded

    def initialize(self, params):
        if str(params.get("contractVersion", "1.0")).split(".")[0] != CONTRACT_MAJOR:
            raise RpcError(INVALID_PARAMS, "unsupported contract version %r" % params.get("contractVersion"))
        settings = dict(DEFAULT_SETTINGS)
        for key, value in (params.get("settings") or {}).items():
            if key in DEFAULT_SETTINGS:
                if not isinstance(value, (int, float)) or isinstance(value, bool):
                    raise RpcError(INVALID_PARAMS, "setting %r must be a number" % key)
                settings[key] = float(value)
        taxonomy = params.get("taxonomy") or {}
        self.genres = {genre.get("id") for genre in taxonomy.get("genres", []) if isinstance(genre, dict)}
        self.settings = settings
        self.initialized = True
        log("info", "initialized (%d genres, settings %s)" % (len(self.genres), json.dumps(settings, sort_keys=True)))
        return {
            "manifest": {
                "id": PLUGIN_ID,
                "name": "Python composition example",
                "version": PLUGIN_VERSION,
                "kind": "composition",
                "contractVersion": "1.0",
                "description": "Mean luma, bright-corner score and a toy night prior (stdlib-only reference plugin).",
                "capabilities": ["extras", "genres"],
            },
            "requirements": {"maxEdge": MAX_EDGE},
        }

    def health(self, params):
        return {"healthy": self.initialized, "message": "%d frame(s) analyzed" % self.analyzed}

    def composition_analyze(self, params):
        if not self.initialized:
            raise RpcError(INVALID_REQUEST, "initialize must be called first")
        frame = params.get("frame")
        width, height, rgb = load_frame(frame)
        source_width = frame.get("sourceWidth") or width
        source_height = frame.get("sourceHeight") or height
        result = analyze(width, height, rgb, source_width, source_height, self.settings, self.genres)
        with self.lock:
            self.analyzed += 1
        return result

    def shutdown(self, params):
        self.stopping = True
        return None

    def methods(self):
        return {
            "initialize": self.initialize,
            "health": self.health,
            "composition.analyze": self.composition_analyze,
            "shutdown": self.shutdown,
        }


def error_response(request_id, code, message):
    return {"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": message}}


def handle_message(plugin, message):
    """Dispatches one decoded JSON-RPC message. Returns the response object, or None for notifications."""
    if not isinstance(message, dict) or not isinstance(message.get("method"), str):
        return error_response(message.get("id") if isinstance(message, dict) else None, INVALID_REQUEST, "invalid request")
    request_id = message.get("id")
    is_notification = "id" not in message
    handler = plugin.methods().get(message["method"])
    params = message.get("params") or {}
    try:
        if handler is None:
            raise RpcError(METHOD_NOT_FOUND, "method not found: %s" % message["method"])
        if not isinstance(params, dict):
            raise RpcError(INVALID_PARAMS, "params must be an object")
        result = handler(params)
    except RpcError as error:
        log("warn", "%s failed: %s" % (message["method"], error.message))
        return None if is_notification else error_response(request_id, error.code, error.message)
    except Exception as error:  # never let one bad request kill the plugin
        log("error", "%s crashed: %s\n%s" % (message["method"], error, traceback.format_exc()))
        return None if is_notification else error_response(request_id, INTERNAL_ERROR, str(error))
    return None if is_notification else {"jsonrpc": "2.0", "id": request_id, "result": result}


def encode(response):
    try:
        return json.dumps(response, separators=(",", ":"), allow_nan=False).encode("utf-8")
    except ValueError as error:  # NaN/Infinity are not valid JSON
        return json.dumps(error_response(response.get("id"), INTERNAL_ERROR, str(error))).encode("utf-8")


def process(plugin, raw):
    try:
        message = json.loads(raw)
    except ValueError as error:
        return error_response(None, PARSE_ERROR, "parse error: %s" % error)
    return handle_message(plugin, message)


# --------------------------------------------------------------------------------------------------
# Transports
# --------------------------------------------------------------------------------------------------

def serve_stdio(plugin):
    """One JSON message per line on stdin/stdout. stdout is reserved for protocol messages."""
    protocol_out = sys.stdout.buffer
    sys.stdout = sys.stderr  # a stray print() must never corrupt the protocol stream
    for raw in sys.stdin.buffer:
        if not raw.strip():
            continue
        response = process(plugin, raw)
        if response is not None:
            protocol_out.write(encode(response) + b"\n")
            protocol_out.flush()
        if plugin.stopping:
            break
    log("info", "stdio transport closed")


def serve_http(plugin, host, port):
    """POST / with one JSON-RPC request per body; notifications get 204 No Content."""

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def do_POST(self):
            body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
            response = process(plugin, body)
            if response is None:
                self.send_response(204)
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            payload = encode(response)
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, fmt, *args):
            log("debug", fmt % args)

    server = ThreadingHTTPServer((host, port), Handler)
    server.daemon_threads = True
    log("info", "listening on http://%s:%d/" % (host, server.server_address[1]))
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--http", type=int, metavar="PORT", help="serve JSON-RPC over HTTP instead of stdio")
    parser.add_argument("--host", default="127.0.0.1", help="http bind address (default 127.0.0.1)")
    args = parser.parse_args()
    plugin = CompositionPlugin()
    if args.http is not None:
        serve_http(plugin, args.host, args.http)
    else:
        serve_stdio(plugin)


if __name__ == "__main__":
    main()
