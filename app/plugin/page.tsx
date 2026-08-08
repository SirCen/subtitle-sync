// Feature-flagged Jellyfin plugin page.
//
// Install and usage documentation for the Jellyfin 10.11 plugin that lives in
// `jellyfin-plugin/`. When `NEXT_PUBLIC_FEATURE_PLUGIN_PAGE` is not "1" this
// route 404s. Because the flag is inlined at build time, the `notFound()` branch
// is all that survives in a flag-off build - but the route still exists in the
// client bundle, so treat this as hidden, not secret.
//
// Everything asserted here is taken from the shipped plugin rather than from
// intent: `jellyfin-plugin/README.md`, the controllers under
// `jellyfin-plugin/Jellyfin.Plugin.SubtitleSync/Api/`, `public/jellyfin/manifest.json`,
// `.github/workflows/release.yml` and `research/jellyfin-10.11-plugin-api.md`.
// If the plugin's behaviour changes, this page is wrong until it is edited.
import type { Metadata } from "next";
import type { ReactNode } from "react";
import Link from "next/link";
import { notFound } from "next/navigation";

import { isPluginPageEnabled } from "@/lib/flags";

/** Pasted into Dashboard > Plugins > Repositories. */
const MANIFEST_URL = "https://subtitlesync.sircen.dev/jellyfin/manifest.json";

/** The third-party plugin the in-page menu item is built on. */
const FILE_TRANSFORMATION_REPO =
  "https://www.iamparadox.dev/jellyfin/plugins/manifest.json";

const RELEASES_URL = "https://github.com/SirCen/subtitle-sync/releases";

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
      <main className="mx-auto flex max-w-3xl flex-col gap-8 px-5 py-10 sm:py-14">
        <Hero />
        <WhatItDoes />
        <Requirements />
        <Install />
        <Use />
        <WhereTheFileGoes />
        <Limitations />
        <Troubleshooting />
        <Footer />
      </main>
    </div>
  );
}

/* ================================ Sections ================================ */

function Hero() {
  return (
    <header>
      <p className="text-xs font-semibold uppercase tracking-wide text-indigo-600 dark:text-indigo-400">
        Jellyfin 10.11 plugin
      </p>
      <h1 className="mt-2 text-2xl font-semibold tracking-tight sm:text-3xl">
        Fix a drifting subtitle without leaving Jellyfin
      </h1>
      <p className="mt-3 max-w-prose text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        Subtitle Sync installs into your Jellyfin server, listens to where speech
        actually happens in a film or episode, and writes a corrected copy of the
        subtitle track beside the media. No downloading the video, no hunting for
        a better release, no editing timestamps by hand.
      </p>
      <p className="mt-3 max-w-prose text-sm leading-relaxed text-neutral-500 dark:text-neutral-500">
        The same algorithm as the{" "}
        <InlineLink href="/">browser version</InlineLink> of this site, running
        inside your dashboard against the files you already have.
      </p>
    </header>
  );
}

function WhatItDoes() {
  return (
    <Section title="What it does">
      <p className="text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        SRT timestamps are plain wall-clock times and carry no framerate, so a
        subtitle authored against a different release does not just sit at a
        fixed offset: it drifts further out the deeper into the episode you get.
        Subtitle Sync corrects both.
      </p>
      <ol className="mt-4 flex flex-col gap-3">
        <Step n={1} title="The server decodes the audio">
          Jellyfin&apos;s own ffmpeg turns the audio track you pick into 16 kHz
          mono PCM and streams it to your browser. The video is never uploaded
          anywhere.
        </Step>
        <Step n={2} title="Your browser finds the speech">
          A WebAssembly build of WebRTC VAD marks every 30 ms frame as speech or
          silence, producing a picture of when people are talking.
        </Step>
        <Step n={3} title="It matches the subtitles against that">
          Cross-correlation over a set of candidate framerate ratios picks the
          ratio and offset that line the cues up with the speech.
        </Step>
        <Step n={4} title="You save the corrected track">
          The result is written next to the media file and Jellyfin re-scans the
          item, so the new track shows up in the player within seconds.
        </Step>
      </ol>
      <Note>
        The heavy lifting happens in your browser, not on the server. A NAS with
        a modest CPU only has to decode audio.
      </Note>
    </Section>
  );
}

