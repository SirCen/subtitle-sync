// Thin wrapper over the Jellyfin 10.11 REST API, covering exactly what the
// harness needs: complete the startup wizard, create users, create the library,
// and find the seeded item.
//
// Endpoints were checked against the v10.11.11 tag of jellyfin/jellyfin:
//   Jellyfin.Api/Controllers/StartupController.cs        -> /Startup/*
//   Jellyfin.Api/Controllers/LibraryStructureController.cs -> /Library/VirtualFolders
//   Jellyfin.Api/Controllers/UserController.cs           -> /Users/*
// They are not assumed from older releases.

import { JELLYFIN_URL, authHeader } from "./config.mjs";

class JellyfinError extends Error {
  constructor(method, path, status, body) {
    super(`${method} ${path} -> ${status}${body ? `: ${body.slice(0, 300)}` : ""}`);
    this.name = "JellyfinError";
    this.status = status;
  }
}

/**
 * Holds the Node event loop open for the duration of a polling wait.
 *
 * This is not paranoia. While the container is booting, Docker's port proxy
 * accepts the TCP connection but nothing answers it. Node 20's fetch leaves a
 * request in that state without a ref'd handle, so the process decides the
 * event loop is empty and exits 0 mid-wait, silently, with the loop's own
 * setTimeout never having been reached. A ref'd interval is what stops that.
 *
 * @returns {() => void} call to release
 */
function keepAlive() {
  const handle = setInterval(() => {}, 1000);
  return () => clearInterval(handle);
}

/**
 * @param {string} method
 * @param {string} path
 * @param {{ token?: string, body?: unknown, allow?: number[], timeoutMs?: number }} [options]
 */
