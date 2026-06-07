import type { AdaptiveShedPayload, CameraState, ClientMessage, ServerMessage } from "../__generated__/types";
import { CameraLifecycle, ErrorSource, Layer } from "../__generated__/types";
import type {
  InboundVideoStats,
  KerbcamConnectionState,
  KerbcamDataChannel,
  KerbcamPeer,
  KerbcamTransport,
} from "../client";

export interface MockCameraInit {
  flightId: number;
  lifecycle?: CameraLifecycle;
  partName?: string;
  partTitle?: string;
  cameraName?: string;
  vesselName?: string;
  layers?: Layer[];
  operatorLayers?: Layer[];
  renderWidth?: number;
  renderHeight?: number;
  operatorWidth?: number;
  operatorHeight?: number;
  supportsZoom?: boolean;
  fov?: number;
  fovMin?: number;
  fovMax?: number;
  supportsPan?: boolean;
  panYaw?: number;
  panPitch?: number;
  panYawMin?: number;
  panYawMax?: number;
  panPitchMin?: number;
  panPitchMax?: number;
  encoderBitrateBps?: number;
  targetBitrateBps?: number;
  degradeLevel?: number;
}

function buildCamera(init: MockCameraInit): CameraState {
  return {
    flightId: init.flightId,
    lifecycle: init.lifecycle ?? CameraLifecycle.Active,
    partName: init.partName ?? `part-${init.flightId}`,
    partTitle: init.partTitle ?? `Part ${init.flightId}`,
    cameraName: init.cameraName ?? `camera-${init.flightId}`,
    vesselName: init.vesselName ?? "Test Vessel",
    layers: init.layers ?? [Layer.Near],
    operatorLayers: init.operatorLayers ?? [Layer.Near],
    renderWidth: init.renderWidth ?? 1280,
    renderHeight: init.renderHeight ?? 720,
    operatorWidth: init.operatorWidth ?? 1280,
    operatorHeight: init.operatorHeight ?? 720,
    supportsZoom: init.supportsZoom ?? true,
    fov: init.fov ?? 60,
    fovMin: init.fovMin ?? 10,
    fovMax: init.fovMax ?? 120,
    supportsPan: init.supportsPan ?? false,
    panYaw: init.panYaw ?? 0,
    panPitch: init.panPitch ?? 0,
    panYawMin: init.panYawMin ?? -90,
    panYawMax: init.panYawMax ?? 90,
    panPitchMin: init.panPitchMin ?? -90,
    panPitchMax: init.panPitchMax ?? 90,
    encoderBitrateBps: init.encoderBitrateBps ?? 0,
    targetBitrateBps: init.targetBitrateBps ?? 0,
    degradeLevel: init.degradeLevel ?? 0,
  };
}

/**
 * In-process protocol-level fake for the kerbcam sidecar.
 *
 * Owns a camera registry and speaks the full kerbcam wire protocol.
 * Use it in tests to exercise `KerbcamClient` behaviour without a real
 * sidecar or WebRTC stack.
 *
 * ```ts
 * const sidecar = new MockSidecar();
 * sidecar.addCamera({ flightId: 42 });
 *
 * vi.spyOn(globalThis, "fetch").mockImplementation(() =>
 *   Promise.resolve(MockSidecar.makeOfferResponse([42]))
 * );
 *
 * const client = new KerbcamClient({ host: "localhost", port: 8088 }, sidecar.createTransport());
 * await client.connect([42]);
 * sidecar.open();   // fires hello + camera-snapshot
 *
 * expect(client.cameras[0].flightId).toBe(42);
 * ```
 */
export class MockSidecar {
  private readonly _cameras = new Map<number, CameraState>();
  private readonly _commands: ClientMessage[] = [];

  private _openHandler: (() => void) | undefined;
  private _clientMsgHandler: ((raw: string) => void) | undefined;
  private _stateHandler: ((s: KerbcamConnectionState) => void) | undefined;
  private _onTrackHandler:
    | ((track: MediaStreamTrack, idx: number, mid: string) => void)
    | undefined;
  private _trackIdx = 0;
  /** Slot mids available for the dynamic-subscription model. Override with
   *  {@link withSlots} before connecting if a test needs a specific pool. */
  private _slotMids: string[] = ["0", "1", "2", "3"];
  /** mid -> camera currently bound to that slot. */
  private readonly _slotBindings = new Map<string, number>();
  /**
   * Per-flight inbound stats to return from the fake getStats. Set via
   * {@link setInboundStats}. Keyed by flightId.
   */
  private readonly _inboundStats = new Map<number, Partial<InboundVideoStats>>();
  /**
   * Tracks delivered by {@link deliverTrack}, keyed by mid/idx string so
   * getStats can synthesize a trackIdentifier matching what the client saw.
   */
  private readonly _deliveredTracks = new Map<string, MediaStreamTrack>();

