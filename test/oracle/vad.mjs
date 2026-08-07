// Shared VAD front end for the fixture generators in this directory.
//
// This is the same pipeline lib/audio.ts runs in the browser, with one
// substitution: audio is decoded by the LOCAL native `ffmpeg` binary rather
// than ffmpeg.wasm. Both are asked for the identical target format
// (`-vn -ac 1 -ar 16000 -c:a pcm_s16le`) and emit identical PCM, so the 0/1
// signal produced here matches what extractSpeechSignal() yields in the app.
//
// The framing and fill arithmetic in runVad + assembleSpeechSignal is copied
// verbatim from lib/audio.ts. Keep it that way: the golden fixture
// (test/fixtures/speech_signal.json) is pinned to this output.
//
// Used by gen_speech_signal.mjs and gen_structured_fixture.mjs.

import { execFileSync } from "node:child_process";
import { readFileSync, unlinkSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import fvadFactory from "@echogarden/fvad-wasm";

// Constants mirror lib/types.ts.
export const SR = 16000;
export const FRAME_MS = 30;
export const SIGNAL_HZ = 100;
export const VAD_AGGRESSIVENESS = 2;

/**
 * Decode a media file's audio to 16 kHz mono s16le PCM via the native ffmpeg
 * binary. `extraArgs` is spliced in before the output (e.g. an `-af` filter).
 */
export function decodeToPcm16(mediaPath, extraArgs = []) {
  const wavPath = join(tmpdir(), `subtitle-sync-gen-${process.pid}-${Date.now()}.wav`);
  try {
    execFileSync(
      "ffmpeg",
      [
        "-y", "-i", mediaPath,
        "-vn", ...extraArgs, "-ac", "1", "-ar", String(SR),
        "-c:a", "pcm_s16le", "-f", "wav",
        wavPath, "-loglevel", "error",
      ],
      { stdio: "inherit" },
    );
    return readWavPcm16(readFileSync(wavPath));
  } finally {
    try {
      unlinkSync(wavPath);
    } catch {
      // best effort
    }
  }
}

/** Decode + VAD + assemble, i.e. the whole front end in one call. */
export async function speechSignalFor(mediaPath, extraArgs = []) {
  const pcm = decodeToPcm16(mediaPath, extraArgs);
  const mod = await fvadFactory();
  return assembleSpeechSignal(runVad(mod, pcm, VAD_AGGRESSIVENESS));
}

/** Fraction of the signal flagged as speech, in [0, 1]. */
export function speechRatio(signal) {
  let n = 0;
  for (let i = 0; i < signal.length; i++) n += signal[i] ? 1 : 0;
  return signal.length ? n / signal.length : 0;
}

export function readWavPcm16(bytes) {
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

export function runVad(mod, pcm, aggressiveness) {
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

export function assembleSpeechSignal(frameIsSpeech, frameMs = FRAME_MS, signalHz = SIGNAL_HZ) {
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
