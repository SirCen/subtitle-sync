# Local Jellyfin harness

A throwaway Jellyfin 10.11 server with a seeded one-movie library, so the plugin
can be smoke tested inside a real web client. Issue #19.

The plugin's riskiest behaviour - the Subtitles-menu injection (#13) and the
Dashboard config page - only exists in a running client. Unit tests cannot see
it. This is what can.

## One command

```bash
npm run jf:e2e
```

That seeds the library, starts the container, completes the first-run wizard,
creates the users and the library, installs the File Transformation plugin,
waits for the scan, then runs the Playwright smoke tests. It is idempotent, so
re-running it against a live server just re-checks everything.

To bring the server up without running tests:

```bash
npm run jf:up      # http://127.0.0.1:8096/web/  (harness / harness-password)
npm run jf:logs    # follow the server log
npm run jf:down    # stop, keep state
npm run jf:down -- --purge   # stop and wipe volumes + seeded media

npm run jf:ft            # install the File Transformation plugin (idempotent)
npm run jf:ft:uninstall  # remove it, to check what happens without it
npm run jf:ft:status     # what the running server actually has loaded
```

### Prerequisites

- **Docker** with Compose v2 (`docker compose version`). Docker Desktop on
  Windows is what this was developed and verified against.
- Node 20+ and `npm install` already run.
- Playwright's browser, once: `npx playwright install chromium`.

## What it gives you

| | |
| --- | --- |
| Image | `jellyfin/jellyfin:10.11.11` |
| URL | `http://127.0.0.1:8096` (`JELLYFIN_PORT` to change) |
| Admin | `harness` / `harness-password` |
| Non-admin | `viewer` / `viewer-password`, holds `EnableSubtitleManagement` |
| Library | "Harness Movies" -> `/media/movies` |
| Item | "Sample Clip" (2020), with an external English `.srt` track |
| Item | "Structured Clip", ditto, but its track is displaced by a **known -3.2 s** |

Everything above is defined once in `harness.config.json` and overridable by env
var; see the `_env` block in that file.

#### Why the non-admin has subtitle rights

`viewer` is given `EnableSubtitleManagement` deliberately, and it is what makes
the #13 non-admin test worth running. Without it the client shows them no "Edit
subtitles" entry either, so "our menu item is absent" would be true whether or
not our script checked anything. With it, the client renders the exact menu our
script attaches to and the item's absence proves the `IsAdministrator` gate is
doing the work.

They are still not an administrator, which is the point: the 10.11 client puts
plugin configuration pages behind an admin route guard, so this user could not
open the sync page even though the server would let them analyse. See #12.

### Why the tag is pinned to 10.11.11

`jellyfin/jellyfin:latest` currently points at the **12.0 pre-release line**,
which is not what this plugin targets. `10.11.11` is the newest release on the
10.11 line on Docker Hub. Do not loosen this to `latest` or `10`.

### Where the media comes from

`scripts/seed-library.mjs` copies the already-committed fixtures into the layout
Jellyfin recognises as a movie with an external subtitle:

```
media/movies/Sample Clip (2020)/Sample Clip (2020).mp4          <- test/fixtures/sample.mp4
media/movies/Sample Clip (2020)/Sample Clip (2020).en.srt       <- test/fixtures/sample.srt
media/movies/Structured Clip/Structured Clip.mp4                <- test/fixtures/structured.mp4
media/movies/Structured Clip/Structured Clip.en.srt             <- test/fixtures/structured.offset.srt
```

Nothing is downloaded. The two clips are ~2 MB and ~150 KB, and their provenance
is documented in `test/fixtures/PROVENANCE.md`. `media/` is gitignored so the
fixtures are never committed twice.

#### Two movies, because they answer different questions

**Sample Clip** is real footage with real dialogue. It proves the plumbing works
on something that is not synthetic. It cannot prove a sync is *correct*: the VAD
flags ~92% of its audio as speech, so there is no speech/silence structure to
correlate against and `analyze` misses by about 8 s on it.

**Structured Clip** is synthesised so that it can. Its audio alternates speech
and silence on a known irregular schedule, and its seeded `.en.srt` is the
correct track displaced by exactly **-3.2 s**. So:

> **Any test that asserts a sync produced the right answer must use Structured
> Clip.** Asserting an offset against Sample Clip passes vacuously or asserts a
> wrong number.

