/**
 * Playwright config for the documentation screenshot capture (#17).
 *
 * The capture is not a test - it drives the same live harness the smoke tests
 * use and writes PNGs into `public/plugin/`. It lives in `jellyfin-plugin/e2e/`
 * so it can reuse `harness.ts` rather than reimplementing login, but it must
 * never run as part of `npx playwright test`. That is why the file is named
 * `*.capture.ts`: Playwright's default `testMatch` only collects
 * `*.spec.ts`/`*.test.ts`, so the main config cannot see it and needs no
 * `testIgnore` to stay clean.
 *
 * Run it with:
 *
 *     npm run jf:screenshots
 *
 * Everything else - bringing the container up, seeding the library, the base
 * URL - is inherited from the main config, so the two cannot drift.
 */

import { defineConfig, devices } from "@playwright/test";

import base from "./playwright.config";

export default defineConfig({
  ...base,

  testMatch: "**/*.capture.ts",

  // One shot at a time against one server, and no retries: a retried capture
  // would silently overwrite a good PNG with a worse one.
  fullyParallel: false,
  workers: 1,
  retries: 0,

  // The sync run decodes audio and runs the VAD in-browser, which is slower
  // than anything in the smoke suite.
  timeout: 600_000,

  reporter: [["list"]],

  // Declared here rather than inherited: `devices["Desktop Chrome"]` in the
  // main config pins a 1280x720 viewport at deviceScaleFactor 1, and a
  // project's `use` wins over the config's, so overriding the scale factor at
  // config level alone would silently do nothing.
  projects: [
    {
      name: "screenshots",
      use: {
        ...devices["Desktop Chrome"],

        // 2x so the PNGs stay sharp on the displays most people read docs on.
        // The crops are small enough that the file size is still modest.
        deviceScaleFactor: 2,
        viewport: { width: 1280, height: 900 },
      },
    },
  ],

  use: {
    ...base.use,

    // A failed capture is worth a trace: there is no assertion output to read
    // afterwards, only a missing or wrong image.
    trace: "retain-on-failure",
    screenshot: "off",
    video: "off",
    actionTimeout: 30_000,
    navigationTimeout: 60_000,
  },
});
