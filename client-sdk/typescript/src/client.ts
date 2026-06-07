import type {
  AdaptiveShedPayload,
  CameraState,
  ClientMessage,
  ErrorPayload,
  Layer,
  ServerMessage,
} from "./__generated__/types";
import { CameraLifecycle, ErrorSource } from "./__generated__/types";
import { type NoisePipeline, tryCreateNoisePipeline } from "./noise";

/** Intensity the static runs at when a camera has no live source (signal lost). */
const SOURCELESS_INTENSITY = 1.0;
/** Floor applied to the degrade-driven intensity of a live feed. */
const LIVE_INTENSITY_FLOOR = 0.05;

/** Controls the digital-static noise overlay baked into `cam.mediaStream`. */
export interface NoiseConfig {
  /** When false, the pipeline is bypassed and `mediaStream` is the raw WebRTC track. */
  enabled: boolean;
}

/**
 * Per-camera handle returned from {@link KerbcamClient.camera}.
 * Stable identity per flight ID for the lifetime of the client.
 */
export interface KerbcamCameraHandle {
  readonly flightId: number;
  readonly state: CameraState | null;
  readonly mediaStream: MediaStream | null;

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
  requestKeyframe(): Promise<void>;

  /**
   * Override noise settings for this camera only. Takes precedence over the
   * client-level default in {@link KerbcamClientConfig.noise}.
   */
  configure(options: { noise?: Partial<NoiseConfig> }): void;

  on<K extends keyof KerbcamCameraEvents>(
    event: K,
    handler: (data: KerbcamCameraEvents[K]) => void,
  ): () => void;
}

/** Event payloads emitted by a {@link KerbcamCameraHandle}. */
export interface KerbcamCameraEvents {
  /** Latest `CameraState` from the sidecar's `camera-state-changed`. */
  change: CameraState;
  /** Track arrival or teardown for this camera. `null` on disconnect. */
  stream: MediaStream | null;
}

/** Configuration for {@link KerbcamClient} construction. */
export interface KerbcamClientConfig {
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
   * ICE gathering timeout in milliseconds. Passed to the default
   * {@link BrowserKerbcamTransport}; ignored when a custom transport is
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
  }) => Promise<{ sdp: string; cameras: number[] }>;
}

/** WebRTC connection state surface. */
export type KerbcamConnectionState =
  | "disconnected"
  | "connecting"
  | "connected"
  | "failed";

/** Camera summary returned by {@link KerbcamClient.discover}. */
export interface DiscoveredCamera {
  flightId: number;
  partName: string;
  partTitle: string;
  cameraName: string;
  vesselName: string;
  maxWidth: number;
  maxHeight: number;
  supportsZoom: boolean;
  fov: number;
  fovMin: number;
  fovMax: number;
  supportsPan: boolean;
}

/** Event payloads emitted by a {@link KerbcamClient}. */
export interface KerbcamClientEvents {
  "state-change": KerbcamConnectionState;
  "cameras-change": CameraState[];
  "adaptive-shed": AdaptiveShedPayload;
  error: ErrorPayload;
  /**
   * Fired whenever a `ping` keepalive arrives from the sidecar.
   * Consumers can use this to reset their own staleness watchdogs
   * without intercepting at the transport layer.
   */
  ping: undefined;
}

// ---------------------------------------------------------------------------
// Transport abstraction for tests
// ---------------------------------------------------------------------------

/**
 * Pluggable WebRTC transport. Production code uses the default
 * (browser `RTCPeerConnection`). Tests substitute an in-memory
 * implementation to exercise the state machine without a real peer.
 */
export interface KerbcamTransport {
  createPeer(iceServers: RTCIceServer[]): KerbcamPeer;
}

export interface KerbcamPeer {
  addRecvOnlyTransceiver(): void;
  createDataChannel(label: string): KerbcamDataChannel;
  onTrack(
    handler: (track: MediaStreamTrack, idx: number, mid: string) => void,
  ): void;
  onStateChange(handler: (state: KerbcamConnectionState) => void): void;
  createOffer(): Promise<string>;
  setLocalDescription(sdp: string): Promise<void>;
  setRemoteAnswer(sdp: string): Promise<void>;
  waitForIceComplete(): Promise<void>;
  localSdp(): string;
  close(): void;
}

export interface KerbcamDataChannel {
  send(payload: string): void;
  onOpen(handler: () => void): void;
  onMessage(handler: (raw: string) => void): void;
  onClose(handler: () => void): void;
}

// ---------------------------------------------------------------------------
// Default transport: browser RTCPeerConnection
// ---------------------------------------------------------------------------

