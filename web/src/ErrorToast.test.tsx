import type { KerbcastClient, RecordingEndedEvent, RecordingHandle } from "@ksp-gonogo/kerbcast";
import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { ErrorToast } from "./ErrorToast";

function fakeClient(): { client: KerbcastClient; end: (event: RecordingEndedEvent) => void } {
  let ended: ((event: RecordingEndedEvent) => void) | null = null;
  const client = {
    on: () => () => {},
    recording: {
      onRecordingEnded: (fn: (event: RecordingEndedEvent) => void) => {
        ended = fn;
        return () => {
          ended = null;
        };
      },
    },
  } as unknown as KerbcastClient;
  return { client, end: (event) => act(() => ended?.(event)) };
}

const clip = { recordingId: "rec-1", flightId: 1 } as RecordingHandle;

afterEach(cleanup);

describe("ErrorToast recording notices", () => {
  it("says when a recording stopped because its camera went away or the connection was lost", () => {
    const { client, end } = fakeClient();
    render(<ErrorToast client={client} />);
    end({ recording: clip, endedBy: "camera-gone" });
    end({ recording: clip, endedBy: "disconnected" });
    end({ recording: clip, endedBy: "feed-closed" });
    expect(screen.getByText(/its camera went away/)).toBeTruthy();
    expect(screen.getByText(/its feed was closed/)).toBeTruthy();
    expect(screen.getByText(/the connection was lost/)).toBeTruthy();
  });

  it("stays quiet for a recording a person stopped or that ran its length", () => {
    const { client, end } = fakeClient();
    render(<ErrorToast client={client} />);
    end({ recording: clip, endedBy: "user" });
    end({ recording: clip, endedBy: "max-duration" });
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("gives a REC+ set that lost its cameras one notice", () => {
    const { client, end } = fakeClient();
    render(<ErrorToast client={client} />);
    end({ recording: clip, groupId: "grp-1", endedBy: "camera-gone" });
    end({ recording: { ...clip, recordingId: "rec-2" }, groupId: "grp-1", endedBy: "camera-gone" });
    expect(screen.getAllByRole("status")).toHaveLength(1);
  });
});
