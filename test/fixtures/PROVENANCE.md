# Test fixture provenance

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

**Creative Commons Attribution 3.0 (CC-BY 3.0)** — the film, its assets, and the
subtitle files are released by the Blender Foundation under CC-BY.

Required attribution:

> (CC) Blender Foundation | mango.blender.org

(Note: `copyright.txt` in the same directory additionally places the *original
soundtrack* audio-only files under CC-BY-ND, but that restriction applies to the
standalone OST files, not to the film clip used here.)

## What was produced

- `sample.mp4` — a 30-second clip trimmed from the full film starting at t=22s
  (original timeline 00:00:22 → 00:00:52). This window contains the opening
  Thom/Celia dialogue, which is clear spoken English.
  - Container/codecs: MP4, H.264 video (640x268, 24 fps) + AAC audio
    (44.1 kHz, stereo).
  - Duration: 30.02 s. Size: ~2.2 MB.
  - Audio verified non-silent: mean_volume -20.4 dB, max_volume -1.5 dB
    (ffmpeg `volumedetect`). Full decode passed with no errors
    (`ffmpeg -v error -i sample.mp4 -f null -`).

- `sample.srt` — **derived from the official CC-BY English SRT** (cues 1–10),
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
#    downloaded — only the bytes needed for the 22s..52s window.
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
