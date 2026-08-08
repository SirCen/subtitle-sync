// Streaming PCM -> VAD frame adapter for the Jellyfin plugin.
//
// The website decodes an in-memory File with ffmpeg.wasm (lib/audio.ts). The
// plugin instead asks the server for `GET /SubtitleSync/Pcm/{id}`, which streams
// raw 16 kHz mono signed-16-bit little-endian PCM - the exact byte format
// `extractPcm16` recovers from its WAV output, and the same format the Python
// reference reads in `read_wav_pcm16`. There is no container and no header: the
// response body is the s16le payload from the first byte.
//
// This module turns that byte stream into the 30 ms VAD frames that
// `speech_signal_from_audio` iterates over, running the VAD incrementally so we
// never hold an hour of PCM in memory, then hands the per-frame booleans to
// `assembleSpeechSignal` from lib/ - unchanged, so the golden parity test keeps
// covering the code we ship.
//
// The VAD is injected (`FrameVad`), so the framing logic is testable without
// WASM. `createFvadFrameVad()` supplies the real one in the browser.

import { SR, FRAME_MS } from "../../../lib/types";
import type { SpeechSignal } from "../../../lib/types";
import { assembleSpeechSignal } from "../../../lib/audio";

// ---------------------------------------------------------------------------
// Contracts
// ---------------------------------------------------------------------------

/**
 * A voice-activity detector that judges one 30 ms frame at a time.
 *
 * `process` receives exactly `frameSamples` int16 samples and returns true for
 * speech. Like the Python reference's try/except around `vad.is_speech`, a
 * detector that cannot judge a frame should return false rather than throw.
 */
export interface FrameVad {
  process(frame: Int16Array): boolean;
  /** Release native/WASM resources. Always called exactly once, even on abort. */
  close?(): void;
}

/** A VAD, or a (possibly async) factory for one - e.g. a lazy WASM load. */
export type FrameVadSource = FrameVad | (() => FrameVad | Promise<FrameVad>);

/** Snapshot handed to `onProgress` after each source chunk is consumed. */
export interface PcmStreamProgress {
  /** Total PCM bytes pulled from the stream so far. */
  bytesRead: number;
  /** The expected total, if the caller knew it (e.g. from Content-Length). */
  totalBytes?: number;
  /** `bytesRead / totalBytes`, clamped to [0,1]. Undefined if unknown. */
  ratio?: number;
  /** Audio duration covered by the whole frames emitted so far, in seconds. */
  secondsDecoded: number;
}

export interface PcmStreamOptions {
  /** Sample rate of the incoming PCM. Defaults to lib/types' SR (16000). */
  sampleRate?: number;
  /** VAD frame size in ms. Defaults to lib/types' FRAME_MS (30). */
  frameMs?: number;
  /** Expected byte length of the stream, used to compute a progress ratio. */
  totalBytes?: number;
  /** Aborts the read; the returned promise/iterator rejects with AbortError. */
  signal?: AbortSignal;
  onProgress?: (progress: PcmStreamProgress) => void;
}

/** Rejection used for aborts - matches the DOM's `AbortSignal.throwIfAborted`. */
function abortError(): Error {
  if (typeof DOMException === "function") {
    return new DOMException("The operation was aborted.", "AbortError");
  }
  const err = new Error("The operation was aborted.");
  err.name = "AbortError";
  return err;
}

function throwIfAborted(signal: AbortSignal | undefined): void {
  if (signal?.aborted) throw abortError();
}

// Host byte order. s16le is little-endian on the wire; every platform that runs
// a browser is little-endian too, so the fast path is the normal one, but we do
// not assume it.
const HOST_IS_LITTLE_ENDIAN =
  new Uint8Array(new Uint16Array([1]).buffer)[0] === 1;

/** Decode exactly `bytes.length / 2` little-endian int16 samples. */
function decodeS16le(bytes: Uint8Array): Int16Array {
  // `slice` gives a fresh, zero-offset (so int16-aligned) buffer, which also
  // means each yielded frame owns its storage and the caller may retain it.
  const copy = bytes.slice();
  if (HOST_IS_LITTLE_ENDIAN) return new Int16Array(copy.buffer);
  const out = new Int16Array(copy.length / 2);
  const dv = new DataView(copy.buffer);
  for (let i = 0; i < out.length; i++) out[i] = dv.getInt16(i * 2, true);
  return out;
}

// ---------------------------------------------------------------------------
// Framing
// ---------------------------------------------------------------------------

/**
 * Re-frame a byte stream of s16le PCM into fixed 30 ms frames.
 *
 * Chunk boundaries are irrelevant to the output: bytes are accumulated into a
 * frame-sized carry buffer, so a chunk may split a frame or even a single
 * 16-bit sample and the frames are identical either way.
 *
 * A trailing partial frame is DISCARDED, exactly as the reference does with
 * `n_frames = len(pcm) // frame_len`. Any trailing odd byte is discarded with
 * it, since it cannot complete a sample.
 */
