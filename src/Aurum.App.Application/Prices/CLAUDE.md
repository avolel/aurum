# The Prices feature and the read endpoints (D-20)

Queries only, so no `Commands/` and no repository — query handlers read `AurumDbContext` directly.

```
Aurum.Api/Controllers/PricesController.cs              ← /api/v1/price/live, /history, /sources
Aurum.Api/Controllers/PriceEventsController.cs         ← /api/v1/events
Aurum.App.Application/Prices/
  Queries/Get{LivePrice,PriceHistory,PriceEvents,PriceSources}Query.cs   ← IQuery<T> records
  Handlers/Queries/*QueryHandler.cs
  Validators/*QueryValidator.cs                       ← live, history, events (sources has no input)
  DTOs/*.cs
Aurum.App.Application/Validation/ValidationErrors.cs   ← ValidationException → ApiResponse.Errors
```

Four `GET` actions, all open in Phase 1 (no login method is registered, so `[Authorize]` would refuse
everything). **None of them calls a price service** — every provider request spends part of a
100-request month, and these can be reloaded freely.

**They needed no new registration.** `Program.cs` already calls `AddControllers()` and
`MapControllers()`, so the planned `MapPricingModule` was dropped with the other module methods
(D-5, D-20). A new controller is picked up by those two lines.

| Route | Reads | Notes |
|---|---|---|
| `/api/v1/price/live?symbol=` | `ILatestQuoteCache` | 503 with `ErrorMessages.NoPriceMessage` when nothing is held |
| `/api/v1/price/history?symbol=&from=&to=&limit=&cursor=` | `price_ticks` | oldest first, `[from, to)`, 400 when `from` is older than `PriceTick.Retention` |
| `/api/v1/events?symbol=&window=&minPercent=&limit=&cursor=` | `price_events` | newest first; `minPercent` matches `abs(DeltaPercent)` |
| `/api/v1/price/sources` | settings + `price_sources` + `SourceCircuitStore` + `IQuotaGovernor` | ordered by `Priority`, switched-off sources included |

- **`Aurum.App.Application` references `Aurum.App.Infrastructure.Pricing`** so handlers can read the
  cache, circuits, governor and settings. Controllers still see only `IMediator`.
- **Paging is keyset, not page numbers.** `Cursor` (SharedKernel) is the last row's `(time, Id)`,
  sent as base64url — plain base64's `+` arrives as a space in a query string. The comparison is
  written out (`t > c.Time || (t == c.Time && Id > c.Id)`): EF Core does not translate tuple
  comparison. Handlers read `limit + 1` rows; `NextCursor` is null on the last page. Page size is
  `PaginationHelper.ClampPageSize` — clamped, never rejected. The tie tests
  (`..._SplitAcrossAPage_...`) fail when `>` becomes `>=` or the `Id` half is dropped; both were
  mutation-checked.
- **Symbols are resolved with `SupportedSymbol.Find`** and compared exactly in SQL. A
  case-insensitive comparison skips the `(Symbol, ObservedAt)` index.
- **`PriceTick.Retention` must equal the migration's `INTERVAL '30 days'`.**
  `SchemaTests.PriceTicks_retention_policy_matches_PriceTick_Retention` reads the job and compares.
- **`/sources` asks the governor only about switched-on sources.** The validator lets a switched-off
  source sit half-configured, and `ResolvePeriod` throws for a rolling period with no anchor. `Quota`
  is null for those; `Circuit` is null for a configured source with no registered code. `Remaining`
  is `0` when `ProviderRejected` (`Handle_ProviderRejected_...` was mutation-checked).
- Controllers rethrow `OperationCanceledException` when the request's own token fired, so a client
  hanging up is not logged as a 500.
- **`price_events` has no index on `DetectedAt`.** `/events` sorts the symbol's rows; fine at Phase 1
  volume. Add `(Symbol, DetectedAt DESC, Id DESC)` if it grows.
- Not handled: CORS, response caching, a cursor reused with a different range or symbol (it starts
  from that point), and malformed dates (`from=yesterday` fails model binding with the framework's
  ProblemDetails, not `ApiResponse`).
