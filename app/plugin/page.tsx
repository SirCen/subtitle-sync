// Feature-flagged Jellyfin plugin page.
//
// Placeholder only: the install and usage documentation is tracked separately.
// When `NEXT_PUBLIC_FEATURE_PLUGIN_PAGE` is not "1" this route 404s. Because the
// flag is inlined at build time, the `notFound()` branch is all that survives in
// a flag-off build - but the route still exists in the client bundle, so treat
// this as hidden, not secret.
import type { Metadata } from "next";
import { notFound } from "next/navigation";

import { isPluginPageEnabled } from "@/lib/flags";

// Metadata is resolved before the page body runs, so it is gated too - otherwise
// the 404 served when the flag is off would still be titled "Jellyfin plugin".
export function generateMetadata(): Metadata {
  if (!isPluginPageEnabled()) return {};
  return {
    title: "Jellyfin plugin - Subtitle Sync",
    description:
      "Install and use Subtitle Sync as a plugin inside your Jellyfin server.",
  };
}

export default function PluginPage() {
  if (!isPluginPageEnabled()) {
    notFound();
  }

  return (
    <div className="min-h-full bg-neutral-100 text-neutral-800 dark:bg-neutral-950 dark:text-neutral-200">
      <main className="mx-auto max-w-3xl px-5 py-10 sm:py-14">
        <h1 className="text-2xl font-semibold tracking-tight sm:text-3xl">
          Jellyfin plugin
        </h1>
        <p className="mt-3 max-w-prose text-sm leading-relaxed text-neutral-500 dark:text-neutral-400">
          Run Subtitle Sync directly from a film or episode in Jellyfin -
          install and usage instructions are coming soon.
        </p>
      </main>
    </div>
  );
}
