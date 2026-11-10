# Pricing: the quota chain and configuration

GoldAPI's free tier is roughly **100 requests per month**. Everything below exists because an
in-memory counter that resets with the container can spend a month of budget in an afternoon while
every individual request looks fine.

## The quota chain

```
PricePollingService  →  IPriceFeed (FailoverPriceFeed)
                            ├─ SourceCircuitStore → SourceCircuit (in-memory, per source)
                            └─ IPriceSource, in priority order
                                  └─ HttpClient (typed)
                                        └─ resilience pipeline   total timeout → retry (MaxAttempts) → per-attempt timeout
                                              └─ QuotaHandler (DelegatingHandler)
                                                    └─ auth handler
                                                          └─ IQuotaGovernor → api_quota_windows
```

Read top to bottom as the order a request passes through. Placement decisions that look arbitrary:

- **`QuotaHandler` is a `DelegatingHandler`, not a wrapper around `IPriceSource`.** Retries and the
  failover chain both sit above `IPriceSource`; a governor there would let every retry spend quota
  uncounted. The handler sees exactly what leaves the process.
- **The resilience pipeline is registered *before* `QuotaHandler`, so it is outermost.** Every retry
  re-enters the handler and is charged its own lease, because that is what the provider bills.
  Swapping the two lines makes retries free in our ledger and billed by the provider — an
  under-count. `ResilienceWiringTests.Each_retry_attempt_spends_its_own_lease` is the only thing
  that catches the swap, and it calls `Program.AddPriceSource<T>` rather than hand-composing the
  chain for exactly that reason.
- **The attempt count is `PriceFeed:Resilience:MaxAttempts`, not a literal.** Polly counts retries,
  the knob counts attempts, so `Program.AddPriceSource` passes `MaxAttempts - 1`; the conversion
  happens there and nowhere else, because the cadence guard multiplies by attempts and an off-by-one
  in two files under-counts one attempt per poll (D-15). `RetryBackoffBase` sets Polly's `Delay`
  explicitly for the same reason `MinimumTotalTimeout` needs it (see Configuration).
- **It takes `IServiceScopeFactory`, not `IQuotaGovernor`.** `IHttpClientFactory` pools handlers for
  minutes, so injecting the scoped governor would capture a `DbContext` in a long-lived object
  shared across concurrent requests.
- **`PostgresQuotaGovernor.AcquireAsync` uses raw SQL** — a single
  `INSERT … ON CONFLICT DO UPDATE … WHERE … RETURNING`. Do not "simplify" this into EF. Load, decide
  in C#, save leaves a gap between check and increment that no C# can close; `ON CONFLICT` re-reads
  the row under a lock, which is the entire concurrency guarantee.
  `Concurrent_acquires_never_oversubscribe` catches a regression. Class remarks and D-7 carry the
  full reasoning.
- **`ReportProviderRejectionAsync` is an upsert for the same reason**, not the `UPDATE … WHERE` it
  looks like it should be. An update changes zero rows and reports success when the period has no
  row yet, which happens when a request straddles a period boundary — the acquire is charged to the
  old period, the rejection arrives in the new one, and the clamp disappears silently.

## Related invariants

- Period rollover needs no scheduled job. A new period means a new `PeriodKey`, so no row exists and
  the next acquire creates one. Spent rows are never reset — they are history.
- `QuotaStatus.Limit - Used` is **not** remaining budget. When `ProviderRejected` is set, remaining
  is zero regardless of the counter. The clamp never lives in the counter: "0 used of 100, rejected"
  is deliberate, and it is the only signal that our accounting has drifted from the provider's.
- `AcquireAsync` reads the row back on the **denial path only**, to log whether the cause was a spent
  budget or a provider rejection. That read is outside the atomic statement, so it is diagnostic
  only — it can be stale. Nothing may branch on it.
- **The circuit breaker's state is the opposite rule from the governor's.** `SourceCircuit` is
  in-memory, authoritative, and never rehydrated at startup; the governor is durable. A spent request
  is a fact about the provider's ledger that outlives our process; a circuit is a fact about *our own
  recent observations*, and a fresh process has none. Loading an open circuit at boot would blind a
  new process to a healthy source (D-14). `price_sources.LastFailureAt` / `LastFailureReason` is a
  projection for operators, written once per poll by `PricePollingService` and never read back.
- **Quota exhaustion is not a circuit fault.** The chain records
  `SourceAttemptOutcome.QuotaExhausted` and moves to the next source immediately; the breaker is not
  touched, because opening on it would keep the source out after its budget is fresh.
- **The poller sleeps only when *every* source is out of quota**, and then only to the *earliest*
  `ResetsAt`. A single fault or open circuit makes `AllSourcesFailedException.AllQuotaExhausted`
  false and the feed stays on cadence — those clear in minutes.
- **`PriceSourceOptions.Enabled` actually filters.** `FailoverPriceFeed` builds the chain from
  configuration and joins it to the registered sources, so a disabled or unconfigured source is not
  called. Before item 3 the poller selected straight off `IEnumerable<IPriceSource>`, which has no
  `Enabled` to filter on, so setting it shortened the startup log while changing nothing.
- **`price_sources` has no `IsEnabled` or `Priority` column** (dropped in D-20). Configuration alone
  says whether a source is used and in what order; `/api/v1/price/sources` reads both from there.
