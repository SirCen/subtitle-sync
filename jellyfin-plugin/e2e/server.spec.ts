/**
 * Smoke tests that stand on their own today, with no plugin installed.
 *
 * These are the harness proving itself: if any of these fail, a failure in
 * plugin.spec.ts tells you nothing. Keep them free of plugin assumptions.
 */

import { expect, test } from "@playwright/test";

import {
  JELLYFIN_URL,
  MOVIE_NAME,
  adminSession,
  findFixtureItemId,
  getItem,
  gotoItemDetail,
  loginAsAdmin,
} from "./harness";

test.describe("Jellyfin harness", () => {
  test("server is up and running the pinned 10.11 line", async ({ request }) => {
    const response = await request.get(`${JELLYFIN_URL}/System/Info/Public`);
    expect(response.ok()).toBe(true);

    const info = await response.json();

    // Pinned deliberately. `latest` on Docker Hub currently points at the 12.0
    // pre-release line, and this plugin targets 10.11.
    expect(info.Version).toMatch(/^10\.11\./);

    // The scripted wizard ran, so nobody has to click through setup.
    expect(info.StartupWizardCompleted).toBe(true);
  });

  test("admin can log in through the web client", async ({ page }) => {
    await loginAsAdmin(page);

    // Proves we reached an authenticated view, not just that the form submitted.
    await expect(page.locator(".headerUserButton")).toBeVisible();
    expect(page.url()).toContain("/web/");
  });

  test("fixture item detail page is reachable", async ({ page }) => {
    const admin = await adminSession();
    const itemId = await findFixtureItemId(admin);

    await loginAsAdmin(page);
    await gotoItemDetail(page, itemId);

    await expect(page.locator(".itemName").first()).toContainText(MOVIE_NAME);
  });

  test("fixture item has the external SRT as a subtitle track", async () => {
    const admin = await adminSession();
    const item = await getItem(admin, await findFixtureItemId(admin));

    // The sync feature is meaningless without a subtitle track to sync, so this
    // guards the seed layout: a sibling `.en.srt` must be picked up as external.
    const external = (item.MediaStreams ?? []).filter(
      (s) => s.Type === "Subtitle" && s.IsExternal,
    );
    expect(external.length).toBeGreaterThan(0);
  });

  test("Dashboard > Plugins loads for an admin", async ({ page }) => {
    await loginAsAdmin(page);

    await page.goto("/web/#/dashboard/plugins");

    // The 10.11 dashboard is a React/MUI app with generated class names, so
    // assert on stable user-visible text rather than internal selectors.
    await expect(page.getByText("Manage Repositories").first()).toBeVisible({
      timeout: 45_000,
    });

    // And the installed list actually populated: TMDb ships with the server, so
    // this passes today and keeps passing once our plugin is added alongside it.
    await expect(page.getByText("TMDb", { exact: false }).first()).toBeVisible({
      timeout: 45_000,
    });
  });
});
