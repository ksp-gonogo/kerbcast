import type {
  AdaptiveShedPayload,
  Anchor,
  CameraState,
  ClientMessage,
  CommandAim,
  CommandCentreInfo,
  CommandVantage,
  DisplaySizeReport,
  ErrorPayload,
  Layer,
  QualityPreset,
  ServerMessage,
  SettingsStatePayload,
  TrackMode,
} from "./__generated__/types";
import { CameraKind, CameraLifecycle, CommandAimKind, ErrorCode, ErrorSource } from "./__generated__/types";
import {
  type DisplayedFrameUt,
  type FrameUt,
  FrameHistory,
  FrameStampStore,
  STAMP_TO_DISPLAY_MAX_MS,
  type VideoWithFrameCallback,
  extrapolateCaptureUt,
  monotonicNowMs,
} from "./frameUt";
import {
  type DisplayedFrameMetadata,
  type DisplayedSourceFrame,
  type NoisePipeline,
  tryCreateNoisePipeline,
} from "./noise";
import {
  RecordingController,
  type RecordingControllerOptions,
  type RecordingTiming,
} from "./recording";
import type { EncodedFrameCopy } from "./captureClip";
import { MAX_ANCHORS_PER_CAMERA } from "./anchors";
import { CommandCentreHistory } from "./commandTrack";

/** Cadence of estimated frames from a watcher on a browser without `requestVideoFrameCallback`. */
const ESTIMATED_FRAME_INTERVAL_MS = 100;
/* How long after an element's last exactly identified frame that frame still bounds an unidentified one. */
const LAST_EXACT_MAX_AGE_MS = 1000;
/** Intensity the static runs at when a camera has no live source (signal lost). */
const SOURCELESS_INTENSITY = 1.0;
/** Floor applied to the degrade-driven intensity of a live feed. */
const LIVE_INTENSITY_FLOOR = 0.05;
/**
 * While a feed is stalled, how often to re-request a keyframe. A stall is
 * usually a wedged decoder that only a fresh IDR clears; the first request or
 * its keyframe can be lost during the same outage, so we keep asking until
 * frames resume.
 */
const STALL_KEYFRAME_RETRY_MS = 2000;
/**
 * The fewest slots one re-offer adds. A page opening several feeds at once
 * gets them in one exchange instead of one per feed.
 */
const SLOT_GROWTH_STEP = 4;
/**
 * How long a slot re-offer may wait for the sidecar's answer before it counts
 * as failed, so a hung request cannot leave the pool stuck mid-grow.
 */
const RENEGOTIATE_TIMEOUT_MS = 10_000;
/**
 * Waits before each retry of a failed slot re-offer, the last repeating
 * while any camera still waits for a slot.
 */
const GROW_RETRY_DELAYS_MS = [2_000, 5_000, 15_000, 30_000];
/**
 * How often a receiver whose transform is watched is checked for arriving
 * packets: quickly for its first minute, then slowly for as long as it stays
 * idle. A freed slot that is never fed again is so polled every 2 s until
 * disconnect. A stats call taking longer than the timeout is abandoned so the
 * next check can ask again.
 *
 * The passthrough, or a camera's transform whose host gave its hold, is
 * checked for decoded frames once packets arrive. An instance replaced on
 * the receiver may go on decoding what it held for up to its hold after the
 * handover. A frame decoded once that drain is over, and once packets have
 * arrived for the transform's own hold, ends the watch. A receiver where a
 * camera's transform with no hold given was replaced, whose drain has no
 * bound, is renewed once when packets first arrive and checked after.
 * Packets arriving with none decoded for the longer of the hold and the
 * drain left, plus the stall window (doubling with each renewal after the
 * first), earn a renewal, up to the retry limit.
 * A gap in packets restarts that count only when longer than the idle
 * window, so a camera at a few frames a second still builds one. A camera's
 * transform with no hold given is renewed once when packets first arrive.
 * See {@link KerbcastClient._syncEncodedTransforms}.
 */
const TRANSFORM_HEAL_POLL_MS = 250;
const TRANSFORM_HEAL_FAST_FOR_MS = 60_000;
const TRANSFORM_HEAL_SLOW_POLL_MS = 2_000;
const TRANSFORM_HEAL_STATS_TIMEOUT_MS = 5_000;
const TRANSFORM_HEAL_STALL_MS = 2_000;
const TRANSFORM_HEAL_IDLE_MS = 1_000;
/**
 * How often a hold given as a function is read while its transform is
 * installed, checked or not, so the longest it reached is known when the
 * instance is replaced and may drain for that long.
 */
const TRANSFORM_HOLD_SAMPLE_MS = 2_000;
const TRANSFORM_HEAL_RETRIES = 3;
/**
 * Failed re-offers in a row after which a connection stops growing. Each
 * failure after the sidecar answered leaves it a few stopped transceivers it
 * keeps for the connection's life, so the retries must end.
 */
const MAX_GROW_FAILURES = 6;

/** The sidecar refused a re-offer outright, so sending it again cannot help. */
class RenegotiateRefusedError extends Error {}

/** Controls the digital-static noise overlay baked into `cam.mediaStream`. */
export interface NoiseConfig {
  /** When false, the pipeline is bypassed and `mediaStream` is the raw WebRTC track. */
  enabled: boolean;
}

/**
 * Per-camera handle returned from {@link KerbcastClient.camera}.
 * Stable identity per flight ID for the lifetime of the client.
 */
export interface KerbcastCameraHandle {
  readonly flightId: number;
  readonly state: CameraState | null;
  readonly mediaStream: MediaStream | null;
  /**
   * True while the camera has a live source whose frames have stopped
   * arriving (the noise pipeline's stall detector). Always false when the
   * noise pipeline is unavailable (no `captureStream`) or bypassed.
   * Transitions are emitted as `stall` events.
   */
  readonly stalled: boolean;
  /**
   * True while this client receives the camera's video: its track is bound
   * and live. False before the first track, after its slot is freed or moved
   * to another camera, once the camera is destroyed or its track ends, and
   * while disconnected. Transitions are emitted as `source` events.
   */
  readonly hasSource: boolean;
  /**
   * True while this client wants the camera's video but the connection has
   * no video slot to carry it, and cannot add one. The camera gets the next
   * slot another camera gives up. A client that can grow its slot pool adds
   * one instead, so this stays false. Transitions are emitted as `waiting`
   * events.
   */
  readonly waitingForSlot: boolean;
  /** Current {@link setShowStatic} setting. */
  readonly showStatic: boolean;
  /**
   * The feed's physical/ceiling maximum render size, or `null` until known.
   * Read from the live camera state's `maxWidth` / `maxHeight`, falling back
   * to the last `client.discover()` for a sidecar that predates those fields.
   * A camera that appears after the page's discovery still gets a ceiling
   * from its first state.
   * Used by `RecordingController` to recognise a force-full-resolution feed
   * that has actually reached its ceiling, as opposed to one merely
   * unchanged since before the force was sent.
   */
  readonly maxRenderSize: { width: number; height: number } | null;

  setLayers(layers: Layer[]): Promise<void>;
  setRenderSize(width: number, height: number): Promise<void>;
  setFov(fov: number): Promise<void>;
  setPan(yaw: number, pitch: number): Promise<void>;
  /**
   * Set a persistent pan/tilt velocity (normalised, -1..1 per axis;
   * +yaw = right, +pitch = up). Holds until superseded — send
   * `setPanRate(0, 0)` to stop. Smoothing happens in the plugin's frame
   * loop, so call this only when the input changes (e.g. arrow down /
   * release), not on a timer.
   */
  setPanRate(yawRate: number, pitchRate: number): Promise<void>;
  /**
   * Set a persistent zoom velocity (normalised, -1..1; +1 = zoom in,
   * FoV decreasing). Holds until superseded — send `setZoomRate(0)` to
   * stop. Like {@link setPanRate}, call on input change (press-and-hold),
   * not on a timer.
   */
  setZoomRate(rate: number): Promise<void>;
  setDegrade(level: number): Promise<void>;
  /**
   * Request a resolution preset for this camera, or `null` for auto
   * (clear the viewer clamp). Server-wide and last-write-wins across
   * peers; the authoritative result arrives as a `change` event
   * (`state.viewerQuality` + `state.qualityLimitedBy`). The effective
   * resolution is min(operator ceiling, adaptive level, preset): a
   * preset can only lower quality, and the sidecar's adaptive perf
   * machinery keeps overriding it downward while throttled.
   */
  setQuality(preset: QualityPreset | null): Promise<void>;
  /**
   * Force this feed to render at the operator's configured maximum
   * regardless of display size, until released by calling this with
   * `false`. Per-viewer, OR-aggregated server-side across viewers: a feed
   * stays forced while any viewer forces it, and releases when the last one
   * clears it (or disconnects); sent again after a reconnect. Overrides the
   * display-size demand and any viewer quality preset (the preset returns
   * on release); still capped by an operator render-size cap and the
   * adaptive shed.
   */
  setForceFullResolution(force: boolean): Promise<void>;
  /**
   * Hold this feed at the size it is rendering now, until released by
   * calling this with `false`, so a recording never changes size mid-clip.
   * While held, load is shed through the frame rate instead, and size
   * changes (display size, quality presets) wait for the release.
   * Per-viewer and OR-aggregated server-side, like
   * {@link setForceFullResolution}, and sent again after a reconnect. Only a
   * sidecar whose `hello` offers holds (1.12 and later) receives it. The
   * recorder holds a feed for as long as it records it.
   */
  setSizeHold(hold: boolean): Promise<void>;
  requestKeyframe(): Promise<void>;

  /**
   * Override noise settings for this camera only. Takes precedence over the
   * client-level default in {@link KerbcastClientConfig.noise}.
   */
  configure(options: { noise?: Partial<NoiseConfig> }): void;

  /**
   * Choose whether animated static is drawn (default true). When false:
   * a stalled source holds its last decoded frame with no noise (the
   * consumer's chrome carries the staleness indicator via the `stall`
   * event / {@link stalled}), and a sourceless path clears to black
   * without noise so only the text overlay renders.
   */
  setShowStatic(enabled: boolean): void;

  /**
   * Run a transform on this camera's encoded frames before they are decoded,
   * and so before Kerbcast's static and frame lock see them. Kerbcast calls
   * `factory` for a fresh instance each time the camera lands on a receiver
   * (connect, slot rebind, reconnect) or its stream starts over on the one it
   * has (back in the list after a scene change, back from destroyed, or a new
   * {@link CameraState.attach} from a reattach in place), and removes it from a
   * receiver the camera leaves; frames the old instance still holds are
   * discarded. On the `"script"` path Chromium can leave an instance handed
   * to an idle receiver without frames, and one built while packets flow is
   * wired at once. With {@link EncodedTransformOptions.holdMs} given, an
   * install is renewed from the factory only when packets keep arriving with
   * no frame decoded past the hold; one whose frames decode is left alone.
   * Without it, each install is renewed once when packets next arrive on its
   * receiver, which drops whatever the first instance held.
   * Build the instance inside the factory call. The setting persists across
   * reconnects. `null` detaches.
   *
   * Needs {@link KerbcastClientConfig.encodedTransforms}; throws without it,
   * or when the browser has no install path. Failures while installing
   * (the factory throws, or returns the wrong kind for the path) surface as
   * client `error` events.
   */
  setEncodedTransform(factory: EncodedTransformFactory | null, opts?: EncodedTransformOptions): void;

  /**
   * Copy this camera's encoded frames, as received and before decode, to
   * `onFrame` until the returned function is called. What the camera shows
   * is unchanged. Returns null when frames cannot be copied: the client was
   * built without `recordingTiming: "capture"`, the browser has no encoded
   * transform path, or (where the browser uses `RTCRtpScriptTransform`) the
   * camera already runs its own {@link setEncodedTransform}. Follows the
   * camera across receivers, so a copy can switch synchronisation source
   * mid-stream. On that same `RTCRtpScriptTransform` path, setting a
   * transform on a tapped camera stops the copies until it is removed; the
   * client emits an `error` event when that happens.
   */
  tapEncodedFrames(onFrame: (frame: EncodedFrameCopy) => void): (() => void) | null;

  /** Whether {@link tapEncodedFrames} would copy frames right now. */
  canTapEncodedFrames(): boolean;

  on<K extends keyof KerbcastCameraEvents>(
    event: K,
    handler: (data: KerbcastCameraEvents[K]) => void,
  ): () => void;
}

/** Event payloads emitted by a {@link KerbcastCameraHandle}. */
export interface KerbcastCameraEvents {
  /** Latest `CameraState` from the sidecar's `camera-state-changed`. */
  change: CameraState;
  /** Track arrival or teardown for this camera. `null` on disconnect. */
  stream: MediaStream | null;
  /** Frames stopped arriving on a live source (`true`) / resumed (`false`). */
  stall: boolean;
  /** {@link KerbcastCameraHandle.hasSource} changed. */
  source: boolean;
  /** {@link KerbcastCameraHandle.waitingForSlot} changed. */
  waiting: boolean;
}

/**
 * How a peer installs encoded transforms. `"script"` sets an
 * `RTCRtpScriptTransform` as the receiver's `transform`, used wherever the
 * browser has it; `"streams"` runs a main-thread `TransformStream` over the
 * receiver's `createEncodedStreams()`, used on Chrome builds without it.
 */
export type EncodedTransformPath = "script" | "streams";

/** A transform over encoded video frames: an `RTCRtpScriptTransform` on the `"script"` path, a `TransformStream` on `"streams"`. */
export type EncodedTransform =
  | RTCRtpScriptTransform
  | TransformStream<RTCEncodedVideoFrame, RTCEncodedVideoFrame>;

/** Passed to an {@link EncodedTransformFactory}. */
export interface EncodedTransformContext {
  flightId: number;
  /** Which kind of {@link EncodedTransform} to return. */
  path: EncodedTransformPath;
}

/** Options for {@link KerbcastCameraHandle.setEncodedTransform}. */
export interface EncodedTransformOptions {
  /**
   * The longest a frame can be held inside the transform, including any
   * pacing buffer, in ms, or a function returning it, called at each check so
   * the hold can change as it runs. Given, Kerbcast renews the transform on
   * the `"script"` path only when packets keep arriving with no frame decoded
   * for longer than the hold plus 2 s (doubling with each retry after the
   * first, at most three), since Chromium may have left the instance without
   * frames. An instance whose frames decode once packets have reached it for
   * the hold is never renewed, so its held frames survive. The longest hold
   * read (a function is read every 2 s while the transform is installed)
   * also bounds how long the instance may go on decoding what it held after
   * it is replaced on its receiver, relaunch included, which the check of the
   * one replacing it waits out before trusting or renewing it. Left out,
   * Kerbcast cannot tell a hold from a lost instance and renews each install
   * once when packets first arrive, restarting the hold, and does the same
   * for whatever replaces it on that receiver. A value that is not a finite
   * number of 0 or more, or a function that throws, counts as left out.
   */
  holdMs?: number | (() => number);
}

/**
 * Builds a transform instance for one receiver. An instance is single-use,
 * so Kerbcast asks for a new one whenever the camera moves receiver or its
 * stream starts over.
 */
export type EncodedTransformFactory = (ctx: EncodedTransformContext) => EncodedTransform;

/** A factory's instance on one receiver, as {@link KerbcastClient} tracks it. */
interface TransformInstall {
  flightId: number;
  factory: EncodedTransformFactory;
  /** The hold the host gave with the factory when it was installed. */
  holdMs: EncodedTransformOptions["holdMs"];
  /** The longest hold read for this install, which bounds what it can still be holding after a shorter one is given. */
  maxHoldMs: number;
}

/** A receiver's inbound-rtp counters, for one stats entry. */
interface ReceiverCounts {
  packets: number;
  frames: number;
}

/** A script-path receiver whose transform is watched once packets arrive on it, and renewed only while none decode. */
interface PendingHeal {
  /** The camera's install to renew from its factory, or null for the passthrough left when one was removed. */
  install: TransformInstall | null;
  /** The camera named when the renewal fails and nothing is bound to the receiver. */
  flightId: number;
  /** Orders this watch against stats requests, so it is sampled only from a report asked for after its transform was handed over. */
  seq: number;
  /** Each inbound-rtp stats entry's counters on the receiver at the latest sample, by stats id. */
  last: Map<string, ReceiverCounts> | null;
  /** When the latest sample was taken. */
  lastAt: number;
  /** Renewals made so far. */
  renewals: number;
  /** Since when packets have kept arriving with no frame decoded, through gaps no longer than {@link TRANSFORM_HEAL_IDLE_MS}. */
  stalledSince: number | null;
  /** The first poll since the watch started or last renewed that saw packets arrive. */
  packetsSince: number | null;
  /** The latest poll that saw packets arrive. */
  packetsAt: number | null;
  /** When the watch started or last renewed, which sets how fast it is polled. */
  since: number;
}

