import { describe, it, expect } from "vitest";
import type { SrtBlock } from "./types";
import { srtTimeToSeconds, secondsToSrtTime, parseSrt, writeSrt } from "./srt";

describe("secondsToSrtTime", () => {
  it("formats zero", () => {
    expect(secondsToSrtTime(0)).toBe("00:00:00,000");
  });

  it("formats a normal value", () => {
    // 1h 2m 3s 456ms
    expect(secondsToSrtTime(3600 + 120 + 3 + 0.456)).toBe("01:02:03,456");
  });

  it("clamps negative values to zero", () => {
    expect(secondsToSrtTime(-5)).toBe("00:00:00,000");
    expect(secondsToSrtTime(-0.001)).toBe("00:00:00,000");
  });

  it("handles the ms=1000 rounding rollover cascading to a new hour", () => {
    // 3599.9996 rounds ms up to 1000 -> cascades s->m->h to 01:00:00,000
    expect(secondsToSrtTime(3599.9996)).toBe("01:00:00,000");
  });

  it("handles a value crossing a minute boundary", () => {
    expect(secondsToSrtTime(59.9994)).toBe("00:00:59,999");
    expect(secondsToSrtTime(60)).toBe("00:01:00,000");
    // 119.9996 -> ms rolls to 1000 -> 00:02:00,000
    expect(secondsToSrtTime(119.9996)).toBe("00:02:00,000");
  });
});

describe("srtTimeToSeconds", () => {
  it("parses comma millisecond separator", () => {
    expect(srtTimeToSeconds("01:02:03,456")).toBeCloseTo(3723.456, 6);
  });

  it("parses dot millisecond separator to the same value", () => {
    expect(srtTimeToSeconds("01:02:03.456")).toBeCloseTo(
      srtTimeToSeconds("01:02:03,456"),
      9,
    );
  });

  it("round-trips secondsToSrtTime(srtTimeToSeconds(x)) === x", () => {
    const samples = [
      "00:00:00,000",
      "00:00:01,500",
      "01:02:03,456",
      "10:20:30,999",
      "00:59:59,001",
    ];
    for (const x of samples) {
      expect(secondsToSrtTime(srtTimeToSeconds(x))).toBe(x);
    }
  });
});

describe("parseSrt", () => {
  it("parses a basic 2-block SRT", () => {
    const raw = [
      "1",
      "00:00:01,000 --> 00:00:02,000",
      "Hello",
      "",
      "2",
      "00:00:03,000 --> 00:00:04,500",
      "World",
      "",
    ].join("\n");
    const blocks = parseSrt(raw);
    expect(blocks).toHaveLength(2);
    expect(blocks[0]).toMatchObject({ index: 1, start: 1, end: 2, text: "Hello" });
    expect(blocks[1]).toMatchObject({ index: 2, start: 3, end: 4.5, text: "World" });
  });

  it("parses blocks with NO numeric index line", () => {
    const raw = [
      "00:00:01,000 --> 00:00:02,000",
      "Hello",
      "",
      "00:00:03,000 --> 00:00:04,000",
      "World",
    ].join("\n");
    const blocks = parseSrt(raw);
    expect(blocks).toHaveLength(2);
    // re-indexed 1..N
    expect(blocks.map((b) => b.index)).toEqual([1, 2]);
    expect(blocks[0].text).toBe("Hello");
  });

  it("strips a leading UTF-8 BOM", () => {
    const raw =
      "﻿1\n00:00:01,000 --> 00:00:02,000\nHello\n";
    const blocks = parseSrt(raw);
    expect(blocks).toHaveLength(1);
    expect(blocks[0].start).toBe(1);
    expect(blocks[0].text).toBe("Hello");
  });

  it("handles CRLF line endings", () => {
    const raw =
      "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nWorld\r\n";
    const blocks = parseSrt(raw);
    expect(blocks).toHaveLength(2);
    expect(blocks[0].text).toBe("Hello");
    expect(blocks[1].text).toBe("World");
  });

  it("preserves multi-line subtitle text", () => {
    const raw =
      "1\n00:00:01,000 --> 00:00:02,000\nLine one\nLine two\n";
    const blocks = parseSrt(raw);
    expect(blocks[0].text).toBe("Line one\nLine two");
  });

  it("accepts dot millisecond separators in timestamps", () => {
    const raw = "1\n00:00:01.250 --> 00:00:02.750\nHi\n";
    const blocks = parseSrt(raw);
    expect(blocks[0].start).toBeCloseTo(1.25, 6);
    expect(blocks[0].end).toBeCloseTo(2.75, 6);
  });

  it("re-indexes blocks 1..N regardless of source indices", () => {
    const raw =
      "7\n00:00:01,000 --> 00:00:02,000\nA\n\n42\n00:00:03,000 --> 00:00:04,000\nB\n";
    const blocks = parseSrt(raw);
    expect(blocks.map((b) => b.index)).toEqual([1, 2]);
  });

  it("throws on empty input", () => {
    expect(() => parseSrt("")).toThrow();
    expect(() => parseSrt("   \n  \n")).toThrow();
  });

  it("throws on garbage input", () => {
    expect(() => parseSrt("this is not\na subtitle file\nat all")).toThrow();
  });
});

describe("writeSrt", () => {
  const blocks: SrtBlock[] = [
    { index: 1, start: 1, end: 2, text: "Hello" },
    { index: 2, start: 3, end: 4.5, text: "World\nSecond line" },
  ];

  it("produces exact SRT format including a trailing blank line per block", () => {
    const out = writeSrt(blocks);
    expect(out).toBe(
      "1\n00:00:01,000 --> 00:00:02,000\nHello\n\n" +
        "2\n00:00:03,000 --> 00:00:04,500\nWorld\nSecond line\n\n",
    );
  });

  it("round-trips: parseSrt(writeSrt(blocks)) preserves times and text", () => {
    const parsed = parseSrt(writeSrt(blocks));
    expect(parsed).toHaveLength(blocks.length);
    for (let i = 0; i < blocks.length; i++) {
      expect(parsed[i].start).toBeCloseTo(blocks[i].start, 6);
      expect(parsed[i].end).toBeCloseTo(blocks[i].end, 6);
      expect(parsed[i].text).toBe(blocks[i].text);
      expect(parsed[i].index).toBe(i + 1);
    }
  });
});
