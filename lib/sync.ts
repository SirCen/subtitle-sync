// Signal generation, cross-correlation offset search, ratio selection,
// confidence/warning logic, and SRT correction math.
//
// Ported from reference/sync_srt.py. Numerical parity with that reference
// is intentional (a later golden test compares against it).
//
// Pure TypeScript: runs in both Node (vitest) and the browser. No Node-only
// APIs. The only external dependency is fft.js for the FFT-based convolution.

import FFT from "fft.js";
import type {
  SrtBlock,
  SpeechSignal,
  RatioCandidate,
  RatioResult,
  SyncOptions,
  SyncResult,
} from "./types";
import { SIGNAL_HZ } from "./types";

// ---------------------------------------------------------------------------
// Candidate ratios (mirror Python's DEFAULT_RATIOS, preserving order)
// ---------------------------------------------------------------------------

export const DEFAULT_RATIOS: RatioCandidate[] = [
  { label: "1.0 (offset only)", ratio: 1.0 },
  { label: "23.976/25", ratio: 23.976 / 25 },
  { label: "25/23.976", ratio: 25 / 23.976 },
  { label: "24/25", ratio: 24 / 25 },
  { label: "25/24", ratio: 25 / 24 },
  { label: "23.976/24", ratio: 23.976 / 24 },
  { label: "24/23.976", ratio: 24 / 23.976 },
  { label: "25/29.97", ratio: 25 / 29.97 },
  { label: "29.97/25", ratio: 29.97 / 25 },
  { label: "24/29.97", ratio: 24 / 29.97 },
  { label: "29.97/24", ratio: 29.97 / 24 },
];

/** Port of parse_ratio_arg: "23.976/25" -> division; plain number otherwise. */
export function parseRatio(s: string): number {
  if (s.includes("/")) {
    const [num, den] = s.split("/");
    return parseFloat(num) / parseFloat(den);
  }
  return parseFloat(s);
}

// ---------------------------------------------------------------------------
// Subtitle signal
// ---------------------------------------------------------------------------

/**
 * Port of subtitle_signal. Builds a 0/1 signal at SIGNAL_HZ from the subtitle
 * intervals, rescaled by `ratio`, clamped to [0, length).
 */
export function subtitleSignal(
  blocks: SrtBlock[],
  ratio: number,
  length: number,
): Float32Array {
  const signal = new Float32Array(length);
  for (const b of blocks) {
    const s0 = Math.trunc(b.start * ratio * SIGNAL_HZ);
    const s1 = Math.trunc(b.end * ratio * SIGNAL_HZ);
    if (s0 >= length) continue;
    const lo = Math.max(s0, 0);
    const hi = Math.min(s1, length);
    for (let i = lo; i < hi; i++) signal[i] = 1.0;
  }
  return signal;
}

// ---------------------------------------------------------------------------
// FFT helpers
// ---------------------------------------------------------------------------

function nextPow2(n: number): number {
  let p = 1;
  while (p < n) p *= 2;
  return p;
}

/**
 * Full linear convolution of two real sequences via FFT, equivalent to
 * numpy/scipy full-mode convolution. Returns a Float64Array of length
 * a.length + c.length - 1.
 */
function fftConvolveFull(
  a: ArrayLike<number>,
  c: ArrayLike<number>,
): Float64Array {
  const la = a.length;
  const lc = c.length;
  const n = la + lc - 1;
  const size = nextPow2(n);

  const fft = new FFT(size);
  const fa = fft.createComplexArray();
  const fc = fft.createComplexArray();
  for (let i = 0; i < la; i++) fa[2 * i] = a[i];
  for (let i = 0; i < lc; i++) fc[2 * i] = c[i];

  const outA = fft.createComplexArray();
  const outC = fft.createComplexArray();
  fft.transform(outA, fa);
  fft.transform(outC, fc);

  // Pointwise complex multiply: outA .* outC
  const prod = fft.createComplexArray();
  for (let i = 0; i < size; i++) {
    const ar = outA[2 * i];
    const ai = outA[2 * i + 1];
    const cr = outC[2 * i];
    const ci = outC[2 * i + 1];
    prod[2 * i] = ar * cr - ai * ci;
    prod[2 * i + 1] = ar * ci + ai * cr;
  }

  const inv = fft.createComplexArray();
  fft.inverseTransform(inv, prod); // inverseTransform divides by size

  const result = new Float64Array(n);
  for (let i = 0; i < n; i++) result[i] = inv[2 * i];
  return result;
}

// ---------------------------------------------------------------------------
// Cross-correlation search
// ---------------------------------------------------------------------------

