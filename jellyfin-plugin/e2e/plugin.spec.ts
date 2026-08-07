/**
 * The plugin-specific smoke tests from issue #19.
 *
 * THE SKIPPED ONES ARE SKIPPED ON PURPOSE: the behaviour they assert does not
 * exist yet. They are written out in full rather than left as TODOs so that
 * whoever lands each piece has a ready-made check: drop the built DLL into
 * jellyfin-plugin/docker/plugins/SubtitleSync/, restart the container, remove
 * the `.skip`, and the test either passes or tells you why not.
 *
 * The two Dashboard tests are live as of #3.
 *
 * Enabling issues:
 *   #3  scaffold the plugin           -> the two Dashboard tests
 *   #13 File Transformation injection -> the banner and Subtitles-menu tests
 *   #8  save endpoint                 -> the save block, live as of that issue
 *   #12 sync page UI                  -> the one remaining skipped sync run
 *
 * Selectors below are the current best guess at the 10.11 web client's DOM and
 * are expected to need adjustment when first run for real. That is the point of
 * having the harness: they can be adjusted against a live server in minutes.
 */

import { readFile, rm } from "node:fs/promises";
import path from "node:path";

import { expect, test, type Page } from "@playwright/test";

import {
  adminSession,
  findFixtureItemId,
  getItem,
  gotoItemDetail,
  hostPathFor,
  listPlugins,
  loginAsAdmin,
  loginAsViewer,
  refreshItem,
  saveSyncedSubtitle,
  viewerSession,
  waitForSubtitleStreams,
} from "./harness";

const PLUGIN_NAME = "Subtitle Sync";
const MENU_ITEM_TEXT = "Sync subtitles";

/** Opens the Subtitles menu on an item detail page. */
async function openSubtitlesMenu(page: Page): Promise<void> {
  await page.getByRole("button", { name: /subtitle/i }).first().click();
  await expect(page.locator(".actionSheetContent")).toBeVisible();
}

test.describe("plugin: Dashboard integration", () => {
  // Enabled by #3. Needs the built DLL in
  // jellyfin-plugin/docker/plugins/SubtitleSync/ and a container restart; see
  // jellyfin-plugin/README.md for the one-liner.
  test("plugin appears under Dashboard > Plugins", async ({ page }) => {
    const admin = await adminSession();
    const plugins = await listPlugins(admin);
    expect(
      plugins.some((p) => p.Name === PLUGIN_NAME),
      `${PLUGIN_NAME} is not loaded by the server`,
    ).toBe(true);

    await loginAsAdmin(page);
    await page.goto("/web/#/dashboard/plugins");

    await expect(page.getByText(PLUGIN_NAME, { exact: false }).first()).toBeVisible();
  });

  // Enabled by #3. The config page is the fallback route that #13 depends on
  // being genuinely usable, so "it renders" is the minimum bar.
  test("plugin config page renders", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto(`/web/#/configurationpage?name=${encodeURIComponent(PLUGIN_NAME)}`);

    await expect(page.locator("form, .pluginConfigurationPage").first()).toBeVisible();
  });

  // ENABLE WITH #13. Deliberately asserts the *absence* of File Transformation
  // is handled: this harness installs no other plugins, so the banner is the
  // expected state until one is added on purpose.
  test.skip("config page shows the File Transformation install banner when it is absent", async ({
    page,
  }) => {
    await loginAsAdmin(page);
    await page.goto(`/web/#/configurationpage?name=${encodeURIComponent(PLUGIN_NAME)}`);

    await expect(page.getByText(/File Transformation/i).first()).toBeVisible();

    // And the Dashboard flow still works without it: the sync page must be
    // reachable directly, not only via the injected menu item.
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);
    await page.goto(`/web/#/configurationpage?name=SubtitleSync&itemId=${itemId}`);
    await expect(page.locator("form, .pluginConfigurationPage").first()).toBeVisible();
  });
});

test.describe("plugin: Subtitles menu injection (#13)", () => {
  // ENABLE WITH #13, and only once the File Transformation plugin is installed
  // into the harness container. Injection is the riskiest behaviour in the whole
  // feature, so this is the test that matters most.
  test.skip('"Sync subtitles..." appears in the Subtitles menu for an admin', async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    await loginAsAdmin(page);
    await gotoItemDetail(page, itemId);
    await openSubtitlesMenu(page);

    await expect(page.getByText(MENU_ITEM_TEXT, { exact: false })).toBeVisible();
  });

  // ENABLE WITH #13. Permission checks enforced only server-side still leak the
  // affordance, so this asserts the menu item itself is hidden.
  test.skip("the menu item does not appear for a non-admin user", async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    await loginAsViewer(page);
    await gotoItemDetail(page, itemId);
    await openSubtitlesMenu(page);

    await expect(page.getByText(MENU_ITEM_TEXT, { exact: false })).toHaveCount(0);
  });

  // ENABLE WITH #13. The SPA requirement from the issue: the injection has to
  // survive client-side navigation between detail pages without a reload.
  test.skip("the menu item survives SPA navigation between pages", async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    await loginAsAdmin(page);
    await gotoItemDetail(page, itemId);

    // Leave to the home screen and come back without a full page load.
    await page.locator(".headerHomeButton").click();
    await expect(page.locator(".itemName")).toHaveCount(0);
    await page.goBack();

    await openSubtitlesMenu(page);
    await expect(page.getByText(MENU_ITEM_TEXT, { exact: false })).toBeVisible();
  });
});