function Requirements() {
  return (
    <Section title="Requirements">
      <Rows>
        <Row label="Jellyfin 10.11 or newer">
          The plugin targets the 10.11 plugin API and runs on <Code>net9.0</Code>
          , which is what 10.11 ships. There is no upper bound: the manifest
          declares 10.11 as a minimum, so newer servers are offered the plugin
          too. It will not load on 10.10 or earlier.
        </Row>
        <Row label="An administrator account">
          In principle the API splits permissions: analysing a track needs the{" "}
          <strong>Subtitle Management</strong> permission and saving into a
          library needs an administrator. In practice the 10.11 web client puts
          every plugin page behind an admin-level route guard, so{" "}
          <strong>only an administrator can reach the page at all</strong>,
          whatever their subtitle permission is set to.
        </Row>
        <Row label="Media that lives on disk, as a local file">
          Saving writes a sibling file next to the video, so the library folder
          has to be a real, writable path on the server. Network streams and disc
          folders have nowhere to put the result.
        </Row>
        <Row label="File Transformation (optional)">
          Only needed for the <strong>Sync subtitles</strong> entry inside the
          Subtitles menu on a detail page. Everything works without it from
          Dashboard &gt; Plugins &gt; Subtitle Sync. Jellyfin 10.11 has no plugin
          dependency mechanism, so we cannot install it for you - see below.
        </Row>
      </Rows>
    </Section>
  );
}

function Install() {
  return (
    <Section title="Install">
      <SubHeading>1. Add the repository (recommended)</SubHeading>
      <p className="mt-2 text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        In your Jellyfin dashboard, go to <strong>Plugins</strong> &gt;{" "}
        <strong>Repositories</strong>, add a repository with any name you like,
        and paste this URL:
      </p>
      <CodeBlock>{MANIFEST_URL}</CodeBlock>
      <p className="mt-3 text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        Then open <strong>Plugins</strong> &gt; <strong>Catalogue</strong>, find{" "}
        <strong>Subtitle Sync</strong> under Subtitles, and install it. Restart
        Jellyfin: it only scans for plugins at startup, so the restart is not
        optional. The dashboard should then list Subtitle Sync as{" "}
        <strong>Active</strong>. Installing from the repository is also what
        makes future versions show up as updates.
      </p>

      <SubHeading className="mt-6">2. Or install the zip by hand</SubHeading>
      <p className="mt-2 text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        If your server cannot reach this site, download{" "}
        <Code>subtitle-sync_&lt;version&gt;.zip</Code> from the{" "}
        <InlineLink href={RELEASES_URL}>GitHub releases page</InlineLink>, create
        a folder for it inside your Jellyfin data directory under{" "}
        <Code>plugins/</Code>, and extract the zip contents straight into that
        folder - the assemblies and <Code>meta.json</Code> sit at the zip root
        with no wrapping directory. Restart Jellyfin afterwards. A hand-installed
        copy will not receive updates automatically.
      </p>

      <SubHeading className="mt-6">
        3. Optional: File Transformation, for the menu item
      </SubHeading>
      <p className="mt-2 text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        The <strong>Sync subtitles</strong> entry in a film or episode&apos;s
        Subtitles menu is added by injecting a small script into the web client,
        which needs the third-party{" "}
        <InlineLink href="https://github.com/IAmParadox27/jellyfin-plugin-file-transformation">
          File Transformation
        </InlineLink>{" "}
        plugin. Jellyfin 10.11 has no way for one plugin to require another, so
        you have to add its repository yourself:
      </p>
      <CodeBlock>{FILE_TRANSFORMATION_REPO}</CodeBlock>
      <p className="mt-3 text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        Its plugin ID is <Code>5e87cc92-571a-4d8d-8d98-d2d4147f9f90</Code>, which
        is worth checking against if you are not sure whether you already have it
        installed. This step is genuinely optional, and skipping it costs you one
        shortcut and nothing else.
      </p>
    </Section>
  );
}

