// Browser stub for the Node built-in `module`.
//
// @echogarden/fvad-wasm contains a Node-only `await import("module")` branch
// (createRequire) guarded by an `IS_NODE` check that never executes in the
// browser. Turbopack still has to resolve the `module` specifier when bundling
// the VAD for the client, so we alias it to this empty stub for the browser
// build (see next.config.ts). Nothing here is ever called at runtime.
export {};
