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
 *   #12 sync page UI                  -> the sync page block, live as of that
 *                                        issue
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
  findSyncableItemId,
  getItem,
  gotoItemDetail,
  hostPathFor,
  listPlugins,
  loginAsAdmin,
  loginAsViewer,
  refreshItem,
  saveSyncedSubtitle,
  SYNCABLE_KNOWN_OFFSET,
  SYNCABLE_NAME,
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

});

/**
 * The end-to-end run, driven through the plugin's own page (#12).
 *
 * Deliberately against SYNCABLE_NAME rather than the Sample Clip the block
 * above uses. Sample Clip reads as ~92% speech to the VAD, so a sync of it has
 * no right answer to assert; Structured Clip's track is displaced by exactly
 * SYNCABLE_KNOWN_OFFSET and the same recovery is asserted at the unit level by
 * test/structured.test.ts. Asserting an offset against the wrong fixture passes
 * vacuously.
 */
test.describe("plugin: the sync page (#12)", () => {
  const SYNC_PAGE = "SubtitleSyncPage";

  /** Files this block wrote into the seeded library, removed after each test. */
  const written: string[] = [];

  test.afterEach(async () => {
    if (written.length === 0) return;

    for (const containerPath of written.splice(0)) {
      await rm(hostPathFor(containerPath), { force: true });
    }

    const admin = await adminSession();
    const itemId = await findSyncableItemId(admin);
    await refreshItem(admin, itemId);
    await waitForSubtitleStreams(admin, itemId, (streams) => streams.length === 1);
  });

  test("the page opens from an item id and lists the item's tracks", async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findSyncableItemId(admin);

    await loginAsAdmin(page);
    await page.goto(`/web/#/configurationpage?name=${SYNC_PAGE}&itemId=${itemId}`);

    await expect(page.locator("#ssItemName")).toHaveText(SYNCABLE_NAME, { timeout: 60_000 });
    // The picker is the other entry path and must be out of the way here.
    await expect(page.locator("#ssPicker")).toBeHidden();
    await expect(page.locator("#ssSubtitle option")).toHaveCount(1);
    await expect(page.locator("#ssAudio option")).toHaveCount(1);
    await expect(page.locator("#ssRun")).toBeEnabled();
  });

  test("the page opens with no item id and finds one through the picker", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto(`/web/#/configurationpage?name=${SYNC_PAGE}`);

    // This is the PRIMARY route: Dashboard > Plugins > Subtitle Sync, with no
    // item in hand. It cannot be a stub - the injected menu item that supplies
    // an itemId depends on a third-party plugin that may not be installed.
    const search = page.locator("#ssSearch");
    await expect(search).toBeVisible({ timeout: 60_000 });
    await search.fill(SYNCABLE_NAME);

    const result = page.locator("#ssPickerResults button", { hasText: SYNCABLE_NAME });
    await expect(result).toBeVisible({ timeout: 30_000 });
    await result.click();

    await expect(page.locator("#ssItemName")).toHaveText(SYNCABLE_NAME);
    await expect(page.locator("#ssRun")).toBeEnabled();
  });

  test("a full sync run through the plugin page produces a sibling .srt", async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findSyncableItemId(admin);

    const before = await getItem(admin, itemId);
    const original = (before.MediaStreams ?? []).find((s) => s.Type === "Subtitle" && s.IsExternal);
    expect(original?.Path, "the fixture should have its seeded external track").toBeTruthy();
    const originalBytes = await readFile(hostPathFor(original!.Path!));

    await loginAsAdmin(page);
    await page.goto(`/web/#/configurationpage?name=${SYNC_PAGE}&itemId=${itemId}`);
    await expect(page.locator("#ssRun")).toBeEnabled({ timeout: 60_000 });

    await page.locator("#ssRun").click();

    // The analysis runs in this browser - PCM from the server, VAD in wasm, then
    // lib/analyze - so wait on the page's own completion rather than a sleep.
    await expect(page.locator("#ssResult")).toBeVisible({ timeout: 300_000 });

    // THE OFFSET IS THE POINT. The seeded track is displaced by exactly
    // SYNCABLE_KNOWN_OFFSET, and the page reports what it recovered.
    const recovered = Number(await page.locator("#ssNudgeOffset").inputValue());
    expect(recovered).toBeCloseTo(SYNCABLE_KNOWN_OFFSET, 2);

    await page.locator("#ssSave").click();
    await expect(page.locator("#ssSaveNote")).toContainText(/^Saved /, { timeout: 60_000 });

    const streams = await waitForSubtitleStreams(admin, itemId, (s) => s.length > 1);
    const saved = streams.find((s) => s.Path?.includes(".synced.srt"));
    expect(saved?.Path, `no synced track appeared: ${JSON.stringify(streams)}`).toBeTruthy();
    written.push(saved!.Path!);

    // THE ORIGINAL IS UNTOUCHED.
    expect(await readFile(hostPathFor(original!.Path!))).toEqual(originalBytes);

    // And the file that landed really is the corrected one: every cue moved by
    // the offset the page recovered, which is what "synced" has to mean.
    const originalText = originalBytes.toString("utf8");
    const savedText = await readFile(hostPathFor(saved!.Path!), "utf8");
    const shift = firstCueStart(savedText) - firstCueStart(originalText);
    expect(shift).toBeCloseTo(SYNCABLE_KNOWN_OFFSET, 1);
  });

  test("the saved file is offered as a download even without saving", async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findSyncableItemId(admin);

    await loginAsAdmin(page);
    await page.goto(`/web/#/configurationpage?name=${SYNC_PAGE}&itemId=${itemId}`);
    await expect(page.locator("#ssRun")).toBeEnabled({ timeout: 60_000 });
    await page.locator("#ssRun").click();
    await expect(page.locator("#ssResult")).toBeVisible({ timeout: 300_000 });

    const download = page.waitForEvent("download");
    await page.locator("#ssDownload").click();
    expect((await download).suggestedFilename()).toMatch(/\.synced\.srt$/);
  });

  test("a nudge re-times the output without re-analysing", async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findSyncableItemId(admin);

    await loginAsAdmin(page);
    await page.goto(`/web/#/configurationpage?name=${SYNC_PAGE}&itemId=${itemId}`);
    await expect(page.locator("#ssRun")).toBeEnabled({ timeout: 60_000 });
    await page.locator("#ssRun").click();
    await expect(page.locator("#ssResult")).toBeVisible({ timeout: 300_000 });

    const preview = page.locator("#ssPreview");
    const detected = await preview.textContent();

    // No request may leave the page for this: applyCorrection is pure.
    let requests = 0;
    page.on("request", (request) => {
      if (request.url().includes("/SubtitleSync/")) requests++;
    });

    await page.locator("#ssNudgeOffset").fill("-1.000");
    await expect(preview).not.toHaveText(detected!);
    expect(requests, "nudging must not talk to the server").toBe(0);
  });

  /**
   * Saving is administrator-only while analysing is not, so a user who got a
   * result may still be refused - and must be told, not left staring at a page
   * that looks broken.
   *
   * The refusal is injected rather than driven as the viewer account because the
   * 10.11 web client routes every plugin page behind an admin guard: a non-admin
   * never reaches this page at all, whatever their subtitle permission.
   */
  test("a refused save explains itself and leaves the download working", async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findSyncableItemId(admin);

    await loginAsAdmin(page);
    await page.route("**/SubtitleSync/Save/**", (route) =>
      route.fulfill({
        status: 403,
        contentType: "application/json",
        body: JSON.stringify({ title: "Forbidden" }),
      }),
    );

    await page.goto(`/web/#/configurationpage?name=${SYNC_PAGE}&itemId=${itemId}`);
    await expect(page.locator("#ssRun")).toBeEnabled({ timeout: 60_000 });
    await page.locator("#ssRun").click();
    await expect(page.locator("#ssResult")).toBeVisible({ timeout: 300_000 });

    await page.locator("#ssSave").click();
    await expect(page.locator("#ssSaveNote")).toContainText(/administrator/i);
    await expect(page.locator("#ssDownload")).toBeEnabled();
  });

  /**
   * The client's own route guard, asserted rather than assumed. It is the reason
   * the Dashboard path is admin-only in practice however the server's
   * SubtitleManagement policy is set, and the reason #13 cannot simply link a
   * non-admin here.
   */
  test("a non-admin is bounced by the client before the page loads", async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findSyncableItemId(admin);

    await loginAsViewer(page);
    await page.goto(`/web/#/configurationpage?name=${SYNC_PAGE}&itemId=${itemId}`);

    await page.waitForURL(/#\/home/, { timeout: 60_000 });
    await expect(page.locator("#subtitleSyncPage")).toHaveCount(0);
  });
});

/** Seconds of the first cue's start time in an SRT document. */
function firstCueStart(srt: string): number {
  const match = /(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->/.exec(srt);
  if (!match) throw new Error("no cue found in the subtitle");
  const [, h, m, s, ms] = match;
  return Number(h) * 3600 + Number(m) * 60 + Number(s) + Number(ms) / 1000;
}
