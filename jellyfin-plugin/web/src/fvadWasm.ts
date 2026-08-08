// Self-contained loader for @echogarden/fvad-wasm.
//
// WHY THIS FILE EXISTS
//
// `createFvadFrameVad()` in pcmStream.ts does `await import("@echogarden/fvad-wasm")`
// and calls the default export with no arguments. Left alone, that Emscripten
// module resolves its companion `fvad.wasm` at runtime with
// `new URL("fvad.wasm", import.meta.url)` and fetches it over the network.
// Inside a bundled IIFE served from a Jellyfin plugin route there is no
// `import.meta.url` and no sibling `.wasm` to fetch, and a Jellyfin server may
// well sit on a LAN with no internet access, so a CDN is not an option either.
//
// So `build.mjs` redirects `@echogarden/fvad-wasm` to this module for every
// importer except this one. It inlines the 20 KB `fvad.wasm` as base64
// (`__FVAD_WASM_BASE64__`, injected by esbuild `define`) and hands it to
// Emscripten as `wasmBinary`, with a `locateFile` that keeps Emscripten off the
// `new URL(..., import.meta.url)` path entirely. Result: one file, no second
// request, no MIME-type negotiation, and the WASM can never drift from the JS
// that was compiled against it.

/** Base64 of node_modules/@echogarden/fvad-wasm/fvad.wasm, injected at build time. */
declare const __FVAD_WASM_BASE64__: string;

let decoded: Uint8Array | null = null;

/** Decode the inlined WASM once and cache it. */
function wasmBinary(): Uint8Array {
  if (decoded) return decoded;
  const binary = atob(__FVAD_WASM_BASE64__);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  decoded = bytes;
  return bytes;
}

// Resolved by build.mjs to the real package (this file is exempt from the
// redirect), so this is the genuine Emscripten factory.
// @ts-expect-error - untyped WASM module
import realFvad from "@echogarden/fvad-wasm";

type EmscriptenFactory = (
  arg?: Record<string, unknown>,
) => Promise<Record<string, unknown>>;

/**
 * Drop-in replacement for the package's default export that never touches the
 * network. Extra module options from the caller still win, apart from the two
 * we must control.
 */
const fvad: EmscriptenFactory = (arg = {}) =>
  (realFvad as EmscriptenFactory)({
    ...arg,
    wasmBinary: wasmBinary(),
    // Emscripten only calls `new URL("fvad.wasm", import.meta.url)` when
    // `locateFile` is absent. Supplying one keeps it on the string path; the
    // value is never fetched because `wasmBinary` short-circuits first.
    locateFile: (path: string) => path,
  });

export default fvad;
