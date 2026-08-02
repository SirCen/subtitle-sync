"use client";
// Subtitle Sync - the real, browser-only subtitle synchronization UI.
//
// Two-panel workspace (chosen design "Variant B"): a LEFT control panel for
// files + advanced options + the primary action, and a RIGHT results panel with
// a confidence readout, offset/ratio stats, warnings, manual nudge, and a dense
// per-ratio table. Everything runs client-side - ffmpeg.wasm extracts audio, a
// WASM VAD detects speech, and lib/sync.ts cross-correlates against the subtitle
// track. The video never leaves the device.
//
// This component wires the REAL pipeline (lib/audio, lib/srt, lib/sync). It owns
// a small state machine (idle → files-ready → processing → done | error), caches
// the extracted speech signal so option tweaks don't re-run ffmpeg needlessly,
// and gates auto-download on confidence.

import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type ChangeEvent,
  type DragEvent,
  type ReactNode,
} from "react";

import { extractSpeechSignal } from "@/lib/audio";
import { parseSrt, writeSrt } from "@/lib/srt";
import {
  analyze,
  applyCorrection,
  parseRatio,
  DEFAULT_RATIOS,
} from "@/lib/sync";
import type {
  SrtBlock,
  SyncResult,
  RatioCandidate,
  SpeechSignal,
} from "@/lib/types";

/* ============================ Constants / helpers ============================ */

const MAX_VIDEO_BYTES = 5 * 1024 ** 3; // hard cap: reject
const SOFT_WARN_BYTES = 1.5 * 1024 ** 3; // soft warning: allow

const DEFAULT_MAX_OFFSET = 120;
const DEFAULT_VAD: VadLevel = 2;

// Extensions we accept as video when the browser doesn't report a video/* MIME.
const VIDEO_EXTENSIONS = new Set([
  "mp4",
  "mkv",
  "mov",
  "avi",
  "webm",
  "m4v",
  "mpg",
  "mpeg",
  "wmv",
  "flv",
  "ts",
  "m2ts",
  "ogv",
  "ogm",
  "3gp",
  "vob",
]);

type VadLevel = 0 | 1 | 2 | 3;

