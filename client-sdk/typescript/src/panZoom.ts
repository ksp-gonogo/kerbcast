/**
 * Headless pan/zoom control state machine extracted from the CameraFeed
 * component. Manages rate deduplication, analog deadzone, optimistic
 * accumulators for discrete nudges, debounced FoV slider, and echo-sync
 * while idle -- all without any DOM or React dependency.
 *
 * `KerbcamCameraHandle` satisfies `PanZoomCommandSink` structurally: pass a
 * camera handle directly as the sink and the controller will drive it.
 *
 * ```ts
 * const cam = client.camera(flightId);
 * const ctrl = new PanZoomController(cam);
 *
 * // Analog stick input
 * ctrl.setPanRate(stickX, stickY);
 *
 * // Discrete nudge buttons
 * ctrl.nudgePan(1, 0);   // pan right one step
 * ctrl.nudgeZoom(-1);    // zoom in one step
 *
 * // FoV slider
 * ctrl.setFovSliderDragging(true);
 * ctrl.fovSliderInput(newFov);
 * ctrl.setFovSliderDragging(false); // flushes immediately
 *
 * // Sync echoed state (call from camera "change" listener)
 * ctrl.syncFromState({ fov, panYaw, panPitch, fovMin, fovMax,
 *                      panYawMin, panYawMax, panPitchMin, panPitchMax });
 *
 * // Cleanup on unmount
 * ctrl.stop();
 * ```
 */

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Command sink that the controller writes to. `KerbcamCameraHandle`
 * satisfies this interface structurally -- no adapter needed.
 */
export interface PanZoomCommandSink {
  setPan(yaw: number, pitch: number): Promise<void> | void;
  setPanRate(yawRate: number, pitchRate: number): Promise<void> | void;
  setFov(fov: number): Promise<void> | void;
  setZoomRate(rate: number): Promise<void> | void;
}

/** Camera FoV and pan bounds for clamping. */
export interface PanZoomBounds {
  fovMin: number;
  fovMax: number;
  panYawMin: number;
  panYawMax: number;
  panPitchMin: number;
  panPitchMax: number;
}

/** Tuning knobs for {@link PanZoomController}. All have safe defaults. */
export interface PanZoomControllerOptions {
  /**
   * Degrees moved per discrete pan nudge (arrow buttons, keyboard).
   * Default: 5.
   */
  panNudgeDeg?: number;
  /**
   * Degrees moved per discrete zoom nudge (zoom buttons, keyboard).
   * Default: 5.
   */
  fovNudgeDeg?: number;
  /**
   * Debounce window (ms) for the FoV slider. Only the settled value is
   * sent; intermediate drag positions do not stream commands.
   * Default: 120.
   */
  fovSliderDebounceMs?: number;
  /**
   * Magnitude below which an analog axis is snapped to 0. Prevents tiny
   * dithering near centre from emitting a stream of non-zero rate commands.
   * Default: 0.05.
   */
  analogDeadzone?: number;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function clamp(v: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, v));
}

function applyDeadzone(v: number, deadzone: number): number {
  return Math.abs(v) < deadzone ? 0 : v;
}

// ---------------------------------------------------------------------------
// PanZoomController
// ---------------------------------------------------------------------------

/**
 * Headless pan/zoom state machine. Manages rate deduplication, analog
 * deadzone, optimistic accumulators, FoV slider debounce, and echo-sync
 * idle rules. Mirrors the behaviour of the CameraFeed component in gonogo
 * (CameraFeed.tsx lines 323-592) without any React or DOM coupling.
 */
export class PanZoomController {
  private readonly sink: PanZoomCommandSink;
  private readonly panNudgeDeg: number;
  private readonly fovNudgeDeg: number;
  private readonly fovSliderDebounceMs: number;
  private readonly analogDeadzone: number;

  // Last-sent rates (dedupe against re-sending the same value).
  private sentPanRate = { yaw: 0, pitch: 0 };
  private sentZoomRate = 0;

  // Optimistic accumulators for discrete nudges.
  private localPan = { yaw: 0, pitch: 0 };
  private localFov = 0;

  // FoV slider state.
  private _sliderFov = 0;
  private sliderDragging = false;
  private fovDebounceTimer: ReturnType<typeof setTimeout> | null = null;
  private pendingFov: number | null = null;

  // Drag-ball flag (used only for the idle rule; pixel math lives in the UI).
  private ballDragging = false;

  // Last-known bounds for clamping.
  private bounds: PanZoomBounds = {
    fovMin: 0,
    fovMax: 180,
    panYawMin: -180,
    panYawMax: 180,
    panPitchMin: -90,
    panPitchMax: 90,
  };

