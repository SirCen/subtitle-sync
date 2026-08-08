// Build-time feature flags.
//
// These are read from `NEXT_PUBLIC_*` environment variables, which Next.js
// INLINES at build time: every literal `process.env.NEXT_PUBLIC_FOO` reference
// is textually replaced with the value present when `next build` ran. Two
// consequences worth knowing:
//
//   1. The references below must stay as literal member expressions. Indirection
//      (`const env = process.env; env.NEXT_PUBLIC_FOO`, or `process.env[name]`)
//      is NOT inlined and would silently read `undefined` in the browser.
//   2. The value is frozen at build time, so flipping a flag requires a rebuild,
//      and the flag ships in the client bundle. A flag can hide a page; it
//      cannot keep one secret.

/** Any value other than the string "1" - including unset - means OFF. */
function isOn(value: string | undefined): boolean {
  return value === "1";
}

/**
 * Is the feature-flagged `/plugin` page (Jellyfin plugin docs) enabled?
 *
 * Controlled by `NEXT_PUBLIC_FEATURE_PLUGIN_PAGE`. Defaults to OFF.
 */
export function isPluginPageEnabled(): boolean {
  return isOn(process.env.NEXT_PUBLIC_FEATURE_PLUGIN_PAGE);
}
