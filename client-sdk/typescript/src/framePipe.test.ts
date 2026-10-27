// @vitest-environment node
/*
 * The legacy createEncodedStreams pipe: a frame copy must never cost the
 * live picture, whatever the tap does.
 */
import { describe, expect, it, vi } from "vitest";
import { SwitchableFramePipe } from "./client";

function fakeFrame(n: number): RTCEncodedVideoFrame {
  return {
    data: new Uint8Array([n]).buffer,
    type: "delta",
    getMetadata: () => ({ rtpTimestamp: n * 3000, synchronizationSource: 1 }),
  } as unknown as RTCEncodedVideoFrame;
}

function pipeHarness() {
  let feed!: ReadableStreamDefaultController<RTCEncodedVideoFrame>;
  const readable = new ReadableStream<RTCEncodedVideoFrame>({
    start: (c) => {
      feed = c;
    },
  });
  const decoded: RTCEncodedVideoFrame[] = [];
  const writable = new WritableStream<RTCEncodedVideoFrame>({
    write: (f) => {
      decoded.push(f);
    },
  });
  const pipe = new SwitchableFramePipe({ readable, writable });
  return { pipe, feed, decoded };
}

const settle = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

describe("SwitchableFramePipe tap", () => {
  it("copies each frame to the tap with its RTP timestamp", async () => {
    const { pipe, feed, decoded } = pipeHarness();
    const copies: number[] = [];
    pipe.tap = (f) => copies.push(f.rtpTimestamp);
    feed.enqueue(fakeFrame(1));
    feed.enqueue(fakeFrame(2));
    await settle();
    expect(decoded).toHaveLength(2);
    expect(copies).toEqual([3000, 6000]);
  });

  it("keeps frames flowing to the decoder when the tap throws", async () => {
    const { pipe, feed, decoded } = pipeHarness();
    const errSpy = vi.spyOn(console, "error").mockImplementation(() => {});
    pipe.tap = () => {
      throw new Error("boom");
    };
    for (let i = 0; i < 5; i++) feed.enqueue(fakeFrame(i));
    await settle();
    expect(decoded).toHaveLength(5);
    pipe.tap = null;
    feed.enqueue(fakeFrame(9));
    await settle();
    expect(decoded).toHaveLength(6);
    errSpy.mockRestore();
  });

  it("keeps frames flowing when copying a frame fails", async () => {
    const { pipe, feed, decoded } = pipeHarness();
    const tap = vi.fn();
    pipe.tap = tap;
    feed.enqueue({
      get data(): ArrayBuffer {
        throw new Error("detached");
      },
      getMetadata: () => ({}),
    } as unknown as RTCEncodedVideoFrame);
    feed.enqueue(fakeFrame(1));
    await settle();
    expect(decoded).toHaveLength(2);
    expect(tap).toHaveBeenCalledTimes(1);
  });
});
