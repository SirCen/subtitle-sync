import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    // Pure logic + golden test run in Node (golden test spawns Python/ffmpeg).
    environment: "node",
    include: [
      "lib/**/*.test.ts",
      "test/**/*.test.ts",
      "jellyfin-plugin/web/src/**/*.test.ts",
    ],
    testTimeout: 120_000, // golden test extracts audio with ffmpeg
  },
});
