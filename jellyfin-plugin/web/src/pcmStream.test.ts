import { describe, it, expect, vi } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { FRAME_MS, SIGNAL_HZ, SR } from "../../../lib/types";
import { assembleSpeechSignal } from "../../../lib/audio";
import {
  iteratePcmFrames,
  runVadOverPcmStream,
  speechSignalFromPcmStream,
  type FrameVad,
} from "./pcmStream";

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const FRAME_SAMPLES = (SR * FRAME_MS) / 1000; // 480
const FRAME_BYTES = FRAME_SAMPLES * 2; // 960

/** A ReadableStream that emits `bytes` in fixed-size chunks. */
function streamOf(
  bytes: Uint8Array,
  chunkSize: number = bytes.length,
): ReadableStream<Uint8Array> {
  let pos = 0;
  return new ReadableStream<Uint8Array>({
    pull(controller) {
      if (pos >= bytes.length) {
        controller.close();
        return;
      }
      const end = Math.min(pos + chunkSize, bytes.length);
      // Copy so the consumer can never alias our backing buffer.
      controller.enqueue(bytes.slice(pos, end));
      pos = end;
    },
  });
}

/** Encode int16 samples as little-endian bytes (what the server sends). */
function s16le(samples: number[] | Int16Array): Uint8Array {
  const out = new Uint8Array(samples.length * 2);
  const dv = new DataView(out.buffer);
  for (let i = 0; i < samples.length; i++) dv.setInt16(i * 2, samples[i], true);
  return out;
}

/** Deterministic pseudo-random int16 samples, so tests are reproducible. */
function fakeSamples(n: number): Int16Array {
  const out = new Int16Array(n);
  let seed = 12345;
  for (let i = 0; i < n; i++) {
    seed = (seed * 1103515245 + 12345) & 0x7fffffff;
    out[i] = (seed % 65536) - 32768;
  }
  return out;
}

async function collect(
  stream: ReadableStream<Uint8Array>,
  options?: Parameters<typeof iteratePcmFrames>[1],
): Promise<Int16Array[]> {
  const frames: Int16Array[] = [];
  for await (const frame of iteratePcmFrames(stream, options)) {
    frames.push(frame);
  }
  return frames;
}

/** A VAD that replays a fixed list of decisions, recording what it saw. */
function scriptedVad(decisions: boolean[]): FrameVad & {
  seen: Int16Array[];
} {
  let i = 0;
  const seen: Int16Array[] = [];
  return {
    seen,
    process(frame: Int16Array) {
      seen.push(frame.slice());
      return decisions[i++] ?? false;
    },
  };
}

// ---------------------------------------------------------------------------

describe("iteratePcmFrames", () => {
  it("yields 30 ms frames of 480 samples", async () => {
    const samples = fakeSamples(FRAME_SAMPLES * 3);
    const frames = await collect(streamOf(s16le(samples)));

    expect(frames).toHaveLength(3);
    for (const f of frames) expect(f.length).toBe(FRAME_SAMPLES);
    expect(Array.from(frames[0])).toEqual(
      Array.from(samples.subarray(0, FRAME_SAMPLES)),
    );
    expect(Array.from(frames[2])).toEqual(
      Array.from(samples.subarray(FRAME_SAMPLES * 2, FRAME_SAMPLES * 3)),
    );
  });

  it("produces identical frames for one chunk and byte-by-byte delivery", async () => {
    const bytes = s16le(fakeSamples(FRAME_SAMPLES * 4));

    const oneChunk = await collect(streamOf(bytes));
    const byteByByte = await collect(streamOf(bytes, 1));

    expect(byteByByte).toHaveLength(oneChunk.length);
    expect(byteByByte.map((f) => Array.from(f))).toEqual(
      oneChunk.map((f) => Array.from(f)),
    );
  });

  it("handles a chunk boundary that splits a single 16-bit sample", async () => {
    // 0x1234 little-endian is [0x34, 0x12]; split the pair across chunks.
    const samples = new Int16Array(FRAME_SAMPLES);
    samples[0] = 0x1234;
    samples[1] = -2; // 0xfffe -> [0xfe, 0xff]
    samples[FRAME_SAMPLES - 1] = -32768;
    const bytes = s16le(samples);

    // Odd chunk size guarantees boundaries land mid-sample repeatedly.
    const frames = await collect(streamOf(bytes, 3));

    expect(frames).toHaveLength(1);
    expect(frames[0][0]).toBe(0x1234);
    expect(frames[0][1]).toBe(-2);
    expect(frames[0][FRAME_SAMPLES - 1]).toBe(-32768);
    expect(Array.from(frames[0])).toEqual(Array.from(samples));
  });

  it("decodes bytes as little-endian regardless of chunking", async () => {
    // A lone [0x00, 0x80] pair is -32768 LE and 128 BE. Split it in half.
    const samples = new Int16Array(FRAME_SAMPLES);
    samples[10] = -32768;
    const bytes = s16le(samples);
    const frames = await collect(streamOf(bytes, 21));
    expect(frames[0][10]).toBe(-32768);
  });

  it("discards a trailing partial frame like the Python reference", async () => {
    // len(pcm) // frame_len == 2 for 2 frames + 1 sample.
    const samples = fakeSamples(FRAME_SAMPLES * 2 + 1);
    const frames = await collect(streamOf(s16le(samples), 7));
    expect(frames).toHaveLength(2);
  });

  it("discards a trailing odd byte that cannot form a whole sample", async () => {
    const bytes = s16le(fakeSamples(FRAME_SAMPLES));
    const withStrayByte = new Uint8Array(bytes.length + 1);
    withStrayByte.set(bytes);
    withStrayByte[bytes.length] = 0x7f;

    const frames = await collect(streamOf(withStrayByte, 5));
    expect(frames).toHaveLength(1);
  });

  it("yields nothing for a stream shorter than one frame", async () => {
    const frames = await collect(streamOf(s16le(fakeSamples(479))));
    expect(frames).toEqual([]);
  });

  it("yields nothing for an empty stream", async () => {
    const frames = await collect(streamOf(new Uint8Array(0)));
    expect(frames).toEqual([]);
  });

  it("gives each frame its own storage", async () => {
    const frames = await collect(
      streamOf(s16le(fakeSamples(FRAME_SAMPLES * 2))),
    );
    expect(frames[0].buffer).not.toBe(frames[1].buffer);
  });
});

