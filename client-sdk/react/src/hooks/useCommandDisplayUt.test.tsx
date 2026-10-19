/**
 * Display UT for command feeds and the displayed-frame UT hook: the presented
 * frame's own capture stamp wins over the capture clock, every value says
 * which it is, and a host override beats both.
 */

import { KerbcastClient, type CommandTrackEvent } from "@ksp-gonogo/kerbcast";
import { MockSidecar } from "@ksp-gonogo/kerbcast/testing";
import { act, cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { KerbcastProvider } from "../context";
import { crossesEvent, useCommandDisplayUt, type CommandDisplayUt } from "./useCommandDisplayUt";
import { useDisplayedFrameUt } from "./useDisplayedFrameUt";

async function connected() {
  const sidecar = new MockSidecar();
  const client = new KerbcastClient(
    { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
    sidecar.createTransport(),
  );
  await act(async () => {
    await client.connect([], { slots: 4 });
    sidecar.open();
    sidecar.setConnectionState("connected");
  });
  sidecar.fireSettingsState({ throttleMainScreen: false, captureUt: 1000, timeWarpRate: 1 });
  return { client, sidecar };
}

/** A video element whose frames the test presents by hand. */
function fakeVideo() {
  let pending: ((now: number, meta: object) => void) | null = null;
  const video = {
    requestVideoFrameCallback: (cb: (now: number, meta: object) => void) => {
      pending = cb;
      return 1;
    },
    cancelVideoFrameCallback: () => {
      pending = null;
    },
  } as unknown as HTMLVideoElement;
  const present = (rtpTimestamp?: number) =>
    act(() => {
      const cb = pending;
      pending = null;
      cb?.(0, rtpTimestamp === undefined ? {} : { rtpTimestamp });
    });
  return { video, present };
}

const event = (ut: number): CommandTrackEvent =>
  ({ ut, occluded: true, sunlit: true, occluder: "", blank: "" }) as CommandTrackEvent;

let last: CommandDisplayUt | null | undefined;
function CommandProbe(props: {
  override?: number;
  events?: CommandTrackEvent[];
  video: HTMLVideoElement | null;
}) {
  last = useCommandDisplayUt(true, props.override, props.events, 7, props.video);
  return null;
}

afterEach(() => {
  cleanup();
  last = undefined;
});

describe("useCommandDisplayUt", () => {
  it("is estimated from the clock until an exactly stamped frame is shown", async () => {
    const { client, sidecar } = await connected();
    const { video, present } = fakeVideo();
    render(
      <KerbcastProvider client={client}>
        <CommandProbe video={video} />
      </KerbcastProvider>,
    );
    expect(last?.source).toBe("estimated");

    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 4500, captureUt: 999.8, captureEpoch: 0 });
    present(4500);
    expect(last).toEqual({ ut: 999.8, source: "exact" });

    /* An unstamped frame falls back to the clock, and says so. */
    present(9000);
    expect(last?.source).toBe("estimated");
  });

  it("re-renders when an exact frame steps across an event", async () => {
    const { client, sidecar } = await connected();
    const { video, present } = fakeVideo();
    render(
      <KerbcastProvider client={client}>
        <CommandProbe video={video} events={[event(999.9)]} />
      </KerbcastProvider>,
    );
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 1, captureUt: 999.8, captureEpoch: 0 });
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 2, captureUt: 999.85, captureEpoch: 0 });
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 3, captureUt: 999.95, captureEpoch: 0 });
    present(1);
    expect(last?.ut).toBe(999.8);
    /* Same side of the event: no re-render needed, the value shown holds. */
    present(2);
    expect(last?.ut).toBe(999.8);
    present(3);
    expect(last?.ut).toBe(999.95);
  });

  it("prefers a host override over the displayed frame", async () => {
    const { client, sidecar } = await connected();
    const { video, present } = fakeVideo();
    render(
      <KerbcastProvider client={client}>
        <CommandProbe video={video} override={990} />
      </KerbcastProvider>,
    );
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 1, captureUt: 999.8, captureEpoch: 0 });
    present(1);
    expect(last).toEqual({ ut: 990, source: "override" });
  });
});

describe("crossesEvent", () => {
  it("counts an event the display moves onto or past, in either direction", () => {
    const events = [event(10)];
    expect(crossesEvent(events, 9, 10)).toBe(true);
    expect(crossesEvent(events, 11, 9)).toBe(true);
    expect(crossesEvent(events, 10, 11)).toBe(false);
    expect(crossesEvent(undefined, 0, 100)).toBe(false);
  });
});

describe("useDisplayedFrameUt", () => {
  let frame: ReturnType<typeof useDisplayedFrameUt> | undefined;
  function FrameProbe({ video }: { video: HTMLVideoElement }) {
    frame = useDisplayedFrameUt(7, video);
    return null;
  }

  it("reports every presented frame with its source", async () => {
    const { client, sidecar } = await connected();
    const { video, present } = fakeVideo();
    render(
      <KerbcastProvider client={client}>
        <FrameProbe video={video} />
      </KerbcastProvider>,
    );
    expect(frame).toBeNull();
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 77, captureUt: 999.5, captureEpoch: 4 });
    present(77);
    expect(frame).toMatchObject({ ut: 999.5, epoch: 4, source: "exact", rtpTimestamp: 77 });
    /* Unstamped: bounded by the last exactly identified frame. */
    present(78);
    expect(frame).toMatchObject({ ut: 999.5, source: "estimated", rtpTimestamp: 77 });
  });
});
