/**
 * Captures the screenshots used by the `/plugin` documentation page (#17).
 *
 * These are not tests. They are a repeatable way to regenerate real images of a
 * real Jellyfin 10.11 client with the plugin installed, so the page can be
 * re-shot when the UI changes instead of carrying hand-taken artefacts nobody
 * else can reproduce.
 *
 *     npm run jf:screenshots
 *
 * Deliberately excluded from `npx playwright test` - see
 * `playwright.screenshots.config.ts` for how the filename does that, and why.
 *
 * WHAT IT NEEDS. `npm run jf:up` provides all of it:
 *   - the built plugin DLL staged and the container restarted
 *   - the File Transformation plugin installed (the menu shot is its whole point)
 *   - both fixture movies seeded
 *
 * WHAT IT MUST NOT DO: leave anything behind. Nothing here clicks Save, so no
 * file is written into the seeded library and the harness is left as found.
 *
 * ON WHAT ENDS UP IN THE PIXELS. Playwright screenshots the page viewport, not
 * the browser chrome, so no address bar and no URL is captured. The harness
 * credentials are throwaway and documented publicly in
 * `jellyfin-plugin/docker/README.md`. Every crop is still looked at before the
 * PNG is committed: Jellyfin puts `api_key` in image URLs, and while those live
 * in the DOM rather than on screen, "probably not visible" is not a check.
 */

import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";

import { expect, test, type Locator, type Page } from "@playwright/test";
import sharp from "sharp";

import {
  adminSession,
  findFixtureItemId,
  findSyncableItemId,
  gotoItemDetail,
  loginAsAdmin,
  MOVIE_NAME,
  SYNCABLE_KNOWN_OFFSET,
  SYNCABLE_NAME,
} from "./harness";

/** Where the page reads them from. NOT public/jellyfin, which is the manifest. */
const OUTPUT_DIR = path.join(__dirname, "..", "..", "public", "plugin");

/**
 * Default breathing room around a crop, in CSS pixels.
 *
 * Per-shot, because it is not free: a few pixels too many and the crop catches
 * the top of whatever the page renders next, which reads as a broken image
 * rather than as generosity.
 */
const DEFAULT_PADDING = 14;

interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * A bounding box measured only once it has stopped moving.
 *
 * Not defensive padding. Jellyfin's action sheet opens with a scale transform,
 * and a `boundingBox()` taken the moment it becomes visible reports the box
 * mid-animation - about a third of its final width. The first version of this
 * script cropped to that and produced an image with every menu label sliced in
 * half, which is exactly the kind of thing a capture script is supposed to stop
 * happening twice.
 */
async function settledBox(locator: Locator): Promise<Box> {
  let previous: Box | null = null;

  for (let attempt = 0; attempt < 40; attempt++) {
    const box = await locator.boundingBox();
    if (
      box &&
      previous &&
      box.x === previous.x &&
      box.y === previous.y &&
      box.width === previous.width &&
      box.height === previous.height
    ) {
      return box;
    }
    previous = box;
    await locator.page().waitForTimeout(100);
  }

  throw new Error("Element never stopped moving, so its box cannot be trusted");
}

/**
 * Writes a PNG cropped to the union of some elements' settled boxes.
 *
 * `page.screenshot({ clip })` rather than `locator.screenshot()` because both
 * shots want a little of the surrounding page: one spans two elements, the
 * other wants padding a locator screenshot cannot give it. The clip is in CSS
 * pixels and the config's deviceScaleFactor supplies the resolution, so the
 * numbers here stay readable.
 */
async function captureRegion(
  page: Page,
  name: string,
  locators: Locator[],
  options: { padding?: number; paddingBottom?: number } = {},
): Promise<void> {
  const padding = options.padding ?? DEFAULT_PADDING;
  const paddingBottom = options.paddingBottom ?? padding;

  const boxes = await Promise.all(locators.map((l) => settledBox(l)));

  const left = Math.min(...boxes.map((b) => b.x));
  const top = Math.min(...boxes.map((b) => b.y));
  const right = Math.max(...boxes.map((b) => b.x + b.width));
  const bottom = Math.max(...boxes.map((b) => b.y + b.height));

  const viewport = page.viewportSize()!;

  // A crop the viewport had to clamp is a crop with a sentence sliced through
  // it, which is the failure this script exists to stop. The check is against
  // the elements themselves, not the padded box: PADDING is a nicety, and an
  // element that already spans the full width simply does not get any.
  if (right > viewport.width || bottom > viewport.height) {
    throw new Error(
      `"${name}" does not fit in the ${viewport.width}x${viewport.height} viewport ` +
        `(needs ${Math.ceil(right)}x${Math.ceil(bottom)}). Enlarge the viewport for ` +
        "this shot rather than shipping a clipped image.",
    );
  }

  const x = Math.max(0, left - padding);
  const y = Math.max(0, top - padding);
  const width = Math.min(right + padding, viewport.width) - x;
  const height = Math.min(bottom + paddingBottom, viewport.height) - y;

  const raw = await page.screenshot({
    clip: { x, y, width, height },
    animations: "disabled",
    scale: "device",
  });

  // These ship in the site bundle, so the 24-bit PNG Chromium hands back is not
  // what gets committed. A palette PNG is the right trade for this material:
  // Jellyfin's UI is near-greyscale with one accent blue, so ~250 colours cover
  // it and the text stays crisp - roughly a third of the bytes, no visible
  // difference. Checked by eye against the 24-bit original, not assumed.
  const buffer = await sharp(raw)
    .png({ palette: true, quality: 90, effort: 10, compressionLevel: 9 })
    .toBuffer();

  await mkdir(OUTPUT_DIR, { recursive: true });
  const file = path.join(OUTPUT_DIR, `${name}.png`);
  await writeFile(file, buffer);

  console.log(
    `  ${name}.png  ${Math.round(width)}x${Math.round(height)} css  ` +
      `${(buffer.byteLength / 1024).toFixed(0)} KB ` +
      `(from ${(raw.byteLength / 1024).toFixed(0)} KB)`,
  );
}