function mean(x: ArrayLike<number>): number {
  let s = 0;
  for (let i = 0; i < x.length; i++) s += x[i];
  return x.length ? s / x.length : 0;
}

function l2norm(x: ArrayLike<number>): number {
  let s = 0;
  for (let i = 0; i < x.length; i++) s += x[i] * x[i];
  return Math.sqrt(s);
}

/**
 * Port of best_offset_for_ratio. Zero-means both signals, computes the full
 * cross-correlation (equivalent to scipy fftconvolve(a, b[::-1], "full")),
 * restricts the search to +/- maxOffset around the zero-lag center, and
 * returns the best offset (seconds) and a normalized score.
 *
 * Lag convention matches Python: corr[k] corresponds to lag = k - (len(b)-1),
 * and offsetSeconds = bestLag / SIGNAL_HZ. A positive offset means the
 * subtitle signal must be shifted later (subtitles are early); this is the
 * value added directly in `t*ratio + offset`.
 */
export function bestOffsetForRatio(
  speech: SpeechSignal,
  blocks: SrtBlock[],
  ratio: number,
  maxOffsetS: number,
): { offset: number; score: number } {
  const subSig = subtitleSignal(blocks, ratio, speech.length);

  const aMean = mean(speech);
  const bMean = mean(subSig);
  const a = new Float64Array(speech.length);
  const b = new Float64Array(subSig.length);
  for (let i = 0; i < a.length; i++) a[i] = speech[i] - aMean;
  for (let i = 0; i < b.length; i++) b[i] = subSig[i] - bMean;

  // b reversed
  const bRev = new Float64Array(b.length);
  for (let i = 0; i < b.length; i++) bRev[i] = b[b.length - 1 - i];

  const corr = fftConvolveFull(a, bRev);
  // lags run from -(len(b)-1) .. len(a)-1; center (lag 0) is at index len(b)-1
  const center = b.length - 1;
  const maxLagSteps = Math.trunc(maxOffsetS * SIGNAL_HZ);
  const lo = Math.max(0, center - maxLagSteps);
  const hi = Math.min(corr.length, center + maxLagSteps);

  let bestIdx = lo;
  let bestVal = -Infinity;
  for (let i = lo; i < hi; i++) {
    if (corr[i] > bestVal) {
      bestVal = corr[i];
      bestIdx = i;
    }
  }
  const bestLag = bestIdx - center;

  const denom = l2norm(a) * l2norm(b) || 1.0;
  const normScore = bestVal / denom;

  return { offset: bestLag / SIGNAL_HZ, score: normScore };
}

// ---------------------------------------------------------------------------
// Analysis: run all ratios, rank, compute confidence/warnings
// ---------------------------------------------------------------------------

const LOW_CONFIDENCE_WARNING =
  "Confidence score is very low. The audio may have little dialogue, the SRT " +
  "may not match this video at all, or VAD settings need adjusting.";

const AMBIGUOUS_TOP2_NOTE =
  "The top two candidates scored similarly — worth double-checking the result.";

export function analyze(
  speech: SpeechSignal,
  blocks: SrtBlock[],
  options?: Partial<SyncOptions>,
): SyncResult {
  const maxOffset = options?.maxOffset ?? 120;
  const ratios = options?.ratios ?? DEFAULT_RATIOS;

  const all: RatioResult[] = ratios.map(({ label, ratio }) => {
    const { offset, score } = bestOffsetForRatio(speech, blocks, ratio, maxOffset);
    return { label, ratio, offset, score };
  });

  all.sort((x, y) => y.score - x.score);

  const best = all[0];
  const runnerUp = all.length > 1 ? all[1] : undefined;

  const warnings: string[] = [];
  if (best.score < 0.05) {
    warnings.push(LOW_CONFIDENCE_WARNING);
  } else if (
    runnerUp &&
    runnerUp.score > 0 &&
    (best.score - runnerUp.score) / best.score < 0.15
  ) {
    warnings.push(AMBIGUOUS_TOP2_NOTE);
  }

  const confident =
    best.score >= 0.05 &&
    (!runnerUp || (best.score - runnerUp.score) / best.score >= 0.15);

  return { best, runnerUp, all, confident, warnings };
}

// ---------------------------------------------------------------------------
// Correction math
// ---------------------------------------------------------------------------

/**
 * Port of the final correction loop: map each block's start/end by
 * `t * ratio + offset`. Returns new blocks; input is not mutated.
 */
export function applyCorrection(
  blocks: SrtBlock[],
  ratio: number,
  offset: number,
): SrtBlock[] {
  return blocks.map((b) => ({
    index: b.index,
    start: b.start * ratio + offset,
    end: b.end * ratio + offset,
    text: b.text,
  }));
}
