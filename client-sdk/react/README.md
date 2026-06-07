# @jonpepler/kerbcam-react

React components and hooks for [kerbcam](https://github.com/jonpepler/kerbcam) camera feeds.

This package provides a shared `CameraFeed` component (and related hooks) consumed by
both gonogo's mission-control dashboard and the kerbcam sidecar's embedded web page.

## Install

Published to [GitHub Packages](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-npm-registry).
Add the registry to your project:

```
@jonpepler:registry=https://npm.pkg.github.com
```

Then install:

```
pnpm add @jonpepler/kerbcam-react
```

Peer dependencies required: `react` (18 or 19), `react-dom`, `styled-components` (6).

## Purpose

Extracts camera-feed UI from gonogo into a reusable layer so both gonogo and the
sidecar page consume the same component without duplicating protocol or rendering code.

## Versioning

This package tracks `@jonpepler/kerbcam` version for version. Use matching versions of both.
`./scripts/bump-version.sh` bumps them atomically.
