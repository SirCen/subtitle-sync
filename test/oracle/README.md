# Golden-test oracle & fixtures

The golden/parity test (`test/golden.test.ts`) proves that the TypeScript sync
port (`lib/sync.ts`) reproduces the Python reference (`reference/sync_srt.py`) on
the real Tears-of-Steel fixture. The test itself spawns **no** Python, ffmpeg, or
WASM — it only reads two committed JSON artifacts. This directory holds the
scripts that regenerate those artifacts.

## Artifacts (committed, read by the test)

- `test/fixtures/speech_signal.json` — the shared 0/1 VAD speech signal at
  `SIGNAL_HZ = 100`, produced from `sample.mp4`. Both the TS port and the Python
  oracle consume this identical signal, decoupling the parity check from VAD.
- `test/fixtures/expected.json` — the Python reference result: the best
  `{label, ratio, offset, score}` plus the full per-ratio table.

## Regenerating

### 1. Speech signal — `gen_speech_signal.mjs`

```bash
node test/oracle/gen_speech_signal.mjs
```

Requires `ffmpeg` on PATH. It decodes `sample.mp4` to 16 kHz mono s16le PCM with
the **native ffmpeg binary** (same target format ffmpeg.wasm produces in the
browser), then runs the **same production WebRTC VAD** the app uses
(`@echogarden/fvad-wasm`, aggressiveness 2). The framing + fill arithmetic is
copied verbatim from `lib/audio.ts` (`runVad` + `assembleSpeechSignal`), so the
signal matches what `extractSpeechSignal()` yields in the browser — only the
audio-decode front end differs (native ffmpeg vs ffmpeg.wasm), which emits
identical PCM.

### 2. Oracle result — `oracle.py`

```bash
pip install numpy scipy      # wheels only, no C compiler needed
python test/oracle/oracle.py
```

Reuses `reference/sync_srt.py`'s `subtitle_signal`, `best_offset_for_ratio`,
`DEFAULT_RATIOS`, and `parse_srt` — numpy + scipy only. It loads the committed
`speech_signal.json` (no VAD, so no `webrtcvad`/compiler needed — the reference's
top-level `import webrtcvad` is stubbed) and `sample.srt`, runs the full ratio
search at `maxOffset = 120 s`, and writes `expected.json`.

## Parity achieved

With the committed fixtures, scipy (Python) vs fft.js (TS) agree to:

- best/per-ratio **offset**: identical (Δ = 0, well within the ±0.01 s = one
  `SIGNAL_HZ` step tolerance).
- per-ratio **score**: max |Δ| ≈ 1.5e-7 (the test allows 1e-4 abs + 1e-3 rel).
