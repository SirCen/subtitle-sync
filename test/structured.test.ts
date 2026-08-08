// Recovery test: proves `analyze` recovers a KNOWN ratio and offset from a
// fixture whose correct answer is fixed by construction.
//
// This is the complement to golden.test.ts. That test pins the TypeScript port
// to the Python reference - it proves the two agree, not that either is right.
// This one proves the algorithm actually finds the truth, because the truth was
// built in: test/oracle/gen_structured_fixture.mjs mutes deliberate gaps in the
// fixture's audio, writes one cue per surviving speech region, then emits
// out-of-sync copies displaced by a ratio and offset it records.
//
// It exists because the original Sample Clip cannot support this assertion: the
// VAD flags ~92% of it as speech, leaving no speech/silence structure to
// correlate against, so `analyze` misses by about 8 s on it. Issue #20.
//
// Like golden.test.ts it reads committed artifacts only - no ffmpeg, no WASM,
// no Python at test time. Regenerate with:
//   node test/oracle/gen_structured_fixture.mjs

import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

import { parseSrt } from "../lib/srt";
import { analyze, applyCorrection } from "../lib/sync";
import { SIGNAL_HZ } from "../lib/types";

const here = dirname(fileURLToPath(import.meta.url));
const fixtures = join(here, "fixtures");

interface StructuredExpected {
  meta: {
    signalHz: number;
    signalLength: number;
    durationS: number;
    maxOffsetS: number;
    speechRatio: number;
  };
  speechRegions: [number, number][];
  speechRuns: [number, number][];
  alignedSrt: string;
  cases: {
    name: string;
    srt: string;
    label: string;
    ratio: number;
    offset: number;
  }[];
}

const expected: StructuredExpected = JSON.parse(
  readFileSync(join(fixtures, "structured_expected.json"), "utf-8"),
);

const rawSignal = JSON.parse(
  readFileSync(join(fixtures, "structured_speech_signal.json"), "utf-8"),
) as { signalHz: number; length: number; signal: number[] };

const speech = Float32Array.from(rawSignal.signal);

const MAX_OFFSET_S = expected.meta.maxOffsetS;

// The search resolves to one SIGNAL_HZ step (0.01 s) and SRT timestamps round
// to 1 ms, so one step is the tightest honest tolerance. Both cases currently
// land dead on their constructed offset; this leaves room for the rescaled
// case's millisecond rounding without leaving room for a regression. For scale,
// the old Sample Clip fixture missed by about 8 s.
const OFFSET_TOL_S = 1 / SIGNAL_HZ + 1e-9;

// Neighbouring candidate ratios differ by ~0.1% (1.0 vs 24/23.976), which over
// a 30 s clip is 30 ms of drift - below what a 10 ms signal can separate. So
// assert the ratio to 0.5% relative rather than demanding an exact label, and
// let the end-to-end check below (corrected timings) carry the real weight.
const RATIO_REL_TOL = 0.005;

// The corrected cue boundaries land within one VAD frame of the aligned truth.
const CUE_TOL_S = 0.05;

