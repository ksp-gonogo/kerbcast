import type { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { PanZoomController, QualityPreset } from "@ksp-gonogo/kerbcast";
import {
  forwardRef,
  useCallback,
  useEffect,
  useId,
  useImperativeHandle,
  useMemo,
  useRef,
  useState,
} from "react";
import { createPortal } from "react-dom";
import styled, { css } from "styled-components";
import { buildCameraLabeler } from "./cameraLabels";
import { KerbcastProvider, useKerbcastClient } from "./context";
import { useKerbcastCameras } from "./hooks/useKerbcastCameras";
import { useKerbcastStream } from "./hooks/useKerbcastStream";
import { isCameraDestroyed } from "./lifecycle";

// ---------------------------------------------------------------------------
// Tuning constants
// ---------------------------------------------------------------------------
const PAN_BALL_RADIUS = 15; // pixel deflection bound (full = rate 1)
const MENU_MAX_WIDTH = 260; // matches CameraMenu's CSS cap
const MENU_MAX_HEIGHT = 300; // matches CameraMenu's min(40vh, 300px) cap
const QUALITY_MENU_MAX_WIDTH = 220; // matches QualityMenu's CSS cap
const QUALITY_MENU_MAX_HEIGHT = 220; // matches QualityMenu's min(40vh, 220px) cap
const MENU_GAP = 4; // trigger-to-menu spacing
const MENU_EDGE = 8; // minimum inset from the viewport edge

/*
 * Geometry of a portaled dropdown: the CSS size caps of the menu (the
 * helper clamps as if the menu fills them) and which edge of the trigger
 * the menu hangs from: "start" lines the menu's left edge up with the
 * trigger's (camera picker), "end" lines the right edges up (quality
 * button at the action bar's corner).
 */
interface MenuAnchor {
  maxWidth: number;
  maxHeight: number;
  align: "start" | "end";
}

/*
 * Fixed-position style for a portaled menu, anchored to its trigger button.
 * Opens downward by default; flips above the trigger when there is not
 * enough room below but there is above, otherwise clamps to the viewport.
 */
function computeMenuPosition(
  trigger: HTMLElement,
  anchor: MenuAnchor,
): React.CSSProperties {
  const rect = trigger.getBoundingClientRect();
  const vw = window.innerWidth;
  const vh = window.innerHeight;
  const width = Math.min(anchor.maxWidth, vw - 2 * MENU_EDGE);
  /* Inline offset from the aligned viewport edge, clamped so a full-width
     menu still fits inside the opposite edge. */
  const inset = Math.max(MENU_EDGE, vw - width - MENU_EDGE);
  const inline: React.CSSProperties =
    anchor.align === "start"
      ? { left: Math.min(Math.max(rect.left, MENU_EDGE), inset) }
      : { right: Math.min(Math.max(vw - rect.right, MENU_EDGE), inset) };
  const menuH = Math.min(0.4 * vh, anchor.maxHeight);
  const fitsBelow = rect.bottom + MENU_GAP + menuH <= vh - MENU_EDGE;
  const fitsAbove = rect.top - MENU_GAP - menuH >= MENU_EDGE;
  if (!fitsBelow && fitsAbove) {
    return { ...inline, bottom: vh - rect.top + MENU_GAP };
  }
  const top = Math.min(
    rect.bottom + MENU_GAP,
    Math.max(MENU_EDGE, vh - MENU_EDGE - menuH),
  );
  return { ...inline, top };
}

/*
 * Shared behaviour for a dropdown portaled to document.body: fixed position
 * computed from the trigger's rect (so tile overflow cannot clip it),
 * re-anchored on window resize/scroll while open, Escape to close with focus
 * returned to the trigger, and portal-aware outside-pointer-down dismissal
 * (menuRef points at the portaled menu, so clicks inside it stay "inside").
 * One menu at a time falls out of the dismissal: opening another menu's
 * trigger is an outside press for this one.
 */
function usePortalMenu({ maxWidth, maxHeight, align }: MenuAnchor) {
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState<React.CSSProperties | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) {
      setPosition(null);
      return;
    }
    const update = () => {
      const trigger = triggerRef.current;
      if (trigger) {
        setPosition(computeMenuPosition(trigger, { maxWidth, maxHeight, align }));
      }
    };
    update();
    window.addEventListener("resize", update);
    window.addEventListener("scroll", update, true);
    return () => {
      window.removeEventListener("resize", update);
      window.removeEventListener("scroll", update, true);
    };
  }, [open, maxWidth, maxHeight, align]);

  useEffect(() => {
    if (!open) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.stopPropagation();
        setOpen(false);
        triggerRef.current?.focus();
      }
    };
    const onPointerDown = (e: PointerEvent) => {
      if (
        !menuRef.current?.contains(e.target as Node) &&
        !triggerRef.current?.contains(e.target as Node)
      ) {
        setOpen(false);
      }
    };
    document.addEventListener("keydown", onKeyDown);
    document.addEventListener("pointerdown", onPointerDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      document.removeEventListener("pointerdown", onPointerDown);
    };
  }, [open]);

  const toggle = useCallback(() => setOpen((v) => !v), []);
  /** Close after an item pick, returning focus to the trigger. */
  const close = useCallback(() => {
    setOpen(false);
    triggerRef.current?.focus();
  }, []);

  return { open, toggle, close, position, menuRef, triggerRef };
}

/** Round n to the nearest even integer, minimum 2 (H.264 chroma requirement). */
function toEvenPx(n: number): number {
  return Math.max(2, Math.round(n / 2) * 2);
}

/*
 * Viewer quality presets, in menu order. Scales mirror the sidecar's
 * QualityPreset mapping (fractions of the camera's operator render size);
 * the target-dims hint floors to even exactly like the plugin does.
 */
const QUALITY_PRESETS: ReadonlyArray<{
  preset: QualityPreset;
  label: string;
  scale: number;
}> = [
  { preset: QualityPreset.Full, label: "Full", scale: 1.0 },
  { preset: QualityPreset.ThreeQuarter, label: "3/4", scale: 0.75 },
  { preset: QualityPreset.Half, label: "1/2", scale: 0.5 },
  { preset: QualityPreset.Quarter, label: "1/4", scale: 0.25 },
];

function presetDim(operatorDim: number, scale: number): number {
  const v = Math.trunc(operatorDim * scale) & ~1;
  return v < 2 ? 2 : v;
}

/*
 * Fullscreen helpers. Safari (incl. iPadOS) only exposes the webkit-prefixed
 * API; iOS iPhone has no element fullscreen at all, which `isFullscreenSupported`
 * reports as unsupported so the button hides.
 */
type FsDocument = Document & {
  webkitFullscreenElement?: Element | null;
  webkitFullscreenEnabled?: boolean;
  webkitExitFullscreen?: () => Promise<void> | void;
};
type FsElement = HTMLElement & {
  webkitRequestFullscreen?: () => Promise<void> | void;
};

