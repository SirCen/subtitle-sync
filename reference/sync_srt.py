#!/usr/bin/env python3
"""
sync_srt.py — Auto-detect the correct offset/framerate for an SRT file
by comparing it against the actual speech (voice activity) in a video,
then write out a corrected SRT.

HOW IT WORKS
------------
SRT timestamps don't store a framerate — they're just wall-clock times.
So "wrong framerate" subtitles actually just drift more and more out of
sync the further into the video you go. This script:

  1. Extracts the video's audio and runs Voice Activity Detection (VAD)
     to get a "speech happening / not happening" signal over time.
  2. Builds the same kind of signal from the SRT's subtitle intervals.
  3. Tries a set of candidate framerate ratios (23.976/25, 24/25, etc,
     plus 1.0 for "just an offset, no framerate issue").
  4. For each candidate ratio, rescales the subtitle signal and
     cross-correlates it against the speech signal to find the best
     time offset and a confidence score.
  5. Picks the ratio+offset combo with the highest confidence score,
     applies it to the original SRT, and writes a corrected file.

REQUIREMENTS
------------
  - ffmpeg / ffprobe on PATH
  - pip install numpy scipy webrtcvad

USAGE
-----
  python3 sync_srt.py video.mp4 subtitles.srt -o synced.srt

  # narrow down / widen the search
  python3 sync_srt.py video.mp4 subtitles.srt -o synced.srt \\
      --max-offset 120 --ratios 1.0 23.976/25 25/23.976 24/25 25/24

  # just analyze, don't write a file
  python3 sync_srt.py video.mp4 subtitles.srt --dry-run
"""

import argparse
import re
import subprocess
import sys
import tempfile
from fractions import Fraction
from pathlib import Path

import numpy as np

try:
    import webrtcvad
except ImportError:
    sys.exit("Missing dependency: pip install webrtcvad")

try:
    from scipy.signal import fftconvolve
except ImportError:
    sys.exit("Missing dependency: pip install scipy")


# ---------------------------------------------------------------------------
# SRT parsing / writing
# ---------------------------------------------------------------------------

SRT_TIME_RE = re.compile(r"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})")


def srt_time_to_seconds(t: str) -> float:
    h, m, s, ms = SRT_TIME_RE.match(t).groups()
    return int(h) * 3600 + int(m) * 60 + int(s) + int(ms) / 1000.0


