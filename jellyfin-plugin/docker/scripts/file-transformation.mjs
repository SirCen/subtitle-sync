// Installs (or removes) the File Transformation plugin in the harness container.
//
//   node jellyfin-plugin/docker/scripts/file-transformation.mjs install
//   node jellyfin-plugin/docker/scripts/file-transformation.mjs uninstall
//   node jellyfin-plugin/docker/scripts/file-transformation.mjs status
//
// Why this exists: the Subtitles-menu item (#13) is injected into the web
// client by a third-party plugin, and Jellyfin 10.11 has no dependency
// mechanism that could pull it in for us. The three Playwright specs that assert
// the menu item therefore need it present, and "click through the Dashboard
// once" is not a reproducible harness.
//
// WHY NOT THE DASHBOARD INSTALLER. Jellyfin can install this itself - add the
// repository, POST /Packages/Installed/{name} - and that is the route a real
// user takes. It is the wrong route for a harness:
//
//   - The manifest publishes SIX entries all numbered 2.5.11.0, one per Jellyfin
//     patch release, distinguished only by targetAbi. Asking for "version
//     2.5.11.0" does not say which, and which one you get is up to Jellyfin's
//     compatibility filter rather than up to this script.
//   - It needs iamparadox.dev to be reachable from inside the container at the
//     moment the test runs, rather than from this machine once.
//   - Uninstalling through the API leaves the plugin loaded until a restart, and
//     the "what happens with it absent" check needs it genuinely gone.
//
// So this pins the exact asset for the Jellyfin tag the harness runs, verifies
// the MD5 the manifest publishes, and unpacks it into a bind mount that can be
// emptied again. One version, one checksum, reproducible from a purged server.

import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";

import { DOCKER_DIR, JELLYFIN_URL, authHeader } from "./config.mjs";

/**
 * The plugin's id, from its own FileTransformationPlugin.cs. Also what our
 * plugin looks for at runtime - Injection/FileTransformationFacts.cs.
 */
export const FILE_TRANSFORMATION_GUID = "5e87cc92-571a-4d8d-8d98-d2d4147f9f90";

/** Where its repository manifest lives. Read once, at install time, from here. */
const MANIFEST_URL = "https://www.iamparadox.dev/jellyfin/plugins/manifest.json";

/**
 * The Jellyfin release the harness pins (docker-compose.yml). The manifest
 * publishes one asset per Jellyfin patch release under the same version number,
 * so this - not the version - is what selects the download.
 */
const TARGET_ABI = "10.11.11.0";

/**
 * Bind-mounted at /config/plugins/FileTransformation. Gitignored: the zip is
 * fetched, not committed.
 */
export const PLUGIN_DIR = path.join(DOCKER_DIR, "plugins", "FileTransformation");

/** Marker file recording what was installed, so `status` needs no network. */
const STAMP = path.join(PLUGIN_DIR, ".installed.json");

const CONTAINER = "subtitle-sync-jellyfin";

/**
 * Where Jellyfin MIGRATES the plugin to on first start.
 *
 * This is the trap the harness README warns about for our own plugin, and it
 * bites harder here: emptying the bind mount does not uninstall anything,
 * because the server has long since copied the DLL to a versioned directory and
 * loads from there. Uninstall has to delete both.
 *
 * Worse, leaving the migrated copy while staging a fresh one gives Jellyfin TWO
 * File Transformation assemblies in two load contexts. It loads both, the second
 * fails to construct with an InvalidCastException between two identically named
 * types, and every request for /web/ then 500s. Hence the quoting: the path
 * contains a space, and an unquoted glob in `sh -c` splits into two arguments
 * and deletes nothing.
 */
const MIGRATED_GLOB = '"/config/plugins/File Transformation_"*';

function docker(...args) {
  return execFileSync("docker", args, { encoding: "utf8" });
}

/** The manifest entry for the ABI this harness runs. */
async function findRelease() {
  const response = await fetch(MANIFEST_URL);
  if (!response.ok) {
    throw new Error(`GET ${MANIFEST_URL} -> ${response.status}`);
  }

  const packages = await response.json();
  const pkg = packages.find((p) => p.guid?.toLowerCase() === FILE_TRANSFORMATION_GUID);
  if (!pkg) {
    throw new Error(`No package with guid ${FILE_TRANSFORMATION_GUID} in the manifest.`);
  }

  const release = pkg.versions?.find((v) => v.targetAbi === TARGET_ABI);
  if (!release) {
    const abis = [...new Set(pkg.versions?.map((v) => v.targetAbi) ?? [])].join(", ");
    throw new Error(
      `No ${pkg.name} build for targetAbi ${TARGET_ABI}. Published ABIs: ${abis}. ` +
        "Either the plugin has not caught up with this Jellyfin release or the " +
        "harness has moved on from 10.11.11.",
    );
  }

  return { name: pkg.name, ...release };
}