  /** Register a camera that will appear in the `camera-snapshot` sent on `open()`. */
  addCamera(init: MockCameraInit): void {
    this._cameras.set(init.flightId, buildCamera(init));
  }

  /**
   * Returns a `KerbcamTransport` backed by this mock. Pass it as the
   * second argument to `KerbcamClient`.
   */
  createTransport(): KerbcamTransport {
    const self = this;
    return {
      createPeer(): KerbcamPeer {
        const channel: KerbcamDataChannel = {
          send(payload) {
            const msg = JSON.parse(payload) as ClientMessage;
            self._commands.push(msg);
            self._handleClientMessage(msg);
          },
          onOpen(h) {
            self._openHandler = h;
          },
          onMessage(h) {
            self._clientMsgHandler = h;
          },
          onClose() {},
        };
        return {
          addRecvOnlyTransceiver() {},
          createDataChannel: () => channel,
          onTrack(h) {
            self._onTrackHandler = h;
          },
          onStateChange(h) {
            self._stateHandler = h;
          },
          createOffer: async () => "v=0\r\n",
          setLocalDescription: async () => {},
          setRemoteAnswer: async () => {},
          waitForIceComplete: async () => {},
          localSdp: () => "v=0\r\n",
          close() {},
          getStats: async () => self._buildStatsReport(),
        };
      },
    };
  }

  /**
   * Simulate the sidecar completing the WebRTC handshake. Fires the
   * channel `onOpen` handler (which triggers the client's `hello`), then
   * responds with `hello` + `camera-snapshot`.
   */
  open(): void {
    this._openHandler?.();
    this._sendToClient({ type: "hello", content: { sidecarVersion: "0.0.1-mock", encoderBackend: "mock" } });
    this._sendToClient({ type: "camera-snapshot", content: { cameras: Array.from(this._cameras.values()) } });
  }

  /** Drive the underlying peer's connection-state handler. */
  setConnectionState(state: KerbcamConnectionState): void {
    this._stateHandler?.(state);
  }

  /**
   * Mark a camera as destroyed and push a `camera-state-changed` message
   * to the client. The camera stays in the internal registry with
   * `lifecycle: Destroyed`.
   */
  destroyCamera(flightId: number): void {
    const cam = this._cameras.get(flightId);
    if (!cam) return;
    const destroyed: CameraState = { ...cam, lifecycle: CameraLifecycle.Destroyed };
    this._cameras.set(flightId, destroyed);
    this._sendToClient({ type: "camera-state-changed", content: { state: destroyed } });
  }

  /**
   * Apply a partial update to an existing camera and push a
   * `camera-state-changed` message to the client.
   */
  updateCamera(flightId: number, partial: Partial<CameraState>): void {
    const cam = this._cameras.get(flightId);
    if (!cam) return;
    const updated: CameraState = { ...cam, ...partial };
    this._cameras.set(flightId, updated);
    this._sendToClient({ type: "camera-state-changed", content: { state: updated } });
  }

  /**
   * Replace the entire camera registry and push a fresh `camera-snapshot` to
   * the client. Models a vessel change / scene switch where the set of
   * available cameras changes (cameras appear or disappear) — distinct from
   * {@link destroyCamera}, which keeps the camera present but `Destroyed`.
   */
  setCameras(inits: MockCameraInit[]): void {
    this._cameras.clear();
    for (const init of inits) {
      this._cameras.set(init.flightId, buildCamera(init));
    }
    this._sendToClient({
      type: "camera-snapshot",
      content: { cameras: Array.from(this._cameras.values()) },
    });
  }

  /** Send a `ping` from the sidecar; the client should respond with `pong`. */
  firePing(): void {
    this._sendToClient({ type: "ping" });
  }

  /** Push an `adaptive-shed` event to the client. */
  fireAdaptiveShed(payload: AdaptiveShedPayload): void {
    this._sendToClient({ type: "adaptive-shed", content: payload });
  }