export async function* iteratePcmFrames(
  stream: ReadableStream<Uint8Array>,
  options: PcmStreamOptions = {},
): AsyncGenerator<Int16Array, void, undefined> {
  const { sampleRate = SR, frameMs = FRAME_MS, signal, onProgress } = options;
  const frameSamples = Math.trunc((sampleRate * frameMs) / 1000);
  const frameBytes = frameSamples * 2;

  throwIfAborted(signal);

  const reader = stream.getReader();
  const carry = new Uint8Array(frameBytes);
  let carryLen = 0;
  let bytesRead = 0;
  let framesEmitted = 0;
  let cancelled = false;

  try {
    for (;;) {
      throwIfAborted(signal);
      const { done, value } = await reader.read();
      if (done) break;
      throwIfAborted(signal);

      const chunk = value as Uint8Array;
      bytesRead += chunk.length;

      let offset = 0;
      while (offset < chunk.length) {
        const take = Math.min(frameBytes - carryLen, chunk.length - offset);
        carry.set(chunk.subarray(offset, offset + take), carryLen);
        carryLen += take;
        offset += take;
        if (carryLen === frameBytes) {
          carryLen = 0;
          framesEmitted++;
          yield decodeS16le(carry);
          // The consumer may have aborted while handling the frame.
          throwIfAborted(signal);
        }
      }

      if (onProgress) {
        onProgress({
          bytesRead,
          totalBytes: options.totalBytes,
          ratio:
            options.totalBytes && options.totalBytes > 0
              ? Math.min(1, bytesRead / options.totalBytes)
              : undefined,
          secondsDecoded: (framesEmitted * frameMs) / 1000,
        });
      }
    }
    // carryLen > 0 here is the trailing partial frame: dropped on purpose.
  } finally {
    if (!cancelled) {
      cancelled = true;
      // Never let cancellation surface as an unhandled rejection; the abort (or
      // the caller's own error) is the failure worth reporting.
      void reader.cancel().catch(() => {});
    }
    reader.releaseLock();
  }
}

// ---------------------------------------------------------------------------
// VAD over the stream
// ---------------------------------------------------------------------------

async function resolveVad(source: FrameVadSource): Promise<FrameVad> {
  return typeof source === "function" ? await source() : source;
}

/**
 * Run `vad` over every whole frame in the stream and collect the per-frame
 * speech booleans - the streaming equivalent of `runVad` in lib/audio.ts.
 *
 * The VAD is closed exactly once when the stream ends, aborts, or throws.
 */
export async function runVadOverPcmStream(
  stream: ReadableStream<Uint8Array>,
  vad: FrameVadSource,
  options: PcmStreamOptions = {},
): Promise<boolean[]> {
  throwIfAborted(options.signal);
  const detector = await resolveVad(vad);
  const frames: boolean[] = [];
  try {
    for await (const frame of iteratePcmFrames(stream, options)) {
      frames.push(detector.process(frame));
    }
  } finally {
    detector.close?.();
  }
  return frames;
}

/**
 * Full plugin-side pipeline: PCM byte stream -> VAD frames -> 0/1 speech signal
 * at SIGNAL_HZ, ready for `analyze()` in lib/sync.ts.
 *
 * The fill arithmetic is `assembleSpeechSignal` from lib/, untouched, so for the
 * same frame decisions this yields a signal identical to the website's.
 */
export async function speechSignalFromPcmStream(
  stream: ReadableStream<Uint8Array>,
  vad: FrameVadSource,
  options: PcmStreamOptions = {},
): Promise<SpeechSignal> {
  const frames = await runVadOverPcmStream(stream, vad, options);
  return assembleSpeechSignal(frames, options.frameMs ?? FRAME_MS);
}

// ---------------------------------------------------------------------------
// The real VAD (browser only)
// ---------------------------------------------------------------------------

// Same Emscripten surface lib/audio.ts uses; redeclared because lib/ does not
// export it and lib/ must not be edited.
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

/**
 * Create the production WebRTC VAD (libfvad via @echogarden/fvad-wasm), the same
 * detector `runVad` uses. BROWSER-ONLY: loads WASM, so it is dynamically
 * imported and never pulled into a Node test's module graph.
 */
export async function createFvadFrameVad(
  aggressiveness: 0 | 1 | 2 | 3 = 2,
  sampleRate: number = SR,
  frameMs: number = FRAME_MS,
): Promise<FrameVad> {
  // @ts-expect-error - untyped WASM module
  const imported = await import("@echogarden/fvad-wasm");
  const factory = imported.default as unknown as (
    arg?: Record<string, unknown>,
  ) => Promise<FvadModule>;
  const mod = await factory();

  const frameSamples = Math.trunc((sampleRate * frameMs) / 1000);
  const inst = mod._fvad_new();
  if (!inst) throw new Error("fvad: failed to allocate VAD instance");
  const framePtr = mod._malloc(frameSamples * 2);
  try {
    if (mod._fvad_set_mode(inst, aggressiveness) !== 0) {
      throw new Error(`fvad: invalid aggressiveness ${aggressiveness}`);
    }
    if (mod._fvad_set_sample_rate(inst, sampleRate) !== 0) {
      throw new Error(`fvad: unsupported sample rate ${sampleRate}`);
    }
  } catch (err) {
    mod._free(framePtr);
    mod._fvad_free(inst);
    throw err;
  }

  const heapBase = framePtr >> 1; // int16 index into HEAP16
  let closed = false;
  return {
    process(frame: Int16Array): boolean {
      mod.HEAP16.set(frame, heapBase);
      // 1 = speech, 0 = non-speech, -1 = error (treated as non-speech, as the
      // Python reference's try/except does).
      return mod._fvad_process(inst, framePtr, frameSamples) === 1;
    },
    close() {
      if (closed) return;
      closed = true;
      mod._free(framePtr);
      mod._fvad_free(inst);
    },
  };
}