/**
 * Extracts a zip into a directory.
 *
 * Hand-rolled, which needs justifying. Node has no zip reader in its standard
 * library, this repository has no dependency that provides one, and adding a
 * package to npm's production graph so a local test harness can unpack one file
 * is a bad trade. The container has no `unzip` or `python3` either, so doing it
 * over `docker exec` is not available. What is left is the format itself, which
 * for this purpose is a hundred lines: walk the central directory, inflate each
 * entry.
 *
 * Only what this archive uses is supported - stored and deflated entries, no
 * encryption, no zip64. Anything else throws rather than silently producing a
 * broken plugin directory.
 *
 * @param {Buffer} zip The archive.
 * @param {string} destination Directory to write into. Created if absent.
 */
function extractZip(zip, destination) {
  // End of central directory record. Scanned backwards because it is followed
  // by a variable-length comment.
  const EOCD = 0x06054b50;
  let eocd = -1;
  for (let at = zip.length - 22; at >= 0; at--) {
    if (zip.readUInt32LE(at) === EOCD) {
      eocd = at;
      break;
    }
  }

  if (eocd < 0) {
    throw new Error("Not a zip archive: no end-of-central-directory record.");
  }

  const entryCount = zip.readUInt16LE(eocd + 10);
  let at = zip.readUInt32LE(eocd + 16);

  fs.mkdirSync(destination, { recursive: true });

  for (let i = 0; i < entryCount; i++) {
    if (zip.readUInt32LE(at) !== 0x02014b50) {
      throw new Error(`Corrupt central directory at entry ${i}.`);
    }

    const method = zip.readUInt16LE(at + 10);
    const compressedSize = zip.readUInt32LE(at + 20);
    const nameLength = zip.readUInt16LE(at + 28);
    const extraLength = zip.readUInt16LE(at + 30);
    const commentLength = zip.readUInt16LE(at + 32);
    const localOffset = zip.readUInt32LE(at + 42);
    const name = zip.toString("utf8", at + 46, at + 46 + nameLength);

    at += 46 + nameLength + extraLength + commentLength;

    // Directories are implied by the file paths; no need to create them empty.
    if (name.endsWith("/")) continue;

    // A zip is attacker-controllable in general. This one is checksummed
    // against a manifest, but refusing traversal costs one line.
    if (name.includes("..") || path.isAbsolute(name)) {
      throw new Error(`Refusing to extract suspicious path "${name}".`);
    }

    // The local header repeats the name and extra fields, and its lengths can
    // differ from the central directory's, so they are read again here.
    const localNameLength = zip.readUInt16LE(localOffset + 26);
    const localExtraLength = zip.readUInt16LE(localOffset + 28);
    const dataStart = localOffset + 30 + localNameLength + localExtraLength;
    const data = zip.subarray(dataStart, dataStart + compressedSize);

    let contents;
    if (method === 0) {
      contents = data;
    } else if (method === 8) {
      contents = zlib.inflateRawSync(data);
    } else {
      throw new Error(`Unsupported zip compression method ${method} for "${name}".`);
    }

    const target = path.join(destination, name);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, contents);
  }
}

/**
 * Writes the meta.json Jellyfin expects beside a plugin's assemblies.
 *
 * THE RELEASE ZIP DOES NOT CONTAIN ONE. Jellyfin's own installer synthesises it
 * from the repository manifest entry, so a zip dropped into /config/plugins by
 * hand arrives without it - and a plugin folder with no meta.json gets an
 * INVENTED identity: Jellyfin derives a guid from the folder name, calls the
 * plugin "FileTransformation", gives it the server's version number, and
 * (having no idea what it is) can end up marking it Deleted and skipping it on
 * the next start. That failure looks exactly like the plugin not working, with
 * nothing in the log that points at the cause.
 *
 * So the manifest is written here, from the same repository entry the download
 * came from, which is what makes the folder a real install rather than a pile
 * of DLLs.
 */
function writeManifest(release) {
  fs.writeFileSync(
    path.join(PLUGIN_DIR, "meta.json"),
    JSON.stringify(
      {
        category: "General",
        changelog: "",
        description: "Allows plugins to transform files served by the web client.",
        guid: FILE_TRANSFORMATION_GUID,
        name: release.name,
        overview: "Installed by the subtitle-sync harness; see scripts/file-transformation.mjs.",
        owner: "IAmParadox27",
        targetAbi: release.targetAbi,
        timestamp: new Date().toISOString().replace("Z", "0000Z"),
        version: release.version,
        status: "Active",
        autoUpdate: false,
        imagePath: "",
        assemblies: [],
      },
      null,
      2,
    ),
  );
}