/** Options for {@link BrowserKerbcamTransport}. */
export interface BrowserKerbcamTransportOptions {
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

export class BrowserKerbcamTransport implements KerbcamTransport {
  private readonly opts: Required<BrowserKerbcamTransportOptions>;

  constructor(opts: BrowserKerbcamTransportOptions = {}) {
    this.opts = { iceGatheringTimeoutMs: opts.iceGatheringTimeoutMs ?? 2000 };
  }

  createPeer(iceServers: RTCIceServer[]): KerbcamPeer {
    const pc = new RTCPeerConnection({ iceServers });
    const { iceGatheringTimeoutMs } = this.opts;
    let trackIdx = 0;
    let onTrack:
      | ((t: MediaStreamTrack, idx: number, mid: string) => void)
      | null = null;
    let onStateChange: ((s: KerbcamConnectionState) => void) | null = null;

    pc.ontrack = (ev) => {
      // ev.transceiver.mid is the stable m-line id the sidecar keys its
      // SlotMap on; it's set by the time ontrack fires (post-answer).
      onTrack?.(ev.track, trackIdx++, ev.transceiver.mid ?? "");
    };
    pc.onconnectionstatechange = () => {
      onStateChange?.(mapPeerState(pc.connectionState));
    };

    return {
      addRecvOnlyTransceiver: () => {
        pc.addTransceiver("video", { direction: "recvonly" });
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
      close: () => {
        pc.close();
      },
    };
  }
}

function wrapBrowserDataChannel(dc: RTCDataChannel): KerbcamDataChannel {
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

function mapPeerState(state: RTCPeerConnectionState): KerbcamConnectionState {
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
  extends TypedEmitter<KerbcamCameraEvents>
  implements KerbcamCameraHandle
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
  private readonly client: KerbcamClient;

  constructor(flightId: number, client: KerbcamClient) {
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

  configure(options: { noise?: Partial<NoiseConfig> }): void {
    this._noiseOverride = options.noise ?? null;
    // Re-evaluate with the new noise setting: rebuild from whatever source
    // state we're currently in (live, sourceless, or torn down).
    this._applySource(this._rawStream);
  }

  /** Internal — called by the client when CameraState pushes arrive. */
  _setState(state: CameraState): void {
    const prevDestroyed = this._state?.lifecycle === CameraLifecycle.Destroyed;
    this._state = state;
    this.emit("change", state);

    // A destroyed camera stops forwarding frames but its track never ends, so
    // nothing else drives the handle into the sourceless state — do it here.
    if (state.lifecycle === CameraLifecycle.Destroyed) {
      if (!this._sourceless || !prevDestroyed) this._applySource(null);
      return;
    }

    // Live feed: degrade drives intensity. Don't touch a sourceless pipeline,
    // which is pinned at full static.
    if (!this._sourceless) {
      this._noisePipeline?.setIntensity(this._liveIntensity());
    }
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

  /**
   * Drive the persistent pipeline to a given source (or sourceless static when
   * `raw` is null). Creates the pipeline lazily on first need and reuses it
   * thereafter so the output stream stays stable across source swaps.
   */
  private _applySource(raw: MediaStream | null): void {
    this._rawStream = raw;
    this._sourceless = raw === null;

    const noiseEnabled = this.client._resolveNoise(this._noiseOverride);

    if (noiseEnabled) {
      // Reuse an existing pipeline; otherwise try to create one.
      if (!this._noisePipeline) {
        const initial = raw ? this._liveIntensity() : SOURCELESS_INTENSITY;
        this._noisePipeline = tryCreateNoisePipeline(raw, initial);
      } else {
        this._noisePipeline.setSource(raw);
      }

      const pipeline = this._noisePipeline;
      if (pipeline) {
        pipeline.setIntensity(raw ? this._liveIntensity() : SOURCELESS_INTENSITY);
        this._setOutput(pipeline.processedStream);
        return;
      }
    } else if (this._noisePipeline) {
      // Noise just got disabled — drop the pipeline and fall through to raw.
      this._noisePipeline.destroy();
      this._noisePipeline = null;
    }

    // Noise disabled, or captureStream unavailable (no pipeline creatable):
    // expose the raw stream directly, or null when there's no source.
    this._setOutput(raw);
  }

  private _setOutput(stream: MediaStream | null): void {
    this._mediaStream = stream;
    this.emit("stream", stream);
  }

  private _liveIntensity(): number {
    return Math.max(LIVE_INTENSITY_FLOOR, this._state?.degradeLevel ?? 0);
  }

  /**
   * Internal — genuine teardown (client disconnect / peer failure). Destroys
   * the persistent pipeline, stops its rAF loop, and nulls the stream.
   */
  _teardown(): void {
    this._noisePipeline?.destroy();
    this._noisePipeline = null;
    this._rawStream = null;
    this._sourceless = false;
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

  async requestKeyframe(): Promise<void> {
    await this.client._send({
      type: "request-keyframe",
      content: { flightId: this.flightId },
    });
  }
}

// ---------------------------------------------------------------------------
// KerbcamClient
// ---------------------------------------------------------------------------

/**
 * High-level kerbcam sidecar client. Owns the WebRTC peer + the
 * `kerbcam-control` data channel + the per-camera registry + the
 * `MediaStream`s for each subscribed camera.
 *
 * Usage:
 *
 * ```ts
 * const client = new KerbcamClient({ host: "192.168.1.74", port: 8088 });
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
export class KerbcamClient extends TypedEmitter<KerbcamClientEvents> {
  private readonly cfg: KerbcamClientConfig;
  private readonly transport: KerbcamTransport;
  private peer: KerbcamPeer | null = null;
  private control: KerbcamDataChannel | null = null;
  private _state: KerbcamConnectionState = "disconnected";
  private _cameras: CameraState[] = [];
  private readonly handles = new Map<number, CameraHandle>();
  private requestedOrder: number[] = [];
  /** Set when `connect` is given an explicit slot pool. Switches track
   *  routing from legacy index order to mid-keyed dynamic slots. */
  private dynamicMode = false;
  /** Dynamic mode: transceiver mid -> the live track on that slot. */
  private readonly trackByMid = new Map<string, MediaStreamTrack>();
  /** Dynamic mode: transceiver mid -> the camera bound to that slot
   *  (populated by SlotMap — initial bindings announced on Hello). */
  private readonly flightByMid = new Map<string, number>();
  /** Sidecar version reported on `Hello`; null before handshake. */
  private _sidecarVersion: string | null = null;
  private _encoderBackend: string | null = null;

  constructor(cfg: KerbcamClientConfig, transport?: KerbcamTransport) {
    super();
    this.cfg = cfg;
    this.transport =
      transport ??
      new BrowserKerbcamTransport({
        iceGatheringTimeoutMs: cfg.iceGatheringTimeoutMs,
      });
  }

  get state(): KerbcamConnectionState {
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
   * Pre-handshake camera discovery via the sidecar's HTTP `/cameras`
   * endpoint. Useful when the consumer wants to pick which cameras
   * to subscribe to before opening a peer connection.
   */
  async discover(): Promise<DiscoveredCamera[]> {
    const res = await fetch(`http://${this.cfg.host}:${this.cfg.port}/cameras`);
    if (!res.ok) throw new Error(`/cameras returned ${res.status}`);
    const body = (await res.json()) as { cameras: DiscoveredCamera[] };
    return body.cameras;
  }

  /**
   * Open the WebRTC peer and subscribe to cameras. Idempotent: calling
   * `connect` while already connected disconnects first.
   *
   * Two modes:
   * - **Legacy** (no `opts.slots`): one transceiver per requested camera;
   *   an empty array subscribes to every currently-known camera. Tracks
   *   route by m-line index. Unchanged behaviour.
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
    this.trackByMid.clear();
    this.flightByMid.clear();

    const peer = this.transport.createPeer(
      this.cfg.iceServers ?? [{ urls: "stun:stun.l.google.com:19302" }],
    );
    this.peer = peer;

    // Slot-pool size = the recv-only transceivers we offer. Dynamic mode
    // uses the explicit pool size (spares subscribable at runtime); legacy
    // uses one per requested camera (>=1).
    const slotCount = this.dynamicMode
      ? Math.max(opts.slots ?? 0, 1)
      : Math.max(this.requestedOrder.length, 1);
    for (let i = 0; i < slotCount; i++) {
      peer.addRecvOnlyTransceiver();
    }

    peer.onTrack((track, idx, mid) => this.handleTrack(track, idx, mid));

    peer.onStateChange((s) => {
      this.setState(s);
      if (s === "disconnected" || s === "failed") {
        this.tearDownStreams();
      }
    });

    this.control = peer.createDataChannel("kerbcam-control");
    this.control.onOpen(() => {
      void this._send({ type: "hello" });
    });
    this.control.onMessage((raw) => {
      this.handleServerMessage(raw);
    });
    this.control.onClose(() => {
      // Channel closure usually follows peer disconnect; the peer
      // state-change handler covers the tear-down. Nothing to do here.
    });

    await peer.createOffer().then((sdp) => peer.setLocalDescription(sdp));
    await peer.waitForIceComplete();

    const body: { sdp: string; cameras: number[]; slots?: number } = {
      sdp: peer.localSdp(),
      cameras: this.requestedOrder,
    };
    if (this.dynamicMode) body.slots = slotCount;

    let answer: { sdp: string; cameras: number[] };
    try {
      answer = await (this.cfg.negotiate
        ? this.cfg.negotiate(body)
        : this.httpNegotiate(body));
    } catch (err) {
      this.setState("failed");
      throw err;
    }
    await peer.setRemoteAnswer(answer.sdp);
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
  }): Promise<{ sdp: string; cameras: number[] }> {
    const res = await fetch(`http://${this.cfg.host}:${this.cfg.port}/offer`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(offer),
    });
    if (!res.ok) throw new Error(`POST /offer returned ${res.status}`);
    return (await res.json()) as { sdp: string; cameras: number[] };
  }

  disconnect(): void {
    this.peer?.close();
    this.peer = null;
    this.control = null;
    this.tearDownStreams();
    this.setState("disconnected");
  }

  /**
   * Stable per-flight-id handle. Creates a new handle on first call
   * for a given ID; returns the same handle on subsequent calls.
   */
  camera(flightId: number): KerbcamCameraHandle {
    return this.getOrCreateHandle(flightId);
  }

  /**
   * Dynamic mode only: bind a camera to a free slot on the already-connected
   * peer. The camera's stream appears on its handle once the sidecar answers
   * with a SlotMap; the sidecar emits an `error` event if no slot is free. No
   * renegotiation.
   */
  async subscribe(flightId: number): Promise<void> {
    await this._send({ type: "subscribe", content: { flightId } });
  }

  /**
   * Dynamic mode only: release a camera's slot. Its handle's `mediaStream`
   * goes null once the sidecar confirms with a SlotMap.
   */
  async unsubscribe(flightId: number): Promise<void> {
    await this._send({ type: "unsubscribe", content: { flightId } });
  }

  /**
   * Internal — resolves the effective noise enabled state for a camera,
   * merging the per-camera override (if any) with the client-level default.
   */
  _resolveNoise(override: Partial<NoiseConfig> | null): boolean {
    if (override?.enabled !== undefined) return override.enabled;
    return this.cfg.noise?.enabled !== false; // default: enabled
  }

  /**
   * Internal — sends a typed `ClientMessage` over the control
   * channel. Drops silently if the channel hasn't opened yet (and
   * logs to console). Public on the camera handle, but consumers
   * shouldn't need to call this directly.
   */
  async _send(msg: ClientMessage): Promise<void> {
    if (!this.control) {
      throw new Error("[kerbcam] control channel not open");
    }
    this.control.send(JSON.stringify(msg));
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
    this.getOrCreateHandle(flightId)._setMediaStream(new MediaStream([track]));
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
    switch (msg.type) {
      case "hello":
        this._sidecarVersion = msg.content.sidecarVersion;
        this._encoderBackend = msg.content.encoderBackend;
        break;
      case "camera-snapshot":
        this._cameras = msg.content.cameras;
        for (const cam of this._cameras) {
          this.getOrCreateHandle(cam.flightId)._setState(cam);
        }
        this.emit("cameras-change", this._cameras);
        break;
      case "camera-state-changed": {
        const next = this._cameras.filter(
          (c) => c.flightId !== msg.content.state.flightId,
        );
        next.push(msg.content.state);
        next.sort((a, b) => a.flightId - b.flightId);
        this._cameras = next;
        this.getOrCreateHandle(msg.content.state.flightId)._setState(
          msg.content.state,
        );
        this.emit("cameras-change", this._cameras);
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
        } else {
          // A switch (a different camera held this slot) clears the old one.
          if (prev !== undefined && prev !== flightId) {
            this.getOrCreateHandle(prev)._setMediaStream(null);
          }
          this.flightByMid.set(mid, flightId);
          const track = this.trackByMid.get(mid);
          if (track) {
            this.getOrCreateHandle(flightId)._setMediaStream(
              new MediaStream([track]),
            );
          } else {
            // Bound to a slot but the track hasn't arrived yet (the
            // camera-switch gap). Drive the incoming handle sourceless so it
            // shows static rather than going blank until the track lands.
            this.getOrCreateHandle(flightId)._setMediaStream(null);
          }
        }
        break;
      }
      case "adaptive-shed":
        this.emit("adaptive-shed", msg.content);
        break;
      case "error":
        this.emit("error", msg.content);
        break;
      case "ping":
        void this._send({ type: "pong" });
        this.emit("ping", undefined);
        break;
    }
  }

  private setState(state: KerbcamConnectionState): void {
    if (this._state === state) return;
    this._state = state;
    this.emit("state-change", state);
  }

  private tearDownStreams(): void {
    for (const handle of this.handles.values()) {
      handle._teardown();
    }
  }
}