The offset is in `harness.config.json` as `syncableKnownOffset`, exposed to the
Node scripts as `SYNCABLE_KNOWN_OFFSET` and to the Playwright specs as the
same name from `e2e/harness.ts`, alongside `findSyncableItemId(session)`.
`test/structured.test.ts` asserts the same recovery at the unit level, so if the
e2e test disagrees with it the fault is in the plugin, not the algorithm.

A ratio (framerate-drift) input, `test/fixtures/structured.ratio.srt`, is
committed too and covered by `test/structured.test.ts`. It is deliberately *not*
seeded: a second subtitle track would make the track index the e2e test picks
non-obvious for no extra plugin coverage. Seed it if you want to exercise ratio
correction through the UI.

Regenerate both synthesised files with
`node test/oracle/gen_structured_fixture.mjs` (needs ffmpeg on PATH).

#### Why the second folder has no year

`Sample Clip (2020)` resolves to an item named "Sample Clip" - Jellyfin strips
the year. The same folder shape with a different name did **not**: it resolved
to "Structured Clip (2021)", year included. That reproduced on a purged server,
on both the initial scan and a later refresh, with either fixture's media and
with several years, so it is not the media, the year or the scan type. Rather
than depend on which way the resolver goes, the folder is simply
`Structured Clip` - with no year there is nothing to strip, and the item name is
the folder name.

The library is created with **metadata fetchers disabled**. Left on, Jellyfin
would query TMDB and rename the clip to whatever it matched, which would make
the Playwright lookup non-deterministic and tie the harness to a third party
being up. Off, the item name comes straight from the file name.

### How the setup wizard is bypassed

There is no clicking through setup. `scripts/jellyfin-api.mjs` drives the
wizard over REST:

```
POST /Startup/Configuration   server name, locale, metadata language
GET  /Startup/User            seeds the first-user slot
POST /Startup/User            creates the admin
POST /Startup/RemoteAccess
POST /Startup/Complete
```

Those endpoints sit behind Jellyfin's `FirstTimeSetupOrElevated` policy, which
means they are callable **without a token for exactly as long as the wizard is
incomplete**. That window is what the script uses. After `/Startup/Complete`
they lock down to admins, and the script no-ops on subsequent runs.

Routes were verified against the `v10.11.11` tag of `jellyfin/jellyfin`
(`Jellyfin.Api/Controllers/StartupController.cs`), not assumed from older docs.

## Dropping in a plugin build

Rather than mounting the build output directly, the compose file mounts a
**committed staging directory** you copy into:

```
./plugins/SubtitleSync  ->  /config/plugins/SubtitleSync
```

It is committed with a `.gitkeep` on purpose. A bind mount of a path that does
not exist on the host would be created root-owned by the daemon on first `up`,
which is a confusing failure; an existing empty directory is harmless - Jellyfin
finds no assemblies and carries on, so the non-plugin smoke tests still pass
against a server with nothing staged.

The loop is:

```bash
dotnet publish -c Release                       # from the plugin project
cp <publish-output>/*.dll jellyfin-plugin/docker/plugins/SubtitleSync/
docker compose -f jellyfin-plugin/docker/docker-compose.yml restart jellyfin
```

Jellyfin only scans plugins at startup, so the restart is required. Confirm with
`npm run jf:logs` or by checking `GET /Plugins`.

> **Restaging can silently keep the old build.** On first start Jellyfin
> *migrates* loose DLLs out of `/config/plugins/SubtitleSync/` into a versioned
> directory, `/config/plugins/Subtitle Sync_1.0.0.0/`, and from then on it loads
> from there. Copying a new DLL into the bind mount therefore has no effect: the
> server keeps running the migrated copy, the log still says the plugin loaded,
> and you debug a build that is not the one you just made. Delete the versioned
> directory before restarting:
>
> ```bash
> docker exec subtitle-sync-jellyfin sh -c 'rm -rf "/config/plugins/Subtitle Sync_"*'
> ```
>
> If the version number has not changed, checking the log line is not enough to
> tell the two builds apart. `window.SubtitleSync.BUILD` in the browser carries
> an ISO build stamp, which is the reliable way to confirm the bundle is fresh.

## The File Transformation plugin

