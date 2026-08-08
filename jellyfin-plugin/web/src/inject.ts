// The "Sync subtitles..." item in the web client's item context menu (#13).
//
// This file is bundled to dist/subtitleSyncInject.js, embedded in the assembly,
// and inlined into /web/index.html by the File Transformation plugin. See
// Jellyfin.Plugin.SubtitleSync/Injection/IndexHtmlTransformation.cs.
//
// EVERYTHING HERE IS A CONTROLLED HACK. Jellyfin 10.11 has no supported
// extension point for the item detail page (research/jellyfin-10.11-plugin-api.md
// section 12), so this reads and writes the client's own DOM. Two rules follow
// and neither is negotiable:
//
//   1. NEVER THROW. This code runs inside someone else's application. A missing
//      button is a small disappointment; an exception escaping into the client's
//      event loop is a broken Jellyfin. Every entry point is wrapped.
//   2. Recognise, do not assume. If the markup is not the shape we expect - no
//      "Edit subtitles" entry, no resolvable item id, no ApiClient - do nothing
//      at all and leave no trace. The Dashboard route still works, and that is
//      the primary route by design.
//
// Admin-only, deliberately. The 10.11 client puts `configurationpage` behind an
// admin-level route guard, so a non-admin who clicked this would be bounced to
// #/home before the page loaded. See issue #12's finding.

/** `data-id` of the menu item we add. Also how we detect our own handiwork. */
const MENU_ITEM_ID = "subtitlesync-sync";

/** What the added item says. */
const MENU_ITEM_LABEL = "Sync subtitles...";

/**
 * `data-id` of the client's own Subtitles entry, which we sit directly beneath.
 *
 * From `src/components/itemContextMenu.js` at v10.11.11, confirmed against a
 * live 10.11.11 client. Its presence is also our permission and applicability
 * check: the client only emits it for items whose subtitles the signed-in user
 * may edit, so if it is absent there is nothing for us to attach to.
 */
const ANCHOR_MENU_ITEM_ID = "editsubtitles";

/** The client's material-icons glyph on the anchor, swapped for ours. */
const ANCHOR_ICON = "closed_caption";
const MENU_ITEM_ICON = "sync";

/** Where the item lands. `SubtitleSyncPage` is `Plugin.SyncPageName`. */
const SYNC_PAGE_HASH = "#/configurationpage?name=SubtitleSyncPage&itemId=";

/** Jellyfin ids are 32 hex digits, dashless, in both the URL and `data-id`. */
const ITEM_ID = /^[0-9a-f]{32}$/i;

/** `#/details?id=<id>` and its `&`-prefixed variants. */
const DETAILS_HASH = /^#\/details\?(?:[^#]*&)?id=([0-9a-f]{32})/i;

/**
 * How recently a click must have happened for it to count as the thing that
 * opened a menu. Without this, a menu opened some other way would inherit the
 * item id of whatever was last clicked, which is how you get a button that
 * syncs the wrong file.
 */
const TRIGGER_MAX_AGE_MS = 5_000;

/** The one thing on `ApiClient` this script needs, and the client may not have. */
interface CurrentUserApi {
  getCurrentUser(): Promise<{ Id?: string; Policy?: { IsAdministrator?: boolean } }>;
  getCurrentUserId?(): string | null;
}

/** The last click, and when. Used to work out which item a menu belongs to. */
let lastTrigger: { element: Element; at: number } | null = null;

/**
 * Cached answer to "is the signed-in user an administrator", keyed by user id
 * so signing in as someone else re-asks rather than reusing the last verdict.
 * `null` means not asked yet or unanswerable.
 */
let adminFor: { userId: string; isAdministrator: boolean } | null = null;

/** In-flight lookup, so a burst of menu opens makes one request. */
let adminLookup: Promise<boolean> | null = null;

function debug(message: string, detail?: unknown): void {
  try {
    // eslint-disable-next-line no-console
    console.debug(`[SubtitleSync] ${message}`, detail ?? "");
  } catch {
    // A console that throws is not our problem to solve.
  }
}

function apiClient(): CurrentUserApi | null {
  const candidate = (window as { ApiClient?: unknown }).ApiClient as
    | Partial<CurrentUserApi>
    | undefined;
  return typeof candidate?.getCurrentUser === "function"
    ? (candidate as CurrentUserApi)
    : null;
}

/** The signed-in user's id, or "" when the client will not say. */
function currentUserId(api: CurrentUserApi): string {
  try {
    return api.getCurrentUserId?.() ?? "";
  } catch {
    return "";
  }
}

/**
 * Whether the signed-in user is an administrator, as a value that can be read
 * synchronously once known.
 *
 * Returns `null` when the answer is not in hand yet, which the caller turns
 * into an asynchronous retry rather than a guess. Guessing `true` would show a
 * dead button to a non-admin; guessing `false` would hide it from an admin on
 * the first menu open of a session.
 */
function knownAdmin(): boolean | null {
  const api = apiClient();
  if (!api) return null;
  if (adminFor && adminFor.userId === currentUserId(api)) {
    return adminFor.isAdministrator;
  }
  return null;
}

/** Asks the client who is signed in, caches it, and never rejects. */
function resolveAdmin(): Promise<boolean> {
  if (adminLookup) return adminLookup;

  const api = apiClient();
  if (!api) return Promise.resolve(false);

  adminLookup = Promise.resolve()
    .then(() => api.getCurrentUser())
    .then(
      (user) => {
        const isAdministrator = user?.Policy?.IsAdministrator === true;
        adminFor = { userId: user?.Id ?? currentUserId(api), isAdministrator };
        return isAdministrator;
      },
      () => false,
    )
    .finally(() => {
      adminLookup = null;
    });

  return adminLookup;
}