  /** Configure the slot-pool mids before connecting (dynamic mode). */
  withSlots(mids: string[]): this {
    this._slotMids = [...mids];
    return this;
  }

  /**
   * Deliver a track onto a slot (by mid), simulating the slot's media
   * arriving over WebRTC. The client routes it to whichever camera is bound
   * to that mid. (jsdom can't make real tracks; pass a stub in unit tests or
   * a `canvas.captureStream()` track in a real-browser harness.)
   *
   * The track is remembered so the fake `getStats()` can synthesize a
   * `trackIdentifier` matching what the client received.
   */
  deliverTrack(mid: string, track: MediaStreamTrack): void {
    this._deliveredTracks.set(mid, track);
    this._onTrackHandler?.(track, this._trackIdx++, mid);
  }

  /** The slot mid currently carrying `flightId`, or undefined. */
  slotMidFor(flightId: number): string | undefined {
    for (const [mid, fid] of this._slotBindings) {
      if (fid === flightId) return mid;
    }
    return undefined;
  }

  /** Every `ClientMessage` received from the client, in order. */
  get commands(): ReadonlyArray<ClientMessage> {
    return this._commands;
  }

  /**
   * Find the most recent client command of the given type. Pass `flightId`
   * to further filter by camera (ignored for message types without a
   * `content.flightId` field).
   */
  lastCommand<T extends ClientMessage["type"]>(
    type: T,
    flightId?: number,
  ): Extract<ClientMessage, { type: T }> | undefined {
    for (let i = this._commands.length - 1; i >= 0; i--) {
      const cmd = this._commands[i];
      if (cmd.type !== type) continue;
      if (flightId !== undefined) {
        const c = cmd as { content?: { flightId?: number } };
        if (c.content?.flightId !== flightId) continue;
      }
      return cmd as Extract<ClientMessage, { type: T }>;
    }
    return undefined;
  }

  /**
   * Build a `Response` that looks like the sidecar's `POST /offer` reply.
   * Pass to `vi.spyOn(globalThis, "fetch").mockImplementation(...)` so the
   * client's handshake can complete without a real HTTP server.
   *
   * ```ts
   * vi.spyOn(globalThis, "fetch").mockImplementation(() =>
   *   Promise.resolve(MockSidecar.makeOfferResponse([42]))
   * );
   * ```
   */
  static makeOfferResponse(cameras: number[]): Response {
    return new Response(
      JSON.stringify({ sdp: "v=0\r\n", cameras }),
      { status: 200, headers: { "Content-Type": "application/json" } },
    );
  }

  /**
   * Signaling-seam analogue of {@link makeOfferResponse}: resolve an offer's
   * answer without HTTP. Pass as the client's `negotiate` config to exercise
   * the brokered-signaling path a station uses.
   */
  negotiate(offer: {
    sdp: string;
    cameras: number[];
    slots?: number;
  }): Promise<{ sdp: string; cameras: number[] }> {
    return Promise.resolve({ sdp: "v=0\r\n", cameras: offer.cameras });
  }

  /**
   * Configure the inbound video stats the fake `getStats()` will return for
   * a given camera. Call before `client.inboundVideoStats()` in tests.
   *
   * The mock synthesizes a minimal `RTCStatsReport`-shaped object: one entry
   * per flight that has stats set, with `type: "inbound-rtp"`, `kind: "video"`,
   * and either a `trackIdentifier` matching the track delivered for that camera
   * (legacy path) or a `mid` matching the slot binding (dynamic path), plus
   * the stat fields from `partialStats`.
   *
   * ```ts
   * sidecar.setInboundStats(42, { packetsReceived: 1000, framesDecoded: 300 });
   * const stats = await client.inboundVideoStats();
   * expect(stats.get(42)?.packetsReceived).toBe(1000);
   * ```
   */
  setInboundStats(flightId: number, partialStats: Partial<InboundVideoStats>): void {
    this._inboundStats.set(flightId, partialStats);
  }

