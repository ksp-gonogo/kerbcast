import path from "path";
import { defineConfig } from "vitest/config";

export default defineConfig({
  resolve: {
    alias: {
      "@ksp-gonogo/kerbcast/testing": path.resolve(
        __dirname,
        "../client-sdk/typescript/src/testing/index.ts",
      ),
      "@ksp-gonogo/kerbcast": path.resolve(
        __dirname,
        "../client-sdk/typescript/src/index.ts",
      ),
      "@ksp-gonogo/kerbcast-react": path.resolve(
        __dirname,
        "../client-sdk/react/src/index.ts",
      ),
    },
  },
  test: {
    name: "kerbcast-web",
    environment: "jsdom",
    globals: true,
    include: ["src/**/*.test.ts", "src/**/*.test.tsx"],
    exclude: ["dist/**", "node_modules/**"],
    setupFiles: ["src/test/setup.ts"],
  },
});
