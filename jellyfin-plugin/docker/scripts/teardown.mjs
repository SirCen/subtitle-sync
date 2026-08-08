// Stops the harness. `--purge` also drops the config and cache volumes and the
// seeded media, giving a genuinely fresh first-run server next time.
//
//   npm run jf:down
//   npm run jf:down -- --purge

import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import { DOCKER_DIR } from "./config.mjs";

const purge = process.argv.includes("--purge");

execFileSync("docker", ["compose", "down", ...(purge ? ["-v"] : [])], {
  cwd: DOCKER_DIR,
  stdio: "inherit",
});

if (purge) {
  const media = path.join(DOCKER_DIR, "media");
  fs.rmSync(media, { recursive: true, force: true });
  console.log("removed seeded media");

  // The downloaded File Transformation build, and the stamp file that records
  // it. Left behind, the stamp would tell the next `jf:up` that a plugin is
  // installed when the volume holding it has just been dropped, and the #13
  // specs would fail on a server that looks freshly set up.
  const fileTransformation = path.join(DOCKER_DIR, "plugins", "FileTransformation");
  fs.rmSync(fileTransformation, { recursive: true, force: true });
  fs.mkdirSync(fileTransformation, { recursive: true });
  fs.writeFileSync(path.join(fileTransformation, ".gitkeep"), "");
  console.log("removed the downloaded File Transformation plugin");
}
