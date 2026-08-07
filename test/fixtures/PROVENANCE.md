# Test fixture provenance

Two fixtures, answering two different questions.

| Fixture | Question it answers | Test |
| --- | --- | --- |
| `sample.mp4` + `sample.srt` | do the TypeScript port and the Python reference agree on real footage? | `test/golden.test.ts` |
| `structured.mp4` + `structured.*.srt` | does the algorithm recover a displacement that is known by construction? | `test/structured.test.ts` |

The second is **derived from the first** and inherits its licence. Both sections
below apply to it.

---

# 1. Sample Clip (`sample.mp4`, `sample.srt`)

Fixture for the golden/parity test that runs `reference/sync_srt.py` and the
TypeScript port on the same input (`sample.mp4` + `sample.srt`) and asserts they
agree.

## Source

Blender open movie **"Tears of Steel"** (project "Mango", 2012).

- Project site: https://mango.blender.org/
- Video source (full film, 720p QuickTime, ~372 MB):
  https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov
- Official English subtitles (full film):
  https://download.blender.org/demo/movies/ToS/subtitles/TOS-en.srt

## License

**Creative Commons Attribution 3.0 (CC-BY 3.0)** - the film, its assets, and the
subtitle files are released by the Blender Foundation under CC-BY.

Required attribution:

> (CC) Blender Foundation | mango.blender.org

(Note: `copyright.txt` in the same directory additionally places the *original
soundtrack* audio-only files under CC-BY-ND, but that restriction applies to the
standalone OST files, not to the film clip used here.)

## What was produced

- `sample.mp4` - a 30-second clip trimmed from the full film starting at t=22s
  (original timeline 00:00:22 → 00:00:52). This window contains the opening
  Thom/Celia dialogue, which is clear spoken English.
  - Container/codecs: MP4, H.264 video (640x268, 24 fps) + AAC audio
    (44.1 kHz, stereo).
  - Duration: 30.02 s. Size: ~2.2 MB.
  - Audio verified non-silent: mean_volume -20.4 dB, max_volume -1.5 dB
    (ffmpeg `volumedetect`). Full decode passed with no errors
    (`ffmpeg -v error -i sample.mp4 -f null -`).

- `sample.srt` - **derived from the official CC-BY English SRT** (cues 1–10),
  with every timestamp shifted by **-22 s** to line up with the trimmed clip's
  new zero point. Because it is a straight offset of the official subtitles,
  the alignment is essentially accurate (not merely rough). Cue 10's end was
  capped at 00:00:30,000 to stay within the clip length. Text is verbatim from
  the official subtitles.

## Exact commands used

Local tooling: `ffmpeg`/`ffprobe` version 4.3 (this build lacks `libx264`, so the
Windows MediaFoundation H.264 encoder `h264_mf` was used).

```bash
# 1. Fetch the official English subtitles (used as the text/timing basis)
curl -s -o TOS-en.srt \
  "https://download.blender.org/demo/movies/ToS/subtitles/TOS-en.srt"

# 2. Trim + re-encode a small 30s clip directly from the HTTP source.
#    ffmpeg HTTP byte-range seeking is used, so the full 372 MB file is NOT
#    downloaded - only the bytes needed for the 22s..52s window.
ffmpeg -y -ss 22 \
  -i "https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov" \
  -t 30 -vf "scale=640:-2" \
  -c:v h264_mf -b:v 500k \
  -c:a aac -b:a 96k -ac 2 \
  sample.mp4

# 3. sample.srt was authored by hand from TOS-en.srt cues 1-10, subtracting
#    22s from each timestamp. TOS-en.srt was then deleted.
```

## Verification

```bash
ffprobe -v error -show_entries format=duration,size,bit_rate \
  -show_entries stream=codec_type,codec_name,sample_rate,channels sample.mp4
# -> h264 video + aac audio (44100 Hz, 2 ch), duration 30.024s, ~2.2 MB

ffmpeg -v error -i sample.mp4 -f null -    # clean decode, no errors
ffmpeg -i sample.mp4 -af volumedetect -f null -   # mean -20.4 dB / max -1.5 dB
```

## Known limitation: it cannot validate a sync

The VAD flags **91.8%** of this clip as speech. A signal that is speech nearly
everywhere has no structure to cross-correlate against, so `analyze` cannot
recover a known offset from it - measured error is around 8 seconds. That is a
property of the clip, not of the algorithm.

