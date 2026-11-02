# External plugins for photo-clippy

photo-clippy can load components written in **any language** as out-of-process plugins. A plugin
is a program that speaks JSON-RPC 2.0 either over **stdio** (the host starts it as a child
process) or over **HTTP** (you run it as a service). The full wire contract is in
[PROTOCOL.md](PROTOCOL.md).

| example | kind | transports | what it shows |
|---------|------|------------|---------------|
| [`python-composition/plugin.py`](python-composition/plugin.py) | `composition` | stdio, http | parsing PPM frames, `extras` metrics, a genre prior, settings validation, threaded http server |
| [`python-markdown-report/plugin.py`](python-markdown-report/plugin.py) | `report` (format `md`) | stdio | reading the `ReportModel`, writing `<baseName>.md` |

Both use only the Python 3.9 standard library.

## When components run

`run` scans and renders a habits report; `scan` only analyses. Daemon scans also analyse
and render without ranking lenses. Owned gear and its usage come from photo metadata only.
The lens catalog (kind `lensCatalog`, `builtin.lenses.lensfun` by default) is never used
during a scan. It only answers the user's searches when picking candidates, and it only
downloads on an explicit `lenses update`.

Recommendation plugins run only on an explicit request: `recommend` (prompts for
candidates on an interactive terminal), recommendation options on `run` or `report`,
or `POST /api/v1/recommendations` with runtime candidates. Configured candidates are
optional defaults for those requests, not a trigger for automatic ranking. An explicit
request with no candidates can suggest generic lens specifications, not catalog products;
catalog lenses become candidates only when the user picks them.
Saved analysis is reused; no rescan is needed. A plain `report` reuses any saved ranking.
New scans clear the ranking rather than carry a stale comparison into a fresh report,
so renderers must support an absent `ReportModel.recommendation`.

## Using a plugin

Add an entry to `plugins.external` in `photo-clippy.json`:

```json
{
  "plugins": {
    "external": [
      {
        "id": "ext.python-composition",
        "kind": "composition",
        "transport": "stdio",
        "command": "python3",
        "args": ["examples/python-composition/plugin.py"],
        "weight": 0.3,
        "settings": { "nightThreshold": 0.12, "nightPrior": 0.25 }
      },
      {
        "id": "ext.python-composition-http",
        "kind": "composition",
        "transport": "http",
        "url": "http://127.0.0.1:7070/",
        "timeoutSeconds": 30,
        "maxConcurrency": 8
      },
      {
        "id": "ext.python-markdown-report",
        "kind": "report",
        "command": "python3",
        "args": ["examples/python-markdown-report/plugin.py"]
      }
    ]
  }
}
```

For the http entry, start the service yourself first: `python3 examples/python-composition/plugin.py --http 7070`.

| option | default | meaning |
|--------|---------|---------|
| `id` | **required** | unique plugin id. The plugin's manifest id must equal it or start with `ext.`. |
| `kind` | **required** | `metadata`, `composition`, `color`, `recommendation`, `report` or `lensCatalog`. Must match the manifest's kind. |
| `transport` | `"stdio"` | `stdio` (host spawns `command`) or `http` (host POSTs to `url`). |
| `command` | — | stdio: executable. A bare name is looked up on `PATH`; a relative path with a separator is resolved against `workingDirectory`. |
| `args` | `[]` | stdio: arguments, passed verbatim (no shell). Relative script paths are resolved by the child against `workingDirectory`. |
| `workingDirectory` | host's current directory | stdio: working directory of the child process. |
| `environment` | `{}` | stdio: extra environment variables for the child. |
| `url` | — | http: absolute `http(s)` endpoint. |
| `timeoutSeconds` | `60` | per-request deadline; a timed-out request fails alone and the process keeps running. |
| `maxConcurrency` | `4` | maximum number of requests in flight to this plugin. |
| `maxRestarts` | `3` | stdio: crash restarts (exponential backoff) before the plugin is marked degraded. |
| `settings` | `{}` | free-form JSON passed to the plugin in `initialize.settings`. |
| `weight` | `1.0` | composition plugins: weight of this plugin's genre prior when the host fuses genre evidence. |

## Writing a plugin in any language

