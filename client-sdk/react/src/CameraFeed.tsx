import type { KerbcamClient } from "@jonpepler/kerbcam";
import { PanZoomController } from "@jonpepler/kerbcam";
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
import styled, { css } from "styled-components";
import { buildCameraLabeler } from "./cameraLabels";
import { KerbcamProvider, useKerbcamClient } from "./context";
import { useKerbcamCameras } from "./hooks/useKerbcamCameras";
import { useKerbcamStream } from "./hooks/useKerbcamStream";
import { isCameraDestroyed } from "./lifecycle";

// ---------------------------------------------------------------------------
// Tuning constants
// ---------------------------------------------------------------------------
const PAN_BALL_RADIUS = 15; // pixel deflection bound (full = rate 1)

/** Round n to the nearest even integer, minimum 2 (H.264 chroma requirement). */
function toEvenPx(n: number): number {
  return Math.max(2, Math.round(n / 2) * 2);
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

export interface CameraFeedProps {
  /** Override the context client for this feed only. */
  client?: KerbcamClient;
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
  /** Show resolution + encoder readout. Default false. */
  showDebugInfo?: boolean;
  /**
   * "auto" (default): ResizeObserver drives `setRenderSize` at a 16:9 crop,
   * debounced 500 ms. "none": no render-size feedback.
   */
  renderSize?: "auto" | "none";
  /** Message shown when no cameras are available. */
  emptyMessage?: string;
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
      showDebugInfo = false,
      renderSize = "auto",
      emptyMessage = "No camera feeds - start a vessel with Hullcam parts installed",
    },
    ref,
  ) {
    const client = useKerbcamClient();
    const cameras = useKerbcamCameras();

    // -------------------------------------------------------------------------
    // Selection model (mirrors gonogo's CameraFeed)
    //
    // Resolution order:
    //   1. Explicit pick, if still present in the list (destroyed or not).
    //   2. Auto: latch the currently-displayed camera. Keep showing it even if
    //      destroyed; the latch releases only if that flightId leaves the list.
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
      const latchedPresent =
        latched !== null && cameras.some((c) => c.flightId === latched);
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

    // Commit on-screen camera for the auto-mode latch.
    useEffect(() => {
      displayedRef.current = flightId;
    }, [flightId]);

    const stream = useKerbcamStream(flightId);
    const videoRef = useRef<HTMLVideoElement>(null);
    useEffect(() => {
      if (videoRef.current && stream) {
        videoRef.current.srcObject = stream;
      }
    }, [stream]);

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
    const wrapRef = useRef<HTMLDivElement>(null);
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
    const [menuOpen, setMenuOpen] = useState(false);
    const menuRef = useRef<HTMLDivElement>(null);
    const menuTriggerRef = useRef<HTMLButtonElement>(null);

    // Escape closes the menu; outside pointer-down dismisses it.
    useEffect(() => {
      if (!menuOpen) return;
      const onKeyDown = (e: KeyboardEvent) => {
        if (e.key === "Escape") {
          e.stopPropagation();
          setMenuOpen(false);
          menuTriggerRef.current?.focus();
        }
      };
      const onPointerDown = (e: PointerEvent) => {
        if (
          !menuRef.current?.contains(e.target as Node) &&
          !menuTriggerRef.current?.contains(e.target as Node)
        ) {
          setMenuOpen(false);
        }
      };
      document.addEventListener("keydown", onKeyDown);
      document.addEventListener("pointerdown", onPointerDown);
      return () => {
        document.removeEventListener("keydown", onKeyDown);
        document.removeEventListener("pointerdown", onPointerDown);
      };
    }, [menuOpen]);

    const cameraLabel = useMemo(() => buildCameraLabeler(cameras), [cameras]);
    const title = camera ? cameraLabel(camera) : "Camera Feed";

    const topOverlay = (
      <TopOverlay>
        <TitleRow>
          <TopTitle>
            {hasCameras ? (
              <TitleButton
                ref={menuTriggerRef}
                type="button"
                aria-haspopup="menu"
                aria-expanded={menuOpen}
                aria-controls={menuId}
                onClick={() => setMenuOpen((v) => !v)}
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

        {menuOpen && hasCameras && (
          <CameraMenu
            ref={menuRef}
            id={menuId}
            role="menu"
            aria-label="Camera"
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
                  setMenuOpen(false);
                  menuTriggerRef.current?.focus();
                }}
              >
                {cameraLabel(c)} ({c.vesselName})
                {isCameraDestroyed(c) ? " - signal lost" : ""}
              </CameraMenuItem>
            ))}
          </CameraMenu>
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

    return (
      <Stage ref={wrapRef} $pinned={chromePinned}>
        {flightId === null ? (
          <>
            <Empty>{emptyMessage}</Empty>
            {topOverlay}
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
            {isDestroyed && (
              <SignalLostOverlay role="status" aria-label="Signal lost">
                <SignalLostText>SIGNAL LOST</SignalLostText>
              </SignalLostOverlay>
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
 * Display a live kerbcam camera feed with pan/zoom controls and camera picker.
 * Must be rendered inside a `KerbcamProvider` unless the `client` prop is
 * provided (which creates an implicit inner provider for this feed only).
 */
export const CameraFeed = forwardRef<CameraFeedHandle, CameraFeedProps>(
  function CameraFeed({ client, ...rest }, ref) {
    if (client) {
      return (
        <KerbcamProvider client={client}>
          <CameraFeedInner ref={ref} {...rest} />
        </KerbcamProvider>
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
      color: var(--kerbcam-accent, #00ff88);
    }
  }
  &:disabled {
    opacity: 0.3;
    cursor: default;
  }
  &:focus-visible {
    outline: 2px solid var(--kerbcam-accent, #00ff88);
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
    outline: 2px solid var(--kerbcam-accent, #00ff88);
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
  margin-top: 4px;
  max-width: 260px;
  display: flex;
  flex-direction: column;
  background: rgba(0, 0, 0, 0.85);
  border: 1px solid rgba(255, 255, 255, 0.3);
  border-radius: 4px;
  overflow: hidden;
`;

const CameraMenuItem = styled.button<{ $selected: boolean }>`
  display: block;
  width: 100%;
  padding: 6px 8px;
  text-align: left;
  background: ${(p) =>
    p.$selected
      ? "var(--kerbcam-accent-wash, rgba(0, 255, 136, 0.15))"
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
    outline: 2px solid var(--kerbcam-accent, #00ff88);
    outline-offset: -2px;
  }
`;

const TopMeta = styled.div`
  font-size: 11px;
  letter-spacing: 0.04em;
  color: rgba(255, 255, 255, 0.78);
  text-shadow: 0 1px 2px rgba(0, 0, 0, 0.9);
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
  &:focus-within ${PanControl} {
    opacity: 1;
  }
  &:hover ${TopOverlay},
  &:focus-within ${TopOverlay} {
    pointer-events: auto;
  }

  ${(p) =>
    p.$pinned &&
    css`
      ${TopOverlay} {
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

const OverlayIconButton = styled.button`
  width: 24px;
  height: 24px;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0;
  background: rgba(0, 0, 0, 0.5);
  border: 1px solid rgba(255, 255, 255, 0.3);
  border-radius: 3px;
  color: #fff;
  cursor: pointer;

  &:disabled {
    opacity: 0.4;
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
 * Shown when the sidecar reports `lifecycle: "destroyed"`. The kerbcam SDK
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

const SignalLostText = styled.span`
  color: #ff4444;
  font-size: 1.1rem;
  font-weight: 700;
  letter-spacing: 0.15em;
  text-transform: uppercase;
  text-shadow:
    0 0 8px rgba(255, 68, 68, 0.7),
    0 1px 2px rgba(0, 0, 0, 0.9);

  @media (prefers-reduced-motion: no-preference) {
    animation: signal-lost-pulse 2s ease-in-out infinite;
  }

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
