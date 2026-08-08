// The bits of the Jellyfin web client this page leans on.
//
// None of it is a package we can depend on: `ApiClient` and `Dashboard` are
// globals the client installs, and a plugin page is loaded into an already
// running client. So they are declared, narrowly, rather than imported - and
// narrowly is the point, because everything declared here is a promise the
// client has not made to us and could break on any release.

import type * as Bundle from "../index";

/** The subset of jellyfin-apiclient's ApiClient the page uses. */
export interface ApiClientLike {
  /** Absolute URL for a server path, with the query string applied. */
  getUrl(path: string, params?: Record<string, string>): string;
  /** The signed-in session's access token. */
  accessToken(): string;
  /** The signed-in user's id, or null before login. */
  getCurrentUserId(): string;
}

/** The subset of the client's Dashboard helper the page uses. */
export interface DashboardLike {
  alert(options: string | { title?: string; message?: string }): void;
  navigate?(url: string): void;
}

declare global {
  interface Window {
    /**
     * The shared algorithm from `lib/`, bundled by
     * jellyfin-plugin/web/build.mjs and served as an embedded resource.
     * Typed from the bundle's own entry point, so anything the page reaches for
     * that is not exported there is a compile error rather than a blank screen.
     */
    SubtitleSync?: typeof Bundle;
    /** This page's own entry point, called by the bootstrap in syncPage.html. */
    SubtitleSyncPage?: {
      init(page: HTMLElement): void;
      destroy(page: HTMLElement): void;
    };
    ApiClient?: ApiClientLike;
    Dashboard?: DashboardLike;
  }
}

/**
 * The query string of the page URL.
 *
 * The client routes plugin pages as `/web/#/configurationpage?name=...`, so the
 * parameters live in the hash, not in `location.search`. It also passes the same
 * query on to the server when it fetches the fragment, so both spellings can
 * occur; the hash is authoritative because that is what the user's link carries.
 */
export function pageQuery(): URLSearchParams {
  const hash = window.location.hash;
  const start = hash.indexOf("?");
  if (start >= 0) {
    return new URLSearchParams(hash.slice(start + 1));
  }
  return new URLSearchParams(window.location.search);
}

/** Rewrites the current URL's query without adding a history entry. */
export function replacePageQuery(params: URLSearchParams): void {
  const hash = window.location.hash;
  const base = hash.indexOf("?") >= 0 ? hash.slice(0, hash.indexOf("?")) : hash;
  const query = params.toString();
  const next = query ? `${base}?${query}` : base;
  window.history.replaceState(window.history.state, "", next);
}
