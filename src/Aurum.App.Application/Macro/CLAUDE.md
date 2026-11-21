# The Macro feature: `/api/v1/macro/snapshot` (D-27)

Queries only, like Prices: no `Commands/`, no repository. The handler reads `AurumDbContext` directly.

```
Aurum.Api/Controllers/MacroController.cs                 ← /api/v1/macro/snapshot?asOf=
Aurum.App.Application/Macro/
  Queries/GetMacroSnapshotQuery.cs
  Handlers/Queries/GetMacroSnapshotQueryHandler.cs
  Validators/GetMacroSnapshotQueryValidator.cs           ← asOf not in the future
  DTOs/MacroFigureDto.cs
```

The endpoint returns the newest stored figure of each series in `FredSeries.All`, as known at `asOf`
(default: now, by the injected clock). It **never calls FRED or BLS**; `MacroPollingService` fills the
tables. Open, like the price endpoints (D-20). No new registration: `AddControllers()` picks the
controller up, and assembly scanning finds the handler and validator.

- **"Known at `asOf`" has two rules** (D-27):
  - Release series (`FredSeries.IsRelease`): `ReleasedAt <= asOf`. Item 6 passes a move's start
    time, so a figure published after the move can't be used to explain it.
  - Market series: `ObservedOn` before `asOf`'s **New York** date (`NewYorkTime.Day`). A
    daily yield isn't final until that US day ends. Using the UTC date would let yesterday's
    unfinished New York day in after 19:00 New York time.
- **Rows with a null `Value` are skipped**, so FRED's `"."` (a missing month or a market holiday)
  falls back to the row before it.
- **`AgeDays` is worked out in the handler only**: whole days from `asOf` back to `ReleasedAt`, or
  from `asOf`'s New York date back to `ObservedOn` for market series. Item 6's prompt reads the DTO
  instead of counting again, so the two can't disagree.
- Series are matched by `FredSeries.All`, not by whatever is in `macro_series`, because the rule
  depends on `IsRelease`, which only the code knows. A stored series missing from `FredSeries.All` is
  not returned. A series with no qualifying row is left out, not returned as null.
- One query: filter, then `GroupBy(Code)` taking the newest row per group. EF Core translates it to
  SQL; the handler tests run it against Postgres, so a version that stops translating fails there.
- Not handled: an `asOf` before 2006 (`HistoryStart`) returns an empty or partial list with no
  warning, and a malformed `asOf` fails model binding with the framework's ProblemDetails, not
  `ApiResponse` (same as `/price/history`).
