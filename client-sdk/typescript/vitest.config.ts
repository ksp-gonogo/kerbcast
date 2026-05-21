import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    name: "kerbcam-protocol",
    environment: "jsdom",
    globals: true,
    include: ["src/**/*.test.ts"],
    exclude: ["dist/**", "node_modules/**"],
  },
});
