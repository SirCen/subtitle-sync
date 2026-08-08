// Generate the "Structured Clip" fixture: a synthesised clip whose speech and
// silence alternate on a known, deliberately irregular schedule, so a sync's
// correct answer is known BY CONSTRUCTION.
//
// Why it exists (issue #20): the VAD flags ~92% of test/fixtures/sample.mp4 as
// speech. A signal that is speech nearly everywhere has no structure to
// cross-correlate against, so `analyze` cannot recover a known offset from it
// and any end-to-end assertion built on it either asserts a wrong value or
// passes vacuously. This fixture fixes that without sourcing new media: it
// reuses sample.mp4's own audio and mutes the gaps.
//
// What it produces (all committed to test/fixtures/):
//   structured.mp4              media: sample.mp4's audio with SILENT_GAPS
//                               muted, over a synthetic black video track
//   structured.aligned.srt      ground truth: one cue per speech region
//   structured.offset.srt       aligned.srt shifted, the offset-case input
//   structured.ratio.srt        aligned.srt rescaled + shifted, the ratio case
//   structured_speech_signal.json  the VAD output for structured.mp4
//   structured_expected.json    the known answers + measured speech ratio
//
// test/structured.test.ts reads the last two and asserts `analyze` recovers the
// known ratio and offset. It reads committed artifacts only, so it needs no
// ffmpeg, no Python and no WASM at test time - same contract as golden.test.ts.
//
// Usage:  ffmpeg on PATH, then:  node test/oracle/gen_structured_fixture.mjs
//
// Regeneration is not expected to be bit-exact across ffmpeg builds (the AAC
// encoder is not specified to be), but the speech/silence structure and the
// recovered answers are stable, which is what the test asserts.

import { writeFileSync, statSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

import { SIGNAL_HZ, speechSignalFor, speechRatio } from "./vad.mjs";

// ---------------------------------------------------------------------------
// The construction. Everything the fixture means is in these constants.
// ---------------------------------------------------------------------------

/** Clip length, seconds. sample.mp4 is 30.02 s; stay inside it. */
const DURATION_S = 30;

/**
 * Speech regions, seconds, in the ALIGNED (correct) timeline. Audio is kept
 * here and muted everywhere else, and one subtitle cue covers each region.
 *
 * Deliberately irregular. A regular pattern makes the cross-correlation
 * periodic, so several lags score alike and the recovered offset is ambiguous
 * by construction - exactly the failure mode this fixture exists to avoid.
 * Both edges are padded with silence so a shifted copy never runs off the clip.
 */
const SPEECH_REGIONS = [
  [1.0, 2.2],
  [3.0, 3.8],
  [5.4, 7.0],
  [8.2, 9.0],
  [10.6, 12.4],
  [13.2, 14.0],
  [15.6, 17.4],
  [18.4, 19.4],
  [20.8, 21.6],
  [22.6, 24.4],
];

/**
 * The two out-of-sync inputs. `ratio`/`offset` are the answers `analyze` must
 * recover: applyCorrection(input, ratio, offset) maps the input back onto
 * SPEECH_REGIONS, since lib/sync.ts corrects by `t * ratio + offset`.
 *
 * So the input cue times are the inverse map, `(aligned - offset) / ratio`.
 */
const CASES = [
  {
    name: "offset",
    file: "structured.offset.srt",
    label: "1.0 (offset only)",
    ratio: 1.0,
    offset: -3.2, // subtitles arrive 3.2 s late
  },
  {
    name: "ratio",
    file: "structured.ratio.srt",
    label: "25/23.976",
    ratio: 25 / 23.976, // a 23.976 fps subtitle track against a 25 fps clip
    offset: -1.5,
  },
];

// ---------------------------------------------------------------------------

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, "..", "..");
const fixtures = join(repoRoot, "test", "fixtures");
const sourceMp4 = join(fixtures, "sample.mp4");
const outMp4 = join(fixtures, "structured.mp4");

// 1. Build the media: sample.mp4's audio with the gaps muted, muxed over a
//    synthetic black video track.
//
//    The video is synthetic and tiny on purpose. Jellyfin needs a video stream
//    to file the item as a movie, but nothing here looks at the picture, and
//    re-using sample.mp4's 2 MB H.264 track would double the repo's fixture
//    weight for no test value. mpeg4 (part 2) + aac are built into every
//    ffmpeg, so this does not depend on a libx264-enabled build - the local
//    ffmpeg used for the original fixture does not have one.
const keepExpr = SPEECH_REGIONS.map(([a, b]) => `between(t,${a},${b})`).join("+");
const muteFilter = `volume=volume=0:enable='not(${keepExpr})'`;

execFileSync(
  "ffmpeg",
  [
    "-y",
    "-f", "lavfi", "-i", `color=c=black:s=320x160:r=5:d=${DURATION_S}`,
    "-i", sourceMp4,
    "-map", "0:v:0", "-map", "1:a:0",
    "-af", muteFilter,
    "-t", String(DURATION_S),
    "-c:v", "mpeg4", "-q:v", "31",
    "-c:a", "aac", "-b:a", "64k", "-ac", "1", "-ar", "44100",
    "-movflags", "+faststart",
    outMp4,
    "-loglevel", "error",
  ],
  { stdio: "inherit" },
);

// 2. Run the production VAD over the generated media.
const signal = await speechSignalFor(outMp4);
const arr = Array.from(signal, (v) => (v ? 1 : 0));
const ratio = speechRatio(signal);