/**
 * Shot 1: the Subtitles menu, which is what issue #17 section 1 asks for and
 * the only image that answers "does this show up where I would go looking".
 *
 * The 10.11 client has no "Subtitles" button on a detail page - the entry lives
 * in the `...` overflow menu, `.btnMoreCommands`. That was established against a
 * live client by `plugin.spec.ts`, whose selectors this follows rather than
 * re-deriving.
 *
 * Cropped to the menu AND the item heading together: the menu alone is a
 * floating list that could be from anywhere, and the point of the image is that
 * it is attached to a film.
 */
test("capture: the Sync subtitles entry in the item menu", async ({ page }) => {
  // Wide enough that the menu, which the client anchors to the right edge, is
  // not against the viewport wall.
  await page.setViewportSize({ width: 1500, height: 900 });

  const admin = await adminSession();
  const itemId = await findFixtureItemId(admin);

  await loginAsAdmin(page);
  await gotoItemDetail(page, itemId);

  await page.locator(".btnMoreCommands").filter({ visible: true }).first().click();

  const sheet = page.locator(".actionSheetContent");
  await expect(sheet).toBeVisible();

  // The shot is worthless if our entry is missing, so this is an assertion and
  // not a wait: File Transformation not being installed must fail the capture.
  const ours = page.locator('.actionSheet button[data-id="subtitlesync-sync"]');
  await expect(ours).toBeVisible();
  await expect(ours).toContainText("Sync subtitles");

  const heading = page.locator(".itemName").filter({ visible: true }).first();
  await expect(heading).toHaveText(new RegExp(MOVIE_NAME));

  // No bottom padding: the client renders a "More Like This" row immediately
  // under the sheet, and 14 px of generosity slices its heading in half.
  await captureRegion(page, "jellyfin-subtitles-menu", [sheet, heading], {
    paddingBottom: 0,
  });
});

/**
 * Shot 2: a finished run, on Structured Clip.
 *
 * Structured Clip and not Sample Clip, for the reason the harness README
 * labours: Sample Clip reads as ~92% speech to the VAD, so a sync of it has no
 * right answer. Structured Clip's seeded track is displaced by a known
 * SYNCABLE_KNOWN_OFFSET, so the offset in this image is genuinely correct
 * rather than a staged number. The assertion below is what keeps that true - if
 * the plugin regresses, the capture fails instead of quietly shipping a
 * screenshot of a wrong answer.
 */
test("capture: a completed sync result", async ({ page }) => {
  // Tall, because the whole result block has to be on screen at once for a
  // clipped screenshot; narrow, because the candidate table stretches to its
  // container and a wider one is mostly empty column.
  await page.setViewportSize({ width: 1000, height: 1400 });

  const admin = await adminSession();
  const itemId = await findSyncableItemId(admin);

  await loginAsAdmin(page);
  await page.goto(`/web/#/configurationpage?name=SubtitleSyncPage&itemId=${itemId}`);

  await expect(page.locator("#ssItemName")).toHaveText(SYNCABLE_NAME, { timeout: 60_000 });
  await expect(page.locator("#ssRun")).toBeEnabled({ timeout: 60_000 });

  await page.locator("#ssRun").click();

  const result = page.locator("#ssResult");
  await expect(result).toBeVisible({ timeout: 300_000 });

  // THE NUMBER IN THE IMAGE HAS TO BE THE RIGHT ONE.
  const recovered = Number(await page.locator("#ssNudgeOffset").inputValue());
  expect(recovered).toBeCloseTo(SYNCABLE_KNOWN_OFFSET, 2);

  await result.scrollIntoViewIfNeeded();
  // Likewise: the bundle build stamp sits just below the result block.
  await captureRegion(page, "jellyfin-sync-result", [result], { paddingBottom: 4 });
});
