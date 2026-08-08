/**
 * The plugin-specific smoke tests from issue #19. Nothing here is skipped any
 * more: every issue these were written ahead of has landed.
 *
 * Written against a live 10.11.11 client rather than guessed, which matters most
 * for the injection block - the client has no "Subtitles" button, the entry
 * lives inside the "..." menu, and after a client-side navigation the previous
 * page's markup is still in the DOM. All three caught a wrong selector here.
 *
 * WHAT THE SERVER HAS TO LOOK LIKE. `npm run jf:up` does all of it:
 *   - the built plugin DLL staged in docker/plugins/SubtitleSync/
 *   - the File Transformation plugin installed (docker/scripts/file-transformation.mjs)
 *   - the `viewer` account holding EnableSubtitleManagement but NOT admin
 * The last one is not incidental. Without subtitle rights the viewer sees no
 * "Edit subtitles" entry either, and the "not for a non-admin" test would pass
 * without proving anything.
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
  MOVIE_NAME,
  refreshItem,
  saveSyncedSubtitle,
  spaNavigateToItem,
  SYNCABLE_KNOWN_OFFSET,
  SYNCABLE_NAME,
  viewerSession,
  waitForSubtitleStreams,
} from "./harness";

const PLUGIN_NAME = "Subtitle Sync";
const MENU_ITEM_TEXT = "Sync subtitles";

/** `data-id` the injected script stamps on its own menu item. */
const MENU_ITEM_ID = "subtitlesync-sync";

/** The client's own Subtitles entry, which ours is inserted beneath. */
const ANCHOR_MENU_ITEM_ID = "editsubtitles";

/**
 * Opens the context menu that carries the Subtitles entry.
 *
 * There is no "Subtitles" button on a 10.11 detail page. The entry lives inside
 * the "..." overflow menu, whose button is `.btnMoreCommands` (title "More") and
 * whose menu is a `.actionSheet` dialog of `button[data-id]` items. Checked
 * against a live 10.11.11 client, not guessed.
 *
 * `visible: true` on the button because the client keeps the markup of pages you
 * have already visited in the DOM.
 */
async function openContextMenu(page: Page): Promise<void> {
  await page.locator(".btnMoreCommands").filter({ visible: true }).first().click();
  await expect(page.locator(".actionSheetContent")).toBeVisible();
}

/**
 * Closes it again.
 *
 * A real pointer event outside the dialog, because that is the only thing that
 * works. Checked against 10.11.11: `page.keyboard.press("Escape")` does not
 * close it, a synthetic `keydown` on `document` or on the dialog does not close
 * it, and a programmatic `.click()` on `.dialogBackdrop` does not either - the
 * client is listening for a trusted pointer event. `history.back()` also works
 * but changes the URL, which this test is about.
 */
async function closeContextMenu(page: Page): Promise<void> {
  await page.mouse.click(5, 5);
  await expect(page.locator(".actionSheet")).toHaveCount(0);
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

  /**
   * The install note, driven by forcing the server's answer rather than by
   * uninstalling File Transformation.
   *
   * The harness now installs File Transformation - the three specs below cannot
   * run without it - so the absent case cannot also be the live state of the
   * server. Injecting the response is how the rest of this suite handles the
   * same problem (see the refused-save test), and it buys something a real
   * uninstall would not: the banner is asserted against a *specific* status
   * value, so it keeps working when the reason changes.
   *
   * The genuine uninstalled path - plugin still loads, client still works,
   * Dashboard route still works - is checked by the assertions at the end of
   * this test plus `npm run jf:ft:uninstall`, documented in docker/README.md.
   */
  test("config page shows the File Transformation install note when it is absent", async ({
    page,
  }) => {
    await loginAsAdmin(page);

    await page.route("**/SubtitleSync/Status", (route) =>
      route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          Availability: "NotInstalled",
          MenuItemActive: false,
          FileTransformationVersion: null,
          Detail:
            "File Transformation is not installed, so the Subtitles menu item will not appear.",
          RepositoryUrl: "https://www.iamparadox.dev/jellyfin/plugins/manifest.json",
          ProjectUrl: "https://github.com/IAmParadox27/jellyfin-plugin-file-transformation",
          PluginName: "File Transformation",
        }),
      }),
    );

    await page.goto(`/web/#/configurationpage?name=${encodeURIComponent(PLUGIN_NAME)}`);

    const notice = page.locator("#fileTransformationNotice");
    await expect(notice).toBeVisible({ timeout: 60_000 });
    await expect(notice).toContainText(/File Transformation/i);

    // It must say how to get it, with the repository URL - that is the whole
    // point of the note, since Jellyfin cannot install it for us.
    await expect(page.locator("#fileTransformationHowTo")).toBeVisible();
    await expect(page.locator("#fileTransformationRepo")).toHaveText(
      "https://www.iamparadox.dev/jellyfin/plugins/manifest.json",
    );

    // And the Dashboard flow still works without it: the sync page must be
    // reachable directly, not only via the injected menu item.
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);
    await page.goto(`/web/#/configurationpage?name=SubtitleSyncPage&itemId=${itemId}`);
    await expect(page.locator("#subtitleSyncPage")).toBeVisible({ timeout: 60_000 });
  });

  /**
   * The other half, and the one that would otherwise rot: with File
   * Transformation genuinely installed the note must NOT appear. A banner that
   * is always on is indistinguishable from a banner nobody wired up.
   */
  test("config page stays quiet when File Transformation is working", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto(`/web/#/configurationpage?name=${encodeURIComponent(PLUGIN_NAME)}`);

    // Wait for something the page loads asynchronously, so "hidden" is a
    // verdict rather than a race with the fetch.
    await expect(page.locator("#signalCacheReadout")).not.toHaveText("Loading...", {
      timeout: 60_000,
    });
    await expect(page.locator("#fileTransformationNotice")).toBeHidden();
  });
});