The Subtitles-menu item (#13) is injected into the web client by
[File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation),
a third-party plugin. Jellyfin 10.11 has no dependency mechanism that could pull
it in, so the harness installs it itself:

```bash
npm run jf:ft            # download, verify, stage
docker compose -f jellyfin-plugin/docker/docker-compose.yml restart jellyfin
npm run jf:ft:status     # File Transformation 2.5.11.0 (Active)
```

`npm run jf:up` does all of that, including the restart, so a purged server comes
back with it installed without anyone clicking through the Dashboard. Set
`JELLYFIN_SKIP_FILE_TRANSFORMATION=1` (or pass `--no-file-transformation`) to
bring the harness up without it.

### Why not install it through Jellyfin

Jellyfin can do this itself - add the repository, `POST
/Packages/Installed/{name}` - and that is the route a real user takes. It is the
wrong route for a harness. The manifest publishes **six entries all numbered
`2.5.11.0`**, one per Jellyfin patch release, distinguished only by `targetAbi`;
asking for "version 2.5.11.0" does not say which one you get. It also needs
`iamparadox.dev` reachable from inside the container at the moment the test runs.

`scripts/file-transformation.mjs` instead pins the asset for
`targetAbi 10.11.11.0` - the tag `docker-compose.yml` runs - checks the MD5 the
manifest publishes, and unpacks it into `plugins/FileTransformation/`, which is
bind-mounted into the container. One version, one checksum, reproducible.

### Two traps, both found the hard way

**The release zip has no `meta.json`.** Jellyfin's own installer synthesises one
from the repository manifest, so a zip unpacked by hand arrives without it - and
a plugin folder with no manifest gets an *invented* identity: a guid derived from
the folder name, the name "FileTransformation", the server's version number, and
eventually `"status": "Deleted"` and a `Skipping disabled plugin` line in the
log. The install script writes a real `meta.json`, which is what makes the folder
an install rather than a pile of DLLs.

**Uninstalling means deleting two directories.** Once Jellyfin has migrated a
plugin into `/config/plugins/File Transformation_2.5.11.0/` it loads from there,
so emptying the bind mount removes nothing. Worse, staging a fresh copy while the
migrated one is still present gives the server **two** File Transformation
assemblies in two load contexts: it loads both, the second fails to construct
with an `InvalidCastException` between two identically named types, and every
request for `/web/` then returns 500. `npm run jf:ft:uninstall` clears both.
Note the quoting - the path has a space in it, and an unquoted glob in `sh -c`
silently deletes nothing.

### Checking the absent case

The plugin has to work with File Transformation gone, and that is a real state
worth visiting rather than only mocking:

```bash
npm run jf:ft:uninstall
docker compose -f jellyfin-plugin/docker/docker-compose.yml restart jellyfin
```

Expect: Subtitle Sync still loads, `GET /SubtitleSync/Status` answers
`NotInstalled`, the configuration page shows the install note with the
repository URL, the Dashboard route to the sync page still works, and
`/web/index.html` no longer carries the injected `<script data-subtitle-sync>`.
Then `npm run jf:ft` and restart to put it back.

## The smoke tests

Specs live in `jellyfin-plugin/e2e/`. Run with `npm run jf:e2e`.

**Passing today** (`server.spec.ts`) - the harness proving itself:

- server is up and running the pinned 10.11 line
- admin can log in through the web client
- fixture item detail page is reachable
- fixture item has the external SRT as a subtitle track
- Dashboard > Plugins loads for an admin

**Also passing** (`plugin.spec.ts`) - the plugin itself, needing the DLL staged
and the container restarted:

- plugin appears under Dashboard > Plugins, and its config page renders (#3)
- the save endpoint's behaviour: siblings, collisions, concurrency, permissions
  and refusals (#8)
- the sync page (#12): both entry paths, a full sync run over **Structured
  Clip** asserting it recovers `syncableKnownOffset` exactly, download, a nudge
  that talks to nobody, a refused save, and the client's admin-only route guard

- the Subtitles-menu injection (#13): the item appears for an admin, directly
  beneath the client's own "Edit subtitles"; it opens the sync page for the
  right item; it does **not** appear for a non-admin who *does* have subtitle
  rights; it survives client-side navigation between two detail pages and
  carries the new item's id; and the configuration page shows the install note
  when the status endpoint reports File Transformation missing, and stays quiet
  when it is working

**Nothing is skipped.** Every issue these were written ahead of has landed.

Any test that asserts a sync is *correct* runs against **Structured Clip**, not
Sample Clip - see "Two movies" above.

### What the injection tests learned from a live client

The selectors these were first written with were all wrong, which is exactly
what the harness is for:

- **There is no "Subtitles" button on a detail page.** The entry lives inside
  the `...` overflow menu: button `.btnMoreCommands` (title "More"), menu
  `.actionSheet`, items `button[data-id]` - ours is `subtitlesync-sync`, the
  client's is `editsubtitles`.
- **The client caches views.** After a client-side navigation between two detail
  pages, both pages' markup is in the DOM and only one is displayed, so
  `.itemName` and `.btnMoreCommands` need a `visible: true` filter or they
  resolve to the page you just left.
- **The menu will not close on Escape.** Not from `page.keyboard.press`, not
  from a synthetic `keydown` on `document` or on the dialog, and not from a
  programmatic `.click()` on `.dialogBackdrop`. A real pointer event outside the
  dialog works, and so does `history.back()`. The injected script relies on
  neither: routing to the sync page tears the dialog down.
- **`itemContextMenu.js` lives in `55802.9a5b7bc258c2f90abe5e.chunk.js`** on
  10.11.11 - a webpack module id plus a content hash, minified. Settled against
  the running container, and the reason `index.html` is the injection target.

> **The plugin page route is admin-only in the client, whatever the server
> policy says.** 10.11's router puts `configurationpage` inside an
> `admin`-level `ConnectionRequired` guard, so a non-admin who *does* have
> `EnableSubtitleManagement` is redirected to `#/home` before the page loads.
> The server-side split still holds - analysing is `SubtitleManagement`, saving
> is elevated - and the page handles a 403 from Save by pointing at Download,
> but reaching it as a non-admin is something #13 has to solve, not something
> the Dashboard route can.

### Gotchas found the hard way

- **Log in via `/web/`, not `/web/#/login.html`.** The latter is the deprecated
  URL format the 10.11 client warns about. It authenticates successfully but
  routes into a fallback that never leaves the login view, which is
  indistinguishable from a failed login. `harness.ts` enters at `/web/` and
  waits for `#/home`.
- **The dashboard is React/MUI now.** Class names are generated
  (`css-1riowxi`), so assert on user-visible text, not selectors.
- **Polling the server as it boots needs a ref'd timer.** Docker's port proxy
  accepts the TCP connection before Jellyfin answers, and Node 20's `fetch`
  leaves that request without a ref'd handle - the process decides the event
  loop is empty and exits 0 mid-wait, silently. `keepAlive()` in
  `scripts/jellyfin-api.mjs` is what prevents that.
- **`/System/Info/Public` returns 503 with a plain-text body** for the first few
  seconds, so a 2xx alone is not a readiness signal.

## CI: local-only, for now

**Recommendation: keep this local-only. Do not add it to the PR gate.**

It could technically run on `ubuntu-latest` - Docker is present, the image
pulls, and the fixture is committed so there is no media to fetch. But:

- The image is ~600 MB. Pulling it on every PR dominates a workflow that
  currently finishes in a couple of minutes.
- Cold start plus library scan plus the Playwright browser download is a large
  fixed cost for what is, until the plugin lands, five tests that assert
  Jellyfin works.
- The value here is *interactive*: the injection in #13 breaks on client
  updates, and diagnosing that means opening the page, not reading a CI log.
- Adding the File Transformation download to the critical path of every PR ties
  the gate to a third-party host being up.

The honest split: `npx tsc --noEmit`, `npm test` and `dotnet test` (#14) stay on
the PR gate; this stays a local pre-merge check for plugin work. Now that the
#13 tests are live and load-bearing it is a genuine candidate for a **scheduled
or manually-dispatched** workflow - which is the right shape for it anyway,
since what those tests actually watch for is a Jellyfin or File Transformation
release breaking the injection, not a change in this repository.

Nothing here blocks that: `JELLYFIN_SKIP_DOCKER=1` plus `JELLYFIN_URL` already
points the suite at a server started by other means, e.g. a compose service in a
workflow.

`npm test` (Vitest) does not pick these up - its `include` globs only cover
`lib/`, `test/` and `jellyfin-plugin/web/src/`.