  // sliderFov change subscribers.
  private readonly sliderFovListeners = new Set<(fov: number) => void>();

  constructor(sink: PanZoomCommandSink, opts: PanZoomControllerOptions = {}) {
    this.sink = sink;
    this.panNudgeDeg = opts.panNudgeDeg ?? 5;
    this.fovNudgeDeg = opts.fovNudgeDeg ?? 5;
    this.fovSliderDebounceMs = opts.fovSliderDebounceMs ?? 120;
    this.analogDeadzone = opts.analogDeadzone ?? 0.05;
  }

  // ---------------------------------------------------------------------------
  // Public getters
  // ---------------------------------------------------------------------------

  /**
   * Optimistic FoV value tracked by the slider. Follows the pointer while
   * dragging, then the camera echo when idle. Reactive via
   * {@link onSliderFov}.
   */
  get sliderFov(): number {
    return this._sliderFov;
  }

  // ---------------------------------------------------------------------------
  // Echo-sync
  // ---------------------------------------------------------------------------

  /**
   * Sync from the camera's echoed state (call from the camera's `"change"`
   * event listener). Applies the echoed pan to the pan accumulator only when
   * the controller is pan-idle (no active pan rate, no active ball drag).
   * Applies the echoed FoV to the accumulator and slider only when the
   * controller is zoom-idle (zoom rate 0, slider not dragging, no pending
   * debounced send). Mirrors CameraFeed.tsx:370-387.
   */
  syncFromState(
    state: { fov: number; panYaw: number; panPitch: number } & PanZoomBounds,
  ): void {
    this.bounds = {
      fovMin: state.fovMin,
      fovMax: state.fovMax,
      panYawMin: state.panYawMin,
      panYawMax: state.panYawMax,
      panPitchMin: state.panPitchMin,
      panPitchMax: state.panPitchMax,
    };

    const panIdle =
      !this.ballDragging &&
      this.sentPanRate.yaw === 0 &&
      this.sentPanRate.pitch === 0;
    if (panIdle) {
      this.localPan = { yaw: state.panYaw, pitch: state.panPitch };
    }

    const zoomIdle =
      this.sentZoomRate === 0 &&
      !this.sliderDragging &&
      this.pendingFov === null;
    if (zoomIdle) {
      this.localFov = state.fov;
      this.setSliderFovInternal(state.fov);
    }
  }

  // ---------------------------------------------------------------------------
  // Pan rate
  // ---------------------------------------------------------------------------

  /**
   * Set a persistent normalised pan velocity (-1..1 per axis). Clamps,
   * applies the analog deadzone, and deduplicates against the last-sent
   * value before forwarding to the sink. Mirrors sendPanRate.
   */
  setPanRate(yaw: number, pitch: number): void {
    const y = applyDeadzone(clamp(yaw, -1, 1), this.analogDeadzone);
    const p = applyDeadzone(clamp(pitch, -1, 1), this.analogDeadzone);
    if (y === this.sentPanRate.yaw && p === this.sentPanRate.pitch) return;
    this.sentPanRate = { yaw: y, pitch: p };
    void this.sink.setPanRate(y, p);
  }

  /**
   * Update one pan axis, preserving the other. Allows two independent inputs
   * (e.g. a serial stick axis and the ball) to compose rather than clobber
   * each other. Mirrors setPanAxis.
   */
  setPanAxis(axis: "yaw" | "pitch", value: number): void {
    const cur = this.sentPanRate;
    if (axis === "yaw") this.setPanRate(value, cur.pitch);
    else this.setPanRate(cur.yaw, value);
  }

  // ---------------------------------------------------------------------------
  // Zoom rate
  // ---------------------------------------------------------------------------

  /**
   * Set a persistent normalised zoom velocity (-1..1; +1 = zoom in, FoV
   * decreasing). Clamps, applies deadzone, deduplicates. Mirrors sendZoomRate.
   */
  setZoomRate(rate: number): void {
    const r = applyDeadzone(clamp(rate, -1, 1), this.analogDeadzone);
    if (r === this.sentZoomRate) return;
    this.sentZoomRate = r;
    void this.sink.setZoomRate(r);
  }

  // ---------------------------------------------------------------------------
  // Discrete nudges
  // ---------------------------------------------------------------------------

  /**
   * Discrete pan step (+1/-1 per axis sign). Moves the pan accumulator by
   * `panNudgeDeg`, clamped to bounds, then sends an absolute `setPan`.
   * Mirrors nudgePan.
   */
  nudgePan(yawSign: number, pitchSign: number): void {
    const b = this.bounds;
    this.localPan.yaw = clamp(
      this.localPan.yaw + yawSign * this.panNudgeDeg,
      b.panYawMin,
      b.panYawMax,
    );
    this.localPan.pitch = clamp(
      this.localPan.pitch + pitchSign * this.panNudgeDeg,
      b.panPitchMin,
      b.panPitchMax,
    );
    void this.sink.setPan(this.localPan.yaw, this.localPan.pitch);
  }

