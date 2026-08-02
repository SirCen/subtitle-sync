// Browser-integration layer: turn an uploaded video File into the 0/1
// speech-activity signal that lib/sync.ts consumes.
//
// Ports `extract_audio` + `read_wav_pcm16` + `speech_signal_from_audio`
// from reference/sync_srt.py:
//   - ffmpeg.wasm (single-threaded core) extracts mono 16kHz signed-16-bit PCM
//   - a WASM WebRTC VAD (libfvad, @echogarden/fvad-wasm) flags 30ms speech frames
//   - the per-frame booleans are filled into a SIGNAL_HZ-resolution 0/1 signal
//
// Only `assembleSpeechSignal` is pure and unit-tested. The ffmpeg + VAD code is
// BROWSER-ONLY and is lazy-loaded via dynamic import() so that merely importing
// this module in Node (vitest) never instantiates any WASM. Do NOT convert those
// dynamic imports to static top-level imports.
//
// SINGLE-THREADED ONLY: we load @ffmpeg/core (not @ffmpeg/core-mt), so no
// SharedArrayBuffer / COOP-COEP headers are required.

import { SR, FRAME_MS, SIGNAL_HZ } from "./types";
import type { SpeechSignal } from "./types";

// ---------------------------------------------------------------------------
// Pure: frame booleans -> 0/1 signal (port of speech_signal_from_audio's fill)
// ---------------------------------------------------------------------------

/**
 * Port of the frame->signal fill logic in `speech_signal_from_audio`.
 *
 * Given per-frame speech booleans, build a Float32Array sampled at `signalHz`.
 * For each speech frame i, the steps covering [i*frameMs, (i+1)*frameMs) are
 * marked 1.0, using the exact same integer/rounding arithmetic as the Python
 * reference (note the `+ 1` on the end index, which makes adjacent frames share
 * a boundary step so contiguous speech frames yield contiguous 1s).
 *
 * PURE - no WASM, no I/O. This is the unit-tested part.
 */
