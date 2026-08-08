# Jellyfin plugin

The Jellyfin 10.11 side of Subtitle Sync. Everything here is separate from the
Next.js site at the repo root and builds with the .NET SDK, not npm.

The design is a **thin C# shell hosting a browser page**. The sync algorithm
stays in `lib/` at the repo root and runs in the browser, so `lib/` remains the
single source of truth and the golden parity test keeps covering the code that
actually ships. See epic #18.

## Layout

| Path | What it is |
| --- | --- |
| `Jellyfin.Plugin.SubtitleSync/` | The plugin assembly. Targets `net9.0`. |
| `Jellyfin.Plugin.SubtitleSync.Tests/` | xUnit tests for it. |
| `Jellyfin.Plugin.SubtitleSync.sln` | Solution over both. |
| `docker/` | Local Jellyfin 10.11 server to test against. See `docker/README.md`. |
| `e2e/` | Playwright smoke tests that drive that server. |
| `web/` | Browser-side sources for the plugin page (#10, #12) and the injected menu item (#13). |

Inside the plugin project:

```
Plugin.cs                          BasePlugin<PluginConfiguration>, IHasWebPages
Configuration/PluginConfiguration.cs   settings, serialised to XML by the server
Configuration/configPage.html          embedded resource, served as the config page
Configuration/syncPage.html            embedded resource, served as the sync page
Api/                                   the endpoints the sync page calls
Injection/                             the Subtitles-menu item (#13)
```

### The pages

`GetPages()` registers four resources, all reachable at
`/web/ConfigurationPage?name=<name>`:

| Name | What |
| --- | --- |
| `Subtitle Sync` | The Dashboard settings page. Links to the sync page. |
| `SubtitleSyncPage` | The sync UI. `/web/#/configurationpage?name=SubtitleSyncPage`, optionally `&itemId=<id>`. |
| `subtitleSync.js` | The esbuild bundle of `lib/`, exposed as `window.SubtitleSync`. |
| `subtitleSyncPage.js` | The sync page's own UI code. Reads the bundle above. |

A fifth bundle, `subtitleSyncInject.js`, is built alongside them but is **not**
registered here. It is inlined into the web client's `index.html` instead; see
below.

Both entry paths into the sync page have to work: with an `itemId` (what the
injected Subtitles-menu item of #13 supplies) and without one, where the page
shows a library picker. The picker is the **primary** route, because the
injection depends on a third-party plugin that patches the server at runtime.

Note that the 10.11 web client routes `configurationpage` behind an
admin-level guard, so a non-admin cannot open the sync page from the Dashboard
however their subtitle permission is set. The server-side split is still real -
reading and analysing need `SubtitleManagement`, saving needs elevation - and
the page presents a refused save as a limit with Download still working.

### The Subtitles menu item

Jellyfin 10.11 has no supported extension point for the item detail page, so
this is a controlled hack and is built to fail softly.

```
Injection/FileTransformationFacts.cs      what we know about the third-party plugin
Injection/FileTransformationRegistrar.cs  detect it, register the transformation
Injection/InjectionStartupService.cs      IHostedService that runs it once at startup
Injection/IndexHtmlTransformation.cs      the callback: inline the script into index.html
Injection/InjectionState.cs               the outcome, published on the config page
web/src/inject.ts                         the script itself
```

How it works: at startup we look for the **File Transformation** plugin
(`5e87cc92-571a-4d8d-8d98-d2d4147f9f90`) and ask it to run a callback over
`index.html`. The callback inlines `subtitleSyncInject.js`, which watches for
the client's `...` menu opening and adds a "Sync subtitles..." item beneath the
client's own "Edit subtitles" entry, linking to the sync page with that item's
id.

Four things are worth knowing before changing any of it.

- **File Transformation cannot be depended on.** Jellyfin 10.11 has no plugin
  dependency mechanism at all, so the user has to install it themselves. If it
  is absent, the menu item is absent and everything else works unchanged. The
  Dashboard route is the primary one. The configuration page says so, with the
  repository URL, and `GET /SubtitleSync/Status` is where that comes from.
- **The item is gated on `IsAdministrator`, not `EnableSubtitleManagement`.**
  The 10.11 client puts `configurationpage` behind an admin route guard, so a
  non-admin who clicked it would be bounced to `#/home`. See #12.
- **The injected script must never throw**, and neither must the C# callback.
  File Transformation invokes the callback by reflection and casts the result to
  `string` unconditionally, in the middle of serving `index.html`. An exception
  there is a web client that will not load. It must also never return a
  *shorter* string than it was given: the result is written over the original
  stream without truncating it.
- **Newtonsoft.Json is referenced with `ExcludeAssets=runtime` on purpose.**
  Shipping a copy beside our DLL would give us a different `JObject` type
  identity from the one File Transformation binds to, and the registration call
  would fail. Both of us bind to the server's `/jellyfin/Newtonsoft.Json.dll`.

`index.html` is the target rather than the chunk that actually builds the menu:
on a real 10.11.11 install that chunk is `55802.9a5b7bc258c2f90abe5e.chunk.js`,
a webpack module id plus a content hash, minified, and not marked `no-cache`.

## Building

Needs the .NET SDK. A .NET 10 SDK is fine - it restores the `net9.0` targeting
pack on demand - but the **plugin must stay on `net9.0`**, because that is what
Jellyfin 10.11 runs on (`Jellyfin.Server.csproj` at `v10.11.11`).

```bash
cd jellyfin-plugin
dotnet build -c Release
dotnet test
```

`dotnet test` runs the test assembly on whatever shared runtime is installed:
the test project sets `<RollForward>Major</RollForward>` so a machine with only
the .NET 10 runtime does not need a .NET 9 one installed as well.

To produce something loadable:

```bash
dotnet publish Jellyfin.Plugin.SubtitleSync/Jellyfin.Plugin.SubtitleSync.csproj \
  -c Release -o out
```

`out/` contains `Jellyfin.Plugin.SubtitleSync.dll` and `Newtonsoft.Json.dll` and
nothing else. The Jellyfin packages carry `ExcludeAssets=runtime`, so the
server's own assemblies are deliberately not copied: the server already has them
loaded, and shipping duplicates is how you get type-identity failures at load
time.

## Testing against a real server

`docker/` is a throwaway Jellyfin 10.11.11 with a seeded one-movie library
(issue #19). Full details in `docker/README.md`. The loop:

```bash
npm run jf:up                      # from the repo root, if it is not already up

cd jellyfin-plugin
dotnet publish Jellyfin.Plugin.SubtitleSync/Jellyfin.Plugin.SubtitleSync.csproj -c Release -o out
cp out/*.dll docker/plugins/SubtitleSync/
docker compose -f docker/docker-compose.yml restart jellyfin

cd .. && npm run jf:e2e            # Playwright smoke tests
```

Jellyfin only scans for plugins at startup, so the restart is not optional.
Confirm it took with:

```bash
docker logs subtitle-sync-jellyfin 2>&1 | grep "Loaded plugin: Subtitle Sync"
```

Then open <http://127.0.0.1:8096/web/#/dashboard/plugins> (`harness` /
`harness-password`).

`docker/plugins/SubtitleSync/` is a committed staging directory whose contents
are gitignored, so the copied DLLs never end up in a commit.

### No `meta.json` is needed for a manual drop

Jellyfin's `PluginManager` loads a plugin directory containing bare assemblies
and takes the name, version and description from `BasePlugin` itself - verified
on the harness, which lists "Subtitle Sync 1.0.0.0 Active" with no `meta.json`
present. The in-zip `meta.json` matters for the packaged release only, which is
issue #15.

## Configuration

`PluginConfiguration` is serialised to XML into the server's
`PluginConfigurationsPath`. Because an existing config file written by an older
build is simply missing any newly added element, **every property needs a public
setter and a sensible default**.

| Setting | Default | Notes |
| --- | --- | --- |
| `OverwriteOriginal` | `false` | Destructive with no undo, so opt-in. Off means a sibling `<base>.<lang>.synced.srt`. |
| `EnableSignalCache` | `true` | Placeholder for #9. Nothing reads it yet. |
| `SignalCacheSizeLimitMb` | `512` | Placeholder for #9. Zero means unbounded. |

Persistent data must **not** go in `BasePlugin.DataFolderPath`: that is the
install directory and is wiped on plugin update. The signal cache belongs under
`Path.Join(IApplicationPaths.DataPath, "subtitlesync")`. See section 10 of
`research/jellyfin-10.11-plugin-api.md`.

## Things worth knowing before changing this

- **Read `research/jellyfin-10.11-plugin-api.md` first.** It is verified against
  tag `v10.11.11` with source links, and 10.11 differs from what most training
  data and most blog posts describe.
- **Do not use `jellyfin/jellyfin-plugin-template`.** Its `master` is pinned to
  `Jellyfin.Controller` 10.9.11 and `unstable` has moved on to `net10.0` / 12.x.
  Bookshelf and OpenSubtitles are the right structural references.
- The plugin GUID `96d55013-3cf0-465e-9036-7fb73dd47f71` is the shared key
  between the server, the repository manifest and every installed copy. Changing
  it orphans existing installs. `PluginManifestTests` guards it.
- `EmbeddedResourcePath` in `GetPages()` is a plain string holding an MSBuild
  manifest resource name (`<RootNamespace>.<folder>.<file>`). Renaming or moving
  the HTML breaks it at runtime only, which is why there is a test asserting the
  resource exists under exactly that name.
- `AnalysisMode=AllEnabledByDefault` plus `TreatWarningsAsErrors` means every
  public member needs an XML doc comment. `CA1724` is suppressed in the csproj:
  a type called `Plugin` in a `Jellyfin.Plugin.*` assembly is the convention the
  whole ecosystem uses.
- The Jellyfin `emby-checkbox` inputs on the config page are visually replaced,
  so Playwright's `check()` needs `{ force: true }` or a click on the label.
