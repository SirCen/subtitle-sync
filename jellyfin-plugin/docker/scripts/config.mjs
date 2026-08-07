// Node-side view of the harness config.
//
// Values come from ../harness.config.json so the Playwright specs (which cannot
// import this ESM module, see the note in that file) read the same numbers.
// Anything derived from the filesystem lives here rather than in the JSON.

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));

/** jellyfin-plugin/docker */
export const DOCKER_DIR = path.resolve(here, "..");

/** Repo root. */
export const REPO_ROOT = path.resolve(DOCKER_DIR, "..", "..");

const raw = JSON.parse(
  fs.readFileSync(path.join(DOCKER_DIR, "harness.config.json"), "utf8"),
);

export const JELLYFIN_PORT = process.env.JELLYFIN_PORT ?? String(raw.port);

export const JELLYFIN_URL =
  process.env.JELLYFIN_URL ?? `http://127.0.0.1:${JELLYFIN_PORT}`;

/**
 * The admin the startup wizard is scripted to create. Deliberately weak: this
 * server is a throwaway bound to loopback and is never exposed.
 */
export const ADMIN_USERNAME = process.env.JELLYFIN_ADMIN_USER ?? raw.adminUsername;
export const ADMIN_PASSWORD = process.env.JELLYFIN_ADMIN_PASSWORD ?? raw.adminPassword;

/** A second, non-admin account, so we can assert the menu item is hidden for it (#13). */
export const VIEWER_USERNAME = process.env.JELLYFIN_VIEWER_USER ?? raw.viewerUsername;
export const VIEWER_PASSWORD = process.env.JELLYFIN_VIEWER_PASSWORD ?? raw.viewerPassword;

export const LIBRARY_NAME = raw.libraryName;

/**
 * Folder and file stem for the seeded movie. Jellyfin derives the item name
 * from this, and metadata fetchers are disabled on the library, so the name is
 * stable and the Playwright specs can look the item up by it.
 */
export const MOVIE_FOLDER = raw.movieFolder;
export const MOVIE_NAME = raw.movieName;

/**
 * The second seeded movie: the synthesised fixture with real speech/silence
 * structure, seeded with a subtitle track displaced by a known offset. This is
 * the one a sync run can be checked against - see the `_syncableComment` in
 * harness.config.json and issue #20.
 */
export const SYNCABLE_FOLDER = raw.syncableFolder;
export const SYNCABLE_NAME = raw.syncableName;

/** Seconds. `analyze` must recover this from the seeded track. */
export const SYNCABLE_KNOWN_OFFSET = raw.syncableKnownOffset;

/** Path inside the container. Must match the ./media mount in docker-compose.yml. */
export const CONTAINER_MEDIA_ROOT = raw.containerMediaRoot;

/** Host path the seeded library is written to. */
export const HOST_MEDIA_ROOT = path.join(DOCKER_DIR, "media", "movies");

const FIXTURE_DIR = path.join(REPO_ROOT, "test", "fixtures");

export const FIXTURE_MP4 = path.join(FIXTURE_DIR, "sample.mp4");
export const FIXTURE_SRT = path.join(FIXTURE_DIR, "sample.srt");

/** Media whose audio the VAD can actually resolve into speech and silence. */
export const SYNCABLE_MP4 = path.join(FIXTURE_DIR, "structured.mp4");
/** Its subtitles, displaced by SYNCABLE_KNOWN_OFFSET. The sync input. */
export const SYNCABLE_SRT = path.join(FIXTURE_DIR, "structured.offset.srt");

/**
 * Sent on every API call. Jellyfin requires this on authenticated requests and
 * tolerates it on the unauthenticated startup ones.
 */
export function authHeader(token) {
  const parts = [
    'Client="subtitle-sync-harness"',
    'Device="node"',
    'DeviceId="subtitle-sync-harness"',
    'Version="0.1.0"',
  ];
  if (token) parts.push(`Token="${token}"`);
  return `MediaBrowser ${parts.join(", ")}`;
}
