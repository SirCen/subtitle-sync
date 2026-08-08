// Every HTTP call the sync page makes, in one place.
//
// Two rules run through all of it:
//
//   - Jellyfin's JSON is PascalCase. `MediaSources[].SubtitleStreams[].Index`,
//     not `mediaSources[].subtitleStreams[].index`. The interfaces below are
//     the wire shape, not a camelCase view of it.
//   - `fetch` rather than `ApiClient.ajax`, because the PCM response has to be
//     consumed as a ReadableStream and aborted mid-body. ApiClient is still what
//     builds URLs and holds the token, so nothing here hardcodes a base path.

import type { ApiClientLike } from "./jellyfin";

/** What the server refused to do, unwrapped from its ProblemDetails body. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly url: string,
  ) {
    super(message);
    this.name = "ApiError";
  }

  /** True when the caller is authenticated but not allowed to do this. */
  get isForbidden(): boolean {
    return this.status === 403;
  }
}

// ---------------------------------------------------------------------------
// Wire models (PascalCase, mirroring Jellyfin.Plugin.SubtitleSync.Api)
// ---------------------------------------------------------------------------

/** Mirrors `SubtitleTrackSupport`. */
export type SubtitleTrackSupport = "Supported" | "ImageBased" | "UnknownFormat";

export interface SubtitleTrack {
  Index: number;
  MediaSourceId: string;
  Language?: string | null;
  Codec?: string | null;
  Title?: string | null;
  DisplayTitle?: string | null;
  IsExternal: boolean;
  Path?: string | null;
  IsDefault: boolean;
  IsForced: boolean;
  IsHearingImpaired: boolean;
  Support: SubtitleTrackSupport;
  CanSync: boolean;
  StylingWillBeLost: boolean;
  Note?: string | null;
}

export interface AudioStream {
  Index: number;
  Language?: string | null;
  Codec?: string | null;
  Title?: string | null;
  DisplayTitle?: string | null;
  Channels?: number | null;
  SampleRate?: number | null;
  IsDefault: boolean;
}

export interface MediaSource {
  Id: string;
  Name?: string | null;
  Path?: string | null;
  Container?: string | null;
  RunTimeTicks?: number | null;
  DefaultAudioStreamIndex?: number | null;
  AudioStreams: AudioStream[];
  SubtitleStreams: SubtitleTrack[];
}

export interface ItemDescription {
  ItemId: string;
  Name?: string | null;
  ItemType: string;
  SeriesName?: string | null;
  ParentIndexNumber?: number | null;
  IndexNumber?: number | null;
  RunTimeTicks?: number | null;
  RunTimeSeconds?: number | null;
  MediaSources: MediaSource[];
  HasSyncableSubtitles: boolean;
}

export interface SignalKey {
  Key: string;
  MediaSourceId?: string | null;
  AudioStreamIndex: number;
  VadAggressiveness: number;
}

export interface SaveResult {
  Path: string;
  FileName: string;
  Language?: string | null;
  OverwroteSource: boolean;
  Bytes: number;
  CueCount: number;
  RefreshQueued: boolean;
}

/** One row of the library search the picker renders. */
export interface LibraryItem {
  Id: string;
  Name: string;
  Type: string;
  ProductionYear?: number | null;
  SeriesName?: string | null;
  ParentIndexNumber?: number | null;
  IndexNumber?: number | null;
}

// ---------------------------------------------------------------------------

/**
 * Advisory total body size for the PCM stream.
 *
 * NOT a Content-Length, and deliberately so: the PCM endpoint sends none,
 * because the obvious runtime-times-32000 formula disagrees with the real body
 * by a few hundred bytes. Use it to move a progress bar and nothing else.
 */
export const ESTIMATED_LENGTH_HEADER = "X-SubtitleSync-Estimated-Length";

export class SubtitleSyncApi {
  constructor(private readonly client: ApiClientLike) {}

  /**
   * The auth header for a raw fetch.
   *
   * Token-only is a complete `MediaBrowser` credential as far as 10.11 is
   * concerned - the token already identifies the device it was issued to - and
   * it avoids depending on which of the ApiClient accessors this client build
   * exposes.
   */
  private headers(extra?: Record<string, string>): Record<string, string> {
    return {
      Authorization: `MediaBrowser Token="${this.client.accessToken()}"`,
      ...extra,
    };
  }

  private url(path: string, params?: Record<string, string | number | undefined>): string {
    const clean: Record<string, string> = {};
    for (const [key, value] of Object.entries(params ?? {})) {
      if (value !== undefined && value !== null && value !== "") {
        clean[key] = String(value);
      }
    }
    return this.client.getUrl(path, clean);
  }

  /**
   * Turns a non-2xx response into an ApiError carrying the server's own
   * explanation, which is nearly always more useful than the status code.
   */
  private static async fail(response: Response): Promise<never> {
    let detail = "";
    try {
      const text = await response.text();
      if (text) {
        try {
          const body = JSON.parse(text) as { detail?: string; title?: string };
          detail = body.detail ?? body.title ?? text;
        } catch {
          detail = text;
        }
      }
    } catch {
      // A body we cannot read is not worth a second failure.
    }

    throw new ApiError(
      detail || `${response.status} ${response.statusText}`,
      response.status,
      response.url,
    );
  }

