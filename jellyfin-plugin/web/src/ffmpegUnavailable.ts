// Stub substituted for @ffmpeg/ffmpeg and @ffmpeg/util in the plugin bundle.
//
// `lib/audio.ts` lazily imports both to decode an uploaded File in the browser,
// loading @ffmpeg/core from a CDN. The plugin never takes that path: the server
// runs its own ffmpeg and streams 16 kHz mono s16le down `GET /SubtitleSync/Pcm/{id}`,
// which `speechSignalFromPcmStream` consumes. Bundling ffmpeg.wasm anyway would
// add dead weight and, worse, bake a CDN URL into a plugin that has to work on
// an air-gapped LAN.
//
// Stubbing rather than marking external is deliberate: `external` would leave a
// bare `import("@ffmpeg/ffmpeg")` specifier in the output that no browser can
// resolve. This fails with a sentence that says what to do instead.

function unavailable(): never {
  throw new Error(
    "ffmpeg.wasm is not bundled into the Jellyfin plugin. " +
      "Fetch PCM from GET /SubtitleSync/Pcm/{id} and use speechSignalFromPcmStream instead.",
  );
}

/** Stands in for `@ffmpeg/ffmpeg`'s FFmpeg class. Constructing it throws. */
export class FFmpeg {
  constructor() {
    unavailable();
  }
}

/** Stands in for `@ffmpeg/util`'s toBlobURL. Calling it throws. */
export function toBlobURL(): never {
  return unavailable();
}

/** Stands in for `@ffmpeg/util`'s fetchFile. Calling it throws. */
export function fetchFile(): never {
  return unavailable();
}
