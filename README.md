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
  page.tsx           # renders SubtitleSync
lib/
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
