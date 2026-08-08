// Entry point for the Jellyfin plugin's browser bundle.
//
// This module is deliberately thin. It re-exports the algorithm from `lib/` -
// the single source of truth the golden parity test covers - plus the plugin's
// own PCM-to-VAD adapter, and nothing else. esbuild rolls the whole graph into
// one self-contained IIFE (`jellyfin-plugin/web/build.mjs`) that is embedded in
// the C# assembly and served by the plugin.
//
// The sync page UI (#12) is a separate file that consumes `window.SubtitleSync`.
// Everything the UI needs must be exported here, because nothing else from the
// bundle is reachable from the page.

export {
  parseSrt,
  writeSrt,
  srtTimeToSeconds,
  secondsToSrtTime,
} from "../../../lib/srt";

export {
  analyze,
  applyCorrection,
  subtitleSignal,
  bestOffsetForRatio,
  parseRatio,
  DEFAULT_RATIOS,
} from "../../../lib/sync";

// Pure frame->signal fill. Importing it does NOT pull ffmpeg.wasm in: the
// bundler stubs `@ffmpeg/*` (see build.mjs), because the plugin gets its PCM
// from the server's ffmpeg rather than decoding in the browser.
export { assembleSpeechSignal } from "../../../lib/audio";

export { SR, FRAME_MS, SIGNAL_HZ } from "../../../lib/types";
export type {
  SrtBlock,
  SpeechSignal,
  RatioCandidate,
  RatioResult,
  SyncOptions,
  SyncResult,
} from "../../../lib/types";

export {
  iteratePcmFrames,
  runVadOverPcmStream,
  speechSignalFromPcmStream,
  createFvadFrameVad,
} from "./pcmStream";
export type {
  FrameVad,
  FrameVadSource,
  PcmStreamOptions,
  PcmStreamProgress,
} from "./pcmStream";

/**
 * Build stamp, injected by esbuild via `define`.
 *
 * Cheap proof that the served bundle is the one that was just built: the sync
 * page and the smoke tests can read `window.SubtitleSync.BUILD` rather than
 * guessing whether the browser cached an older copy.
 */
declare const __SUBTITLE_SYNC_BUILD__: string;
export const BUILD: string = __SUBTITLE_SYNC_BUILD__;
