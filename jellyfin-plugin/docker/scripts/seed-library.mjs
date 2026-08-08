// Builds the seeded library on the host from the already-committed fixtures.
//
// Nothing is downloaded. Both fixtures are committed and their provenance is
// documented in test/fixtures/PROVENANCE.md.
//
// Layout follows Jellyfin's movie naming convention, so each file is recognised
// as a movie and its sibling .srt as an external English subtitle track:
//
//   media/movies/Sample Clip (2020)/Sample Clip (2020).mp4
//   media/movies/Sample Clip (2020)/Sample Clip (2020).en.srt
//   media/movies/Structured Clip (2021)/Structured Clip (2021).mp4
//   media/movies/Structured Clip (2021)/Structured Clip (2021).en.srt
//
// Two movies, because they answer different questions:
//
//   Sample Clip      real footage, real dialogue. Proves the plumbing works on
//                    something that is not synthetic. Its audio reads as ~92%
//                    speech, so a sync over it has no verifiable right answer.
//   Structured Clip  synthesised so it does. Its audio alternates speech and
//                    silence on a known schedule and its subtitle track is
//                    displaced by SYNCABLE_KNOWN_OFFSET, so a sync run through
//                    the plugin can be asserted against a real number (#20).
//
// Assert on the Structured Clip when the test is about a sync being correct.

import fs from "node:fs";
import path from "node:path";

import {
  FIXTURE_MP4,
  FIXTURE_SRT,
  HOST_MEDIA_ROOT,
  MOVIE_FOLDER,
  SYNCABLE_MP4,
  SYNCABLE_SRT,
  SYNCABLE_FOLDER,
} from "./config.mjs";

/** Each seeded movie: the folder/file stem, and the fixtures it is built from. */
const MOVIES = [
  { folder: MOVIE_FOLDER, mp4: FIXTURE_MP4, srt: FIXTURE_SRT },
  { folder: SYNCABLE_FOLDER, mp4: SYNCABLE_MP4, srt: SYNCABLE_SRT },
];

export function seedLibrary({ force = false } = {}) {
  const dirs = [];

  for (const movie of MOVIES) {
    for (const fixture of [movie.mp4, movie.srt]) {
      if (!fs.existsSync(fixture)) {
        throw new Error(
          `Missing fixture ${fixture}. It is committed to the repo; if it is gone, restore it rather than downloading a replacement. The synthesised ones can also be rebuilt with \`node test/oracle/gen_structured_fixture.mjs\`.`,
        );
      }
    }

    const movieDir = path.join(HOST_MEDIA_ROOT, movie.folder);
    fs.mkdirSync(movieDir, { recursive: true });
    dirs.push(movieDir);

    const targets = [
      [movie.mp4, path.join(movieDir, `${movie.folder}.mp4`)],
      [movie.srt, path.join(movieDir, `${movie.folder}.en.srt`)],
    ];

    for (const [from, to] of targets) {
      if (force || !fs.existsSync(to)) {
        fs.copyFileSync(from, to);
        console.log(`seeded ${path.relative(process.cwd(), to)}`);
      } else {
        console.log(`kept    ${path.relative(process.cwd(), to)} (already present)`);
      }
    }
  }

  return dirs;
}

if (import.meta.url === `file://${process.argv[1]}` || process.argv[1]?.endsWith("seed-library.mjs")) {
  seedLibrary({ force: process.argv.includes("--force") });
}
