/**
 * Shared helpers for the Jellyfin smoke tests.
 *
 * Config values come from ../docker/harness.config.json, the same file the
 * Docker scripts read, so the port, credentials and fixture name have exactly
 * one definition. See jellyfin-plugin/docker/README.md for how to bring the
 * server up.
 */

import { expect, type Page } from "@playwright/test";

import config from "../docker/harness.config.json";

export const JELLYFIN_PORT = process.env.JELLYFIN_PORT ?? String(config.port);
export const JELLYFIN_URL =
  process.env.JELLYFIN_URL ?? `http://127.0.0.1:${JELLYFIN_PORT}`;

export const ADMIN_USERNAME = process.env.JELLYFIN_ADMIN_USER ?? config.adminUsername;
export const ADMIN_PASSWORD = process.env.JELLYFIN_ADMIN_PASSWORD ?? config.adminPassword;
export const VIEWER_USERNAME = process.env.JELLYFIN_VIEWER_USER ?? config.viewerUsername;
export const VIEWER_PASSWORD = process.env.JELLYFIN_VIEWER_PASSWORD ?? config.viewerPassword;

export const MOVIE_NAME = config.movieName;

/** Jellyfin requires this header shape on authenticated requests. */
export function authHeader(token?: string): string {
  const parts = [
    'Client="subtitle-sync-harness"',
    'Device="playwright"',
    'DeviceId="subtitle-sync-harness-e2e"',
    'Version="0.1.0"',
  ];
  if (token) parts.push(`Token="${token}"`);
  return `MediaBrowser ${parts.join(", ")}`;
}

export interface Session {
  token: string;
  userId: string;
}

/** Authenticates over REST. Used to look up ids the UI does not expose. */
export async function authenticate(
  username: string,
  password: string,
): Promise<Session> {
  const response = await fetch(`${JELLYFIN_URL}/Users/AuthenticateByName`, {
    method: "POST",
    headers: {
      Authorization: authHeader(),
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ Username: username, Pw: password }),
  });

  if (!response.ok) {
    throw new Error(
      `AuthenticateByName failed for "${username}": ${response.status} ${await response.text()}`,
    );
  }

  const result = (await response.json()) as {
    AccessToken: string;
    User: { Id: string };
  };
  return { token: result.AccessToken, userId: result.User.Id };
}

export function adminSession(): Promise<Session> {
  return authenticate(ADMIN_USERNAME, ADMIN_PASSWORD);
}

export interface MediaStream {
  Type: string;
  IsExternal?: boolean;
  Path?: string;
}

export interface Item {
  Id: string;
  Name: string;
  MediaStreams?: MediaStream[];
}

/** Fetches an item with its media streams. */
export async function getItem(session: Session, itemId: string): Promise<Item> {
  const response = await fetch(
    `${JELLYFIN_URL}/Items/${itemId}?userId=${session.userId}`,
    { headers: { Authorization: authHeader(session.token) } },
  );
  if (!response.ok) {
    throw new Error(`GET /Items/${itemId} -> ${response.status}`);
  }
  return (await response.json()) as Item;
}

/** Resolves the seeded fixture movie's item id. */
export async function findFixtureItemId(session: Session): Promise<string> {
  const query = new URLSearchParams({
    userId: session.userId,
    recursive: "true",
    includeItemTypes: "Movie",
    searchTerm: MOVIE_NAME,
    limit: "20",
  });

  const response = await fetch(`${JELLYFIN_URL}/Items?${query}`, {
    headers: { Authorization: authHeader(session.token) },
  });
  const result = (await response.json()) as { Items?: Item[] };
  const item = result.Items?.find((i) => i.Name === MOVIE_NAME);

  if (!item) {
    throw new Error(
      `Fixture movie "${MOVIE_NAME}" is not in the library. Run \`npm run jf:up\` first.`,
    );
  }
  return item.Id;
}

/** Every plugin the server has loaded. */
export async function listPlugins(
  session: Session,
): Promise<Array<{ Id: string; Name: string; Version: string }>> {
  const response = await fetch(`${JELLYFIN_URL}/Plugins`, {
    headers: { Authorization: authHeader(session.token) },
  });
  return (await response.json()) as Array<{ Id: string; Name: string; Version: string }>;
}

/**
 * Signs in through the web client's own login form, which is the point: this
 * exercises the real client rather than forging a token into localStorage.
 *
 * With more than one visible user Jellyfin can show a user-picker splash first,
 * so the manual form is unhidden defensively before filling it.
 */
export async function login(page: Page, username: string, password: string): Promise<void> {
  // Enter at /web/ and let the client route itself to #/login?...&url=/home.
  // Do NOT navigate straight to #/login.html: that is the deprecated URL format
  // the 10.11 client warns about, and it lands on a FallbackRoute that
  // authenticates fine but never leaves the login view, which looks exactly
  // like a failed login.
  await page.goto("/web/");

  const manualLoginButton = page.locator("button.btnManual");
  if (await manualLoginButton.isVisible().catch(() => false)) {
    await manualLoginButton.click();
  }

  const name = page.locator("#txtManualName");
  await expect(name).toBeVisible({ timeout: 30_000 });

  await name.fill(username);
  await page.locator("#txtManualPassword").fill(password);
  await page.locator("button.button-submit").click();

  // Reaching #/home is the signal: the client only routes there after the
  // credentials are stored and the session is live.
  await page.waitForURL(/#\/home/, { timeout: 60_000 });
  await expect(page.locator(".headerUserButton")).toBeVisible({ timeout: 30_000 });
}

export function loginAsAdmin(page: Page): Promise<void> {
  return login(page, ADMIN_USERNAME, ADMIN_PASSWORD);
}

export function loginAsViewer(page: Page): Promise<void> {
  return login(page, VIEWER_USERNAME, VIEWER_PASSWORD);
}

/** Navigates to an item's detail page and waits for its title to render. */
export async function gotoItemDetail(page: Page, itemId: string): Promise<void> {
  await page.goto(`/web/#/details?id=${itemId}`);
  await expect(page.locator(".itemName").first()).toBeVisible({ timeout: 30_000 });
}
