import path from "path";
import { defineConfig } from "vitest/config";

export default defineConfig({
  resolve: {
    alias: {
      "@jonpepler/kerbcam/testing": path.resolve(
        __dirname,
        "../client-sdk/typescript/src/testing/index.ts",
      ),
      "@jonpepler/kerbcam": path.resolve(
        __dirname,
        "../client-sdk/typescript/src/index.ts",
      ),
      "@jonpepler/kerbcam-react": path.resolve(
        __dirname,
        "../client-sdk/react/src/index.ts",
      ),
    },
  },
  test: {
    name: "kerbcam-web",
    environment: "jsdom",
    globals: true,
    include: ["src/**/*.test.ts", "src/**/*.test.tsx"],
    exclude: ["dist/**", "node_modules/**"],
    setupFiles: ["src/test/setup.ts"],
  },
});