  /** Build a minimal RTCStatsReport-compatible object for the current state. */
  private _buildStatsReport(): RTCStatsReport {
    const entries: [string, RTCStats][] = [];

    for (const [flightId, stats] of this._inboundStats) {
      const id = `inbound-rtp-${flightId}`;

      // Resolve the identifier: prefer trackIdentifier from the delivered track
      // (legacy path); fall back to mid from the slot binding (dynamic path).
      let trackIdentifier: string | undefined;
      let mid: string | undefined;

      // Check slot bindings (dynamic mode).
      for (const [slotMid, fid] of this._slotBindings) {
        if (fid === flightId) {
          mid = slotMid;
          const track = this._deliveredTracks.get(slotMid);
          if (track?.id) trackIdentifier = track.id;
          break;
        }
      }

      // Legacy mode: look for a delivered track whose mid matches the index
      // position (mids in legacy mode are the slot index strings "0", "1", ...).
      if (!trackIdentifier && !mid) {
        for (const [deliveredMid, track] of this._deliveredTracks) {
          if (track.id) {
            trackIdentifier = track.id;
            mid = deliveredMid;
            break;
          }
        }
      }

      const entry = {
        id,
        type: "inbound-rtp" as const,
        timestamp: Date.now(),
        kind: "video",
        trackIdentifier,
        mid,
        packetsReceived: stats.packetsReceived ?? 0,
        bytesReceived: stats.bytesReceived ?? 0,
        framesReceived: stats.framesReceived,
        framesDecoded: stats.framesDecoded,
        jitter: stats.jitter,
        framesPerSecond: stats.framesPerSecond,
      };
      entries.push([id, entry as unknown as RTCStats]);
    }

    // Build a Map that satisfies the RTCStatsReport interface (iterable + forEach).
    const map = new Map<string, RTCStats>(entries);
    return map as unknown as RTCStatsReport;
  }

  private _sendToClient(msg: ServerMessage): void {
    this._clientMsgHandler?.(JSON.stringify(msg));
  }

  private _handleClientMessage(msg: ClientMessage): void {
    switch (msg.type) {
      case "set-fov": {
        const cam = this._cameras.get(msg.content.flightId);
        if (cam) this._cameras.set(msg.content.flightId, { ...cam, fov: msg.content.fov });
        break;
      }
      case "set-layers": {
        const cam = this._cameras.get(msg.content.flightId);
        if (cam) {
          this._cameras.set(msg.content.flightId, {
            ...cam,
            layers: msg.content.layers,
            operatorLayers: msg.content.layers,
          });
        }
        break;
      }
      case "set-render-size": {
        const cam = this._cameras.get(msg.content.flightId);
        if (cam) {
          this._cameras.set(msg.content.flightId, {
            ...cam,
            renderWidth: msg.content.width,
            renderHeight: msg.content.height,
            operatorWidth: msg.content.width,
            operatorHeight: msg.content.height,
          });
        }
        break;
      }
      case "set-pan": {
        const cam = this._cameras.get(msg.content.flightId);
        if (cam) {
          this._cameras.set(msg.content.flightId, {
            ...cam,
            panYaw: msg.content.yaw,
            panPitch: msg.content.pitch,
          });
        }
        break;
      }
      case "set-degrade": {
        const cam = this._cameras.get(msg.content.flightId);
        if (cam) {
          this._cameras.set(msg.content.flightId, { ...cam, degradeLevel: msg.content.level });
        }
        break;
      }
      case "subscribe": {
        const flightId = msg.content.flightId;
        const freeMid = this._slotMids.find((m) => !this._slotBindings.has(m));
        if (freeMid === undefined) {
          this._sendToClient({
            type: "error",
            content: { message: "no free slot", source: ErrorSource.Sidecar },
          });
        } else {
          this._slotBindings.set(freeMid, flightId);
          this._sendToClient({
            type: "slot-map",
            content: { mid: freeMid, flightId },
          });
        }
        break;
      }
      case "unsubscribe": {
        const flightId = msg.content.flightId;
        let bound: string | undefined;
        for (const [mid, fid] of this._slotBindings) {
          if (fid === flightId) {
            bound = mid;
            break;
          }
        }
        if (bound !== undefined) {
          this._slotBindings.delete(bound);
          this._sendToClient({
            type: "slot-map",
            content: { mid: bound, flightId: undefined },
          });
        }
        break;
      }
      // Persistent velocities are integrated frame-by-frame by the plugin;
      // the mock has no frame clock, so it doesn't model their *effect* on
      // panYaw/fov. They're still recorded in `_commands`, so consumer tests
      // can assert the command was sent via `lastCommand("set-pan-rate")`.
      case "set-pan-rate":
      case "set-zoom-rate":
      case "hello":
      case "pong":
      case "request-keyframe":
        break;
    }
  }
}

