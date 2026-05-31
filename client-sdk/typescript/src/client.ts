import type {
  AdaptiveShedPayload,
  CameraState,
  ClientMessage,
  ErrorPayload,
  Layer,
  ServerMessage,
} from "./__generated__/types";
import { ErrorSource } from "./__generated__/types";
import { type NoisePipeline, tryCreateNoisePipeline } from "./noise";

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
   * this via `cam.configure({ noise: … })`. Noise is enabled by default.
   */
  noise?: Partial<NoiseConfig>;
  /**
   * Override how the SDP offer/answer is exchanged. Defaults to a POST to the
   * sidecar's HTTP `/offer`. A station screen injects a version that relays
   * the offer through the main screen (which can reach the sidecar), so the
   * station needs no direct sidecar address. The media itself still flows
   * peer↔sidecar (direct or via TURN) — only the handshake is brokered.
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

export class BrowserKerbcamTransport implements KerbcamTransport {
  createPeer(iceServers: RTCIceServer[]): KerbcamPeer {
    const pc = new RTCPeerConnection({ iceServers });
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
          if (pc.iceGatheringState === "complete") return resolve();
          const check = () => {
            if (pc.iceGatheringState === "complete") {
              pc.removeEventListener("icegatheringstatechange", check);
              resolve();
            }
          };
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
    if (this._rawStream) this._rebuildPipeline(this._rawStream);
  }

  /** Internal — called by the client when CameraState pushes arrive. */
  _setState(state: CameraState): void {
    this._state = state;
    this._noisePipeline?.setIntensity(Math.max(0.05, state.degradeLevel));
    this.emit("change", state);
  }

  /** Internal — called by the client when a track arrives or drops. */
  _setMediaStream(stream: MediaStream | null): void {
    this._noisePipeline?.destroy();
    this._noisePipeline = null;
    this._rawStream = stream;

    if (!stream) {
      this._mediaStream = null;
      this.emit("stream", null);
      return;
    }

    this._rebuildPipeline(stream);
  }

  private _rebuildPipeline(raw: MediaStream): void {
    this._noisePipeline?.destroy();
    this._noisePipeline = null;

    const noiseEnabled = this.client._resolveNoise(this._noiseOverride);
    const initialIntensity = Math.max(0.05, this._state?.degradeLevel ?? 0);

    if (noiseEnabled) {
      const pipeline = tryCreateNoisePipeline(raw, initialIntensity);
      if (pipeline) {
        this._noisePipeline = pipeline;
        this._mediaStream = pipeline.processedStream;
        this.emit("stream", pipeline.processedStream);
        return;
      }
    }

    // Noise disabled or captureStream unavailable — expose raw stream directly.
    this._mediaStream = raw;
    this.emit("stream", raw);
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
    this.transport = transport ?? new BrowserKerbcamTransport();
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
      handle._setMediaStream(null);
    }
  }
}
