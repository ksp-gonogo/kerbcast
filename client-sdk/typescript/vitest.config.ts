import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    name: "kerbcast-protocol",
    environment: "jsdom",
    globals: true,
    include: ["src/**/*.test.ts"],
    exclude: ["dist/**", "node_modules/**"],
  },
});
