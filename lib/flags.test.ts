import { describe, it, expect, afterEach } from "vitest";

import { isPluginPageEnabled } from "./flags";

const VAR = "NEXT_PUBLIC_FEATURE_PLUGIN_PAGE";
const original = process.env[VAR];

function setFlag(value: string | undefined): void {
  if (value === undefined) delete process.env[VAR];
  else process.env[VAR] = value;
}

afterEach(() => {
  setFlag(original);
});

describe("isPluginPageEnabled", () => {
  it("is off when the variable is unset", () => {
    setFlag(undefined);
    expect(isPluginPageEnabled()).toBe(false);
  });

  it('is on when the variable is exactly "1"', () => {
    setFlag("1");
    expect(isPluginPageEnabled()).toBe(true);
  });

  // The flag is deliberately strict: only "1" opts in. Anything else - including
  // values that look truthy - must fail closed, so a typo hides the page rather
  // than publishing it.
  it.each(["", "0", "true", "TRUE", "yes", "on", " 1", "1 ", "2", "false"])(
    "is off for %o",
    (value) => {
      setFlag(value);
      expect(isPluginPageEnabled()).toBe(false);
    },
  );

  it("re-reads the environment on every call rather than caching at import", () => {
    setFlag("1");
    expect(isPluginPageEnabled()).toBe(true);
    setFlag("0");
    expect(isPluginPageEnabled()).toBe(false);
  });
});