So this fixture proves *parity* (the port matches the reference) and nothing
about *correctness*. Fixture 2 exists to cover that gap. Issue #20.

---

# 2. Structured Clip (`structured.mp4`, `structured.*.srt`)

A synthesised fixture whose correct answer is fixed by construction: the audio
alternates speech and silence on a schedule we chose, the subtitles are cut to
the speech, and the out-of-sync inputs are displaced by amounts we recorded.
`analyze` must recover exactly those amounts.

## Source and licence

Derived from `sample.mp4` above, so it carries the same attribution:

> (CC) Blender Foundation | mango.blender.org

Only the **audio** is derived. The video track is a synthetic black frame
(nothing looks at the picture, and reusing the 2.2 MB H.264 track would have
multiplied the repo's fixture weight for no test value). The subtitle text is
placeholder (`Structured cue 1`...), not from the Blender subtitles.

## What was produced

| File | Size | What it is |
| --- | --- | --- |
| `structured.mp4` | 147 KB | 30 s. `sample.mp4`'s audio with the gaps muted, over a synthetic black video track. mpeg4 (part 2) video + AAC 64 kbps mono. |
| `structured.aligned.srt` | 501 B | Ground truth: one cue per speech run. Correct for this audio, needs no correction. |
| `structured.offset.srt` | 501 B | The offset case input: aligned, displaced so the answer is ratio 1.0, offset **-3.2 s**. |
| `structured.ratio.srt` | 501 B | The ratio case input: aligned, rescaled and displaced so the answer is ratio **25/23.976**, offset **-1.5 s**. |
| `structured_speech_signal.json` | 6.0 KB | The VAD output for `structured.mp4`, so the test needs no ffmpeg. |
| `structured_expected.json` | 1.3 KB | The mute schedule, the detected speech runs, and the known answer for each case. |

About 159 KB in total, against the 2.2 MB of `sample.mp4`.

The mute schedule is deliberately **irregular** (region lengths 0.8 s to 1.8 s,
gaps 0.6 s to 1.6 s). A regular pattern would make the cross-correlation
periodic, so several lags would score alike and the recovered offset would be
ambiguous by construction - the very failure this fixture exists to avoid.

The aligned cues are cut to the runs the **VAD actually found**, not to the mute
schedule directly. The two differ by tens of milliseconds because a WebRTC VAD
decides per 30 ms frame and has a trailing hangover. Cueing off the schedule
baked that in as a fixed ~0.1 s bias in the recovered offset; cueing off the
detected runs makes the aligned track genuinely correct for this audio, so the
only thing left to recover is the displacement - and it comes back exact. The
generator still asserts the runs match the schedule (one per region, within
0.25 s), so the fixture cannot quietly stop meaning what it claims.

## Exact command

```bash
node test/oracle/gen_structured_fixture.mjs      # needs ffmpeg on PATH
```

That rewrites all six files. The construction - regions, ratios, offsets - lives
in named constants at the top of that script; change it there, never by hand.

Regeneration is **not** expected to be bit-exact across ffmpeg builds (the AAC
encoder is not specified to be), but the speech/silence structure and the
recovered answers are stable, and those are what the test asserts. The script
deliberately uses `mpeg4` + `aac`, which are built into every ffmpeg, rather
than libx264, which the local ffmpeg 4.3 build does not have.

## Verification

The VAD flags **45.6%** of this clip as speech, against 91.8% for `sample.mp4`,
and resolves it into exactly 10 runs - one per muted region. `analyze` recovers:

| Input | Known answer | Recovered | Score |
| --- | --- | --- | --- |
| `structured.aligned.srt` | ratio 1.0, offset 0 | ratio 1.0, offset **0.000** | 0.999 |
| `structured.offset.srt` | ratio 1.0, offset -3.2 | ratio 1.0, offset **-3.200** | 0.909 |
| `structured.ratio.srt` | ratio 25/23.976, offset -1.5 | ratio 25/23.976, offset **-1.500** | 0.952 |

Exact to the search's 0.01 s resolution in all three cases.

Note that `analyze` reports `confident: false` on this fixture, with the
"top two candidates scored similarly" note. That is correct, not a defect: the
runner-up is a ratio 0.1% away (1.0 vs 24/23.976), which over a 30 s clip is
30 ms of drift - less than the signal can resolve. The low-confidence warning,
which would mean no real peak was found, never appears. The ratio case is still
decisive where it matters: the correct ratio scores 0.952 against 0.632 for
offset-only, so ratio search is genuinely being exercised.
