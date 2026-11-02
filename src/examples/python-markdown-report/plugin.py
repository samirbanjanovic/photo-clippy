#!/usr/bin/env python3
"""photo-clippy reference plugin: a Markdown report renderer (kind "report", format "md").

Standard library only (Python 3.9+). Speaks JSON-RPC 2.0 over stdio as described in ../PROTOCOL.md.

    python3 plugin.py      # spawned by the host; one JSON message per line on stdin/stdout

`report.render` receives {model: ReportModel, target: RenderTarget} and writes
<target.outputDirectory>/<target.baseName>.md containing a few key tables. Every field of the
model is treated as optional so that the renderer keeps working when the host adds new fields.

Settings:
    maxBins   maximum number of rows per distribution table (default 12)
    maxLenses maximum number of recommended lenses listed (default 5)
"""

import json
import os
import sys
import traceback

PLUGIN_ID = "ext.python-markdown-report"
PLUGIN_VERSION = "1.0.0"
CONTRACT_MAJOR = "1"

PARSE_ERROR = -32700
INVALID_REQUEST = -32600
METHOD_NOT_FOUND = -32601
INVALID_PARAMS = -32602
INTERNAL_ERROR = -32603

DEFAULT_SETTINGS = {"maxBins": 12, "maxLenses": 5}

# Distribution ids defined by the host contract (DistributionIds), with fallback titles.
DISTRIBUTIONS = [("genre", "Genres"), ("focal.eq", "Focal length (35 mm equivalent)"), ("aperture", "Aperture")]


class RpcError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


def log(level, message):
    sys.stderr.write("%s: %s\n" % (level, message))
    sys.stderr.flush()


# --------------------------------------------------------------------------------------------------
# Markdown helpers
# --------------------------------------------------------------------------------------------------

def cell(value):
    if value is None:
        return ""
    if isinstance(value, float):
        return "%.4g" % value
    return str(value).replace("|", "\\|").replace("\n", " ")


def percent(value):
    return "" if value is None else "%.1f %%" % (float(value) * 100.0)


def table(headers, rows):
    lines = ["| " + " | ".join(headers) + " |", "|" + "|".join("---" for _ in headers) + "|"]
    lines.extend("| " + " | ".join(cell(value) for value in row) + " |" for row in rows)
    return lines


def render_markdown(model, settings):
    run = model.get("run") or {}
    profile = model.get("profile") or {}
    library = profile.get("library") or {}
    out = ["# %s" % (model.get("title") or "Photo habits"), ""]

    out += ["## Run", ""]
    out += table(["Field", "Value"], [
        ("Run id", run.get("runId")),
        ("Started (UTC)", run.get("startedUtc")),
        ("Finished (UTC)", run.get("finishedUtc")),
        ("Tool version", run.get("toolVersion")),
        ("Plugins", ", ".join(p.get("id", "?") for p in run.get("plugins") or [])),
    ])
    out.append("")

    out += ["## Library", ""]
    out += table(["Metric", "Value"], [
        ("Files seen", library.get("filesSeen")),
        ("Files with metadata", library.get("filesWithMetadata")),
        ("Captures", library.get("captures")),
        ("Captures in scope", library.get("capturesInScope")),
        ("Frames analyzed", library.get("framesAnalyzed")),
        ("First capture", library.get("firstCapture")),
        ("Last capture", library.get("lastCapture")),
    ])
    out.append("")

    distributions = profile.get("distributions") or {}
    for distribution_id, fallback_title in DISTRIBUTIONS:
        distribution = distributions.get(distribution_id)
        if not distribution or not distribution.get("bins"):
            continue
        title = distribution.get("title") or fallback_title
        unit = distribution.get("unit")
        out += ["## %s%s" % (title, " (%s)" % unit if unit else ""), ""]
        bins = sorted(distribution["bins"], key=lambda b: -(b.get("count") or 0))[: int(settings["maxBins"])]
        out += table(["Bin", "Count", "Share"], [(b.get("label"), b.get("count"), percent(b.get("share"))) for b in bins])
        out.append("")

    lenses = profile.get("lenses") or []
    if lenses:
        out += ["## Lenses used", ""]
        out += table(["Lens", "Captures", "Share", "Median focal (35 mm)", "Median f-number"], [
            (lens.get("display") or lens.get("key"), lens.get("captures"), percent(lens.get("share")),
             lens.get("medianFocal35"), lens.get("medianFNumber"))
            for lens in lenses[: int(settings["maxBins"])]
        ])
        out.append("")

    recommendation = model.get("recommendation")
    if recommendation:
        out += ["## Recommendations", ""]
        if recommendation.get("headline"):
            out += ["> %s" % recommendation["headline"], ""]
        ranked = (recommendation.get("ranked") or [])[: int(settings["maxLenses"])]
        out += table(["Rank", "Lens", "Score", "Verdict"], [
            (r.get("rank"), (r.get("lens") or {}).get("name"), r.get("score"), r.get("verdict")) for r in ranked
        ])
        out.append("")

    insights = model.get("insights") or []
    if insights:
        out += ["## Insights", ""]
        out += ["- **%s**: %s" % (cell(i.get("title")), cell(i.get("text"))) for i in insights]
        out.append("")

    return "\n".join(out)


