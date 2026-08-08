/**
 * Playwright config for the Jellyfin plugin smoke tests (#19).
 *
 * These are deliberately kept out of Vitest: `npm test` only globs
 * lib/**, test/** and jellyfin-plugin/web/src/**, none of which match
 * jellyfin-plugin/e2e/*.spec.ts. Run these with `npm run jf:e2e`.
 *
 * Local-only by default. See jellyfin-plugin/docker/README.md for why.
 */

import { defineConfig, devices } from "@playwright/test";

const port = process.env.JELLYFIN_PORT ?? "8096";
const baseURL = process.env.JELLYFIN_URL ?? `http://127.0.0.1:${port}`;

export default defineConfig({
  testDir: "./jellyfin-plugin/e2e",
  globalSetup: "./jellyfin-plugin/e2e/global-setup.ts",

  outputDir: "./jellyfin-plugin/e2e/.artifacts",

  // The Jellyfin web client is a single shared server. Running specs in
  // parallel against one instance makes library-mutating tests (the full sync
  // run) race, so keep it serial.
  fullyParallel: false,
  workers: 1,

  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,

  // Generous: the client is a heavy SPA and the first paint after a cold
  // container start is slow.
  timeout: 120_000,
  expect: { timeout: 20_000 },

  reporter: [["list"], ["html", { outputFolder: "./jellyfin-plugin/e2e/.report", open: "never" }]],

  use: {
    baseURL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "off",
    actionTimeout: 30_000,
    navigationTimeout: 60_000,
  },

  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
