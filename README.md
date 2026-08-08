# Subtitle Sync

Auto-sync an `.srt` subtitle file to a video - entirely in your browser. Drop in
a video and a subtitle file, and Subtitle Sync figures out the correct time
offset (and framerate ratio) by *listening* to where speech actually happens in
the video, then hands back a corrected `.srt`.

**Nothing is uploaded.** The video never leaves your device - audio extraction,
speech detection, and the matching all run client-side in WebAssembly.

## How it works

SRT timestamps are just wall-clock times; they carry no framerate. So subtitles
authored for a different framerate don't just sit at a fixed offset - they drift
further out of sync the deeper into the video you go. Subtitle Sync handles both
the fixed offset and the drift:

1. **Extract audio** from the video with [`ffmpeg.wasm`](https://ffmpegwasm.netlify.app/)
   (16 kHz mono PCM).
2. **Detect speech** with a WebAssembly build of WebRTC VAD
   ([`@echogarden/fvad-wasm`](https://www.npmjs.com/package/@echogarden/fvad-wasm),
   the same `libfvad` engine as Python's `webrtcvad`), producing a "speech / no
   speech" signal over time.
3. **Match** the subtitle intervals against that speech signal by cross-correlation,
   testing a set of candidate framerate ratios (23.976/25, 24/25, ..., plus 1.0
   for "offset only") and picking the ratio + offset with the highest confidence.
4. **Rewrite** the SRT with the chosen correction and download it.

This is a faithful browser port of the original Python script, which is kept in
[`reference/sync_srt.py`](reference/sync_srt.py) and used as the oracle for a
golden parity test (see [Testing](#testing)).

## Features

- 100% client-side - private, no server, no upload limits to fight.
- Auto-detects framerate-ratio drift, not just a flat offset.
- **Confidence-gated auto-download**: high-confidence results download
  automatically; low-confidence results show a warning and wait for you.
- **Manual nudge**: tweak the offset or ratio by hand and re-download instantly
  (no reprocessing).
- **Advanced options**: max search offset, VAD aggressiveness, and the candidate
  ratio list - each with a reset-to-default.
- Order-independent drag-and-drop, 5 GB hard cap with a large-file warning.

## Jellyfin plugin

The same algorithm also ships as a **Jellyfin 10.11 plugin**, so you can re-time
a track from inside your own server instead of downloading the video first. It
analyses the audio of a film or episode in your browser and writes a corrected
`<base>.<lang>.synced.srt` beside the media file; the original is left alone.

- Source, build instructions and a local Docker test server:
  [`jellyfin-plugin/`](jellyfin-plugin/README.md).
- Install and usage docs: the site's `/plugin` page (behind
  [`NEXT_PUBLIC_FEATURE_PLUGIN_PAGE`](#next_public_feature_plugin_page)).
- Install by adding `https://subtitlesync.sircen.dev/jellyfin/manifest.json` as
  a repository in Dashboard > Plugins, or drop a release zip in by hand.

The plugin is a thin C# shell around the browser code: `lib/` stays the single
source of truth and is bundled into the plugin, so the golden parity test covers
what the plugin ships.

## Getting started

```bash
npm install
npm run dev
```

Open [http://localhost:3000](http://localhost:3000), drop in a video and its
`.srt`, and hit **Sync subtitles**.

> Because everything runs in-browser, extracting audio from a long or large
> video can take several minutes and use a lot of memory. That's expected.

### Scripts

| Command | What it does |
| --- | --- |
| `npm run dev` | Start the dev server |
| `npm run build` | Production build |
| `npm start` | Serve the production build |
| `npm test` | Run the Vitest suite once |
| `npm run test:watch` | Run Vitest in watch mode |
| `npm run lint` | Lint |

### Environment variables

Copy [`.env.example`](.env.example) to `.env.local` (git-ignored) and edit it, or
set the variables in your host's environment settings.

| Variable | Default | What it does |
| --- | --- | --- |
| `NEXT_PUBLIC_FEATURE_PLUGIN_PAGE` | off | Set to `1` to show the **Plugin** nav link and the `/plugin` page |

## Feature flags

Flags live in [`lib/flags.ts`](lib/flags.ts), one exported predicate per flag,
and are read from `NEXT_PUBLIC_*` environment variables.

They **fail closed**: only the exact string `1` turns a flag on. Unset, empty,
`0`, `true` or anything else leaves it off, so a typo hides a feature rather than
publishing it.

### `NEXT_PUBLIC_FEATURE_PLUGIN_PAGE`

Gates the Jellyfin plugin documentation page.

- **Off (default)** - the **Plugin** link is absent from the site nav, and
  `/plugin` calls `notFound()`, so the route returns a 404.
- **On (`1`)** - the nav link renders and `/plugin` serves the page.

```bash
# Turn it on locally
echo 'NEXT_PUBLIC_FEATURE_PLUGIN_PAGE=1' >> .env.local
npm run dev
```

### Known trade-off: hidden, not secret

`NEXT_PUBLIC_` variables are **inlined at build time**, which has two
consequences worth being explicit about:

1. **Changing a flag requires a rebuild and redeploy.** The value is baked into
   the bundle when `next build` runs; flipping the variable on a running
   deployment does nothing.
2. **The flag and the gated page ship to the browser.** The `/plugin` route
   module is part of the client bundle whether or not the flag is on, so anyone
   reading the JavaScript can see that the page exists and what it contains. The
   page is **hidden, not secret**.

This was chosen deliberately over a server-only variable: the flag gates public
marketing-style documentation, not anything sensitive, and a build-time flag
keeps the nav server-rendered with no client-side flicker. Anything that must
actually stay private needs a server-only variable and a server-side check.

## Testing

The pure logic is developed test-first (Vitest):

- `lib/srt.ts` - SRT parsing/writing and time conversion.
- `lib/sync.ts` - subtitle-signal generation, FFT cross-correlation, ratio
  selection, and confidence scoring.
- `lib/audio.ts` - the pure signal-assembly step of the VAD pipeline (the
  ffmpeg.wasm/VAD glue is browser-only and not unit-tested).

A **golden parity test** (`test/golden.test.ts`) pins the TypeScript port to the
Python reference: it runs `lib/sync.ts` on a real committed clip and asserts the
winning ratio/offset and per-ratio scores match `reference/sync_srt.py` (agree to
~1.5e-7). It reads only committed JSON artifacts, so it needs no browser, ffmpeg,
or Python at test time. See [`test/oracle/README.md`](test/oracle/README.md) for
how those artifacts are regenerated.

```bash
npm test
```

## Deploying to Vercel

This is a standard Next.js (App Router) app and deploys to Vercel with zero
configuration - import the repo and deploy.

- The `ffmpeg.wasm` **core is loaded from a CDN** at runtime (via `toBlobURL`),
  so there are no large WASM assets to serve from this app. If you'd prefer to
  self-host them, drop the core files in `public/` and update the base URL in
  `lib/audio.ts`.
- The app uses the **single-threaded** `ffmpeg.wasm` core, which does **not**
  require `SharedArrayBuffer`, so **no COOP/COEP cross-origin-isolation headers
  are needed**.

## Project structure

```
app/
  SubtitleSync.tsx   # the client UI + pipeline orchestration
  layout.tsx         # root layout + server-rendered site nav
  page.tsx           # renders SubtitleSync
  plugin/page.tsx    # feature-flagged Jellyfin plugin page (404s when off)
lib/
  flags.ts           # build-time feature flags
  types.ts           # shared pipeline contracts
  srt.ts             # SRT parse/write + time conversion
  sync.ts            # signal + cross-correlation + ratio/confidence
  audio.ts           # ffmpeg.wasm audio extraction + WASM VAD
reference/
  sync_srt.py        # original Python script (the algorithm's source of truth)
test/
  golden.test.ts     # TS-vs-Python parity test
  fixtures/          # sample clip + subtitles + oracle artifacts
  oracle/            # scripts + docs to regenerate the oracle
```

## Credits

- Test fixture clip and subtitles: **"Tears of Steel"** -
  **(CC) Blender Foundation | mango.blender.org**, licensed
  [CC-BY 3.0](https://creativecommons.org/licenses/by/3.0/). See
  [`test/fixtures/PROVENANCE.md`](test/fixtures/PROVENANCE.md).
- Audio: [`ffmpeg.wasm`](https://ffmpegwasm.netlify.app/) and
  [`@echogarden/fvad-wasm`](https://www.npmjs.com/package/@echogarden/fvad-wasm)
  (WebRTC VAD / `libfvad`).

## License

[MIT](LICENSE) - the application code. The bundled test fixture is CC-BY 3.0 as
noted above.