describe("progress reporting", () => {
  it("reports a 0..1 ratio when totalBytes is known", async () => {
    const bytes = s16le(fakeSamples(FRAME_SAMPLES * 4));
    const onProgress = vi.fn();

    await collect(streamOf(bytes, FRAME_BYTES * 2), {
      totalBytes: bytes.length,
      onProgress,
    });

    expect(onProgress).toHaveBeenCalled();
    const ratios = onProgress.mock.calls.map((c) => c[0].ratio as number);
    expect(ratios[ratios.length - 1]).toBe(1);
    for (let i = 1; i < ratios.length; i++) {
      expect(ratios[i]).toBeGreaterThanOrEqual(ratios[i - 1]);
      expect(ratios[i]).toBeLessThanOrEqual(1);
    }
    const last = onProgress.mock.calls.at(-1)![0];
    expect(last.bytesRead).toBe(bytes.length);
    expect(last.secondsDecoded).toBeCloseTo((4 * FRAME_MS) / 1000, 10);
  });

  it("omits the ratio when totalBytes is unknown but still reports bytes", async () => {
    const bytes = s16le(fakeSamples(FRAME_SAMPLES * 2));
    const onProgress = vi.fn();
    await collect(streamOf(bytes, FRAME_BYTES), { onProgress });

    const last = onProgress.mock.calls.at(-1)![0];
    expect(last.ratio).toBeUndefined();
    expect(last.bytesRead).toBe(bytes.length);
  });
});

describe("abort handling", () => {
  it("stops promptly when aborted mid-stream", async () => {
    const controller = new AbortController();
    let chunksPulled = 0;
    let cancelled = false;

    const stream = new ReadableStream<Uint8Array>({
      pull(c) {
        chunksPulled++;
        c.enqueue(new Uint8Array(FRAME_BYTES)); // infinite source
      },
      cancel() {
        cancelled = true;
      },
    });

    let frames = 0;
    const run = async () => {
      for await (const _frame of iteratePcmFrames(stream, {
        signal: controller.signal,
      })) {
        void _frame;
        frames++;
        if (frames === 3) controller.abort();
      }
    };

    await expect(run()).rejects.toMatchObject({ name: "AbortError" });
    expect(frames).toBe(3);
    expect(cancelled).toBe(true);
    // Prompt: it must not have kept draining the infinite source.
    expect(chunksPulled).toBeLessThan(50);
  });

  it("rejects immediately if the signal is already aborted", async () => {
    const controller = new AbortController();
    controller.abort();
    const bytes = s16le(fakeSamples(FRAME_SAMPLES));
    await expect(
      collect(streamOf(bytes), { signal: controller.signal }),
    ).rejects.toMatchObject({ name: "AbortError" });
  });

  it("does not leave an unhandled rejection after an abort", async () => {
    const unhandled: unknown[] = [];
    const onUnhandled = (reason: unknown) => unhandled.push(reason);
    process.on("unhandledRejection", onUnhandled);
    try {
      const controller = new AbortController();
      const stream = new ReadableStream<Uint8Array>({
        pull(c) {
          c.enqueue(new Uint8Array(FRAME_BYTES));
        },
      });
      const vad: FrameVad = {
        process() {
          controller.abort();
          return false;
        },
      };
      await expect(
        runVadOverPcmStream(stream, vad, { signal: controller.signal }),
      ).rejects.toMatchObject({ name: "AbortError" });
      // Let any stray rejection surface.
      await new Promise((r) => setTimeout(r, 20));
      expect(unhandled).toEqual([]);
    } finally {
      process.off("unhandledRejection", onUnhandled);
    }
  });

  it("closes the VAD even when aborted", async () => {
    const controller = new AbortController();
    const close = vi.fn();
    const vad: FrameVad = {
      process() {
        controller.abort();
        return true;
      },
      close,
    };
    const stream = new ReadableStream<Uint8Array>({
      pull(c) {
        c.enqueue(new Uint8Array(FRAME_BYTES));
      },
    });
    await expect(
      speechSignalFromPcmStream(stream, vad, { signal: controller.signal }),
    ).rejects.toMatchObject({ name: "AbortError" });
    expect(close).toHaveBeenCalledTimes(1);
  });
});