A plugin is a loop: read a line, parse JSON, dispatch on `method`, write one line of JSON back.
The minimal contract (details in [PROTOCOL.md](PROTOCOL.md)):

1. **`initialize`** is the first request. Validate `params.settings`, remember `params.taxonomy`
   (the genre ids you may emit), then return the manifest:
   ```json
   {"jsonrpc":"2.0","id":1,"result":{
     "manifest":{"id":"ext.my-plugin","name":"My plugin","version":"1.0.0","kind":"composition","contractVersion":"1.0"},
     "requirements":{"maxEdge":384}}}
   ```
2. **Your component method**: `composition.analyze`, `color.analyze`, `metadata.extract`,
   `recommend.run` or `report.render`. For frame analyzers, read the binary PPM at
   `params.frame.path` (stdio) or base64-decode `params.frame.data` (http).
3. **`health`** (optional): return `{"healthy": true, "message": "…"}`.
4. **`shutdown`** notification or stdin EOF: exit.
5. Errors: reply with `{"jsonrpc":"2.0","id":<id>,"error":{"code":-32602,"message":"…"}}`; never exit
   because one request failed.
6. stdout is for protocol messages only; log to stderr with an `info:`/`warn:`/`error:` prefix.
7. Bump `manifest.version` whenever your output changes: the host caches results per plugin version.

### Go sketch (stdio)

```go
scanner := bufio.NewScanner(os.Stdin)
scanner.Buffer(make([]byte, 1<<20), 256<<20)
out := json.NewEncoder(os.Stdout) // Encode appends '\n'; responses are compact single lines
for scanner.Scan() {
    var req struct {
        ID     *json.RawMessage `json:"id"`
        Method string           `json:"method"`
        Params json.RawMessage  `json:"params"`
    }
    if json.Unmarshal(scanner.Bytes(), &req) != nil { continue }
    result, rpcErr := dispatch(req.Method, req.Params) // your code
    if req.ID == nil { if req.Method == "shutdown" { return }; continue }
    if rpcErr != nil {
        out.Encode(map[string]any{"jsonrpc": "2.0", "id": req.ID, "error": rpcErr})
    } else {
        out.Encode(map[string]any{"jsonrpc": "2.0", "id": req.ID, "result": result})
    }
}
```

### Rust sketch (stdio, serde_json)

```rust
let stdin = std::io::stdin();
let mut stdout = std::io::stdout().lock();
for line in stdin.lock().lines() {
    let msg: serde_json::Value = match serde_json::from_str(&line?) { Ok(v) => v, Err(_) => continue };
    let method = msg["method"].as_str().unwrap_or_default();
    if msg.get("id").is_none() { if method == "shutdown" { break } else { continue } }
    let reply = match dispatch(method, &msg["params"]) {           // your code
        Ok(result) => json!({"jsonrpc": "2.0", "id": msg["id"], "result": result}),
        Err((code, message)) => json!({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": code, "message": message}}),
    };
    writeln!(stdout, "{}", reply)?;
    stdout.flush()?;
}
```

Both sketches answer requests sequentially, which is valid. To use `maxConcurrency > 1` effectively,
process requests on worker threads and write each response (one whole line, under a lock) as soon
as it is ready — responses are matched by `id`, not by order.

## Trying the examples by hand

```sh
cd examples/python-composition
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"contractVersion":"1.0","kind":"composition","settings":{},"taxonomy":{"genres":[{"id":"night","name":"Night"}]}}}' \
  '{"jsonrpc":"2.0","id":2,"method":"health"}' \
  '{"jsonrpc":"2.0","method":"shutdown"}' | python3 plugin.py
```

Over http:

```sh
python3 examples/python-composition/plugin.py --http 7070 &
curl -s http://127.0.0.1:7070/ -d '{"jsonrpc":"2.0","id":1,"method":"health"}'
```

## Using plugins from .NET

The host creates plugins through `PhotoClippy.Plugins.External`:

```csharp
var options = config.GetProperty("plugins").GetProperty("external")[0].Deserialize<ExternalPluginOptions>(ContractJson.Options)!;
await using var plugin = (IExternalPlugin)await ExternalPlugins.CreateAsync(options, pluginContext, ct);
var analyzer = (ICompositionAnalyzer)plugin; // the adapter implements the interface matching options.Kind
```