# --------------------------------------------------------------------------------------------------
# JSON-RPC methods and stdio loop (same structure as ../python-composition/plugin.py)
# --------------------------------------------------------------------------------------------------

class ReportPlugin:
    def __init__(self):
        self.settings = dict(DEFAULT_SETTINGS)
        self.initialized = False
        self.stopping = False
        self.rendered = 0

    def initialize(self, params):
        if str(params.get("contractVersion", "1.0")).split(".")[0] != CONTRACT_MAJOR:
            raise RpcError(INVALID_PARAMS, "unsupported contract version %r" % params.get("contractVersion"))
        for key, value in (params.get("settings") or {}).items():
            if key in DEFAULT_SETTINGS:
                if not isinstance(value, int) or isinstance(value, bool) or value < 1:
                    raise RpcError(INVALID_PARAMS, "setting %r must be a positive integer" % key)
                self.settings[key] = value
        self.initialized = True
        return {
            "manifest": {
                "id": PLUGIN_ID,
                "name": "Python Markdown report",
                "version": PLUGIN_VERSION,
                "kind": "report",
                "contractVersion": "1.0",
                "description": "Writes <baseName>.md with run, library, distribution and recommendation tables.",
                "capabilities": ["format:md"],
            },
            "format": "md",
        }

    def health(self, params):
        return {"healthy": self.initialized, "message": "%d report(s) rendered" % self.rendered}

    def report_render(self, params):
        model, target = params.get("model"), params.get("target")
        if not isinstance(model, dict) or not isinstance(target, dict) or not target.get("outputDirectory"):
            raise RpcError(INVALID_PARAMS, "params need 'model' and 'target.outputDirectory'")
        directory = target["outputDirectory"]
        os.makedirs(directory, exist_ok=True)
        path = os.path.join(directory, "%s.md" % (target.get("baseName") or "photo-report"))
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(render_markdown(model, self.settings))
        self.rendered += 1
        log("info", "wrote %s" % path)
        return {"files": [path]}

    def shutdown(self, params):
        self.stopping = True

    def methods(self):
        return {
            "initialize": self.initialize,
            "health": self.health,
            "report.render": self.report_render,
            "shutdown": self.shutdown,
        }


def handle_line(plugin, raw):
    try:
        message = json.loads(raw)
    except ValueError as error:
        return {"jsonrpc": "2.0", "id": None, "error": {"code": PARSE_ERROR, "message": str(error)}}
    if not isinstance(message, dict) or not isinstance(message.get("method"), str):
        return {"jsonrpc": "2.0", "id": None, "error": {"code": INVALID_REQUEST, "message": "invalid request"}}
    request_id, is_notification = message.get("id"), "id" not in message
    handler = plugin.methods().get(message["method"])
    try:
        if handler is None:
            raise RpcError(METHOD_NOT_FOUND, "method not found: %s" % message["method"])
        result = handler(message.get("params") or {})
        response = {"jsonrpc": "2.0", "id": request_id, "result": result}
    except RpcError as error:
        response = {"jsonrpc": "2.0", "id": request_id, "error": {"code": error.code, "message": error.message}}
    except Exception as error:
        log("error", "%s crashed: %s\n%s" % (message["method"], error, traceback.format_exc()))
        response = {"jsonrpc": "2.0", "id": request_id, "error": {"code": INTERNAL_ERROR, "message": str(error)}}
    return None if is_notification else response


def main():
    protocol_out = sys.stdout.buffer
    sys.stdout = sys.stderr
    plugin = ReportPlugin()
    for raw in sys.stdin.buffer:
        if not raw.strip():
            continue
        response = handle_line(plugin, raw)
        if response is not None:
            protocol_out.write(json.dumps(response, separators=(",", ":")).encode("utf-8") + b"\n")
            protocol_out.flush()
        if plugin.stopping:
            break


if __name__ == "__main__":
    main()