// The advanced "ratios" field defaults to the expression form of DEFAULT_RATIOS,
// one per line. When the field is left untouched we omit `ratios` entirely so the
// analysis uses DEFAULT_RATIOS (which carry friendlier labels like
// "1.0 (offset only)").
const DEFAULT_RATIO_EXPRS = [
  "1.0",
  "23.976/25",
  "25/23.976",
  "24/25",
  "25/24",
  "23.976/24",
  "24/23.976",
  "25/29.97",
  "29.97/25",
  "24/29.97",
  "29.97/24",
];
const DEFAULT_RATIO_TEXT = DEFAULT_RATIO_EXPRS.join("\n");

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB"];
  let v = bytes / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(v < 10 ? 1 : 0)} ${units[i]}`;
}

function formatOffset(sec: number): string {
  const sign = sec >= 0 ? "+" : "−";
  return `${sign}${Math.abs(sec).toFixed(2)}s`;
}

function fileExtension(name: string): string {
  const dot = name.lastIndexOf(".");
  return dot >= 0 ? name.slice(dot + 1).toLowerCase() : "";
}

function isVideoFile(f: File): boolean {
  if (f.type.startsWith("video/")) return true;
  return VIDEO_EXTENSIONS.has(fileExtension(f.name));
}

function fileIdentity(f: File): string {
  return `${f.name}|${f.size}|${f.lastModified}`;
}

/** `movie.en.srt` → `movie.en.synced.srt`. */
function downloadName(srtName: string): string {
  const base = srtName.replace(/\.srt$/i, "");
  return `${base}.synced.srt`;
}

/**
 * Build the `ratios` option from the advanced textarea. Returns `undefined`
 * (→ DEFAULT_RATIOS) when the field is untouched or nothing parses, so the
 * common case keeps DEFAULT_RATIOS' nice labels.
 */
function buildRatios(text: string): RatioCandidate[] | undefined {
  const trimmed = text.trim();
  if (trimmed === "" || trimmed === DEFAULT_RATIO_TEXT) return undefined;
  const tokens = trimmed.split(/\s+/).filter(Boolean);
  const candidates: RatioCandidate[] = [];
  for (const token of tokens) {
    const ratio = parseRatio(token);
    if (Number.isFinite(ratio) && ratio > 0) {
      candidates.push({ label: token, ratio });
    }
  }
  return candidates.length > 0 ? candidates : undefined;
}

function triggerDownload(text: string, filename: string): void {
  const blob = new Blob([text], { type: "application/x-subrip;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

/** Yield to the browser so a just-set "phase" label can paint before we block. */
function tick(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

/* ================================ Types ================================ */

type Phase = "idle" | "files-ready" | "processing" | "done" | "error";

// Sub-phases of the processing stage, used to label the progress readout.
type ProcPhase = "loading" | "extracting" | "detecting" | "matching";

const PROC_LABELS: Record<ProcPhase, string> = {
  loading: "Loading audio engine…",
  extracting: "Extracting audio…",
  detecting: "Detecting speech…",
  matching: "Matching subtitles…",
};

interface CachedSignal {
  key: string; // videoIdentity | vadAggressiveness
  signal: SpeechSignal;
}

/* ============================ Main component ============================ */

export default function SubtitleSync() {
  // Files + validation
  const [videoFile, setVideoFile] = useState<File | null>(null);
  const [srtFile, setSrtFile] = useState<File | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [largeWarning, setLargeWarning] = useState<string | null>(null);
  const [dragActive, setDragActive] = useState(false);

  // Advanced options
  const [maxOffset, setMaxOffset] = useState<number>(DEFAULT_MAX_OFFSET);
  const [vad, setVad] = useState<VadLevel>(DEFAULT_VAD);
  const [ratiosText, setRatiosText] = useState<string>(DEFAULT_RATIO_TEXT);

  // Machine state
  const [phase, setPhase] = useState<Phase>("idle");
  const [procPhase, setProcPhase] = useState<ProcPhase>("loading");
  const [progress, setProgress] = useState(0); // 0..1, ffmpeg extract stage
  const [result, setResult] = useState<SyncResult | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  // Manual nudge / download
  const [manualOffset, setManualOffset] = useState(0);
  const [manualRatioLabel, setManualRatioLabel] = useState("");
  const [correctedText, setCorrectedText] = useState("");
  const [autoDownloaded, setAutoDownloaded] = useState(false);
  const [ratioByLabel, setRatioByLabel] = useState<Map<string, number>>(
    new Map(),
  );

  // Cross-call caches (don't trigger re-render)
  const signalCacheRef = useRef<CachedSignal | null>(null);
  const parsedBlocksRef = useRef<SrtBlock[] | null>(null);
  // The maxOffset that produced the current result - anchors the nudge slider.
  const [resultMaxOffset, setResultMaxOffset] = useState(DEFAULT_MAX_OFFSET);

  const canSync =
    videoFile !== null &&
    srtFile !== null &&
    fileError === null &&
    phase !== "processing";

  const isDefaultMaxOffset = maxOffset === DEFAULT_MAX_OFFSET;
  const isDefaultVad = vad === DEFAULT_VAD;
  const isDefaultRatios = ratiosText.trim() === DEFAULT_RATIO_TEXT;

  /* ------------------------------- file intake ------------------------------- */

  const ingestFiles = useCallback(
    (files: File[]) => {
      if (files.length === 0) return;

      let video = videoFile;
      let srt = srtFile;
      let error: string | null = null;
      let warn: string | null = largeWarning;

      for (const f of files) {
        if (fileExtension(f.name) === "srt") {
          srt = f;
        } else if (isVideoFile(f)) {
          if (f.size > MAX_VIDEO_BYTES) {
            error = `That video is ${formatBytes(
              f.size,
            )}, over the 5 GB limit. Try a smaller file.`;
            continue; // reject: keep any previously accepted video
          }
          video = f;
          warn =
            f.size > SOFT_WARN_BYTES
              ? `This video is ${formatBytes(
                  f.size,
                )}. Everything runs in your browser, so extracting audio may take several minutes and use a lot of memory.`
              : null;
        } else {
          error = `“${f.name}” isn’t a video or an .srt file. Drop a video file and a .srt subtitle.`;
        }
      }

      setVideoFile(video);
      setSrtFile(srt);
      setFileError(error);
      setLargeWarning(warn);
      // Fresh files invalidate any prior result; go back to a pre-run phase.
      setResult(null);
      setErrorMessage(null);
      setPhase(video && srt && !error ? "files-ready" : "idle");
    },
    [videoFile, srtFile, largeWarning],
  );

  const removeFile = useCallback((kind: "video" | "srt") => {
    if (kind === "video") setVideoFile(null);
    else setSrtFile(null);
    setFileError(null);
    setLargeWarning(kind === "video" ? null : (w) => w);
    setResult(null);
    setErrorMessage(null);
    setPhase("idle");
  }, []);

  const onDrop = useCallback(
    (e: DragEvent<HTMLElement>) => {
      e.preventDefault();
      setDragActive(false);
      if (e.dataTransfer?.files?.length) {
        ingestFiles(Array.from(e.dataTransfer.files));
      }
    },
    [ingestFiles],
  );

  const onDragOver = useCallback((e: DragEvent<HTMLElement>) => {
    e.preventDefault();
    setDragActive(true);
  }, []);

  const onDragLeave = useCallback((e: DragEvent<HTMLElement>) => {
    e.preventDefault();
    setDragActive(false);
  }, []);

  const onBrowse = useCallback(
    (e: ChangeEvent<HTMLInputElement>) => {
      if (e.target.files?.length) ingestFiles(Array.from(e.target.files));
      e.target.value = ""; // allow re-selecting the same file
    },
    [ingestFiles],
  );

  /* --------------------------------- sync run -------------------------------- */

  const handleSync = useCallback(async () => {
    if (!videoFile || !srtFile) return;

    setPhase("processing");
    setProcPhase("loading");
    setProgress(0);
    setResult(null);
    setErrorMessage(null);
    setAutoDownloaded(false);

    // 1. Parse subtitles first (cheap) so a bad SRT fails before minutes of
    //    audio extraction.
    let blocks: SrtBlock[];
    try {
      blocks = parseSrt(await srtFile.text());
    } catch {
      setErrorMessage("Couldn’t read any subtitles from this file.");
      setPhase("error");
      return;
    }
    parsedBlocksRef.current = blocks;

    const cacheKey = `${fileIdentity(videoFile)}|${vad}`;

    try {
      // 2. Get the speech signal - reuse the cache when only maxOffset/ratios
      //    changed (same video + same VAD aggressiveness).
      let signal: SpeechSignal;
      const cached = signalCacheRef.current;
      if (cached && cached.key === cacheKey) {
        signal = cached.signal;
        setProcPhase("matching");
      } else {
        let extractStarted = false;
        signal = await extractSpeechSignal(
          videoFile,
          { vadAggressiveness: vad },
          (r) => {
            if (!extractStarted) {
              extractStarted = true;
              setProcPhase("extracting");
            }
            setProgress(r);
            if (r >= 1) setProcPhase("detecting");
          },
        );
        signalCacheRef.current = { key: cacheKey, signal };
        setProcPhase("matching");
      }

      // Let the "Matching subtitles…" label paint before analyze() blocks.
      await tick();

      // 3. Analyze + correct.
      const ratios = buildRatios(ratiosText);
      const analysis = analyze(signal, blocks, {
        maxOffset,
        vadAggressiveness: vad,
        ratios,
      });
      const corrected = applyCorrection(
        blocks,
        analysis.best.ratio,
        analysis.best.offset,
      );
      const srtText = writeSrt(corrected);

      setResult(analysis);
      setResultMaxOffset(maxOffset);
      setRatioByLabel(new Map(analysis.all.map((r) => [r.label, r.ratio])));
      setManualOffset(analysis.best.offset);
      setManualRatioLabel(analysis.best.label);
      setCorrectedText(srtText);
      setPhase("done");

      // 4. Confidence-gated auto-download.
      if (analysis.confident) {
        triggerDownload(srtText, downloadName(srtFile.name));
        setAutoDownloaded(true);
      }
    } catch {
      setErrorMessage(
        "Couldn’t extract audio from this video - it may be corrupt or an unsupported format.",
      );
      setPhase("error");
    }
  }, [videoFile, srtFile, vad, maxOffset, ratiosText]);

  /* ------------------------------- manual nudge ------------------------------ */

  // Re-run ONLY applyCorrection + writeSrt on the already-parsed blocks whenever
  // the nudge controls change. No re-extraction, no re-analysis.
  useEffect(() => {
    if (phase !== "done") return;
    const blocks = parsedBlocksRef.current;
    if (!blocks) return;
    const ratio = ratioByLabel.get(manualRatioLabel) ?? 1.0;
    setCorrectedText(writeSrt(applyCorrection(blocks, ratio, manualOffset)));
  }, [phase, manualOffset, manualRatioLabel, ratioByLabel]);

  const downloadCurrent = useCallback(() => {
    if (!srtFile || !correctedText) return;
    triggerDownload(correctedText, downloadName(srtFile.name));
  }, [srtFile, correctedText]);

  const resetAdvanced = useCallback((field: "maxOffset" | "vad" | "ratios") => {
    if (field === "maxOffset") setMaxOffset(DEFAULT_MAX_OFFSET);
    else if (field === "vad") setVad(DEFAULT_VAD);
    else setRatiosText(DEFAULT_RATIO_TEXT);
  }, []);

  const retry = useCallback(() => {
    setErrorMessage(null);
    setPhase(videoFile && srtFile && !fileError ? "files-ready" : "idle");
  }, [videoFile, srtFile, fileError]);

  /* --------------------------------- render ---------------------------------- */

  return (
    <div className="min-h-screen bg-neutral-100 text-neutral-800 dark:bg-neutral-950 dark:text-neutral-200">
      <header className="flex items-center gap-3 border-b border-neutral-200 bg-white px-6 py-3 dark:border-neutral-800 dark:bg-neutral-900">
        <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-indigo-600 text-white shadow-sm">
          <ClockIcon />
        </div>
        <div>
          <h1 className="text-sm font-semibold leading-tight">Subtitle Sync</h1>
          <p className="text-[11px] leading-tight text-neutral-500">
            Runs entirely in your browser - your video is never uploaded
          </p>
        </div>
      </header>

      <main className="mx-auto grid max-w-6xl grid-cols-1 gap-5 p-5 lg:grid-cols-[360px_minmax(0,1fr)]">
        <LeftPanel
          videoFile={videoFile}
          srtFile={srtFile}
          fileError={fileError}
          largeWarning={largeWarning}
          dragActive={dragActive}
          canSync={canSync}
          processing={phase === "processing"}
          maxOffset={maxOffset}
          vad={vad}
          ratiosText={ratiosText}
          isDefaultMaxOffset={isDefaultMaxOffset}
          isDefaultVad={isDefaultVad}
          isDefaultRatios={isDefaultRatios}
          onDrop={onDrop}
          onDragOver={onDragOver}
          onDragLeave={onDragLeave}
          onBrowse={onBrowse}
          onRemove={removeFile}
          onMaxOffset={setMaxOffset}
          onVad={setVad}
          onRatios={setRatiosText}
          onResetField={resetAdvanced}
          onSync={handleSync}
        />

        <section className="min-h-[560px]">
          {phase === "idle" && <EmptyState hasError={fileError !== null} />}
          {phase === "files-ready" && (
            <ReadyState
              videoName={videoFile?.name ?? ""}
              srtName={srtFile?.name ?? ""}
              largeWarning={largeWarning}
            />
          )}
          {phase === "processing" && (
            <ProcessingState
              procPhase={procPhase}
              progress={progress}
              videoName={videoFile?.name ?? ""}
            />
          )}
          {phase === "error" && (
            <ErrorState message={errorMessage ?? ""} onRetry={retry} />
          )}
          {phase === "done" && result && (
            <ResultState
              result={result}
              srtName={srtFile?.name ?? ""}
              autoDownloaded={autoDownloaded}
              manualOffset={manualOffset}
              manualRatioLabel={manualRatioLabel}
              maxOffset={resultMaxOffset}
              onManualOffset={setManualOffset}
              onManualRatio={setManualRatioLabel}
              onDownload={downloadCurrent}
            />
          )}
        </section>
      </main>
    </div>
  );
}

/* ============================ LEFT: control panel ============================ */

interface LeftPanelProps {
  videoFile: File | null;
  srtFile: File | null;
  fileError: string | null;
  largeWarning: string | null;
  dragActive: boolean;
  canSync: boolean;
  processing: boolean;
  maxOffset: number;
  vad: VadLevel;
  ratiosText: string;
  isDefaultMaxOffset: boolean;
  isDefaultVad: boolean;
  isDefaultRatios: boolean;
  onDrop: (e: DragEvent<HTMLElement>) => void;
  onDragOver: (e: DragEvent<HTMLElement>) => void;
  onDragLeave: (e: DragEvent<HTMLElement>) => void;
  onBrowse: (e: ChangeEvent<HTMLInputElement>) => void;
  onRemove: (kind: "video" | "srt") => void;
  onMaxOffset: (v: number) => void;
  onVad: (v: VadLevel) => void;
  onRatios: (v: string) => void;
  onResetField: (field: "maxOffset" | "vad" | "ratios") => void;
  onSync: () => void;
}

function LeftPanel(props: LeftPanelProps) {
  const inputRef = useRef<HTMLInputElement | null>(null);

  return (
    <aside className="flex flex-col gap-4">
      <Card>
        <SectionTitle>Files</SectionTitle>

        <div
          onDrop={props.onDrop}
          onDragOver={props.onDragOver}
          onDragLeave={props.onDragLeave}
          onClick={() => inputRef.current?.click()}
          className={
            "mt-3 cursor-pointer rounded-xl border-2 border-dashed px-3 py-3 transition-colors " +
            (props.dragActive
              ? "border-indigo-500 bg-indigo-50/60 dark:bg-indigo-500/10"
              : "border-neutral-300 hover:border-indigo-400 dark:border-neutral-700")
          }
        >
          <input
            ref={inputRef}
            type="file"
            accept="video/*,.srt"
            multiple
            className="hidden"
            onChange={props.onBrowse}
            onClick={(e) => e.stopPropagation()}
          />
          <div className="flex flex-col gap-2.5">
            <FileRow
              kind="video"
              label="Video"
              file={props.videoFile}
              hint="Drop a video file (.mp4, .mkv…)"
              onRemove={() => props.onRemove("video")}
            />
            <FileRow
              kind="srt"
              label="Subtitles"
              file={props.srtFile}
              hint="Drop an .srt file"
              onRemove={() => props.onRemove("srt")}
            />
          </div>
          <p className="mt-3 text-center text-[11px] text-neutral-400">
            Drag &amp; drop or click to browse - order doesn’t matter.
          </p>
        </div>

        {props.fileError && <AlertBox tone="error">{props.fileError}</AlertBox>}
        {props.largeWarning && (
          <AlertBox tone="warn">{props.largeWarning}</AlertBox>
        )}

        {/* GRAFT 1: reassurance copy near the dropzone. */}
        <p className="mt-3 flex items-start gap-1.5 text-[11px] leading-relaxed text-neutral-500">
          <span className="mt-0.5 shrink-0 text-emerald-600 dark:text-emerald-400">
            <LockIcon />
          </span>
          <span>
            Private by design: everything runs on your device. Nothing is
            uploaded. Long or large videos can take several minutes to process.
          </span>
        </p>
      </Card>

      <Card>
        <SectionTitle>Advanced options</SectionTitle>
        <div className="mt-3 flex flex-col gap-4">
          <Field
            label="Max offset (seconds)"
            hint="How far apart the tracks may drift."
            isDefault={props.isDefaultMaxOffset}
            onReset={() => props.onResetField("maxOffset")}
          >
            <input
              type="number"
              min={1}
              value={props.maxOffset}
              onChange={(e) =>
                props.onMaxOffset(Math.max(1, Number(e.target.value) || 0))
              }
              className="w-full rounded-md border border-neutral-300 bg-white px-2.5 py-1.5 text-sm tabular-nums outline-none focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-neutral-700 dark:bg-neutral-800"
            />
          </Field>

          <Field
            label={`VAD aggressiveness - ${props.vad}`}
            hint="0 = lenient, 3 = strict. Changing this re-extracts audio."
            isDefault={props.isDefaultVad}
            onReset={() => props.onResetField("vad")}
          >
            <div className="flex items-center gap-3">
              <input
                type="range"
                min={0}
                max={3}
                step={1}
                value={props.vad}
                onChange={(e) =>
                  props.onVad(Number(e.target.value) as VadLevel)
                }
                className="h-1.5 w-full cursor-pointer appearance-none rounded-full bg-neutral-200 accent-indigo-600 dark:bg-neutral-700"
              />
              <div className="flex gap-1 text-[11px] tabular-nums text-neutral-400">
                {[0, 1, 2, 3].map((n) => (
                  <span
                    key={n}
                    className={
                      n === props.vad ? "font-semibold text-indigo-600" : ""
                    }
                  >
                    {n}
                  </span>
                ))}
              </div>
            </div>
          </Field>

          <Field
            label="Framerate ratios"
            hint="One ratio per line (e.g. 23.976/25). Changing this re-runs matching only."
            isDefault={props.isDefaultRatios}
            onReset={() => props.onResetField("ratios")}
          >
            <textarea
              value={props.ratiosText}
              onChange={(e) => props.onRatios(e.target.value)}
              rows={4}
              spellCheck={false}
              className="w-full resize-y rounded-md border border-neutral-300 bg-white px-2.5 py-1.5 font-mono text-[11px] leading-relaxed outline-none focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-neutral-700 dark:bg-neutral-800"
            />
          </Field>
        </div>
      </Card>

      <button
        type="button"
        onClick={props.onSync}
        disabled={!props.canSync}
        className="w-full rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm transition hover:bg-indigo-700 disabled:cursor-not-allowed disabled:bg-neutral-300 disabled:text-neutral-500 dark:disabled:bg-neutral-800 dark:disabled:text-neutral-600"
      >
        {props.processing ? "Syncing…" : "Sync subtitles"}
      </button>
      {!props.canSync && !props.processing && (
        <p className="-mt-2 text-center text-[11px] text-neutral-400">
          {props.fileError
            ? "Fix the errors above to continue."
            : "Add a video and an .srt file to sync."}
        </p>
      )}
    </aside>
  );
}

function FileRow({
  kind,
  label,
  file,
  hint,
  onRemove,
}: {
  kind: "video" | "srt";
  label: string;
  file: File | null;
  hint: string;
  onRemove: () => void;
}) {
  return (
    <div
      className={
        "rounded-lg border px-3 py-2.5 " +
        (file
          ? "border-neutral-200 bg-neutral-50 dark:border-neutral-700 dark:bg-neutral-800/50"
          : "border-dashed border-neutral-300 bg-transparent dark:border-neutral-700")
      }
    >
      <div className="flex items-center gap-2.5">
        <div
          className={
            "flex h-9 w-9 shrink-0 items-center justify-center rounded-md " +
            (kind === "video"
              ? "bg-indigo-100 text-indigo-600 dark:bg-indigo-500/15 dark:text-indigo-400"
              : "bg-emerald-100 text-emerald-600 dark:bg-emerald-500/15 dark:text-emerald-400")
          }
        >
          {kind === "video" ? <FilmIcon /> : <TextIcon />}
        </div>
        <div className="min-w-0 flex-1">
          <p className="text-[10px] font-medium uppercase tracking-wide text-neutral-400">
            {label}
          </p>
          {file ? (
            <>
              <p className="truncate text-sm font-medium">{file.name}</p>
              <p className="text-xs tabular-nums text-neutral-500">
                {formatBytes(file.size)}
              </p>
            </>
          ) : (
            <p className="truncate text-xs text-neutral-400">{hint}</p>
          )}
        </div>
        {file && (
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation();
              onRemove();
            }}
            className="shrink-0 rounded-md border border-neutral-200 bg-white px-2 py-1 text-[11px] font-medium text-neutral-600 transition hover:bg-neutral-50 dark:border-neutral-700 dark:bg-neutral-800 dark:text-neutral-300 dark:hover:bg-neutral-700"
          >
            Remove
          </button>
        )}
      </div>
    </div>
  );
}

/* ============================ RIGHT: results states ============================ */

function EmptyState({ hasError }: { hasError: boolean }) {
  return (
    <Card className="flex h-full min-h-[560px] flex-col items-center justify-center text-center">
      <div className="flex h-20 w-20 items-center justify-center rounded-full bg-neutral-100 text-neutral-300 dark:bg-neutral-800 dark:text-neutral-600">
        <GaugeIcon />
      </div>
      <h2 className="mt-5 text-lg font-semibold">No results yet</h2>
      <p className="mt-1 max-w-xs text-sm text-neutral-500">
        {hasError
          ? "Resolve the file errors on the left, then add a video and subtitle file."
          : "Add a video and its .srt subtitle on the left, then press Sync subtitles. Detection runs locally in your browser."}
      </p>
      <div className="mt-6 flex flex-wrap justify-center gap-2 text-xs text-neutral-400">
        <Pill>Auto framerate detection</Pill>
        <Pill>Offset correction</Pill>
        <Pill>Private - nothing uploaded</Pill>
      </div>
    </Card>
  );
}

function ReadyState({
  videoName,
  srtName,
  largeWarning,
}: {
  videoName: string;
  srtName: string;
  largeWarning: string | null;
}) {
  return (
    <Card className="flex h-full min-h-[560px] flex-col items-center justify-center text-center">
      <div className="flex h-20 w-20 items-center justify-center rounded-full bg-indigo-50 text-indigo-500 dark:bg-indigo-500/10 dark:text-indigo-400">
        <GaugeIcon />
      </div>
      <h2 className="mt-5 text-lg font-semibold">Ready to sync</h2>
      <p className="mt-1 max-w-sm text-sm text-neutral-500">
        <span className="font-medium text-neutral-700 dark:text-neutral-300">
          {videoName}
        </span>{" "}
        and{" "}
        <span className="font-medium text-neutral-700 dark:text-neutral-300">
          {srtName}
        </span>{" "}
        are loaded. Press{" "}
        <span className="font-medium text-neutral-700 dark:text-neutral-300">
          Sync subtitles
        </span>{" "}
        to analyze the audio and detect the correct framerate and offset.
      </p>
      <p className="mt-3 max-w-sm text-xs text-neutral-400">
        Everything happens on your device - nothing is uploaded.
      </p>
      {largeWarning && (
        <div className="mt-5 w-full max-w-md text-left">
          <AlertBox tone="warn">{largeWarning}</AlertBox>
        </div>
      )}
    </Card>
  );
}

function ProcessingState({
  procPhase,
  progress,
  videoName,
}: {
  procPhase: ProcPhase;
  progress: number;
  videoName: string;
}) {
  const extracting = procPhase === "extracting";
  // Only the extraction stage has real progress; other stages show an
  // indeterminate arc with a representative fill.
  const frac = extracting
    ? progress
    : procPhase === "loading"
      ? 0.06
      : procPhase === "detecting"
        ? 0.9
        : 0.97;
  const pct = Math.round(progress * 100);

  return (
    <Card className="flex h-full min-h-[560px] flex-col items-center justify-center text-center">
      <Gauge
        score={frac}
        label={extracting ? `${pct}%` : ""}
        sublabel="working"
        tone="processing"
        pulse={!extracting}
      />
      <h2 className="mt-6 text-lg font-semibold">{PROC_LABELS[procPhase]}</h2>

      {/* GRAFT 1: reassurance copy stays visible during processing. */}
      <p className="mt-2 max-w-md text-sm text-neutral-500">
        This runs entirely in your browser - your video never leaves your
        device. Long or large videos can take several minutes, so this is a good
        time to grab a coffee.
      </p>

      <div className="mt-5 h-2 w-full max-w-md overflow-hidden rounded-full bg-neutral-200 dark:bg-neutral-800">
        <div
          className={
            "h-full rounded-full bg-indigo-600 transition-all " +
            (extracting ? "" : "animate-pulse")
          }
          style={{ width: `${Math.round(frac * 100)}%` }}
        />
      </div>
      <p className="mt-2 text-xs tabular-nums text-neutral-400">
        {extracting ? `${pct}% - extracting audio` : PROC_LABELS[procPhase]}
      </p>
      {videoName && (
        <div className="mt-4 flex items-center gap-2 text-xs text-neutral-400">
          <span className="inline-block h-2 w-2 animate-ping rounded-full bg-indigo-400" />
          {videoName}
        </div>
      )}
    </Card>
  );
}

function ErrorState({
  message,
  onRetry,
}: {
  message: string;
  onRetry: () => void;
}) {
  return (
    <Card className="flex h-full min-h-[560px] flex-col items-center justify-center text-center">
      <div className="flex h-20 w-20 items-center justify-center rounded-full bg-red-100 text-red-500 dark:bg-red-500/15 dark:text-red-400">
        <WarnIcon />
      </div>
      <h2 className="mt-5 text-lg font-semibold">Something went wrong</h2>
      <p className="mt-1 max-w-sm text-sm text-neutral-500">{message}</p>
      <button
        type="button"
        onClick={onRetry}
        className="mt-6 rounded-lg bg-indigo-600 px-5 py-2.5 text-sm font-semibold text-white shadow-sm transition hover:bg-indigo-700"
      >
        Try again
      </button>
    </Card>
  );
}

function ResultState({
  result,
  srtName,
  autoDownloaded,
  manualOffset,
  manualRatioLabel,
  maxOffset,
  onManualOffset,
  onManualRatio,
  onDownload,
}: {
  result: SyncResult;
  srtName: string;
  autoDownloaded: boolean;
  manualOffset: number;
  manualRatioLabel: string;
  maxOffset: number;
  onManualOffset: (v: number) => void;
  onManualRatio: (v: string) => void;
  onDownload: () => void;
}) {
  const confident = result.confident;
  const ratioLabels = result.all.map((r) => r.label);

  return (
    <div className="flex flex-col gap-4">
      {/* Headline: GRAFT 2 - celebratory when confident, cautionary when not. */}
      <Card>
        {confident ? (
          <ConfidentHeader score={result.best.score} />
        ) : (
          <CautionHeader score={result.best.score} />
        )}

        <div className="mt-6 grid grid-cols-2 gap-4">
          <Stat label="Time offset" value={formatOffset(result.best.offset)} big />
          <Stat label="Framerate ratio" value={result.best.label} />
        </div>
        {result.runnerUp && (
          <p className="mt-3 text-xs text-neutral-400">
            Runner-up: {result.runnerUp.label} ·{" "}
            {formatOffset(result.runnerUp.offset)} · score{" "}
            {result.runnerUp.score.toFixed(3)}
          </p>
        )}

        <div className="mt-5 border-t border-neutral-100 pt-4 dark:border-neutral-800">
          {confident ? (
            <div className="flex flex-col items-start gap-3 rounded-xl bg-emerald-50 p-4 dark:bg-emerald-500/10 sm:flex-row sm:items-center sm:justify-between">
              <div className="flex items-center gap-2 text-sm text-emerald-700 dark:text-emerald-300">
                <CheckIcon />
                <span>
                  {autoDownloaded ? "Corrected " : "Ready - corrected "}
                  <code className="font-mono text-xs">
                    {downloadName(srtName)}
                  </code>
                  {autoDownloaded ? " downloaded automatically." : "."}
                </span>
              </div>
              <button
                type="button"
                onClick={onDownload}
                className="shrink-0 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm transition hover:bg-indigo-700"
              >
                Download again
              </button>
            </div>
          ) : (
            <div className="flex flex-col items-start gap-3 rounded-xl bg-amber-50 p-4 dark:bg-amber-500/10 sm:flex-row sm:items-center sm:justify-between">
              <div className="flex items-center gap-2 text-sm text-amber-700 dark:text-amber-300">
                <WarnIcon />
                <span>
                  Low confidence - the corrected file was not downloaded
                  automatically. Check it first.
                </span>
              </div>
              <button
                type="button"
                onClick={onDownload}
                className="shrink-0 rounded-lg border border-amber-500 bg-amber-100 px-4 py-2 text-sm font-semibold text-amber-800 transition hover:bg-amber-200 dark:border-amber-500/50 dark:bg-amber-500/10 dark:text-amber-200"
              >
                Download anyway
              </button>
            </div>
          )}
        </div>
      </Card>

      {result.warnings.length > 0 && (
        <div className="flex flex-col gap-2">
          {result.warnings.map((w, i) => (
            <AlertBox key={i} tone="warn">
              {w}
            </AlertBox>
          ))}
        </div>
      )}

      {/* Manual nudge */}
      <Card>
        <SectionTitle>Manual adjustment</SectionTitle>
        <p className="mt-1 text-xs text-neutral-500">
          Not quite right? Nudge the offset or force a ratio - the download
          updates instantly, without reprocessing the video.
        </p>
        <div className="mt-4 grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Field label={`Offset - ${formatOffset(manualOffset)}`}>
            <input
              type="range"
              min={-maxOffset}
              max={maxOffset}
              step={0.01}
              value={manualOffset}
              onChange={(e) => onManualOffset(Number(e.target.value))}
              className="h-1.5 w-full cursor-pointer appearance-none rounded-full bg-neutral-200 accent-indigo-600 dark:bg-neutral-700"
            />
          </Field>
          <Field label="Framerate ratio">
            <select
              value={manualRatioLabel}
              onChange={(e) => onManualRatio(e.target.value)}
              className="w-full rounded-md border border-neutral-300 bg-white px-2.5 py-1.5 text-sm outline-none focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-neutral-700 dark:bg-neutral-800"
            >
              {ratioLabels.map((r) => (
                <option key={r} value={r}>
                  {r}
                </option>
              ))}
            </select>
          </Field>
        </div>
        <div className="mt-4 flex flex-wrap items-center gap-3">
          <input
            type="number"
            step={0.01}
            value={manualOffset}
            onChange={(e) => onManualOffset(Number(e.target.value) || 0)}
            className="w-32 rounded-md border border-neutral-300 bg-white px-2.5 py-1.5 text-sm tabular-nums outline-none focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-neutral-700 dark:bg-neutral-800"
          />
          <span className="text-xs text-neutral-400">seconds (fine)</span>
          <button
            type="button"
            onClick={onDownload}
            className="ml-auto rounded-lg border border-neutral-300 px-4 py-2 text-sm font-medium transition hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
          >
            Download with adjustments
          </button>
        </div>
      </Card>

      {/* GRAFT 3: dense monospace per-ratio table. */}
      <Card>
        <SectionTitle>All candidates</SectionTitle>
        <RatioTable result={result} />
      </Card>
    </div>
  );
}

function ConfidentHeader({ score }: { score: number }) {
  return (
    <div className="flex flex-col items-center gap-4 text-center sm:flex-row sm:text-left">
      <div className="flex h-20 w-20 shrink-0 items-center justify-center rounded-full bg-emerald-100 text-emerald-600 dark:bg-emerald-500/15 dark:text-emerald-400">
        <BigCheckIcon />
      </div>
      <div>
        <div className="flex items-center justify-center gap-2 sm:justify-start">
          <h2 className="text-2xl font-bold text-emerald-600 dark:text-emerald-400">
            Synced!
          </h2>
          <span className="inline-flex items-center gap-1.5 rounded-full bg-emerald-100 px-3 py-1 text-xs font-semibold text-emerald-700 dark:bg-emerald-500/15 dark:text-emerald-300">
            High confidence
          </span>
        </div>
        <p className="mt-1 text-sm text-neutral-500">
          We’re confident this is a great match - confidence score{" "}
          <span className="font-semibold tabular-nums text-emerald-600 dark:text-emerald-400">
            {score.toFixed(3)}
          </span>
          .
        </p>
      </div>
    </div>
  );
}

function CautionHeader({ score }: { score: number }) {
  return (
    <div className="flex flex-col items-center gap-6 sm:flex-row">
      <Gauge
        score={score}
        label={`${Math.round(score * 100)}`}
        sublabel="confidence"
        tone="bad"
      />
      <div>
        <span className="inline-flex items-center gap-1.5 rounded-full bg-amber-100 px-3 py-1 text-xs font-semibold text-amber-700 dark:bg-amber-500/15 dark:text-amber-400">
          <WarnIcon />
          Low confidence
        </span>
        <h2 className="mt-2 text-xl font-semibold">Best guess - double-check it</h2>
        <p className="mt-1 text-sm text-neutral-500">
          Here’s the strongest candidate we found, but the score is low. Review
          it before relying on it.
        </p>
      </div>
    </div>
  );
}

function RatioTable({ result }: { result: SyncResult }) {
  const rows = result.all;
  const labelW = Math.max(
    ...rows.map((r) => r.label.length),
    "ratio".length,
  );
  const pad = (s: string, w: number) =>
    s + " ".repeat(Math.max(0, w - s.length));
  const padL = (s: string, w: number) =>
    " ".repeat(Math.max(0, w - s.length)) + s;

  const header = `${pad("ratio", labelW)}  ${padL("offset", 9)}  ${padL(
    "score",
    8,
  )}`;
  const sep = "-".repeat(header.length);

  return (
    <div className="mt-3 overflow-x-auto rounded-lg border border-neutral-100 bg-neutral-50 px-3 py-2 dark:border-neutral-800 dark:bg-neutral-950/60">
      <pre className="m-0 font-mono text-[12px] leading-relaxed text-neutral-700 dark:text-neutral-300">
        <span className="text-neutral-400">{header}</span>
        {"\n"}
        <span className="text-neutral-300 dark:text-neutral-700">{sep}</span>
        {"\n"}
        {rows.map((r, i) => {
          const best = i === 0;
          const line = `${pad(r.label, labelW)}  ${padL(
            formatOffset(r.offset),
            9,
          )}  ${padL(r.score.toFixed(5), 8)}`;
          return (
            <span
              key={r.label}
              className={
                best
                  ? "font-semibold text-indigo-600 dark:text-indigo-400"
                  : "text-neutral-600 dark:text-neutral-400"
              }
            >
              {best ? "▸ " : "  "}
              {line}
              {"\n"}
            </span>
          );
        })}
      </pre>
    </div>
  );
}

/* ================================ Gauge ================================ */

function Gauge({
  score,
  label,
  sublabel,
  tone,
  pulse,
}: {
  score: number;
  label: string;
  sublabel: string;
  tone: "good" | "bad" | "processing";
  pulse?: boolean;
}) {
  const frac = Math.max(0, Math.min(1, score));
  const size = 132;
  const stroke = 12;
  const r = (size - stroke) / 2;
  const circ = 2 * Math.PI * r;
  const dash = circ * frac;
  const color =
    tone === "good" ? "#059669" : tone === "bad" ? "#d97706" : "#4f46e5";

  return (
    <div
      className={"relative shrink-0" + (pulse ? " animate-pulse" : "")}
      style={{ width: size, height: size }}
    >
      <svg width={size} height={size} className="-rotate-90">
        <circle
          cx={size / 2}
          cy={size / 2}
          r={r}
          fill="none"
          strokeWidth={stroke}
          className="stroke-neutral-200 dark:stroke-neutral-800"
        />
        <circle
          cx={size / 2}
          cy={size / 2}
          r={r}
          fill="none"
          stroke={color}
          strokeWidth={stroke}
          strokeLinecap="round"
          strokeDasharray={`${dash} ${circ}`}
          style={{ transition: "stroke-dasharray .4s ease" }}
        />
      </svg>
      <div className="absolute inset-0 flex flex-col items-center justify-center">
        <span className="text-3xl font-bold tabular-nums" style={{ color }}>
          {label || "…"}
        </span>
        <span className="text-[10px] uppercase tracking-wide text-neutral-400">
          {sublabel}
        </span>
      </div>
    </div>
  );
}

/* ============================ Small shared UI ============================ */

function Card({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) {
  return (
    <div
      className={
        "rounded-2xl border border-neutral-200 bg-white p-5 shadow-sm dark:border-neutral-800 dark:bg-neutral-900 " +
        className
      }
    >
      {children}
    </div>
  );
}

function SectionTitle({ children }: { children: ReactNode }) {
  return (
    <h3 className="text-xs font-semibold uppercase tracking-wide text-neutral-500 dark:text-neutral-400">
      {children}
    </h3>
  );
}

function Field({
  label,
  hint,
  children,
  isDefault,
  onReset,
}: {
  label: string;
  hint?: string;
  children: ReactNode;
  isDefault?: boolean;
  onReset?: () => void;
}) {
  return (
    <label className="block">
      <span className="mb-1.5 flex items-center justify-between gap-2">
        <span className="text-xs font-medium text-neutral-600 dark:text-neutral-300">
          {label}
        </span>
        {onReset && (
          <button
            type="button"
            onClick={onReset}
            disabled={isDefault}
            title="Reset to default"
            aria-label={`Reset ${label} to default`}
            className="inline-flex h-5 items-center gap-1 rounded px-1 text-[11px] font-medium text-neutral-400 transition hover:text-indigo-600 disabled:cursor-default disabled:opacity-30 disabled:hover:text-neutral-400"
          >
            <ResetIcon />
            reset
          </button>
        )}
      </span>
      {children}
      {hint && (
        <span className="mt-1 block text-[11px] text-neutral-400">{hint}</span>
      )}
    </label>
  );
}

function Stat({
  label,
  value,
  big,
}: {
  label: string;
  value: string;
  big?: boolean;
}) {
  return (
    <div>
      <p className="text-[10px] font-medium uppercase tracking-wide text-neutral-400">
        {label}
      </p>
      <p
        className={
          "tabular-nums font-semibold " +
          (big ? "text-2xl" : "text-lg break-words leading-tight")
        }
      >
        {value}
      </p>
    </div>
  );
}

function AlertBox({
  tone,
  children,
}: {
  tone: "error" | "warn";
  children: ReactNode;
}) {
  const cls =
    tone === "error"
      ? "border-red-200 bg-red-50 text-red-700 dark:border-red-500/30 dark:bg-red-500/10 dark:text-red-300"
      : "border-amber-200 bg-amber-50 text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300";
  return (
    <div
      className={
        "mt-3 flex gap-2 rounded-lg border px-3 py-2.5 text-xs leading-relaxed " +
        cls
      }
    >
      <span className="mt-0.5 shrink-0">
        {tone === "error" ? <WarnIcon /> : <InfoIcon />}
      </span>
      <span>{children}</span>
    </div>
  );
}

function Pill({ children }: { children: ReactNode }) {
  return (
    <span className="rounded-full border border-neutral-200 bg-neutral-50 px-2.5 py-1 dark:border-neutral-700 dark:bg-neutral-800">
      {children}
    </span>
  );
}

/* ============================ Icons (inline SVG) ============================ */

function iconProps(extra = "") {
  return {
    width: 16,
    height: 16,
    viewBox: "0 0 24 24",
    fill: "none",
    stroke: "currentColor",
    strokeWidth: 2,
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const,
    className: extra,
  };
}

function ClockIcon() {
  return (
    <svg {...iconProps()}>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3 2" />
    </svg>
  );
}
function FilmIcon() {
  return (
    <svg {...iconProps()}>
      <rect x="3" y="4" width="18" height="16" rx="2" />
      <path d="M7 4v16M17 4v16M3 9h4M3 15h4M17 9h4M17 15h4" />
    </svg>
  );
}
function TextIcon() {
  return (
    <svg {...iconProps()}>
      <path d="M5 4h14M5 9h14M5 14h9M5 19h6" />
    </svg>
  );
}
function GaugeIcon() {
  return (
    <svg {...iconProps("h-9 w-9")} width={36} height={36}>
      <path d="M12 14a6 6 0 1 1 8 0" />
      <path d="M12 14l3-3" />
      <circle cx="12" cy="14" r="1" fill="currentColor" />
    </svg>
  );
}
function CheckIcon() {
  return (
    <svg {...iconProps("h-4 w-4")}>
      <path d="M20 6L9 17l-5-5" />
    </svg>
  );
}
function BigCheckIcon() {
  return (
    <svg {...iconProps("h-9 w-9")} width={36} height={36}>
      <path d="M20 6L9 17l-5-5" />
    </svg>
  );
}
function WarnIcon() {
  return (
    <svg {...iconProps("h-4 w-4")}>
      <path d="M12 9v4M12 17h.01" />
      <path d="M10.3 3.9L2 18a2 2 0 0 0 1.7 3h16.6a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z" />
    </svg>
  );
}
function InfoIcon() {
  return (
    <svg {...iconProps("h-4 w-4")}>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 11v5M12 8h.01" />
    </svg>
  );
}
function LockIcon() {
  return (
    <svg {...iconProps("h-3.5 w-3.5")} width={14} height={14}>
      <rect x="5" y="11" width="14" height="10" rx="2" />
      <path d="M8 11V7a4 4 0 0 1 8 0v4" />
    </svg>
  );
}
function ResetIcon() {
  return (
    <svg {...iconProps("h-3 w-3")} width={12} height={12}>
      <path d="M3 12a9 9 0 1 0 3-6.7L3 8" />
      <path d="M3 3v5h5" />
    </svg>
  );
}