/** Configuration for {@link KerbcastClient} construction. */
export interface KerbcastClientConfig {
  /** Sidecar host (matches the sidecar's `--http-bind`). */
  host: string;
  /** Sidecar port. */
  port: number;
  /**
   * ICE servers to use. Defaults to Google's public STUN server,
   * which is enough for LAN streaming.
   */
  iceServers?: RTCIceServer[];
  /**
   * Default noise settings for all cameras. Individual cameras can override
   * this via `cam.configure({ noise: ... })`. Noise is enabled by default.
   */
  noise?: Partial<NoiseConfig>;
  /**
   * Keep cameras whose noise is disabled on the frame-locked canvas path,
   * with no static, instead of exposing the raw track. On Chrome this lets
   * `watchDisplayedFrames` identify every displayed frame exactly even where
   * the browser's frame callbacks carry no `rtpTimestamp`. Where the browser
   * has MediaStreamTrackProcessor and MediaStreamTrackGenerator, frames are
   * forwarded untouched with no canvas work; elsewhere it costs one canvas
   * composite per frame. Cameras with noise enabled are always frame-locked.
   * Default false.
   */
  frameLock?: boolean;
  /**
   * Enable {@link KerbcastCameraHandle.setEncodedTransform}. Every receiver
   * then carries a passthrough transform from creation, because Chrome only
   * honours a receiver transform that was in place before negotiation and
   * stops a receiver's frames when its transform is cleared. This routes
   * every camera's frames through a worker (or, on Chrome builds without
   * `RTCRtpScriptTransform`, a main-thread `createEncodedStreams` pipe), so
   * it is off by default. The worker is a `blob:` URL, so a page with a
   * Content Security Policy must allow `worker-src blob:`. Set it before
   * connecting; a peer keeps the path it was created with.
   */
  encodedTransforms?: boolean;
  /**
   * How `client.recording` times a clip. `"capture"` records the encoded
   * H.264 frames as received, without re-encoding, into an MP4 timed by each
   * frame's RTP timestamp, which follows when the game captured the frame:
   * a network stall no longer shows as a freeze and then a burst. It needs
   * the same per-receiver passthrough as {@link encodedTransforms} (a worker
   * on a `blob:` URL, or Chrome's legacy `createEncodedStreams` where that is
   * the only path), so it too is set before connecting and is off by
   * default. `"arrival"` (the default) records the decoded track with
   * `MediaRecorder`, timed by arrival. A clip falls back to arrival timing
   * when its frames cannot be copied; `RecordingHandle.timing` says which
   * it got.
   */
  recordingTiming?: RecordingTiming;
  /**
   * ICE gathering timeout in milliseconds. Passed to the default
   * {@link BrowserKerbcastTransport}; ignored when a custom transport is
   * provided (the transport owns gathering). Defaults to 2000 ms.
   *
   * On LAN topologies where the STUN server is unreachable (e.g. Steam Deck
   * IPv6 LAN with no internet path), gathering can stall for the full STUN
   * timeout. Host candidates alone are sufficient for LAN streaming, so this
   * timeout lets the connect flow proceed once host candidates are gathered.
   */
  iceGatheringTimeoutMs?: number;
  /**
   * Override how the SDP offer/answer is exchanged. Defaults to a POST to the
   * sidecar's HTTP `/offer`. A station screen injects a version that relays
   * the offer through the main screen (which can reach the sidecar), so the
   * station needs no direct sidecar address. The media itself still flows
   * peer-to-sidecar (direct or via TURN) -- only the handshake is brokered.
   */
  negotiate?: (offer: {
    sdp: string;
    cameras: number[];
    slots?: number;
  }) => Promise<{ sdp: string; cameras: number[]; session?: string }>;
  /**
   * Override how a re-offer that grows the slot pool is exchanged. Defaults
   * to a POST to the sidecar's HTTP `/renegotiate`. `session` comes from the
   * `/offer` answer, so a relay that brokers {@link negotiate} must pass it
   * through. A client given `negotiate` but not this one cannot grow its
   * pool: a camera that finds every slot taken reports
   * {@link KerbcastCameraHandle.waitingForSlot} instead.
   */
  renegotiate?: (offer: {
    session: string;
    sdp: string;
    slots: number;
  }) => Promise<{ sdp: string }>;
  /**
   * Options for the lazily-built {@link RecordingController} (`client.recording`).
   * The load-bearing use is `loadTrimmer`: a consumer that can't rely on the
   * default bare `import("mediabunny")` resolving (the sidecar's embedded web
   * page serves its own copy of the package locally, offline/LAN, no CDN)
   * supplies its own loader here. Bundler consumers can omit
   * this and get the default.
   */
  recording?: RecordingControllerOptions;
}

/** WebRTC connection state surface. */
export type KerbcastConnectionState =
  | "disconnected"
  | "connecting"
  | "connected"
  | "failed";

/**
 * Inbound RTP video statistics for one camera track, derived from the
 * WebRTC stats report. Fields are `undefined` when the browser has not yet
 * emitted that stat (e.g. `framesPerSecond` before the first full second).
 * `packetsReceived` and `bytesReceived` default to 0 in the same case.
 *
 * Retrieve per-camera stats via {@link KerbcastClient.inboundVideoStats}.
 */
export interface InboundVideoStats {
  packetsReceived: number;
  bytesReceived: number;
  framesReceived: number | undefined;
  framesDecoded: number | undefined;
  jitter: number | undefined;
  framesPerSecond: number | undefined;
}

/**
 * Camera summary returned by {@link KerbcastClient.discover} (`GET /cameras`).
 *
 * This is the shape the sidecar serialises from its internal `CameraInfo`
 * struct. It is a discovery snapshot, not a live operating state: it carries
 * capability and identity fields but not per-connection operator state
 * (`layers`, `renderWidth`, `panYaw`, etc.) -- those arrive over the data
 * channel after `connect()`.
 *
 * Deliberately separate from `CameraState` even though the two share several
 * field names, because the shapes differ: `CameraState` adds runtime fields
 * (`layers`, `renderWidth`, `panYaw`, ...).
 */
export interface DiscoveredCamera {
  flightId: number;
  /** Part-destruction lifecycle. `"active"` for live cameras. */
  lifecycle?: CameraLifecycle;
  partName: string;
  partTitle: string;
  cameraName: string;
  vesselName: string;
  /** Physical sensor width ceiling (pixels). Distinct from `renderWidth` on
   *  `CameraState`, which is the currently-negotiated resolution. */
  maxWidth: number;
  /** Physical sensor height ceiling (pixels). */
  maxHeight: number;
  supportsZoom: boolean;
  fov: number;
  fovMin: number;
  fovMax: number;
  supportsPan: boolean;
  panYawMin: number;
  panYawMax: number;
  panPitchMin: number;
  panPitchMax: number;
  /** Current encoder bitrate in bits per second. 0 when no encoder is running. */
  encoderBitrateBps: number;
  /** REMB-derived bandwidth target. 0 until the first feedback arrives. */
  targetBitrateBps: number;
  /** Effective degrade level (max across active subscribers). 0.0 to 1.0. */
  degradeLevel: number;
  /*
   * Live rate fields, absent from sidecars that predate them. Poll
   * discover() for current values; the data-channel CameraState carries the
   * same fields as of its last push.
   */
  /** Rate Kerbcast paces this camera's capture to: the primary rate while
   *  displayed, the background rate while undisplayed, 0 when not capturing. */
  targetCaptureFps?: number;
  /** Any consumer reports a non-zero display size, or the feed is forced. */
  displayed?: boolean;
  /** Frames the plugin produced per second over roughly the last 2 s. */
  captureFps?: number;
  /** Encoded frames handed to this camera's tracks per second, same window. */
  sentFps?: number;
  /** Keyframes the encoder emitted since the camera's stream started. */
  keyframesSent?: number;
  /** PLI/FIR keyframe requests received across peers since the stream started. */
  keyframeRequestsReceived?: number;
  /** Largest display size held across consumers; null when none is held. */
  displaySize?: DisplaySizeReport | null;
}

/**
 * How long a command sent between `connect` resolving and the control channel
 * opening waits for the channel before it rejects.
 */
export const CONTROL_OPEN_TIMEOUT_MS = 10_000;