export function isInstalled() {
  return fs.existsSync(STAMP);
}

export async function install({ log = console.log } = {}) {
  if (isInstalled()) {
    const stamp = JSON.parse(fs.readFileSync(STAMP, "utf8"));
    log(`File Transformation ${stamp.version} (${stamp.targetAbi}): already staged`);
    return { changed: false, ...stamp };
  }

  const release = await findRelease();
  log(`--- fetching ${release.name} ${release.version} for Jellyfin ${release.targetAbi}`);

  const response = await fetch(release.sourceUrl, { redirect: "follow" });
  if (!response.ok) {
    throw new Error(`GET ${release.sourceUrl} -> ${response.status}`);
  }

  const zip = Buffer.from(await response.arrayBuffer());

  // The manifest publishes an MD5 and Jellyfin's own installer checks it. Not a
  // security control - the manifest and the asset come from the same place -
  // but it does catch a truncated download, which otherwise shows up as an
  // inexplicably absent plugin.
  const digest = createHash("md5").update(zip).digest("hex");
  if (digest.toLowerCase() !== release.checksum.toLowerCase()) {
    throw new Error(
      `Checksum mismatch for ${release.sourceUrl}: manifest says ${release.checksum}, got ${digest}.`,
    );
  }

  // Straight into the bind mount, so the container sees the files without a
  // docker cp and without needing any archive tooling of its own.
  fs.mkdirSync(PLUGIN_DIR, { recursive: true });
  for (const entry of fs.readdirSync(PLUGIN_DIR)) {
    if (entry !== ".gitkeep") {
      fs.rmSync(path.join(PLUGIN_DIR, entry), { recursive: true, force: true });
    }
  }

  extractZip(zip, PLUGIN_DIR);
  writeManifest(release);

  const stamp = {
    version: release.version,
    targetAbi: release.targetAbi,
    sourceUrl: release.sourceUrl,
    checksum: release.checksum,
    installedAt: new Date().toISOString(),
  };
  fs.writeFileSync(STAMP, JSON.stringify(stamp, null, 2));

  log(`--- staged ${release.name} ${release.version} into ${PLUGIN_DIR}`);
  log("--- a container restart is required before Jellyfin loads it");

  return { changed: true, ...stamp };
}

export function uninstall({ log = console.log } = {}) {
  const staged = fs.existsSync(PLUGIN_DIR);

  // Both places, and the second is the one people forget. Jellyfin migrates
  // loose plugin DLLs into a versioned directory on first start and loads from
  // there ever after, so clearing the bind mount alone uninstalls nothing.
  try {
    docker("exec", CONTAINER, "sh", "-c", `rm -rf ${MIGRATED_GLOB} /config/plugins/FileTransformation/*`);
  } catch {
    log("--- container is not running; clearing the staging directory only");
  }

  if (staged) {
    fs.rmSync(PLUGIN_DIR, { recursive: true, force: true });
    fs.mkdirSync(PLUGIN_DIR, { recursive: true });
    // The directory has to exist for the compose bind mount, and the .gitkeep
    // is what keeps it in the repository.
    fs.writeFileSync(path.join(PLUGIN_DIR, ".gitkeep"), "");
  }

  log("--- File Transformation removed. Restart the container for it to take effect.");
  return { changed: staged };
}

/** Asks the running server, which is the only answer that counts. */
export async function status({ token }) {
  const response = await fetch(`${JELLYFIN_URL}/Plugins`, {
    headers: { Authorization: authHeader(token) },
  });
  const plugins = await response.json();
  const found = plugins.find(
    (p) => p.Id?.replace(/-/g, "").toLowerCase() === FILE_TRANSFORMATION_GUID.replace(/-/g, ""),
  );
  return found ?? null;
}

const invokedDirectly = process.argv[1]?.replace(/\\/g, "/").endsWith("scripts/file-transformation.mjs");
if (invokedDirectly) {
  const command = process.argv[2] ?? "install";

  const run = async () => {
    if (command === "install") return install();
    if (command === "uninstall") return uninstall();
    if (command === "status") {
      const { authenticate } = await import("./jellyfin-api.mjs");
      const { ADMIN_USERNAME, ADMIN_PASSWORD } = await import("./config.mjs");
      const admin = await authenticate(ADMIN_USERNAME, ADMIN_PASSWORD);
      const found = await status({ token: admin.token });
      console.log(found ? `${found.Name} ${found.Version} (${found.Status})` : "not loaded");
      return found;
    }
    throw new Error(`Unknown command "${command}". Use install, uninstall or status.`);
  };

  run().catch((error) => {
    console.error(`\nfile-transformation ${command} failed: ${error.message}`);
    process.exit(1);
  });
}
