# photo-clippy external plugin protocol (contract 1.x)

This is the interop contract between the photo-clippy host (.NET) and an out-of-process plugin
written in any language. A plugin is a program that speaks **JSON-RPC 2.0** over **stdio** (the host
spawns it) or **HTTP** (the plugin runs as a service). It implements exactly one component kind:

| kind          | method(s) the plugin must implement | .NET interface the host adapts it to |
|---------------|-------------------------------------|--------------------------------------|
| `metadata`    | `metadata.extract`                  | `IMetadataExtractor`                 |
| `composition` | `composition.analyze`               | `ICompositionAnalyzer`               |
| `color`       | `color.analyze`                     | `IColorAnalyzer`                     |
| `recommendation` | `recommend.run`                  | `IRecommender`                       |
| `report`      | `report.render`                     | `IReportRenderer`                    |

Every plugin also implements `initialize`, should implement `health`, and should honour the
`shutdown` notification. The reference implementations are
[`python-composition/plugin.py`](python-composition/plugin.py) (stdio + http) and
[`python-markdown-report/plugin.py`](python-markdown-report/plugin.py) (stdio).

## 1. Transports

### 1.1 stdio (default)

* The host starts `command args...` in `workingDirectory` (default: the host's current directory).
  A relative `command` containing a path separator is resolved against `workingDirectory`; a bare
  name (`python3`, `node`) is looked up on `PATH`.
* **Framing: newline-delimited JSON.** Each message is one UTF-8 JSON object followed by `\n`
  (`\r\n` is accepted). Messages must not contain raw newlines — every JSON serializer's compact
  mode satisfies this. Maximum line length is 256 MiB (longer lines kill the process).
* **stdin** carries host → plugin messages, **stdout** carries plugin → host messages and nothing
  else. Redirect your own prints/logging to stderr (the Python example does `sys.stdout = sys.stderr`
  after grabbing the binary stdout for protocol use). Flush after every message.
* **stderr** is a log channel: every line is forwarded to the host logger. The level is taken from
  the line prefix (case-insensitive, optionally preceded by `[`): `error`/`critical`/`fatal`/`Traceback`
  → Error, `warn…` → Warning, `debug`/`trace` → Debug, anything else → Information.
  Example: `warn: frame too small, skipping`.
* Lines on stdout that are not valid JSON are logged as warnings and ignored; they do not break
  the session. Messages carrying a `method` (plugin → host requests/notifications) are ignored.
* Requests are **multiplexed**: the host may send up to `maxConcurrency` requests before receiving
  any response. Responses may be returned **in any order**; they are matched by `id`. A plugin
  that processes requests sequentially is perfectly valid — it just gets less throughput.
* Environment variables set by the host (user-configured `environment` entries win):

  | variable                   | value                                                   |
  |----------------------------|---------------------------------------------------------|
  | `PHOTO_CLIPPY_PLUGIN_ID` | configured plugin id                                    |
  | `PHOTO_CLIPPY_TRANSPORT` | `stdio`                                                 |
  | `PHOTO_CLIPPY_STATE_DIR` | per-plugin writable state directory (same as `initialize.stateDirectory`) |
  | `PYTHONUNBUFFERED`         | `1`                                                     |
  | `PYTHONIOENCODING`         | `utf-8`                                                 |

### 1.2 http

* The plugin is a long-running service; the host never starts or stops it.
* Every call is `POST <url>` with `Content-Type: application/json; charset=utf-8` and a **single**
  JSON-RPC request object as body (no batches). The response body is a single JSON-RPC response
  object, normally with status `200`.
* A non-2xx status whose body is a JSON-RPC error object is treated as that error; any other non-2xx
  status is a transport failure. Notifications are never sent over http (the host does not send
  `shutdown`, because it does not own the service lifetime).
* The host may issue up to `maxConcurrency` requests concurrently, so the server must handle
  concurrent requests (e.g. `ThreadingHTTPServer`).
* The host calls `initialize` once at startup; the service must accept it repeatedly (several hosts
  or restarts of the host may initialize the same service).

## 2. Message envelope

Standard JSON-RPC 2.0:

```jsonc
// request (host → plugin)
{"jsonrpc":"2.0","id":7,"method":"composition.analyze","params":{...}}
// success response (plugin → host)
{"jsonrpc":"2.0","id":7,"result":{...}}
// error response
{"jsonrpc":"2.0","id":7,"error":{"code":-32602,"message":"params.frame is missing","data":{"any":"json"}}}
// notification (no id, no response expected)
{"jsonrpc":"2.0","method":"shutdown"}
```

* Ids sent by the host are positive integers; echo them back unchanged (numeric strings are tolerated).
* A response must contain exactly one of `result` or `error`.
* `params` is always an object (it is omitted for `health`).

### 2.1 JSON conventions (ContractJson)

All payloads follow the host's `ContractJson` conventions:

* property names are **camelCase**; property names are matched case-insensitively on input;
* enums are **camelCase strings** (`"composition"`, `"jpeg"`, `"raw"`, `"neutral"`);
* `null` values are omitted by the host — treat a missing property as null/default;
* numbers must be finite (no `NaN`/`Infinity`: they are not valid JSON);
* timestamps are ISO-8601 strings (`DateTimeOffset` carries an offset, `DateTime` local times do not);
* unknown properties are ignored in both directions, so new optional fields can be added without
  breaking anyone. Plugins should ignore fields they do not know.

## 3. Lifecycle

```
host                                         plugin
 | --- initialize {contractVersion,...} ---->  |   validate, load models, return manifest
 | <-- {manifest, requirements?} ------------  |
 | --- composition.analyze (id 2) ---------->  |
 | --- composition.analyze (id 3) ---------->  |   (concurrent; answer in any order)
 | <-- result (id 3) ------------------------  |
 | <-- result (id 2) ------------------------  |
 | --- health ------------------------------>  |
 | <-- {healthy, message} -------------------  |
 | --- shutdown (notification) ------------->  |   finish in-flight work, then exit
 |     (stdin closed; process killed after 2 s if still running)
```

* `initialize` is always the first request and is sent exactly once per process. It uses the same
  timeout as any other request (`timeoutSeconds`), so keep model loading within it.
* **Timeouts:** each request has a deadline of `timeoutSeconds` (default 60). If it expires, that
  single request fails on the host side (code `-32001`); a late response is discarded. The process
  is *not* restarted for a timeout.
* **Crashes (stdio):** if the process exits or closes stdout, every in-flight request fails with
  `-32002`. The host restarts the process with exponential backoff (250 ms, 500 ms, 1 s, … capped at
  15 s) and sends `initialize` again; requests issued meanwhile wait for the restart (bounded by
  their timeout). After `maxRestarts` restarts (default 3) the plugin is **degraded**: all further
  calls fail immediately with `-32003` and `health` reports unhealthy. The manifest returned by a
  restarted process must have the same id/version/fingerprint; otherwise the original is kept and a
  warning is logged.
* **Shutdown (stdio):** on dispose the host sends the `shutdown` notification, closes stdin and waits
  up to 2 s before killing the whole process tree. Treat stdin EOF as shutdown too.
* Each request should be independent; the host may send requests for different photos in any order.

## 4. Methods

### 4.1 `initialize`

Params:

| field             | type          | description |
|-------------------|---------------|-------------|
| `contractVersion` | string        | host contract version, currently `"1.0"`. Refuse (error) if the major is unknown to you. |
| `kind`            | string        | the kind the host configured this plugin as. |
| `pluginId`        | string        | the configured plugin id. |
| `settings`        | object        | the plugin's `settings` object from the host config (`{}` if none). Validate it; reply `-32602` on bad settings. |
| `dataDirectory`   | string        | absolute path of the host data directory (read-only reference data). |
| `modelsDirectory` | string        | absolute path where model files live. |
| `stateDirectory`  | string        | absolute, writable, per-plugin directory (`<state>/external/<id>`) for caches. |
| `taxonomy`        | GenreTaxonomy | `{version, genres:[{id, name, group, prompts[], junk?, affinity?}]}` — the genre ids the host knows. |

Result:

| field                   | type     | description |
|-------------------------|----------|-------------|
| `manifest`              | object   | **required**, see below. |
| `requirements.maxEdge`  | int      | frame components: longest edge (px) of the frames you want (default 512, clamped to 32..8192). The host downsamples larger frames before sending. |
| `format`                | string   | report renderers: output format id, e.g. `"md"`. Fallback: a `"format:<fmt>"` capability, then the last dot-segment of the id. |
| `extensions`            | string[] | metadata extractors: file extensions handled (`".jpg"`); empty/absent = any. |
| `mediaKinds`            | string[] | metadata extractors: media kinds handled (`"jpeg"`, `"heif"`, `"tiff"`, `"png"`, `"raw"`, `"other"`); empty/absent = any. A file is handled if its extension **or** kind matches. |
| `maxBatch`              | int      | metadata extractors: maximum files per `metadata.extract` request (default 256). |

`manifest` (PluginManifest):

| field             | type     | rule |
|-------------------|----------|------|
| `id`              | string   | must equal the configured `id`, **or** start with `ext.` |
| `name`            | string   | non-empty |
| `version`         | string   | non-empty; bump it whenever your outputs change (it is part of the cache key `id@version#fingerprint`) |
| `kind`            | string   | must equal the configured kind |
| `contractVersion` | string   | major must be `1` (default `"1.0"`) |
| `description`     | string?  | |
| `capabilities`    | string[] | free-form tags (`"extras"`, `"genres"`, `"format:md"`) |
| `fingerprint`     | string?  | optional; if omitted and `settings` is non-empty the host derives one from the settings hash |

A manifest that violates these rules makes plugin creation fail (`-32007`) and the process is stopped.

### 4.2 `health`

No params. Result: `{"healthy": bool, "message": string?}`. Called on demand, outside the
concurrency throttle, with a timeout of `min(timeoutSeconds, 10)` s. A plugin that does not implement
it (`-32601`) is reported healthy as long as it responds.

### 4.3 `metadata.extract` (kind `metadata`)

Params: `{"files": SourceFile[]}` — at most `maxBatch` files per request.

```jsonc
{"files":[{"path":"/photos/a.jpg","size":123456,"lastWriteUtc":"2024-05-01T10:00:00+00:00","kind":"jpeg"}]}
```

Result: `{"results": PhotoMetadata[]}` (a bare array is also accepted). One element per input file,
each carrying the input `path` (results are matched by `path`, case-insensitively; elements without
a `path` are matched by position). PhotoMetadata fields (all optional except `path`): `extractor`,
`make`, `model`, `bodySerial`, `software`, `lensMake`, `lensModel`, `lensSerial`, `lensSpec`,
`focalLengthMm`, `focalLength35mm`, `fNumber`, `exposureTimeS`, `iso`, `exposureBiasEv`,
`exposureProgram`, `meteringMode`, `whiteBalance`, `flashFired`, `captureLocal` (local time, no
offset), `offsetTime`, `subSec`, `imageUniqueId`, `width`, `height`, `orientation`,
`focalPlaneXResolutionPerMm`, `subjectDistanceM`, `rating`, `hasGps`, `latitude`, `longitude`,
`warnings` (string[]), `extras` (object of string → string).

Host guarantees: the host always ends up with exactly one result per input. Missing results become
an empty PhotoMetadata with a warning; if the request fails with a JSON-RPC error every file gets
that error as a warning. Report per-file problems in `warnings` rather than failing the whole batch.

### 4.4 `composition.analyze` (kind `composition`) and `color.analyze` (kind `color`)

Params:

```jsonc
{
  "frame": {
    "width": 256, "height": 171,             // pixel size of the PPM you receive
    "sourceWidth": 6000, "sourceHeight": 4000, // size of the original image
    "encoding": "ppm",
    "path": "/state/external-tmp/ext.x-4242-17.ppm"  // stdio: temp file, deleted after your response
    // "data": "UDYKMjU2IDE3MQoyNTUK..."              // http: base64 of the same PPM bytes
  },
  "context": {
    "captureId": "c0ffee…", "sourcePath": "/photos/a.jpg",
    "facts": { "focal35Mm": 35, "fNumber": 1.8, "iso": 3200, "lensDisplay": "…", … }  // optional CaptureFacts
  }
}
```

**Frame format:** binary PPM, `P6\n<width> <height>\n255\n` followed by `width*height*3` bytes of
RGB24, row-major, top-left origin, sRGB. The image is already orientation-corrected and downsampled so that
`max(width, height) <= requirements.maxEdge`. Read the file synchronously before responding — it is
deleted as soon as your response arrives. Never write to it.

`composition.analyze` result — CompositionFeatures, all optional:

| field | type | meaning |
|-------|------|---------|
| `genres` | object id → score | genre evidence in 0..1. Ids must exist in `initialize.taxonomy` (unknown ids are dropped); scores are clamped to 0..1 and renormalized if they sum to more than 1. The host fuses priors from several plugins using each plugin's configured `weight`. |
| `subjectX`, `subjectY` | 0..1 | subject centre (normalized) |
| `subjectArea` | 0..1 | |
| `thirdsScore`, `centerScore`, `symmetryScore`, `negativeSpace`, `separationScore` | 0..1 | |
| `horizonTiltDeg`, `horizonY` | number | |
| `horizontalEdges`, `verticalEdges`, `diagonalEdges` | 0..1 | |
| `extras` | object string → number | namespaced free-form metrics, e.g. `"ext.python.meanLuma"`. Non-finite values are dropped. |

`color.analyze` result — ColorFeatures, all optional: `meanLuma`, `lumaP05`, `lumaP50`, `lumaP95`,
`shadowClip`, `highlightClip`, `contrast`, `key` (`"low"`/`"mid"`/`"high"`), `colorfulness`,
`meanChroma`, `saturation`, `monochrome` (bool), `warmth`, `tint`, `sharpness`, `hueHistogram`
(number[]), `lumaHistogram` (number[]), `palette` (`[{"hex":"#a0b0c0","weight":0.3}]`), `extras`.

If you cannot analyze a frame, return a JSON-RPC error: the host logs it and uses empty features for
that photo (it is not counted as a transport failure).

### 4.5 `recommend.run` (kind `recommendation`)

Params: a RecommendationRequest — `{profile, target, samples, candidates, owned, taxonomy, preferences}`
(see `PhotoClippy.Contracts/Recommendation.cs`). Result: a RecommendationSet. `recommenderId`,
`preset` and `target` may be omitted; the host fills them with the manifest id,
`preferences.preset` and the request target. Everything else (`ranked[]`, `weights`, `criteria`,
`headline`, `notes`, …) follows the contract types.

### 4.6 `report.render` (kind `report`)

Params: `{"model": ReportModel, "target": {"outputDirectory": "/abs/out", "baseName": "photo-report", "options": {}}}`.
The host creates `outputDirectory` before the call. Write your file(s) there, typically
`<outputDirectory>/<baseName>.<format>`.

Result: `{"files": ["/abs/out/photo-report.md"]}` — relative paths are resolved against `outputDirectory`.

ReportModel highlights: `title`, `run` (`runId`, `startedUtc`, `finishedUtc`, `toolVersion`,
`plugins[]`, `timings`), `profile` (`library`, `bodies[]`, `lenses[]`, `distributions` keyed by id
such as `"genre"`, `"focal.eq"`, `"aperture"`, `"iso"`, `"time.hour"` — each `{id, title, unit?, bins:[{label, count, share}]}`
— `matrices`, `palette`, `signals`, `quality`), `recommendation` (RecommendationSet or absent),
`insights[]` (`{section, title, text, metric?, tone}`), `taxonomy`.

### 4.7 `shutdown` (notification)

No params, no response. Finish or abandon in-flight work and exit promptly (within 2 s). stdin EOF
means the same thing.

## 5. Error codes

Return standard JSON-RPC codes from your plugin; the host also uses a few of its own codes in the
`-32000..-32099` range when something fails on its side. All surface in .NET as
`ExternalPluginException { PluginId, Code, RemoteMessage, IsRemote, ErrorData }`.

| code   | origin | meaning |
|--------|--------|---------|
| -32700 | plugin | parse error (reply with `id: null`) |
| -32600 | plugin | invalid request |
| -32601 | plugin | method not found |
| -32602 | plugin | invalid params (bad settings, unreadable frame, …) |
| -32603 | plugin | internal error |
| -32000..-32099 | plugin | your own application errors (avoid the host codes below) |
| -32001 | host | request timed out |
| -32002 | host | plugin process exited while the request was in flight |
| -32003 | host | plugin unavailable (degraded after `maxRestarts`) |
| -32004 | host | invalid response (no result/error, undecodable result) |
| -32005 | host | transport failure (http connection error, non-2xx without JSON-RPC body, stdin broken) |
| -32006 | host | failed to start the process |
| -32007 | host | manifest rejected during `initialize` |

## 6. Checklist for plugin authors

1. Read stdin line by line; parse each line as JSON; dispatch on `method`.
2. Write exactly one compact JSON object per line to stdout for each request with an `id`; flush.
3. Never print anything else to stdout; log to stderr with `info:`/`warn:`/`error:` prefixes.
4. Return a manifest whose `kind` matches the config and whose `id` is the configured id or starts with `ext.`.
5. Catch exceptions per request and return a JSON-RPC error instead of exiting.
6. Exit on `shutdown` or stdin EOF.
7. Version your outputs (`manifest.version`): the host caches results by `id@version#fingerprint`.
