import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import Link from "next/link";
import "./globals.css";

import { isPluginPageEnabled } from "@/lib/flags";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: "Subtitle Sync",
  description:
    "Sync .srt subtitles to your video entirely in the browser - nothing is uploaded.",
};

const NAV_LINK_CLASS =
  "rounded-md px-2.5 py-1.5 text-sm font-medium text-neutral-600 transition hover:bg-neutral-200 hover:text-neutral-900 dark:text-neutral-400 dark:hover:bg-neutral-800 dark:hover:text-neutral-100";

/**
 * Site nav. Server-rendered: the Plugin link is decided at build time by the
 * `NEXT_PUBLIC_FEATURE_PLUGIN_PAGE` flag, so when the flag is off the markup
 * simply does not contain the link.
 */
function SiteNav() {
  return (
    <nav
      aria-label="Site"
      className="border-b border-neutral-200 bg-neutral-100 dark:border-neutral-800 dark:bg-neutral-950"
    >
      <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-x-4 gap-y-1 px-4 py-2 sm:px-5">
        <Link
          href="/"
          className="mr-auto text-sm font-semibold tracking-tight text-neutral-800 transition hover:text-indigo-600 dark:text-neutral-200 dark:hover:text-indigo-400"
        >
          Subtitle Sync
        </Link>
        <div className="-mx-1.5 flex items-center gap-1">
          <Link href="/" className={NAV_LINK_CLASS}>
            Home
          </Link>
          {isPluginPageEnabled() && (
            <Link href="/plugin" className={NAV_LINK_CLASS}>
              Plugin
            </Link>
          )}
        </div>
      </div>
    </nav>
  );
}

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html
      lang="en"
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
    >
      <body className="min-h-full flex flex-col">
        <SiteNav />
        <div className="flex-1">{children}</div>
      </body>
    </html>
  );
}