/**
 * The riskiest behaviour in the whole feature, so these are the tests that
 * matter most. They need the File Transformation plugin installed in the
 * container; `npm run jf:up` does that (docker/scripts/file-transformation.mjs).
 *
 * If they start failing after a Jellyfin update, read the failure carefully
 * before "fixing" it. The correct outcome of a client whose markup has changed
 * is that the item is ABSENT, quietly, and everything else still works - so a
 * failure here is information, not necessarily a bug to paper over.
 */
test.describe("plugin: Subtitles menu injection (#13)", () => {
  test('"Sync subtitles..." appears in the Subtitles menu for an admin', async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    await loginAsAdmin(page);
    await gotoItemDetail(page, itemId);
    await openContextMenu(page);

    const item = page.locator(`.actionSheet button[data-id="${MENU_ITEM_ID}"]`);
    await expect(item).toBeVisible();
    await expect(item).toContainText(MENU_ITEM_TEXT);

    // Directly beneath the client's own Subtitles entry, which is where a user
    // looking for this would look.
    const ids = await page
      .locator(".actionSheet button[data-id]")
      .evaluateAll((buttons) => buttons.map((b) => b.getAttribute("data-id")));
    expect(ids.indexOf(MENU_ITEM_ID)).toBe(ids.indexOf(ANCHOR_MENU_ITEM_ID) + 1);

    // And it goes somewhere: the sync page, for THIS item, with the menu closed
    // behind it.
    await item.click();
    await expect(page).toHaveURL(new RegExp(`configurationpage\\?name=SubtitleSyncPage&itemId=${itemId}`));
    await expect(page.locator(".actionSheet")).toHaveCount(0);
    await expect(page.locator("#ssItemName")).toHaveText(MOVIE_NAME, { timeout: 60_000 });
  });

  /**
   * The permission gate, and the reason it is IsAdministrator rather than
   * EnableSubtitleManagement.
   *
   * The harness viewer has EnableSubtitleManagement on purpose, so the client
   * emits its own "Edit subtitles" entry for them - the exact menu our script
   * attaches to. Without that this test would pass vacuously: a user with no
   * subtitle rights has no anchor to attach to either. The assertion below that
   * the anchor IS present is what keeps the test honest.
   *
   * They are excluded because the 10.11 client puts plugin configuration pages
   * behind an admin route guard, so the page would bounce them to #/home. See
   * issue #12.
   */
  test("the menu item does not appear for a non-admin user", async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    await loginAsViewer(page);
    await gotoItemDetail(page, itemId);
    await openContextMenu(page);

    await expect(
      page.locator(`.actionSheet button[data-id="${ANCHOR_MENU_ITEM_ID}"]`),
      "the viewer must have EnableSubtitleManagement, or this test proves nothing",
    ).toBeVisible();

    await expect(page.locator(`.actionSheet button[data-id="${MENU_ITEM_ID}"]`)).toHaveCount(0);
  });

  /**
   * The SPA requirement from the issue: the injection has to survive
   * client-side navigation between detail pages without a reload.
   *
   * Between two DIFFERENT items, because the failure this guards against is not
   * only "the item stops appearing" but "the item appears and points at the
   * page you came from".
   */
  test("the menu item survives SPA navigation between pages", async ({ page }) => {
    const admin = await adminSession();
    const first = await findFixtureItemId(admin);
    const second = await findSyncableItemId(admin);

    await loginAsAdmin(page);
    await gotoItemDetail(page, first);
    await openContextMenu(page);
    await expect(page.locator(`.actionSheet button[data-id="${MENU_ITEM_ID}"]`)).toBeVisible();
    await closeContextMenu(page);

    let loads = 0;
    page.on("load", () => loads++);

    await spaNavigateToItem(page, second);
    await openContextMenu(page);

    const item = page.locator(`.actionSheet button[data-id="${MENU_ITEM_ID}"]`);
    await expect(item).toBeVisible();

    expect(loads, "the navigation under test has to be client-side").toBe(0);

    // The item id has to have moved with the page, not stuck on the first one.
    await item.click();
    await expect(page).toHaveURL(new RegExp(`itemId=${second}`));
    await expect(page.locator("#ssItemName")).toHaveText(SYNCABLE_NAME, { timeout: 60_000 });
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
