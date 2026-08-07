// One-command bring-up: seed the library, start the container, complete the
// startup wizard, create the users and the library, wait for the scan.
//
//   npm run jf:up
//
// Idempotent. Running it against an already-configured server just re-checks
// everything and exits, so it is safe as a Playwright globalSetup too.

import { execFileSync } from "node:child_process";

import {
  ADMIN_PASSWORD,
  ADMIN_USERNAME,
  CONTAINER_MEDIA_ROOT,
  DOCKER_DIR,
  JELLYFIN_URL,
  LIBRARY_NAME,
  MOVIE_NAME,
  SYNCABLE_KNOWN_OFFSET,
  SYNCABLE_NAME,
  VIEWER_PASSWORD,
  VIEWER_USERNAME,
} from "./config.mjs";
import {
  authenticate,
  completeStartupWizard,
  ensureLibrary,
  ensureUser,
  listPlugins,
  waitForItem,
  waitForServer,
} from "./jellyfin-api.mjs";
import { seedLibrary } from "./seed-library.mjs";

function compose(...args) {
  execFileSync("docker", ["compose", ...args], {
    cwd: DOCKER_DIR,
    stdio: "inherit",
  });
}

export async function setup({ startContainer = true, log = console.log } = {}) {
  log("--- seeding library from test/fixtures");
  seedLibrary();

  if (startContainer) {
    log("--- docker compose up -d");
    compose("up", "-d");
  }

  log("--- waiting for Jellyfin");
  await waitForServer({ log });

  log("--- first-run setup");
  await completeStartupWizard({
    username: ADMIN_USERNAME,
    password: ADMIN_PASSWORD,
    log,
  });

  const admin = await authenticate(ADMIN_USERNAME, ADMIN_PASSWORD);

  await ensureUser({
    token: admin.token,
    username: VIEWER_USERNAME,
    password: VIEWER_PASSWORD,
    log,
  });

  await ensureLibrary({
    token: admin.token,
    name: LIBRARY_NAME,
    containerPath: CONTAINER_MEDIA_ROOT,
    log,
  });

  const item = await waitForItem({
    token: admin.token,
    userId: admin.userId,
    name: MOVIE_NAME,
    log,
  });

  // The fixture a sync can actually be checked against - see #20 and the
  // header of seed-library.mjs.
  const syncable = await waitForItem({
    token: admin.token,
    userId: admin.userId,
    name: SYNCABLE_NAME,
    log,
  });

  const plugins = await listPlugins(admin.token);
  log(
    plugins.length
      ? `--- plugins loaded: ${plugins.map((p) => `${p.Name} ${p.Version}`).join(", ")}`
      : "--- plugins loaded: none (expected until the plugin DLL is built, see #3)",
  );

  log("");
  log(`ready: ${JELLYFIN_URL}/web/`);
  log(`  admin      ${ADMIN_USERNAME} / ${ADMIN_PASSWORD}`);
  log(`  non-admin  ${VIEWER_USERNAME} / ${VIEWER_PASSWORD}`);
  log(`  item       ${MOVIE_NAME} (${item.Id})`);
  log(
    `  item       ${SYNCABLE_NAME} (${syncable.Id}) ` +
    `- subtitles displaced by ${SYNCABLE_KNOWN_OFFSET}s, sync should recover it`,
  );

  return { admin, item, syncable, plugins };
}

const invokedDirectly = process.argv[1]?.replace(/\\/g, "/").endsWith("scripts/setup.mjs");
if (invokedDirectly) {
  setup({ startContainer: !process.argv.includes("--no-start") }).catch((error) => {
    console.error(`\nsetup failed: ${error.message}`);
    process.exit(1);
  });
}
