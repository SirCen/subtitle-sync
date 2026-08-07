# Fixture generators

Two tests, two fixtures, one rule: **the tests read committed artifacts only**.
Neither spawns Python, ffmpeg or WASM. This directory holds the scripts that
regenerate those artifacts, and `vad.mjs`, the decode + VAD front end they share.

| Test | Proves | Generator |
| --- | --- | --- |
| `test/golden.test.ts` | `lib/sync.ts` reproduces `reference/sync_srt.py` on real footage | `gen_speech_signal.mjs`, `oracle.py` |
| `test/structured.test.ts` | `analyze` recovers a displacement known by construction | `gen_structured_fixture.mjs` |

The two are complementary. Parity says the port matches the reference; it says
nothing about either being right. The structured fixture is what says that.

---

# Golden parity fixtures

The golden/parity test (`test/golden.test.ts`) proves that the TypeScript sync
port (`lib/sync.ts`) reproduces the Python reference (`reference/sync_srt.py`) on
the real Tears-of-Steel fixture.

## Artifacts (committed, read by the test)

- `test/fixtures/speech_signal.json` - the shared 0/1 VAD speech signal at
  `SIGNAL_HZ = 100`, produced from `sample.mp4`. Both the TS port and the Python
  oracle consume this identical signal, decoupling the parity check from VAD.
- `test/fixtures/expected.json` - the Python reference result: the best
  `{label, ratio, offset, score}` plus the full per-ratio table.

## Regenerating

### 1. Speech signal - `gen_speech_signal.mjs`

```bash
node test/oracle/gen_speech_signal.mjs
```

Requires `ffmpeg` on PATH. The pipeline lives in `vad.mjs`: it decodes
`sample.mp4` to 16 kHz mono s16le PCM with the **native ffmpeg binary** (same
target format ffmpeg.wasm produces in the browser), then runs the **same
production WebRTC VAD** the app uses (`@echogarden/fvad-wasm`, aggressiveness 2).
The framing + fill arithmetic is copied verbatim from `lib/audio.ts` (`runVad` +
`assembleSpeechSignal`), so the signal matches what `extractSpeechSignal()`
yields in the browser - only the audio-decode front end differs (native ffmpeg vs
ffmpeg.wasm), which emits identical PCM.

`speech_signal.json` is pinned to this output byte-for-byte, so changes to
`vad.mjs` must leave it unchanged. Re-run and check `git diff` is empty.

### 2. Oracle result - `oracle.py`

```bash
pip install numpy scipy      # wheels only, no C compiler needed
python test/oracle/oracle.py
```

Reuses `reference/sync_srt.py`'s `subtitle_signal`, `best_offset_for_ratio`,
`DEFAULT_RATIOS`, and `parse_srt` - numpy + scipy only. It loads the committed
`speech_signal.json` (no VAD, so no `webrtcvad`/compiler needed - the reference's
top-level `import webrtcvad` is stubbed) and `sample.srt`, runs the full ratio
search at `maxOffset = 120 s`, and writes `expected.json`.

## Parity achieved

With the committed fixtures, scipy (Python) vs fft.js (TS) agree to:

- best/per-ratio **offset**: identical (Δ = 0, well within the ±0.01 s = one
  `SIGNAL_HZ` step tolerance).
- per-ratio **score**: max |Δ| ≈ 1.5e-7 (the test allows 1e-4 abs + 1e-3 rel).

---

# Structured (known-answer) fixture

`test/structured.test.ts` asserts that `analyze` recovers a displacement that
was built in, so an algorithm regression fails the build. The golden test cannot
do this: the VAD reads 91.8% of `sample.mp4` as speech, leaving no speech/silence
structure to correlate against, and `analyze` misses by about 8 s on it. Issue
#20.

## Artifacts (committed, read by the test)

- `test/fixtures/structured.mp4` - `sample.mp4`'s audio with deliberate gaps
  muted, over a synthetic black video track. 147 KB.
- `test/fixtures/structured.aligned.srt` - the correct track for that audio.
- `test/fixtures/structured.offset.srt` - displaced by ratio 1.0, offset -3.2 s.
- `test/fixtures/structured.ratio.srt` - displaced by ratio 25/23.976,
  offset -1.5 s.
- `test/fixtures/structured_speech_signal.json` - the VAD output for it.
- `test/fixtures/structured_expected.json` - the known answers and the measured
  speech ratio.

## Regenerating

```bash
node test/oracle/gen_structured_fixture.mjs
```

Requires `ffmpeg` on PATH; uses `mpeg4` + `aac`, which are built into every
ffmpeg, so it does not need a libx264-enabled build. It mutes, re-muxes, runs
the VAD from `vad.mjs`, cuts the aligned cues to the detected speech runs, and
writes all six files. The construction lives in named constants at the top of
the script.

Output is not guaranteed bit-exact across ffmpeg builds (the AAC encoder is not
specified to be), but the structure and the recovered answers are stable, and
those are what the test asserts.

`test/fixtures/PROVENANCE.md` records the licensing, the design rationale, and
the measured results.
