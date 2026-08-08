// Pins the browser codec to the server's `SpeechSignalCodec`.
//
// The two implementations never meet in a unit test, so what keeps them honest
// is asserting the same byte layout and the same published CRC check value that
// `SpeechSignalCodecTests` asserts on the C# side. If either drifts, a cache hit
// would decode into a plausible but wrong signal - which produces a confidently
// incorrect sync rather than an error, so it has to be caught here.

import { describe, expect, it } from "vitest";

import {
  crc32,
  decodeSpeechSignal,
  encodeSpeechSignal,
  packedLength,
  SIGNAL_HEADER_LENGTH,
  SignalPayloadRejected,
  validateSpeechSignal,
} from "./signalCodec";

function signalOf(bits: number[]): Float32Array {
  return Float32Array.from(bits);
}

describe("crc32", () => {
  it("matches the published CRC-32/ISO-HDLC check value", () => {
    const input = new TextEncoder().encode("123456789");
    expect(crc32(input)).toBe(0xcbf43926);
  });

  it("is zero for no input", () => {
    expect(crc32(new Uint8Array(0))).toBe(0);
  });
});

describe("packedLength", () => {
  it("rounds up to whole bytes", () => {
    expect(packedLength(0)).toBe(0);
    expect(packedLength(1)).toBe(1);
    expect(packedLength(8)).toBe(1);
    expect(packedLength(9)).toBe(2);
  });
});

describe("encodeSpeechSignal", () => {
  it("writes the SSC1 header the server validates", () => {
    const envelope = encodeSpeechSignal(signalOf([1, 0, 1, 1]));

    expect(Array.from(envelope.subarray(0, 4))).toEqual([0x53, 0x53, 0x43, 0x31]);

    const view = new DataView(envelope.buffer);
    expect(view.getUint32(4, true)).toBe(4);
    expect(envelope.length).toBe(SIGNAL_HEADER_LENGTH + 1);
  });

  it("packs least significant bit first", () => {
    // 1,0,1,1 -> bits 0,2,3 -> 0b00001101 = 13.
    const envelope = encodeSpeechSignal(signalOf([1, 0, 1, 1]));
    expect(envelope[SIGNAL_HEADER_LENGTH]).toBe(0b00001101);
  });

  it("leaves the unused bits of the final byte zero", () => {
    const envelope = encodeSpeechSignal(signalOf([1, 1, 1]));
    expect(envelope[SIGNAL_HEADER_LENGTH] >>> 3).toBe(0);
    expect(validateSpeechSignal(envelope)).toBeNull();
  });

  it("treats any non-zero sample as speech", () => {
    const envelope = encodeSpeechSignal(signalOf([2, 0, -1, 0.5]));
    expect(envelope[SIGNAL_HEADER_LENGTH]).toBe(0b00001101);
  });

  it("checksums the body, not the header", () => {
    const envelope = encodeSpeechSignal(signalOf([1, 0, 1, 1]));
    const view = new DataView(envelope.buffer);
    expect(view.getUint32(8, true)).toBe(crc32(envelope.subarray(SIGNAL_HEADER_LENGTH)));
  });

  it("refuses a signal past the cap", () => {
    // Nothing is allocated at that size: the length check comes first.
    const oversize = { length: 8_640_001 } as unknown as Float32Array;
    expect(() => encodeSpeechSignal(oversize)).toThrow(RangeError);
  });
});

describe("round trip", () => {
  it("recovers the exact samples", () => {
    const bits = Array.from({ length: 1000 }, (_, i) => (i % 7 === 0 ? 1 : 0));
    const decoded = decodeSpeechSignal(encodeSpeechSignal(signalOf(bits)));

    expect(decoded.length).toBe(bits.length);
    expect(Array.from(decoded)).toEqual(bits);
  });

  it("handles the empty signal", () => {
    const decoded = decodeSpeechSignal(encodeSpeechSignal(new Float32Array(0)));
    expect(decoded.length).toBe(0);
  });

  it("survives a non-zero byteOffset, which fetch buffers often have", () => {
    const envelope = encodeSpeechSignal(signalOf([1, 0, 1, 1]));
    const backing = new Uint8Array(envelope.length + 3);
    backing.set(envelope, 3);
    const offsetView = backing.subarray(3);

    expect(Array.from(decodeSpeechSignal(offsetView))).toEqual([1, 0, 1, 1]);
  });
});

describe("validateSpeechSignal", () => {
  it("rejects a truncated header", () => {
    expect(validateSpeechSignal(new Uint8Array(11))).toBe("TooShort");
  });

  it("rejects a payload from some other producer", () => {
    // An HTML error page from a misconfigured proxy is the realistic case.
    const html = new TextEncoder().encode("<!doctype html><html>oh no</html>");
    expect(validateSpeechSignal(html)).toBe("BadMagic");
  });

  it("rejects a body that does not match the declared count", () => {
    const envelope = encodeSpeechSignal(signalOf([1, 0, 1, 1]));
    expect(validateSpeechSignal(envelope.subarray(0, envelope.length - 1))).toBe(
      "LengthMismatch",
    );
  });

  it("rejects a flipped bit in the body", () => {
    const envelope = encodeSpeechSignal(signalOf([1, 0, 1, 1, 0, 0, 0, 0, 1]));
    envelope[SIGNAL_HEADER_LENGTH] ^= 0b10;
    expect(validateSpeechSignal(envelope)).toBe("ChecksumMismatch");
  });

  it("rejects dirty padding in the final byte", () => {
    const envelope = encodeSpeechSignal(signalOf([1, 0, 1]));
    const body = envelope.subarray(SIGNAL_HEADER_LENGTH);
    body[body.length - 1] |= 0b1000_0000;
    new DataView(envelope.buffer).setUint32(8, crc32(body), true);

    expect(validateSpeechSignal(envelope)).toBe("PaddingNotZero");
  });

  it("rejects an implausible sample count before sizing anything from it", () => {
    const envelope = new Uint8Array(SIGNAL_HEADER_LENGTH);
    envelope.set([0x53, 0x53, 0x43, 0x31]);
    new DataView(envelope.buffer).setUint32(4, 0xffffffff, true);

    expect(validateSpeechSignal(envelope)).toBe("SampleCountOutOfRange");
  });
});

describe("decodeSpeechSignal", () => {
  it("throws rather than salvaging what it can", () => {
    const envelope = encodeSpeechSignal(signalOf([1, 0, 1, 1]));
    expect(() => decodeSpeechSignal(envelope.subarray(0, envelope.length - 1))).toThrow(
      SignalPayloadRejected,
    );
  });

  it("names the reason on the error", () => {
    try {
      decodeSpeechSignal(new Uint8Array(4));
      expect.unreachable("should have thrown");
    } catch (err) {
      expect((err as SignalPayloadRejected).reason).toBe("TooShort");
    }
  });
});
