# The latest-quote cache (D-16)

`LatestQuoteCache` holds the newest `PriceQuote` per symbol for `/api/v1/price/live`. Singleton,
registered beside `SourceCircuitStore`. `PricePollingService` awaits `EnsureWarmAsync` before its
first poll and calls `Record` after every successful one.

- **`Record` runs before the tick is added to the `DbContext`.** Memory, then database, then clients
  (D-19): a failed save must not hide a price the app fetched. A failed poll never records — the
  `AllSourcesFailedException` path projects onto `price_sources` and rethrows, and the held quote's
  growing `Age` is the signal. `A_poll_writes_the_cache_before_it_persists` was mutation-checked
  against `Record` moved below the save.
- **Warm-up is awaited by the poller, not a hosted service of its own** — hosted-service start order
  is registration order, and nobody re-reads that before adding one. The delta engine warms the same
  way, but the two reads do **not** merge: a `Sample` has no bid, ask or `ReceivedAt` (D-17). Each
  warm-up is in its own try in `PricePollingService.TryWarmAsync`; one that throws is logged and
  polling continues, because an unhandled exception in `ExecuteAsync` stops the host by default.
- **Warm-up goes through `Record`**, so it is idempotent and cannot overwrite a newer live quote. It
  builds `PriceQuote` with the constructor, not `Normalize`, so a since-tightened plausibility band
  cannot fail boot over stored data. A warmed quote has empty `AttemptedSources` ("not observed by
  this process") and `IsFallback` derived from the *current* primary, so reordering sources across a
  restart can flip it.
- It visits `SupportedSymbol.All`. A symbol added to `SupportedSymbol` but not to `All` is never
  warmed.
- **A `ConcurrentDictionary`, not `IMemoryCache`.** Eviction makes "eight hours old" look identical
  to "never had a price", and the age is the product requirement. Do not "upgrade" it.
- **Newest-only by `ObservedAt`, strictly.** The comparison lives inside `AddOrUpdate`'s update
  delegate; a `TryGetValue`-then-indexer rewrite reopens the check-then-set gap. The delegate may run
  more than once under contention, so it must stay side-effect free — the debug log for a dropped
  quote is decided afterwards by `ReferenceEquals(held, incoming)`.
- **`Age = clock.GetUtcNow() - Quote.ObservedAt`, computed in `Get`, never stored.** Not
  `ReceivedAt`: a provider answering 200 with a frozen `ObservedAt` would look fresh forever.
  `Age_is_measured_from_ObservedAt_not_ReceivedAt` pins this.
- **`LatestQuote` must not carry `SourceAttempt`.** It holds an `Exception`, which a cached quote
  would pin for hours. `Record` takes the whole `PriceFeedResult` so that projection happens in one
  place; `A_cached_quote_holds_no_exception_reference` is a decision-pinning test, not dead weight.
- `ILatestQuoteCache` and both records are **public** because `Aurum.App.Application` references this
  project and `GetLivePriceQueryHandler` reads the cache (D-20).
- Symbol keys are `OrdinalIgnoreCase` to match `PriceSourcesOptions.Sources` and
  `PriceFeedResult.UsedFallback`.
