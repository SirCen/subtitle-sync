// The Subtitle Sync plugin page (#12).
//
// Vanilla TypeScript against the markup in Configuration/syncPage.html. It is
// deliberately not a framework app: this runs inside Jellyfin's own client,
// borrows its stylesheet, and has to look like a part of it rather than a panel
// bolted on. Every class name used here is the client's.
//
// The algorithm is NOT duplicated. `window.SubtitleSync` is the esbuild bundle
// of lib/ - the same code the golden parity test covers - and this file only
// sequences it:
//
//   subtitle track -> parseSrt          -> cue blocks
//   audio          -> cached signal, or PCM stream -> VAD -> speech signal
//   analyze(speechSignal, blocks, opts) -> best ratio + offset
//   applyCorrection(blocks, ratio, offset) -> writeSrt -> save or download
//
// Note the argument orders. `analyze` takes cue BLOCKS, not a subtitle signal,
// and `applyCorrection` takes blocks first. Getting either backwards produces a
// plausible-looking wrong answer rather than an error.

import {
  decodeSpeechSignal,
  encodeSpeechSignal,
  SignalPayloadRejected,
} from "../signalCodec";
import { ApiError, SubtitleSyncApi } from "./api";
import type { ItemDescription, LibraryItem, MediaSource, SubtitleTrack } from "./api";
import type { ApiClientLike } from "./jellyfin";
import { pageQuery, replacePageQuery } from "./jellyfin";

import type { RatioCandidate, SpeechSignal, SrtBlock, SyncResult } from "../../../../lib/types";

// ---------------------------------------------------------------------------
// Defaults, each with a reset in the Advanced panel
// ---------------------------------------------------------------------------

const DEFAULT_MAX_OFFSET = 120;
const DEFAULT_VAD = 2;

/**
 * The default ratio list as text.
 *
 * Built from the bundle's own DEFAULT_RATIOS so it cannot drift from lib/. When
 * the field still holds exactly this, `ratios` is omitted from the options
 * entirely, which keeps DEFAULT_RATIOS' friendlier labels ("1.0 (offset only)")
 * instead of echoing the raw expressions back.
 */
function defaultRatioText(): string {
  const bundle = window.SubtitleSync;
  if (!bundle) return "1.0";
  return bundle.DEFAULT_RATIOS.map((r) => r.label).join("\n");
}

// ---------------------------------------------------------------------------
// Small DOM helpers
// ---------------------------------------------------------------------------

function el<T extends HTMLElement>(page: HTMLElement, id: string): T {
  const found = page.querySelector<T>(`#${id}`);
  if (!found) throw new Error(`Subtitle Sync page is missing #${id}`);
  return found;
}

function show(node: HTMLElement, visible: boolean): void {
  node.style.display = visible ? "" : "none";
}

function formatSeconds(seconds: number): string {
  const sign = seconds < 0 ? "-" : "+";
  return `${sign}${Math.abs(seconds).toFixed(3)} s`;
}

