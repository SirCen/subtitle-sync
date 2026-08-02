import { describe, it, expect } from "vitest";
import type { SrtBlock } from "./types";
import { SIGNAL_HZ } from "./types";
import {
  DEFAULT_RATIOS,
  parseRatio,
  subtitleSignal,
  bestOffsetForRatio,
  analyze,
  applyCorrection,
} from "./sync";

// --- helpers ------------------------------------------------------------

/** Build a block list from [start, end] second-pairs (ratio-1.0 timescale). */
function blocksFrom(intervals: [number, number][]): SrtBlock[] {
  return intervals.map(([start, end], i) => ({
    index: i + 1,
    start,
    end,
    text: `line ${i + 1}`,
  }));
}

/** Build a 0/1 speech Float32Array with bursts at the given [start,end] seconds. */
function speechFrom(lengthS: number, bursts: [number, number][]): Float32Array {
  const sig = new Float32Array(Math.round(lengthS * SIGNAL_HZ));
  for (const [s, e] of bursts) {
    const s0 = Math.round(s * SIGNAL_HZ);
    const s1 = Math.round(e * SIGNAL_HZ);
    for (let i = s0; i < s1 && i < sig.length; i++) sig[i] = 1;
  }
  return sig;
}

// --- parseRatio ---------------------------------------------------------

describe("parseRatio", () => {
  it("parses a fraction with '/'", () => {
    expect(parseRatio("24/25")).toBeCloseTo(24 / 25, 12);
    expect(parseRatio("23.976/25")).toBeCloseTo(23.976 / 25, 12);
  });

  it("parses a plain number", () => {
    expect(parseRatio("1.0")).toBe(1.0);
    expect(parseRatio("0.95904")).toBeCloseTo(0.95904, 12);
  });
});

describe("DEFAULT_RATIOS", () => {
  it("includes the offset-only 1.0 candidate", () => {
    const offsetOnly = DEFAULT_RATIOS.find((r) => r.ratio === 1.0);
    expect(offsetOnly).toBeDefined();
    expect(offsetOnly!.label).toBe("1.0 (offset only)");
  });

  it("matches the Python DEFAULT_RATIOS set", () => {
    const byLabel = Object.fromEntries(
      DEFAULT_RATIOS.map((r) => [r.label, r.ratio]),
    );
    expect(byLabel["23.976/25"]).toBeCloseTo(23.976 / 25, 12);
    expect(byLabel["25/23.976"]).toBeCloseTo(25 / 23.976, 12);
    expect(byLabel["29.97/24"]).toBeCloseTo(29.97 / 24, 12);
    expect(DEFAULT_RATIOS).toHaveLength(11);
  });
});

// --- subtitleSignal -----------------------------------------------------

describe("subtitleSignal", () => {
  it("places 0/1 at the correct indices (ratio 1.0)", () => {
    const blocks = blocksFrom([[1.0, 2.0]]);
    const sig = subtitleSignal(blocks, 1.0, 500);
    // Python: signal[int(1*100):int(2*100)] = 1 -> indices 100..199
    expect(sig[99]).toBe(0);
    expect(sig[100]).toBe(1);
    expect(sig[199]).toBe(1);
    expect(sig[200]).toBe(0);
  });

  it("scales indices by the ratio", () => {
    const blocks = blocksFrom([[1.0, 2.0]]);
    const sig = subtitleSignal(blocks, 2.0, 500);
    // s0 = int(1*2*100)=200, s1=int(2*2*100)=400
    expect(sig[199]).toBe(0);
    expect(sig[200]).toBe(1);
    expect(sig[399]).toBe(1);
    expect(sig[400]).toBe(0);
  });

  it("clamps a block that extends past length", () => {
    const blocks = blocksFrom([[4.0, 10.0]]);
    const sig = subtitleSignal(blocks, 1.0, 500);
    // s0=400, s1=1000 -> clamp to 500
    expect(sig[399]).toBe(0);
    expect(sig[400]).toBe(1);
    expect(sig[499]).toBe(1);
    expect(sig.length).toBe(500);
  });

  it("skips a block whose start is past length", () => {
    const blocks = blocksFrom([[6.0, 7.0]]);
    const sig = subtitleSignal(blocks, 1.0, 500);
    expect(sig.every((v) => v === 0)).toBe(true);
  });
});

// --- bestOffsetForRatio -------------------------------------------------

