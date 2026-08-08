// Bundles the Jellyfin plugin's browser code into ONE self-contained file.
//
//   jellyfin-plugin/web/src/index.ts  ->  jellyfin-plugin/web/dist/subtitleSync.js
//
// The entry point imports `../../../lib/*` by relative path, so `lib/` stays the
// single source of truth: the algorithm the plugin ships is byte-for-byte the
// one the golden parity test covers. There is no copy of `lib/` under
// jellyfin-plugin/ and there must never be one.
//
// The output is embedded in the C# assembly as a manifest resource (see
// Jellyfin.Plugin.SubtitleSync.csproj) and served through `IHasWebPages`.
//
// HARD REQUIREMENT: no runtime network dependency. A Jellyfin server is often on
// a LAN with no internet access, so a CDN fetch is a broken plugin, not a slow
// one. Two things enforce that here:
//   - `@echogarden/fvad-wasm` is redirected to src/fvadWasm.ts, which carries the
//     20 KB fvad.wasm inline as base64.
//   - `@ffmpeg/*` (which lib/audio.ts lazily loads from unpkg) is replaced by a
//     throwing stub; the plugin gets its PCM from the server instead.
// `assertSelfContained` below re-checks both against the finished bundle, so a
// future import that reintroduces a fetch fails the build rather than the LAN.
//
// Usage:
//   node jellyfin-plugin/web/build.mjs            production bundle (minified)
//   node jellyfin-plugin/web/build.mjs --dev      unminified + inline sourcemap
//   node jellyfin-plugin/web/build.mjs --watch    rebuild on change (implies --dev)

import { createRequire } from "node:module";
import { mkdir, readFile, writeFile, stat } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, "..", "..");

const args = new Set(process.argv.slice(2));
const watch = args.has("--watch");
const dev = watch || args.has("--dev");

const ENTRY = resolve(here, "src", "index.ts");
const OUTFILE = resolve(here, "dist", "subtitleSync.js");

// The sync page UI (#12). A SECOND bundle on purpose: it reaches the algorithm
// through `window.SubtitleSync`, which the bundle above defines, so lib/ and the
// 27 KB of inline libfvad are downloaded once and cached once rather than
// duplicated into the page. It is also what keeps the page replaceable without
// rebuilding the shared bundle.
const PAGE_ENTRY = resolve(here, "src", "page", "main.ts");
const PAGE_OUTFILE = resolve(here, "dist", "subtitleSyncPage.js");

// The Subtitles-menu injection (#13). A THIRD bundle, and the odd one out: it is
// not served over HTTP at all. It is inlined verbatim into /web/index.html by
// the File Transformation plugin, so it must be a complete, self-executing
// script that depends on nothing else having loaded - not window.SubtitleSync,
// not the page bundle, not even document.body.
const INJECT_ENTRY = resolve(here, "src", "inject.ts");
const INJECT_OUTFILE = resolve(here, "dist", "subtitleSyncInject.js");
const FVAD_SHIM = resolve(here, "src", "fvadWasm.ts");
const FFMPEG_STUB = resolve(here, "src", "ffmpegUnavailable.ts");

// ---------------------------------------------------------------------------
// Dependency check, up front and loud
// ---------------------------------------------------------------------------

let esbuild;
try {
  esbuild = await import("esbuild");
} catch {
  console.error(
    "\n[subtitle-sync] esbuild is not installed.\n" +
      "  The Jellyfin plugin embeds a bundle of lib/ built by esbuild, so this is\n" +
      "  required for `dotnet build`. Run `npm install` in the repository root.\n",
  );
  process.exit(1);
}

const fvadWasmPath = require.resolve("@echogarden/fvad-wasm/fvad.wasm");
const fvadWasmBase64 = (await readFile(fvadWasmPath)).toString("base64");

// ---------------------------------------------------------------------------
// Resolution overrides
// ---------------------------------------------------------------------------

/**
 * Redirects that turn a graph with three lazy network loads into a closed one.
 * Everything else resolves normally; nothing is marked `external`, so the output
 * contains no import specifier a browser would have to resolve.
 */
const selfContainedPlugin = {
  name: "subtitle-sync-self-contained",
  setup(build) {
    // The shim itself must reach the real package, or this recurses forever.
    build.onResolve({ filter: /^@echogarden\/fvad-wasm$/ }, (a) =>
      a.importer === FVAD_SHIM ? undefined : { path: FVAD_SHIM },
    );

    build.onResolve({ filter: /^@ffmpeg\/(ffmpeg|util)$/ }, () => ({
      path: FFMPEG_STUB,
    }));

    // fvad.js carries an Emscripten Node branch (`await import("module")`, then
    // fs/path/url) that is dead in a browser but still has to resolve for the
    // bundle to build. Scoped to that package on purpose: a node builtin
    // imported from anywhere else is a real mistake and should fail here.
    build.onResolve({ filter: /^(node:)?(module|fs|path|url)$/ }, (a) =>
      a.importer.replace(/\\/g, "/").includes("@echogarden/fvad-wasm")
        ? { path: a.path, namespace: "node-builtin-stub" }
        : undefined,
    );
    build.onLoad({ filter: /.*/, namespace: "node-builtin-stub" }, () => ({
      contents: "export default {};",
      loader: "js",
    }));
  },
};