describe("runVadOverPcmStream", () => {
  it("feeds every whole frame to the VAD in order and returns its decisions", async () => {
    const samples = fakeSamples(FRAME_SAMPLES * 3 + 100);
    const vad = scriptedVad([true, false, true]);
    const frames = await runVadOverPcmStream(
      streamOf(s16le(samples), 101),
      vad,
    );

    expect(frames).toEqual([true, false, true]);
    expect(vad.seen).toHaveLength(3);
    expect(Array.from(vad.seen[1])).toEqual(
      Array.from(samples.subarray(FRAME_SAMPLES, FRAME_SAMPLES * 2)),
    );
  });

  it("closes the VAD when the stream ends", async () => {
    const close = vi.fn();
    const vad: FrameVad = { process: () => false, close };
    await runVadOverPcmStream(
      streamOf(s16le(fakeSamples(FRAME_SAMPLES))),
      vad,
    );
    expect(close).toHaveBeenCalledTimes(1);
  });
});

describe("speechSignalFromPcmStream", () => {
  it("matches assembleSpeechSignal over the same frame decisions", async () => {
    const decisions = [true, false, false, true, true];
    const vad = scriptedVad(decisions);
    const signal = await speechSignalFromPcmStream(
      streamOf(s16le(fakeSamples(FRAME_SAMPLES * decisions.length)), 37),
      vad,
    );
    expect(Array.from(signal)).toEqual(
      Array.from(assembleSpeechSignal(decisions)),
    );
  });

  it("reproduces the golden fixture's speech signal", async () => {
    // test/fixtures/speech_signal.json is the committed golden signal produced
    // by the real ffmpeg + WASM VAD pipeline. Frame i is the only frame that
    // marks signal step 3i+1 (frame i covers [3i, 3i+4), so its neighbours
    // touch only steps 3i and 3i+3), which makes the frame decisions exactly
    // recoverable from the signal. We replay those real decisions through a
    // scripted VAD and assert the streaming adapter rebuilds the signal
    // bit-for-bit.
    const fixturePath = join(
      __dirname,
      "../../../test/fixtures/speech_signal.json",
    );
    const fixture = JSON.parse(readFileSync(fixturePath, "utf8")) as {
      signalHz: number;
      length: number;
      signal: number[];
    };
    expect(fixture.signalHz).toBe(SIGNAL_HZ);

    const stepsPerFrame = (FRAME_MS * SIGNAL_HZ) / 1000; // 3
    const nFrames = (fixture.length - 1) / stepsPerFrame;
    expect(Number.isInteger(nFrames)).toBe(true);
    const decisions = Array.from(
      { length: nFrames },
      (_, i) => fixture.signal[i * stepsPerFrame + 1] === 1,
    );
    // Sanity: the recovered decisions must regenerate the fixture exactly.
    expect(Array.from(assembleSpeechSignal(decisions))).toEqual(fixture.signal);

    // Stream the equivalent PCM in awkwardly sized chunks (not a multiple of
    // the 960-byte frame, and odd so samples get split too).
    const pcmBytes = new Uint8Array(nFrames * FRAME_BYTES);
    const signal = await speechSignalFromPcmStream(
      streamOf(pcmBytes, 4097),
      scriptedVad(decisions),
    );

    expect(signal.length).toBe(fixture.length);
    expect(Array.from(signal)).toEqual(fixture.signal);
  });

  it("supports an async VAD factory so WASM can be lazily created", async () => {
    const decisions = [false, true];
    const signal = await speechSignalFromPcmStream(
      streamOf(s16le(fakeSamples(FRAME_SAMPLES * 2))),
      async () => scriptedVad(decisions),
    );
    expect(Array.from(signal)).toEqual(
      Array.from(assembleSpeechSignal(decisions)),
    );
  });
});
