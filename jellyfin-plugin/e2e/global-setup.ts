/**
 * Playwright globalSetup: brings the Jellyfin container up, completes the
 * first-run wizard, seeds the library and waits for the scan.
 *
 * The setup script is spawned as a child process rather than imported. It is an
 * ESM .mjs and this package is CommonJS, so Playwright's transpiler would load
 * it under the wrong module semantics; a child process runs it under real Node.
 *
 * Idempotent, so `npx playwright test` against an already-running server just
 * re-verifies the state and moves on.
 *
 * Set JELLYFIN_SKIP_DOCKER=1 to point the suite at a server you started
 * yourself (a different host, or a container you are tailing logs on).
 */

import { spawnSync } from "node:child_process";
import path from "node:path";

export default async function globalSetup(): Promise<void> {
  const script = path.join(__dirname, "..", "docker", "scripts", "setup.mjs");
  const args = [script];
  if (process.env.JELLYFIN_SKIP_DOCKER === "1") args.push("--no-start");

  const result = spawnSync(process.execPath, args, {
    stdio: "inherit",
    env: process.env,
  });

  if (result.error) {
    throw new Error(`Could not run the harness setup script: ${result.error.message}`);
  }

  if (result.status !== 0) {
    throw new Error(
      [
        `Harness setup failed (exit ${result.status}).`,
        "",
        "This suite needs Docker running. See jellyfin-plugin/docker/README.md.",
        "If you already have a server up elsewhere, set JELLYFIN_SKIP_DOCKER=1 and JELLYFIN_URL.",
      ].join("\n"),
    );
  }
}