/**
 * Which item a just-opened menu is about.
 *
 * Two sources, in order of specificity: the card or list row that was clicked
 * carries the id it represents, and failing that the detail page's own URL. A
 * card wins because a detail page is full of cards for *other* items, and
 * reading the URL there would sync the wrong one.
 */
function itemIdForMenu(): string | null {
  const trigger =
    lastTrigger && Date.now() - lastTrigger.at <= TRIGGER_MAX_AGE_MS
      ? lastTrigger.element
      : null;

  const card = trigger?.closest?.(".card[data-id], .listItem[data-id]");
  const cardId = card?.getAttribute("data-id");
  if (cardId && ITEM_ID.test(cardId)) return cardId;

  const detail = DETAILS_HASH.exec(window.location.hash || "");
  return detail ? detail[1] : null;
}

/**
 * Adds our item to a menu, if that menu is one we recognise.
 *
 * Built by cloning the client's own "Edit subtitles" button rather than
 * assembling one from a hard-coded class list. The clone inherits whatever
 * markup and classes this client version uses, so a restyle of the menu carries
 * across for free instead of leaving one item looking foreign.
 */
function addMenuItem(sheet: Element, itemId: string): void {
  if (sheet.querySelector(`[data-id="${MENU_ITEM_ID}"]`)) return;

  const anchor = sheet.querySelector(`button[data-id="${ANCHOR_MENU_ITEM_ID}"]`);
  if (!anchor?.parentNode) return;

  const button = anchor.cloneNode(true) as HTMLElement;
  button.setAttribute("data-id", MENU_ITEM_ID);

  const icon = button.querySelector(".actionsheetMenuItemIcon");
  if (icon) {
    icon.className = icon.className.replace(ANCHOR_ICON, MENU_ITEM_ICON);
  }

  const text = button.querySelector(".actionSheetItemText");
  if (text) {
    text.textContent = MENU_ITEM_LABEL;
  } else {
    button.textContent = MENU_ITEM_LABEL;
  }

  // Capture phase, and stopped dead: the client's own delegated handler would
  // otherwise see a menu id it has never heard of.
  //
  // Nothing closes the menu explicitly. The client's own dismissal paths are a
  // real pointer event outside the dialog or a history pop - neither of which
  // can be honestly synthesised from here, and the second races the navigation
  // below. It does not need synthesising: the router tears the dialog down as
  // part of routing to the sync page. Verified against 10.11.11, and asserted
  // by the e2e spec.
  button.addEventListener(
    "click",
    (event) => {
      try {
        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation();
        window.location.hash = SYNC_PAGE_HASH + itemId;
      } catch (error) {
        debug("navigation failed", error);
      }
    },
    true,
  );

  anchor.parentNode.insertBefore(button, anchor.nextSibling);
}

/**
 * Decides whether a newly opened menu gets our item, and adds it.
 *
 * The administrator check is the gate, and it may not have an answer yet on the
 * first menu of a session. In that case the item is added late, once the answer
 * arrives, provided the menu is still open - which is why the DOM insert is
 * written to be idempotent and to re-check that the menu still exists.
 */
function considerMenu(sheet: Element): void {
  const itemId = itemIdForMenu();
  if (!itemId) return;
  if (!sheet.querySelector(`button[data-id="${ANCHOR_MENU_ITEM_ID}"]`)) return;

  const admin = knownAdmin();
  if (admin === true) {
    addMenuItem(sheet, itemId);
    return;
  }
  if (admin === false) return;

  void resolveAdmin().then((isAdministrator) => {
    try {
      if (isAdministrator && sheet.isConnected) addMenuItem(sheet, itemId);
    } catch (error) {
      debug("late insert failed", error);
    }
  });
}

/** Every `.actionSheet` in a batch of added nodes, however deeply nested. */
function menusIn(node: Node): Element[] {
  if (node.nodeType !== Node.ELEMENT_NODE) return [];
  const element = node as Element;
  const found = element.classList?.contains("actionSheet") ? [element] : [];
  return found.concat(Array.from(element.querySelectorAll?.(".actionSheet") ?? []));
}

function install(): void {
  const flag = "__subtitleSyncInjected";
  const global = window as unknown as Record<string, unknown>;
  if (global[flag]) return;
  global[flag] = true;

  // Capture phase so the trigger is recorded before the client acts on it and
  // possibly removes it from the document.
  document.addEventListener(
    "click",
    (event) => {
      const target = event.target;
      if (target instanceof Element) lastTrigger = { element: target, at: Date.now() };
    },
    true,
  );

  // Observing `document` rather than `document.body`: this script is inlined
  // into index.html and may run before the body exists.
  new MutationObserver((records) => {
    try {
      for (const record of records) {
        for (const node of Array.from(record.addedNodes)) {
          for (const sheet of menusIn(node)) considerMenu(sheet);
        }
      }
    } catch (error) {
      debug("observer failed", error);
    }
  }).observe(document, { childList: true, subtree: true });

  // Warm the cache so the first menu of a session gets the item immediately,
  // and re-ask on navigation, which is the cheapest signal that the session may
  // have changed hands.
  void resolveAdmin();
  window.addEventListener("hashchange", () => {
    if (knownAdmin() === null) void resolveAdmin();
  });
}

try {
  install();
} catch (error) {
  debug("install failed", error);
}
