// The browser half of the speech-signal cache wire format (#9).
//
// The server's `SpeechSignalCodec` (C#) is the specification; this is the
// mirror the page needs to read a cache hit and to contribute one back. The two
// are pinned to each other by `signalCodec.test.ts`, which encodes a signal here
// and asserts the byte layout the C# tests assert on.
//
//   0..3    magic, ASCII "SSC1"
//   4..7    uint32 LE sample count
//   8..11   uint32 LE CRC-32/ISO-HDLC of the packed body
//   12..    packed body, ceil(sampleCount / 8) bytes, LSB first
//
// An hour of runtime is 360,000 samples at SIGNAL_HZ: 45 KB packed, against the
// ~115 MB of PCM it was derived from. That difference is the whole point of
// checking the cache before pulling audio.

import type { SpeechSignal } from "../../../lib/types";

/** Fixed header length in bytes: magic, sample count, checksum. */
export const SIGNAL_HEADER_LENGTH = 12;

/** Twenty-four hours at 100 Hz. The server refuses anything longer. */
export const SIGNAL_MAX_SAMPLE_COUNT = 8_640_000;

const MAGIC = [0x53, 0x53, 0x43, 0x31]; // "SSC1"

/** Why an envelope was rejected. Mirrors the server's `SignalPayloadError`. */
export type SignalPayloadError =
  | "TooShort"
  | "BadMagic"
  | "SampleCountOutOfRange"
  | "LengthMismatch"
  | "PaddingNotZero"
  | "ChecksumMismatch";

/**
 * Thrown by {@link decodeSpeechSignal} rather than returning a best effort.
 *
 * A signal that has silently lost its tail does not look wrong: it produces a
 * confidently incorrect offset, which is a far worse failure than an error.
 */
export class SignalPayloadRejected extends Error {
  constructor(public readonly reason: SignalPayloadError) {
    super(`Speech signal payload rejected: ${reason}.`);
    this.name = "SignalPayloadRejected";
  }
}

const CRC32_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let entry = i;
    for (let bit = 0; bit < 8; bit++) {
      entry = entry & 1 ? (entry >>> 1) ^ 0xedb88320 : entry >>> 1;
    }
    table[i] = entry >>> 0;
  }
  return table;
})();

/** CRC-32/ISO-HDLC, the variant gzip and zip use. */
export function crc32(data: Uint8Array): number {
  let crc = 0xffffffff;
  for (let i = 0; i < data.length; i++) {
    crc = CRC32_TABLE[(crc ^ data[i]) & 0xff] ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

/** Bytes needed to hold a given number of one-bit samples. */
export function packedLength(sampleCount: number): number {
  return (sampleCount + 7) >> 3;
}

/**
 * Wraps a speech signal in the envelope the cache endpoint accepts.
 *
 * Any non-zero sample is speech. `assembleSpeechSignal` only ever emits 0 and 1,
 * but nothing about a Float32Array enforces that and mapping an unexpected value
 * to silence would be a silent data change.
 */
export function encodeSpeechSignal(signal: SpeechSignal): Uint8Array {
  if (signal.length > SIGNAL_MAX_SAMPLE_COUNT) {
    throw new RangeError(
      `A signal of ${signal.length} samples exceeds the cap of ${SIGNAL_MAX_SAMPLE_COUNT}.`,
    );
  }

  const body = new Uint8Array(packedLength(signal.length));
  for (let i = 0; i < signal.length; i++) {
    if (signal[i] !== 0) {
      body[i >> 3] |= 1 << (i & 7);
    }
  }

  const envelope = new Uint8Array(SIGNAL_HEADER_LENGTH + body.length);
  envelope.set(MAGIC, 0);
  const view = new DataView(envelope.buffer);
  view.setUint32(4, signal.length, true);
  view.setUint32(8, crc32(body), true);
  envelope.set(body, SIGNAL_HEADER_LENGTH);
  return envelope;
}

/**
 * Checks an envelope end to end without allocating anything sized by its
 * contents. Returns null when it is well formed.
 */
export function validateSpeechSignal(envelope: Uint8Array): SignalPayloadError | null {
  if (envelope.length < SIGNAL_HEADER_LENGTH) return "TooShort";

  for (let i = 0; i < MAGIC.length; i++) {
    if (envelope[i] !== MAGIC[i]) return "BadMagic";
  }

  const view = new DataView(envelope.buffer, envelope.byteOffset, envelope.byteLength);
  const declared = view.getUint32(4, true);
  if (declared > SIGNAL_MAX_SAMPLE_COUNT) return "SampleCountOutOfRange";

  const body = envelope.subarray(SIGNAL_HEADER_LENGTH);
  if (body.length !== packedLength(declared)) return "LengthMismatch";

  const usedBitsInLastByte = declared & 7;
  if (usedBitsInLastByte !== 0 && body[body.length - 1] >>> usedBitsInLastByte !== 0) {
    return "PaddingNotZero";
  }

  if (crc32(body) !== view.getUint32(8, true)) return "ChecksumMismatch";

  return null;
}

/**
 * Unwraps an envelope into the signal `analyze()` consumes.
 *
 * @throws {SignalPayloadRejected} if anything about the envelope is wrong.
 */
export function decodeSpeechSignal(envelope: Uint8Array): SpeechSignal {
  const error = validateSpeechSignal(envelope);
  if (error) throw new SignalPayloadRejected(error);

  const view = new DataView(envelope.buffer, envelope.byteOffset, envelope.byteLength);
  const sampleCount = view.getUint32(4, true);
  const body = envelope.subarray(SIGNAL_HEADER_LENGTH);

  const signal = new Float32Array(sampleCount);
  for (let i = 0; i < sampleCount; i++) {
    signal[i] = (body[i >> 3] >> (i & 7)) & 1;
  }
  return signal;
}
