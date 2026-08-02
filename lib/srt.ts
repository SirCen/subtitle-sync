// SRT parsing / writing + time-conversion logic.
// Ported from reference/sync_srt.py to run in the browser (no filesystem).
// Times are in seconds (wall-clock). See lib/types.ts for the SrtBlock contract.

import type { SrtBlock } from "./types";

// Matches HH:MM:SS,mmm or HH:MM:SS.mmm — comma or dot millisecond separator.
const SRT_TIME_RE = /(\d{2}):(\d{2}):(\d{2})[,.](\d{3})/;

// A start --> end timestamp pair on a cue line.
const SRT_RANGE_RE =
  /(\d{2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}[,.]\d{3})/;

/** Parse a single "HH:MM:SS,mmm" (or ".mmm") timestamp into seconds. */
export function srtTimeToSeconds(t: string): number {
  const m = SRT_TIME_RE.exec(t);
  if (!m) {
    throw new Error(`Invalid SRT timestamp: ${JSON.stringify(t)}`);
  }
  const [, h, min, s, ms] = m;
  return (
    parseInt(h, 10) * 3600 +
    parseInt(min, 10) * 60 +
    parseInt(s, 10) +
    parseInt(ms, 10) / 1000
  );
}

/** Format seconds as "HH:MM:SS,mmm". Negatives clamp to zero. */
export function secondsToSrtTime(sec: number): string {
  if (sec < 0) {
    sec = 0;
  }
  let h = Math.floor(sec / 3600);
  sec -= h * 3600;
  let m = Math.floor(sec / 60);
  sec -= m * 60;
  let s = Math.floor(sec);
  let ms = Math.round((sec - s) * 1000);
  // Rounding can push ms to 1000 — cascade the carry up through s/m/h.
  if (ms === 1000) {
    ms = 0;
    s += 1;
    if (s === 60) {
      s = 0;
      m += 1;
      if (m === 60) {
        m = 0;
        h += 1;
      }
    }
  }
  return `${pad2(h)}:${pad2(m)}:${pad2(s)},${pad3(ms)}`;
}

/**
 * Parse SRT file TEXT into blocks, re-indexed 1..N.
 * Mirrors parse_srt from the Python reference:
 *  - strips a leading UTF-8 BOM (Python read as utf-8-sig)
 *  - splits on blank lines, tolerating \r\n and surrounding whitespace
 *  - first non-blank line may or may not be a numeric index
 *  - accepts `,` or `.` millisecond separators
 * Throws if zero blocks parse (Python did sys.exit).
 */
export function parseSrt(raw: string): SrtBlock[] {
  // Strip UTF-8 BOM if present.
  if (raw.charCodeAt(0) === 0xfeff) {
    raw = raw.slice(1);
  }
  // Normalize CRLF/CR to LF so blank-line splitting and text join behave.
  const normalized = raw.replace(/\r\n/g, "\n").replace(/\r/g, "\n");

  const blocks: SrtBlock[] = [];
  // Split on blank lines (a newline followed by optional whitespace + newline).
  const chunks = normalized.trim().split(/\n\s*\n/);

  for (const chunk of chunks) {
    const lines = chunk.split("\n").filter((l) => l.trim() !== "");
    if (lines.length < 2) {
      continue;
    }
    // First line may or may not be a numeric index; the cue line has "-->".
    let idxLine = 0;
    if (!lines[0].includes("-->")) {
      idxLine = 1;
    }
    const m = SRT_RANGE_RE.exec(lines[idxLine]);
    if (!m) {
      continue;
    }
    const start = srtTimeToSeconds(m[1]);
    const end = srtTimeToSeconds(m[2]);
    const text = lines.slice(idxLine + 1).join("\n");
    blocks.push({ index: blocks.length + 1, start, end, text });
  }

  if (blocks.length === 0) {
    throw new Error("Could not parse any subtitle entries from the input.");
  }
  return blocks;
}

/**
 * Serialize blocks to SRT text. Mirrors write_srt: each block is
 * `index\nHH:MM:SS,mmm --> HH:MM:SS,mmm\ntext\n\n` (trailing blank line).
 */
export function writeSrt(blocks: SrtBlock[]): string {
  let out = "";
  for (const b of blocks) {
    out += `${b.index}\n`;
    out += `${secondsToSrtTime(b.start)} --> ${secondsToSrtTime(b.end)}\n`;
    out += `${b.text}\n\n`;
  }
  return out;
}

function pad2(n: number): string {
  return String(n).padStart(2, "0");
}

function pad3(n: number): string {
  return String(n).padStart(3, "0");
}
