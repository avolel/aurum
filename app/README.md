# app — the start of the mobile app (not the Phase 1 app yet)

This is an empty Expo app (Expo is a toolkit for building one app that runs on the web, iPhone and
Android). There's one thing in it worth keeping: `src/fixture/`, which generates made-up gold prices
for testing.

This folder is a placeholder until work on the Phase 1 dashboard starts. At that point it gets
rebuilt. (This file used to say the folder wasn't tracked by git. It is now.)

## What happened to the chart experiment (D-2)

This folder used to hold a test bed for comparing three chart libraries: Skia, `lightweight-charts`
and `victory-native`, each drawing 30,000 prices.

D-2 was decided without it. The website uses `lightweight-charts`. The question the experiment was
really for, whether one chart can work on both web and phones or the product needs two, moves to
Phase 4, when there's a real phone to test on. Measuring now would mean testing on a phone I don't
have, with code I haven't written.

What was removed:

- `src/harness/`: frame timing and scripted swipes. Neither was ever written.
- `src/screens/`: the three trial charts. None was ever written.
- `src/fixture/adapters.ts`: code shaping the data for each library, no longer needed.
- The libraries `@shopify/react-native-skia`, `victory-native` and `react-native-svg`.

`react-native-gesture-handler` and `react-native-reanimated` stay. A chart you can drag and zoom needs
them whichever library wins.

D-2 in `ops/decisions/decisions.md` records this as a postponement, not a measurement. It also has
notes on how to measure fairly, for whoever runs the comparison in Phase 4. Read it before assuming
`lightweight-charts` beat anything.

## The made-up prices

Calling `generateGoldTicks()` with no arguments gives:

- 30,000 prices, one minute apart
- starting at $3,320
- moving randomly in the way real prices tend to: a steady upward drift of 6% a year, with swings
  sized like 15% a year (the model is called "geometric Brownian motion")
- with the weekend market close skipped (Friday 22:00 to Sunday 22:00 UTC)

That covers 2026-01-05 to 2026-02-02: about a month of one-minute gold prices with four two-day
gaps in it.

The random numbers come from a fixed starting value (`0x601d`), so the same settings give exactly the
same prices every time, on every platform. That's the whole reason `rng.ts` uses its own random number
generator (mulberry32) instead of JavaScript's `Math.random()`, which can't be made repeatable.

The gaps are on purpose, and they're why this survived when the rest of the experiment was deleted.
A chart that draws a straight line across a 48-hour close shows price movement that never happened.
That's the same mistake the "how much did the price move" feature must avoid one level down: a gap
has to give "no answer", not "the price moved 0.00%". The same generator can be reused to build the
test data for that feature.

## Running it

```bash
npm install
npm run web
```

`App.tsx` is a placeholder that shows a summary of the made-up prices and nothing else. It's there so
the app still runs. The Phase 1 dashboard will replace it completely.
