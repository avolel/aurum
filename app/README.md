# app — Expo shell (not yet the Phase 1 app)

An Expo shell with one thing worth keeping in it: `src/fixture/`, a seeded generator for synthetic
gold ticks.

This directory is **untracked on purpose**. It is scaffolding until the Phase 1 dashboard tranche
starts, at which point it gets rebuilt and committed.

## What happened to the D-2 spike

This used to be a three-way comparison harness — Skia vs `lightweight-charts` vs `victory-native`,
measured against 30,000 ticks. D-2 was decided without it: web goes to `lightweight-charts`, and the
question the spike actually existed to answer — whether one implementation can serve web *and*
native, or whether the product carries two — moves to Phase 4, where a physical device is in play.
Measuring now would produce numbers on a device we do not have, for a codebase we have not written.

Removed: `src/harness/` (frame sampling and gesture scripts, both unimplemented), `src/screens/`
(all three candidates, all unimplemented), `src/fixture/adapters.ts` (per-library shapes for
candidates that no longer exist), and the `@shopify/react-native-skia`, `victory-native` and
`react-native-svg` dependencies. `react-native-gesture-handler` and `react-native-reanimated` stay —
a pan/zoom chart wants them regardless of which library wins.

D-2 in `ops/decisions/decisions.md` records this as a deferral rather than a measurement, and
carries the instrumentation notes for whoever runs the comparison in Phase 4. Read it before
assuming `lightweight-charts` beat anything.

## The fixture

`generateGoldTicks()` with no arguments: 30,000 ticks, one-minute spacing, seed `0x601d`, geometric
Brownian motion at 6% drift and 15% annualised vol from $3,320, with the weekend close
(Fri 22:00 → Sun 22:00 UTC) skipped. That is 2026-01-05 through 2026-02-02 — about a month of
one-minute gold with four two-day holes in it.

Same seed and count gives a byte-identical series on every platform, which is the whole point of
`rng.ts` using mulberry32 rather than `Math.random()`.

The gaps are not incidental, and they are why this survived the spike's deletion. A chart that
interpolates a straight line across a 48-hour close draws price action that did not happen — the
same failure the delta engine's null-window invariant exists to prevent, one layer up. The
generator's approach is directly reusable for the delta-engine replay fixture, where a gap has to
produce `null` short windows rather than a 0.00% move.

## Running it

```bash
npm install
npm run web
```

`App.tsx` is a placeholder that renders the fixture's summary and nothing else. It exists so the
shell stays runnable; the Phase 1 dashboard replaces it wholesale.
