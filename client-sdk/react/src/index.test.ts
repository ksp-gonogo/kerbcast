import { describe, it, expect } from "vitest";

// Smoke test: the package module resolves and its shape is what consumers
// expect. Real component tests land with the CameraFeed extraction.
describe("@jonpepler/kerbcam-react", () => {
  it("module resolves", async () => {
    const mod = await import("./index");
    expect(mod).toBeDefined();
  });
});