describe("bestOffsetForRatio", () => {
  const bursts: [number, number][] = [
    [5.0, 6.0],
    [10.0, 11.0],
    [15.0, 16.0],
    [20.0, 21.0],
  ];

  it("recovers a POSITIVE offset (subtitles early by 2s)", () => {
    const speech = speechFrom(30, bursts);
    // Subtitles sit 2s BEFORE the real speech -> need +2.0s correction.
    const blocks = blocksFrom(bursts.map(([s, e]) => [s - 2.0, e - 2.0]));
    const { offset, score } = bestOffsetForRatio(speech, blocks, 1.0, 60);
    expect(offset).toBeCloseTo(2.0, 1);
    expect(Math.abs(offset - 2.0)).toBeLessThanOrEqual(1 / SIGNAL_HZ + 1e-9);
    expect(score).toBeGreaterThan(0.5);
  });

  it("recovers a NEGATIVE offset (subtitles late by 3s)", () => {
    const speech = speechFrom(30, bursts);
    // Subtitles sit 3s AFTER the real speech -> need -3.0s correction.
    const blocks = blocksFrom(bursts.map(([s, e]) => [s + 3.0, e + 3.0]));
    const { offset, score } = bestOffsetForRatio(speech, blocks, 1.0, 60);
    expect(offset).toBeCloseTo(-3.0, 1);
    expect(Math.abs(offset + 3.0)).toBeLessThanOrEqual(1 / SIGNAL_HZ + 1e-9);
    expect(score).toBeGreaterThan(0.5);
  });

  it("does not return an offset beyond the maxOffset window", () => {
    const speech = speechFrom(30, bursts);
    // True alignment needs +2.0s, but restrict the window to 1.0s.
    const blocks = blocksFrom(bursts.map(([s, e]) => [s - 2.0, e - 2.0]));
    const { offset } = bestOffsetForRatio(speech, blocks, 1.0, 1.0);
    expect(Math.abs(offset)).toBeLessThanOrEqual(1.0 + 1e-9);
  });
});

// --- analyze ------------------------------------------------------------

describe("analyze", () => {
  const bursts: [number, number][] = [
    [5.0, 6.0],
    [10.0, 11.0],
    [15.0, 16.0],
    [20.0, 21.0],
  ];

  it("picks ratio 1.0 with the right offset, confident, no warnings", () => {
    const speech = speechFrom(30, bursts);
    const blocks = blocksFrom(bursts.map(([s, e]) => [s - 2.0, e - 2.0]));
    // Use a well-separated candidate set. The DEFAULT_RATIOS include
    // 23.976/24 (0.999) and 24/23.976 (1.001) which are physically
    // indistinguishable from 1.0 on 30s of content (<30ms drift), so the
    // top-2 gap is always ambiguous there. Separated ratios isolate the
    // confidence logic being tested here.
    const ratios = [
      { label: "1.0 (offset only)", ratio: 1.0 },
      { label: "24/25", ratio: 24 / 25 },
      { label: "25/24", ratio: 25 / 24 },
      { label: "25/29.97", ratio: 25 / 29.97 },
      { label: "29.97/25", ratio: 29.97 / 25 },
    ];
    const result = analyze(speech, blocks, { ratios });
    expect(result.best.ratio).toBe(1.0);
    expect(result.best.offset).toBeCloseTo(2.0, 1);
    expect(result.confident).toBe(true);
    expect(result.warnings).toHaveLength(0);
    expect(result.all.length).toBe(ratios.length);
  });

  it("runs the full DEFAULT_RATIOS set by default", () => {
    const speech = speechFrom(30, bursts);
    const blocks = blocksFrom(bursts.map(([s, e]) => [s - 2.0, e - 2.0]));
    const result = analyze(speech, blocks);
    expect(result.best.ratio).toBe(1.0);
    expect(result.all.length).toBe(DEFAULT_RATIOS.length);
  });

  it("flags low confidence when there is no usable signal", () => {
    const speech = new Float32Array(3000); // all zeros -> no speech
    const blocks = blocksFrom(bursts);
    const result = analyze(speech, blocks);
    expect(result.confident).toBe(false);
    expect(result.warnings.length).toBeGreaterThan(0);
    expect(result.warnings.some((w) => /confidence/i.test(w))).toBe(true);
  });
});

// --- applyCorrection ----------------------------------------------------

describe("applyCorrection", () => {
  it("maps start/end by t*ratio + offset", () => {
    const blocks = blocksFrom([
      [10.0, 12.0],
      [20.0, 22.5],
    ]);
    const out = applyCorrection(blocks, 0.96, 1.5);
    expect(out[0].start).toBeCloseTo(10.0 * 0.96 + 1.5, 9);
    expect(out[0].end).toBeCloseTo(12.0 * 0.96 + 1.5, 9);
    expect(out[1].start).toBeCloseTo(20.0 * 0.96 + 1.5, 9);
    expect(out[1].end).toBeCloseTo(22.5 * 0.96 + 1.5, 9);
    expect(out[0].index).toBe(1);
    expect(out[0].text).toBe("line 1");
  });

  it("does not mutate the input blocks", () => {
    const blocks = blocksFrom([[10.0, 12.0]]);
    const snapshot = { ...blocks[0] };
    applyCorrection(blocks, 2.0, 5.0);
    expect(blocks[0]).toEqual(snapshot);
  });
});