  private async json<T>(url: string, signal?: AbortSignal): Promise<T> {
    const response = await fetch(url, { headers: this.headers(), signal });
    if (!response.ok) return SubtitleSyncApi.fail(response);
    return (await response.json()) as T;
  }

  /** Item metadata: versions, audio tracks, subtitle tracks and their support. */
  describeItem(itemId: string, signal?: AbortSignal): Promise<ItemDescription> {
    return this.json<ItemDescription>(this.url(`SubtitleSync/Item/${itemId}`), signal);
  }

  /**
   * The cache key for one (version, audio track, VAD mode) combination.
   *
   * Has to come from the server: two of the six inputs are the media file's
   * length and last write time.
   */
  signalKey(
    itemId: string,
    mediaSourceId: string | undefined,
    audioStreamIndex: number | undefined,
    vadAggressiveness: number,
    signal?: AbortSignal,
  ): Promise<SignalKey> {
    return this.json<SignalKey>(
      this.url(`SubtitleSync/SignalKey/${itemId}`, {
        mediaSourceId,
        audioStreamIndex,
        vadAggressiveness,
      }),
      signal,
    );
  }

  /**
   * A cached speech signal, or null on a miss.
   *
   * A miss is the normal first answer for any file, so it is a return value
   * rather than an exception.
   */
  async cachedSignal(key: string, signal?: AbortSignal): Promise<Uint8Array | null> {
    const response = await fetch(this.url(`SubtitleSync/Signal/${key}`), {
      headers: this.headers(),
      signal,
    });

    if (response.status === 404) return null;
    if (!response.ok) return SubtitleSyncApi.fail(response);

    return new Uint8Array(await response.arrayBuffer());
  }

  /**
   * Contributes a signal back to the cache.
   *
   * Best effort by design: the sync has already succeeded by the time this
   * runs, and a full or read-only cache must not turn a good result into an
   * error. Returns whether it landed, for the readout.
   */
  async putSignal(key: string, envelope: Uint8Array): Promise<boolean> {
    try {
      const response = await fetch(this.url(`SubtitleSync/Signal/${key}`), {
        method: "POST",
        headers: this.headers({ "Content-Type": "application/octet-stream" }),
        // A fresh ArrayBuffer, because a Uint8Array view over a larger buffer
        // would otherwise be sent in full.
        body: envelope.slice().buffer as ArrayBuffer,
      });
      return response.ok;
    } catch {
      return false;
    }
  }

  /**
   * Opens the PCM stream. The caller owns the body and must consume or cancel
   * it: aborting the signal is what kills the server's ffmpeg process.
   */
  async openPcm(
    itemId: string,
    mediaSourceId: string | undefined,
    audioStreamIndex: number | undefined,
    signal: AbortSignal,
  ): Promise<{ body: ReadableStream<Uint8Array>; estimatedBytes?: number }> {
    const response = await fetch(
      this.url(`SubtitleSync/Pcm/${itemId}`, { mediaSourceId, audioStreamIndex }),
      { headers: this.headers(), signal },
    );

    if (!response.ok) return SubtitleSyncApi.fail(response);
    if (!response.body) {
      throw new ApiError("The audio stream arrived with no body.", 200, response.url);
    }

    const advertised = Number(response.headers.get(ESTIMATED_LENGTH_HEADER));
    return {
      body: response.body,
      estimatedBytes: Number.isFinite(advertised) && advertised > 0 ? advertised : undefined,
    };
  }

  /** One subtitle track, converted to SRT by the server. */
  async subtitle(
    itemId: string,
    index: number,
    mediaSourceId: string | undefined,
    signal?: AbortSignal,
  ): Promise<string> {
    const response = await fetch(
      this.url(`SubtitleSync/Subtitle/${itemId}`, { index, mediaSourceId }),
      { headers: this.headers(), signal },
    );
    if (!response.ok) return SubtitleSyncApi.fail(response);
    return response.text();
  }

  /**
   * Writes the corrected track beside the media file. Administrator only - a
   * user who could run the analysis may still get a 403 here, which the caller
   * is expected to present as a limit rather than a fault.
   */
  async save(
    itemId: string,
    index: number,
    mediaSourceId: string | undefined,
    srt: string,
  ): Promise<SaveResult> {
    const response = await fetch(
      this.url(`SubtitleSync/Save/${itemId}`, { index, mediaSourceId }),
      {
        method: "POST",
        headers: this.headers({ "Content-Type": "text/plain; charset=utf-8" }),
        body: srt,
      },
    );
    if (!response.ok) return SubtitleSyncApi.fail(response);
    return (await response.json()) as SaveResult;
  }

  /**
   * Library search for the picker. Core Jellyfin, not the plugin: the picker
   * offers whatever the signed-in user can already see.
   */
  async searchItems(term: string, signal?: AbortSignal): Promise<LibraryItem[]> {
    const params: Record<string, string | number | undefined> = {
      userId: this.client.getCurrentUserId(),
      recursive: "true",
      includeItemTypes: "Movie,Episode",
      fields: "ParentId",
      limit: 40,
      searchTerm: term || undefined,
      // Without a search term this is "what would I most likely want to fix?",
      // which is whatever was added last.
      sortBy: term ? "SortName" : "DateCreated",
      sortOrder: term ? "Ascending" : "Descending",
    };

    const result = await this.json<{ Items?: LibraryItem[] }>(
      this.url("Items", params),
      signal,
    );
    return result.Items ?? [];
  }
}
