// Generate the committed speech-signal fixture (test/fixtures/speech_signal.json).
//
// The decode + VAD front end lives in ./vad.mjs and is shared with
// gen_structured_fixture.mjs. It mirrors lib/audio.ts's real pipeline: the
// LOCAL `ffmpeg` binary decodes sample.mp4 to 16 kHz mono s16le PCM (the same
// target format ffmpeg.wasm produces in the browser), then the SAME production
// WebRTC VAD the app uses (@echogarden/fvad-wasm, aggressiveness 2) runs over
// it, with framing + fill arithmetic copied verbatim from lib/audio.ts.
//
// Usage:  ffmpeg on PATH, then:  node test/oracle/gen_speech_signal.mjs
// Output: test/fixtures/speech_signal.json  { signalHz, length, signal: [0/1,...] }

import { writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

import { SIGNAL_HZ, speechSignalFor } from "./vad.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, "..", "..");
const mp4Path = join(repoRoot, "test", "fixtures", "sample.mp4");
const outPath = join(repoRoot, "test", "fixtures", "speech_signal.json");

const signal = await speechSignalFor(mp4Path);

// Emit as plain JSON (0/1 integers to keep it compact & exact).
const arr = Array.from(signal, (v) => (v ? 1 : 0));
const speechPct = ((arr.reduce((s, v) => s + v, 0) / arr.length) * 100).toFixed(1);
writeFileSync(
  outPath,
  JSON.stringify({ signalHz: SIGNAL_HZ, length: arr.length, signal: arr }) + "\n",
);
console.log(
  `Wrote ${outPath}: length=${arr.length} (${(arr.length / SIGNAL_HZ).toFixed(1)}s), ` +
  `${speechPct}% flagged as speech`,
);