describe("known-answer recovery on the structured fixture", () => {
  it("the fixture has real speech/silence structure for the VAD to find", () => {
    expect(rawSignal.signalHz).toBe(SIGNAL_HZ);
    expect(rawSignal.signal.length).toBe(rawSignal.length);
    expect(speech.length).toBe(expected.meta.signalLength);

    // The whole point of the fixture. The original Sample Clip sits at ~0.92,
    // which is what makes it unusable for a known-answer assertion. Anything
    // approaching that here means the mute filter stopped working.
    const measured =
      speech.reduce((s: number, v: number) => s + v, 0) / speech.length;
    expect(measured).toBeCloseTo(expected.meta.speechRatio, 3);
    expect(measured).toBeGreaterThan(0.25);
    expect(measured).toBeLessThan(0.65);
  });

  it("the VAD runs are one per muted region, close to where they were cut", () => {
    expect(expected.speechRuns).toHaveLength(expected.speechRegions.length);
    expected.speechRuns.forEach(([start, end], i) => {
      const [wantStart, wantEnd] = expected.speechRegions[i];
      // The VAD decides per 30 ms frame and has a trailing hangover, so a run
      // reaches a little past the region that produced it. A quarter-second is
      // generous for that and still catches a mute schedule gone wrong.
      expect(Math.abs(start - wantStart)).toBeLessThan(0.25);
      expect(Math.abs(end - wantEnd)).toBeLessThan(0.25);
    });
  });

  it("the aligned SRT is the correct track for this audio", () => {
    const aligned = parseSrt(
      readFileSync(join(fixtures, expected.alignedSrt), "utf-8"),
    );
    expect(aligned).toHaveLength(expected.speechRuns.length);
    aligned.forEach((b, i) => {
      expect(b.start).toBeCloseTo(expected.speechRuns[i][0], 3);
      expect(b.end).toBeCloseTo(expected.speechRuns[i][1], 3);
    });

    // Sanity: an already-correct track needs no correction.
    const result = analyze(speech, aligned, { maxOffset: MAX_OFFSET_S });
    expect(Math.abs(result.best.offset)).toBeLessThanOrEqual(OFFSET_TOL_S);
    expect(result.best.score).toBeGreaterThan(0.9);
  });

  for (const c of expected.cases) {
    describe(`case: ${c.name}`, () => {
      const blocks = parseSrt(readFileSync(join(fixtures, c.srt), "utf-8"));
      const result = analyze(speech, blocks, { maxOffset: MAX_OFFSET_S });

      it("recovers the ratio it was displaced by", () => {
        expect(Math.abs(result.best.ratio / c.ratio - 1)).toBeLessThanOrEqual(
          RATIO_REL_TOL,
        );
      });

      it("recovers the offset it was displaced by", () => {
        expect(Math.abs(result.best.offset - c.offset)).toBeLessThanOrEqual(
          OFFSET_TOL_S,
        );
      });

      it("scores high, and never raises the low-confidence warning", () => {
        expect(result.best.score).toBeGreaterThan(0.85);
        // `confident` is false here and that is correct, not a defect: the
        // runner-up is a ratio 0.1% away, which a 30 s clip cannot separate,
        // so analyze() flags the top two as close. What must never appear is
        // the low-confidence warning - that would mean no real peak was found.
        expect(result.warnings).not.toContain(
          expect.stringContaining("Confidence score is very low"),
        );
      });

      it("correcting by the recovered answer restores the aligned timings", () => {
        // The assertion that matters end to end, and the one that stays valid
        // whichever of the near-identical ratios wins: applying the recovered
        // ratio and offset must put every cue back on its speech run.
        const corrected = applyCorrection(
          blocks,
          result.best.ratio,
          result.best.offset,
        );
        expect(corrected).toHaveLength(expected.speechRuns.length);
        corrected.forEach((b, i) => {
          const [start, end] = expected.speechRuns[i];
          expect(Math.abs(b.start - start)).toBeLessThanOrEqual(CUE_TOL_S);
          expect(Math.abs(b.end - end)).toBeLessThanOrEqual(CUE_TOL_S);
        });
      });
    });
  }

  it("the ratio case is genuinely a ratio problem, not a disguised offset", () => {
    // Guards the second case's reason for existing. If a pure offset scored as
    // well as the rescale, the fixture would not be exercising ratio search.
    const ratioCase = expected.cases.find((c) => c.name === "ratio")!;
    const blocks = parseSrt(readFileSync(join(fixtures, ratioCase.srt), "utf-8"));
    const result = analyze(speech, blocks, { maxOffset: MAX_OFFSET_S });
    const offsetOnly = result.all.find((r) => r.ratio === 1.0)!;
    expect(result.best.score - offsetOnly.score).toBeGreaterThan(0.2);
  });
});