  /**
   * Discrete FoV step. `deltaSign: -1` = zoom in (FoV decreases by
   * `fovNudgeDeg`), `+1` = zoom out (FoV increases). Clamped to fov bounds.
   * Mirrors nudgeZoom (which calls onFovChange internally).
   */
  nudgeZoom(deltaSign: number): void {
    const b = this.bounds;
    const next = clamp(
      this.localFov + deltaSign * this.fovNudgeDeg,
      b.fovMin,
      b.fovMax,
    );
    this.localFov = next;
    void this.sink.setFov(next);
  }

  // ---------------------------------------------------------------------------
  // FoV slider
  // ---------------------------------------------------------------------------

  /**
   * Update the optimistic slider value and schedule a debounced `setFov`.
   * The settled (paused) value is what gets sent; rapid drag input does not
   * stream a command per pixel. Mirrors scheduleFovSlider.
   */
  fovSliderInput(fov: number): void {
    this.pendingFov = fov;
    this.setSliderFovInternal(fov);
    if (this.fovDebounceTimer !== null) clearTimeout(this.fovDebounceTimer);
    this.fovDebounceTimer = setTimeout(() => {
      this.fovDebounceTimer = null;
      this.flushFovSlider();
    }, this.fovSliderDebounceMs);
  }

  /**
   * Declare slider drag start/end. While dragging, echo-sync does not
   * override the slider (the user is in control). Setting `false` flushes
   * any pending debounced FoV immediately. Mirrors the pointer-up
   * flushFovSlider call.
   */
  setFovSliderDragging(dragging: boolean): void {
    this.sliderDragging = dragging;
    if (!dragging) this.flushFovSlider();
  }

  /**
   * Subscribe to slider FoV changes. The callback fires on `fovSliderInput`
   * and on idle echo-sync (when the camera echo updates the slider). Returns
   * an unsubscribe function.
   */
  onSliderFov(cb: (fov: number) => void): () => void {
    this.sliderFovListeners.add(cb);
    return () => {
      this.sliderFovListeners.delete(cb);
    };
  }

  // ---------------------------------------------------------------------------
  // Ball drag
  // ---------------------------------------------------------------------------

  /**
   * Declare ball-drag start/end. While dragging, echo-sync does not apply
   * the echoed pan to the local accumulator (the user is in control).
   * The pixel-deflection-to-rate conversion happens in the UI layer; the
   * UI calls `setPanRate` with the normalised result.
   */
  setBallDragging(dragging: boolean): void {
    this.ballDragging = dragging;
  }

  // ---------------------------------------------------------------------------
  // Lifecycle
  // ---------------------------------------------------------------------------

  /**
   * Send zero pan and zoom rates if (and only if) a non-zero rate is currently
   * active, and clear drag flags and any pending FoV timer without sending it.
   * Use on component unmount or camera hide -- mirrors the cleanup effects at
   * CameraFeed.tsx:477-508.
   */
  stop(): void {
    if (this.sentPanRate.yaw !== 0 || this.sentPanRate.pitch !== 0) {
      this.sentPanRate = { yaw: 0, pitch: 0 };
      void this.sink.setPanRate(0, 0);
    }
    if (this.sentZoomRate !== 0) {
      this.sentZoomRate = 0;
      void this.sink.setZoomRate(0);
    }
    this.ballDragging = false;
    this.sliderDragging = false;
    if (this.fovDebounceTimer !== null) {
      clearTimeout(this.fovDebounceTimer);
      this.fovDebounceTimer = null;
    }
    this.pendingFov = null;
  }

  /**
   * Stop the controller and drop all slider-change subscribers. Call when
   * the owning component is permanently destroyed.
   */
  dispose(): void {
    this.stop();
    this.sliderFovListeners.clear();
  }

  // ---------------------------------------------------------------------------
  // Private helpers
  // ---------------------------------------------------------------------------

  private flushFovSlider(): void {
    if (this.fovDebounceTimer !== null) {
      clearTimeout(this.fovDebounceTimer);
      this.fovDebounceTimer = null;
    }
    if (this.pendingFov !== null) {
      const fov = clamp(this.pendingFov, this.bounds.fovMin, this.bounds.fovMax);
      this.localFov = fov;
      this.pendingFov = null;
      void this.sink.setFov(fov);
    }
  }

  private setSliderFovInternal(fov: number): void {
    this._sliderFov = fov;
    for (const cb of this.sliderFovListeners) cb(fov);
  }
}