// 3. Derive the aligned (ground-truth) cues from the runs the VAD ACTUALLY
//    found, not from SPEECH_REGIONS directly.
//
//    The two differ by tens of milliseconds: a WebRTC VAD decides per 30 ms
//    frame and has a trailing hangover, so each detected run reaches a little
//    past the region that produced it. Cueing off SPEECH_REGIONS would bake
//    that difference in as a fixed bias in the recovered offset - measured at
//    roughly 0.1 s - and the test would then have to tolerate a bias that has
//    nothing to do with the algorithm. Cueing off the detected runs makes the
//    aligned track the genuinely correct one for this audio, so the only answer
//    left to recover is the displacement, which is exact.
//
//    The check below is what keeps this honest: the derived runs must still be
//    the mute schedule we designed, one per region and close to it.
const alignedCues = speechRuns(signal).map((r, i) => ({
  ...r,
  text: `Structured cue ${i + 1}`,
}));

if (alignedCues.length !== SPEECH_REGIONS.length) {
  throw new Error(
    `VAD found ${alignedCues.length} speech runs but the mute schedule has ` +
    `${SPEECH_REGIONS.length}. The fixture no longer means what it claims.`,
  );
}
alignedCues.forEach((cue, i) => {
  const [start, end] = SPEECH_REGIONS[i];
  const drift = Math.max(Math.abs(cue.start - start), Math.abs(cue.end - end));
  if (drift > 0.25) {
    throw new Error(
      `VAD run ${i + 1} (${cue.start}-${cue.end}) drifted ${drift.toFixed(2)}s ` +
      `from its region (${start}-${end}).`,
    );
  }
});

// 4. Write the aligned SRT and the two out-of-sync inputs.
writeFileSync(join(fixtures, "structured.aligned.srt"), toSrt(alignedCues));

for (const c of CASES) {
  const shifted = alignedCues.map((cue) => ({
    ...cue,
    start: (cue.start - c.offset) / c.ratio,
    end: (cue.end - c.offset) / c.ratio,
  }));
  writeFileSync(join(fixtures, c.file), toSrt(shifted));
}

writeFileSync(
  join(fixtures, "structured_speech_signal.json"),
  JSON.stringify({ signalHz: SIGNAL_HZ, length: arr.length, signal: arr }) + "\n",
);

// 4. Record the construction alongside the measurement, so the test asserts
//    against declared intent rather than against whatever came out.
writeFileSync(
  join(fixtures, "structured_expected.json"),
  JSON.stringify(
    {
      meta: {
        signalHz: SIGNAL_HZ,
        signalLength: arr.length,
        durationS: DURATION_S,
        maxOffsetS: 120,
        /** Measured, not asserted exactly: the point is that it is nowhere near 1. */
        speechRatio: Number(ratio.toFixed(4)),
      },
      /** The mute schedule the media was built to. Design intent. */
      speechRegions: SPEECH_REGIONS,
      /** The runs the VAD found in it, which the aligned cues are cut to. */
      speechRuns: alignedCues.map((c) => [c.start, c.end]),
      alignedSrt: "structured.aligned.srt",
      cases: CASES.map((c) => ({
        name: c.name,
        srt: c.file,
        label: c.label,
        ratio: c.ratio,
        offset: c.offset,
      })),
    },
    null,
    2,
  ) + "\n",
);

const mp4Bytes = statSync(outMp4).size;
console.log(
  `Wrote ${outMp4} (${(mp4Bytes / 1024).toFixed(0)} KB)\n` +
  `  signal length=${arr.length} (${(arr.length / SIGNAL_HZ).toFixed(2)} s), ` +
  `${(ratio * 100).toFixed(1)}% flagged as speech\n` +
  `  cases: ${CASES.map((c) => `${c.name} (ratio ${c.ratio.toFixed(6)}, offset ${c.offset})`).join(", ")}`,
);

// ---------------------------------------------------------------------------

/** Contiguous runs of 1s in a 0/1 signal, as {start, end} in seconds. */
function speechRuns(signal) {
  const runs = [];
  let start = -1;
  for (let i = 0; i <= signal.length; i++) {
    const on = i < signal.length && signal[i] > 0;
    if (on && start < 0) start = i;
    if (!on && start >= 0) {
      runs.push({ start: start / SIGNAL_HZ, end: i / SIGNAL_HZ });
      start = -1;
    }
  }
  return runs;
}

function toSrt(cues) {
  return (
    cues
      .map(
        (c, i) =>
          `${i + 1}\n${srtTime(c.start)} --> ${srtTime(c.end)}\n${c.text}\n`,
      )
      .join("\n") + ""
  );
}

/** Same formatting as lib/srt.ts secondsToSrtTime. */
function srtTime(sec) {
  if (sec < 0) sec = 0;
  let h = Math.floor(sec / 3600);
  sec -= h * 3600;
  let m = Math.floor(sec / 60);
  sec -= m * 60;
  let s = Math.floor(sec);
  let ms = Math.round((sec - s) * 1000);
  if (ms === 1000) {
    ms = 0;
    s += 1;
    if (s === 60) { s = 0; m += 1; }
    if (m === 60) { m = 0; h += 1; }
  }
  const p2 = (n) => String(n).padStart(2, "0");
  return `${p2(h)}:${p2(m)}:${p2(s)},${String(ms).padStart(3, "0")}`;
}