function isFullscreenSupported(): boolean {
  if (typeof document === "undefined") return false;
  const d = document as FsDocument;
  return Boolean(d.fullscreenEnabled || d.webkitFullscreenEnabled);
}

function currentFullscreenElement(): Element | null {
  const d = document as FsDocument;
  return d.fullscreenElement ?? d.webkitFullscreenElement ?? null;
}

function requestFullscreen(el: HTMLElement): void {
  const e = el as FsElement;
  void (e.requestFullscreen?.() ?? e.webkitRequestFullscreen?.());
}

function exitFullscreen(): void {
  const d = document as FsDocument;
  void (d.exitFullscreen?.() ?? d.webkitExitFullscreen?.());
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/**
 * A consumer-supplied action rendered into the feed's top-right action bar.
 * Lets a host page (e.g. the sidecar's spotlight toggle) inject controls
 * without the library knowing what they do.
 */
export interface FeedAction {
  /** Stable identity for the React key. */
  id: string;
  /** Accessible label; used for aria-label and the native tooltip. */
  label: string;
  /** Icon node, sized by the action bar (~14px). */
  icon: React.ReactNode;
  /** Toggle state: renders the button highlighted and sets aria-pressed. */
  active?: boolean;
  onClick: () => void;
}

/**
 * A hook that yields the live `MediaStream` for a resolved flightId. Must be
 * a stable reference (the same function identity every render) and called
 * unconditionally, per the rules of hooks. Default: the built-in
 * `useKerbcastStream`. Consumers wrap this to inject delayed playout,
 * alternate transports, etc.; the feed stays unaware of what the wrapper
 * does, and keeps binding whatever stream comes back to its `<video>`.
 *
 * A replacement takes over the camera subscription slot: the built-in
 * `useKerbcastStream` (which acquires/releases the slot) does not run when
 * this is supplied, so a replacement must either compose `useKerbcastStream`
 * or acquire the slot itself, or the sidecar is never subscribed and the
 * feed stays black.
 */
export type CameraStreamHook = (flightId: number | null) => MediaStream | null;

export interface CameraFeedProps {
  /** Override the context client for this feed only. */
  client?: KerbcastClient;
  /**
   * KSP `Part.flightID` of the camera to display. `null` triggers
   * auto-selection: the first live camera in the registry is latched.
   */
  flightId: number | null;
  /**
   * Called when the user explicitly picks a camera (picker, Next/Prev
   * buttons). Never called on auto-latch.
   */
  onSelectCamera?: (flightId: number) => void;
  /**
   * Called whenever the camera this feed actually displays changes — including
   * auto-latch and fallback picks, not just explicit selection. The argument
   * is the resolved flightId (null when nothing is shown). Use it to label or
   * annotate the feed by what it is really showing rather than what was
   * requested; the two differ whenever auto-selection kicks in.
   */
  onDisplayedCameraChange?: (flightId: number | null) => void;
  /** Show resolution + encoder readout. Default false. */
  showDebugInfo?: boolean;
  /**
   * Whether to show animated static. `true`: stall ramps noise in over the
   * held last frame; sourceless path shows live noise. `false`: stall freezes
   * the last frame with a dim scrim and stale badge; sourceless path shows a
   * plain black background. `undefined` (default): auto mode, reads
   * `prefers-reduced-motion: reduce` at mount and follows changes at runtime
   * (reduced motion defaults to off, normal motion defaults to on).
   */
  showStatic?: boolean;
  /**
   * "auto" (default): ResizeObserver drives `setRenderSize` at a 16:9 crop,
   * debounced 500 ms. "none": no render-size feedback.
   */
  renderSize?: "auto" | "none";
  /** Message shown when no cameras are available. */
  emptyMessage?: string;
  /**
   * Show a built-in fullscreen toggle that fullscreens this feed's frame.
   * Auto-hidden where the Fullscreen API is unavailable (e.g. iOS Safari,
   * which only fullscreens the bare <video>). Default false.
   */
  enableFullscreen?: boolean;
  /**
   * Show a built-in Picture-in-Picture toggle for this feed's video.
   * Auto-hidden where `document.pictureInPictureEnabled` is false. Default
   * false.
   */
  enablePictureInPicture?: boolean;
  /**
   * Show a built-in per-camera quality control in the action bar: Auto plus
   * the resolution presets (full / 3-4 / 1-2 / 1-4 of the operator-configured
   * size), with the camera's effective resolution and a marker when the
   * sidecar's adaptive machinery is throttling below the request. Requests
   * can only lower quality; the resolution change arrives in-band over the
   * existing WebRTC track (the video element is never remounted). Default
   * false.
   */
  enableQualityControl?: boolean;
  /**
   * Consumer-injected action buttons, rendered left of the built-in
   * fullscreen/PiP controls in the top-right action bar.
   */
  actions?: FeedAction[];
  /**
   * Consumer-injected action buttons rendered at the far end of the action
   * bar, right of the built-in controls — the natural home for a close/remove
   * button so it sits in the corner.
   */
  trailingActions?: FeedAction[];
  /**
   * Override how the displayed video stream is sourced for the resolved
   * flightId. Omit (the default) to use the built-in `useKerbcastStream` —
   * unchanged behaviour. When supplied it must be a stable reference passed
   * consistently across renders (it is called as a hook); see
   * {@link CameraStreamHook}.
   */
  useStream?: CameraStreamHook;
}

export interface CameraFeedHandle {
  stepCamera(delta: number): void;
  setZoomRate(rate: number): void;
  setPanAxis(axis: "yaw" | "pitch", value: number): void;
  nudgeZoom(deltaSign: number): void;
  nudgePan(yawSign: number, pitchSign: number): void;
}

// ---------------------------------------------------------------------------
// Inner component (reads from context)
// ---------------------------------------------------------------------------

const CameraFeedInner = forwardRef<CameraFeedHandle, CameraFeedProps>(
  function CameraFeedInner(
    {
      flightId: requestedFlightId,
      onSelectCamera,
      onDisplayedCameraChange,
      showDebugInfo = false,
      showStatic,
      renderSize = "auto",
      emptyMessage = "No camera feeds - start a vessel with Hullcam parts installed",
      enableFullscreen = false,
      enablePictureInPicture = false,
      enableQualityControl = false,
      actions,
      trailingActions,
      useStream,
    },
    ref,
  ) {
    const client = useKerbcastClient();
    const cameras = useKerbcastCameras();

    /*
     * Reduced-motion auto mode: when `showStatic` prop is undefined, read and
     * track `prefers-reduced-motion: reduce`. Reduced motion defaults to off
     * (no animated noise); normal motion defaults to on.
     */
    const [reducedMotion, setReducedMotion] = useState(() => {
      if (typeof window === "undefined") return false;
      return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    });
    useEffect(() => {
      if (showStatic !== undefined) return;
      const mq = window.matchMedia("(prefers-reduced-motion: reduce)");
      const handler = (e: MediaQueryListEvent) => setReducedMotion(e.matches);
      mq.addEventListener("change", handler);
      return () => mq.removeEventListener("change", handler);
    }, [showStatic]);
    const effectiveShowStatic = showStatic === undefined ? !reducedMotion : showStatic;

    // -------------------------------------------------------------------------
    // Selection model (mirrors gonogo's CameraFeed)
    //
    // Resolution order:
    //   1. Explicit pick, if still present in the list (destroyed or not).
    //   2. Auto: latch the currently-displayed camera. Keep showing it even if
    //      destroyed, but only while no other live camera exists (destroyed
    //      tombstones never leave the list on their own, so a destroyed latch
    //      would otherwise never release). Once a live camera is available,
    //      release the latch and auto-pick it.
    //   3. Fresh auto-pick: prefer the first live camera; fall back to first
    //      overall only when every camera is destroyed.
    // -------------------------------------------------------------------------
    const displayedRef = useRef<number | null>(null);
    const requestedStillPresent =
      requestedFlightId !== null &&
      cameras.some((c) => c.flightId === requestedFlightId);

    let flightId: number | null;
    if (requestedStillPresent) {
      flightId = requestedFlightId;
    } else {
      const latched = displayedRef.current;
      const latchedCamera =
        latched !== null ? cameras.find((c) => c.flightId === latched) : undefined;
      const anyLive = cameras.some((c) => !isCameraDestroyed(c));
      const latchedPresent =
        latchedCamera !== undefined &&
        (!isCameraDestroyed(latchedCamera) || !anyLive);
      flightId = latchedPresent
        ? latched
        : (cameras.find((c) => !isCameraDestroyed(c))?.flightId ??
          cameras[0]?.flightId ??
          null);
    }

    const camera =
      flightId !== null
        ? (cameras.find((c) => c.flightId === flightId) ?? null)
        : null;

    // Latest onDisplayedCameraChange, held in a ref so the commit effect below
    // fires only on a resolved-flightId change, not on every render when the
    // consumer passes an inline callback.
    const onDisplayedRef = useRef(onDisplayedCameraChange);
    useEffect(() => {
      onDisplayedRef.current = onDisplayedCameraChange;
    });

    // Commit on-screen camera for the auto-mode latch, and report which camera
    // is actually displayed so consumers can label by it (auto-picks included).
    useEffect(() => {
      displayedRef.current = flightId;
      onDisplayedRef.current?.(flightId);
    }, [flightId]);

    // `flightId` here is the RESOLVED id (auto-latch / fallback applied), so a
    // consumer-injected hook never has to duplicate that resolution. The
    // built-in hook is the default. See CameraStreamHook's rules-of-hooks note.
    const resolveStream = useStream ?? useKerbcastStream;
    const stream = resolveStream(flightId);
    const videoRef = useRef<HTMLVideoElement>(null);
    // The feed frame; fullscreen targets this and ResizeObserver measures it.
    const wrapRef = useRef<HTMLDivElement>(null);
    useEffect(() => {
      if (videoRef.current && stream) {
        videoRef.current.srcObject = stream;
      }
    }, [stream]);

    // -------------------------------------------------------------------------
    // Stall presentation. The static is composited in-stream by the SDK's
    // noise pipeline; this forwards the resolved setting to the camera handle
    // and mirrors the handle's stall state for the no-static badge.
    // -------------------------------------------------------------------------
    useEffect(() => {
      if (flightId === null) return;
      client.camera(flightId).setShowStatic(effectiveShowStatic);
    }, [client, flightId, effectiveShowStatic]);

    const [isStale, setIsStale] = useState(false);
    useEffect(() => {
      if (flightId === null) {
        setIsStale(false);
        return;
      }
      const cam = client.camera(flightId);
      setIsStale(cam.stalled);
      return cam.on("stall", setIsStale);
    }, [client, flightId]);

    // -------------------------------------------------------------------------
    // Fullscreen + Picture-in-Picture (opt-in, feature-detected)
    // -------------------------------------------------------------------------
    const fullscreenAvailable = enableFullscreen && isFullscreenSupported();
    const pipAvailable =
      enablePictureInPicture &&
      typeof document !== "undefined" &&
      document.pictureInPictureEnabled === true;

    const [isFullscreen, setIsFullscreen] = useState(false);
    const [isPip, setIsPip] = useState(false);

    // Keep the fullscreen icon in sync however the user enters/exits (Esc, etc).
    useEffect(() => {
      if (!fullscreenAvailable) return;
      const sync = () =>
        setIsFullscreen(currentFullscreenElement() === wrapRef.current);
      document.addEventListener("fullscreenchange", sync);
      document.addEventListener("webkitfullscreenchange", sync);
      sync();
      return () => {
        document.removeEventListener("fullscreenchange", sync);
        document.removeEventListener("webkitfullscreenchange", sync);
      };
    }, [fullscreenAvailable]);

    // PiP listeners re-attach when the <video> mounts/remounts (flightId change).
    useEffect(() => {
      if (!pipAvailable) return;
      const v = videoRef.current;
      if (!v) return;
      const onEnter = () => setIsPip(true);
      const onLeave = () => setIsPip(false);
      v.addEventListener("enterpictureinpicture", onEnter);
      v.addEventListener("leavepictureinpicture", onLeave);
      return () => {
        v.removeEventListener("enterpictureinpicture", onEnter);
        v.removeEventListener("leavepictureinpicture", onLeave);
      };
    }, [pipAvailable, flightId]);

    const toggleFullscreen = useCallback(() => {
      const el = wrapRef.current;
      if (!el) return;
      if (currentFullscreenElement()) exitFullscreen();
      else requestFullscreen(el);
    }, []);

    const togglePip = useCallback(() => {
      const v = videoRef.current;
      if (!v) return;
      if (document.pictureInPictureElement) {
        void document.exitPictureInPicture().catch(() => {});
      } else {
        void v.requestPictureInPicture().catch(() => {});
      }
    }, []);

    const isDestroyed = camera ? isCameraDestroyed(camera) : false;
    const showPan = camera?.supportsPan && !isDestroyed;
    const showZoom = camera?.supportsZoom && !isDestroyed;
    const supportsPitch =
      !!camera && camera.panPitchMax - camera.panPitchMin > 0;

    // -------------------------------------------------------------------------
    // Camera selection callbacks (Next/Prev, picker)
    // onSelectCamera is ONLY called on explicit user picks, never on auto-latch.
    // -------------------------------------------------------------------------
    const currentIndex = useMemo(
      () =>
        flightId !== null
          ? cameras.findIndex((c) => c.flightId === flightId)
          : -1,
      [cameras, flightId],
    );

    const stepCamera = useCallback(
      (delta: number) => {
        if (cameras.length === 0) return;
        const base = currentIndex >= 0 ? currentIndex : 0;
        const next = (base + delta + cameras.length) % cameras.length;
        const nextId = cameras[next]?.flightId;
        if (nextId !== undefined) onSelectCamera?.(nextId);
      },
      [cameras, currentIndex, onSelectCamera],
    );

    // -------------------------------------------------------------------------
    // PanZoomController -- one per displayed camera
    // -------------------------------------------------------------------------
    const controllerRef = useRef<PanZoomController | null>(null);
    const [sliderFov, setSliderFov] = useState(60);

    useEffect(() => {
      if (flightId === null) {
        controllerRef.current?.dispose();
        controllerRef.current = null;
        return;
      }
      const cam = client.camera(flightId);
      const ctrl = new PanZoomController(cam);
      controllerRef.current?.dispose();
      controllerRef.current = ctrl;

      const unsubSlider = ctrl.onSliderFov(setSliderFov);
      // Seed the slider from the controller's initial (0) -- will be overwritten
      // immediately by syncFromState in the camera-state effect below.
      setSliderFov(ctrl.sliderFov);

      return () => {
        unsubSlider();
        ctrl.stop();
        ctrl.dispose();
        controllerRef.current = null;
      };
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [client, flightId]);

    // Sync controller from camera state on every state update.
    useEffect(() => {
      if (!camera || !controllerRef.current) return;
      controllerRef.current.syncFromState(camera);
    }, [camera]);

    // Stop pan/zoom when the control hides (signal lost / support dropped).
    useEffect(() => {
      if (!showPan) {
        controllerRef.current?.setPanRate(0, 0);
        setBallPos({ x: 0, y: 0 });
        setBallActive(false);
      }
    }, [showPan]);
    useEffect(() => {
      if (!showZoom) controllerRef.current?.setZoomRate(0);
    }, [showZoom]);

    // -------------------------------------------------------------------------
    // Ball drag state (pixel math lives here)
    // -------------------------------------------------------------------------
    const ballStartRef = useRef({ x: 0, y: 0 });
    const [ballPos, setBallPos] = useState({ x: 0, y: 0 });
    const [ballActive, setBallActive] = useState(false);

    const handleBallDown = useCallback(
      (e: React.PointerEvent<HTMLDivElement>) => {
        if (flightId === null) return;
        e.currentTarget.setPointerCapture(e.pointerId);
        ballStartRef.current = { x: e.clientX, y: e.clientY };
        setBallActive(true);
        controllerRef.current?.setBallDragging(true);
      },
      [flightId],
    );

    const handleBallMove = useCallback(
      (e: React.PointerEvent<HTMLDivElement>) => {
        if (!ballActive) return;
        let dx = e.clientX - ballStartRef.current.x;
        let dy = supportsPitch ? e.clientY - ballStartRef.current.y : 0;
        const mag = Math.hypot(dx, dy);
        if (mag > PAN_BALL_RADIUS) {
          const k = PAN_BALL_RADIUS / mag;
          dx *= k;
          dy *= k;
        }
        setBallPos({ x: dx, y: dy });
        controllerRef.current?.setPanRate(dx / PAN_BALL_RADIUS, -dy / PAN_BALL_RADIUS);
      },
      [ballActive, supportsPitch],
    );

    const handleBallUp = useCallback(() => {
      setBallActive(false);
      setBallPos({ x: 0, y: 0 });
      controllerRef.current?.setBallDragging(false);
      controllerRef.current?.setPanRate(0, 0);
    }, []);

    // -------------------------------------------------------------------------
    // Handle API (forwardRef)
    // -------------------------------------------------------------------------
    useImperativeHandle(
      ref,
      () => ({
        stepCamera,
        setZoomRate(rate: number) {
          if (!showZoom) return;
          controllerRef.current?.setZoomRate(rate);
        },
        setPanAxis(axis: "yaw" | "pitch", value: number) {
          if (!showPan) return;
          if (axis === "pitch" && !supportsPitch) return;
          controllerRef.current?.setPanAxis(axis, value);
        },
        nudgeZoom(deltaSign: number) {
          if (!showZoom) return;
          controllerRef.current?.nudgeZoom(deltaSign);
        },
        nudgePan(yawSign: number, pitchSign: number) {
          if (!showPan) return;
          controllerRef.current?.nudgePan(yawSign, pitchSign);
        },
      }),
      [stepCamera, showZoom, showPan, supportsPitch],
    );

    // -------------------------------------------------------------------------
    // Render-size feedback (ResizeObserver, 500 ms debounce, 16:9 crop)
    // -------------------------------------------------------------------------
    const resizeTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

    useEffect(() => {
      if (renderSize !== "auto" || flightId === null) return;
      const el = wrapRef.current;
      if (!el) return;
      const cam = client.camera(flightId);

      const observer = new ResizeObserver((entries) => {
        const entry = entries[0];
        if (!entry) return;
        const { width } = entry.contentRect;
        if (resizeTimerRef.current !== null) clearTimeout(resizeTimerRef.current);
        resizeTimerRef.current = setTimeout(() => {
          const w = toEvenPx(width);
          const h = toEvenPx((width * 9) / 16);
          void cam.setRenderSize(w, h);
        }, 500);
      });

      observer.observe(el);

      return () => {
        observer.disconnect();
        if (resizeTimerRef.current !== null) clearTimeout(resizeTimerRef.current);
      };
    }, [client, flightId, renderSize]);

    // -------------------------------------------------------------------------
    // UI state
    // -------------------------------------------------------------------------
    const bitrateLabel =
      camera && camera.encoderBitrateBps > 0
        ? ` · ${Math.round(camera.encoderBitrateBps / 1000)}kbps`
        : "";
    const adaptiveLabel =
      camera && camera.renderWidth < camera.operatorWidth ? " · adaptive" : "";

    const hasCameras = cameras.length > 0;
    const canStep = cameras.length > 1;
    const menuId = useId();
    const [chromePinned, setChromePinned] = useState(false);
    const cameraMenu = usePortalMenu({
      maxWidth: MENU_MAX_WIDTH,
      maxHeight: MENU_MAX_HEIGHT,
      align: "start",
    });

    const cameraLabel = useMemo(() => buildCameraLabeler(cameras), [cameras]);
    const title = camera ? cameraLabel(camera) : "Camera Feed";

    // -------------------------------------------------------------------------
    // Viewer quality control (opt-in). A built-in action button + menu: Auto
    // plus the resolution presets. Requested state = camera.viewerQuality
    // (authoritative, broadcast by the sidecar so every UI agrees); effective
    // state = renderWidth/Height; qualityLimitedBy marks adaptive throttling.
    // -------------------------------------------------------------------------
    const qualityAvailable =
      enableQualityControl && flightId !== null && camera !== null;
    const qualityThrottled = Boolean(camera?.qualityLimitedBy);
    const qualityMenuId = useId();
    // Same portal/anchor/dismissal machinery as the camera menu, hung from
    // the right edge of its action-bar trigger.
    const qualityMenu = usePortalMenu({
      maxWidth: QUALITY_MENU_MAX_WIDTH,
      maxHeight: QUALITY_MENU_MAX_HEIGHT,
      align: "end",
    });
    const closeQualityMenu = qualityMenu.close;

    const selectQuality = useCallback(
      (preset: QualityPreset | null) => {
        if (flightId === null) return;
        void client.camera(flightId).setQuality(preset);
        closeQualityMenu();
      },
      [client, flightId, closeQualityMenu],
    );

    const topOverlay = (
      <TopOverlay>
        <TitleRow>
          <TopTitle>
            {hasCameras ? (
              <TitleButton
                ref={cameraMenu.triggerRef}
                type="button"
                aria-haspopup="menu"
                aria-expanded={cameraMenu.open}
                aria-controls={menuId}
                onClick={cameraMenu.toggle}
              >
                <TitleButton__Text>{title}</TitleButton__Text>
                <ChevronDownIcon aria-hidden="true" />
              </TitleButton>
            ) : (
              title
            )}
          </TopTitle>
          {hasCameras && (
            <StepButtons>
              <OverlayIconButton
                type="button"
                aria-label="Previous camera"
                disabled={!canStep}
                onClick={() => stepCamera(-1)}
              >
                &#8249;
              </OverlayIconButton>
              <OverlayIconButton
                type="button"
                aria-label="Next camera"
                disabled={!canStep}
                onClick={() => stepCamera(1)}
              >
                &#8250;
              </OverlayIconButton>
            </StepButtons>
          )}
        </TitleRow>

        {cameraMenu.open &&
          hasCameras &&
          cameraMenu.position &&
          createPortal(
            <CameraMenu
              ref={cameraMenu.menuRef}
              id={menuId}
              role="menu"
              aria-label="Camera"
              style={cameraMenu.position}
            >
              {cameras.map((c) => (
                <CameraMenuItem
                  key={c.flightId}
                  type="button"
                  role="menuitemradio"
                  aria-checked={c.flightId === flightId}
                  $selected={c.flightId === flightId}
                  onClick={() => {
                    onSelectCamera?.(c.flightId);
                    cameraMenu.close();
                  }}
                >
                  {cameraLabel(c)} ({c.vesselName})
                  {isCameraDestroyed(c) ? " - signal lost" : ""}
                </CameraMenuItem>
              ))}
            </CameraMenu>,
            document.body,
          )}

        {showDebugInfo &&
          (camera ? (
            <TopMeta>
              {camera.vesselName} · {camera.renderWidth}×{camera.renderHeight}
              {bitrateLabel}
              {adaptiveLabel}
            </TopMeta>
          ) : (
            <TopMeta>no cameras on this vessel</TopMeta>
          ))}
      </TopOverlay>
    );

    const renderAction = (a: FeedAction) => (
      <OverlayIconButton
        key={a.id}
        type="button"
        aria-label={a.label}
        aria-pressed={a.active ?? undefined}
        title={a.label}
        $active={a.active ?? false}
        onClick={a.onClick}
      >
        {a.icon}
      </OverlayIconButton>
    );

    const builtInActions =
      flightId !== null && (pipAvailable || fullscreenAvailable || qualityAvailable);
    const hasActionBar =
      (actions && actions.length > 0) ||
      (trailingActions && trailingActions.length > 0) ||
      builtInActions;
    const actionBar = hasActionBar ? (
        <ActionBar>
          {actions?.map(renderAction)}
          {qualityAvailable && (
            <OverlayIconButton
              ref={qualityMenu.triggerRef}
              type="button"
              aria-label="Quality"
              aria-haspopup="menu"
              aria-expanded={qualityMenu.open}
              aria-controls={qualityMenuId}
              title={
                qualityThrottled
                  ? "Quality (throttled by adaptive performance)"
                  : "Quality"
              }
              $active={qualityMenu.open}
              onClick={qualityMenu.toggle}
            >
              <QualityIcon />
              {qualityThrottled && <ThrottledDot aria-hidden="true" />}
            </OverlayIconButton>
          )}
          {flightId !== null && pipAvailable && (
            <OverlayIconButton
              type="button"
              aria-label={isPip ? "Exit picture in picture" : "Picture in picture"}
              aria-pressed={isPip}
              title={isPip ? "Exit picture in picture" : "Picture in picture"}
              $active={isPip}
              onClick={togglePip}
            >
              <PictureInPictureIcon />
            </OverlayIconButton>
          )}
          {flightId !== null && fullscreenAvailable && (
            <OverlayIconButton
              type="button"
              aria-label={isFullscreen ? "Exit fullscreen" : "Enter fullscreen"}
              aria-pressed={isFullscreen}
              title={isFullscreen ? "Exit fullscreen" : "Enter fullscreen"}
              $active={isFullscreen}
              onClick={toggleFullscreen}
            >
              {isFullscreen ? <FullscreenExitIcon /> : <FullscreenEnterIcon />}
            </OverlayIconButton>
          )}
          {trailingActions?.map(renderAction)}
        </ActionBar>
      ) : null;

    return (
      <Stage ref={wrapRef} $pinned={chromePinned}>
        {flightId === null ? (
          <>
            <Empty>{emptyMessage}</Empty>
            {topOverlay}
            {actionBar}
          </>
        ) : (
          <>
            <StyledVideo
              ref={videoRef}
              autoPlay
              playsInline
              muted
              controls={false}
              onClick={() => setChromePinned((v) => !v)}
            />
            {topOverlay}
            {actionBar}
            {qualityAvailable &&
              qualityMenu.open &&
              qualityMenu.position &&
              camera &&
              createPortal(
                <QualityMenu
                  ref={qualityMenu.menuRef}
                  id={qualityMenuId}
                  role="menu"
                  aria-label="Quality"
                  style={qualityMenu.position}
                >
                  <CameraMenuItem
                    type="button"
                    role="menuitemradio"
                    aria-checked={camera.viewerQuality == null}
                    $selected={camera.viewerQuality == null}
                    onClick={() => selectQuality(null)}
                  >
                    Auto
                  </CameraMenuItem>
                  {QUALITY_PRESETS.map(({ preset, label, scale }) => (
                    <CameraMenuItem
                      key={preset}
                      type="button"
                      role="menuitemradio"
                      aria-checked={camera.viewerQuality === preset}
                      $selected={camera.viewerQuality === preset}
                      onClick={() => selectQuality(preset)}
                    >
                      {label} ({presetDim(camera.operatorWidth, scale)}×
                      {presetDim(camera.operatorHeight, scale)})
                    </CameraMenuItem>
                  ))}
                  <QualityMeta role="status">
                    now {camera.renderWidth}×{camera.renderHeight}
                    {qualityThrottled ? " · throttled" : ""}
                  </QualityMeta>
                </QualityMenu>,
                document.body,
              )}
            {isDestroyed && (
              <SignalLostOverlay role="status" aria-label="Signal lost">
                <SignalLostText $animated={effectiveShowStatic}>SIGNAL LOST</SignalLostText>
              </SignalLostOverlay>
            )}
            {!effectiveShowStatic && isStale && !isDestroyed && (
              <>
                <StaleScrim aria-hidden="true" />
                <StaleBadge role="status" aria-label="Feed stale">
                  <StaleIcon />
                  Stale
                </StaleBadge>
              </>
            )}
            {showZoom && (
              <ZoomControlsWrap>
                <ZoomButton
                  type="button"
                  aria-label="Zoom in"
                  $pos="top"
                  onPointerDown={() =>
                    controllerRef.current?.setZoomRate(1)
                  }
                  onPointerUp={() => controllerRef.current?.setZoomRate(0)}
                  onPointerLeave={() => controllerRef.current?.setZoomRate(0)}
                  onPointerCancel={() => controllerRef.current?.setZoomRate(0)}
                  onClick={(e) => {
                    if (e.detail === 0) controllerRef.current?.nudgeZoom(-1);
                  }}
                >
                  +
                </ZoomButton>
                <FovSlider
                  type="range"
                  aria-label="Zoom"
                  min={camera.fovMin}
                  max={camera.fovMax}
                  step={0.5}
                  value={sliderFov}
                  onChange={(e) => {
                    const v = Number(e.target.value);
                    controllerRef.current?.fovSliderInput(v);
                  }}
                  onPointerDown={() => {
                    controllerRef.current?.setFovSliderDragging(true);
                  }}
                  onPointerUp={() => {
                    controllerRef.current?.setFovSliderDragging(false);
                  }}
                  onPointerCancel={() => {
                    controllerRef.current?.setFovSliderDragging(false);
                  }}
                />
                <ZoomButton
                  type="button"
                  aria-label="Zoom out"
                  $pos="bottom"
                  onPointerDown={() => controllerRef.current?.setZoomRate(-1)}
                  onPointerUp={() => controllerRef.current?.setZoomRate(0)}
                  onPointerLeave={() => controllerRef.current?.setZoomRate(0)}
                  onPointerCancel={() => controllerRef.current?.setZoomRate(0)}
                  onClick={(e) => {
                    if (e.detail === 0) controllerRef.current?.nudgeZoom(1);
                  }}
                >
                  &#8722;
                </ZoomButton>
              </ZoomControlsWrap>
            )}
            {showPan && (
              <PanControl role="group" aria-label="Pan camera">
                <PanArrow
                  type="button"
                  $dir="up"
                  aria-label="Pan up"
                  disabled={!supportsPitch}
                  onClick={() => controllerRef.current?.nudgePan(0, 1)}
                >
                  &#9650;
                </PanArrow>
                <PanArrow
                  type="button"
                  $dir="down"
                  aria-label="Pan down"
                  disabled={!supportsPitch}
                  onClick={() => controllerRef.current?.nudgePan(0, -1)}
                >
                  &#9660;
                </PanArrow>
                <PanArrow
                  type="button"
                  $dir="left"
                  aria-label="Pan left"
                  onClick={() => controllerRef.current?.nudgePan(-1, 0)}
                >
                  &#9664;
                </PanArrow>
                <PanArrow
                  type="button"
                  $dir="right"
                  aria-label="Pan right"
                  onClick={() => controllerRef.current?.nudgePan(1, 0)}
                >
                  &#9654;
                </PanArrow>
                <PanBall
                  aria-hidden="true"
                  title="Drag to pan"
                  onPointerDown={handleBallDown}
                  onPointerMove={handleBallMove}
                  onPointerUp={handleBallUp}
                  onPointerCancel={handleBallUp}
                  style={{
                    transform: `translate(${ballPos.x}px, ${ballPos.y}px)`,
                  }}
                />
              </PanControl>
            )}
          </>
        )}
      </Stage>
    );
  },
);

// ---------------------------------------------------------------------------
// Public CameraFeed - wraps inner in a nested provider when client prop given
// ---------------------------------------------------------------------------

/**
 * Display a live kerbcast camera feed with pan/zoom controls and camera picker.
 * Must be rendered inside a `KerbcastProvider` unless the `client` prop is
 * provided (which creates an implicit inner provider for this feed only).
 */
export const CameraFeed = forwardRef<CameraFeedHandle, CameraFeedProps>(
  function CameraFeed({ client, ...rest }, ref) {
    if (client) {
      return (
        <KerbcastProvider client={client}>
          <CameraFeedInner ref={ref} {...rest} />
        </KerbcastProvider>
      );
    }
    return <CameraFeedInner ref={ref} {...rest} />;
  },
);

// ---------------------------------------------------------------------------
// Inline UI primitives (replacing @gonogo/ui dependencies)
// ---------------------------------------------------------------------------

function ChevronDownIcon(props: React.SVGProps<SVGSVGElement>) {
  return (
    <svg
      viewBox="0 0 16 16"
      fill="currentColor"
      aria-hidden="true"
      {...props}
    >
      <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth={1.5} fill="none" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

const iconProps = {
  viewBox: "0 0 16 16",
  width: 14,
  height: 14,
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 1.6,
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
  "aria-hidden": true as const,
};

function FullscreenEnterIcon() {
  return (
    <svg {...iconProps}>
      <path d="M2 6V2.5h3.5M14 6V2.5h-3.5M2 10v3.5h3.5M14 10v3.5h-3.5" />
    </svg>
  );
}

function FullscreenExitIcon() {
  return (
    <svg {...iconProps}>
      <path d="M5.5 2v3.5H2M10.5 2v3.5H14M5.5 14v-3.5H2M10.5 14v-3.5H14" />
    </svg>
  );
}

/* Quality control: three horizontal slider rails with offset knobs. */
function QualityIcon() {
  return (
    <svg {...iconProps}>
      <path d="M2 4.5h12M2 8h12M2 11.5h12" />
      <circle cx="10.5" cy="4.5" r="1.6" fill="currentColor" stroke="none" />
      <circle cx="5.5" cy="8" r="1.6" fill="currentColor" stroke="none" />
      <circle cx="8.5" cy="11.5" r="1.6" fill="currentColor" stroke="none" />
    </svg>
  );
}

/* Stale badge: fading signal bars (tallest dimmed). */
function StaleIcon() {
  return (
    <svg
      viewBox="0 0 16 16"
      width={10}
      height={10}
      fill="currentColor"
      stroke="none"
      aria-hidden="true"
    >
      <rect x="2" y="9" width="2.5" height="5" />
      <rect x="6.5" y="6" width="2.5" height="8" />
      <rect x="11" y="3" width="2.5" height="11" opacity="0.35" />
    </svg>
  );
}

function PictureInPictureIcon() {
  return (
    <svg {...iconProps}>
      <rect x="2" y="3" width="12" height="10" rx="1" />
      <rect x="8" y="8" width="5" height="4" rx="0.5" fill="currentColor" stroke="none" />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Styled components
// ---------------------------------------------------------------------------

const PanControl = styled.div`
  position: absolute;
  bottom: 10px;
  right: 10px;
  width: 52px;
  height: 52px;
  opacity: 0;
  transition: opacity 0.15s;
  touch-action: none;

  @media (prefers-reduced-motion: reduce) {
    transition: none;
  }
`;

const PanArrow = styled.button<{ $dir: "up" | "down" | "left" | "right" }>`
  position: absolute;
  width: 16px;
  height: 16px;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0;
  font-size: 11px;
  line-height: 1;
  color: #fff;
  opacity: 0.5;
  background: none;
  border: none;
  cursor: pointer;
  touch-action: none;
  text-shadow:
    0 0 3px rgba(0, 0, 0, 0.9),
    0 1px 2px rgba(0, 0, 0, 0.8);

  ${(p) =>
    p.$dir === "up"
      ? "top: 0; left: 50%; transform: translateX(-50%);"
      : p.$dir === "down"
        ? "bottom: 0; left: 50%; transform: translateX(-50%);"
        : p.$dir === "left"
          ? "left: 0; top: 50%; transform: translateY(-50%);"
          : "right: 0; top: 50%; transform: translateY(-50%);"}

  @media (hover: hover) {
    &:hover:not(:disabled) {
      opacity: 1;
      color: var(--kerbcast-accent, #00ff88);
    }
  }
  &:disabled {
    opacity: 0.3;
    cursor: default;
  }
  &:focus-visible {
    outline: 2px solid var(--kerbcast-accent, #00ff88);
    outline-offset: 2px;
  }
`;

const PanBall = styled.div`
  position: absolute;
  top: 50%;
  left: 50%;
  width: 12px;
  height: 12px;
  margin: -6px 0 0 -6px;
  border-radius: 50%;
  background: radial-gradient(circle at 35% 30%, #ffffff, #d6dbe1);
  box-shadow:
    0 0 0 1px rgba(0, 0, 0, 0.5),
    0 0 4px rgba(255, 255, 255, 0.4);
  cursor: grab;
  touch-action: none;

  &:active {
    cursor: grabbing;
  }
`;

const ZoomControlsWrap = styled.div`
  position: absolute;
  bottom: 8px;
  left: 8px;
  width: 30px;
  display: flex;
  flex-direction: column;
  align-items: stretch;
  background: rgba(0, 0, 0, 0.6);
  border: 1px solid rgba(255, 255, 255, 0.5);
  opacity: 0;
  transition: opacity 0.15s;

  @media (prefers-reduced-motion: reduce) {
    transition: none;
  }
`;

const ZoomButton = styled.button<{ $pos: "top" | "bottom" }>`
  width: 100%;
  height: 26px;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0;
  background: transparent;
  border: none;
  border-radius: 0;
  color: #fff;
  font-size: 1rem;
  cursor: pointer;
  ${(p) =>
    p.$pos === "top"
      ? "border-bottom: 1px solid rgba(255, 255, 255, 0.3);"
      : "border-top: 1px solid rgba(255, 255, 255, 0.3);"}

  @media (hover: hover) {
    &:hover {
      color: #fff;
      background: rgba(255, 255, 255, 0.15);
    }
  }

  &:focus-visible {
    outline: 2px solid #fff;
    outline-offset: -2px;
  }
`;

const TopOverlay = styled.div`
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  z-index: 2;
  display: flex;
  flex-direction: column;
  gap: 3px;
  padding: 6px 8px 14px;
  background: linear-gradient(to bottom, rgba(0, 0, 0, 0.78), rgba(0, 0, 0, 0));
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.15s;

  @media (prefers-reduced-motion: reduce) {
    transition: none;
  }
`;

const TitleRow = styled.div`
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
`;

const TopTitle = styled.h3`
  margin: 0;
  min-width: 0;
  font-size: var(--font-size-xs, 11px);
  font-weight: 600;
  letter-spacing: 0.12em;
  text-transform: uppercase;
  color: #fff;
  text-shadow: 0 1px 2px rgba(0, 0, 0, 0.9);
`;

const TitleButton = styled.button`
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  margin: 0;
  padding: 0;
  background: none;
  border: none;
  cursor: pointer;
  color: inherit;
  font: inherit;
  letter-spacing: inherit;
  text-transform: inherit;
  text-shadow: inherit;
  text-align: left;

  svg {
    width: 12px;
    height: 12px;
    flex-shrink: 0;
    transition: transform 0.15s;
  }

  &[aria-expanded="true"] svg {
    transform: rotate(180deg);
  }

  @media (prefers-reduced-motion: reduce) {
    svg {
      transition: none;
    }
  }

  &:focus-visible {
    outline: 2px solid var(--kerbcast-accent, #00ff88);
    outline-offset: 2px;
  }
`;

const TitleButton__Text = styled.span`
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
`;

const StepButtons = styled.div`
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
`;

const CameraMenu = styled.div`
  /* Portaled to document.body: fixed position (set inline from the trigger
     rect) keeps it clear of the tile's overflow clipping. */
  position: fixed;
  z-index: 1000;
  max-width: min(260px, calc(100vw - 16px));
  /* Cap the list so a long camera roster scrolls instead of spilling past
     the tile/viewport. 40vh keeps it sane on short windows; 300px on tall. */
  max-height: min(40vh, 300px);
  display: flex;
  flex-direction: column;
  background: rgba(0, 0, 0, 0.85);
  border: 1px solid rgba(255, 255, 255, 0.3);
  border-radius: 4px;
  overflow-x: hidden;
  overflow-y: auto;
`;

const CameraMenuItem = styled.button<{ $selected: boolean }>`
  display: block;
  width: 100%;
  flex-shrink: 0;
  padding: 6px 8px;
  text-align: left;
  background: ${(p) =>
    p.$selected
      ? "var(--kerbcast-accent-wash, rgba(0, 255, 136, 0.15))"
      : "transparent"};
  border: none;
  cursor: pointer;
  color: #fff;
  font-size: 11px;
  letter-spacing: 0.04em;

  @media (hover: hover) {
    &:hover {
      background: rgba(255, 255, 255, 0.15);
    }
  }

  &:focus-visible {
    outline: 2px solid var(--kerbcast-accent, #00ff88);
    outline-offset: -2px;
  }
`;

const TopMeta = styled.div`
  font-size: 11px;
  letter-spacing: 0.04em;
  color: rgba(255, 255, 255, 0.78);
  text-shadow: 0 1px 2px rgba(0, 0, 0, 0.9);
`;

/* The quality picker: portaled to document.body like CameraMenu, hung from
   its action-bar trigger (fixed position set inline from the trigger rect)
   so tile clipping cannot cut it off and it survives the hover-revealed
   chrome fading while the pointer is over the menu. */
const QualityMenu = styled.div`
  position: fixed;
  z-index: 1000;
  min-width: 140px;
  max-width: min(220px, calc(100vw - 16px));
  max-height: min(40vh, 220px);
  display: flex;
  flex-direction: column;
  background: rgba(0, 0, 0, 0.85);
  border: 1px solid rgba(255, 255, 255, 0.3);
  border-radius: 4px;
  overflow-x: hidden;
  overflow-y: auto;
`;

/* Effective-state footer of the quality menu: what the camera is actually
   rendering right now, with a "throttled" marker while the adaptive
   machinery holds it below the request. */
const QualityMeta = styled.div`
  padding: 5px 8px;
  border-top: 1px solid rgba(255, 255, 255, 0.2);
  font-size: 10px;
  letter-spacing: 0.04em;
  color: rgba(255, 255, 255, 0.65);
`;

/* Throttled marker on the quality action button. */
const ThrottledDot = styled.span`
  position: absolute;
  top: -3px;
  right: -3px;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: #ffb347;
  box-shadow: 0 0 0 1px rgba(0, 0, 0, 0.6);
`;

/* Top-right cluster of overlay controls (custom actions + fullscreen/PiP). */
const ActionBar = styled.div`
  position: absolute;
  top: 6px;
  right: 8px;
  z-index: 3;
  display: flex;
  align-items: center;
  gap: 4px;
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.15s;

  @media (prefers-reduced-motion: reduce) {
    transition: none;
  }
`;

/*
 * Outer frame. Fills the host container: the video is absolutely
 * positioned, so without explicit width/height the Stage collapses
 * to zero height.
 */
const Panel = styled.div`
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
  box-sizing: border-box;
  background: #111;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 4px;
  overflow: hidden;
`;

const Stage = styled(Panel)<{ $pinned: boolean }>`
  padding: 0;
  gap: 0;
  position: relative;
  background: #000;
  align-items: center;
  justify-content: center;

  &:hover ${TopOverlay},
  &:focus-within ${TopOverlay},
  &:hover ${ZoomControlsWrap},
  &:focus-within ${ZoomControlsWrap},
  &:hover ${PanControl},
  &:focus-within ${PanControl},
  &:hover ${ActionBar},
  &:focus-within ${ActionBar} {
    opacity: 1;
  }
  &:hover ${TopOverlay},
  &:focus-within ${TopOverlay},
  &:hover ${ActionBar},
  &:focus-within ${ActionBar} {
    pointer-events: auto;
  }

  ${(p) =>
    p.$pinned &&
    css`
      ${TopOverlay} {
        opacity: 1;
        pointer-events: auto;
      }
      ${ActionBar} {
        opacity: 1;
        pointer-events: auto;
      }
      ${ZoomControlsWrap},
      ${PanControl} {
        opacity: 1;
      }
    `}
`;

const Empty = styled.div`
  color: #888;
  font-size: 13px;
  font-style: italic;
  padding: 1rem;
  text-align: center;
`;

const OverlayIconButton = styled.button<{ $active?: boolean }>`
  position: relative;
  width: 24px;
  height: 24px;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0;
  background: ${(p) =>
    p.$active
      ? "var(--kerbcast-accent, #00ff88)"
      : "rgba(0, 0, 0, 0.5)"};
  border: 1px solid
    ${(p) =>
      p.$active
        ? "var(--kerbcast-accent, #00ff88)"
        : "rgba(255, 255, 255, 0.3)"};
  border-radius: 3px;
  color: ${(p) => (p.$active ? "#000" : "#fff")};
  cursor: pointer;
  transition: background 0.12s, border-color 0.12s, color 0.12s;

  @media (hover: hover) {
    &:hover {
      background: ${(p) =>
        p.$active
          ? "var(--kerbcast-accent, #00ff88)"
          : "rgba(0, 0, 0, 0.7)"};
      border-color: rgba(255, 255, 255, 0.6);
    }
  }

  &:focus-visible {
    outline: 2px solid var(--kerbcast-accent, #00ff88);
    outline-offset: 2px;
  }

  &:disabled {
    opacity: 0.4;
  }

  @media (prefers-reduced-motion: reduce) {
    transition: none;
  }
`;

const StyledVideo = styled.video`
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
`;

/**
 * Shown when the sidecar reports `lifecycle: "destroyed"`. The kerbcast SDK
 * keeps the camera's noise pipeline alive on the same `mediaStream`, so the
 * video behind this overlay shows live signal-loss static.
 */
const SignalLostOverlay = styled.div`
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.25);
`;

const SignalLostText = styled.span<{ $animated: boolean }>`
  color: #ff4444;
  font-size: 1.1rem;
  font-weight: 700;
  letter-spacing: 0.15em;
  text-transform: uppercase;
  text-shadow:
    0 0 8px rgba(255, 68, 68, 0.7),
    0 1px 2px rgba(0, 0, 0, 0.9);

  ${(p) =>
    p.$animated &&
    `
    @media (prefers-reduced-motion: no-preference) {
      animation: signal-lost-pulse 2s ease-in-out infinite;
    }
  `}

  @keyframes signal-lost-pulse {
    0%,
    100% {
      opacity: 1;
    }
    50% {
      opacity: 0.6;
    }
  }
`;

/*
 * No-static stall presentation (`showStatic={false}`): a subtle dim over
 * the frozen last frame plus a corner badge, so a frozen frame is never
 * mistakable for a live one. Always visible (not hover chrome).
 */
const StaleScrim = styled.div`
  position: absolute;
  inset: 0;
  z-index: 1;
  background: rgba(0, 0, 0, 0.32);
  pointer-events: none;
`;

const StaleBadge = styled.div`
  position: absolute;
  bottom: 8px;
  right: 8px;
  z-index: 2;
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  background: rgba(0, 0, 0, 0.6);
  border: 1px solid rgba(255, 179, 71, 0.6);
  border-radius: 3px;
  color: #ffb347;
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.12em;
  text-transform: uppercase;
  text-shadow: 0 1px 2px rgba(0, 0, 0, 0.9);
  pointer-events: none;
`;

const FovSlider = styled.input`
  writing-mode: vertical-lr;
  width: 100%;
  height: 54px;
  margin: 0;
  padding: 3px 0;
  cursor: pointer;
  accent-color: #fff;

  &:focus-visible {
    outline: 2px solid #fff;
    outline-offset: -2px;
  }
`;
