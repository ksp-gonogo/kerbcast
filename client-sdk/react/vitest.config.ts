import { defineConfig } from "vitest/config";
import path from "path";

export default defineConfig({
  resolve: {
    alias: {
      // Resolve @ksp-gonogo/kerbcast imports to source so tests run without
      // a prior `pnpm -r build` in the monorepo.
      "@ksp-gonogo/kerbcast/testing": path.resolve(
        __dirname,
        "../typescript/src/testing/index.ts",
      ),
      "@ksp-gonogo/kerbcast": path.resolve(
        __dirname,
        "../typescript/src/index.ts",
      ),
    },
  },
  test: {
    name: "kerbcast-react",
    environment: "jsdom",
    globals: true,
    include: ["src/**/*.test.ts", "src/**/*.test.tsx"],
    exclude: ["dist/**", "node_modules/**"],
    setupFiles: ["src/test/setup.ts"],
  },
});