// ---------------------------------------------------------------------------
// Post-build assertions
// ---------------------------------------------------------------------------

/**
 * Patterns that would mean the bundle needs the network or a module loader.
 *
 * Note what is NOT here: a bare `cdn.jsdelivr.net` match. lib/audio.ts declares
 * `CDN_CORE_BASE_URL` as a top-level template literal, which esbuild keeps as a
 * dead string even after tree-shaking every function that reads it. The URL only
 * becomes a fetch when `${CDN_CORE_BASE_URL}/ffmpeg-core.js` is built, so that
 * is what we look for - an inert string is not a network dependency, and
 * pretending otherwise would just train someone to weaken the check later.
 */
const FORBIDDEN = [
  [/ffmpeg-core\.(js|wasm)/, "the ffmpeg.wasm core loader"],
  [/\bfrom\s*"[^".]/, "a bare ES import specifier"],
  [/\bimport\s*\(\s*["'][^"'.]/, "a bare dynamic import()"],
  [/\brequire\s*\(\s*["'](fs|path|url|module)["']/, "a Node require()"],
];
// Emscripten's own `new URL("fvad.wasm", import.meta.url)` fallback survives
// minification, so its presence proves nothing either way. What proves the wasm
// shipped inline is the base64 payload itself, checked below.

/**
 * The only third-party packages allowed into the bundle: the VAD, and the FFT
 * that `lib/sync.ts` cross-correlates with. Anything else is a stowaway and
 * almost certainly a network dependency in disguise.
 */
const ALLOWED_DEPENDENCIES = ["@echogarden/fvad-wasm", "fft.js"];

async function assertSelfContained(file, metafile) {
  const code = await readFile(file, "utf8");
  for (const [pattern, what] of FORBIDDEN) {
    const hit = code.match(pattern);
    if (hit) {
      throw new Error(
        `bundle is not self-contained: found ${what} (${JSON.stringify(hit[0])}). ` +
          "Add a redirect in build.mjs or stop importing it.",
      );
    }
  }

  if (metafile) {
    const stowaways = Object.keys(metafile.inputs)
      .map((p) => p.replace(/\\/g, "/"))
      .filter(
        (p) =>
          p.includes("node_modules/") &&
          !ALLOWED_DEPENDENCIES.some((dep) =>
            p.includes(`node_modules/${dep}/`),
          ),
      );
    if (stowaways.length) {
      throw new Error(
        `bundle pulled in unexpected dependencies: ${stowaways.join(", ")}`,
      );
    }
  }

  // The VAD is the whole point; a bundle without it would look fine until the
  // first sync run.
  if (!code.includes("fvad_process")) {
    throw new Error("bundle is missing the libfvad VAD glue");
  }
  if (!code.includes(fvadWasmBase64)) {
    throw new Error(
      "bundle does not contain fvad.wasm inline - it would try to fetch it",
    );
  }
  const { size } = await stat(file);
  if (size < 40_000) {
    throw new Error(`bundle is implausibly small (${size} bytes)`);
  }
  return size;
}

/**
 * The page bundle's own check. It carries no algorithm and no wasm - it reads
 * `window.SubtitleSync` - so the payload assertions above do not apply. What
 * still must hold is that it needs no loader and no network, and that it did
 * not accidentally inline lib/ by importing a value where a type was meant.
 */
async function assertPageSelfContained(file, metafile) {
  const code = await readFile(file, "utf8");
  for (const [pattern, what] of FORBIDDEN) {
    const hit = code.match(pattern);
    if (hit) {
      throw new Error(
        `page bundle is not self-contained: found ${what} (${JSON.stringify(hit[0])}).`,
      );
    }
  }

  if (metafile) {
    const inputs = Object.keys(metafile.inputs).map((p) => p.replace(/\\/g, "/"));

    const stowaways = inputs.filter((p) => p.includes("node_modules/"));
    if (stowaways.length) {
      throw new Error(`page bundle pulled in dependencies: ${stowaways.join(", ")}`);
    }

    // A value import from lib/ would duplicate the algorithm into a second
    // download and, worse, give the page a copy that could drift from the one
    // `window.SubtitleSync` exposes. Type-only imports leave no input here.
    const libCopies = inputs.filter((p) => /(^|\/)lib\//.test(p));
    if (libCopies.length) {
      throw new Error(
        `page bundle inlined lib/ (${libCopies.join(", ")}). Reach the algorithm ` +
          "through window.SubtitleSync, and import from lib/ with `import type` only.",
      );
    }
  }

  const { size } = await stat(file);
  if (size < 2_000) {
    throw new Error(`page bundle is implausibly small (${size} bytes)`);
  }
  return size;
}

/**
 * The injected script's own check.
 *
 * Two things matter here that do not matter for the other two bundles. It is
 * inlined into someone else's HTML document, so a literal `</script>` anywhere
 * in it - even inside a string - would end the tag early and spray the rest of
 * the bundle across the page as text. And it must stay small: this is bytes on
 * every single page load of the web client, for every user, whether or not they
 * ever open a context menu.
 */
async function assertInjectable(file, metafile) {
  const code = await readFile(file, "utf8");

  for (const [pattern, what] of FORBIDDEN) {
    const hit = code.match(pattern);
    if (hit) {
      throw new Error(
        `inject bundle is not self-contained: found ${what} (${JSON.stringify(hit[0])}).`,
      );
    }
  }

  if (/<\/script/i.test(code)) {
    throw new Error(
      "inject bundle contains a literal </script>, which would break the " +
        "document it is inlined into. Split the string.",
    );
  }

  if (metafile) {
    const inputs = Object.keys(metafile.inputs).map((p) => p.replace(/\\/g, "/"));
    const stowaways = inputs.filter(
      (p) => p.includes("node_modules/") || /(^|\/)lib\//.test(p),
    );
    if (stowaways.length) {
      throw new Error(
        `inject bundle pulled in ${stowaways.join(", ")}. It is inlined into every ` +
          "page load of the web client and must stay a single standalone file.",
      );
    }
  }

  const { size } = await stat(file);
  if (size > 16_000) {
    throw new Error(
      `inject bundle is ${size} bytes. It is inlined into index.html on every ` +
        "page load; keep it under 16 KB or move the work to a fetched script.",
    );
  }
  if (size < 1_000) {
    throw new Error(`inject bundle is implausibly small (${size} bytes)`);
  }
  return size;
}

// ---------------------------------------------------------------------------
// Build
// ---------------------------------------------------------------------------

const options = {
  entryPoints: [ENTRY],
  outfile: OUTFILE,
  bundle: true,
  format: "iife",
  // The page (#12) reaches everything through this one global.
  globalName: "SubtitleSync",
  platform: "browser",
  // Jellyfin 10.11's web client targets evergreen browsers; async generators and
  // top-level ReadableStream in pcmStream.ts need ES2020 at minimum.
  target: ["es2020"],
  charset: "utf8",
  legalComments: "none",
  minify: !dev,
  sourcemap: dev ? "inline" : false,
  logLevel: "warning",
  // A warning here is nearly always a resolution surprise worth stopping for.
  logOverride: { "empty-import-meta": "error" },
  define: {
    __FVAD_WASM_BASE64__: JSON.stringify(fvadWasmBase64),
    __SUBTITLE_SYNC_BUILD__: JSON.stringify(new Date().toISOString()),
    // fvad.js reads import.meta.url in branches we have already neutralised
    // with `locateFile`. iife format has no import.meta, so give it a value
    // rather than let esbuild substitute an empty object.
    "import.meta.url": JSON.stringify("about:blank"),
  },
  plugins: [selfContainedPlugin],
  metafile: true,
};

const pageOptions = {
  ...options,
  entryPoints: [PAGE_ENTRY],
  outfile: PAGE_OUTFILE,
  // The page installs `window.SubtitleSyncPage` itself, from module scope, so
  // it needs no global name of its own.
  globalName: undefined,
};

const injectOptions = {
  ...options,
  entryPoints: [INJECT_ENTRY],
  outfile: INJECT_OUTFILE,
  // No global name: it installs one flag on `window` and is otherwise invisible.
  globalName: undefined,
};

const builds = [
  { name: "bundle", options, outfile: OUTFILE, verify: assertSelfContained },
  { name: "page", options: pageOptions, outfile: PAGE_OUTFILE, verify: assertPageSelfContained },
  { name: "inject", options: injectOptions, outfile: INJECT_OUTFILE, verify: assertInjectable },
];

await mkdir(dirname(OUTFILE), { recursive: true });

function report(outfile, size) {
  const rel = outfile.slice(repoRoot.length + 1).replace(/\\/g, "/");
  console.log(
    `[subtitle-sync] ${rel}  ${(size / 1024).toFixed(1)} KB${dev ? "  (dev)" : ""}`,
  );
}

if (watch) {
  for (const build of builds) {
    const ctx = await esbuild.context({
      ...build.options,
      plugins: [
        ...build.options.plugins,
        {
          name: "report",
          setup(esbuildBuild) {
            esbuildBuild.onEnd(async (result) => {
              if (result.errors.length) return;
              try {
                report(build.outfile, await build.verify(build.outfile, result.metafile));
              } catch (err) {
                console.error(`[subtitle-sync] ${err.message}`);
              }
            });
          },
        },
      ],
    });
    await ctx.watch();
  }
  console.log("[subtitle-sync] watching jellyfin-plugin/web and lib/ ...");
} else {
  for (const build of builds) {
    const result = await esbuild.build(build.options);
    // Written next to the bundle so a stale build is diagnosable without
    // guessing which files went into it. Not embedded in the assembly.
    await writeFile(
      resolve(dirname(build.outfile), `meta.${build.name}.json`),
      JSON.stringify(result.metafile, null, 2),
    );
    report(build.outfile, await build.verify(build.outfile, result.metafile));
  }
}
