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
 *   #8  save endpoint                 -> the full sync run
 *
 * Selectors below are the current best guess at the 10.11 web client's DOM and
 * are expected to need adjustment when first run for real. That is the point of
 * having the harness: they can be adjusted against a live server in minutes.
 */

import { expect, test, type Page } from "@playwright/test";

import {
  adminSession,
  findFixtureItemId,
  getItem,
  gotoItemDetail,
  listPlugins,
  loginAsAdmin,
  loginAsViewer,
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

test.describe("plugin: end-to-end sync", () => {
  // ENABLE WITH #8 (save endpoint). The fixture clip is short, so a real run is
  // fast enough to be a practical smoke test rather than a nightly job.
  //
  // Note this test mutates the seeded library by writing a new .srt next to the
  // fixture. Run `npm run jf:down -- --purge && npm run jf:up` for a clean slate.
  test.skip("a full sync run produces a sibling .srt that shows up as a new track", async ({
    page,
  }) => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    const before = await getItem(admin, itemId);
    const subtitlesBefore = (before.MediaStreams ?? []).filter(
      (s) => s.Type === "Subtitle",
    ).length;

    await loginAsAdmin(page);
    await page.goto(`/web/#/configurationpage?name=SubtitleSync&itemId=${itemId}`);

    await page.getByRole("button", { name: /sync/i }).first().click();

    // The analysis runs in the browser (VAD + lib/analyze), so this waits on the
    // plugin's own completion signal rather than a fixed sleep.
    await expect(page.getByText(/saved|complete/i).first()).toBeVisible({ timeout: 300_000 });

    const after = await getItem(admin, itemId);
    const subtitlesAfter = (after.MediaStreams ?? []).filter(
      (s) => s.Type === "Subtitle",
    ).length;

    expect(subtitlesAfter).toBeGreaterThan(subtitlesBefore);
    expect(
      (after.MediaStreams ?? []).some((s) => s.Path?.includes(".synced.srt")),
    ).toBe(true);
  });
});
