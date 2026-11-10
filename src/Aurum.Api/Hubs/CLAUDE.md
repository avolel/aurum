# Live updates (D-19)

`PriceHub` (mapped at `/hubs/price`) is a `Hub<IPriceClient>` with `Subscribe`/`Unsubscribe(symbol)`
over one group per symbol, `PriceHub.GroupName` → `price:{symbol}`. The poller talks only to
`IPriceBroadcaster` (`Pricing/Realtime/`); `SignalRPriceBroadcaster` is its one implementation, a
singleton registered directly in `Program.cs`.

- **No `NullPriceBroadcaster`, no `TryAdd`-then-replace.** That pattern existed only because two
  module methods each registered a sender; D-5 removed them. Tests that build `PricePollingService`
  by hand pass a `RecordingBroadcaster`. Do not reintroduce a default "so tests don't need one".
- **Poll order: memory → tick save → `TryPublishAsync` → `SaveEventsAsync`.** After the save so an
  app is never told a price the database lost (`A_poll_publishes_after_the_tick_is_saved`,
  `A_failed_save_does_not_publish`). Before events so a failed insert can't silence apps. Own catch so
  a failed send can't skip events (`A_failed_publish_still_saves_events`). All three were
  mutation-checked.
- **`Subscribe` joins the group, then sends to `Clients.Caller`.** The reverse loses a broadcast that
  lands in between. **No test catches the swap** — it was mutation-checked and stayed green. Guard it
  in review.
- **`Subscribe` resolves the symbol against `SupportedSymbol.All` ignoring case and uses the stored
  spelling.** Group names are case-sensitive; `xauusd` would otherwise be a silent group
  (`Symbol_case_does_not_split_groups`). Unknown symbol → `HubException` naming the supported ones.
  `SignalRPriceBroadcaster` does **not** resolve: its only caller passes `PriceQuote.Symbol`, already
  the stored spelling. A new caller with user input must resolve first.
- **`PriceUpdate.From(symbol, LatestQuoteSnapshot?, DeltaSnapshot?)` is the only mapper**, used by hub
  and sender. Takes `symbol` because both snapshots can be null. `Windows` is keyed by
  `DeltaWindow.Code` and always has all six keys; no answer is a `null` value, never a zero record or
  a missing key (`A_window_with_no_answer_arrives_as_null_not_zero` reads raw JSON). `Quote` null =
  "no price yet", still sent. `AttemptedSources` is not sent over the hub; `/api/v1/price/live`
  serves it.
- **The hub reads `ILatestQuoteCache`/`IDeltaEngine` directly, not via MediatR** — a cache-only query
  would be a pass-through handler. D-20 kept it that way.
- Typed client methods take no `CancellationToken`; `PublishAsync`'s `ct` is unused by the SignalR
  implementation. Group sends only queue into per-connection buffers.
- Not handled: auth (Phase 1 is open), CORS, events over SignalR, scale-out (groups are in-process;
  needs a backplane), and subscribe/broadcast arrival order (client keeps newer `observedAt`).
