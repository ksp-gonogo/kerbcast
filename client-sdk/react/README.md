# @ksp-gonogo/kerbcast-react

React components and hooks for [kerbcast](https://github.com/ksp-gonogo/kerbcast) camera feeds.

This package provides a shared `CameraFeed` component (and related hooks) consumed by
both gonogo's mission-control dashboard and the kerbcast sidecar's embedded web page.

## Install

Published to public npm. No registry route or auth needed:

```
pnpm add @ksp-gonogo/kerbcast-react
```

Peer dependencies required: `react` (18 or 19), `react-dom`, `styled-components` (6).

## Purpose

Extracts camera-feed UI from gonogo into a reusable layer so both gonogo and the
sidecar page consume the same component without duplicating protocol or rendering code.

## Versioning

This package tracks `@ksp-gonogo/kerbcast` version for version. Use matching versions of both.
`./scripts/bump-version.sh` bumps them atomically.
