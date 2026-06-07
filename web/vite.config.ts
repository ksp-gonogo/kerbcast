import react from "@vitejs/plugin-react";
import path from "path";
import { defineConfig } from "vite";
import checker from "vite-plugin-checker";
import { viteSingleFile } from "vite-plugin-singlefile";

export default defineConfig(({ command }) => ({
  plugins: [
    react(),
    // Typecheck only in dev (build has tsc --noEmit as a separate step)
    ...(command === "serve"
      ? [checker({ typescript: true })]
      : []),
    viteSingleFile(),
  ],
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
  build: {
    outDir: "dist",
    rollupOptions: {
      input: "index.html",
    },
  },
  server: {
    proxy: {
      "/cameras": "http://127.0.0.1:8088",
      "/offer": "http://127.0.0.1:8088",
      "/profile": "http://127.0.0.1:8088",
      "/health": "http://127.0.0.1:8088",
      "/ice-config": "http://127.0.0.1:8088",
    },
  },
}));