async function call(method, path, options = {}) {
  const { token, body, allow = [], timeoutMs } = options;

  const headers = { Authorization: authHeader(token) };
  if (body !== undefined) headers["Content-Type"] = "application/json";

  const response = await fetch(`${JELLYFIN_URL}${path}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: timeoutMs ? AbortSignal.timeout(timeoutMs) : undefined,
  });

  if (!response.ok && !allow.includes(response.status)) {
    throw new JellyfinError(method, path, response.status, await response.text());
  }

  if (response.status === 204) return null;

  const text = await response.text();
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

export const api = {
  get: (path, token) => call("GET", path, { token }),
  post: (path, body, token) => call("POST", path, { token, body }),
};

/** Polls /System/Info/Public until the web server answers, or the deadline passes. */
export async function waitForServer({ timeoutMs = 180_000, log = console.log } = {}) {
  const deadline = Date.now() + timeoutMs;
  const release = keepAlive();
  let lastError = "no response";

  try {
    while (Date.now() < deadline) {
      try {
        // Jellyfin answers /System/Info/Public with a 503 and a plain-text
        // "Server is loading" body for the first few seconds, so a successful
        // parse into an object is the real readiness signal, not a 2xx alone.
        const info = await call("GET", "/System/Info/Public", { timeoutMs: 5000 });
        if (info && typeof info === "object" && info.Version) {
          log(`server up: ${info.ServerName} ${info.Version}`);
          return info;
        }
        lastError = "still loading";
      } catch (error) {
        lastError = error.message;
      }
      await new Promise((resolve) => setTimeout(resolve, 2000));
    }
  } finally {
    release();
  }

  throw new Error(
    `Jellyfin did not answer on ${JELLYFIN_URL} within ${timeoutMs} ms (last: ${lastError})`,
  );
}

/**
 * Drives the first-run wizard over REST so nobody has to click through it.
 *
 * The /Startup/* endpoints sit behind the FirstTimeSetupOrElevated policy, which
 * means they are callable without a token for exactly as long as the wizard is
 * incomplete. That window is what this uses. Once /Startup/Complete returns,
 * they lock down to admins.
 *
 * No-ops if the wizard is already done, so the script is safe to re-run.
 */
export async function completeStartupWizard({ username, password, log = console.log }) {
  const info = await call("GET", "/System/Info/Public");

  if (info.StartupWizardCompleted) {
    log("startup wizard: already completed, skipping");
    return false;
  }

  await call("POST", "/Startup/Configuration", {
    body: {
      ServerName: "Subtitle Sync Harness",
      UICulture: "en-US",
      MetadataCountryCode: "GB",
      PreferredMetadataLanguage: "en",
    },
  });

  // GET /Startup/User seeds the server-side "first user" slot that the
  // subsequent POST updates. The real wizard does this too.
  await call("GET", "/Startup/User");
  await call("POST", "/Startup/User", { body: { Name: username, Password: password } });

  await call("POST", "/Startup/RemoteAccess", {
    body: { EnableRemoteAccess: true, EnableAutomaticPortMapping: false },
  });

  await call("POST", "/Startup/Complete");

  log(`startup wizard: completed, admin "${username}" created`);
  return true;
}

/** @returns {Promise<{ token: string, userId: string, serverId: string }>} */
export async function authenticate(username, password) {
  const result = await call("POST", "/Users/AuthenticateByName", {
    body: { Username: username, Pw: password },
  });
  return {
    token: result.AccessToken,
    userId: result.User.Id,
    serverId: result.ServerId ?? result.User.ServerId,
  };
}

/**
 * Creates a plain, non-admin user if it is missing. Jellyfin's default policy
 * for a new user has IsAdministrator false, which is the whole point: it gives
 * the "menu item must not appear for a non-admin" test (#13) a real subject.
 */
export async function ensureUser({ token, username, password, log = console.log }) {
  const users = await call("GET", "/Users", { token });
  const existing = users.find((u) => u.Name === username);
  if (existing) {
    log(`user "${username}": already exists`);
    return existing.Id;
  }

  const created = await call("POST", "/Users/New", {
    token,
    body: { Name: username, Password: password },
  });
  log(`user "${username}": created (non-admin)`);
  return created.Id;
}

/**
 * Creates the movie library pointed at the seeded folder.
 *
 * Metadata fetchers are switched off deliberately. With them on, Jellyfin would
 * reach out to TMDB and rename our clip to whatever it matched, which would make
 * the Playwright lookup non-deterministic and the harness dependent on a third
 * party being up. Off, the item name comes straight from the file name.
 */
export async function ensureLibrary({ token, name, containerPath, log = console.log }) {
  const folders = await call("GET", "/Library/VirtualFolders", { token });
  if (folders.some((f) => f.Name === name)) {
    log(`library "${name}": already exists`);
    return false;
  }

  const query = new URLSearchParams({
    name,
    collectionType: "movies",
    paths: containerPath,
    refreshLibrary: "true",
  });

  await call("POST", `/Library/VirtualFolders?${query}`, {
    token,
    body: {
      LibraryOptions: {
        EnableRealtimeMonitor: true,
        // Keep external .srt files as separate tracks rather than trying to
        // fetch replacements.
        SubtitleDownloadLanguages: [],
        DisabledSubtitleFetchers: [],
        SubtitleFetcherOrder: [],
        TypeOptions: [
          {
            Type: "Movie",
            MetadataFetchers: [],
            MetadataFetcherOrder: [],
            ImageFetchers: [],
            ImageFetcherOrder: [],
          },
        ],
      },
    },
  });

  log(`library "${name}": created at ${containerPath}`);
  return true;
}

/** Polls the library until the seeded movie has been scanned in. */
export async function waitForItem({
  token,
  userId,
  name,
  timeoutMs = 180_000,
  refreshAfterMs = 45_000,
  log = console.log,
}) {
  const deadline = Date.now() + timeoutMs;
  const refreshAfter = Date.now() + refreshAfterMs;
  const release = keepAlive();
  let refreshed = false;

  try {
    while (Date.now() < deadline) {
      const query = new URLSearchParams({
        userId,
        recursive: "true",
        includeItemTypes: "Movie",
        searchTerm: name,
        limit: "20",
      });
      const result = await call("GET", `/Items?${query}`, { token, timeoutMs: 15_000 });
      const item = result?.Items?.find((i) => i.Name === name);
      if (item) {
        log(`item "${name}": id ${item.Id}`);
        return item;
      }
      if (!refreshed && Date.now() >= refreshAfter) {
        // Media added to a library that already exists is invisible until a
        // scan runs, and creating a library only scans it once. Kick one, once.
        //
        // Not immediately: a scan requested while the library-creation scan is
        // still running raced it and produced items named "Structured Clip
        // (2021)" - year still in the name - instead of "Structured Clip". So
        // give the first scan a grace period to finish on its own.
        refreshed = true;
        log(`item "${name}": not indexed yet, requesting a library scan`);
        await refreshLibrary(token);
      }
      await new Promise((resolve) => setTimeout(resolve, 2000));
    }
  } finally {
    release();
  }

  throw new Error(
    `Movie "${name}" never appeared in the library. Check that jellyfin-plugin/docker/media is seeded and mounted.`,
  );
}

/**
 * Kicks a library scan. Creating a library scans it, but adding media to one
 * that already exists does not, so a re-run that seeds a new fixture would
 * otherwise wait forever for an item the server has never looked for.
 */
export async function refreshLibrary(token) {
  await call("POST", "/Library/Refresh", { token });
}

/** Every plugin the server has loaded. Empty until the plugin DLL is dropped in. */
export async function listPlugins(token) {
  return (await call("GET", "/Plugins", { token })) ?? [];
}
