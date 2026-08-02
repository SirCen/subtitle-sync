import { describe, it, expect } from "vitest";
import { FRAME_MS, SIGNAL_HZ } from "./types";
import { assembleSpeechSignal } from "./audio";

// These tests cover ONLY the pure frame->signal fill logic ported from
// `speech_signal_from_audio` in reference/sync_srt.py. The ffmpeg.wasm /
// WebRTC-VAD glue is browser-only and intentionally not exercised here.

describe("assembleSpeechSignal", () => {
  it("uses FRAME_MS / SIGNAL_HZ defaults from lib/types", () => {
    // With FRAME_MS=30, SIGNAL_HZ=100 each frame spans 3 signal steps and the
    // Python mapping adds +1 to the end index, so a single speech frame marks
    // 4 steps. Verify the defaults produce that behavior.
    const out = assembleSpeechSignal([true, false, false]);
    expect(FRAME_MS).toBe(30);
    expect(SIGNAL_HZ).toBe(100);
    // signal length = trunc(nFrames * FRAME_MS/1000 * SIGNAL_HZ) + 1 = 3*3 + 1
    expect(out.length).toBe(10);
    expect(Array.from(out)).toEqual([1, 1, 1, 1, 0, 0, 0, 0, 0, 0]);
  });

  it("returns all zeros when no frame is speech", () => {
    const out = assembleSpeechSignal([false, false, false, false, false]);
    // length = trunc(5*3) + 1 = 16
    expect(out.length).toBe(16);
    expect(Array.from(out).every((v) => v === 0)).toBe(true);
  });

  it("marks the correct steps for an isolated speech frame", () => {
    // Frame index 2: t0=0.06 -> s0=6, t1=0.09 -> s1=trunc(9)+1=10 -> [6,10)
    const out = assembleSpeechSignal([false, false, true]);
    expect(out.length).toBe(10);
    expect(Array.from(out)).toEqual([0, 0, 0, 0, 0, 0, 1, 1, 1, 1]);
  });

  it("produces contiguous 1s for contiguous speech frames", () => {
    // Frame0 -> [0,4), Frame1 -> [3,7); union is [0,7)
    const out = assembleSpeechSignal([true, true, false]);
    expect(out.length).toBe(10);
    expect(Array.from(out)).toEqual([1, 1, 1, 1, 1, 1, 1, 0, 0, 0]);
  });

  it("all-speech input fills the whole signal with 1s", () => {
    const out = assembleSpeechSignal([true, true, true, true]);
    // length = trunc(4*3) + 1 = 13; last frame end index is clamped to length
    expect(out.length).toBe(13);
    expect(Array.from(out).every((v) => v === 1)).toBe(true);
  });

  it("returns an empty-length signal (1 step) for zero frames", () => {
    const out = assembleSpeechSignal([]);
    // trunc(0) + 1 = 1
    expect(out.length).toBe(1);
    expect(Array.from(out)).toEqual([0]);
  });

  it("returns a Float32Array with 0.0 / 1.0 values", () => {
    const out = assembleSpeechSignal([true, false]);
    expect(out).toBeInstanceOf(Float32Array);
    for (const v of out) expect(v === 0 || v === 1).toBe(true);
  });

  it("honors custom frameMs / signalHz overriding the defaults", () => {
    // frameMs=20, signalHz=50 -> each frame spans 1 step (20ms * 50Hz = 1),
    // plus the +1 end index -> 2 steps per frame.
    // length = trunc(3 * 20/1000 * 50) + 1 = trunc(3) + 1 = 4
    const out = assembleSpeechSignal([true, false, false], 20, 50);
    expect(out.length).toBe(4);
    // Frame0: t0=0 -> s0=0, t1=0.02 -> s1=trunc(1)+1=2 -> [0,2)
    expect(Array.from(out)).toEqual([1, 1, 0, 0]);
  });

  it("clamps the final frame's end index to the signal length", () => {
    // frameMs=10, signalHz=100 -> each frame spans 1 step, +1 -> 2 steps.
    // 2 frames: length = trunc(2 * 10/1000 * 100) + 1 = trunc(2)+1 = 3
    // Frame1 (last): t0=0.01 -> s0=1, t1=0.02 -> s1=trunc(2)+1=3 -> min(3,3)=3
    const out = assembleSpeechSignal([false, true], 10, 100);
    expect(out.length).toBe(3);
    expect(Array.from(out)).toEqual([0, 1, 1]);
  });
});