/**
 * Install jsdom shims needed by kerbcam component tests.
 *
 * jsdom omits several browser APIs that the SDK and React components call at
 * construction or mount time. Stubbing here keeps individual tests clean.
 * Each shim is idempotent so setup files can call `installDomStubs()`
 * unconditionally.
 *
 * Stubs installed:
 *   ResizeObserver   - jsdom does not implement it; CameraFeed uses it to
 *                      drive auto render-size updates.
 *   captureStream    - jsdom's HTMLCanvasElement lacks captureStream; the
 *                      noise pipeline calls it to get its output MediaStream.
 *                      Returns a stub MediaStream so callers receive a valid
 *                      object rather than throwing.
 *   MediaStream      - jsdom's MediaStream constructor is incomplete; tests
 *                      need to construct instances for track delivery and
 *                      stream assertions.
 *   play             - jsdom prints "Not implemented" for HTMLMediaElement.play;
 *                      the noise pipeline awaits it on the internal video element.
 *   matchMedia       - jsdom does not implement window.matchMedia; theme
 *                      detection reads prefers-color-scheme through it.
 */
export function installDomStubs(): void {
  // ResizeObserver: jsdom omits entirely; CameraFeed's auto-size hook needs it.
  if (typeof globalThis.ResizeObserver === "undefined") {
    globalThis.ResizeObserver = class ResizeObserver {
      observe() {}
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver;
  }

  // captureStream: jsdom's HTMLCanvasElement does not have it; the noise
  // pipeline's tryCreateNoisePipeline checks for it and returns null when
  // absent (safe degrade), but component tests that exercise the stream
  // path directly need a stub that returns a constructible MediaStream.
  if (
    typeof HTMLCanvasElement !== "undefined" &&
    typeof (HTMLCanvasElement.prototype as { captureStream?: unknown }).captureStream !== "function"
  ) {
    (HTMLCanvasElement.prototype as { captureStream: (fps?: number) => MediaStream }).captureStream =
      (_fps?: number): MediaStream => new StubMediaStream() as unknown as MediaStream;
  }

  // MediaStream: jsdom's implementation is minimal and not always constructible
  // with tracks. Provide a stub that satisfies the basic interface used by
  // tests (getTracks, addTrack, id).
  installStubMediaStream();

  // play: jsdom's HTMLMediaElement.play is not implemented and prints a warning.
  // The noise pipeline awaits video.play() on its internal video element.
  if (typeof HTMLMediaElement !== "undefined") {
    HTMLMediaElement.prototype.play = (): Promise<void> => Promise.resolve();
  }

  // matchMedia: jsdom does not implement window.matchMedia; theme detection
  // queries prefers-color-scheme through it. Stub returns a non-matching
  // MediaQueryList so tests default to the light theme unless overridden.
  if (typeof window !== "undefined" && typeof window.matchMedia !== "function") {
    window.matchMedia = (query: string): MediaQueryList => ({
      matches: false,
      media: query,
      onchange: null,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent: () => false,
    } as MediaQueryList);
  }
}

/** Minimal MediaStream stand-in for jsdom environments. */
class StubMediaStream {
  readonly id: string = Math.random().toString(36).slice(2);
  private readonly _tracks: MediaStreamTrack[] = [];

  getTracks(): MediaStreamTrack[] {
    return [...this._tracks];
  }
  getVideoTracks(): MediaStreamTrack[] {
    return this._tracks.filter((t) => t.kind === "video");
  }
  getAudioTracks(): MediaStreamTrack[] {
    return this._tracks.filter((t) => t.kind === "audio");
  }
  addTrack(track: MediaStreamTrack): void {
    this._tracks.push(track);
  }
  removeTrack(track: MediaStreamTrack): void {
    const i = this._tracks.indexOf(track);
    if (i !== -1) this._tracks.splice(i, 1);
  }
  clone(): StubMediaStream {
    return new StubMediaStream();
  }
}

function installStubMediaStream(): void {
  if (typeof globalThis === "undefined") return;
  // Only replace if the native MediaStream is not constructible (jsdom
  // registers the class but its constructor requires active getUserMedia
  // permissions that are never granted in a test environment).
  try {
    const ms = new globalThis.MediaStream();
    if (typeof ms.getTracks === "function") return; // native is adequate
  } catch {
    // Fall through to install the stub.
  }
  (globalThis as unknown as { MediaStream: unknown }).MediaStream = StubMediaStream;
}
