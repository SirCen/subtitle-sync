// Generate the committed speech-signal fixture (test/fixtures/speech_signal.json).
//
// This mirrors lib/audio.ts's real VAD pipeline as closely as is practical in a
// headless Node context:
//   - Audio decode: the LOCAL `ffmpeg` binary decodes sample.mp4 to 16kHz mono
//     signed-16-bit PCM. This is byte-for-byte the same target format that
//     ffmpeg.wasm produces in the browser (`-vn -ac 1 -ar 16000 -c:a pcm_s16le`),
//     just via the native binary so we don't have to fetch the ffmpeg.wasm core
//     from a CDN at generation time.
//   - VAD: the SAME WebRTC VAD used by the app in production, @echogarden/fvad-wasm
//     (libfvad), run with aggressiveness=2 (the app default). The framing + fill
//     arithmetic below is copied verbatim from lib/audio.ts runVad +
//     assembleSpeechSignal so the emitted 0/1 signal matches what
//     extractSpeechSignal() would produce.
//
// Usage:  ffmpeg on PATH, then:  node test/oracle/gen_speech_signal.mjs
// Output: test/fixtures/speech_signal.json  { signalHz, length, signal: [0/1,...] }

import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { tmpdir } from "node:os";
import fvadFactory from "@echogarden/fvad-wasm";

// Constants mirror lib/types.ts.
const SR = 16000;
const FRAME_MS = 30;
const SIGNAL_HZ = 100;
const VAD_AGGRESSIVENESS = 2;

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, "..", "..");
const mp4Path = join(repoRoot, "test", "fixtures", "sample.mp4");
const outPath = join(repoRoot, "test", "fixtures", "speech_signal.json");
const wavPath = join(tmpdir(), `subtitle-sync-gen-${process.pid}.wav`);

// 1. Decode to 16kHz mono s16le WAV with the native ffmpeg binary.
execFileSync(
  "ffmpeg",
  [
    "-y", "-i", mp4Path,
    "-vn", "-ac", "1", "-ar", String(SR),
    "-c:a", "pcm_s16le", "-f", "wav",
    wavPath, "-loglevel", "error",
  ],
  { stdio: "inherit" },
);

// 2. Read the WAV and recover the little-endian int16 PCM payload.
const wavBytes = readFileSync(wavPath);
const pcm = readWavPcm16(wavBytes);

// 3. Run the WASM WebRTC VAD (port of lib/audio.ts runVad).
const mod = await fvadFactory();
const frames = runVad(mod, pcm, VAD_AGGRESSIVENESS);

// 4. Assemble the 0/1 signal (port of lib/audio.ts assembleSpeechSignal).
const signal = assembleSpeechSignal(frames);

// 5. Emit as plain JSON (0/1 integers to keep it compact & exact).
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

// ---------------------------------------------------------------------------

function readWavPcm16(bytes) {
  // Walk RIFF chunks to find "data"; fall back to canonical 44-byte header.
  if (
    bytes.length >= 44 &&
    bytes[0] === 0x52 && bytes[1] === 0x49 && bytes[2] === 0x46 && bytes[3] === 0x46
  ) {
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    let pos = 12;
    while (pos + 8 <= bytes.length) {
      const isData =
        bytes[pos] === 0x64 && bytes[pos + 1] === 0x61 &&
        bytes[pos + 2] === 0x74 && bytes[pos + 3] === 0x61;
      const size = dv.getUint32(pos + 4, true);
      if (isData) {
        const start = pos + 8;
        const end = Math.min(start + size, bytes.length);
        const evenLen = (end - start) - ((end - start) % 2);
        const copy = new Uint8Array(evenLen);
        copy.set(bytes.subarray(start, start + evenLen));
        return new Int16Array(copy.buffer);
      }
      pos += 8 + size + (size & 1);
    }
  }
  const payload = bytes.subarray(44);
  const evenLen = payload.byteLength - (payload.byteLength % 2);
  const copy = new Uint8Array(evenLen);
  copy.set(payload.subarray(0, evenLen));
  return new Int16Array(copy.buffer);
}

function runVad(mod, pcm, aggressiveness) {
  const frameLen = Math.trunc((SR * FRAME_MS) / 1000); // 480 @ 16kHz/30ms
  const nFrames = Math.trunc(pcm.length / frameLen);
  const result = new Array(nFrames);
  const inst = mod._fvad_new();
  if (!inst) throw new Error("fvad: failed to allocate VAD instance");
  const framePtr = mod._malloc(frameLen * 2);
  try {
    if (mod._fvad_set_mode(inst, aggressiveness) !== 0) {
      throw new Error(`fvad: invalid aggressiveness ${aggressiveness}`);
    }
    if (mod._fvad_set_sample_rate(inst, SR) !== 0) {
      throw new Error(`fvad: unsupported sample rate ${SR}`);
    }
    const heapBase = framePtr >> 1;
    for (let i = 0; i < nFrames; i++) {
      const start = i * frameLen;
      mod.HEAP16.set(pcm.subarray(start, start + frameLen), heapBase);
      const r = mod._fvad_process(inst, framePtr, frameLen);
      result[i] = r === 1;
    }
  } finally {
    mod._free(framePtr);
    mod._fvad_free(inst);
  }
  return result;
}

function assembleSpeechSignal(frameIsSpeech, frameMs = FRAME_MS, signalHz = SIGNAL_HZ) {
  const nFrames = frameIsSpeech.length;
  const totalDuration = (nFrames * frameMs) / 1000.0;
  const nSignal = Math.trunc(totalDuration * signalHz) + 1;
  const signal = new Float32Array(nSignal);
  for (let i = 0; i < nFrames; i++) {
    if (!frameIsSpeech[i]) continue;
    const t0 = (i * frameMs) / 1000.0;
    const t1 = t0 + frameMs / 1000.0;
    const s0 = Math.trunc(t0 * signalHz);
    const s1 = Math.trunc(t1 * signalHz) + 1;
    const hi = Math.min(s1, nSignal);
    for (let s = s0; s < hi; s++) signal[s] = 1.0;
  }
  return signal;
}