function formatDuration(seconds: number | null | undefined): string {
  if (!seconds || !Number.isFinite(seconds)) return "unknown length";
  const total = Math.round(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const mm = String(m).padStart(2, "0");
  const ss = String(s).padStart(2, "0");
  return h > 0 ? `${h}:${mm}:${ss}` : `${m}:${ss}`;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(1)} ${units[unit]}`;
}

/** Lets a just-set label paint before a synchronous block of work. */
function nextFrame(): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, 0));
}

function describeItemLine(item: LibraryItem): string {
  if (item.Type === "Episode") {
    const season = item.ParentIndexNumber ?? 0;
    const episode = item.IndexNumber ?? 0;
    const code = `S${String(season).padStart(2, "0")}E${String(episode).padStart(2, "0")}`;
    return `${item.SeriesName ?? "Unknown series"} - ${code} - ${item.Name}`;
  }
  return item.ProductionYear ? `${item.Name} (${item.ProductionYear})` : item.Name;
}

function trackLabel(track: SubtitleTrack): string {
  const base = track.DisplayTitle?.trim() || track.Title?.trim() || track.Language || "Untitled";
  const where = track.IsExternal ? "external file" : "embedded";
  return `${base} - ${track.Codec ?? "unknown codec"}, ${where}`;
}

// ---------------------------------------------------------------------------
// Page controller
// ---------------------------------------------------------------------------

type Phase = "idle" | "running";

class SyncPage {
  private readonly api: SubtitleSyncApi;

  private item: ItemDescription | null = null;
  private blocks: SrtBlock[] | null = null;
  private analysis: SyncResult | null = null;
  private correctedSrt = "";
  private ratioByLabel = new Map<string, number>();

  /** In-flight run. Aborting it is what kills the server's ffmpeg process. */
  private controller: AbortController | null = null;
  private phase: Phase = "idle";

  private searchTimer = 0;
  private searchController: AbortController | null = null;

  constructor(
    private readonly page: HTMLElement,
    client: ApiClientLike,
  ) {
    this.api = new SubtitleSyncApi(client);
  }

  // ---------------------------------------------------------------- lifecycle

  start(): void {
    this.wire();
    el(this.page, "ssBuild").textContent = `Bundle build ${window.SubtitleSync?.BUILD ?? "unknown"}`;

    const itemId = pageQuery().get("itemId");
    if (itemId) {
      void this.loadItem(itemId);
    } else {
      // The picker is the primary route in, so it opens with something to click
      // rather than an empty box: without a search term the search returns the
      // most recently added items, which is what a drifting subtitle usually
      // belongs to.
      this.showPicker();
      void this.runSearch();
    }
  }

  /** Called on viewdestroy, so a cancelled navigation still frees the server. */
  stop(): void {
    this.controller?.abort();
    this.controller = null;
    this.searchController?.abort();
    window.clearTimeout(this.searchTimer);
  }

  // -------------------------------------------------------------------- wiring

  private wire(): void {
    const page = this.page;

    el<HTMLInputElement>(page, "ssSearch").addEventListener("input", () => {
      window.clearTimeout(this.searchTimer);
      this.searchTimer = window.setTimeout(() => void this.runSearch(), 300);
    });

    el(page, "ssChangeItem").addEventListener("click", () => {
      this.item = null;
      const query = pageQuery();
      query.delete("itemId");
      try {
        replacePageQuery(query);
      } catch {
        // A client that will not let us rewrite the URL is not a reason to
        // refuse to change item.
      }
      this.showPicker();
      void this.runSearch();
    });

    el<HTMLSelectElement>(page, "ssSource").addEventListener("change", () => {
      this.renderTracks();
      this.clearResult();
    });
    el<HTMLSelectElement>(page, "ssSubtitle").addEventListener("change", () => {
      this.renderSubtitleNote();
      this.clearResult();
    });
    el<HTMLSelectElement>(page, "ssAudio").addEventListener("change", () => this.clearResult());

    el(page, "ssResetMaxOffset").addEventListener("click", () => {
      el<HTMLInputElement>(page, "ssMaxOffset").value = String(DEFAULT_MAX_OFFSET);
    });
    el(page, "ssResetVad").addEventListener("click", () => {
      el<HTMLSelectElement>(page, "ssVad").value = String(DEFAULT_VAD);
    });
    el(page, "ssResetRatios").addEventListener("click", () => {
      el<HTMLTextAreaElement>(page, "ssRatios").value = defaultRatioText();
    });

    el(page, "ssRun").addEventListener("click", () => void this.run());
    el(page, "ssCancel").addEventListener("click", () => this.cancel());

    el<HTMLInputElement>(page, "ssNudgeOffset").addEventListener("input", () =>
      this.applyNudge(),
    );
    el<HTMLSelectElement>(page, "ssNudgeRatio").addEventListener("change", () =>
      this.applyNudge(),
    );
    el(page, "ssResetNudge").addEventListener("click", () => {
      const best = this.analysis?.best;
      if (!best) return;
      el<HTMLInputElement>(page, "ssNudgeOffset").value = best.offset.toFixed(3);
      el<HTMLSelectElement>(page, "ssNudgeRatio").value = best.label;
      this.applyNudge();
    });

    el(page, "ssSave").addEventListener("click", () => void this.save());
    el(page, "ssDownload").addEventListener("click", () => this.download());

    // Defaults, before anything is loaded, so the panel is never blank.
    el<HTMLInputElement>(page, "ssMaxOffset").value = String(DEFAULT_MAX_OFFSET);
    el<HTMLSelectElement>(page, "ssVad").value = String(DEFAULT_VAD);
    el<HTMLTextAreaElement>(page, "ssRatios").value = defaultRatioText();
  }

  // -------------------------------------------------------------------- picker

  private showPicker(): void {
    show(el(this.page, "ssPicker"), true);
    show(el(this.page, "ssItem"), false);
    el(this.page, "ssHeading").textContent = "Sync subtitles";
  }

  private async runSearch(): Promise<void> {
    const term = el<HTMLInputElement>(this.page, "ssSearch").value.trim();
    const status = el(this.page, "ssPickerStatus");
    const results = el(this.page, "ssPickerResults");

    this.searchController?.abort();
    const controller = new AbortController();
    this.searchController = controller;

    status.textContent = "Searching...";
    try {
      const items = await this.api.searchItems(term, controller.signal);
      results.replaceChildren();

      if (items.length === 0) {
        status.textContent = term
          ? `Nothing in your library matches "${term}".`
          : "Your library has no films or episodes.";
        return;
      }

      status.textContent = term
        ? `${items.length} match${items.length === 1 ? "" : "es"}.`
        : "Recently added. Search above for anything else.";

      for (const item of items) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "listItem listItem-border emby-button";
        button.style.width = "100%";
        button.style.textAlign = "left";
        button.textContent = describeItemLine(item);
        button.addEventListener("click", () => void this.loadItem(item.Id));
        results.appendChild(button);
      }
    } catch (err) {
      if (controller.signal.aborted) return;
      status.textContent = this.explain(err, "The library could not be searched.");
    }
  }

  // ---------------------------------------------------------------------- item

  private async loadItem(itemId: string): Promise<void> {
    const fatal = el(this.page, "ssFatal");
    show(fatal, false);
    this.clearResult();

    try {
      this.item = await this.api.describeItem(itemId);
    } catch (err) {
      this.item = null;
      show(fatal, true);
      fatal.textContent = this.explain(
        err,
        "That item could not be read.",
        "You do not have permission to manage subtitles on this server. Ask an administrator to enable subtitle management for your account.",
      );
      show(el(this.page, "ssItem"), false);
      show(el(this.page, "ssPicker"), true);
      return;
    }

    const query = pageQuery();
    query.set("itemId", itemId);
    try {
      replacePageQuery(query);
    } catch {
      // Cosmetic only: the page already has the item.
    }

    show(el(this.page, "ssPicker"), false);
    show(el(this.page, "ssItem"), true);
    this.renderItem();
  }

  private renderItem(): void {
    const item = this.item;
    if (!item) return;

    const page = this.page;
    const name =
      item.ItemType === "Episode"
        ? describeItemLine({
            Id: item.ItemId,
            Name: item.Name ?? "",
            Type: "Episode",
            SeriesName: item.SeriesName,
            ParentIndexNumber: item.ParentIndexNumber,
            IndexNumber: item.IndexNumber,
          })
        : (item.Name ?? "Untitled");

    el(page, "ssHeading").textContent = "Sync subtitles";
    el(page, "ssItemName").textContent = name;
    el(page, "ssItemMeta").textContent =
      `${item.ItemType} - ${formatDuration(item.RunTimeSeconds)} - ` +
      `${item.MediaSources.length} version${item.MediaSources.length === 1 ? "" : "s"}`;

    const sourceSelect = el<HTMLSelectElement>(page, "ssSource");
    sourceSelect.replaceChildren();
    for (const source of item.MediaSources) {
      const option = document.createElement("option");
      option.value = source.Id;
      option.textContent = source.Name || source.Path || source.Id;
      sourceSelect.appendChild(option);
    }
    show(el(page, "ssSourceContainer"), item.MediaSources.length > 1);

    this.renderTracks();
  }

  private currentSource(): MediaSource | null {
    const item = this.item;
    if (!item || item.MediaSources.length === 0) return null;
    const id = el<HTMLSelectElement>(this.page, "ssSource").value;
    return item.MediaSources.find((s) => s.Id === id) ?? item.MediaSources[0];
  }

  private renderTracks(): void {
    const page = this.page;
    const source = this.currentSource();
    const subtitles = el<HTMLSelectElement>(page, "ssSubtitle");
    const audio = el<HTMLSelectElement>(page, "ssAudio");

    subtitles.replaceChildren();
    audio.replaceChildren();

    if (!source) {
      el(page, "ssSubtitleNote").textContent = "This item has no media to work with.";
      el<HTMLButtonElement>(page, "ssRun").disabled = true;
      return;
    }

    let firstSyncable = -1;
    for (const track of source.SubtitleStreams) {
      const option = document.createElement("option");
      option.value = String(track.Index);
      option.textContent = trackLabel(track);

      // Image-based tracks stay VISIBLE and disabled. Hiding them turns "PGS
      // cannot be re-timed, and never will be" into "your subtitles are
      // missing", which sends people looking for a bug that is not there.
      if (!track.CanSync) {
        option.disabled = true;
        option.textContent = `${option.textContent} - cannot be synced`;
      } else if (firstSyncable < 0) {
        firstSyncable = track.Index;
      }

      subtitles.appendChild(option);
    }

    if (source.SubtitleStreams.length === 0) {
      const option = document.createElement("option");
      option.textContent = "No subtitle tracks on this version";
      option.disabled = true;
      subtitles.appendChild(option);
    }

    if (firstSyncable >= 0) subtitles.value = String(firstSyncable);

    for (const track of source.AudioStreams) {
      const option = document.createElement("option");
      option.value = String(track.Index);
      const detail = [track.Codec, track.Channels ? `${track.Channels}ch` : null]
        .filter(Boolean)
        .join(", ");
      option.textContent = `${track.DisplayTitle || track.Language || `Track ${track.Index}`}${
        detail ? ` - ${detail}` : ""
      }`;
      audio.appendChild(option);
    }

    if (source.AudioStreams.length === 0) {
      const option = document.createElement("option");
      option.textContent = "No audio tracks on this version";
      option.disabled = true;
      audio.appendChild(option);
    } else {
      const preferred =
        source.DefaultAudioStreamIndex ??
        source.AudioStreams.find((a) => a.IsDefault)?.Index ??
        source.AudioStreams[0].Index;
      audio.value = String(preferred);
    }

    this.renderSubtitleNote();
    el<HTMLButtonElement>(page, "ssRun").disabled =
      firstSyncable < 0 || source.AudioStreams.length === 0;
  }

  private currentTrack(): SubtitleTrack | null {
    const source = this.currentSource();
    if (!source) return null;
    const index = Number(el<HTMLSelectElement>(this.page, "ssSubtitle").value);
    return source.SubtitleStreams.find((t) => t.Index === index) ?? null;
  }

  private renderSubtitleNote(): void {
    const note = el(this.page, "ssSubtitleNote");
    const track = this.currentTrack();
    const source = this.currentSource();

    if (!track) {
      note.textContent =
        source && source.SubtitleStreams.length > 0
          ? "None of this version's subtitle tracks can be re-timed."
          : "This version carries no subtitle tracks.";
      return;
    }

    const parts: string[] = [];
    if (track.Note) parts.push(track.Note);
    if (track.StylingWillBeLost) {
      parts.push(
        "This track will be converted to SRT, so its positioning and styling will be lost. The timings are what get fixed.",
      );
    }
    if (track.IsExternal && track.Path) {
      parts.push(`Source file: ${track.Path}`);
    }
    note.textContent = parts.join(" ");
  }

  // ----------------------------------------------------------------- the run

  private options(): { maxOffset: number; vad: 0 | 1 | 2 | 3; ratios?: RatioCandidate[] } {
    const bundle = window.SubtitleSync;
    const page = this.page;

    const rawOffset = Number(el<HTMLInputElement>(page, "ssMaxOffset").value);
    const maxOffset = Number.isFinite(rawOffset) && rawOffset > 0 ? rawOffset : DEFAULT_MAX_OFFSET;
    const vad = Number(el<HTMLSelectElement>(page, "ssVad").value) as 0 | 1 | 2 | 3;

    const text = el<HTMLTextAreaElement>(page, "ssRatios").value.trim();
    if (!bundle || text === "" || text === defaultRatioText()) {
      return { maxOffset, vad };
    }

    const ratios: RatioCandidate[] = [];
    for (const token of text.split(/\s+/).filter(Boolean)) {
      const ratio = bundle.parseRatio(token);
      if (Number.isFinite(ratio) && ratio > 0) ratios.push({ label: token, ratio });
    }
    return ratios.length > 0 ? { maxOffset, vad, ratios } : { maxOffset, vad };
  }

  private setPhase(text: string, detail = "", ratio?: number): void {
    el(this.page, "ssPhase").textContent = text;
    el(this.page, "ssProgressDetail").textContent = detail;
    const bar = el<HTMLProgressElement>(this.page, "ssBar");
    if (ratio === undefined) {
      bar.removeAttribute("value");
    } else {
      bar.value = Math.max(0, Math.min(1, ratio));
    }
  }

  private setRunning(running: boolean): void {
    this.phase = running ? "running" : "idle";
    show(el(this.page, "ssProgress"), running);
    show(el(this.page, "ssCancel"), running);
    show(el(this.page, "ssRun"), !running);
    el<HTMLButtonElement>(this.page, "ssCancel").disabled = false;
  }

  private cancel(): void {
    if (this.phase !== "running") return;
    el<HTMLButtonElement>(this.page, "ssCancel").disabled = true;
    this.setPhase("Cancelling...");
    // Aborting the fetch closes the PCM response, which is what makes the
    // server's ffmpeg process exit rather than decode to nobody.
    this.controller?.abort();
  }

  private async run(): Promise<void> {
    const bundle = window.SubtitleSync;
    const item = this.item;
    const source = this.currentSource();
    const track = this.currentTrack();
    if (!bundle || !item || !source || !track) return;

    const audioIndex = Number(el<HTMLSelectElement>(this.page, "ssAudio").value);
    const { maxOffset, vad, ratios } = this.options();

    this.clearResult();
    this.setRunning(true);
    const controller = new AbortController();
    this.controller = controller;

    try {
      this.setPhase("Reading the subtitle track...");
      const srt = await this.api.subtitle(item.ItemId, track.Index, source.Id, controller.signal);
      const blocks = bundle.parseSrt(srt);
      if (blocks.length === 0) {
        throw new Error("That track converted to an empty subtitle file, so there is nothing to re-time.");
      }
      this.blocks = blocks;

      const signal = await this.speechSignal(item.ItemId, source.Id, audioIndex, vad, controller);

      this.setPhase("Correlating subtitles against speech...");
      await nextFrame(); // analyze() blocks; let the label paint first.

      const analysis = bundle.analyze(signal, blocks, {
        maxOffset,
        vadAggressiveness: vad,
        ratios,
      });

      this.analysis = analysis;
      this.ratioByLabel = new Map(analysis.all.map((r) => [r.label, r.ratio]));
      this.renderResult(blocks.length);
    } catch (err) {
      if (controller.signal.aborted) {
        this.setPhase("");
        this.showError(
          "Cancelled. The server stopped decoding as soon as the connection closed.",
        );
      } else {
        this.showError(this.explain(err, "The sync could not be completed."));
      }
    } finally {
      this.controller = null;
      this.setRunning(false);
    }
  }

  /**
   * The speech signal, from the cache when possible.
   *
   * A hit is roughly 45 KB per hour of runtime; a miss streams the audio, about
   * 115 MB per hour. Skipping the cache check would make every re-run - a
   * different maximum offset, a different ratio list, a second track on the same
   * file - pay full price for a signal the server already has.
   */
  private async speechSignal(
    itemId: string,
    mediaSourceId: string,
    audioIndex: number,
    vad: 0 | 1 | 2 | 3,
    controller: AbortController,
  ): Promise<SpeechSignal> {
    const bundle = window.SubtitleSync!;

    this.setPhase("Checking the speech signal cache...");
    let key: string | null = null;
    try {
      const resolved = await this.api.signalKey(
        itemId,
        mediaSourceId,
        audioIndex,
        vad,
        controller.signal,
      );
      key = resolved.Key;

      const cached = await this.api.cachedSignal(key, controller.signal);
      if (cached) {
        const signal = decodeSpeechSignal(cached);
        this.setPhase(
          "Cache hit - no audio needed.",
          `${formatBytes(cached.length)} instead of about ${formatBytes(
            Math.round((signal.length / 100) * 32000),
          )} of audio.`,
          1,
        );
        return signal;
      }
    } catch (err) {
      // A cancelled run, and a caller who is not allowed to use the plugin at
      // all, are both real failures. Everything else here is survivable: a
      // corrupt or unreachable cache entry just means rebuilding the signal
      // from the audio, which is the authoritative path anyway.
      if (controller.signal.aborted) throw err;
      if (err instanceof ApiError && err.isForbidden) throw err;
      if (!(err instanceof SignalPayloadRejected) && !(err instanceof ApiError)) throw err;
    }

    this.setPhase("Fetching audio from the server...", "", 0);
    const { body, estimatedBytes } = await this.api.openPcm(
      itemId,
      mediaSourceId,
      audioIndex,
      controller.signal,
    );

    // The stream is decoded as it arrives, so "fetching" and "detecting" are the
    // same wall-clock window; the first progress callback is the moment audio
    // actually started flowing, which is the only distinction worth showing.
    let sawBytes = false;
    const signal = await bundle.speechSignalFromPcmStream(
      body,
      () => bundle.createFvadFrameVad(vad),
      {
        totalBytes: estimatedBytes,
        signal: controller.signal,
        onProgress: (progress) => {
          sawBytes = true;
          this.setPhase(
            "Detecting speech in the audio...",
            `${formatDuration(progress.secondsDecoded)} analysed` +
              (estimatedBytes ? " (the total is an estimate)" : ""),
            progress.ratio,
          );
        },
      },
    );

    if (!sawBytes) {
      throw new Error("The server sent no audio for this track.");
    }

    if (key) {
      this.setPhase("Saving the speech signal for next time...", "", 1);
      const stored = await this.api.putSignal(key, encodeSpeechSignal(signal));
      if (!stored) {
        // Worth saying, because the only symptom otherwise is that the next run
        // is slow again for no visible reason.
        el(this.page, "ssProgressDetail").textContent =
          "The signal could not be cached, so the next run will download the audio again.";
      }
    }

    return signal;
  }

  // -------------------------------------------------------------------- result

  private clearResult(): void {
    this.analysis = null;
    this.correctedSrt = "";
    show(el(this.page, "ssResult"), false);
    show(el(this.page, "ssError"), false);
    el(this.page, "ssSaveNote").textContent = "";
  }

  private showError(message: string): void {
    const box = el(this.page, "ssError");
    show(box, true);
    el(box, "ssErrorText").textContent = message;
  }

  private renderResult(cueCount: number): void {
    const page = this.page;
    const analysis = this.analysis;
    if (!analysis) return;

    show(el(page, "ssResult"), true);
    const { best, runnerUp } = analysis;

    const gap =
      runnerUp && best.score > 0
        ? `${(((best.score - runnerUp.score) / best.score) * 100).toFixed(1)}% clear of the runner-up`
        : "no runner-up to compare against";

    el(page, "ssVerdict").textContent =
      `Best match: ratio ${best.label} at ${formatSeconds(best.offset)}, ` +
      `score ${best.score.toFixed(4)}, ${gap}. ` +
      (runnerUp
        ? `Runner-up: ${runnerUp.label} at ${formatSeconds(runnerUp.offset)}, score ${runnerUp.score.toFixed(4)}. `
        : "") +
      `${cueCount} cue${cueCount === 1 ? "" : "s"} will be re-timed.`;

    // The warnings are the honest signal, not `confident`. On a short clip two
    // near-identical ratios can sit 0.1% apart, which makes `confident` false
    // while the offset it found is exactly right - gating the UI on that flag
    // alone would make every correct short-clip sync look like a failure.
    const warnings = el(page, "ssWarnings");
    warnings.replaceChildren();
    if (analysis.warnings.length === 0) {
      warnings.textContent = "No warnings. Check the first cue below before saving anyway.";
    } else {
      for (const warning of analysis.warnings) {
        const line = document.createElement("div");
        line.textContent = `Warning: ${warning}`;
        warnings.appendChild(line);
      }
    }

    const table = el<HTMLTableElement>(page, "ssRatioTable");
    table.replaceChildren();
    const header = table.insertRow();
    for (const heading of ["Ratio", "Offset", "Score", ""]) {
      const cell = document.createElement("th");
      cell.textContent = heading;
      cell.style.textAlign = "left";
      header.appendChild(cell);
    }
    for (const candidate of analysis.all.slice(0, 6)) {
      const row = table.insertRow();
      row.insertCell().textContent = candidate.label;
      row.insertCell().textContent = formatSeconds(candidate.offset);
      row.insertCell().textContent = candidate.score.toFixed(4);
      row.insertCell().textContent =
        candidate === best ? "best" : candidate === runnerUp ? "runner-up" : "";
    }

    const ratioSelect = el<HTMLSelectElement>(page, "ssNudgeRatio");
    ratioSelect.replaceChildren();
    for (const candidate of analysis.all) {
      const option = document.createElement("option");
      option.value = candidate.label;
      option.textContent = `${candidate.label} (${candidate.ratio.toFixed(6)})`;
      ratioSelect.appendChild(option);
    }
    ratioSelect.value = best.label;
    el<HTMLInputElement>(page, "ssNudgeOffset").value = best.offset.toFixed(3);

    this.applyNudge();
  }

  /**
   * Re-applies the correction from the nudge controls.
   *
   * Pure and instant: `applyCorrection` is arithmetic over the already-parsed
   * cue blocks, so nothing is re-fetched, re-decoded or re-analysed.
   */
  private applyNudge(): void {
    const bundle = window.SubtitleSync;
    const blocks = this.blocks;
    if (!bundle || !blocks) return;

    const offset = Number(el<HTMLInputElement>(this.page, "ssNudgeOffset").value);
    const label = el<HTMLSelectElement>(this.page, "ssNudgeRatio").value;
    const ratio = this.ratioByLabel.get(label) ?? 1;
    if (!Number.isFinite(offset)) return;

    const corrected = bundle.applyCorrection(blocks, ratio, offset);
    this.correctedSrt = bundle.writeSrt(corrected);

    const first = corrected[0];
    const original = blocks[0];
    el(this.page, "ssPreview").textContent = first
      ? `First cue moves from ${bundle.secondsToSrtTime(original.start)} to ` +
        `${bundle.secondsToSrtTime(Math.max(0, first.start))}: "${original.text
          .replace(/\s+/g, " ")
          .slice(0, 60)}"`
      : "";
  }

  // ------------------------------------------------------------------- output

  private outputName(): string {
    const item = this.item;
    const track = this.currentTrack();
    const base = (item?.Name ?? "subtitles").replace(/[\\/:*?"<>|]/g, "_");
    const language = track?.Language ? `.${track.Language}` : "";
    return `${base}${language}.synced.srt`;
  }

  private download(): void {
    if (!this.correctedSrt) return;
    const blob = new Blob([this.correctedSrt], {
      type: "application/x-subrip;charset=utf-8",
    });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = this.outputName();
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  }

  private async save(): Promise<void> {
    const item = this.item;
    const source = this.currentSource();
    const track = this.currentTrack();
    const note = el(this.page, "ssSaveNote");
    if (!item || !source || !track || !this.correctedSrt) return;

    const button = el<HTMLButtonElement>(this.page, "ssSave");
    button.disabled = true;
    note.textContent = "Saving...";

    try {
      const result = await this.api.save(item.ItemId, track.Index, source.Id, this.correctedSrt);
      note.textContent =
        `Saved ${result.FileName} (${result.CueCount} cues, ${formatBytes(result.Bytes)}) ` +
        `beside the media file.` +
        (result.OverwroteSource ? " The original subtitle file was replaced." : "") +
        (result.RefreshQueued
          ? " Jellyfin is re-scanning the item, so the new track appears in the player shortly."
          : "");
    } catch (err) {
      if (err instanceof ApiError && err.isForbidden) {
        // The permission split from the epic: analysing is SubtitleManagement,
        // writing to the library is administrator. Not a fault, and not a dead
        // end either - the download below produces the same file.
        note.textContent =
          "Saving to the library needs an administrator account. Your result is ready: " +
          "use Download the .srt and put the file beside the media yourself, or ask an " +
          "administrator to run the save.";
      } else {
        note.textContent = this.explain(err, "The subtitle could not be saved.");
      }
    } finally {
      button.disabled = false;
    }
  }

  // -------------------------------------------------------------------- errors

  private explain(err: unknown, fallback: string, forbidden?: string): string {
    if (err instanceof ApiError) {
      if (err.isForbidden && forbidden) return forbidden;
      if (err.isForbidden) {
        return `${fallback} You do not have permission to do this on this server.`;
      }
      return err.message || fallback;
    }
    if (err instanceof Error && err.message) return err.message;
    return fallback;
  }
}

// ---------------------------------------------------------------------------
// Entry point, called by the bootstrap in syncPage.html
// ---------------------------------------------------------------------------

const controllers = new WeakMap<HTMLElement, SyncPage>();

window.SubtitleSyncPage = {
  init(page: HTMLElement): void {
    // The client keeps up to three views alive and re-shows them, so pageshow
    // fires again on a view it never destroyed. Re-initialising would double
    // every listener.
    if (controllers.has(page)) return;

    const client = window.ApiClient;
    if (!client) {
      throw new Error("The Jellyfin ApiClient is not available on this page.");
    }

    const controller = new SyncPage(page, client);
    controllers.set(page, controller);
    controller.start();
  },

  destroy(page: HTMLElement): void {
    const controller = controllers.get(page);
    if (!controller) return;
    controller.stop();
    controllers.delete(page);
  },
};
