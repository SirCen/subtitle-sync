// TEMPORARY — intentionally failing test to verify that a red suite blocks
// both the PR (required CI check) and the Vercel deployment (build command
// runs `npm test`). Delete this file once the gate is confirmed.
import { describe, it, expect } from "vitest";

describe("CI/deploy gate verification (intentionally failing)", () => {
  it("fails on purpose to prove red tests block the PR and the deploy", () => {
    expect(1).toBe(2);
  });
});