- The cadence is one feed-wide value, `PricePolling:PollInterval`, coupled to the **primary**
  source's `MonthlyRequestLimit` (the enabled entry with the lowest `Priority`) **and to
  `PriceFeed:Resilience:MaxAttempts`**: every attempt is charged, so the guard multiplies polls per
  period by `MaxAttempts` (D-15). **The circuit breaker does not bound that multiple** — a provider
  that fails two attempts and succeeds on the third spends triple forever while the breaker reads it
  as healthy. `PriceSourcesOptionsValidator` fails boot when the budget cannot fund the cadence.
  Backups are exempt (D-10): holding every source to the full period pegs the cadence to the smallest
  budget. A backup that empties its budget during a long primary outage is counted and clamped by the
  governor; the cost is visible only in `PricePollingService`'s startup coverage log. The validator
  also refuses a tie for the lowest `Priority` and a configuration with nothing enabled.
- **The injected `TimeProvider` is the sole authority for every timestamp the app writes**; `now()`
  never appears in governor SQL. `AurumDbContext` takes it as a **required** constructor parameter so
  no construction site falls back to wall clock — that fallback once made two rows in the same table
  carry times from two clocks. Raw SQL bypasses `ApplyAuditFields`, so pass `CreatedAt` /
  `UpdatedAt` explicitly there.

## Configuration

`PriceSources` binds to a **map of sources** (`PriceSourcesOptions.Sources`, keyed by config key,
with the provider's natural key inside as `SourceCode`). Environment variables override with `__`
(`PriceSources__GoldApiIo__MonthlyRequestLimit`), which is how `docker-compose.yml` and `.env` set
them.

- **Compose has no `env_file`; it forwards only the variables under `api.environment`.** Today:
  `ASPNETCORE_ENVIRONMENT` (defaults to Production), the connection string, the three API keys, `Macro__Fred__ApiKey`, each source's `Enabled`, GoldAPI's
  `MonthlyRequestLimit` (D-8), `PollInterval`, `MaxAttempts` and `Ollama__BaseUrl`. The fallback
  check depends on `PriceSources__ApiNinjas__Enabled` being there.
- **The app reads `ConnectionStrings:DefaultConnection`.** Compose fills it from `.env`'s
  `ConnectionStrings__Aurum`. `appsettings.json` has none; without one the app refuses to start.
- **Look sources up with `RequireByCode`, never a `switch` on the code.** A fall-through arm is a
  fabricated configuration, so a 20-request source gets accounted against whatever the arm guessed.
  `RequireByCode` throws instead.
- The map key is the friendly config key (`GoldApiIo`), not the source code, because the code puts a
  dot in every environment variable name and dotenv parsers disagree about those. Nothing structural
  stops two entries sharing a `SourceCode` (one ledger row), so the validator rejects duplicates.
- **`ValidateDataAnnotations()` does not descend into nested objects.** `PriceSourcesOptionsValidator`
  descends explicitly and is what enforces `[Required]` on `ApiKey` and `[Range]` on
  `MonthlyRequestLimit`. Without it a missing key bound to the empty string, booted clean, and spent
  the month one 401 at a time (401 is not a quota rejection, so nothing clamps and leases are never
  refunded). Any new annotation on `PriceSourceOptions` is enforced by that validator.
- The validator also holds cross-field checks: the cadence-vs-budget guard (formerly
  `PricePollingService.GuardPollBudget`, which ran after the host reported healthy and only covered
  the one hardcoded source) and "`RollingThirtyDays`
  requires a `PeriodAnchor`" — without one `ResolvePeriod` throws on every acquire inside an HTTP
  handler, which the poller swallows and retries forever.
- A source with `Enabled: false` is exempt from credential and cadence checks so a half-configured
  provider can sit switched off. `SourceCode` is still required.
- `appsettings.json` ships `ApiKey` as the empty string deliberately; a placeholder would satisfy
  `[Required]`.
- **`PriceFeed:Resilience` is a flat section** bound with `ValidateDataAnnotations()` (its `[Range]`
  on `MaxAttempts` is a direct property). It is feed-wide because there is one request per tick and
  one retry policy.
- **`TotalTimeout` must fit `MaxAttempts × RequestTimeout` plus backoff.**
  `PriceSourcesOptionsValidator.MinimumTotalTimeout` computes it — 3 × 10s at a 2s base needs 36s,
  not the 35s that shipped until item 4. Below that the last attempt is truncated or never starts,
  and a truncated attempt still spends its lease. `RetryBackoffBase` lets that method reconstruct a
  schedule this repo names rather than a Polly default.
- **`UseJitter = false` is set explicitly and must stay.** `HttpRetryStrategyOptions` defaults it to
  **`true`**, unlike Polly's base options; a jittered delay measured 503ms against 400ms nominal, so
  the formula under-estimates. One poller issuing one request at a time gains nothing from jitter.
  `Backoff_schedule_matches_what_MinimumTotalTimeout_models` is the only guard.
- **`PricePolling:StaleAfter`** is optional; null means twice `PollInterval`, derived once when the
  cache is built. The rule (unset, or strictly longer than `PollInterval`) is
  `PricePollingOptions.StaleAfterExceedsPollInterval`, a method so `PricePollingOptionsTests` calls
  the rule the host runs. At or below the interval, every price looks stale one poll after it arrives.
