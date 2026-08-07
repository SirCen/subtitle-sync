// Builds the seeded library on the host from the already-committed fixtures.
//
// Nothing is downloaded. test/fixtures/sample.mp4 is ~2 MB and its provenance
// is documented in test/fixtures/PROVENANCE.md.
//
// Layout follows Jellyfin's movie naming convention, so the file is recognised
// as a movie and the sibling .srt as an external English subtitle track:
//
//   media/movies/Sample Clip (2020)/Sample Clip (2020).mp4
//   media/movies/Sample Clip (2020)/Sample Clip (2020).en.srt

import fs from "node:fs";
import path from "node:path";

import {
  FIXTURE_MP4,
  FIXTURE_SRT,
  HOST_MEDIA_ROOT,
  MOVIE_FOLDER,
} from "./config.mjs";

export function seedLibrary({ force = false } = {}) {
  for (const fixture of [FIXTURE_MP4, FIXTURE_SRT]) {
    if (!fs.existsSync(fixture)) {
      throw new Error(
        `Missing fixture ${fixture}. It is committed to the repo; if it is gone, restore it rather than downloading a replacement.`,
      );
    }
  }

  const movieDir = path.join(HOST_MEDIA_ROOT, MOVIE_FOLDER);
  fs.mkdirSync(movieDir, { recursive: true });

  const targets = [
    [FIXTURE_MP4, path.join(movieDir, `${MOVIE_FOLDER}.mp4`)],
    [FIXTURE_SRT, path.join(movieDir, `${MOVIE_FOLDER}.en.srt`)],
  ];

  for (const [from, to] of targets) {
    if (force || !fs.existsSync(to)) {
      fs.copyFileSync(from, to);
      console.log(`seeded ${path.relative(process.cwd(), to)}`);
    } else {
      console.log(`kept    ${path.relative(process.cwd(), to)} (already present)`);
    }
  }

  return movieDir;
}

if (import.meta.url === `file://${process.argv[1]}` || process.argv[1]?.endsWith("seed-library.mjs")) {
  seedLibrary({ force: process.argv.includes("--force") });
}