def seconds_to_srt_time(sec: float) -> str:
    if sec < 0:
        sec = 0.0
    h = int(sec // 3600)
    sec -= h * 3600
    m = int(sec // 60)
    sec -= m * 60
    s = int(sec)
    ms = round((sec - s) * 1000)
    if ms == 1000:
        ms = 0
        s += 1
        if s == 60:
            s = 0
            m += 1
            if m == 60:
                m = 0
                h += 1
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


class SrtBlock:
    __slots__ = ("index", "start", "end", "text")

    def __init__(self, index, start, end, text):
        self.index = index
        self.start = start  # seconds
        self.end = end       # seconds
        self.text = text


def parse_srt(path: Path):
    raw = path.read_text(encoding="utf-8-sig", errors="replace")
    blocks = []
    # split on blank lines (allow \r\n)
    chunks = re.split(r"\n\s*\n", raw.strip())
    for chunk in chunks:
        lines = [l for l in chunk.splitlines() if l.strip() != ""]
        if len(lines) < 2:
            continue
        # first line may or may not be a numeric index
        idx_line = 0
        if not re.search(r"-->", lines[0]):
            idx_line = 1
        m = re.search(
            r"(\d{2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}[,.]\d{3})",
            lines[idx_line],
        )
        if not m:
            continue
        start = srt_time_to_seconds(m.group(1))
        end = srt_time_to_seconds(m.group(2))
        text = "\n".join(lines[idx_line + 1:])
        blocks.append(SrtBlock(len(blocks) + 1, start, end, text))
    if not blocks:
        sys.exit(f"Could not parse any subtitle entries from {path}")
    return blocks


def write_srt(blocks, path: Path):
    with path.open("w", encoding="utf-8") as f:
        for b in blocks:
            f.write(f"{b.index}\n")
            f.write(f"{seconds_to_srt_time(b.start)} --> {seconds_to_srt_time(b.end)}\n")
            f.write(f"{b.text}\n\n")


# ---------------------------------------------------------------------------
# Audio extraction + VAD speech signal
# ---------------------------------------------------------------------------

SR = 16000          # sample rate required by webrtcvad
FRAME_MS = 30        # webrtcvad supports 10/20/30 ms frames
SIGNAL_HZ = 100       # resolution of the comparison signal (10ms steps)


def extract_audio(video_path: Path, wav_path: Path):
    cmd = [
        "ffmpeg", "-y", "-i", str(video_path),
        "-vn", "-ac", "1", "-ar", str(SR),
        "-f", "wav", str(wav_path),
        "-loglevel", "error",
    ]
    subprocess.run(cmd, check=True)


def read_wav_pcm16(wav_path: Path) -> np.ndarray:
    import wave
    with wave.open(str(wav_path), "rb") as wf:
        assert wf.getframerate() == SR, "unexpected sample rate"
        assert wf.getsampwidth() == 2, "expected 16-bit PCM"
        n = wf.getnframes()
        data = wf.readframes(n)
    return np.frombuffer(data, dtype=np.int16)


def speech_signal_from_audio(pcm: np.ndarray, aggressiveness: int) -> np.ndarray:
    """Return a 0/1 array at SIGNAL_HZ resolution marking speech frames."""
    vad = webrtcvad.Vad(aggressiveness)
    frame_len = int(SR * FRAME_MS / 1000)
    n_frames = len(pcm) // frame_len
    total_duration = n_frames * FRAME_MS / 1000.0
    n_signal = int(total_duration * SIGNAL_HZ) + 1
    signal = np.zeros(n_signal, dtype=np.float32)

    samples_per_signal_step = SIGNAL_HZ / (1000.0 / FRAME_MS)  # signal-steps per vad frame

    for i in range(n_frames):
        frame = pcm[i * frame_len:(i + 1) * frame_len]
        frame_bytes = frame.tobytes()
        try:
            is_speech = vad.is_speech(frame_bytes, SR)
        except Exception:
            is_speech = False
        if is_speech:
            t0 = i * FRAME_MS / 1000.0
            t1 = t0 + FRAME_MS / 1000.0
            s0 = int(t0 * SIGNAL_HZ)
            s1 = int(t1 * SIGNAL_HZ) + 1
            signal[s0:min(s1, n_signal)] = 1.0
    return signal


def subtitle_signal(blocks, ratio: float, length: int) -> np.ndarray:
    signal = np.zeros(length, dtype=np.float32)
    for b in blocks:
        s0 = int(b.start * ratio * SIGNAL_HZ)
        s1 = int(b.end * ratio * SIGNAL_HZ)
        if s0 >= length:
            continue
        signal[max(s0, 0):min(s1, length)] = 1.0
    return signal


# ---------------------------------------------------------------------------
# Cross-correlation search
# ---------------------------------------------------------------------------

def best_offset_for_ratio(speech: np.ndarray, blocks, ratio: float, max_offset_s: float):
    sub_sig = subtitle_signal(blocks, ratio, len(speech))

    # normalize (zero-mean) to make correlation a better similarity measure
    a = speech - speech.mean()
    b = sub_sig - sub_sig.mean()

    # full cross-correlation via FFT; corr[k] corresponds to shifting b by
    # lag = k - (len(b) - 1) relative to a
    corr = fftconvolve(a, b[::-1], mode="full")
    lags = np.arange(-(len(b) - 1), len(a))

    max_lag_steps = int(max_offset_s * SIGNAL_HZ)
    center = len(b) - 1
    lo = max(0, center - max_lag_steps)
    hi = min(len(corr), center + max_lag_steps)

    window = corr[lo:hi]
    window_lags = lags[lo:hi]
    best_idx = int(np.argmax(window))
    best_lag = window_lags[best_idx]
    best_score = window[best_idx]

    # normalize score so ratios/videos are comparable (roughly correlation coefficient)
    denom = (np.linalg.norm(a) * np.linalg.norm(b)) or 1.0
    norm_score = best_score / denom

    offset_seconds = best_lag / SIGNAL_HZ
    return offset_seconds, float(norm_score)


DEFAULT_RATIOS = {
    "1.0 (offset only)": 1.0,
    "23.976/25": 23.976 / 25,
    "25/23.976": 25 / 23.976,
    "24/25": 24 / 25,
    "25/24": 25 / 24,
    "23.976/24": 23.976 / 24,
    "24/23.976": 24 / 23.976,
    "25/29.97": 25 / 29.97,
    "29.97/25": 29.97 / 25,
    "24/29.97": 24 / 29.97,
    "29.97/24": 29.97 / 24,
}


def parse_ratio_arg(s: str) -> float:
    if "/" in s:
        num, den = s.split("/")
        return float(num) / float(den)
    return float(s)


def get_video_fps(video_path: Path):
    try:
        out = subprocess.run(
            ["ffprobe", "-v", "error", "-select_streams", "v:0",
             "-show_entries", "stream=r_frame_rate", "-of", "csv=p=0", str(video_path)],
            capture_output=True, text=True, check=True,
        ).stdout.strip()
        return float(Fraction(out))
    except Exception:
        return None


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("video", type=Path, help="video file")
    ap.add_argument("srt", type=Path, help="input .srt file")
    ap.add_argument("-o", "--output", type=Path, default=None, help="output .srt path (default: <srt>.synced.srt)")
    ap.add_argument("--max-offset", type=float, default=60.0, help="max seconds of offset to search for (default 60)")
    ap.add_argument("--vad-aggressiveness", type=int, default=2, choices=[0, 1, 2, 3],
                     help="0=least aggressive filtering of non-speech, 3=most aggressive (default 2)")
    ap.add_argument("--ratios", nargs="*", default=None,
                     help="candidate ratios to test, e.g. 1.0 23.976/25 25/23.976 (default: built-in common set)")
    ap.add_argument("--dry-run", action="store_true", help="analyze and report only, don't write output")
    args = ap.parse_args()

    if not args.video.exists():
        sys.exit(f"Video not found: {args.video}")
    if not args.srt.exists():
        sys.exit(f"SRT not found: {args.srt}")

    print(f"Parsing subtitles: {args.srt}")
    blocks = parse_srt(args.srt)
    print(f"  {len(blocks)} subtitle entries, "
          f"spanning {seconds_to_srt_time(blocks[0].start)} to {seconds_to_srt_time(blocks[-1].end)}")

    fps = get_video_fps(args.video)
    if fps:
        print(f"Video reports framerate: {fps:.3f} fps (informational only — not used directly)")

    print("Extracting audio and running voice activity detection (this can take a bit)...")
    with tempfile.TemporaryDirectory() as td:
        wav_path = Path(td) / "audio.wav"
        extract_audio(args.video, wav_path)
        pcm = read_wav_pcm16(wav_path)
        speech = speech_signal_from_audio(pcm, args.vad_aggressiveness)
    print(f"  {len(speech) / SIGNAL_HZ:.1f}s of audio analyzed, "
          f"{speech.mean() * 100:.1f}% flagged as speech")

    if args.ratios:
        candidates = {r: parse_ratio_arg(r) for r in args.ratios}
    else:
        candidates = DEFAULT_RATIOS

    print("\nTesting candidate framerate ratios...")
    results = []
    for label, ratio in candidates.items():
        offset, score = best_offset_for_ratio(speech, blocks, ratio, args.max_offset)
        results.append((label, ratio, offset, score))
        print(f"  {label:22s} ratio={ratio:.6f}  best_offset={offset:+7.2f}s  score={score:.4f}")

    results.sort(key=lambda r: r[3], reverse=True)
    best_label, best_ratio, best_offset, best_score = results[0]
    runner_up_score = results[1][3] if len(results) > 1 else 0.0

    print(f"\nBest match: {best_label} (ratio={best_ratio:.6f}, offset={best_offset:+.2f}s, score={best_score:.4f})")
    if best_score < 0.05:
        print("  WARNING: confidence score is very low. The audio may have little dialogue,")
        print("  the SRT may not match this video at all, or VAD settings need adjusting.")
    elif runner_up_score > 0 and (best_score - runner_up_score) / best_score < 0.15:
        print("  NOTE: the top two candidates scored similarly — worth double-checking the result.")

    if args.dry_run:
        return

    out_path = args.output or args.srt.with_suffix(".synced.srt")
    corrected = []
    for b in blocks:
        new_start = b.start * best_ratio + best_offset
        new_end = b.end * best_ratio + best_offset
        corrected.append(SrtBlock(b.index, new_start, new_end, b.text))

    write_srt(corrected, out_path)
    print(f"\nWrote corrected subtitles to: {out_path}")


if __name__ == "__main__":
    main()