test.describe("plugin: saving a synced subtitle (#8)", () => {
  const SYNCED_SRT =
    "1\n00:00:02,500 --> 00:00:04,500\nShifted line one\n\n" +
    "2\n00:00:06,000 --> 00:00:08,000\nShifted line two\n";

  /**
   * Everything this block writes into the seeded library, cleaned up after each
   * test. The save endpoint reports the path it actually used - collision
   * handling may have changed it - so the only reliable record is what came
   * back.
   */
  const written: string[] = [];

  test.afterEach(async () => {
    for (const containerPath of written.splice(0)) {
      await rm(hostPathFor(containerPath), { force: true });
    }

    // Put the library's view back where the seed left it, so the next test does
    // not see a stale track for a file that is gone.
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);
    await refreshItem(admin, itemId);
    await waitForSubtitleStreams(admin, itemId, (streams) => streams.length === 1);
  });

  /**
   * The meaningful part of the original end-to-end test, enabled now.
   *
   * It stops short of driving the browser: the sync page (#12) and the injected
   * menu item (#13) do not exist, so there is no UI to click. What it does cover
   * is the whole server half - the write lands as a sibling of the media file,
   * the track Jellyfin then indexes is the one the endpoint said it wrote, and
   * the file it was derived from is untouched.
   */
  test("a saved subtitle lands beside the media file and becomes a new track", async () => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    const before = await getItem(admin, itemId);
    const original = (before.MediaStreams ?? []).find(
      (s) => s.Type === "Subtitle" && s.IsExternal,
    );
    expect(original?.Path, "the fixture should have its seeded external track").toBeTruthy();

    const originalBytes = await readFile(hostPathFor(original!.Path!));

    const saved = await saveSyncedSubtitle(admin, itemId, 0, SYNCED_SRT);
    expect(saved.status, saved.detail).toBe(200);
    written.push(saved.path!);

    // Named after the media file, in the media file's own folder, and not the
    // file it came from.
    expect(saved.fileName).toBe("Sample Clip (2020).eng.synced.srt");
    expect(saved.overwroteSource).toBe(false);
    expect(saved.cueCount).toBe(2);
    expect(path.posix.dirname(saved.path!)).toBe(path.posix.dirname(original!.Path!));

    // THE ORIGINAL IS UNTOUCHED. This is the promise the whole issue rests on.
    expect(await readFile(hostPathFor(original!.Path!))).toEqual(originalBytes);

    // And the refresh the endpoint queued makes it a real track, without a
    // manual library scan.
    const streams = await waitForSubtitleStreams(admin, itemId, (s) => s.length > 1);
    expect(streams.some((s) => s.Path === saved.path)).toBe(true);
  });

  /**
   * The collision rule, which is why the response reports a path at all.
   */
  test("a second save takes the next collision suffix rather than replacing the first", async () => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    const first = await saveSyncedSubtitle(admin, itemId, 0, SYNCED_SRT);
    expect(first.status, first.detail).toBe(200);
    written.push(first.path!);

    const second = await saveSyncedSubtitle(admin, itemId, 0, SYNCED_SRT);
    expect(second.status, second.detail).toBe(200);
    written.push(second.path!);

    expect(second.path).not.toBe(first.path);
    expect(second.fileName).toBe("Sample Clip (2020).eng.synced.2.srt");
    expect(await readFile(hostPathFor(first.path!), "utf8")).toContain("Shifted line one");
  });

  /**
   * Concurrency, which is the failure mode that costs a user their work rather
   * than merely erroring. Every request must end up with its own file: none may
   * silently overwrite another's.
   */
  test("simultaneous saves each get their own file", async () => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    const payloads = Array.from(
      { length: 8 },
      (_, i) => `1\n00:00:01,500 --> 00:00:04,500\nRequest ${i}\n`,
    );

    const results = await Promise.all(
      payloads.map((srt) => saveSyncedSubtitle(admin, itemId, 0, srt)),
    );

    for (const result of results) {
      expect(result.status, result.detail).toBe(200);
      written.push(result.path!);
    }

    expect(new Set(results.map((r) => r.path)).size).toBe(payloads.length);

    const contents = await Promise.all(
      results.map((r) => readFile(hostPathFor(r.path!), "utf8")),
    );
    expect(new Set(contents).size).toBe(payloads.length);
  });

  /**
   * The permission split from the epic: read and analyse are
   * SubtitleManagement, saving is RequiresElevation. A non-admin must be stopped
   * by the server, not merely by a hidden menu item.
   */
  test("a non-admin user cannot save", async () => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);
    const viewer = await viewerSession();

    const result = await saveSyncedSubtitle(viewer, itemId, 0, SYNCED_SRT);

    expect(result.status).toBe(403);
  });

  /**
   * Attacker-controlled text stops at the endpoint. Nothing is created, and the
   * refusal names what is wrong.
   */
  test("content that is not SRT is refused with an actionable message", async () => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    const result = await saveSyncedSubtitle(
      admin,
      itemId,
      0,
      "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello\n",
    );

    expect(result.status).toBe(400);
    expect(result.detail).toContain("WebVTT");
  });

  // STILL GATED ON #12 AND #13: the same run driven through the plugin's own
  // sync page, with the analysis happening in the browser. There is no page to
  // open yet, so there is nothing here a selector could find.
  test.skip("a full sync run through the plugin page produces a sibling .srt", async ({
    page,
  }) => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    await loginAsAdmin(page);
    await page.goto(`/web/#/configurationpage?name=SubtitleSync&itemId=${itemId}`);

    await page.getByRole("button", { name: /sync/i }).first().click();

    // The analysis runs in the browser (VAD + lib/analyze), so this waits on the
    // plugin's own completion signal rather than a fixed sleep.
    await expect(page.getByText(/saved|complete/i).first()).toBeVisible({ timeout: 300_000 });

    const streams = await waitForSubtitleStreams(admin, itemId, (s) => s.length > 1);
    expect(streams.some((s) => s.Path?.includes(".synced.srt"))).toBe(true);
  });
});
