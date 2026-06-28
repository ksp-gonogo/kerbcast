# kerbcast web

The sidecar's built-in browser UI. Served by the sidecar at `GET /`. Targets
fresh KSP users: auto-connect on load, stream-first camera grid, camera
picker, dev controls behind a settings toggle.

Built with Vite + React, using `@jonpepler/kerbcast` and
`@jonpepler/kerbcast-react` from this same workspace.

## Dev workflow

```sh
# Start with a real sidecar on localhost:8088
pnpm --filter kerbcast-web dev

# Start without a sidecar (mock mode -- full UI, simulated cameras)
pnpm --filter kerbcast-web dev
# then open http://localhost:5173/?mock=1
```

HMR is on; TypeScript errors surface in the Vite overlay and the terminal
(vite-plugin-checker).

## Tests

```sh
pnpm --filter kerbcast-web test
```

Runs 27 vitest tests under jsdom + testing-library, driven by a real
`KerbcastClient` against `MockSidecar`.

## Build and freshness rule

```sh
pnpm --filter kerbcast-web build
```

`vite-plugin-singlefile` bundles the entire app (JS + CSS) into one
self-contained `web/dist/index.html`. That file is committed to the repo;
the sidecar embeds it at compile time via `include_str!` -- no Node in the
cargo build.

The committed file must stay current with the source. CI (`web-ci.yml`)
rebuilds and gates on `git diff --exit-code web/dist/index.html`. If the
check fails, rebuild locally and commit the result. `scripts/bump-version.sh`
also rebuilds the page automatically before each release.
