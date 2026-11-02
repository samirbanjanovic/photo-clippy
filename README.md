<div align="center">

<img src="src/PhotoClippy.Cli/wwwroot/clippy.svg" alt="Photo Clippy: a paperclip with aperture-blade pupils" width="168" height="168">

# Photo Clippy

**Find out how you really shoot, then pick your next lens with evidence instead of hunches.**

*"It looks like you shoot everything at 35 mm. Would you like help buying a lens?"*

[Features](#features) · [Quick start](#quick-start) · [Screenshots](#screenshots) · [How lenses are ranked](#how-lenses-are-ranked) · [Self-hosting](#self-hosting-with-docker) · [Plugins](#plugins)

</div>

<p align="center">
  <img src="docs/images/report-hero.png" alt="The report's opening page: library summary with captures, lenses, bodies, median aperture and most-used focal length" width="720">
</p>

Photo Clippy reads your photo library, recursively and read-only, and turns the EXIF data and the
pixels into a picture of your habits: bodies, lenses, focal lengths, apertures, ISO, shutter
speeds, light, subjects, composition and colour. Then it ranks the lenses you are considering
buying, such as `24/1.4, 14-24/2.8, 24-70/2.8, 35/1.2, 50/1.2`, against that record and explains
each verdict.

It runs as a cross-platform command-line tool on Linux, macOS and Windows (.NET 10), or as a small
self-hosted service with a web UI. Everything stays on your machine.

## Features

- **Shooting statistics.** Distributions of focal length (native and 35 mm-equivalent), aperture,
  ISO, shutter speed, exposure value, bodies and lenses over time, with "what stands out" insights.
- **Subject recognition.** An optional local CLIP model (ONNX), fused with heuristics, sorts frames
  into 20 subject types such as landscape, portrait, street, astro, night and macro.
- **Composition and colour.** Subject placement and size, rule of thirds, symmetry, negative
  space, horizon tilt, palette, hue, tonal key, warmth and colourfulness.
- **Lens recommendation.** A transparent multi-criteria ranking with uncertainty bands, evidence,
  trade-offs and redundancy against the lenses you already own. Type the candidates or pick
  them by maker and model from the open [Lensfun](https://lensfun.github.io/) lens database.
- **A report worth reading.** A self-contained, offline, print-friendly HTML report in a calm
  Nordic style with light and dark themes, plus JSON and CSV exports.
- **Web UI and daemon.** Scheduled rescans, live progress, a lens-comparison page, settings, accounts
  and a JSON API. It ships as a Docker image.
- **Pluggable everything.** Metadata, composition, colour, recommendation, report, lens database and sign-in are
  separate components behind interface contracts. Swap any of them for a .NET assembly or an
  out-of-process plugin in any language (JSON-RPC over stdio or HTTP).
- **Fast and private.** Results are cached in SQLite, so rescans only read new or changed files. RAW+JPEG
  pairs count once. The only network calls are the explicit model and lens-database downloads.

## Screenshots

All screenshots use the synthetic demo library from [`tools/PhotoClippy.SampleGen`](tools/PhotoClippy.SampleGen).

**Lens recommendation.** Each candidate gets a score with an uncertainty band, a criterion
breakdown, the evidence from your own frames and the trade-offs.

<p align="center"><img src="docs/images/report-recommendation.png" alt="Recommendation section of the report: the 24mm f/1.4 ranked first with score, criteria, evidence and trade-offs" width="720"></p>

**Where you actually shoot.** The focal-length histogram on a logarithmic axis, with the lenses
you own drawn underneath, and how focal length and aperture combine.

<p align="center"><img src="docs/images/report-focal.png" alt="Focal-length histogram with owned lens ranges" width="720"></p>
<p align="center"><img src="docs/images/report-heatmap.png" alt="Native focal-length histogram and a focal length by aperture heat map" width="720"></p>

**The look of your pictures.** Palette, hue, tone, key and colour temperature.

<p align="center"><img src="docs/images/report-look.png" alt="Look section with palette, hue and tone charts" width="720"></p>

**Web UI.** The overview updates live while a scan runs. The Lenses page re-ranks candidates
in a second, without rescanning.

<table>
  <tr>
    <td width="50%"><img src="docs/images/web-overview.png" alt="Web UI overview page with library statistics"></td>
    <td width="50%"><img src="docs/images/web-lenses.png" alt="Web UI lens ranking with the top pick expanded"></td>
  </tr>
</table>

## Quick start

### Docker (web UI)

You need Docker with Compose v2. The first build takes a few minutes.

```sh
git clone https://github.com/samirbanjanovic/photo-clippy.git
cd photo-clippy/deploy
cp .env.example .env               # set CLIPPY_LIBRARY to your photo folder
docker compose up -d --build
docker compose logs photo-clippy | grep 'setup code'
```

Open <http://localhost:8080>, enter the setup code and create the first admin account (see
[Sign-in](#sign-in)). The first scan of the library starts automatically. Under
**Settings → Models** you can download the optional subject-recognition model, about 330 MB.
[Self-hosting with Docker](#self-hosting-with-docker) covers volumes, permissions, reverse proxies
and private registries.

### Command line

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Linux, macOS or Windows.

```sh
dotnet publish src/PhotoClippy.Cli -c Release -o ~/.local/photo-clippy
alias photo-clippy=~/.local/photo-clippy/photo-clippy

photo-clippy doctor                 # check decoder, models, exiftool, plugins
photo-clippy models fetch           # optional, ~330 MB: CLIP subject recognition (SHA-256 verified)
photo-clippy run ~/Pictures -c "24/1.4, 14-24/2.8, 24-70/2.8, 35/1.2, 50/1.2" --open
```

`run` scans the folder, ranks the candidates and opens the HTML report. Without `-c` on an
interactive terminal you are asked for candidates. With `--no-prompt`, or when not on a
terminal, the recommender suggests archetypes based on your gaps, such as a wide prime or a
standard zoom. Without the CLIP model the analysis still runs, and subjects come from the
heuristic analyzer alone.

To try it without your own photos, generate the demo library used for the screenshots:

```sh
dotnet run --project tools/PhotoClippy.SampleGen -- /tmp/samplelib --count 600 --seed 42
photo-clippy run /tmp/samplelib -c "24/1.4, 35/1.2" --open
```

## Commands

| command | purpose |
|---|---|
| `run <paths>` | Scan, rank and render in one go (the usual entry point). |
| `scan <paths>` | Scan and analyse only (incremental). |
| `recommend` | Re-rank against the last scan with new candidates, preset or limits (no rescan). |
| `report` | Re-render the last scan (re-ranks first if recommendation options are given). |
| `summary` | Terminal summary of the last scan: library, bodies, lenses, focal peaks, insights, ranking. |
| `inspect <file>` | Debug one photo: raw metadata per extractor, normalized capture, frame, every analyzer's output. |
| `serve [paths]` | Daemon with a web UI at `/` (overview, report, lens ranking, settings), scheduled scans, JSON API under `/api/v1`. |
| `plugins` | Built-in, directory and external plugins with version, selection and health. |
| `users list\|add\|passwd\|admin\|remove` | Web UI accounts (`builtin.auth.local`). See [Sign-in](#sign-in). |
| `models list\|fetch` | Model files required by plugins. |
| `lenses update\|search\|info` | Lens database: download it, then find candidates by maker, model, focal length or aperture. See [Lens database](#lens-database). |
| `doctor` | Installation check. |
| `config show\|path\|init` | Effective config, where it comes from, starter file. |
| `cache stats\|clear` | Analysis cache maintenance. |

Useful options (see `photo-clippy <command> --help`):

- **Scan:** `--ignore '**/exports/**'`, `--scope all` (include phones and compacts; default is
  interchangeable-lens bodies only), `--since/--until yyyy-MM-dd`, `--no-frames` (metadata only,
  much faster), `-j` workers, `--retry-failed`.
- **Recommend:** `-c`, `--body` (when you own several), `--preset`, `--budget`, `--max-weight`,
  `--top N` (default 8; `0` = all), `--no-prompt`.
- **Report:** `-o <dir>`, `-f html,json,csv`, `--title`, `--open`.
- **Global:** `--home <dir>`, `--config <file>`, `--json` (NDJSON on stdout for scripting), `-v`,
  `-q`.

Exit codes: `0` ok, `1` error, `2` usage/config (for example an unparseable lens), `3` no data,
`4` partial (a renderer, download or plugin failed), `130` cancelled.

### Candidate shorthand

```
24/1.4                     prime: focal/f-number
14-24/2.8                  constant-aperture zoom
100-400/4.5-5.6            variable aperture (interpolated in log-focal space)
24-70mm f/2.8              "mm" and "f/" are optional
35/1.2 1060g $2800 "Z 35mm f/1.2 S"   optional weight (g or kg), price (USD) and display label
```

Separate entries with commas or semicolons; commas inside quotes are kept. Weights enable the
portability criterion and `--max-weight`. Prices enable the value criterion and `--budget`. Either
is used only when every candidate supplies it.

### Lens database

Instead of typing specs, you can pick candidates from the [Lensfun](https://lensfun.github.io/)
database (about 1,100 lenses from more than 50 makers, CC BY-SA 3.0). It is downloaded once, on
request, and kept in `<home>/state/builtin.lenses.lensfun/`:

```bash
photo-clippy lenses update                     # ~350 KB from GitHub; the only network access
photo-clippy lenses search 23 f/1.4            # filtered to your camera's mount from the last scan
photo-clippy lenses search sigma 18-50 --any-mount
photo-clippy lenses search --maker Viltrox --body "Nikon Z 6"
```

Search words all have to match: `35` or `35mm` (a prime at that length or a zoom covering it),
`24-70` (a zoom with about that range), `f/1.4` (that fast or faster), plus any words of the name,
maker, mount or type (`fisheye`). Each result prints a candidate shorthand such as
`23/1.4 "Fujifilm XF 23mm f/1.4 R"` to paste into `-c`. On the **Lenses** page, the *Browse lens
database* card does the same with one click per lens, defaulting to your camera's mount.

Lensfun lists lenses it has calibration data for. That includes most current mirrorless and DSLR
glass, but the newest releases can be missing, and it has **no weight or price**. Append those
to a picked line (`… 560g $1100`) to score portability and value. For offline installs, point
`plugins.settings."builtin.lenses.lensfun".directory` at a folder of Lensfun XML files, such as
a checkout of `lensfun/data/db`. Set `plugins.lensCatalog` to `""` to turn the feature off, or to
the id of your own catalog plugin.

## What is analysed

| component | built-in plugin(s) | signals |
|---|---|---|
| Metadata | `builtin.metadata.native` (default), `builtin.metadata.exiftool` (optional, merged field by field) | body, lens (incl. adapted/manual), focal and 35 mm-equivalent focal, aperture, shutter, ISO, exposure bias, program, metering, flash, WB, date |
| Composition | `builtin.composition.clip`, `builtin.composition.heuristic` (fused) | subject/genre (20-type taxonomy), subject position and size, thirds/centre/symmetry, negative space, subject separation, horizon tilt and height, edge orientation |
| Colour | `builtin.color` | luminance percentiles, clipping, contrast and key, colourfulness, saturation, monochrome, warmth/tint, sharpness, hue and luma histograms, palette |
| Recommendation | `builtin.recommend.mcda` | ranked candidates with criterion breakdown, uncertainty and narrative |
| Report | `builtin.report.html`, `.json`, `.csv` | renderers |
| Lens database | `builtin.lenses.lensfun` | lens catalog for picking candidates (see [Lens database](#lens-database)) |

- **Formats:** JPEG, HEIF/HEIC/AVIF, TIFF, PNG, WebP, and RAW via its embedded preview (DNG, NEF,
  NRW, ARW, CR2, CR3, RAF, ORF, RW2, PEF, SRW, IIQ, 3FR and more). RAWs are not demosaiced, so
  colour and composition see the camera's rendering, including film simulations and picture
  styles: a frame shot with a monochrome simulation counts as monochrome. RAW+JPEG pairs (same folder
  and stem) and copies of the same instant count as one capture, so habits aren't inflated.
  Screenshots and document photos are excluded once their genre probability reaches
  `analysis.junkThreshold`.
- **Distributions:** distributions and peaks use 35 mm-equivalent focal lengths so bodies with
  different sensor sizes compare fairly.
- **Lens lists:** the "owned" lens list comes only from your metadata.

### How lenses are ranked

The recommender is multi-criteria decision analysis (MCDA) over your frames. Frames can be weighted
by recency with `analysis.recencyHalfLifeYears`; the default is `0`, which weights every frame
equally.

| criterion | meaning |
|---|---|
| Fit | Share of your frames whose 35 mm-eq focal length the lens covers (zoom range with soft edges, or a prime's natural framing band). |
| Low light | Stops gained on frames shot at your lens' maximum aperture, counted 1.5× when the frame was also light-starved. |
| Isolation | Entrance-pupil (focal ÷ f-number) gain over what you used, weighted toward wide-open frames and isolation genres. |
| Reach | Frames pinned at a zoom end-stop or outside every owned lens that the candidate covers by going ≥ 15 % further. |
| Genre match | Overlap with each genre's preferred focal range (×1.5 when the genre wants fast glass), weighted by your genre mix. |
| Portability / Value | Lightest weight ÷ this lens' weight, and lowest price ÷ this lens' price (only when all candidates supply them). |

- **Presets:** `balanced` (default), `low-light`, `reach`, `versatility`, `portability` and `value`
  re-weight the criteria. `recommendation.weights` in the config overrides individual criteria.
- **Redundancy:** candidates that duplicate an owned lens are penalised. The redundancy is the
  focal overlap discounted by `exp(−1.6 × stops faster)`, so a 50/1.2 is only partly redundant
  with a 50/1.8.
- **Uncertainty:** scores come with bootstrap uncertainty (500 resamples, fixed seed, so
  results are deterministic). The report also shows which lens wins under each preset, plus
  strengths, trade-offs and assumptions in plain language.

## Web UI and API

```sh
photo-clippy serve ~/Pictures --urls http://0.0.0.0:8080
```

Open `http://<host>:8080`. The UI is Blazor Server, so it works without any client-side build
step, updates live during scans, and doesn't send anything outside your network.

| page | what it does |
|---|---|
| **Overview** `/` | Library summary, focal/aperture/ISO/subject distributions, insights; scan status with *Scan now*, the next scheduled scan and what the last scan did (who started it, how long it took, how many files were read versus served from the cache) |
| **Report** `/reports` | The latest full HTML report embedded, plus JSON/CSV downloads |
| **Lenses** `/lenses` | Type candidates (`24/1.4`, `24-70/2.8 900g $2100`, quick-add chips) or browse the lens database by maker, mount and search; pick body/preset/limits, rank without rescanning; *Save as defaults* |
| **Platform** `/platform` | Live process telemetry (CPU against the container's core limit, memory against its memory limit, managed heap, allocation rate, GC pauses, storage I/O, thread pool, HTTP rate and p95 latency, scan throughput) over the last 15 minutes; the running scan stage by stage with cache hits, rate and ETA; scan history (kept across restarts in `<home>/state/scan-jobs.json`) with per-scan CPU, peak memory, allocations and disk reads; storage breakdown and free space; runtime and host details; and a filterable log tail. Admins only |
| **Settings** `/settings` | Library folders (with a folder picker), analysis, components (enable/disable plugins, genre weights, health), recommendation defaults, report, schedule, model download. Admins only |
| **Account** `/access` | Your session and password; admins also manage accounts here |

Folders given to `serve` only seed the library on first start. After that, the folders saved in
Settings are used. The folder picker can only open folders under `serve.browseRoots`. If that's
not set, it can open the library roots and your home folder. It never follows symbolic links.
Any flag passed to `serve` (`-c`, `--rescan-hours`, `--title`, …) locks that setting in the UI
for the process lifetime.

| endpoint | |
|---|---|
| `GET /report/<file>` | Latest rendered report files (HTML, JSON, CSV) |
| `GET /healthz` | `{"status":"ok","version":…,"scan":…}` (no sign-in needed) |
| `GET /api/v1/scan` | Current or last scan state, including `trigger` (`startup`, `web`, `api`, `schedule`), `jobId` and `cached` |
| `POST /api/v1/scan` | Start a scan (`202`; `409` if one is running) |
| `GET /api/v1/events` | Server-sent events: `scan.state`, `scan.started`, `scan.progress`, `scan.completed`, `scan.failed`, `scan.cancelled`, `recommendation.updated`, `config.applied`, `lenses.updated` |
| `GET /api/v1/snapshot` | Latest analysis snapshot (profile, insights, ranking) as JSON |
| `POST /api/v1/recommendations` | Re-rank without rescanning; see the example after this table. Re-renders the report unless `"render": false` |
| `GET /api/v1/plugins?health=true` | Plugin inventory |
| `GET /api/v1/lenses?q=&maker=&mount=&body=&limit=` | Search the lens database; `body` resolves a camera to its mount. Each lens includes its candidate `shorthand` |
| `GET /api/v1/platform?samples=&log=` | Everything on the Platform page: host and limits, the latest sample and 15-minute history, HTTP and session totals, scan state, scan history, storage, the last run's timings and plugins, and the recent log. Pass `samples=false` or `log=false` for a smaller payload. Admins only |
| `POST /api/v1/lenses/update` | Download or refresh the lens database (`202`; `409` if one is running; ends with a `lenses.updated` event) |

`POST /api/v1/recommendations` accepts this body:

```json
{"candidates":["35/1.2","50/1.2 1090g"],"preset":"low-light","body":null,"budgetUsd":null,"maxWeightG":null,"top":8,"weights":{"fit":0.4}}
```

Errors return `{"error","hint"}` with `400`, `404`, `409` or `422`. Everything except `/healthz`
needs a signed-in session: pages and `/report/*` redirect to `/login`, and `/api/v1/*` returns
`401`. UI session keys are kept in `<home>/keys`.

## Sign-in

Sign-in is pluggable like every other component. `auth.providers` lists auth plugins, which are
tried in order. A plugin that doesn't know a user passes to the next one, and a plugin that rejects
the password stops the chain. The host owns sessions, the access policy, throttling and the UI.
Plugins only answer "who is this?"

| plugin | |
|---|---|
| `builtin.auth.local` (default) | Accounts in `<home>/state/builtin.auth.local/users.json` (mode `600`, PBKDF2-SHA256 at 600k iterations). |
| `builtin.auth.none` | Turns sign-in off. Everyone who can reach the server is an admin. |

There are two roles. **Viewers** can use the Overview, Report and Lenses pages, start scans and
re-rank. **Admins** can also open Settings, download models and manage accounts.

**First start.** With no accounts, the server logs a one-time setup code:

```sh
docker logs photo-clippy 2>&1 | grep 'setup code'
```

Open the UI, then enter the code and the first admin's name and password. Alternatively, create
the admin from the command line. This also works while the server runs, and changes apply
within a minute:

```sh
docker exec -it photo-clippy photo-clippy users add alice --admin            # prompts for the password
echo "$PW" | docker exec -i photo-clippy photo-clippy users add anna --password-stdin
docker exec photo-clippy photo-clippy users list
docker exec -it photo-clippy photo-clippy users passwd anna                   # signs anna out everywhere
docker exec photo-clippy photo-clippy users admin anna [--revoke]
docker exec photo-clippy photo-clippy users remove anna
```

The same actions are available to admins on the **Account** page. You can't remove or demote the
last admin. If you edit accounts from the CLI and the UI at the same moment, the last write wins.

**Policy (config file or environment only, read at start-up):**

| setting | |
|---|---|
| `auth.allowedUsers` | If set, only these names may sign in, whatever the plugin says. Useful with a shared directory such as Proxmox or LDAP. |
| `auth.admins` | Names that always get the admin role. |
| `auth.sessionHours` | Sliding session length (default 12). Sessions also end when the password changes or the account is removed. |
| `auth.trustedProxies` | Proxies whose `X-Forwarded-For`/`-Proto` headers are trusted, for client IPs in the throttle and log. The default is loopback and private ranges, which covers a reverse proxy on the LAN. |

```yaml
environment:
  - PHOTO_CLIPPY_AUTH__ADMINS__0=alice
  - PHOTO_CLIPPY_AUTH__SESSIONHOURS=24
  # - PHOTO_CLIPPY_AUTH__PROVIDERS__0=builtin.auth.none    # sign-in off (trusted network only)
```

**Hardening.** Failed sign-ins are throttled: 5 per user name and 20 per address in 15 minutes.
Forms use antiforgery tokens, and cookies are `HttpOnly`, `SameSite=Lax` and `Secure` over HTTPS.
Serve it through a TLS-terminating reverse proxy so passwords never cross the network in clear text. Scripts
calling `/api/v1` need the session cookie, which you get by posting the sign-in form.

**More providers.** Another identity source is just another plugin, loaded like any other
(in-process assembly or listed by id), so the host and the UI stay unchanged:

- **Proxmox or LDAP:** `ICredentialAuthProvider`, for example checking the password against the
  Proxmox `pve` realm. Combine it with `auth.allowedUsers` to admit only some users.
- **OIDC (Authentik, Keycloak, Entra):** `IRedirectAuthProvider`. The sign-in page shows a button
  for it.
- **Traefik forward-auth or Authelia:** `IRequestAuthProvider`. It reads identity headers and
  should trust them only when `IncomingRequest.PeerAddress` is the proxy. The sign-in page is
  skipped.
- **Second factor:** any credential plugin can return `AuthResult.NeedSecondFactor`, and the host
  shows the code prompt.

List several providers to migrate gradually, for example
`["builtin.auth.local", "my.auth.proxmox"]`. Local accounts keep working while users move.

## Configuration

Settings resolve in this order: command-line options, then environment variables, then the
JSON config file, then defaults. The config file is the first that exists of `--config`, `$PHOTO_CLIPPY_CONFIG`,
`./photo-clippy.json` and `<home>/photo-clippy.json`, where `<home>` is `--home` if given.
`photo-clippy config init` writes a starter file, and `config show` prints the effective result.

```jsonc
{
  "library":   { "roots": ["/photos"], "ignore": ["**/exports/**"], "followSymlinks": false },
  "analysis":  { "scope": "interchangeable", "frames": true, "cpuConcurrency": 0, "highIsoThreshold": 3200,
                 "recencyHalfLifeYears": 0, "minGenreConfidence": 0.15 },
  "plugins":   { "metadata": ["builtin.metadata.native"],
                 "composition": ["builtin.composition.clip", "builtin.composition.heuristic"],
                 "color": ["builtin.color"], "recommender": "builtin.recommend.mcda",
                 "lensCatalog": "builtin.lenses.lensfun",   // "" turns the lens database off
                 "reports": ["builtin.report.html", "builtin.report.json", "builtin.report.csv"],
                 // fusion weight of each composition analyzer's genre vote
                 "genreWeights": { "builtin.composition.clip": 1.0, "builtin.composition.heuristic": 0.25 },
                 // per-plugin settings, handed to the plugin as-is
                 "settings": { "builtin.metadata.exiftool": { "executablePath": "exiftool" } },
                 "external": [] },
  "recommendation": { "candidates": ["24/1.4, 35/1.2"], "preset": "balanced", "topN": 8, "prompt": true },
  "report":    { "output": "photo-report", "title": "Photo habits" },
  "paths":     { "home": null, "models": null },
  "serve":     { "urls": "http://0.0.0.0:8080", "rescanHours": 0, "runOnStart": true,
                 // folders the web UI's folder picker may open (default: library roots + user home)
                 "browseRoots": ["/photos"] },
  // web UI sign-in; read at start-up only (see "Sign-in")
  "auth":      { "providers": ["builtin.auth.local"], "allowedUsers": [], "admins": [],
                 "sessionHours": 12, "trustedProxies": ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"] }
}
```

- **Web UI:** the daemon's Settings page edits this file (`<home>/photo-clippy.json` if none
  exists yet). It writes atomically, keeping the previous version as `.bak`, and applies changes
  without a restart. If a scan is running, the changes apply when it finishes. Values set by
  environment variables or command-line flags win over the file, so the UI shows them locked.
  For safety the UI never writes these fields: `paths`, `auth`, `plugins.directory`, `plugins.external`,
  `plugins.settings`, `serve.urls`, `serve.browseRoots` and `report.output`/`baseName`. Set
  them in the file or the environment.
- **Environment variables:** every key maps to `PHOTO_CLIPPY_<SECTION>__<KEY>`. Numeric
  segments index lists, e.g. `PHOTO_CLIPPY_PLUGINS__METADATA__1=builtin.metadata.exiftool`
  or `PHOTO_CLIPPY_RECOMMENDATION__CANDIDATES__0="24/1.4, 35/1.2"`.
- **Data home:** set by `--home` or `PHOTO_CLIPPY_HOME`. It holds `cache.db`, `models/`,
  `plugins/`, `state/` and, for the daemon, `report/`. Defaults:

  | OS | default data home |
  |---|---|
  | macOS | `~/Library/Application Support/photo-clippy` |
  | Linux | `~/.local/share/photo-clippy` |
  | Windows | `%LOCALAPPDATA%\photo-clippy` |
- **ExifTool:** if `exiftool` isn't on `PATH`, set
  `plugins.settings."builtin.metadata.exiftool".executablePath`. It accepts a full path or the
  Perl script.

## Plugins

Every component is an interface in `PhotoClippy.Contracts`. All plugins derive from `IPlugin`
(`Manifest` and `CheckHealthAsync`) and must be thread-safe.

| component | contract |
|---|---|
| Metadata | `IMetadataExtractor` — `CanHandle(file)`, `ExtractAsync(batch)` (one result per input, in order) |
| Composition | `ICompositionAnalyzer` — `Requirements`, `AnalyzeAsync(frame, context)` → `CompositionFeatures` |
| Colour | `IColorAnalyzer` — `Requirements`, `AnalyzeAsync(frame, context)` → `ColorFeatures` |
| Recommendation | `IRecommender` — `RecommendAsync(RecommendationRequest)` → `RecommendationSet` |
| Report | `IReportRenderer` — `Format`, `RenderAsync(ReportModel, RenderTarget)` |
| Lens database | `ILensCatalog` — `GetInfoAsync`, `SearchAsync(LensCatalogQuery)` → `CatalogLens` list, `RefreshAsync`, `MountForBodyAsync(maker, model)` |
| Auth | `IAuthProvider` plus one sign-in style: `ICredentialAuthProvider`, `IRedirectAuthProvider` or `IRequestAuthProvider`; optionally `IUserDirectory`. See [Sign-in](#sign-in) |

There are three ways to provide one:

1. **Built-in.** The plugins listed above.
2. **In-process .NET.** Drop an assembly into `<home>/plugins/<name>/` (or `plugins.directory`)
   that exposes a public `IPluginFactory`. Each folder loads in its own `AssemblyLoadContext`, and
   the contracts are shared with the host. Plugins that need model files implement
   `IRequiresModels`, so `models fetch` can download them.
3. **Out-of-process, any language.** JSON-RPC 2.0 over stdio or HTTP, registered under
   `plugins.external`. Frames go over the wire as PPM; analyzer `extras` show up as extra
   distributions in the report. See [examples/README.md](examples/README.md) and
   [examples/PROTOCOL.md](examples/PROTOCOL.md). These run in Python with no dependencies:
   - a composition analyzer
   - a Markdown report renderer

Selection is by id in `plugins.metadata`, `composition`, `color`, `recommender`, `reports`,
`lensCatalog` and `auth.providers`.
Several composition analyzers are fused with configurable weights, and several metadata
extractors are merged in priority order. Use `photo-clippy plugins` to see what is loaded, what
is in use and whether it's healthy.

## Self-hosting with Docker

[deploy/Dockerfile](deploy/Dockerfile) builds a multi-arch (amd64 and arm64) image. It is based
on `aspnet:10.0`, runs as the non-root `app` user (uid 1654) and includes ExifTool. Inside the
container:

| path | purpose |
|---|---|
| `/photos` | your library, mounted read-only; the UI's folder picker is confined to it |
| `/data` | config written by the UI, analysis cache, models, reports, accounts and session keys |
| port `8080` | web UI and API |

[deploy/docker-compose.yaml](deploy/docker-compose.yaml) is configured with a `.env` file next to
it. Copy [deploy/.env.example](deploy/.env.example) and set at least `CLIPPY_LIBRARY`:

| variable | default | |
|---|---|---|
| `CLIPPY_LIBRARY` | *(required)* | host folder with your photos, mounted at `/photos:ro` |
| `CLIPPY_DATA` | `photo-clippy-data` volume | named volume or host folder for `/data` |
| `PUID` / `PGID` | `1654` | user the container runs as; set them to the owner of `CLIPPY_DATA` if it is a host folder |
| `CLIPPY_PORT` | `8080` | host port |
| `TZ` | `UTC` | time zone for logs and schedules |
| `CLIPPY_IMAGE` | `photo-clippy:latest` | image to run; set it to a registry image to skip the local build |
| `CLIPPY_PULL_POLICY` | `missing` | `always` to pick up a re-pushed `latest` |

If `CLIPPY_DATA` is a host folder, it must be writable by `PUID:PGID`. Otherwise the container
stops with `Access to the path '/data/...' is denied`:

```sh
sudo mkdir -p /srv/photo-clippy && sudo chown 1000:1000 /srv/photo-clippy   # matching PUID=1000 PGID=1000
```

Any `PHOTO_CLIPPY_*` environment variable added to the compose file overrides the config file and
shows as locked in the UI (see [Configuration](#configuration)). To update, pull or rebuild and
run `docker compose up -d` again. State in `/data` survives.

### Behind a reverse proxy

The UI is Blazor Server and uses a WebSocket on `/_blazor`, so the proxy must pass `Upgrade`
headers. Traefik, Caddy and nginx with the usual WebSocket settings all work.
[deploy/docker-compose.traefik.yaml](deploy/docker-compose.traefik.yaml) adds Traefik labels and
joins an existing external network:

```sh
# .env
COMPOSE_FILE=docker-compose.yaml:docker-compose.traefik.yaml
CLIPPY_HOST=photos.example.lan
TRAEFIK_NETWORK=traefik          # the external network Traefik watches
TRAEFIK_ENTRYPOINT=websecure
```

The default `auth.trustedProxies` (loopback and private ranges) lets the app see client addresses
and the original scheme behind the proxy.

### Publishing to your own registry

To build on one machine and run on another, for example a laptop and a home server,
[deploy/publish-image.sh](deploy/publish-image.sh) builds the image from the repository root,
smoke-tests it and pushes it. It runs on Linux and macOS.

```sh
deploy/publish-image.sh -r registry.example.lan        # build linux/amd64, smoke test, push 3 tags
export PHOTO_CLIPPY_REGISTRY=registry.example.lan      # or set it once
deploy/publish-image.sh --dry-run                      # print the commands only
deploy/publish-image.sh --no-push                      # build and smoke test locally as photo-clippy:<tag>
deploy/publish-image.sh -p linux/arm64                 # for an ARM server
```

| tag | meaning |
| :--- | :--- |
| `0.1.0` | `<Version>` from `Directory.Build.props` |
| `sha-<rev>` | git revision, suffixed `-dirty` when the working tree has uncommitted changes |
| `latest` | moving tag, omit with `--no-latest`; add your own with `-t <tag>` |

The default platform is `linux/amd64` whatever the build machine, so an Apple-silicon Mac still
produces an image an x86-64 server can run. If the registry needs a login, export
`REGISTRY_USERNAME` and `REGISTRY_PASSWORD`. Before building, the script checks that
`https://<registry>/v2/` answers like a registry; `--skip-preflight` turns this off.

**Self-signed certificates.** Docker rejects a registry whose certificate it doesn't trust, for
example Traefik's default certificate, with an `x509` error. Do one of these on the pushing
machine and on every host that pulls:

- Trust your CA by putting it at `~/.docker/certs.d/<registry>/ca.crt`, or at
  `/etc/docker/certs.d/<registry>/ca.crt` on Linux. This needs a certificate issued for the
  registry's host name.
- Add `"<registry>"` to `insecure-registries` in the Docker daemon settings (Docker Desktop:
  Settings → Docker Engine; Linux: `/etc/docker/daemon.json`), then restart Docker.

Then run the pushed image on the server:

```sh
# .env on the server, next to docker-compose.yaml
CLIPPY_IMAGE=registry.example.lan/photo-clippy:latest
CLIPPY_PULL_POLICY=always
```

```sh
docker compose up -d       # pulls the image; the source tree isn't needed on the server
```

## Development

```sh
dotnet build PhotoClippy.slnx
dotnet test PhotoClippy.slnx --no-build --maxcpucount:1 -- xUnit.ParallelizeTestCollections=false
dotnet run --project tools/PhotoClippy.SampleGen -- /tmp/samplelib --count 600 --seed 42
dotnet run --project src/PhotoClippy.Cli -- run /tmp/samplelib -c "24/1.4, 35/1.2" -o /tmp/photo-report
```

- **Tests:** the full suite includes timing-sensitive benchmarks. The command above runs projects
  and test collections serially to avoid competing workloads invalidating their thresholds.
- **SampleGen:** writes a reproducible synthetic library. It has JPEGs with realistic EXIF habits
  across several bodies and lenses, plus a few RAW+JPEG pairs, phone shots, screenshots and one
  corrupt file, which makes it useful for demos and benchmarks.
- **ExifTool tests:** the metadata tests that exercise the ExifTool extractor run when `exiftool`
  is on `PATH` or `PHOTO_CLIPPY_EXIFTOOL` points to it.

Performance on an Apple-silicon laptop, 658 synthetic files (≈ 512 captures):

| run | time |
|---|---|
| Cold, with CLIP | ≈ 12 s |
| Warm rescan (cache) | ≈ 0.6 s |

Container throughput depends on the host CPU, model and mounted storage; benchmark it on the
deployment host.

## Project layout

```
PhotoClippy.slnx, global.json, Directory.*.props   solution and shared build settings
src/                                   product source only
  PhotoClippy.Contracts/               interfaces + records (the plugin API)
  PhotoClippy.Core/                    discovery, pipeline, cache, profiling, insights, config, plugin host
  PhotoClippy.Imaging/                 decode (JPEG/HEIF/TIFF/PNG + embedded RAW previews) via SkiaSharp
  PhotoClippy.Plugins.Metadata/        native (MetadataExtractor) and ExifTool extractors
  PhotoClippy.Plugins.Composition/     heuristic composition + scene analyzer
  PhotoClippy.Plugins.Clip/            CLIP ViT-B/32 (ONNX) subject/genre classifier
  PhotoClippy.Plugins.Color/           colour and tonal analytics
  PhotoClippy.Plugins.Recommendation/  MCDA lens ranking
  PhotoClippy.Plugins.Reporting/       HTML, JSON and CSV renderers
  PhotoClippy.Plugins.Lenses/          Lensfun lens database (download, search, body-to-mount)
  PhotoClippy.Plugins.External/        bridge for out-of-process plugins
  PhotoClippy.Cli/                     `photo-clippy` CLI, `serve` daemon and its Blazor web UI
data/taxonomy/genres.json              subject taxonomy (20 genres, focal/aperture preferences)
deploy/                                Dockerfile, compose files, .env.example, publish-image.sh
docs/images/                           README screenshots
examples/                              external plugin examples + wire protocol
tests/                                 xUnit test projects
tools/PhotoClippy.SampleGen/           synthetic EXIF library generator
```

## Limitations and roadmap

- **RAW files with no usable preview:** the native extractor can't read CRW, X3F or MRW metadata.
  Enable the ExifTool extractor for those.
- **Macro magnification:** this isn't assessed, because EXIF has no reliable reproduction ratio.
- **Next feature: local LLM.** The planned next step is a local-LLM recommender or narrator plugin,
  for example Ollama over the external HTTP transport, that receives the profile and ranking JSON.
  The contracts and `/api/v1/snapshot` already expose everything it needs.

## About the mascot

The paperclip is original artwork with aperture-blade pupils, a nod to the helpful office assistant
many of us remember. The master is [clippy.svg](src/PhotoClippy.Cli/wwwroot/clippy.svg), with PNG,
ICO and a simplified small-size SVG next to it. Photo Clippy is an independent project and isn't
affiliated with or endorsed by Microsoft.