function Use() {
  return (
    <Section title="Using it">
      <p className="text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        There are two ways in.
      </p>
      <div className="mt-4 grid grid-cols-1 gap-4 sm:grid-cols-2">
        <Card>
          <p className="text-[10px] font-medium uppercase tracking-wide text-neutral-400">
            The reliable path
          </p>
          <h4 className="mt-1 text-sm font-semibold">
            Dashboard &gt; Plugins &gt; Subtitle Sync
          </h4>
          <p className="mt-2 text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
            Opens the plugin&apos;s settings page, which has a button through to
            the sync page. The sync page starts on a picker listing your most
            recently added items, with a search box. Nothing else is required for
            this to work.
          </p>
        </Card>
        <Card>
          <p className="text-[10px] font-medium uppercase tracking-wide text-neutral-400">
            The shortcut
          </p>
          <h4 className="mt-1 text-sm font-semibold">
            A film or episode &gt; Subtitles &gt; Sync subtitles
          </h4>
          <p className="mt-2 text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
            Jumps straight into the sync page with that item already loaded.
            Needs File Transformation, and is the part most likely to stop
            working after a Jellyfin upgrade.
          </p>
        </Card>
      </div>

      <SubHeading className="mt-6">Then</SubHeading>
      <ol className="mt-3 flex flex-col gap-3">
        <Step n={1} title="Pick the version, subtitle track and audio track">
          Any track Jellyfin lists works, external file or embedded. Tracks that
          can never be synced are shown disabled with the reason.
        </Step>
        <Step n={2} title="Press Sync subtitles and wait">
          The page reports what it is doing: reading the track, checking the
          signal cache, streaming audio, detecting speech, correlating. Cancel
          stops the server decoding immediately.
        </Step>
        <Step n={3} title="Read the result">
          You get the winning framerate ratio and offset, a score table for every
          candidate ratio, the first corrected cue as a preview, and any warnings
          verbatim. Warnings are the honest signal - read them before saving.
        </Step>
        <Step n={4} title="Nudge if it is close but not right">
          Adjust the offset or pick a different ratio by hand. The correction is
          re-applied instantly with no re-analysis and no further audio transfer.
        </Step>
        <Step n={5} title="Save to the library, or download the .srt">
          Save writes the file beside the media and queues a re-scan. Download
          gives you exactly the same file to place yourself.
        </Step>
      </ol>
    </Section>
  );
}

function WhereTheFileGoes() {
  return (
    <Section title="Where the file goes, and how to undo it">
      <p className="text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        Saving writes a new file next to the video, named after it:
      </p>
      <CodeBlock>{"<video file name>.<language>.synced.srt"}</CodeBlock>
      <p className="mt-3 text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        So <Code>Movie (2019).mkv</Code> gains{" "}
        <Code>Movie (2019).en.synced.srt</Code>, and it appears in the
        player&apos;s track picker as{" "}
        <Code>synced - English - SRT - External</Code>. If a file of that name
        already exists, a numbered suffix is added rather than replacing it.
      </p>
      <Note tone="positive">
        <strong>The original is never touched.</strong> The subtitle you synced
        from is left exactly as it was, so undoing a sync is a matter of deleting
        the <Code>.synced.srt</Code> file. The one exception is the{" "}
        <strong>Overwrite the original subtitle file</strong> setting on the
        plugin&apos;s configuration page, which is off by default. Turn it on
        only if you are comfortable with a destructive edit that has no undo.
      </Note>
    </Section>
  );
}