interface PendingSend {
  payload: string;
  resolve: () => void;
  reject: (err: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

/**
 * {@link KerbcastClient.setCommandAim} refused because a command authority in
 * game holds the lock on command camera aims.
 */
export class CommandLockedError extends Error {
  readonly code = ErrorCode.CommandLocked;

  constructor(readonly owner: string) {
    super(`[kerbcast] ${owner} holds the command camera lock; aim refused`);
    this.name = "CommandLockedError";
  }
}

/**
 * {@link KerbcastClient.setCommandAim} refused because the aim's craft (a
 * `vessel` aim's craft, or an `orbit` or `positionAt` aim's subject) is not in
 * the camera's `commandCentre.targets`, for example one merged away in a dock.
 */
export class CommandTargetUnknownError extends Error {
  readonly code = ErrorCode.CommandTargetUnknown;

  constructor(
    readonly flightId: number,
    readonly persistentId: number,
  ) {
    super(`[kerbcast] craft ${persistentId} is not among camera ${flightId}'s targets; aim refused`);
    this.name = "CommandTargetUnknownError";
  }
}

/**
 * The sidecar refused `POST /offer` with a reason, for example
 * `ErrorCode.CodecUnsupported` when the browser offers no H.264 video.
 */
export class OfferRefusedError extends Error {
  constructor(
    readonly status: number,
    readonly reason: string,
    readonly code?: ErrorCode,
  ) {
    super(`[kerbcast] connection refused: ${reason}`);
    this.name = "OfferRefusedError";
  }
}

/** The craft an aim names: a `vessel` aim's craft, or an `orbit` or `positionAt` aim's subject. */
function aimCraft(aim: CommandAim): number | undefined {
  const pid =
    aim.kind === CommandAimKind.Vessel
      ? aim.persistentId
      : aim.kind === CommandAimKind.Orbit || aim.kind === CommandAimKind.PositionAt
        ? aim.subjectPersistentId
        : undefined;
  return pid ? pid : undefined;
}

/** How long {@link KerbcastClient.createCommandCamera} waits for the camera by default. */
export const COMMAND_CAMERA_CREATE_TIMEOUT_MS = 35_000;

/**
 * How long {@link KerbcastClient.createCommandCamera} waits, once the camera
 * has appeared, for the game's first report of its target and target list.
 */
export const COMMAND_CAMERA_STATE_WAIT_MS = 5_000;

/** Options for {@link KerbcastClient.createCommandCamera}. */
export interface CreateCommandCameraOptions {
  /** Feed name; omit for the game's default for the vantage. */
  name?: string;
  /**
   * Where it points; omit for the default (the active craft in flight). Not
   * applied while a command authority in game holds the lock.
   */
  aim?: CommandAim;
  /**
   * Identifies the request; the new camera carries it as
   * `commandCentre.token`. Omit for a random one. Reusing a token within a
   * scene creates nothing.
   */
  token?: string;
  /**
   * Milliseconds to wait for the camera to appear before rejecting. The
   * game retries a launch site, station or craft that is not placed yet for
   * up to 30 s. Default {@link COMMAND_CAMERA_CREATE_TIMEOUT_MS}.
   */
  timeoutMs?: number;
}

function randomToken(): string {
  const c = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
  if (c?.randomUUID) return c.randomUUID();
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

/** Event payloads emitted by a {@link KerbcastClient}. */
export interface KerbcastClientEvents {
  "state-change": KerbcastConnectionState;
  "cameras-change": CameraState[];
  "adaptive-shed": AdaptiveShedPayload;
  error: ErrorPayload;
  /**
   * Fired whenever a `ping` keepalive arrives from the sidecar.
   * Consumers can use this to reset their own staleness watchdogs
   * without intercepting at the transport layer.
   */
  ping: undefined;
  /**
   * Fired when a `settings-state` message arrives (after `Hello` and
   * whenever the plugin-reported throttle state changes). Payload
   * reflects what the plugin has applied, not just what was requested.
   */
  "settings-change": SettingsStatePayload;
  /**
   * Fired when a `scene-state-changed` message arrives. `true` in a
   * flight scene, `false` otherwise, `undefined` before the first
   * signal. Consumers use this to show a calm out-of-flight standby
   * instead of per-camera SIGNAL LOST.
   */
  "scene-change": boolean | undefined;
  /**
   * Fired each time the control channel of a freshly (re)connected peer
   * opens, i.e. the earliest moment it can carry messages. Consumers that
   * hold per-peer state on the sidecar (display sizes, subscriptions) re-send
   * it here, because the sidecar forgets a peer's state when it leaves.
   */
  "control-open": undefined;
  /**
   * Fired when a message arrives after the sidecar was silent for
   * {@link CONTACT_GAP_MS} or more, as when the page was paused or asleep
   * and is handed the messages held back meanwhile.
   */
  "contact-gap": ContactGap;
}

/** A silence from the sidecar: when it was last heard from and when it next was, both as `Date.now()`. */
export interface ContactGap {
  from: number;
  to: number;
}

/** Two of the sidecar's 5 s pings missed: shorter silences are not reported as a {@link ContactGap}. */
export const CONTACT_GAP_MS = 10_000;

// ---------------------------------------------------------------------------
// Transport abstraction for tests
// ---------------------------------------------------------------------------

/**
 * Pluggable WebRTC transport. Production code uses the default
 * (browser `RTCPeerConnection`). Tests substitute an in-memory
 * implementation to exercise the state machine without a real peer.
 */
export interface KerbcastTransport {
  createPeer(iceServers: RTCIceServer[], opts?: KerbcastPeerOptions): KerbcastPeer;
  /**
   * The encoded-transform path a peer created with `opts` would use, or null
   * when it has none. Optional; a transport without it offers no path.
   */
  encodedTransformPath?(opts: KerbcastPeerOptions): EncodedTransformPath | null;
}

/** Peer creation options passed to {@link KerbcastTransport.createPeer}. */
export interface KerbcastPeerOptions {
  /** Mirrors {@link KerbcastClientConfig.encodedTransforms}. */
  encodedTransforms?: boolean;
  /** Set when {@link KerbcastClientConfig.recordingTiming} is `"capture"`: receivers carry a passthrough that can copy frames. */
  frameTaps?: boolean;
}

export interface KerbcastPeer {
  addRecvOnlyTransceiver(): void;
  createDataChannel(label: string): KerbcastDataChannel;
  onTrack(
    handler: (track: MediaStreamTrack, idx: number, mid: string) => void,
  ): void;
  onStateChange(handler: (state: KerbcastConnectionState) => void): void;
  createOffer(): Promise<string>;
  setLocalDescription(sdp: string): Promise<void>;
  setRemoteAnswer(sdp: string): Promise<void>;
  /**
   * Abandon a local offer whose answer never arrived, returning to the last
   * agreed description. Optional so older transports keep compiling; without
   * it a failed slot re-offer is left pending.
   */
  rollback?(): Promise<void>;
  waitForIceComplete(): Promise<void>;
  localSdp(): string;
  close(): void;
  /**
   * Retrieve the raw WebRTC stats report. Optional so existing third-party
   * transports and mocks that predate this method keep compiling without
   * changes. Equivalent to `RTCPeerConnection.getStats(null)`.
   */
  getStats?(): Promise<RTCStatsReport>;
  /**
   * Install `transform` on the receiver of slot `mid`, replacing any
   * previous one; `null` restores passthrough. Throws when the receiver is
   * unknown or the transform's kind does not match the peer's path.
   */
  setReceiverTransform?(mid: string, transform: EncodedTransform | null): void;
  /**
   * Copy each encoded frame the receiver of slot `mid` gets to `onFrame`
   * (`null` stops). Only on a peer created with `frameTaps`; a script-path
   * receiver running a custom transform copies nothing until passthrough
   * is restored.
   */
  setReceiverTap?(mid: string, onFrame: ((frame: EncodedFrameCopy) => void) | null): void;
}

export interface KerbcastDataChannel {
  send(payload: string): void;
  onOpen(handler: () => void): void;
  onMessage(handler: (raw: string) => void): void;
  onClose(handler: () => void): void;
}

// ---------------------------------------------------------------------------
// Default transport: browser RTCPeerConnection
// ---------------------------------------------------------------------------

/** Options for {@link BrowserKerbcastTransport}. */
export interface BrowserKerbcastTransportOptions {
  /**
   * Maximum time (ms) to wait for ICE gathering to complete before proceeding
   * with whatever candidates have been gathered. Defaults to 2000 ms.
   *
   * On LAN paths where the STUN server is unreachable, gathering blocks until
   * the STUN timeout elapses. Host candidates are all a local LAN stream
   * needs, so this timeout lets connect proceed without waiting for STUN.
   */
  iceGatheringTimeoutMs?: number;
}

export class BrowserKerbcastTransport implements KerbcastTransport {
  private readonly opts: Required<BrowserKerbcastTransportOptions>;

  constructor(opts: BrowserKerbcastTransportOptions = {}) {
    this.opts = { iceGatheringTimeoutMs: opts.iceGatheringTimeoutMs ?? 2000 };
  }

  encodedTransformPath(opts: KerbcastPeerOptions): EncodedTransformPath | null {
    return opts.encodedTransforms === true || opts.frameTaps === true
      ? detectEncodedTransformPath()
      : null;
  }

  createPeer(iceServers: RTCIceServer[], opts: KerbcastPeerOptions = {}): KerbcastPeer {
    const transformPath = this.encodedTransformPath(opts);
    /* max-bundle: every slot shares one ICE transport, so a relayed
       connection takes one TURN allocation however many slots it has. The
       sidecar always answers with a single BUNDLE group. */
    const config: RTCConfiguration & { encodedInsertableStreams?: boolean } = {
      iceServers,
      bundlePolicy: "max-bundle",
    };
    if (transformPath === "streams") config.encodedInsertableStreams = true;
    const pc = new RTCPeerConnection(config);
    const { iceGatheringTimeoutMs } = this.opts;
    /* Streams path: every receiver's frames must be taken exactly once, so
       each gets a pipe that runs passthrough until a transform is set.
       Script path: each receiver holds a passthrough transform from
       creation, and swaps back to a fresh one instead of clearing. */
    let passthroughWorker: { worker: Worker; url: string } | null = null;
    const taps = new Map<RTCRtpTransceiver, (frame: EncodedFrameCopy) => void>();
    /* Script path with frame taps: each receiver's passthrough reports to the page over its own port. */
    const tapPorts = new Map<RTCRtpTransceiver, MessagePort>();
    const passthrough = (t: RTCRtpTransceiver): RTCRtpScriptTransform => {
      if (!passthroughWorker) {
        const url = URL.createObjectURL(
          new Blob([PASSTHROUGH_WORKER_SOURCE], { type: "text/javascript" }),
        );
        passthroughWorker = { worker: new Worker(url), url };
      }
      if (opts.frameTaps !== true) return new RTCRtpScriptTransform(passthroughWorker.worker);
      tapPorts.get(t)?.close();
      const channel = new MessageChannel();
      channel.port1.onmessage = (ev: MessageEvent<Omit<EncodedFrameCopy, "receivedAt">>) => {
        taps.get(t)?.({ ...ev.data, receivedAt: monotonicNowMs() });
      };
      channel.port1.postMessage(taps.has(t));
      tapPorts.set(t, channel.port1);
      return new RTCRtpScriptTransform(
        passthroughWorker.worker,
        { port: channel.port2 },
        [channel.port2],
      );
    };
    const pipes = new Map<RTCRtpTransceiver, SwitchableFramePipe>();
    const pipeFor = (t: RTCRtpTransceiver): SwitchableFramePipe => {
      let pipe = pipes.get(t);
      if (!pipe) {
        pipe = new SwitchableFramePipe((t.receiver as ReceiverWithEncodedStreams).createEncodedStreams());
        pipes.set(t, pipe);
      }
      return pipe;
    };
    let trackIdx = 0;
    let onTrack:
      | ((t: MediaStreamTrack, idx: number, mid: string) => void)
      | null = null;
    let onStateChange: ((s: KerbcastConnectionState) => void) | null = null;

    pc.ontrack = (ev) => {
      if (transformPath === "streams") pipeFor(ev.transceiver);
      // ev.transceiver.mid is the stable m-line id the sidecar keys its
      // SlotMap on; it's set by the time ontrack fires (post-answer).
      onTrack?.(ev.track, trackIdx++, ev.transceiver.mid ?? "");
    };
    pc.onconnectionstatechange = () => {
      onStateChange?.(mapPeerState(pc.connectionState));
    };

    return {
      addRecvOnlyTransceiver: () => {
        const t = pc.addTransceiver("video", { direction: "recvonly" });
        if (transformPath === "script") t.receiver.transform = passthrough(t);
      },
      createDataChannel: (label) =>
        wrapBrowserDataChannel(pc.createDataChannel(label)),
      onTrack: (h) => {
        onTrack = h;
      },
      onStateChange: (h) => {
        onStateChange = h;
      },
      createOffer: async () => {
        const offer = await pc.createOffer();
        return offer.sdp ?? "";
      },
      setLocalDescription: async (sdp) => {
        await pc.setLocalDescription({ type: "offer", sdp });
      },
      setRemoteAnswer: async (sdp) => {
        await pc.setRemoteDescription({ type: "answer", sdp });
      },
      rollback: async () => {
        if (pc.signalingState === "have-local-offer") {
          await pc.setLocalDescription({ type: "rollback" });
        }
      },
      waitForIceComplete: () =>
        new Promise<void>((resolve) => {
          if (pc.iceGatheringState === "complete") {
            resolve();
            return;
          }
          let timer: ReturnType<typeof setTimeout> | null = null;
          const cleanup = () => {
            pc.removeEventListener("icegatheringstatechange", check);
            if (timer !== null) {
              clearTimeout(timer);
              timer = null;
            }
          };
          const check = () => {
            if (pc.iceGatheringState === "complete") {
              cleanup();
              resolve();
            }
          };
          timer = setTimeout(() => {
            timer = null;
            cleanup();
            resolve();
          }, iceGatheringTimeoutMs);
          pc.addEventListener("icegatheringstatechange", check);
        }),
      localSdp: () => pc.localDescription?.sdp ?? "",
      getStats: () => pc.getStats(null),
      setReceiverTransform: (mid, transform) => {
        const transceiver = pc.getTransceivers().find((t) => t.mid === mid);
        if (!transceiver) throw new Error(`[kerbcast] no receiver for slot ${mid}`);
        if (transformPath === "streams") {
          if (transform !== null && !(transform instanceof TransformStream)) {
            throw new TypeError("[kerbcast] the streams path needs a TransformStream");
          }
          pipeFor(transceiver).set(transform);
        } else if (transformPath === "script") {
          if (transform !== null && !(transform instanceof RTCRtpScriptTransform)) {
            throw new TypeError("[kerbcast] the script path needs an RTCRtpScriptTransform");
          }
          if (transform) {
            tapPorts.get(transceiver)?.close();
            tapPorts.delete(transceiver);
          }
          transceiver.receiver.transform = transform ?? passthrough(transceiver);
        } else {
          throw new Error("[kerbcast] encoded transforms are unavailable on this peer");
        }
      },
      setReceiverTap: (mid, onFrame) => {
        if (opts.frameTaps !== true || !transformPath) {
          throw new Error("[kerbcast] frame taps are unavailable on this peer");
        }
        const transceiver = pc.getTransceivers().find((t) => t.mid === mid);
        if (!transceiver) throw new Error(`[kerbcast] no receiver for slot ${mid}`);
        if (onFrame) taps.set(transceiver, onFrame);
        else taps.delete(transceiver);
        if (transformPath === "streams") pipeFor(transceiver).tap = onFrame;
        else tapPorts.get(transceiver)?.postMessage(onFrame !== null);
      },
      close: () => {
        for (const port of tapPorts.values()) port.close();
        tapPorts.clear();
        taps.clear();
        pc.close();
        if (passthroughWorker) {
          passthroughWorker.worker.terminate();
          URL.revokeObjectURL(passthroughWorker.url);
          passthroughWorker = null;
        }
      },
    };
  }
}

/*
 * Forwards every frame untouched. A receiver given a port (frame taps on)
 * also posts a copy of each frame to it while the page has switched the
 * copy on. The original goes on to the decoder either way: a failed copy
 * loses only the copy, never the live picture.
 */
const PASSTHROUGH_WORKER_SOURCE = `onrtctransform = (e) => {
  const { readable, writable, options } = e.transformer;
  const port = options && options.port;
  if (!port) {
    readable.pipeTo(writable).catch(() => {});
    return;
  }
  let tap = false;
  port.onmessage = (m) => { tap = m.data === true; };
  readable.pipeThrough(new TransformStream({
    transform(frame, out) {
      if (tap) {
        try {
          const meta = frame.getMetadata();
          const data = frame.data.slice(0);
          port.postMessage({
            data,
            rtpTimestamp: meta.rtpTimestamp ?? frame.timestamp,
            ssrc: meta.synchronizationSource,
          }, [data]);
        } catch {}
      }
      out.enqueue(frame);
    },
  })).pipeTo(writable).catch(() => {});
};`;

interface EncodedVideoStreams {
  readable: ReadableStream<RTCEncodedVideoFrame>;
  writable: WritableStream<RTCEncodedVideoFrame>;
}

/* Chrome's legacy insertable streams API, absent from lib.dom. */
type ReceiverWithEncodedStreams = RTCRtpReceiver & {
  createEncodedStreams(): EncodedVideoStreams;
};

/* Metadata fields not every lib.dom version declares. */
type EncodedFrameWithTimestamp = RTCEncodedVideoFrame & { timestamp?: number };
type EncodedMetadataWithRtp = RTCEncodedVideoFrameMetadata & { rtpTimestamp?: number };

/**
 * Whether the sidecar started a camera over on a new ring since `prev`, which
 * an in-place reattach shows in nothing but `attach`. A missing or zero value
 * on either side (a sidecar predating the field, or a camera not seen before)
 * is never a change.
 */
function attachChanged(prev: CameraState | null, next: CameraState): boolean {
  return !!prev?.attach && !!next.attach && prev.attach !== next.attach;
}

function copyEncodedFrame(frame: RTCEncodedVideoFrame): EncodedFrameCopy {
  const meta = frame.getMetadata() as EncodedMetadataWithRtp;
  return {
    data: frame.data.slice(0),
    rtpTimestamp: meta.rtpTimestamp ?? (frame as EncodedFrameWithTimestamp).timestamp ?? 0,
    ssrc: meta.synchronizationSource,
    receivedAt: monotonicNowMs(),
  };
}

function detectEncodedTransformPath(): EncodedTransformPath | null {
  if (typeof RTCRtpReceiver === "undefined") return null;
  const proto = RTCRtpReceiver.prototype as Partial<ReceiverWithEncodedStreams>;
  if (typeof RTCRtpScriptTransform !== "undefined" && "transform" in proto) return "script";
  if (typeof proto.createEncodedStreams === "function") return "streams";
  return null;
}

/**
 * Forwards one receiver's encoded frames to its decoder, through a
 * TransformStream while one is set. Swapping aborts the old transform, so
 * frames it still holds are dropped.
 */
export class SwitchableFramePipe {
  private readonly out: WritableStreamDefaultWriter<RTCEncodedVideoFrame>;
  private stage: WritableStreamDefaultWriter<RTCEncodedVideoFrame> | null = null;
  /** Gets a copy of each received frame, before any transform, while set. */
  tap: ((frame: EncodedFrameCopy) => void) | null = null;

  constructor(streams: EncodedVideoStreams) {
    this.out = streams.writable.getWriter();
    void this.pump(streams.readable.getReader());
  }

  set(transform: TransformStream<RTCEncodedVideoFrame, RTCEncodedVideoFrame> | null): void {
    const old = this.stage;
    this.stage = null;
    old?.abort().catch(() => {});
    if (!transform) return;
    const stage = transform.writable.getWriter();
    this.stage = stage;
    void this.drain(transform.readable.getReader(), stage);
  }

  private async pump(reader: ReadableStreamDefaultReader<RTCEncodedVideoFrame>): Promise<void> {
    try {
      for (;;) {
        const { value, done } = await reader.read();
        if (done) return;
        /* Copied before the write, which hands the frame's data on; delivered
           after it and off this loop, so a failing tap never stalls the
           decoder (an exception here would read as the receiver closing). */
        const tap = this.tap;
        let copy: EncodedFrameCopy | null = null;
        if (tap) {
          try {
            copy = copyEncodedFrame(value);
          } catch {
            /* lose this copy, keep the picture */
          }
        }
        const stage = this.stage;
        if (stage) await stage.write(value).catch(() => {});
        else await this.out.write(value);
        if (tap && copy) {
          const frame = copy;
          queueMicrotask(() => {
            try {
              tap(frame);
            } catch (err) {
              console.error("kerbcast: encoded frame tap failed", err);
            }
          });
        }
      }
    } catch {
      /* The receiver closed with its peer. */
    }
  }

  private async drain(
    reader: ReadableStreamDefaultReader<RTCEncodedVideoFrame>,
    stage: WritableStreamDefaultWriter<RTCEncodedVideoFrame>,
  ): Promise<void> {
    try {
      for (;;) {
        const { value, done } = await reader.read();
        if (done || this.stage !== stage) return;
        await this.out.write(value);
      }
    } catch {
      /* Aborted by a swap, or the receiver closed. */
    }
  }
}

function wrapBrowserDataChannel(dc: RTCDataChannel): KerbcastDataChannel {
  let onOpen: (() => void) | null = null;
  let onMessage: ((raw: string) => void) | null = null;
  let onClose: (() => void) | null = null;
  dc.onopen = () => onOpen?.();
  dc.onclose = () => onClose?.();
  dc.onmessage = (ev) => {
    if (typeof ev.data === "string") onMessage?.(ev.data);
  };
  return {
    send: (s) => {
      dc.send(s);
    },
    onOpen: (h) => {
      onOpen = h;
    },
    onMessage: (h) => {
      onMessage = h;
    },
    onClose: (h) => {
      onClose = h;
    },
  };
}

function mapPeerState(state: RTCPeerConnectionState): KerbcastConnectionState {
  switch (state) {
    case "new":
    case "connecting":
      return "connecting";
    case "connected":
      return "connected";
    case "failed":
      return "failed";
    case "disconnected":
    case "closed":
      return "disconnected";
  }
}

// ---------------------------------------------------------------------------
// Tiny typed event emitter
// ---------------------------------------------------------------------------

type Listener<T> = (data: T) => void;

/**
 * A random id for {@link KerbcastClient}'s resume name. `randomUUID` only
 * exists in a secure context, and the page is usually served over plain
 * http from the game machine's LAN address.
 */
function mintClientId(): string {
  const crypto = (globalThis as { crypto?: Crypto }).crypto;
  if (typeof crypto?.randomUUID === "function") return crypto.randomUUID();
  if (typeof crypto?.getRandomValues === "function") {
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  }
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

// No bound on E so consumers can use `interface` declarations
// (interfaces don't carry an implicit string index signature, so
// `extends Record<string, unknown>` would reject them).
class TypedEmitter<E> {
  private readonly listeners = new Map<keyof E, Set<Listener<unknown>>>();

  on<K extends keyof E>(event: K, handler: (data: E[K]) => void): () => void {
    let set = this.listeners.get(event);
    if (!set) {
      set = new Set();
      this.listeners.set(event, set);
    }
    set.add(handler as Listener<unknown>);
    return () => {
      set?.delete(handler as Listener<unknown>);
    };
  }

  protected emit<K extends keyof E>(event: K, data: E[K]): void {
    const set = this.listeners.get(event);
    set?.forEach((h) => (h as Listener<E[K]>)(data));
  }
}

// ---------------------------------------------------------------------------
// Camera handle implementation
// ---------------------------------------------------------------------------

class CameraHandle
  extends TypedEmitter<KerbcastCameraEvents>
  implements KerbcastCameraHandle
{
  readonly flightId: number;
  private _state: CameraState | null = null;
  private _mediaStream: MediaStream | null = null;
  private _rawStream: MediaStream | null = null;
  private _noisePipeline: NoisePipeline | null = null;
  private _noiseOverride: Partial<NoiseConfig> | null = null;
  /**
   * True while the handle has no live source but is still showing static
   * (signal-loss / camera-switch gap). Distinct from a true teardown, where
   * the pipeline is destroyed and `_mediaStream` goes null. While sourceless,
   * intensity is pinned at full and degrade updates must not lower it.
   */
  private _sourceless = false;
  /** Stall state mirrored from the pipeline's onStallChange callback. */
  private _stalled = false;
  private _hasSource = false;
  private _waitingForSlot = false;
  /** The raw stream {@link _watchSource} last subscribed to, so a re-apply of it adds no listener. */
  private _watchedStream: MediaStream | null = null;
  /** Interval that re-requests keyframes while stalled; null when not stalled. */
  private _keyframeRetry: ReturnType<typeof setInterval> | null = null;
  /** Whether animated static is drawn; survives pipeline rebuilds. */
  private _showStatic = true;
  /** True while the pipeline runs as a static-free frame-lock passthrough (noise off, `frameLock` on). */
  private _clear = false;
  /** Set by {@link setEncodedTransform}; the client installs it on whichever receiver carries this camera. */
  _encodedTransform: EncodedTransformFactory | null = null;
  /** Set by {@link setEncodedTransform} alongside {@link _encodedTransform}. */
  _encodedTransformHoldMs: EncodedTransformOptions["holdMs"];
  /** Listeners from {@link tapEncodedFrames}; the client taps whichever receiver carries this camera while any are set. */
  readonly _frameTaps = new Set<(frame: EncodedFrameCopy) => void>();
  private readonly client: KerbcastClient;

  constructor(flightId: number, client: KerbcastClient) {
    super();
    this.flightId = flightId;
    this.client = client;
  }

  get state(): CameraState | null {
    return this._state;
  }

  get mediaStream(): MediaStream | null {
    return this._mediaStream;
  }

  get stalled(): boolean {
    return this._stalled;
  }

  get hasSource(): boolean {
    return this._hasSource;
  }

  get waitingForSlot(): boolean {
    return this._waitingForSlot;
  }

  /** Internal: the client found no slot for this camera, or found one. */
  _setWaitingForSlot(waiting: boolean): void {
    if (waiting === this._waitingForSlot) return;
    this._waitingForSlot = waiting;
    this.emit("waiting", waiting);
  }

  get showStatic(): boolean {
    return this._showStatic;
  }

  get maxRenderSize(): { width: number; height: number } | null {
    const state = this._state;
    /* 0 or absent counts as unknown: a sidecar that predates the fields, or
       a state sent before the camera's ring attached. */
    if (state?.maxWidth && state.maxHeight) {
      return { width: state.maxWidth, height: state.maxHeight };
    }
    return this.client._maxRenderSize(this.flightId);
  }

  configure(options: { noise?: Partial<NoiseConfig> }): void {
    this._noiseOverride = options.noise ?? null;
    // Re-evaluate with the new noise setting: rebuild from whatever source
    // state we're currently in (live, sourceless, or torn down).
    this._applySource(this._rawStream);
  }

  setShowStatic(enabled: boolean): void {
    this._showStatic = enabled;
    if (!this._clear) this._noisePipeline?.setShowStatic(enabled);
  }

  setEncodedTransform(factory: EncodedTransformFactory | null, opts?: EncodedTransformOptions): void {
    if (factory) this.client._assertEncodedTransforms();
    if (factory && !this._encodedTransform && this._frameTaps.size > 0) {
      this.client._warnTapsDisplaced(this.flightId);
    }
    this._encodedTransform = factory;
    this._encodedTransformHoldMs = factory ? opts?.holdMs : undefined;
    this.client._syncEncodedTransforms();
  }

  canTapEncodedFrames(): boolean {
    return this.client._frameTapAvailable(this.flightId);
  }

  tapEncodedFrames(onFrame: (frame: EncodedFrameCopy) => void): (() => void) | null {
    if (!this.client._frameTapAvailable(this.flightId)) return null;
    this._frameTaps.add(onFrame);
    this.client._syncEncodedTransforms();
    return () => {
      if (!this._frameTaps.delete(onFrame)) return;
      this.client._syncEncodedTransforms();
    };
  }

  /** Internal: hands a copied frame to every tap. */
  _deliverFrame(frame: EncodedFrameCopy): void {
    for (const tap of this._frameTaps) {
      try {
        tap(frame);
      } catch (err) {
        console.error(`kerbcast: encoded frame tap for camera ${this.flightId} failed`, err);
      }
    }
  }

  /**
   * Internal — called by the client when CameraState pushes arrive. Returns
   * `true` when this update resurrected the camera (Destroyed -> Active), so
   * the client can re-source the handle from its retained track.
   */
  _setState(state: CameraState): boolean {
    const prevDestroyed = this._state?.lifecycle === CameraLifecycle.Destroyed;
    this._state = state;
    this.emit("change", state);

    // A destroyed camera stops forwarding frames but its track never ends, so
    // nothing else drives the handle into the sourceless state — do it here.
    if (state.lifecycle === CameraLifecycle.Destroyed) {
      if (!this._sourceless || !prevDestroyed) this._applySource(null);
      return false;
    }

    // Live feed: degrade drives intensity. Don't touch a sourceless pipeline,
    // which is pinned at full static.
    if (!this._sourceless && !this._clear) {
      this._noisePipeline?.setIntensity(this._liveIntensity());
    }

    // Resurrection: KSP reused this part.flightID after a revert, so the
    // sidecar tombstoned then re-attached the SAME camera and rebound the SAME
    // track with no new slot-map. The handle went sourceless on destroy and
    // only re-sources on a slot-map, so signal the client to re-source it from
    // the track it still holds.
    return prevDestroyed;
  }

  /**
   * Internal — called by the client when a track arrives or drops.
   * A null stream does NOT tear down: if a pipeline can run, the handle keeps
   * emitting live full-intensity static (signal-loss / camera-switch gap).
   * Use {@link _teardown} for genuine teardown (disconnect / peer failure).
   */
  _setMediaStream(stream: MediaStream | null): void {
    this._applySource(stream);
  }

  /** Internal: whether the handle's source is a stream carrying `track`, still live. */
  _isSourcedFrom(track: MediaStreamTrack): boolean {
    return (
      track.readyState !== "ended" &&
      (this._rawStream?.getTracks().includes(track) ?? false)
    );
  }

  /**
   * Drive the persistent pipeline to a given source (or sourceless static when
   * `raw` is null). Creates the pipeline lazily on first need and reuses it
   * thereafter so the output stream stays stable across source swaps.
   */
  private _applySource(raw: MediaStream | null): void {
    this._rawStream = raw;
    this._sourceless = raw === null;
    this._watchSource(raw);

    const noiseEnabled = this.client._resolveNoise(this._noiseOverride);
    this._clear = !noiseEnabled && this.client._frameLock();

    if (noiseEnabled || this._clear) {
      // Sourceless feeds show static in flight, but go blank (black, no
      // noise) out of flight so a whole-scene unload is calm.
      const showStatic = this._clear ? false : raw ? this._showStatic : this._sourcelessShowStatic();
      const intensity = this._clear ? 0 : raw ? this._liveIntensity() : SOURCELESS_INTENSITY;
      // Reuse an existing pipeline; otherwise try to create one.
      if (!this._noisePipeline) {
        this._noisePipeline = tryCreateNoisePipeline(raw, intensity, {
          showStatic,
          passthrough: this._clear,
          onStallChange: (stalled) => this._setStalled(stalled),
        });
      } else {
        this._noisePipeline.setPassthrough(this._clear);
        this._noisePipeline.setSource(raw);
        this._noisePipeline.setShowStatic(showStatic);
      }

      const pipeline = this._noisePipeline;
      if (pipeline) {
        pipeline.setIntensity(intensity);
        this._setOutput(pipeline.processedStream);
        return;
      }
    } else if (this._noisePipeline) {
      // Noise just got disabled — drop the pipeline and fall through to raw.
      this._noisePipeline.destroy();
      this._noisePipeline = null;
      this._setStalled(false);
    }

    // Noise disabled, or captureStream unavailable (no pipeline creatable):
    // expose the raw stream directly, or null when there's no source.
    this._setOutput(raw);
  }

  /**
   * Whether a sourceless (destroyed/absent) feed should render static. True
   * in flight (or before the scene signal), false out of flight so the feed
   * goes blank rather than a wall of red static when the whole scene unloads.
   */
  private _sourcelessShowStatic(): boolean {
    return this._showStatic && this.client.inFlight !== false;
  }

  /**
   * Internal — called by the client when the scene flag flips. Re-drives an
   * existing sourceless pipeline so it switches between static and blank.
   */
  _refreshSceneState(): void {
    if (this._sourceless && !this._clear) {
      this._noisePipeline?.setShowStatic(this._sourcelessShowStatic());
    }
  }

  /** Follows whether `raw` still carries a live track, for {@link hasSource}. */
  private _watchSource(raw: MediaStream | null): void {
    if (raw !== null && raw === this._watchedStream) return;
    this._watchedStream = raw;
    const track = raw?.getVideoTracks?.()[0];
    this._setHasSource(raw !== null && track?.readyState !== "ended");
    track?.addEventListener?.(
      "ended",
      () => {
        if (this._rawStream === raw) this._setHasSource(false);
      },
      { once: true },
    );
  }

  private _setHasSource(hasSource: boolean): void {
    if (this._hasSource === hasSource) return;
    this._hasSource = hasSource;
    this.emit("source", hasSource);
  }

  private _setOutput(stream: MediaStream | null): void {
    this._mediaStream = stream;
    this.emit("stream", stream);
  }

  private _setStalled(stalled: boolean): void {
    if (this._stalled === stalled) return;
    this._stalled = stalled;
    this.emit("stall", stalled);
    if (stalled) {
      // A stall usually means the browser decoder is wedged (broken H.264
      // reference chain after packet loss or heavy degrade), which only a
      // fresh keyframe clears. Force one now and keep asking while stalled,
      // rather than waiting on the browser's rate-limited PLI.
      this._requestKeyframeForRecovery();
      if (this._keyframeRetry === null) {
        this._keyframeRetry = setInterval(
          () => this._requestKeyframeForRecovery(),
          STALL_KEYFRAME_RETRY_MS,
        );
      }
    } else if (this._keyframeRetry !== null) {
      clearInterval(this._keyframeRetry);
      this._keyframeRetry = null;
    }
  }

  private _requestKeyframeForRecovery(): void {
    this.requestKeyframe().catch(() => {
      // Best effort; a failed send (disconnected) is retried on the next tick,
      // or cleared when the stall ends.
    });
  }

  private _liveIntensity(): number {
    return Math.max(LIVE_INTENSITY_FLOOR, this._state?.degradeLevel ?? 0);
  }

  /**
   * Internal: the source frame shown by a frame `video` presented with
   * `metadata`, when `video` is playing the noise pipeline's output, else
   * null.
   */
  _displayedSourceFrame(
    video: HTMLVideoElement,
    metadata: DisplayedFrameMetadata,
  ): DisplayedSourceFrame | null {
    const pipeline = this._noisePipeline;
    if (!pipeline || video.srcObject !== pipeline.processedStream) return null;
    return pipeline.displayedFrame(metadata);
  }

  /**
   * Internal — genuine teardown (client disconnect / peer failure). Destroys
   * the persistent pipeline, stops its rAF loop, and nulls the stream.
   */
  _teardown(): void {
    if (this._keyframeRetry !== null) {
      clearInterval(this._keyframeRetry);
      this._keyframeRetry = null;
    }
    this._noisePipeline?.destroy();
    this._noisePipeline = null;
    this._rawStream = null;
    this._watchedStream = null;
    this._sourceless = false;
    this._setHasSource(false);
    this._setStalled(false);
    this._setOutput(null);
  }

  async setLayers(layers: Layer[]): Promise<void> {
    await this.client._send({
      type: "set-layers",
      content: { flightId: this.flightId, layers },
    });
  }

  async setRenderSize(width: number, height: number): Promise<void> {
    await this.client._send({
      type: "set-render-size",
      content: { flightId: this.flightId, width, height },
    });
  }

  async setFov(fov: number): Promise<void> {
    await this.client._send({
      type: "set-fov",
      content: { flightId: this.flightId, fov },
    });
  }

  async setPan(yaw: number, pitch: number): Promise<void> {
    await this.client._send({
      type: "set-pan",
      content: { flightId: this.flightId, yaw, pitch },
    });
  }

  async setPanRate(yawRate: number, pitchRate: number): Promise<void> {
    await this.client._send({
      type: "set-pan-rate",
      content: { flightId: this.flightId, yawRate, pitchRate },
    });
  }

  async setZoomRate(rate: number): Promise<void> {
    await this.client._send({
      type: "set-zoom-rate",
      content: { flightId: this.flightId, rate },
    });
  }

  async setDegrade(level: number): Promise<void> {
    await this.client._send({
      type: "set-degrade",
      content: { flightId: this.flightId, level },
    });
  }

  async setQuality(preset: QualityPreset | null): Promise<void> {
    await this.client._send({
      type: "set-quality",
      // null (auto) deserializes to None on the sidecar; the generated
      // payload type marks the field optional, so widen for the wire.
      content: { flightId: this.flightId, preset: preset ?? undefined },
    });
  }

  async setForceFullResolution(force: boolean): Promise<void> {
    await this.client._setForceFullResolution(this.flightId, force);
  }

  async setSizeHold(hold: boolean): Promise<void> {
    await this.client._setSizeHold(this.flightId, hold);
  }

  async requestKeyframe(): Promise<void> {
    await this.client._send({
      type: "request-keyframe",
      content: { flightId: this.flightId },
    });
  }
}

// ---------------------------------------------------------------------------
// KerbcastClient
// ---------------------------------------------------------------------------

/**
 * High-level kerbcast sidecar client. Owns the WebRTC peer + the
 * `kerbcast-control` data channel + the per-camera registry + the
 * `MediaStream`s for each subscribed camera.
 *
 * Usage:
 *
 * ```ts
 * const client = new KerbcastClient({ host: "192.168.1.74", port: 8088 });
 * await client.connect();
 *
 * const cam = client.camera(2592004302);
 * await cam.setFov(35);
 * videoEl.srcObject = cam.mediaStream;
 *
 * cam.on("change", (state) => { ... });
 * client.on("adaptive-shed", (event) => { ... });
 * ```
 *
 * Disconnect with {@link disconnect}. The handles returned by
 * `camera()` remain valid across reconnects; their `mediaStream`
 * goes null on disconnect and is replaced when the next connect's
 * tracks arrive.
 */
export class KerbcastClient extends TypedEmitter<KerbcastClientEvents> {
  private readonly cfg: KerbcastClientConfig;
  private readonly transport: KerbcastTransport;
  private peer: KerbcastPeer | null = null;
  private control: KerbcastDataChannel | null = null;
  /** Whether {@link control} has opened and not closed since. */
  private controlOpen = false;
  /** Commands sent after `connect` but before the control channel opened, in send order. */
  private pendingSends: PendingSend[] = [];
  private _state: KerbcastConnectionState = "disconnected";
  private _cameras: CameraState[] = [];
  private readonly handles = new Map<number, CameraHandle>();
  private requestedOrder: number[] = [];
  /** Set when `connect` is given an explicit slot pool. Switches track
   *  routing from legacy index order to mid-keyed dynamic slots. */
  private dynamicMode = false;
  /** Dynamic mode: transceiver mid -> the live track on that slot. */
  private readonly trackByMid = new Map<string, MediaStreamTrack>();
  /** Dynamic mode: transceiver mid -> the camera bound to that slot
   *  (populated by SlotMap -- initial bindings announced on Hello). */
  private readonly flightByMid = new Map<string, number>();
  /**
   * Legacy mode: raw incoming track id -> flightId. Populated in handleTrack
   * so inboundVideoStats() can match inbound-rtp report entries by
   * trackIdentifier. Cleared on disconnect. Not used in dynamic mode (which
   * matches by mid instead).
   */
  private readonly flightByTrackId = new Map<string, number>();
  /** Legacy mode: transceiver mid -> the camera routed to it by index. */
  private readonly legacyFlightByMid = new Map<string, number>();
  /** The encoded-transform path the current peer was created with. */
  private peerTransformPath: EncodedTransformPath | null = null;
  /** Receivers holding a camera's encoded transform, by mid, with the factory that built it. */
  private readonly transformInstalls = new Map<string, TransformInstall>();
  /**
   * Until when, by mid, an instance replaced on a script-path receiver may go
   * on decoding what it held: Infinity after a camera's transform with no
   * hold given, whose drain has no bound.
   */
  private readonly drainUntil = new Map<string, number>();
  /** Script-path receivers whose transform is watched for frames or awaits its renewal, by mid; see {@link _syncEncodedTransforms}. */
  private readonly pendingHeals = new Map<string, PendingHeal>();
  private healTimer: ReturnType<typeof setInterval> | null = null;
  private healTimerMs = 0;
  private healSeq = 0;
  /** The peer whose stats call a heal poll is awaiting, so another poll of it does not overlap. */
  private healPolling: KerbcastPeer | null = null;
  private holdSampler: ReturnType<typeof setInterval> | null = null;
  /** Receivers copying frames for a camera's taps, by mid. */
  private readonly tapInstalls = new Map<string, number>();
  /** Sidecar version reported on `Hello`; null before handshake. */
  private _sidecarVersion: string | null = null;
  /** Whether the connected sidecar's `hello` offered anchors. */
  private _anchorsSupported = false;
  /** This client's anchors per camera, kept across connections until cleared. */
  private readonly anchorsByFlight = new Map<number, Anchor[]>();
  /** Cameras whose anchors this connection's sidecar holds. */
  private readonly anchorsSent = new Set<number>();
  /** Whether this connection has sent its camera list; until then the last one may be stale. */
  private camerasListed = false;
  /** Whether the connected sidecar's `hello` offered size holds. */
  private _sizeHoldSupported = false;
  /** Video slots the current connection negotiated, grown by {@link growSlots}. */
  private slotCount = 0;
  /** Names this connection to `/renegotiate`; from the `/offer` answer. */
  private session: string | null = null;
  private _renegotiateSupported = false;
  private _maxSlots = Number.POSITIVE_INFINITY;
  /**
   * Recv-only transceivers on the peer. Past {@link slotCount} are the ones a
   * rolled-back re-offer left without a mid; the next re-offer carries them again.
   */
  private transceiverCount = 0;
  /** Set once the sidecar refuses a re-offer outright, or the peer cannot roll one back. */
  private growBroken = false;
  private growing = false;
  /** Failed re-offers since the last that landed, picking the next retry's wait. */
  private growFailures = 0;
  private growRetryTimer: ReturnType<typeof setTimeout> | null = null;
  /** Refused cameras waiting on the re-offer in flight. */
  private readonly growQueue = new Set<number>();
  /**
   * Cameras subscribed and not since unsubscribed, across reconnects. A
   * refusal for a camera no longer here is stale and dropped.
   */
  private readonly wanted = new Set<number>();
  /** Cameras refused a slot that the pool cannot grow to carry, in the order they next get one. */
  private readonly slotWaiters = new Set<number>();
  /** Cameras this client forces to full resolution, sent again on every connection. */
  private readonly forcedFlights = new Set<number>();
  /** Cameras this client holds at their size, sent again on every connection that takes holds. */
  private readonly heldFlights = new Set<number>();
  /** This client's last display size report for each camera. */
  private readonly displaySizes = new Map<number, { width: number; height: number }>();
  /**
   * Names this client to the sidecar across its connections, so a reconnect
   * takes over the force and holds the last connection left behind instead
   * of the camera resizing in between.
   */
  private readonly clientId = mintClientId();
  /** The sidecar's per-camera anchor cap from `hello`. */
  private _maxAnchorsPerCamera = MAX_ANCHORS_PER_CAMERA;
  /** A snapshot split across messages, until its last camera arrives. */
  private pendingSnapshot: { cameras: CameraState[]; remaining: number } | null = null;
  private _encoderBackend: string | null = null;
  /** Last-received throttle state from `settings-state`. False until the first push. */
  private _throttleMainScreen = false;
  /**
   * Mission-time capture clock from `settings-state`. `_captureUt` is null
   * until the first clock arrives (and against an old sidecar that never
   * sends one), so consumers treat it as "no clock". `_captureEpoch` bumps
   * on discontinuities (revert / quickload / scene reload); only the change
   * is meaningful. `_warpRate` defaults to 1 when the field is absent.
   */
  private _captureUt: number | null = null;
  private _captureEpoch = 0;
  private _warpRate = 1;
  /** When `_captureUt` arrived, on the monotonic clock, for extrapolation. */
  private _captureUtAt = 0;
  /** Per-frame capture stamps from `frame-stamp`, for exact frame UT lookup. */
  private readonly frameStamps = new FrameStampStore();
  /** The same frames keyed by UT, for {@link frameAt}. */
  private readonly frameHistory = new FrameHistory();
  /** Each command camera's recent targets lists, for {@link commandCentreAt}. */
  private readonly commandCentres = new CommandCentreHistory();
  /** Whether KSP is in a flight scene. `undefined` until the first signal. */
  private _inFlight: boolean | undefined = undefined;
  private _lastContactAt: number | null = null;
  /** Client-side recording controller, lazily built on first `recording` access. */
  private _recording: RecordingController | null = null;
  /**
   * Physical/ceiling max render size per camera, cached from the last
   * `discover()` call: the fallback behind a handle's `maxRenderSize` when
   * the camera's live state carries no ceiling. Empty until `discover()` has
   * been called at least once; a camera absent from the last discovery
   * response is simply missing here.
   */
  private readonly maxRenderSizeByFlight = new Map<number, { width: number; height: number }>();

  constructor(cfg: KerbcastClientConfig, transport?: KerbcastTransport) {
    super();
    this.cfg = cfg;
    this.transport =
      transport ??
      new BrowserKerbcastTransport({
        iceGatheringTimeoutMs: cfg.iceGatheringTimeoutMs,
      });
  }

  get state(): KerbcastConnectionState {
    return this._state;
  }

  get cameras(): ReadonlyArray<CameraState> {
    return this._cameras;
  }

  get sidecarVersion(): string | null {
    return this._sidecarVersion;
  }

  get encoderBackend(): string | null {
    return this._encoderBackend;
  }

  /**
   * Current effective state of the "Throttle KSP main render" setting.
   * Reflects what the plugin has applied (from `settings-state` messages),
   * not just what was last requested. False until the first push arrives.
   */
  get throttleMainScreen(): boolean {
    return this._throttleMainScreen;
  }

  /**
   * Mission-time capture clock, from the sidecar's ~1Hz `settings-state`.
   * `captureUt` is the KSP universal time (seconds) the current video was
   * captured at, or null when no clock is known (old plugin/sidecar, or
   * before the first push). `epoch` bumps on a UT discontinuity so a
   * consumer can flush and resync. `warpRate` lets a consumer interpolate
   * `captureUt` between samples; defaults to 1.
   */
  get clock(): { captureUt: number | null; epoch: number; warpRate: number } {
    return {
      captureUt: this._captureUt,
      epoch: this._captureEpoch,
      warpRate: this._warpRate,
    };
  }

  /**
   * Mission time of one frame of `flightId`'s video. With the frame's RTP
   * timestamp (from `requestVideoFrameCallback` metadata `rtpTimestamp`, or a
   * `VideoFrame`'s metadata) and a matching stamp from the sidecar, the
   * result is the exact UT the frame was rendered at, `source: "exact"`.
   * Otherwise it falls back to the capture clock extrapolated to `atMs`
   * (default now, `performance.now()` clock), `source: "estimated"`. `null`
   * only when there is no stamp and no clock either.
   */
  frameUt(flightId: number, rtpTimestamp?: number | null, atMs?: number): FrameUt | null {
    if (rtpTimestamp != null) {
      const stamp = this.frameStamps.lookup(flightId, rtpTimestamp);
      if (stamp) return { ut: stamp.ut, epoch: stamp.epoch, source: "exact" };
    }
    if (this._captureUt === null) return null;
    return {
      ut: extrapolateCaptureUt(
        this._captureUt,
        this._warpRate,
        this._captureUtAt,
        atMs ?? monotonicNowMs(),
      ),
      epoch: this._captureEpoch,
      source: "estimated",
    };
  }

  /**
   * Report the mission time of every frame `video` presents while it shows
   * `flightId`'s feed. `video` may play the camera handle's `mediaStream`
   * with or without the noise pipeline: through the pipeline, the frame is
   * the source frame composited into the presented output frame.
   *
   * `source: "exact"` only when the presented frame itself identifies its
   * source frame: the RTP timestamp in the element's own frame metadata, or
   * the pipeline's output frame identified by `mediaTime` (Chrome). Anything
   * else is `"estimated"` and lags the picture rather than leading it: an
   * older frame's stamp, in order of preference the oldest pipeline
   * composite that could be on screen, the element's last exactly identified
   * frame (within the last second), or the newest stamp that arrived more
   * than {@link STAMP_TO_DISPLAY_MAX_MS} before the frame was shown. Only
   * with no stamp at all does it fall back to the capture clock, which
   * leads. All this assumes `video` plays the stream as it arrives: a host
   * that holds frames back on its own delay hides which frame is shown, so
   * the estimate runs ahead by that delay. Such a host looks its frame up
   * with {@link frameAt} instead.
   * Without `requestVideoFrameCallback` estimated frames arrive every 100 ms
   * instead. Frames with neither a stamp nor a clock are skipped. Returns a
   * function that stops watching.
   */
  watchDisplayedFrames(
    flightId: number,
    video: HTMLVideoElement,
    onFrame: (frame: DisplayedFrameUt) => void,
  ): () => void {
    const v = video as VideoWithFrameCallback;
    /*
     * The last frame of this element identified exactly. Frames are presented
     * in order, so when a later frame can't be identified, this one is a
     * bound that never leads the picture.
     */
    let lastExact: (DisplayedFrameUt & { mediaTime: number | undefined }) | null = null;
    const estimate = (presentedAt: number) => {
      if (lastExact && presentedAt - lastExact.presentedAt <= LAST_EXACT_MAX_AGE_MS) {
        const { mediaTime: _mediaTime, ...last } = lastExact;
        onFrame({ ...last, source: "estimated", presentedAt });
        return;
      }
      const older = this.frameStamps.newestArrivedBy(
        flightId,
        presentedAt - STAMP_TO_DISPLAY_MAX_MS,
      );
      if (older) {
        onFrame({ ...older, source: "estimated", flightId, presentedAt });
        return;
      }
      const clock = this.frameUt(flightId, null, presentedAt);
      if (clock) onFrame({ ...clock, flightId, rtpTimestamp: null, presentedAt });
    };
    const report = (
      rtpTimestamp: number | null,
      exact: boolean,
      presentedAt: number,
      mediaTime?: number,
    ) => {
      const stamp = rtpTimestamp === null ? null : this.frameStamps.lookup(flightId, rtpTimestamp);
      /* An identification that runs backwards against the last exact frame is not trusted. */
      const ordered =
        mediaTime === undefined ||
        lastExact?.mediaTime === undefined ||
        mediaTime >= lastExact.mediaTime;
      if (!stamp || !ordered) {
        estimate(presentedAt);
        return;
      }
      const frame: DisplayedFrameUt = {
        ...stamp,
        source: exact ? "exact" : "estimated",
        flightId,
        rtpTimestamp,
        presentedAt,
      };
      if (exact) lastExact = { ...frame, mediaTime };
      onFrame(frame);
    };
    if (typeof v.requestVideoFrameCallback !== "function") {
      const timer = setInterval(() => estimate(monotonicNowMs()), ESTIMATED_FRAME_INTERVAL_MS);
      return () => clearInterval(timer);
    }
    let stopped = false;
    let id: number | null = null;
    const arm = () => {
      id = v.requestVideoFrameCallback!((now, metadata) => {
        if (stopped) return;
        const presentedAt = metadata?.expectedDisplayTime ?? now;
        if (typeof metadata?.rtpTimestamp === "number") {
          report(metadata.rtpTimestamp, true, presentedAt);
        } else {
          const shown = this.handles.get(flightId)?._displayedSourceFrame(video, metadata ?? {});
          report(shown?.rtpTimestamp ?? null, shown?.exact ?? false, presentedAt, metadata?.mediaTime);
        }
        arm();
      });
    };
    arm();
    return () => {
      stopped = true;
      if (id !== null) v.cancelVideoFrameCallback?.(id);
    };
  }

  /**
   * Client-side recording controller. Records a camera's already-received
   * track with the browser's `MediaRecorder`, annotated with the mission-time
   * (UT) clock; purely local, no sidecar involvement. Lazily created on first
   * access and stable thereafter. See {@link RecordingController}.
   */
  get recording(): RecordingController {
    if (!this._recording) this._recording = new RecordingController(this, this.cfg.recording);
    return this._recording;
  }

  /**
   * Whether KSP is currently in a flight scene, from the sidecar's
   * `scene-state-changed`. `undefined` until the first signal (so a
   * consumer never flashes the out-of-flight standby on connect).
   */
  get inFlight(): boolean | undefined {
    return this._inFlight;
  }

  /**
   * When the last message from the sidecar arrived, as `Date.now()`, or null
   * before the first. Kept across connections, so once a connection drops it
   * still says when that connection was last heard from, including time the
   * page spent paused or asleep without noticing the drop.
   */
  get lastContactAt(): number | null {
    return this._lastContactAt;
  }

  /**
   * Send a `set-throttle-main-screen` command to the sidecar, which writes
   * `global.control.json` for the plugin to apply. The plugin persists the
   * change in the per-save difficulty parameter. The resulting
   * `settings-state` broadcast reflects the applied value.
   */
  async setThrottleMainScreen(enabled: boolean): Promise<void> {
    await this._send({
      type: "set-throttle-main-screen",
      content: { enabled },
    });
  }

  /**
   * Pre-handshake camera discovery via the sidecar's HTTP `/cameras`
   * endpoint. Useful when the consumer wants to pick which cameras
   * to subscribe to before opening a peer connection.
   */
  async discover(): Promise<DiscoveredCamera[]> {
    const res = await fetch(`http://${this.cfg.host}:${this.cfg.port}/cameras`);
    if (!res.ok) throw new Error(`/cameras returned ${res.status}`);
    const body = (await res.json()) as { cameras: DiscoveredCamera[] };
    for (const cam of body.cameras) {
      this.maxRenderSizeByFlight.set(cam.flightId, {
        width: cam.maxWidth,
        height: cam.maxHeight,
      });
    }
    return body.cameras;
  }

  /**
   * Internal: the discovery fallback behind
   * {@link KerbcastCameraHandle.maxRenderSize}. `null` when `discover()` has
   * never been called or never reported this camera.
   */
  _maxRenderSize(flightId: number): { width: number; height: number } | null {
    return this.maxRenderSizeByFlight.get(flightId) ?? null;
  }

  /**
   * Open the WebRTC peer and subscribe to cameras. Idempotent: calling
   * `connect` while already connected disconnects first.
   *
   * Two modes:
   * - **Legacy** (no `opts.slots`): one transceiver per requested camera;
   *   an empty array offers one transceiver, which carries the first
   *   currently-known camera. Tracks route by m-line index.
   * - **Dynamic** (`opts.slots` set): negotiates a pool of `opts.slots`
   *   recv-only video slots. `requestedCameras` are the *initial*
   *   subscription bound to the first slots; spare slots are filled at
   *   runtime via {@link subscribe}. Tracks route by transceiver mid
   *   (the sidecar announces bindings via SlotMap), so switching a slot's
   *   camera needs no renegotiation.
   */
  async connect(
    requestedCameras: number[] = [],
    opts: { slots?: number } = {},
  ): Promise<void> {
    this.disconnect();
    this.setState("connecting");
    this.requestedOrder = [...requestedCameras];
    this.dynamicMode = opts.slots !== undefined;
    /* Cameras asked for at connect are wanted like subscribed ones, so a
       connect-time refusal grows the pool or reports waiting, not nothing. */
    if (this.dynamicMode) for (const flightId of requestedCameras) this.wanted.add(flightId);
    this.trackByMid.clear();
    this.flightByMid.clear();
    this.flightByTrackId.clear();
    this.legacyFlightByMid.clear();
    /* RTP timestamps restart from a fresh random base on every peer. */
    this.frameStamps.clear();
    this.commandCentres.clear();
    this._anchorsSupported = false;
    this._sizeHoldSupported = false;
    this.slotCount = 0;
    this.session = null;
    this._renegotiateSupported = false;
    this._maxSlots = Number.POSITIVE_INFINITY;
    this.transceiverCount = 0;
    this.growBroken = false;
    this.growing = false;
    this.growFailures = 0;
    this.cancelGrowRetry();
    this.growQueue.clear();
    for (const flightId of this.slotWaiters) {
      this.getOrCreateHandle(flightId)._setWaitingForSlot(false);
    }
    this.slotWaiters.clear();
    this.anchorsSent.clear();
    this.camerasListed = false;
    this._maxAnchorsPerCamera = MAX_ANCHORS_PER_CAMERA;
    this.pendingSnapshot = null;

    const peerOpts = this.peerOptions();
    const peer = this.transport.createPeer(
      this.cfg.iceServers ?? [{ urls: "stun:stun.l.google.com:19302" }],
      peerOpts,
    );
    this.peer = peer;
    this.peerTransformPath = this.transport.encodedTransformPath?.(peerOpts) ?? null;

    // Slot-pool size = the recv-only transceivers we offer. Dynamic mode
    // uses the explicit pool size (spares subscribable at runtime); legacy
    // uses one per requested camera (>=1).
    const slotCount = this.dynamicMode
      ? Math.max(opts.slots ?? 0, 1)
      : Math.max(this.requestedOrder.length, 1);
    for (let i = 0; i < slotCount; i++) {
      peer.addRecvOnlyTransceiver();
    }
    this.transceiverCount = slotCount;

    peer.onTrack((track, idx, mid) => this.handleTrack(track, idx, mid));

    peer.onStateChange((s) => {
      this.setState(s);
      /* Disconnected is a pause the peer comes back from on its own, with the
         same tracks and slots, so the feeds keep their streams and the stall
         static covers the gap. Only a failed peer is gone for good. */
      if (s === "failed" && this.peer === peer) {
        this.clearEncodedTransforms();
        this.tearDownStreams();
      }
      /* A failed peer never recovers; closing it frees its relay allocations. */
      if (s === "failed") {
        if (this.peer === peer) {
          this.growBroken = true;
          this.cancelGrowRetry();
        }
        peer.close();
      }
    });

    const control = peer.createDataChannel("kerbcast-control");
    this.control = control;
    control.onOpen(() => {
      if (this.control !== control) return;
      this.controlOpen = true;
      void this._send({ type: "hello" });
      /* Queued commands follow hello, in order, ahead of control-open listeners. */
      const pending = this.pendingSends;
      this.pendingSends = [];
      for (const p of pending) {
        clearTimeout(p.timer);
        try {
          control.send(p.payload);
          p.resolve();
        } catch (err) {
          p.reject(err instanceof Error ? err : new Error(String(err)));
        }
      }
      this.emit("control-open", undefined);
    });
    control.onMessage((raw) => {
      this.handleServerMessage(raw);
    });
    control.onClose(() => {
      /*
       * Channel closure usually follows peer disconnect; the peer
       * state-change handler covers the tear-down.
       */
      if (this.control !== control) return;
      this.controlOpen = false;
      this.rejectPendingSends("[kerbcast] control channel closed before it opened");
    });

    await peer.createOffer().then((sdp) => peer.setLocalDescription(sdp));
    await peer.waitForIceComplete();

    const body: { sdp: string; cameras: number[]; slots?: number } = {
      sdp: peer.localSdp(),
      cameras: this.requestedOrder,
    };
    if (this.dynamicMode) body.slots = slotCount;

    let answer: { sdp: string; cameras: number[]; session?: string };
    try {
      answer = await (this.cfg.negotiate
        ? this.cfg.negotiate(body)
        : this.httpNegotiate(body));
    } catch (err) {
      peer.close();
      this.setState("failed");
      throw err;
    }
    await peer.setRemoteAnswer(answer.sdp);
    this.slotCount = slotCount;
    this.session = answer.session ?? null;
    // Legacy mode routes by index → requestedOrder, so realign to what the
    // sidecar actually wired. Dynamic mode routes by mid via SlotMap (initial
    // bindings arrive on Hello), so the answer's camera list isn't used.
    if (!this.dynamicMode) {
      this.requestedOrder = answer.cameras;
    }
  }

  /** Default signaling: POST the offer to the sidecar's HTTP `/offer`. */
  private async httpNegotiate(offer: {
    sdp: string;
    cameras: number[];
    slots?: number;
  }): Promise<{ sdp: string; cameras: number[]; session?: string }> {
    const res = await fetch(`http://${this.cfg.host}:${this.cfg.port}/offer`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(offer),
    });
    if (!res.ok) {
      /* A refusal carries an ErrorPayload; anything else keeps the bare status. */
      const refusal = (await res.json().catch(() => null)) as Partial<ErrorPayload> | null;
      if (typeof refusal?.message === "string") {
        throw new OfferRefusedError(res.status, refusal.message, refusal.code);
      }
      throw new Error(`POST /offer returned ${res.status}`);
    }
    return (await res.json()) as { sdp: string; cameras: number[]; session?: string };
  }

  /** Default re-offer exchange: POST to the sidecar's HTTP `/renegotiate`. */
  private async httpRenegotiate(offer: {
    session: string;
    sdp: string;
    slots: number;
  }): Promise<{ sdp: string }> {
    const abort = new AbortController();
    const timer = setTimeout(() => abort.abort(), RENEGOTIATE_TIMEOUT_MS);
    try {
      const res = await fetch(`http://${this.cfg.host}:${this.cfg.port}/renegotiate`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(offer),
        signal: abort.signal,
      });
      if (!res.ok) {
        const message = `POST /renegotiate returned ${res.status}`;
        /* A 4xx is the sidecar refusing this re-offer; a timeout or a busy
           proxy in front of it is worth another try. */
        const refused =
          res.status >= 400 && res.status < 500 && res.status !== 408 && res.status !== 429;
        throw refused ? new RenegotiateRefusedError(message) : new Error(message);
      }
      return (await res.json()) as { sdp: string };
    } catch (err) {
      if (abort.signal.aborted) {
        throw new Error(`POST /renegotiate gave no answer within ${RENEGOTIATE_TIMEOUT_MS} ms`);
      }
      throw err;
    } finally {
      clearTimeout(timer);
    }
  }

  /** Whether a camera refused a slot can be carried by growing the pool. */
  private canGrowSlots(): boolean {
    return (
      this.dynamicMode &&
      this.peer !== null &&
      this._renegotiateSupported &&
      !this.growBroken &&
      this.growRetryTimer === null &&
      this.session !== null &&
      this.slotCount < this._maxSlots &&
      (this.cfg.renegotiate !== undefined || this.cfg.negotiate === undefined)
    );
  }

  /**
   * Add video slots for the cameras in {@link growQueue} with one re-offer,
   * then subscribe them again. Refusals that land meanwhile join the queue
   * and go in the next round. Running feeds keep their slots throughout.
   */
  private async growSlots(): Promise<void> {
    if (this.growing) return;
    const peer = this.peer;
    const session = this.session;
    if (!peer || session === null) return;
    this.growing = true;
    let batch: number[] = [];
    let answerRefused = false;
    try {
      while (this.growQueue.size > 0 && this.peer === peer) {
        batch = [...this.growQueue];
        this.growQueue.clear();
        const target = Math.min(
          this._maxSlots,
          Math.max(this.transceiverCount, this.slotCount + Math.max(batch.length, SLOT_GROWTH_STEP)),
        );
        if (target <= this.slotCount) {
          for (const flightId of batch) this.markWaitingForSlot(flightId);
          batch = [];
          continue;
        }
        for (let i = this.transceiverCount; i < target; i++) peer.addRecvOnlyTransceiver();
        this.transceiverCount = Math.max(this.transceiverCount, target);
        await peer.createOffer().then((sdp) => peer.setLocalDescription(sdp));
        const offer = { session, sdp: peer.localSdp(), slots: target };
        const answer = await (this.cfg.renegotiate
          ? this.cfg.renegotiate(offer)
          : this.httpRenegotiate(offer));
        if (this.peer !== peer) return;
        await peer.setRemoteAnswer(answer.sdp).catch((err: unknown) => {
          answerRefused = true;
          throw err;
        });
        /* A reconnect during that await owns the client now; this grow belongs to a dead peer. */
        if (this.peer !== peer) return;
        this.slotCount = target;
        this.growFailures = 0;
        /* The sidecar binds no camera to the new slots until this lands, and
           the channel is ordered, so the subscribes below can use them. */
        void this._send({ type: "confirm-slots", content: { slots: target } }).catch(() => {});
        for (const flightId of batch) {
          if (this.wanted.has(flightId)) {
            void this._send({ type: "subscribe", content: { flightId } }).catch(() => {});
          }
        }
        batch = [];
      }
    } catch (err) {
      if (this.peer !== peer) return;
      /*
       * The sidecar may have grown its side already (the answer was lost, or
       * the browser refused it). Its new slots stay unconfirmed and idle, so
       * running feeds carry on; rolling back returns the browser to its last
       * agreed description, keeping the added transceivers without a mid.
       * The retry offers those same transceivers, and the sidecar answers
       * again whatever unconfirmed slots it holds, so the two sides line up.
       * Meanwhile the cameras wait for a slot.
       */
      try {
        await peer.rollback?.();
      } catch {
        // Nothing pending to roll back.
      }
      this.growFailures++;
      /* The browser refusing the sidecar's answer would refuse the next one too. */
      if (
        err instanceof RenegotiateRefusedError ||
        answerRefused ||
        !peer.rollback ||
        this.growFailures >= MAX_GROW_FAILURES
      ) {
        this.growBroken = true;
      }
      for (const flightId of [...batch, ...this.growQueue]) this.markWaitingForSlot(flightId);
      this.growQueue.clear();
      if (!this.growBroken) this.scheduleGrowRetry(peer);
      /* A run of failures is said once, not on every retry. */
      if (this.growFailures === 1 || this.growBroken) {
        this.emit("error", {
          message: `[kerbcast] could not add video slots: ${err instanceof Error ? err.message : String(err)}`,
          source: ErrorSource.Sidecar,
        });
      }
    } finally {
      if (this.peer === peer) this.growing = false;
    }
  }

  private scheduleGrowRetry(peer: KerbcastPeer): void {
    if (this.growRetryTimer !== null) return;
    const delay = GROW_RETRY_DELAYS_MS[Math.min(this.growFailures, GROW_RETRY_DELAYS_MS.length) - 1];
    this.growRetryTimer = setTimeout(() => {
      this.growRetryTimer = null;
      if (this.peer !== peer || this.growing) return;
      for (const flightId of this.slotWaiters) {
        const destroyed = this.handles.get(flightId)?.state?.lifecycle === CameraLifecycle.Destroyed;
        if (this.wanted.has(flightId) && !destroyed) this.growQueue.add(flightId);
      }
      if (this.growQueue.size === 0 || !this.canGrowSlots()) {
        this.growQueue.clear();
        this.growFailures = 0;
        return;
      }
      void this.growSlots();
    }, delay);
  }

  /** End a run of failed re-offers, so the next failure starts a fresh one. */
  private cancelGrowRetry(): void {
    this.growFailures = 0;
    if (this.growRetryTimer === null) return;
    clearTimeout(this.growRetryTimer);
    this.growRetryTimer = null;
  }

  /**
   * A subscribe for `flightId` found every slot taken. Grows the pool when it
   * can and returns true (handled quietly); otherwise marks the camera
   * waiting and returns false so the refusal still reaches `error` listeners.
   */
  private onNoFreeSlot(flightId: number): boolean {
    /* A stale refusal, or a waiter refused again after a hand-off race: either way already said. */
    if (!this.wanted.has(flightId) || this.slotWaiters.has(flightId)) return true;
    if (this.canGrowSlots()) {
      this.growQueue.add(flightId);
      void this.growSlots();
      return true;
    }
    this.markWaitingForSlot(flightId);
    return false;
  }

  private markWaitingForSlot(flightId: number): void {
    if (!this.wanted.has(flightId)) return;
    this.slotWaiters.add(flightId);
    this.getOrCreateHandle(flightId)._setWaitingForSlot(true);
  }

  private clearWaitingForSlot(flightId: number): void {
    if (!this.slotWaiters.delete(flightId)) return;
    this.getOrCreateHandle(flightId)._setWaitingForSlot(false);
    if (this.slotWaiters.size === 0) this.cancelGrowRetry();
  }

  /**
   * A slot came free: offer it to the camera that has waited longest. That
   * camera moves to the back of the line, so slots freed together go to
   * different cameras; it leaves the line once a slot-map binds it.
   */
  private handFreedSlotToWaiting(): void {
    for (const flightId of [...this.slotWaiters]) {
      const destroyed = this.handles.get(flightId)?.state?.lifecycle === CameraLifecycle.Destroyed;
      if (!this.wanted.has(flightId) || destroyed) {
        this.clearWaitingForSlot(flightId);
        continue;
      }
      this.slotWaiters.delete(flightId);
      this.slotWaiters.add(flightId);
      void this._send({ type: "subscribe", content: { flightId } }).catch(() => {});
      return;
    }
  }

  disconnect(): void {
    this.cancelGrowRetry();
    this.clearEncodedTransforms();
    this.peer?.close();
    this.peer = null;
    this.control = null;
    this.controlOpen = false;
    this.rejectPendingSends("[kerbcast] client disconnected before the control channel opened");
    this.tearDownStreams();
    this.setState("disconnected");
  }

  /**
   * Stable per-flight-id handle. Creates a new handle on first call
   * for a given ID; returns the same handle on subsequent calls.
   */
  camera(flightId: number): KerbcastCameraHandle {
    return this.getOrCreateHandle(flightId);
  }

  /**
   * Dynamic mode only: bind a camera to a free slot on the already-connected
   * peer. The camera's stream appears on its handle once the sidecar answers
   * with a SlotMap. When every slot is taken the client adds slots with a
   * re-offer, leaving running feeds alone; where it cannot, the handle
   * reports `waitingForSlot`, the refusal is emitted as an `error` event, and
   * the camera takes the next slot that comes free.
   */
  async subscribe(flightId: number): Promise<void> {
    if (this.dynamicMode) this.wanted.add(flightId);
    await this._send({ type: "subscribe", content: { flightId } });
  }

  /**
   * Dynamic mode only: release a camera's slot. Its handle's `mediaStream`
   * goes null once the sidecar confirms with a SlotMap.
   */
  async unsubscribe(flightId: number): Promise<void> {
    this.wanted.delete(flightId);
    this.growQueue.delete(flightId);
    this.clearWaitingForSlot(flightId);
    /* The sidecar forgets this client's size with the subscription. */
    this.displaySizes.delete(flightId);
    await this._send({ type: "unsubscribe", content: { flightId } });
  }

  /**
   * Report this consumer's own displayed pixel size for a camera, driving the
   * sidecar's auto-resolution. Unlike `setRenderSize` (an operator command that
   * sets the shared render size directly, last-writer-wins), this is a
   * per-consumer input the sidecar aggregates MAX-across-consumers, so a big
   * spotlight bumps the stream up while tiny avatars leave it small. The SDK's
   * feed primitives self-measure and call this; consumers do not report by hand.
   */
  async reportDisplaySize(flightId: number, width: number, height: number): Promise<void> {
    this.displaySizes.set(flightId, { width, height });
    await this._send({ type: "report-display-size", content: { flightId, width, height } });
  }

  /**
   * Ask for background capture: subscribed cameras this consumer is NOT
   * displaying keep capturing at a low rate instead of stopping, so switching
   * to one promotes an already-flowing stream rather than cold-starting from
   * black.
   *
   * Opt-in and per-consumer, aggregated MAX-across-consumers. A consumer that
   * never calls this pays nothing, which is why the same kerbcast install
   * serves both a plain viewer and a consumer that wants background capture
   * with no configuration either side. Pass 0 to stop asking; the request is
   * dropped automatically on disconnect.
   *
   * The rate is a REQUEST, not a guarantee: it is capped by the operator's
   * `BackgroundCaptureFps` ceiling and is the first thing dropped as the game
   * approaches its framerate floor. Read `CameraState.captureState` for what is
   * actually happening rather than assuming this took effect — it reports
   * `Background` only while the reduced rate is genuinely running, and `Off`
   * once the layer has been shed.
   *
   * Receiving background frames is not the same as keeping them. A consumer
   * that wants *history* for a camera it isn't showing — to render instantly on
   * switch under a simulated signal delay, say — has to buffer those feeds
   * itself; background capture only makes the frames available to buffer.
   */
  async setBackgroundCaptureFps(fps: number): Promise<void> {
    await this._send({ type: "set-background-capture-fps", content: { fps } });
  }

  /**
   * Ask a pan+zoom camera to auto-track a moving vessel (or stop). Sends the
   * intent; the sidecar holds the chosen mode per camera and publishes it in
   * `CameraState.trackMode`, so the UI must reflect that server-confirmed value
   * rather than this call (two browsers stay in agreement). While tracking, the
   * camera also auto-zooms; `TrackMode.None` hands aiming/zoom back.
   */
  async setTrackTarget(flightId: number, mode: TrackMode): Promise<void> {
    await this._send({ type: "set-track-target", content: { flightId, mode } });
  }

  /**
   * Point a command camera by `aim`, or return it to its default with `null`
   * (the active craft in flight, the configured craft at the Space Center).
   * A `vessel` aim follows any craft from `commandCentre.targets`, whatever
   * its eligibility; `allowUncertain` also accepts one whose position a
   * governing world model cannot narrow enough to frame (`tooUncertain`), as
   * a deliberate search of the region. The aim shows up in
   * `commandCentre.aimKind` and
   * `.subjectPersistentId` once the game applies it.
   *
   * Rejects at once while the camera's state reports a command authority
   * (`commandTrack.authority`): it holds the lock and only it can aim. A
   * refusal that races the lock being taken arrives as an `error` event with
   * code `ErrorCode.CommandLocked` instead.
   *
   * Rejects with a {@link CommandTargetUnknownError} when the aim's craft is
   * not in the camera's `commandCentre.targets` (a craft merged away in a
   * dock, say). A list cut to fit the control channel
   * (`commandCentre.targetsOmitted` set) may leave the craft out, so the
   * sidecar decides then. A refusal that races the list changing, or that the
   * sidecar makes, arrives as an `error` event with code
   * `ErrorCode.CommandTargetUnknown`.
   */
  async setCommandAim(flightId: number, aim: CommandAim | null): Promise<void> {
    const cam = this._cameras.find((c) => c.flightId === flightId);
    const owner = cam?.commandTrack?.authority?.owner;
    if (owner) {
      throw new CommandLockedError(owner);
    }
    /* An empty list is what the game writes when listing fails, and a cut one
       leaves craft out, so the sidecar decides then. */
    const craft = aim ? aimCraft(aim) : undefined;
    const targets = cam?.commandCentre?.targets;
    const whole = !cam?.commandCentre?.targetsOmitted;
    if (craft !== undefined && whole && targets?.length && !targets.some((t) => t.persistentId === craft)) {
      throw new CommandTargetUnknownError(flightId, craft);
    }
    await this._send({
      type: "set-command-aim",
      content: aim ? { flightId, aim } : { flightId },
    });
  }

  /**
   * A command camera's subject, range and targets list as they stood at
   * `displayUt`, the UT of the frame a view shows (`DisplayedFrameUt.ut`), so
   * a delayed view never lists what its picture has not reached. `null`
   * while every list the game has sent is newer than that. Without a
   * displayed UT, the newest, as `commandCentre` on the camera list, but
   * `null` while a command lock is held: with nothing displayed there is no
   * frame the list may run to. Lists from a game older than 1.12 carry no UT
   * and apply at once.
   */
  commandCentreAt(flightId: number, displayUt?: number | null): CommandCentreInfo | null {
    if (displayUt == null) {
      const cam = this._cameras.find((c) => c.flightId === flightId);
      if (cam?.commandTrack?.authority) return null;
    }
    return this.commandCentres.at(flightId, displayUt);
  }

  /**
   * The frame of `flightId` a view showing `displayUt` has on screen: the
   * newest stamped frame at or before that UT, with its command state and
   * anchors. For a host that plays the video out on its own delay, where
   * the browser can no longer say which frame it presents. `null` when no
   * frame that old is kept: frames go 15 minutes of UT behind the newest,
   * at most 20,000 per camera, and on a new capture epoch or when the
   * camera leaves the list. Kept across reconnects.
   */
  frameAt(flightId: number, displayUt: number): DisplayedFrameUt | null {
    return this.frameHistory.at(flightId, displayUt);
  }

  /**
   * Mark things in the game on `flightId`'s picture: a part, a craft, a
   * point on a body or a direction. Replaces this client's anchors on that
   * camera; `null` or an empty list clears them. The game projects each
   * anchor through the camera that rendered every frame, and the results
   * arrive with that frame (see {@link watchDisplayedFrames} and
   * `DisplayedFrameUt.anchors`), so they line up with the picture under any
   * stream delay.
   *
   * Anchors belong to this client: other clients never see them, the
   * command lock does not apply, and they are kept here until cleared and
   * sent again whenever the client reconnects. Anchors on a camera missing
   * from {@link cameras} wait until it appears. Only a sidecar whose
   * `hello` offers anchors (1.12 and later) receives them; against an older
   * one they are kept but never sent. Rejects with a `RangeError` for more
   * than {@link maxAnchorsPerCamera} anchors or a repeated id; a malformed
   * anchor is refused by the sidecar with an `error` event.
   */
  async setAnchors(flightId: number, anchors: readonly Anchor[] | null): Promise<void> {
    const list = anchors ? [...anchors] : [];
    if (list.length > this._maxAnchorsPerCamera) {
      throw new RangeError(
        `at most ${this._maxAnchorsPerCamera} anchors per camera, got ${list.length}`,
      );
    }
    const ids = new Set<string>();
    for (const a of list) {
      if (ids.has(a.id)) throw new RangeError(`anchor id ${a.id} is used twice`);
      ids.add(a.id);
    }
    if (list.length === 0) {
      this.anchorsByFlight.delete(flightId);
      /* A camera the sidecar no longer lists would answer with an error. */
      if (!this.anchorsSent.delete(flightId) || !this._isListed(flightId)) return;
      await this._send({ type: "set-anchors", content: { flightId } });
      return;
    }
    this.anchorsByFlight.set(flightId, list);
    if (!this._anchorsSupported || !this._isListed(flightId)) return;
    this.anchorsSent.add(flightId);
    await this._send({ type: "set-anchors", content: { flightId, anchors: list } });
  }

  private _isListed(flightId: number): boolean {
    return this.camerasListed && this._cameras.some((c) => c.flightId === flightId);
  }

  /** Send the sidecar the anchors it lacks for cameras it now lists. */
  private _sendHeldAnchors(): void {
    if (!this._anchorsSupported) return;
    for (const [flightId, anchors] of this.anchorsByFlight) {
      if (this.anchorsSent.has(flightId) || !this._isListed(flightId)) continue;
      this.anchorsSent.add(flightId);
      void this._send({ type: "set-anchors", content: { flightId, anchors } }).catch(() => {});
    }
  }

  /**
   * Internal: records a camera's force and sends it. Kept here rather than
   * on the handle so a reconnect can send it again: the sidecar forgets a
   * viewer's force when its connection drops.
   */
  async _setForceFullResolution(flightId: number, force: boolean): Promise<void> {
    if (force) this.forcedFlights.add(flightId);
    else this.forcedFlights.delete(flightId);
    await this._send({ type: "set-force-full-resolution", content: { flightId, force } });
  }

  /**
   * Internal: records a camera's size hold and sends it when the sidecar
   * takes holds. Kept across connections like the force; a sidecar older
   * than 1.12 never receives it.
   */
  async _setSizeHold(flightId: number, hold: boolean): Promise<void> {
    if (hold) this.heldFlights.add(flightId);
    else this.heldFlights.delete(flightId);
    if (!this._sizeHoldSupported) return;
    await this._send({ type: "set-size-hold", content: { flightId, hold } });
  }

  /** Must go ahead of {@link _sendHeldSizes}: the sidecar hands over what a resume names. */
  private _sendResume(): void {
    void this._send({
      type: "resume",
      content: {
        clientId: this.clientId,
        forced: [...this.forcedFlights],
        held: this._sizeHoldSupported ? [...this.heldFlights] : [],
      },
    }).catch(() => {});
  }

  /** Sends a new connection the forces and holds this client still has in place. */
  private _sendHeldSizes(): void {
    for (const flightId of this.forcedFlights) {
      void this._send({ type: "set-force-full-resolution", content: { flightId, force: true } }).catch(() => {});
    }
    if (!this._sizeHoldSupported) return;
    for (const flightId of this.heldFlights) {
      void this._send({ type: "set-size-hold", content: { flightId, hold: true } }).catch(() => {});
    }
  }

  /** This client's anchors on `flightId`, as last set. Empty when none. */
  anchorsFor(flightId: number): readonly Anchor[] {
    return this.anchorsByFlight.get(flightId) ?? [];
  }

  /**
   * The most anchors {@link setAnchors} takes on one camera: what the
   * connected sidecar's `hello` says, or {@link MAX_ANCHORS_PER_CAMERA}
   * before it and from a sidecar that does not say.
   */
  get maxAnchorsPerCamera(): number {
    return this._maxAnchorsPerCamera;
  }

  /** Whether the connected sidecar takes anchors. False until its `hello`. */
  get anchorsSupported(): boolean {
    return this._anchorsSupported;
  }

  /**
   * Create a command camera standing at `vantage` for the rest of the
   * current game scene (flight or Space Center), and resolve with its state
   * once it appears in {@link cameras} and the game has reported its target
   * and target list (`commandCentre.subjectPersistentId`, `.targets`).
   * Every consumer sees it. It points at the active craft unless `aim` says
   * otherwise, and takes {@link setCommandAim} and
   * {@link destroyCommandCamera} like any command camera.
   *
   * Rejects if the camera has not appeared within `timeoutMs`: the game
   * builds nothing for a body, launch site, station or craft it can not
   * find, and logs why. A malformed vantage is refused by the sidecar with
   * an `error` event.
   */
  async createCommandCamera(
    vantage: CommandVantage,
    opts: CreateCommandCameraOptions = {},
  ): Promise<CameraState> {
    const token = opts.token ?? randomToken();
    const timeoutMs = opts.timeoutMs ?? COMMAND_CAMERA_CREATE_TIMEOUT_MS;
    const find = (cams: ReadonlyArray<CameraState>) =>
      cams.find(
        (c) =>
          c.kind === CameraKind.Command &&
          c.commandCentre?.token === token &&
          c.lifecycle !== CameraLifecycle.Destroyed,
      );
    /*
     * The camera is announced before the game's first status report for it,
     * so its first state has no target or target list yet. That report always
     * carries commandTrack, so wait for it; a camera whose report never comes
     * still resolves after COMMAND_CAMERA_STATE_WAIT_MS with what is known.
     */
    const appeared = new Promise<CameraState>((resolve, reject) => {
      let latest: CameraState | undefined;
      let stateTimer: ReturnType<typeof setTimeout> | undefined;
      const timer = setTimeout(() => {
        off();
        reject(
          new Error(
            `[kerbcast] command camera ${token} did not appear within ${timeoutMs} ms`,
          ),
        );
      }, timeoutMs);
      const finish = (cam: CameraState) => {
        clearTimeout(timer);
        clearTimeout(stateTimer);
        off();
        resolve(cam);
      };
      const off = this.on("cameras-change", (cams) => {
        const cam = find(cams);
        if (!cam) return;
        if (cam.commandTrack != null) {
          finish(cam);
          return;
        }
        if (latest === undefined) {
          clearTimeout(timer);
          stateTimer = setTimeout(() => finish(latest ?? cam), COMMAND_CAMERA_STATE_WAIT_MS);
        }
        latest = cam;
      });
    });
    try {
      await this._send({
        type: "create-command-camera",
        content: {
          token,
          vantage,
          ...(opts.name !== undefined ? { name: opts.name } : {}),
          ...(opts.aim !== undefined ? { aim: opts.aim } : {}),
        },
      });
    } catch (err) {
      appeared.catch(() => {});
      throw err;
    }
    return appeared;
  }

  /**
   * Destroy a command camera made by {@link createCommandCamera} (or created
   * in game); its feed ends. The game's own command camera can not be
   * destroyed. Destroying one that is already gone does nothing.
   */
  async destroyCommandCamera(flightId: number): Promise<void> {
    await this._send({ type: "destroy-command-camera", content: { flightId } });
  }

  /**
   * Internal — resolves the effective noise enabled state for a camera,
   * merging the per-camera override (if any) with the client-level default.
   */
  _resolveNoise(override: Partial<NoiseConfig> | null): boolean {
    if (override?.enabled !== undefined) return override.enabled;
    return this.cfg.noise?.enabled !== false; // default: enabled
  }

  /** Internal: whether noise-disabled cameras stay on the frame-locked path. */
  _frameLock(): boolean {
    return this.cfg.frameLock === true;
  }

  /**
   * Internal: sends a typed `ClientMessage` over the control channel. Throws
   * when there is no connection. Between `connect` resolving and the channel
   * opening, the message waits and goes out as soon as it opens; it rejects
   * if the channel has not opened within {@link CONTROL_OPEN_TIMEOUT_MS}, or
   * the client disconnects first. Public on the camera handle, but consumers
   * shouldn't need to call this directly.
   */
  async _send(msg: ClientMessage): Promise<void> {
    const control = this.control;
    if (!control) {
      throw new Error("[kerbcast] control channel not open");
    }
    const payload = JSON.stringify(msg);
    if (this.controlOpen) {
      control.send(payload);
      return;
    }
    await new Promise<void>((resolve, reject) => {
      const entry: PendingSend = {
        payload,
        resolve,
        reject,
        timer: setTimeout(() => {
          this.pendingSends = this.pendingSends.filter((p) => p !== entry);
          reject(
            new Error(
              `[kerbcast] control channel did not open within ${CONTROL_OPEN_TIMEOUT_MS} ms`,
            ),
          );
        }, CONTROL_OPEN_TIMEOUT_MS),
      };
      this.pendingSends.push(entry);
    });
  }

  private rejectPendingSends(message: string): void {
    const pending = this.pendingSends;
    this.pendingSends = [];
    for (const p of pending) {
      clearTimeout(p.timer);
      p.reject(new Error(message));
    }
  }

  // -- private --

  private getOrCreateHandle(flightId: number): CameraHandle {
    let handle = this.handles.get(flightId);
    if (!handle) {
      handle = new CameraHandle(flightId, this);
      this.handles.set(flightId, handle);
    }
    return handle;
  }

  private handleTrack(
    track: MediaStreamTrack,
    idx: number,
    mid: string,
  ): void {
    if (this.dynamicMode) {
      // Store the slot's track keyed by mid; bind it to a camera if (or once)
      // a SlotMap names this mid. Track and SlotMap can arrive in either
      // order — whichever lands second triggers the binding.
      this.trackByMid.set(mid, track);
      const flightId = this.flightByMid.get(mid);
      if (flightId !== undefined) {
        this.getOrCreateHandle(flightId)._setMediaStream(
          new MediaStream([track]),
        );
      }
      return;
    }
    // Legacy: m-line index maps to the requested camera list.
    const flightId = this.requestedOrder[idx];
    if (flightId === undefined) return;
    if (mid) {
      this.legacyFlightByMid.set(mid, flightId);
      this._syncEncodedTransforms();
    }
    // Record the raw track id so inboundVideoStats() can match it against
    // the report's trackIdentifier field.
    if (track.id) this.flightByTrackId.set(track.id, flightId);
    this.getOrCreateHandle(flightId)._setMediaStream(new MediaStream([track]));
  }

  private _applySnapshot(cameras: CameraState[]): void {
    const listed = this.camerasListed ? new Set(this._cameras.map((c) => c.flightId)) : null;
    this._cameras = cameras;
    this.camerasListed = true;
    this.commandCentres.retain(cameras.map((c) => c.flightId));
    this.frameHistory.retain(cameras.map((c) => c.flightId));
    for (const cam of cameras) {
      this.commandCentres.record(cam.flightId, cam.commandCentre);
      const handle = this.getOrCreateHandle(cam.flightId);
      const reattached = attachChanged(handle.state, cam);
      const resurrected = handle._setState(cam);
      if (resurrected) this._resourceHandle(cam.flightId);
      if (resurrected || reattached || (listed && !listed.has(cam.flightId))) {
        this._renewEncodedTransform(cam.flightId);
      }
    }
    this.emit("cameras-change", cameras);
    this._sendHeldAnchors();
    /* A camera back in the list after a scene change is a new camera to the
       sidecar, which has forgotten what this client asked of the old one.
       Only cameras this client still subscribes to, so a size from a feed it
       has since closed cannot pin the camera for everyone. */
    if (listed) {
      for (const cam of cameras) {
        if (!listed.has(cam.flightId) && this._subscribes(cam.flightId)) this._sendHeldFor(cam.flightId);
      }
    }
  }

  private _subscribes(flightId: number): boolean {
    return this.dynamicMode ? this.wanted.has(flightId) : this.requestedOrder.includes(flightId);
  }

  private _sendHeldFor(flightId: number): void {
    if (this.forcedFlights.has(flightId)) {
      void this._send({ type: "set-force-full-resolution", content: { flightId, force: true } }).catch(() => {});
    }
    if (this._sizeHoldSupported && this.heldFlights.has(flightId)) {
      void this._send({ type: "set-size-hold", content: { flightId, hold: true } }).catch(() => {});
    }
    const size = this.displaySizes.get(flightId);
    if (size) {
      void this._send({ type: "report-display-size", content: { flightId, ...size } }).catch(() => {});
    }
  }

  private handleServerMessage(raw: string): void {
    let msg: ServerMessage;
    try {
      msg = JSON.parse(raw) as ServerMessage;
    } catch (err) {
      this.emit("error", {
        message: err instanceof Error ? err.message : String(err),
        source: ErrorSource.Client,
      });
      return;
    }
    const heardAt = Date.now();
    const lastHeard = this._lastContactAt;
    this._lastContactAt = heardAt;
    if (lastHeard != null && heardAt - lastHeard >= CONTACT_GAP_MS) {
      this.emit("contact-gap", { from: lastHeard, to: heardAt });
    }
    /* A split snapshot's cameras arrive back to back; anything else means the rest were lost. */
    if (this.pendingSnapshot && msg.type !== "camera-state-changed") {
      const pending = this.pendingSnapshot;
      this.pendingSnapshot = null;
      this._applySnapshot(pending.cameras);
    }
    switch (msg.type) {
      case "hello":
        this._sidecarVersion = msg.content.sidecarVersion;
        this._encoderBackend = msg.content.encoderBackend;
        /* Stamps are opt-in so clients that predate them never pay for them. */
        this._anchorsSupported = msg.content.anchors === true;
        this._maxAnchorsPerCamera = msg.content.maxAnchorsPerCamera ?? MAX_ANCHORS_PER_CAMERA;
        this._sizeHoldSupported = msg.content.sizeHold === true;
        this._renegotiateSupported = msg.content.renegotiate === true;
        this._maxSlots = msg.content.maxSlots ?? Number.POSITIVE_INFINITY;
        if (msg.content.resume === true) this._sendResume();
        this._sendHeldSizes();
        /* A new connection starts with no anchors on the sidecar. */
        this.anchorsSent.clear();
        this.camerasListed = false;
        if (msg.content.frameStamps === true) {
          void this._send({
            type: "set-frame-stamps",
            content: {
              enabled: true,
              ...(this._anchorsSupported ? { anchors: true } : {}),
              ...(msg.content.frameCommand === true ? { command: true } : {}),
            },
          }).catch(() => {});
        }
        break;
      case "camera-snapshot":
        /* The rest of a split snapshot follows straight after as state changes. */
        if (msg.content.follows) {
          this.pendingSnapshot = { cameras: [...msg.content.cameras], remaining: msg.content.follows };
          break;
        }
        this.pendingSnapshot = null;
        this._applySnapshot(msg.content.cameras);
        break;
      case "camera-state-changed": {
        const pending = this.pendingSnapshot;
        if (pending) {
          pending.cameras.push(msg.content.state);
          if (--pending.remaining > 0) break;
          this.pendingSnapshot = null;
          this._applySnapshot(pending.cameras);
          break;
        }
        const next = this._cameras.filter(
          (c) => c.flightId !== msg.content.state.flightId,
        );
        next.push(msg.content.state);
        next.sort((a, b) => a.flightId - b.flightId);
        this.commandCentres.record(msg.content.state.flightId, msg.content.state.commandCentre);
        this._cameras = next;
        const handle = this.getOrCreateHandle(msg.content.state.flightId);
        const reattached = attachChanged(handle.state, msg.content.state);
        const resurrected = handle._setState(msg.content.state);
        if (resurrected) this._resourceHandle(msg.content.state.flightId);
        if (resurrected || reattached) this._renewEncodedTransform(msg.content.state.flightId);
        this.emit("cameras-change", this._cameras);
        this._sendHeldAnchors();
        break;
      }
      case "slot-map": {
        const { mid, flightId } = msg.content;
        const prev = this.flightByMid.get(mid);
        if (flightId == null) {
          // Slot freed — the camera that held it loses its stream.
          if (prev !== undefined) {
            this.getOrCreateHandle(prev)._setMediaStream(null);
          }
          this.flightByMid.delete(mid);
          this.handFreedSlotToWaiting();
        } else {
          this.clearWaitingForSlot(flightId);
          // A switch (a different camera held this slot) clears the old one.
          if (prev !== undefined && prev !== flightId) {
            this.getOrCreateHandle(prev)._setMediaStream(null);
          }
          this.flightByMid.set(mid, flightId);
          const track = this.trackByMid.get(mid);
          if (track) {
            /* The sidecar announces every binding again when the connection
            recovers. A feed already playing that track keeps its source:
            swapping it would drop the held frame and flash static. */
            const handle = this.getOrCreateHandle(flightId);
            if (!handle._isSourcedFrom(track)) {
              handle._setMediaStream(new MediaStream([track]));
            }
          } else {
            // Bound to a slot but the track hasn't arrived yet (the
            // camera-switch gap). Drive the incoming handle sourceless so it
            // shows static rather than going blank until the track lands.
            this.getOrCreateHandle(flightId)._setMediaStream(null);
          }
        }
        this._syncEncodedTransforms();
        break;
      }
      case "adaptive-shed":
        this.emit("adaptive-shed", msg.content);
        break;
      case "error":
        if (
          msg.content.code === ErrorCode.NoFreeSlot &&
          msg.content.flightId != null &&
          this.dynamicMode &&
          this.onNoFreeSlot(msg.content.flightId)
        ) {
          break;
        }
        this.emit("error", msg.content);
        break;
      case "ping":
        /*
         * A ping can race a closing channel (disconnect mid-flight); a
         * pong that can't be delivered is moot, not an error.
         */
        void this._send({ type: "pong" }).catch(() => {});
        this.emit("ping", undefined);
        break;
      case "settings-state":
        this._throttleMainScreen = msg.content.throttleMainScreen;
        /*
         * Capture clock: null captureUt means "no clock" (absent field).
         * Epoch is retained across pushes that omit it (only a present
         * value updates it); warpRate falls back to 1 when absent.
         */
        this._captureUt = msg.content.captureUt ?? null;
        this._captureUtAt = monotonicNowMs();
        if (msg.content.captureEpoch != null) {
          this._captureEpoch = msg.content.captureEpoch;
        }
        this._warpRate = msg.content.timeWarpRate ?? 1;
        this.emit("settings-change", msg.content);
        break;
      case "frame-stamp":
        this.frameStamps.record(msg.content);
        this.frameHistory.record(msg.content);
        break;
      case "scene-state-changed":
        this._inFlight = msg.content.inFlight;
        /* Re-drive every handle: out of flight, sourceless feeds go blank
           instead of showing static. Scene state carries no per-camera
           state, so nudge each handle to re-evaluate its presentation. */
        for (const handle of this.handles.values()) {
          handle._refreshSceneState();
        }
        this.emit("scene-change", this._inFlight);
        break;
    }
  }

  /**
   * Re-source a resurrected camera's handle from the track the client still
   * holds for its slot. Called when {@link KerbcastCameraHandle._setState}
   * reports a Destroyed -> Active transition: the sidecar rebinds the same
   * track without a new slot-map, so nothing else drives the handle back out
   * of the sourceless (static) state it entered on destroy.
   */
  private _resourceHandle(flightId: number): void {
    for (const [mid, boundFlightId] of this.flightByMid) {
      if (boundFlightId !== flightId) continue;
      const track = this.trackByMid.get(mid);
      if (track) {
        /* A feed already playing that track keeps its source: swapping it would drop the held frame and flash static. */
        const handle = this.getOrCreateHandle(flightId);
        if (!handle._isSourcedFrom(track)) handle._setMediaStream(new MediaStream([track]));
      }
      return;
    }
  }

  /**
   * Give a camera whose source restarted (back in the list after a scene
   * change, back from destroyed, or reattached in place) a fresh encoded
   * transform on the receiver it kept. Its frames now come from a new encoder
   * on a new capture timeline, and an instance built for the old one can hold
   * them back for good. The new instance replaces the old in one step, so no
   * frame skips the transform.
   */
  private _renewEncodedTransform(flightId: number): void {
    for (const [mid, installed] of this.transformInstalls) {
      if (installed.flightId !== flightId) continue;
      this.noteDrain(mid, installed);
      this.transformInstalls.delete(mid);
    }
    this._syncEncodedTransforms();
  }

  private setState(state: KerbcastConnectionState): void {
    if (this._state === state) return;
    this._state = state;
    this.emit("state-change", state);
  }

  /**
   * Inbound RTP video statistics keyed by flight ID. Returns an empty map
   * when not connected or when the transport does not implement `getStats`.
   *
   * Iterates the stats report and matches `inbound-rtp` + `kind === "video"`
   * entries to cameras. Legacy mode matches by `trackIdentifier`; dynamic
   * mode falls back to the `mid` field when `trackIdentifier` is absent
   * (modern browsers include it; some implementations omit it).
   */
  async inboundVideoStats(): Promise<Map<number, InboundVideoStats>> {
    const result = new Map<number, InboundVideoStats>();
    if (!this.peer?.getStats) return result;

    let report: RTCStatsReport;
    try {
      report = await this.peer.getStats();
    } catch {
      return result;
    }

    report.forEach((raw) => {
      if (raw.type !== "inbound-rtp" || raw.kind !== "video") return;
      const entry = raw as {
        type: string;
        kind: string;
        trackIdentifier?: string;
        mid?: string;
        packetsReceived?: number;
        bytesReceived?: number;
        framesReceived?: number;
        framesDecoded?: number;
        jitter?: number;
        framesPerSecond?: number;
      };

      // Resolve the flightId: trackIdentifier wins (legacy path populated it),
      // mid fallback covers dynamic mode and browsers that omit trackIdentifier.
      let flightId: number | undefined;
      if (entry.trackIdentifier) {
        flightId = this.flightByTrackId.get(entry.trackIdentifier);
      }
      if (flightId === undefined && entry.mid) {
        flightId = this.flightByMid.get(entry.mid);
      }
      if (flightId === undefined) return;

      result.set(flightId, {
        packetsReceived: entry.packetsReceived ?? 0,
        bytesReceived: entry.bytesReceived ?? 0,
        framesReceived: entry.framesReceived,
        framesDecoded: entry.framesDecoded,
        jitter: entry.jitter,
        framesPerSecond: entry.framesPerSecond,
      });
    });

    return result;
  }

  /** The encoded-transform path this client's next peer will use, or null when it has none. */
  get encodedTransformPath(): EncodedTransformPath | null {
    return this.transport.encodedTransformPath?.(this.peerOptions()) ?? null;
  }

  private peerOptions(): KerbcastPeerOptions {
    return {
      encodedTransforms: this.cfg.encodedTransforms === true,
      ...(this.cfg.recordingTiming === "capture" ? { frameTaps: true } : {}),
    };
  }

  /** Internal: throws unless {@link KerbcastCameraHandle.setEncodedTransform} can work here. */
  _assertEncodedTransforms(): void {
    if (this.encodedTransformPath && this.cfg.encodedTransforms === true) return;
    const optIn = this.transport.encodedTransformPath?.({ encodedTransforms: true });
    if (optIn && this.cfg.encodedTransforms !== true) {
      throw new Error(
        "[kerbcast] encoded transforms need the client constructed with encodedTransforms: true",
      );
    }
    throw new Error(
      "[kerbcast] encoded transforms are unavailable: this browser has neither RTCRtpScriptTransform nor createEncodedStreams",
    );
  }

  /**
   * Internal: bring each receiver's installed transform in line with the
   * camera now bound to it. A receiver whose camera or factory changed loses
   * its instance; a bound camera with a factory gets a fresh one.
   *
   * On the script path every transform handed to a receiver, a camera's or
   * the passthrough it goes back to, is watched once packets arrive on that
   * receiver, and renewed only if no frame decodes; see
   * {@link TRANSFORM_HEAL_POLL_MS}. Chromium wires a script transform to its
   * receiver only as frames reach it, so on a receiver with no packets coming
   * in (a relaunch gap, a slot about to be fed) an instance built before it
   * was handed over can lose to the previous one's late wiring, and then
   * receives nothing. One built while packets flow is wired at once. Renewing
   * a wired instance would drop the frames it holds, which for a delay is
   * seconds of picture.
   */
  _syncEncodedTransforms(): void {
    this.syncFrameTaps();
    /* Every setEncodedTransform syncs, so each hold a host gives is read here before it can change again. */
    for (const install of this.transformInstalls.values()) this.installHoldMs(install);
    const peer = this.peer;
    const path = this.peerTransformPath;
    if (!peer?.setReceiverTransform || !path) return;
    const bindings = this.dynamicMode ? this.flightByMid : this.legacyFlightByMid;
    for (const [mid, installed] of this.transformInstalls) {
      const flightId = bindings.get(mid);
      const factory = flightId === undefined ? null : (this.handles.get(flightId)?._encodedTransform ?? null);
      if (flightId === installed.flightId && factory === installed.factory) continue;
      this.noteDrain(mid, installed);
      this.transformInstalls.delete(mid);
      if (this.setReceiverTransform(peer, mid, installed.flightId, null) && path === "script") {
        this.awaitHeal(mid, null, installed.flightId);
      }
    }
    for (const [mid, flightId] of bindings) {
      if (this.transformInstalls.has(mid)) continue;
      const factory = this.handles.get(flightId)?._encodedTransform;
      if (!factory) continue;
      const install = { flightId, factory, holdMs: this.handles.get(flightId)?._encodedTransformHoldMs, maxHoldMs: 0 };
      this.installHoldMs(install);
      if (this.installTransform(peer, mid, install, path) && path === "script") {
        this.awaitHeal(mid, install, flightId);
      }
    }
    this.syncHoldSampler();
  }

  /** Reads every install's hold on a timer while any script-path install has its hold as a function; see {@link TRANSFORM_HOLD_SAMPLE_MS}. */
  private syncHoldSampler(): void {
    const sampled =
      this.peerTransformPath === "script" &&
      [...this.transformInstalls.values()].some((install) => typeof this.installHold(install) === "function");
    if (sampled && this.holdSampler === null) {
      this.holdSampler = setInterval(() => {
        for (const install of this.transformInstalls.values()) this.installHoldMs(install);
      }, TRANSFORM_HOLD_SAMPLE_MS);
    } else if (!sampled && this.holdSampler !== null) {
      clearInterval(this.holdSampler);
      this.holdSampler = null;
    }
  }

  /** Builds `install`'s instance and hands it to `mid`, recording it there only once it is in place. */
  private installTransform(
    peer: KerbcastPeer,
    mid: string,
    install: TransformInstall,
    path: EncodedTransformPath,
  ): boolean {
    let transform: EncodedTransform;
    try {
      transform = install.factory({ flightId: install.flightId, path });
    } catch (err) {
      this.emitTransformError(install.flightId, err);
      return false;
    }
    if (!this.setReceiverTransform(peer, mid, install.flightId, transform)) return false;
    this.transformInstalls.set(mid, install);
    return true;
  }

  /**
   * Records that `install`'s instance is leaving `mid`, where it can stay
   * wired and go on decoding what it held for up to its hold. Read before the
   * install is dropped, while its hold can still be read.
   */
  private noteDrain(mid: string, install: TransformInstall): void {
    if (this.peerTransformPath !== "script") return;
    const holdMs = this.installHoldMs(install);
    const until = holdMs === null ? Infinity : monotonicNowMs() + Math.max(holdMs, install.maxHoldMs);
    this.drainUntil.set(mid, Math.max(this.drainUntil.get(mid) ?? 0, until));
  }

  /** Watch `mid` for arriving packets, to renew its transform if they come with no frame decoded. */
  private awaitHeal(mid: string, install: TransformInstall | null, flightId: number): void {
    if (!this.peer?.getStats) return;
    this.pendingHeals.set(mid, {
      install,
      flightId,
      seq: ++this.healSeq,
      last: null,
      lastAt: 0,
      renewals: 0,
      stalledSince: null,
      packetsSince: null,
      packetsAt: null,
      since: monotonicNowMs(),
    });
    this.armHealTimer();
    void this.pollHeals();
  }

  /** Whether `pending`'s transform is still the one on `mid`. */
  private healStillOwed(mid: string, pending: PendingHeal): boolean {
    return pending.install
      ? this.transformInstalls.get(mid) === pending.install
      : !this.transformInstalls.has(mid);
  }

  /** Polls quickly while any receiver is in its first minute of waiting, slowly once all have waited longer. */
  private armHealTimer(): void {
    const now = monotonicNowMs();
    let fast = false;
    for (const pending of this.pendingHeals.values()) {
      if (now - pending.since < TRANSFORM_HEAL_FAST_FOR_MS) fast = true;
    }
    const ms = fast ? TRANSFORM_HEAL_POLL_MS : TRANSFORM_HEAL_SLOW_POLL_MS;
    if (this.healTimer !== null) {
      if (this.healTimerMs === ms) return;
      clearInterval(this.healTimer);
    }
    this.healTimerMs = ms;
    this.healTimer = setInterval(() => void this.pollHeals(), ms);
  }

  private async pollHeals(): Promise<void> {
    const peer = this.peer;
    for (const [mid, pending] of this.pendingHeals) {
      if (!this.healStillOwed(mid, pending)) this.pendingHeals.delete(mid);
    }
    if (this.pendingHeals.size === 0 || !peer?.getStats) {
      this.stopHeals();
      return;
    }
    this.armHealTimer();
    if (this.healPolling === peer) return;
    this.healPolling = peer;
    const requested = this.healSeq;
    let timeout: ReturnType<typeof setTimeout> | undefined;
    let report: RTCStatsReport;
    try {
      report = await Promise.race([
        peer.getStats(),
        new Promise<never>((_, reject) => {
          timeout = setTimeout(() => reject(new Error("stats timed out")), TRANSFORM_HEAL_STATS_TIMEOUT_MS);
        }),
      ]);
    } catch {
      return;
    } finally {
      clearTimeout(timeout);
      if (this.healPolling === peer) this.healPolling = null;
    }
    if (this.peer !== peer) return;
    /* Keyed by stats id, so an old stream's entry lingering on the same mid cannot mask the new one's counts. */
    const countsByMid = new Map<string, Map<string, ReceiverCounts>>();
    report.forEach((raw) => {
      const entry = raw as {
        id?: string;
        type?: string;
        kind?: string;
        mid?: string;
        packetsReceived?: number;
        framesDecoded?: number;
      };
      if (entry.type !== "inbound-rtp" || entry.kind !== "video" || entry.mid === undefined) return;
      let byId = countsByMid.get(entry.mid);
      if (!byId) countsByMid.set(entry.mid, (byId = new Map()));
      byId.set(entry.id ?? "", { packets: entry.packetsReceived ?? 0, frames: entry.framesDecoded ?? 0 });
    });
    const now = monotonicNowMs();
    for (const [mid, pending] of this.pendingHeals) {
      if (pending.seq > requested || !this.healStillOwed(mid, pending)) continue;
      const counts = countsByMid.get(mid) ?? new Map<string, ReceiverCounts>();
      const last = pending.last;
      const lastAt = pending.lastAt;
      pending.last = counts;
      pending.lastAt = now;
      if (last === null) continue;
      /* Against the previous sample, so each rise is what arrived or decoded since the last poll. */
      const rose = (key: keyof ReceiverCounts): boolean =>
        [...counts].some(([id, c]) => c[key] > (last.get(id)?.[key] ?? 0));
      const packetsRose = rose("packets");
      const holdMs = this.healHoldMs(pending);
      const drainUntil = this.drainUntil.get(mid) ?? 0;
      if (holdMs === null || drainUntil === Infinity) {
        /*
         * With no hold given, a transform still filling its hold looks the
         * same as one left without frames. And an instance with no hold given
         * that this one replaced may drain decoded frames for any length of
         * time. Either way it is renewed once when packets first arrive,
         * unchecked, then watched only if its own hold is known.
         */
        if (!packetsRose) continue;
        if (pending.renewals > 0) this.pendingHeals.delete(mid);
        else this.renewForHeal(peer, mid, pending);
        continue;
      }
      /*
       * An instance replaced here can stay wired and drain what it held after
       * the handover, so decoded frames show this one is wired only once that
       * drain was over before the previous sample, since a rise counts every
       * frame decoded since it, and packets have reached this one for its own
       * hold. Renewing a wired one would only drop the frames it holds.
       */
      const packetsSince = pending.packetsSince;
      if (lastAt >= drainUntil && packetsSince !== null && now - packetsSince >= holdMs && rose("frames")) {
        this.pendingHeals.delete(mid);
        continue;
      }
      if (!packetsRose) {
        /* A camera at a few frames a second leaves polls with no packet between those with one. */
        if (pending.packetsAt === null || now - pending.packetsAt > TRANSFORM_HEAL_IDLE_MS) pending.stalledSince = null;
        continue;
      }
      pending.packetsSince ??= now;
      pending.packetsAt = now;
      pending.stalledSince ??= now;
      /* Frames cannot count until the drain is over, so the stall runs at least that long. */
      const stallMs = TRANSFORM_HEAL_STALL_MS * 2 ** Math.max(0, pending.renewals - 1);
      const waitMs = Math.max(drainUntil - pending.stalledSince, holdMs) + stallMs;
      if (now - pending.stalledSince < waitMs) continue;
      this.renewForHeal(peer, mid, pending);
    }
    if (this.pendingHeals.size === 0) this.stopHeals();
    else this.armHealTimer();
  }

  /**
   * How long `pending`'s transform may hold frames before a lack of decoded
   * ones means it lost them, read now: 0 for the passthrough, null for a
   * camera's transform whose host gave no usable hold, which is renewed once
   * unchecked.
   */
  private healHoldMs(pending: PendingHeal): number | null {
    return pending.install ? this.installHoldMs(pending.install) : 0;
  }

  /** The host's current hold for `install` while its factory is still the camera's, else the one it was installed with. */
  private installHold(install: TransformInstall): EncodedTransformOptions["holdMs"] {
    const handle = this.handles.get(install.flightId);
    return handle?._encodedTransform === install.factory ? handle._encodedTransformHoldMs : install.holdMs;
  }

  /** `install`'s hold read now, in ms, also kept as its longest when it is. */
  private installHoldMs(install: TransformInstall): number | null {
    const hold = this.installHold(install);
    if (hold === undefined) return null;
    let ms: number;
    try {
      ms = typeof hold === "function" ? hold() : hold;
    } catch {
      return null;
    }
    if (!Number.isFinite(ms) || ms < 0) return null;
    install.maxHoldMs = Math.max(install.maxHoldMs, ms);
    return ms;
  }

  /** Renews `pending`'s transform on `mid` and asks for a keyframe, then watches for frames decoding unless out of retries. */
  private renewForHeal(peer: KerbcastPeer, mid: string, pending: PendingHeal): void {
    const bindings = this.dynamicMode ? this.flightByMid : this.legacyFlightByMid;
    const bound = bindings.get(mid);
    const renewed = pending.install
      ? this.installTransform(peer, mid, pending.install, "script")
      : this.setReceiverTransform(peer, mid, bound ?? pending.flightId, null);
    /*
     * A renewal hands over from this transform's own instance, after any
     * bounded drain was waited out, so its own longest hold bounds the drain
     * from here. That also ends an unbounded one: built while packets flow,
     * the new instance is wired at once, so a drain still running can only
     * end a watch it no longer needs. A renewal that failed leaves the
     * instance it would have replaced, bounded the same way, and the receiver
     * goes unchecked from here, so an unbounded drain would only force a
     * renewal of whatever is wired at the next handover.
     */
    const holdMs = this.healHoldMs(pending);
    const drainMs = pending.install ? Math.max(holdMs ?? 0, pending.install.maxHoldMs) : 0;
    this.drainUntil.set(mid, monotonicNowMs() + drainMs);
    if (!renewed || ++pending.renewals > TRANSFORM_HEAL_RETRIES || holdMs === null) {
      this.pendingHeals.delete(mid);
    } else {
      pending.seq = ++this.healSeq;
      pending.last = null;
      pending.stalledSince = null;
      pending.packetsSince = null;
      pending.packetsAt = null;
      pending.since = monotonicNowMs();
    }
    /* The instance it replaced may have taken the keyframe with it. */
    if (renewed && bound !== undefined) this.handles.get(bound)?.requestKeyframe().catch(() => {});
  }

  private stopHeals(): void {
    this.pendingHeals.clear();
    if (this.healTimer !== null) {
      clearInterval(this.healTimer);
      this.healTimer = null;
    }
  }

  /** Internal: whether {@link KerbcastCameraHandle.tapEncodedFrames} can copy `flightId`'s frames on this client. */
  _frameTapAvailable(flightId: number): boolean {
    const path = this.encodedTransformPath;
    if (!path || this.cfg.recordingTiming !== "capture") return false;
    /* A script-path receiver holds one transform: the camera's own replaces the copying passthrough. */
    return !(path === "script" && this.handles.get(flightId)?._encodedTransform);
  }

  /** Internal: a camera's own transform is about to replace the passthrough its taps copy from. */
  _warnTapsDisplaced(flightId: number): void {
    if (this.encodedTransformPath !== "script") return;
    this.emit("error", {
      message: `[kerbcast] camera ${flightId}'s encoded transform stops its frame copies until it is removed; a capture-timed recording of it misses those frames`,
      source: ErrorSource.Client,
      flightId,
    });
  }

  /** Points each receiver's frame copy at the camera bound to it, while that camera has taps. */
  private syncFrameTaps(): void {
    const peer = this.peer;
    if (!peer?.setReceiverTap) return;
    const bindings = this.dynamicMode ? this.flightByMid : this.legacyFlightByMid;
    for (const [mid, flightId] of this.tapInstalls) {
      if (bindings.get(mid) === flightId && this.handles.get(flightId)?._frameTaps.size) continue;
      this.tapInstalls.delete(mid);
      this.setReceiverTap(peer, mid, flightId, null);
    }
    for (const [mid, flightId] of bindings) {
      if (this.tapInstalls.has(mid) || !this.handles.get(flightId)?._frameTaps.size) continue;
      const deliver = (frame: EncodedFrameCopy): void => {
        this.handles.get(flightId)?._deliverFrame(frame);
      };
      if (this.setReceiverTap(peer, mid, flightId, deliver)) this.tapInstalls.set(mid, flightId);
    }
  }

  private setReceiverTap(
    peer: KerbcastPeer,
    mid: string,
    flightId: number,
    onFrame: ((frame: EncodedFrameCopy) => void) | null,
  ): boolean {
    try {
      peer.setReceiverTap?.(mid, onFrame);
      return true;
    } catch (err) {
      this.emit("error", {
        message: `[kerbcast] frame copy for camera ${flightId}: ${err instanceof Error ? err.message : String(err)}`,
        source: ErrorSource.Client,
        flightId,
      });
      return false;
    }
  }

  private clearEncodedTransforms(): void {
    this.stopHeals();
    this.drainUntil.clear();
    this.tapInstalls.clear();
    const peer = this.peer;
    for (const [mid, installed] of this.transformInstalls) {
      if (peer) this.setReceiverTransform(peer, mid, installed.flightId, null);
    }
    this.transformInstalls.clear();
    this.syncHoldSampler();
  }

  private setReceiverTransform(
    peer: KerbcastPeer,
    mid: string,
    flightId: number,
    transform: EncodedTransform | null,
  ): boolean {
    try {
      peer.setReceiverTransform?.(mid, transform);
      return true;
    } catch (err) {
      this.emitTransformError(flightId, err);
      return false;
    }
  }

  private emitTransformError(flightId: number, err: unknown): void {
    this.emit("error", {
      message: `[kerbcast] encoded transform for camera ${flightId}: ${err instanceof Error ? err.message : String(err)}`,
      source: ErrorSource.Client,
      flightId,
    });
  }

  private tearDownStreams(): void {
    this.flightByTrackId.clear();
    for (const handle of this.handles.values()) {
      handle._teardown();
    }
  }
}
