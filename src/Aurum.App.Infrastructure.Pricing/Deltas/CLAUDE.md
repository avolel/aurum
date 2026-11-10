# Price moves and price-move events

## The price-move calculator (D-17)

`DeltaEngine` works out how far each symbol moved over the six fixed `DeltaWindow`s (1m, 5m, 15m,
1h, 4h, 1d). Singleton, registered beside `LatestQuoteCache`. `PricePollingService` awaits
`EnsureWarmAsync` after the cache's and calls `Record(result.Quote)` on the line after
`cache.Record`, before the save.

- **A window is two real prices or null.** Never a zero-filled `WindowDelta`. At the shipped
  15-minute cadence, 1m and 5m are almost always null — correct, not a bug.
- **Start = last sample at or before `now - W`** (`TickRingBuffer.LastIndexAtOrBefore`), never
  first-after. **Now = the injected clock**, not the newest sample's time, and the newest sample must
  be within `Tolerance(W)` of now. Both rules have tests that go red when broken.
- **One `TickRingBuffer` per symbol behind its own `Lock`**, not one per window. Strictly increasing
  `ObservedAt`; `TryAppend` refuses at-or-before and never re-sorts. `Record` counts refusals in
  `DroppedOutOfOrder`; warm-up appends through `TryAppend` directly so overlap is not counted.
- **Lookback is `1d + Tolerance(1d)` (36h), not 1d.** The 1d start is *older* than "now − 1 day". The
  warm-up query and `DeltaEngineOptions.BufferCoversLongestWindow` (checked at boot against
  `PricePolling:PollInterval`) both use `Lookback`. Shrinking it darkens the 1d window for a day
  after every restart.
- **`EnsureWarmAsync` loads at most once**, behind a `SemaphoreSlim` and a `_warmed` flag. A second
  load adds nothing and the buffer cannot back-fill. A failed or cancelled load leaves the flag false
  so the next caller retries. A symbol with no stored ticks gets no history, so `GetSnapshot` stays
  null ("never held a price") rather than six nulls.
- **`CrossSource` is a flag, not a correction.** It compares the start and end samples' per-process
  `SourceOrdinal`s only.
- Volatility is a `double`, two-pass sample standard deviation of step-to-step percent changes, null
  below `MinSamplesForVolatility`.
- `IDeltaEngine`, `DeltaSnapshot`, `WindowDelta`, `DeltaWindow` and `Sample` are public for the same
  reason as `ILatestQuoteCache`; `GetPriceEventsQueryValidator` reads `DeltaWindow.All`.
- `WindowDelta.StartSourceCode` / `EndSourceCode` come from a reverse `ordinal → code` array filled
  where `Ordinal()` hands one out. `Sample` stays 40 bytes; do not put the code on it.

## Price-move events (D-18)

`SignificanceClassifier` turns a `DeltaSnapshot` into `PriceEvent`s; `PriceEventSql.InsertAsync`
saves each one. `PricePollingService.SaveEventsAsync` runs both **after** the tick's
`SaveChangesAsync`, so an event never points at a price the database failed to keep. A failed insert
falls into the poll's existing catch. `A_poll_whose_tick_fails_to_save_writes_no_event` pins the
order; it was mutation-checked by moving the insert above the save.

- **The waiting period is in the insert, not in memory.** `WHERE NOT EXISTS` an event for the same
  `(Symbol, WindowCode)` with `WindowEndedAt > @windowEndedAt - @cooldown`, plus `ON CONFLICT DO
  NOTHING` on the unique `(Symbol, WindowCode, WindowEndedAt)`. Do not reintroduce an in-memory
  `LastEmittedAt`: after a restart the next poll's 1d window has a new end time, the unique rule
  misses, and the event repeats. `Restart_does_not_re_emit_within_the_cooldown` catches it.
- **Measured on `WindowEndedAt` (price time), never the clock**, so a replay gives the same events.
  The boundary is strict: exactly one cooldown later is saved.
- Raw SQL: pass `CreatedAt`/`UpdatedAt` from the clock, and keep `CAST(... AS numeric)` on
  `Volatility` — a null parameter is untyped and Postgres reads it as text in a `SELECT` list.
- **The classifier is static and pure** — no clock, database or interface. Required magnitude is
  `MinPercent`, times `CrossSourceMagnitudeMultiplier` when `CrossSource`; fires on `>=`.
  `TriggeredRule` uses the invariant culture.
- **`SignificanceOptions` has no defaults in code.** The binder adds dictionary keys but cannot remove
  them, so a coded default could never be switched off. A window missing from `Windows` is never
  classified, deliberately. `Windows` is get-only to keep its `OrdinalIgnoreCase` comparer. Boot rules
  are `WindowKeysAreKnown` and `ValuesAreInRange`, registered in `Program.cs`.
- Only the 5m threshold (0.25%) is from BR-02; the other five are placeholders.
- `price_events` is an ordinary table, not a hypertable, so it never gets `price_ticks`' 30-day
  retention. Nested windows each produce an event; merging is Phase 2.
- **Single poller only.** `NOT EXISTS` is not atomic across instances; the unique rule still stops
  exact duplicates.