function Limitations() {
  return (
    <Section title="What it will not do">
      <p className="text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        None of these are bugs waiting to be fixed. Knowing them up front is
        cheaper than discovering them halfway through a season.
      </p>
      <Rows className="mt-4">
        <Row label="The browser tab has to stay open">
          The analysis runs in your browser, not on the server. Navigating away
          or closing the tab cancels the run, and the server stops decoding as
          soon as the connection closes.
        </Row>
        <Row label="A first run transfers real bandwidth">
          Decoded audio is roughly <strong>115 MB per hour</strong> of runtime.
          Once the server has cached the speech signal for that file, later runs
          fetch about <strong>45 KB per hour</strong> instead, so re-running with
          different settings or on a second track is nearly instant. Fine over a
          LAN, painful over a slow remote link.
        </Row>
        <Row label="Image-based subtitles can never work">
          PGS, VOBSUB and DVB tracks are sequences of pictures. There is no text
          to correlate against speech, and no conversion produces any. Those
          tracks are disabled in the picker rather than left to fail.
        </Row>
        <Row label="One track at a time">
          There is no batch mode and no season-wide sync. Each episode is its own
          run, deliberately: the correct offset differs per file, and a wrong
          answer applied silently across a season is worse than no answer.
        </Row>
        <Row label="Styling is lost on non-SRT tracks">
          An ASS or SSA track is converted to SRT, so positioning and styling do
          not survive. The timings are what get fixed. The page warns you before
          you start.
        </Row>
        <Row label="The menu item is the fragile part">
          File Transformation works by patching the server&apos;s startup path at
          runtime, so a Jellyfin server or web client update can stop the{" "}
          <strong>Sync subtitles</strong> entry appearing. The plugin itself is
          unaffected: the Dashboard route keeps working, which is why it is the
          one we call primary.
        </Row>
      </Rows>
    </Section>
  );
}

function Troubleshooting() {
  return (
    <Section title="Troubleshooting">
      <Rows>
        <Row label="There is no Sync subtitles entry in the Subtitles menu">
          Check that File Transformation is installed and Active in Dashboard
          &gt; Plugins, and that you restarted Jellyfin after installing either
          plugin. If it was working and stopped after an upgrade, that is the
          known fragility above rather than a broken install: go to Dashboard
          &gt; Plugins &gt; Subtitle Sync instead, which does the same job from a
          picker.
        </Row>
        <Row label="Saving fails and says the folder is not writable">
          The plugin writes into the same folder as the video, so the account
          Jellyfin runs as needs write permission there. In Docker this is
          usually a library mounted read-only: change the volume to read-write
          and restart the container. Otherwise check the folder&apos;s ownership
          and permissions. Until then, use <strong>Download the .srt</strong> and
          copy the file into place yourself - it is byte for byte what the save
          would have written.
        </Row>
        <Row label="Saving says it needs an administrator">
          Analysing and saving are separate permissions, and only an
          administrator can write into a library. Your result is not lost:
          download it, or ask an administrator to run the save.
        </Row>
        <Row label="The result looks wrong, or a warning says confidence is low">
          Check the preview of the first corrected cue before saving, and compare
          the top two scores in the candidate table. If two ratios sit very close
          together the correct one may still have won, which is common on short
          items. If the answer is clearly off, try a different audio track (a
          commentary track will not match the dialogue), raise the maximum search
          offset if the drift is large, or nudge the offset by hand and check the
          preview again. A track that is a translation of a different cut will
          not correlate no matter what you set.
        </Row>
        <Row label="The saved file does not appear in the player">
          A re-scan is queued automatically after a save, but if it could not be
          queued the track appears at the next library scan instead. Refresh the
          item&apos;s metadata from its detail page to force it.
        </Row>
      </Rows>
    </Section>
  );
}