export function assembleSpeechSignal(
  frameIsSpeech: boolean[],
  frameMs: number = FRAME_MS,
  signalHz: number = SIGNAL_HZ,
): Float32Array {
  const nFrames = frameIsSpeech.length;
  // Mirror Python operation order exactly so IEEE-754 results match:
  //   total_duration = n_frames * FRAME_MS / 1000.0
  //   n_signal = int(total_duration * SIGNAL_HZ) + 1
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

// ---------------------------------------------------------------------------
// ffmpeg.wasm: decode to 16kHz mono s16le PCM
// ---------------------------------------------------------------------------

// The WASM core assets (ffmpeg-core.js / ffmpeg-core.wasm) are loaded from a
// URL at runtime. For now we fetch the single-threaded UMD core from a CDN via
// toBlobURL (bypasses CORS). TASK #7 will replace CDN_CORE_BASE_URL with an
// app-served path (e.g. /ffmpeg-core/...) so the assets ship from our own
// origin on Vercel - see extractPcm16's load() call.
const FFMPEG_CORE_VERSION = "0.12.10";
const CDN_CORE_BASE_URL = `https://cdn.jsdelivr.net/npm/@ffmpeg/core@${FFMPEG_CORE_VERSION}/dist/umd`;

// Cache the loaded ffmpeg instance across calls (loading the core is expensive).
type FFmpegInstance = import("@ffmpeg/ffmpeg").FFmpeg;
let ffmpegPromise: Promise<FFmpegInstance> | null = null;

async function getFFmpeg(): Promise<FFmpegInstance> {
  if (!ffmpegPromise) {
    ffmpegPromise = (async () => {
      // Dynamic import keeps @ffmpeg/* out of the Node module graph.
      const { FFmpeg } = await import("@ffmpeg/ffmpeg");
      const { toBlobURL } = await import("@ffmpeg/util");
      const ffmpeg = new FFmpeg();
      await ffmpeg.load({
        // Single-threaded core: no ffmpeg-core.worker.js, no SharedArrayBuffer.
        coreURL: await toBlobURL(
          `${CDN_CORE_BASE_URL}/ffmpeg-core.js`,
          "text/javascript",
        ),
        wasmURL: await toBlobURL(
          `${CDN_CORE_BASE_URL}/ffmpeg-core.wasm`,
          "application/wasm",
        ),
      });
      return ffmpeg;
    })();
  }
  return ffmpegPromise;
}

/** Strip a canonical 44-byte WAV/RIFF header, returning the PCM payload. */
function stripWavHeader(bytes: Uint8Array): Uint8Array {
  // Validate "RIFF"...."WAVE" and walk chunks to find "data". Falls back to the
  // canonical 44-byte offset if the layout is unexpected.
  if (
    bytes.length >= 44 &&
    bytes[0] === 0x52 && // R
    bytes[1] === 0x49 && // I
    bytes[2] === 0x46 && // F
    bytes[3] === 0x46 // F
  ) {
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    let pos = 12; // skip RIFF(4) + size(4) + WAVE(4)
    while (pos + 8 <= bytes.length) {
      const id0 = bytes[pos];
      const id1 = bytes[pos + 1];
      const id2 = bytes[pos + 2];
      const id3 = bytes[pos + 3];
      const size = dv.getUint32(pos + 4, true);
      // "data"
      if (id0 === 0x64 && id1 === 0x61 && id2 === 0x74 && id3 === 0x61) {
        const start = pos + 8;
        return bytes.subarray(start, Math.min(start + size, bytes.length));
      }
      pos += 8 + size + (size & 1); // chunks are word-aligned
    }
  }
  return bytes.subarray(44);
}

async function toUint8Array(
  file: File | Blob | ArrayBuffer,
): Promise<Uint8Array> {
  if (file instanceof ArrayBuffer) return new Uint8Array(file);
  // File extends Blob; Blob.arrayBuffer() is available in browsers.
  const buf = await file.arrayBuffer();
  return new Uint8Array(buf);
}

/**
 * Decode `file` to 16kHz mono signed-16-bit PCM using ffmpeg.wasm.
 *
 * Mirrors `extract_audio` (ffmpeg -vn -ac 1 -ar 16000, mono 16kHz) then
 * `read_wav_pcm16`. We emit a WAV and strip its header to recover the raw
 * little-endian int16 samples.
 *
 * BROWSER-ONLY. Reports decode progress in [0,1] via `onProgress` if provided.
 */
export async function extractPcm16(
  file: File | Blob | ArrayBuffer,
  onProgress?: (ratio: number) => void,
): Promise<Int16Array> {
  const ffmpeg = await getFFmpeg();

  let progressHandler: ((e: { progress: number }) => void) | undefined;
  if (onProgress) {
    progressHandler = ({ progress }) => {
      // ffmpeg reports progress in [0,1]; clamp defensively.
      onProgress(Math.max(0, Math.min(1, progress)));
    };
    ffmpeg.on("progress", progressHandler);
  }

  const inputName = "input";
  const outputName = "output.wav";
  try {
    await ffmpeg.writeFile(inputName, await toUint8Array(file));
    // Port of extract_audio's flags: no video, mono, 16kHz, PCM s16le WAV.
    await ffmpeg.exec([
      "-i",
      inputName,
      "-vn",
      "-ac",
      "1",
      "-ar",
      String(SR),
      "-c:a",
      "pcm_s16le",
      "-f",
      "wav",
      outputName,
    ]);
    const out = await ffmpeg.readFile(outputName);
    const bytes =
      typeof out === "string" ? new TextEncoder().encode(out) : out;
    const pcmBytes = stripWavHeader(bytes);
    // Ensure correct alignment for Int16Array (byteOffset must be even and we
    // want an exact copy of the little-endian s16le payload).
    const evenLen = pcmBytes.byteLength - (pcmBytes.byteLength % 2);
    const copy = new Uint8Array(evenLen);
    copy.set(pcmBytes.subarray(0, evenLen));
    if (onProgress) onProgress(1);
    return new Int16Array(copy.buffer);
  } finally {
    if (progressHandler) ffmpeg.off("progress", progressHandler);
    // Best-effort cleanup of the virtual FS.
    try {
      await ffmpeg.deleteFile(inputName);
    } catch {
      /* ignore */
    }
    try {
      await ffmpeg.deleteFile(outputName);
    } catch {
      /* ignore */
    }
  }
}

// ---------------------------------------------------------------------------
// WebRTC VAD (libfvad via @echogarden/fvad-wasm): per-frame speech booleans
// ---------------------------------------------------------------------------

// Raw Emscripten module surface we use from @echogarden/fvad-wasm.
// The fvad.wasm asset is resolved by the module itself via
// `new URL("fvad.wasm", import.meta.url)`. A bundler (Next/webpack/turbopack)
// will emit and serve it alongside the JS chunk, so no extra TASK #7 wiring is
// required for the VAD (unlike the ffmpeg core, which is CDN/self-hosted).
interface FvadModule {
  _fvad_new(): number;
  _fvad_free(inst: number): void;
  _fvad_set_mode(inst: number, mode: number): number;
  _fvad_set_sample_rate(inst: number, rate: number): number;
  _fvad_process(inst: number, framePtr: number, numSamples: number): number;
  _malloc(bytes: number): number;
  _free(ptr: number): void;
  HEAP16: Int16Array;
}

let fvadPromise: Promise<FvadModule> | null = null;

async function getFvad(): Promise<FvadModule> {
  if (!fvadPromise) {
    fvadPromise = (async () => {
      // @echogarden/fvad-wasm ships no type declarations; its default export is
      // an async Emscripten module factory. Suppress the implicit-any import.
      // @ts-expect-error - untyped WASM module
      const mod = await import("@echogarden/fvad-wasm");
      // Default export is an async Emscripten factory returning the Module.
      const factory = mod.default as unknown as (
        arg?: Record<string, unknown>,
      ) => Promise<FvadModule>;
      return factory();
    })();
  }
  return fvadPromise;
}

/**
 * Frame `pcm` into 30ms (FRAME_MS) frames and return per-frame speech booleans
 * using the WASM WebRTC VAD. Mirrors the per-frame `vad.is_speech(...)` loop in
 * `speech_signal_from_audio`; VAD errors on a frame are treated as non-speech
 * (matching the Python try/except).
 *
 * BROWSER-ONLY (loads WASM).
 */
export async function runVad(
  pcm: Int16Array,
  aggressiveness: 0 | 1 | 2 | 3,
): Promise<boolean[]> {
  const mod = await getFvad();
  const frameLen = Math.trunc((SR * FRAME_MS) / 1000); // 480 samples @16kHz/30ms
  const nFrames = Math.trunc(pcm.length / frameLen);
  const result: boolean[] = new Array(nFrames);

  const inst = mod._fvad_new();
  if (!inst) throw new Error("fvad: failed to allocate VAD instance");
  const framePtr = mod._malloc(frameLen * 2); // int16 == 2 bytes
  try {
    if (mod._fvad_set_mode(inst, aggressiveness) !== 0) {
      throw new Error(`fvad: invalid aggressiveness ${aggressiveness}`);
    }
    if (mod._fvad_set_sample_rate(inst, SR) !== 0) {
      throw new Error(`fvad: unsupported sample rate ${SR}`);
    }

    const heapBase = framePtr >> 1; // int16 index into HEAP16
    for (let i = 0; i < nFrames; i++) {
      const start = i * frameLen;
      // Copy this frame's samples into the WASM heap.
      mod.HEAP16.set(pcm.subarray(start, start + frameLen), heapBase);
      const r = mod._fvad_process(inst, framePtr, frameLen);
      result[i] = r === 1; // 1 = speech; 0 = non-speech; -1 = error -> false
    }
  } finally {
    mod._free(framePtr);
    mod._fvad_free(inst);
  }
  return result;
}

// ---------------------------------------------------------------------------
// Orchestrator
// ---------------------------------------------------------------------------

/**
 * Full pipeline: extract PCM -> run VAD -> assemble the 0/1 speech signal.
 * This is what the UI (task #6) calls. BROWSER-ONLY.
 *
 * `onProgress` is forwarded from the ffmpeg decode stage (the dominant cost).
 */
export async function extractSpeechSignal(
  file: File | Blob | ArrayBuffer,
  options: { vadAggressiveness?: 0 | 1 | 2 | 3 } = {},
  onProgress?: (ratio: number) => void,
): Promise<SpeechSignal> {
  const aggressiveness = options.vadAggressiveness ?? 2;
  const pcm = await extractPcm16(file, onProgress);
  const frames = await runVad(pcm, aggressiveness);
  return assembleSpeechSignal(frames);
}
