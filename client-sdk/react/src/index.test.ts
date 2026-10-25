import { describe, it, expect } from "vitest";

// Smoke test: the package module resolves and its shape is what consumers
// expect. Real component tests land with the CameraFeed extraction.
describe("@ksp-gonogo/kerbcast-react", () => {
  /* A cold import transforms the whole package, which can exceed the default timeout on a busy machine. */
  it("module resolves", async () => {
    const mod = await import("./index");
    expect(mod).toBeDefined();
  }, 30_000);
});