function Footer() {
  return (
    <footer className="border-t border-neutral-200 pt-6 text-sm leading-relaxed text-neutral-500 dark:border-neutral-800 dark:text-neutral-500">
      <p>
        The plugin source lives in <Code>jellyfin-plugin/</Code> in the{" "}
        <InlineLink href="https://github.com/SirCen/subtitle-sync">
          repository
        </InlineLink>
        . Bugs and questions go to the{" "}
        <InlineLink href="https://github.com/SirCen/subtitle-sync/issues">
          issue tracker
        </InlineLink>
        .
      </p>
    </footer>
  );
}

/* ============================= Small shared UI ============================= */

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section>
      <h2 className="text-lg font-semibold tracking-tight">{title}</h2>
      <div className="mt-3">{children}</div>
    </section>
  );
}

function SubHeading({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) {
  return (
    <h3
      className={
        "text-xs font-semibold uppercase tracking-wide text-neutral-500 dark:text-neutral-400 " +
        className
      }
    >
      {children}
    </h3>
  );
}

function Card({ children }: { children: ReactNode }) {
  return (
    <div className="rounded-2xl border border-neutral-200 bg-white p-5 shadow-sm dark:border-neutral-800 dark:bg-neutral-900">
      {children}
    </div>
  );
}

function Rows({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) {
  return (
    <dl
      className={
        "divide-y divide-neutral-200 overflow-hidden rounded-2xl border border-neutral-200 bg-white shadow-sm dark:divide-neutral-800 dark:border-neutral-800 dark:bg-neutral-900 " +
        className
      }
    >
      {children}
    </dl>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="px-5 py-4">
      <dt className="text-sm font-semibold">{label}</dt>
      <dd className="mt-1 text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
        {children}
      </dd>
    </div>
  );
}

function Step({
  n,
  title,
  children,
}: {
  n: number;
  title: string;
  children: ReactNode;
}) {
  return (
    <li className="flex gap-3">
      <span className="mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-indigo-600 text-[11px] font-semibold text-white">
        {n}
      </span>
      <div className="min-w-0">
        <p className="text-sm font-medium">{title}</p>
        <p className="mt-0.5 text-sm leading-relaxed text-neutral-600 dark:text-neutral-400">
          {children}
        </p>
      </div>
    </li>
  );
}

function Note({
  children,
  tone = "neutral",
}: {
  children: ReactNode;
  tone?: "neutral" | "positive";
}) {
  const toneClass =
    tone === "positive"
      ? "bg-emerald-50 text-emerald-900 dark:bg-emerald-500/10 dark:text-emerald-200"
      : "bg-neutral-200/60 text-neutral-700 dark:bg-neutral-800/60 dark:text-neutral-300";
  return (
    <p className={`mt-4 rounded-xl p-4 text-sm leading-relaxed ${toneClass}`}>
      {children}
    </p>
  );
}

function Code({ children }: { children: ReactNode }) {
  return (
    <code className="rounded bg-neutral-200 px-1 py-0.5 font-mono text-[12px] text-neutral-700 dark:bg-neutral-800 dark:text-neutral-300">
      {children}
    </code>
  );
}

function CodeBlock({ children }: { children: string }) {
  return (
    <div className="mt-3 overflow-x-auto rounded-lg border border-neutral-200 bg-white px-3 py-2 dark:border-neutral-800 dark:bg-neutral-900">
      <pre className="m-0 font-mono text-[12px] leading-relaxed text-neutral-700 dark:text-neutral-300">
        {children}
      </pre>
    </div>
  );
}

const LINK_CLASS =
  "font-medium text-indigo-600 underline underline-offset-2 transition hover:text-indigo-700 dark:text-indigo-400 dark:hover:text-indigo-300";

/**
 * Internal routes go through `next/link` so they navigate on the client; only
 * off-site URLs get a plain anchor.
 */
function InlineLink({ href, children }: { href: string; children: ReactNode }) {
  if (href.startsWith("/")) {
    return (
      <Link href={href} className={LINK_CLASS}>
        {children}
      </Link>
    );
  }
  return (
    <a
      href={href}
      className={LINK_CLASS}
      target="_blank"
      rel="noreferrer noopener"
    >
      {children}
    </a>
  );
}
