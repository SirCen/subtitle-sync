// Shared contracts for the subtitle-sync pipeline.
// These types are the coordination boundary between the pure-logic modules
// (lib/srt.ts, lib/sync.ts) and the browser-integration module (lib/audio.ts).
//
// Reference implementation: reference/sync_srt.py

/** A single subtitle entry. Times are in seconds (wall-clock). */
export interface SrtBlock {
  index: number;
  start: number; // seconds
  end: number; // seconds
  text: string;
}

// --- Signal / VAD constants (mirror sync_srt.py) ---
export const SR = 16000; // sample rate required by WebRTC VAD
export const FRAME_MS = 30; // WebRTC VAD frame size
export const SIGNAL_HZ = 100; // resolution of the comparison signal (10ms steps)

/**
 * A 0/1 speech-activity signal sampled at SIGNAL_HZ.
 * Produced by lib/audio.ts from the video; consumed by lib/sync.ts.
 */
export type SpeechSignal = Float32Array;

/** One candidate framerate ratio, keyed by a human label (e.g. "23.976/25"). */
export interface RatioCandidate {
  label: string;
  ratio: number;
}

/** Result of testing one ratio against the speech signal. */
export interface RatioResult {
  label: string;
  ratio: number;
  offset: number; // seconds
  score: number; // ~correlation coefficient
}

/** Options controlling the sync search (surfaced via the Advanced panel). */
export interface SyncOptions {
  maxOffset: number; // seconds; default 120
  vadAggressiveness: 0 | 1 | 2 | 3; // default 2
  ratios?: RatioCandidate[]; // default DEFAULT_RATIOS
}

/** Final analysis outcome. */
export interface SyncResult {
  best: RatioResult;
  runnerUp?: RatioResult;
  all: RatioResult[];
  /** True when confidence is high enough to auto-download. */
  confident: boolean;
  /** Human-readable warnings (low confidence, ambiguous top-2, etc.). */
  warnings: string[];
}
