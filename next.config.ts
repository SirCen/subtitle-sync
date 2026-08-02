import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  turbopack: {
    resolveAlias: {
      // @echogarden/fvad-wasm (pulled in by lib/audio.ts) has a Node-only
      // `await import("module")` branch guarded by an IS_NODE check that never
      // runs in the browser. Turbopack must still resolve the specifier when
      // bundling the VAD for the client, so point it at an empty stub for the
      // browser build.
      module: { browser: "./stubs/empty-module.js" },
    },
  },
};

export default nextConfig;
