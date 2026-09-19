# Aurum decision log

The cross-cutting decisions for the platform, with the reasoning and the rejected alternatives.
This is the record that outlives the plan documents: `plans/` is untracked and exists only in a
working copy, so a fresh clone has this file and nothing else explaining why the code is shaped the
way it is.

`D-1` … `D-8` originated as the pre-build decisions in `plans/aurum-phased-plan.md`; `D-9` onward
are recorded as they are made. **IDs are permanent and cited from code and prose** — `D-7` from
`PostgresQuotaGovernor`'s class remarks, `D-3` and `D-8` from `README.md` — so entries are amended
in place rather than renumbered, and an ID is allocated when a decision is actually taken, not
reserved for planned work.

## Measured environment

| | |
| --- | --- |
| GPU | NVIDIA GeForce RTX 5070 Ti Laptop, **12 GB VRAM** |
| .NET SDK | 10.0.110 |
| Docker / Compose | 29.6.2 / v5.3.1 |
| Node | 24.11.0 |

## D-1 — Expo vs bare React Native

**DECIDED: Expo (SDK + EAS Build).**

**Decided without the spike.** No app shell was ever created, so this is a judgement call on the
documented tradeoffs, not a measurement. What that costs, stated so it is not later mistaken for a
verified choice: we have not confirmed that any native dependency the app needs is available as an
Expo module or config plugin. The failure mode is discovering one that is not, mid-Phase-4, and
paying for `expo prebuild` and three hand-rolled build pipelines at the point where app store
submission is already on the critical path.

Accepted because the alternative is worse for a solo developer. Bare React Native means owning web,
iOS and Android build pipelines from the first commit, and `react-native-web`, push notifications
(FR-4.2) and CI builds for both stores (Phase 4) all come free with Expo. The risk is also
front-loadable: the dependency check is cheap to run when the app shell is created, and that is the
moment to run it rather than at Phase 4.

## D-2 — Charting library

**DECIDED IN PART: TradingView `lightweight-charts`, web only. The native chart is deferred to
Phase 4.**

**This is a deferral, not a measurement.** The comparison spike was never run: `app/` was
scaffolded, but the harness, the gesture scripts and all three candidate chart implementations were
left unwritten, so there are no frame-time numbers behind this and there is no threshold that
`lightweight-charts` was shown to clear.

What is actually decided is the smaller question. The web dashboard ships first and web-only, and
`lightweight-charts` is the best-in-class option for that target; picking it needs no spike because
on web it has no serious rival among the candidates. The chart lives behind a component boundary so
the native choice stays cheap to make later.

### What is deferred, and why Phase 4 is the right place

The real question the spike existed to answer is whether one implementation can serve web and
native, or whether the product carries two. That question is only answerable on a physical device
under real gestures, and the device is not in play until Phase 4 builds the mobile apps. Running it
now would produce numbers on a device we do not yet have and a codebase we have not yet written.

The two candidates it will decide between:

- **One codebase** — `@shopify/react-native-skia`, hand-rolled chart, same implementation on web
  (CanvasKit/WASM) and native. Choosing this in Phase 4 means replacing the web chart too.
- **Web/native split** — keep `lightweight-charts` on web, add `victory-native` (or equivalent) on
  native. Two chart implementations to keep in sync for the life of the product.

**Set the threshold before taking the first measurement.** State the p95 frame time at which the
split becomes worth paying for, in writing, before any number exists — a threshold chosen after
seeing the numbers is a rationalisation of whichever candidate won.

Record alongside each measurement block, because the numbers are not comparable without them:

- **What "frame committed" means for that candidate.** The candidates do not share an
  instrumentation point — Skia commits on its own render thread, lightweight-charts on the browser
  compositor, victory-native through a React re-render. A single `requestAnimationFrame` counter
  across all three compares different quantities and produces a winner that is an artefact of the
  harness.
- **Whether the candidate was downsampling, and by whose strategy.** lightweight-charts does it
  internally and does not let you turn it off; a hand-rolled Skia path only does it if you wrote it.
  Full-detail Skia against downsampled lightweight-charts is not a like-for-like number.
- **Device and run order.** A phone several minutes into a session has thermally throttled and is
  not the device that produced the first row.

The fixture is worth keeping for it: 30,000 ticks, seed `0x601d`, one-minute gold with weekend gaps
(`app/src/fixture/`). It is also directly reusable as the delta-engine replay fixture, and it is the
only part of the spike that still exists — the harness, the candidate screens and the per-library
adapters were deleted along with the two losing chart dependencies once this was decided. Phase 4
starts its comparison from the fixture and an empty directory.

### Cost to BO-4, so far unpaid

BO-4 is "one codebase reaching web, iOS and Android". Shipping `lightweight-charts` on web does not
spend it yet — nothing native exists to diverge from — but it does put the cheapest outcome out of
reach: if Phase 4 chooses one codebase, the web chart is rewritten in Skia rather than kept.

If Phase 4 chooses the split instead, this is what BO-4 buys out at:

- two chart implementations to keep at feature parity for the life of the product — every axis
  format, tooltip, annotation and interaction gets built twice, and they drift
- the web/native divergence stops being a rendering detail and becomes a testing surface: a chart
  bug now has to be reproduced per platform
- `lightweight-charts` is DOM-only, so the boundary is hard. Anything above the chart that wants to
  reach into it has to be written against two different APIs, which is what the component boundary
  around the chart exists to contain — keep it narrow, and keep chart-specific types out of it.

State the tradeoff explicitly when Phase 4 closes this. If one codebase wins, that section should
record that the split was measured and rejected, not that it was never considered.

## D-3 — Postgres image

**DECIDED: `timescale/timescaledb-ha:pg17`.** Tag confirmed to exist on Docker Hub. Extensions are
created in `ops/db/init/01-extensions.sql`, which runs once as superuser on an empty data volume —
so first boot fails loudly if the image ever stops shipping TimescaleDB or pgvector.

`SchemaTests.Pgvector_is_available_for_phase_two` asserts pgvector is actually present.
**Executed and passing** against the real image (2026-08-10).

Note for Phase 5: `AurumDbContext.OnModelCreating` also declares both extensions, so the migration
emits `CREATE EXTENSION IF NOT EXISTS`. That is harmless today only because the compose app role
*is* the bootstrap superuser. When a least-privilege application role is introduced, the migration's
`CREATE EXTENSION` will start mattering and the init script must be the sole owner.

## D-4 — Symbol on price entities

**DONE.** `PriceTick.Symbol` (`XAUUSD` default, `PriceSymbols.Gold`) exists in the first migration.

Two consequences worth knowing:

- `price_ticks` has a **composite primary key** `(ObservedAt, Id)`. TimescaleDB rejects any unique
  index that omits the partitioning column, so a surrogate-only PK makes `create_hypertable` fail.
- Hypertable conversion and the 30-day retention policy were **moved into the first migration**
  (the phase plan had them in Phase 1). On an empty table this is a DDL statement; on a quarter of
  accumulated ticks it is a data migration.

## D-5 — Module boundaries

**DONE.** Folder-per-module inside `Aurum.Api`, per the plan's repo layout. `Modules/Pricing` and
`Modules/Macro` exist; each module exposes a single `Add<Module>Module` extension method as its
registration seam.

## D-6 — Ollama host sizing

**DEFERRED to Phase 2 planning.** It defers cleanly: nothing before the causation engine touches
Ollama, so no code written between now and then depends on the answer. The hardware finding below
does *not* defer — it already constrains what Phase 2 can plan for, and it is the reason this entry
stays open rather than being closed as "decide later".

**The plan's assumption does not survive contact with the hardware.**

12 GB VRAM. A 30B-class model at q4_K_M is ~18 GB and will not fit; the plan's fallback of "bigger
local models" (§10) is not available. Realistic ceiling for a fully-resident primary is
**14B-class at q4_K_M (~9 GB)**.

The non-obvious consequence for Phase 2's model registry: at 12 GB you can hold roughly *one*
useful model resident. A 14B primary (~9 GB) plus a 4B relevance model (~3 GB) sits at the VRAM
edge, and Ollama will begin evicting between roles — turning a 200 ms relevance call into a 15 s
model load. **The registry's real constraint is minimising the number of distinct resident models**,
which pushes toward one 14B serving primary/validator/summariser via different prompts, plus one
embedding model.

Throughput is not the risk: ~40–60 tok/s on a 14B q4 puts a 400-token explanation at ~10 s, so the
5-minute SLA (§12/§16) is comfortable. **The risk is Phase 2's ≥70% first-pass validation at ≥60%
confidence.** Measure it early; if it misses, the answer has to be prompt and retrieval engineering,
not a bigger model.

Still to do, as the first item of Phase 2 planning: `ollama pull` the candidates and record real
tokens/sec against the numbers estimated above.

---

## Findings that change the plan

### GoldAPI's free tier cannot support a live dashboard

GoldAPI free is on the order of **100 requests per month**. Fitting that budget means a poll
interval of ~8 hours — the configured default in `appsettings.json`, and the reason
`PriceSourcesOptionsValidator` refuses to boot on a cadence that would overspend.

Three polls a day is not "live price" and cannot meet Phase 1's "price visible within 2× polling
interval" (§8) or FR-1.x in any meaningful sense. The quota governor keeps this honest instead of
failing quietly, but it does not make the tier usable. **Phase 1 needs either a paid tier or a
different primary source, and §12's cost model needs revisiting.**

Free-tier limit confirmed against the account page: 100 requests/month.
**Resolved by D-8 — stay on free for development, buy the paid tier as a Phase 1 go-live gate.**

### GoldAPI's quota reset semantics are unverified

`PriceSourceOptions.QuotaPeriod` defaults to `CalendarMonthUtc`. Whether the provider actually resets
on the calendar month or on a rolling 30 days from signup determines the governor's period key, and
a wrong choice is a silent one-in-twelve failure. **Verify against the account page, not the docs.**

---

## What the foundation work actually verified

Kept because these are the claims the decisions above rest on, and the distinction between
"verified" and "assumed" is the part that rots first.

The earlier Docker blocker (`permission denied` on `/var/run/docker.sock`) is **resolved** — the
account is in the `docker` group and Testcontainers runs. `dotnet test` is green against a real
`timescale/timescaledb-ha:pg17` container.

Verified by that run:

- The first migration applies against the real image (the fixture migrates before every collection).
- `price_ticks` is a hypertable with daily chunks and a 30-day retention policy.
- pgvector is present — D-3's whole justification.
- The quota governor survives a restart: `Budget_survives_a_restart` spends three of five requests,
  drops the `DbContext`, and a second governor over a fresh context reads `Used = 3`, not zero.

Still **unverified**: `docker compose up` producing a running API. The tests exercise the database
image and the schema, not the compose topology or the API container.

---

## D-7 — Quota governor

**DONE.** `Modules/Pricing/Quota/PostgresQuotaGovernor` implements `IQuotaGovernor`;
`QuotaGovernorTests` and `QuotaHandlerTests` pass.

Design is option A from the Phase 0 review — Postgres *is* the bucket. Acquire is a single
`INSERT … ON CONFLICT DO UPDATE … WHERE … RETURNING`, so the budget check and the increment happen
inside one row lock and there is no window for a second caller to act on a stale count. The three
outcomes the original sketch had to disambiguate collapse into that one statement: a missing row is
the `INSERT`, and both "budget spent" and "provider rejected" are the `WHERE` failing. See the class
remarks for why this is raw SQL in an EF codebase.

### Anchor for `RollingThirtyDays`

**Supplied by the caller from configuration; `ResolvePeriod` throws if it is missing.** The anchor is
the provider's signup date — a fact only the provider holds — so there is no safe default.

Rejected `PriceSource.CreatedAt`: that records when *we* registered the source in our own database, a
different event, and using it would offset every period boundary by the gap between signup and first
deploy — silently, permanently, and in a way nothing would ever flag. Rejected deriving it from the
earliest `api_quota_windows` row: circular, and it would make boundaries depend on when the process
first happened to start.

Implemented by D-9: `PriceSourceOptions.PeriodAnchor` supplies it and
`PriceSourcesOptionsValidator` refuses to boot a `RollingThirtyDays` source without one. Before
that the anchor was never threaded through — `GetCurrentPeriod` passed `null` unconditionally, so
configuring the rolling period threw on every acquire. `CalendarMonthUtc` remains the default until
the account page is checked (see the unverified-reset-semantics finding above).

### Refunds on transport failure

**No refund path exists.** A lease taken before a request that then fails in transit stays spent.

The two failure modes are not symmetric. Not refunding leaks one request per network blip: bounded,
self-limiting, and visible as `RequestsUsed` drifting above real usage. Refunding risks unbounded
overspend against a hard monthly cap whose exhaustion is invisible until the provider starts
returning 429 — and on GoldAPI free that means no prices for the rest of the month. An over-count
costs one poll; an under-count can cost the month. The 8-hour cadence already leaves ~7 requests of
headroom against the 100-request budget, which absorbs the expected leak.

Consequence: `IQuotaGovernor` deliberately has no refund method. Adding one later is an interface
change, which is the right amount of friction for a decision this asymmetric.

Enforced by `QuotaHandlerTests.Transport_failure_still_spends_the_lease`. Until that test existed
the decision was enforced only by the *absence* of a `catch` in `QuotaHandler`, which is not
something a reviewer notices. Phase 1 wraps this layer in Polly retries; the test is what makes a
refund introduced there fail loudly instead of quietly halving the effective budget guarantee.

### Denial logging

**Debug level in the governor, naming the cause it found; no rate-limiting machinery.**

Per-instance memoisation was considered and discarded: the governor is registered scoped, so a
"have I already logged this period?" field would be reconstructed on every call and never suppress
anything. The operator-facing event already exists one layer up — `PricePollingService` catches
`QuotaExhaustedException`, logs once at Error, and sleeps until `ResetsAt`, so a spent budget
produces one line per exhaustion episode rather than one per poll.

The wart this entry used to carry — the line said "no budget left" whatever the real cause — is
fixed. `AcquireAsync` reads the row back on the denial path and logs a spent budget and a provider
rejection differently. The two need different responses: one waits out the period, the other says
our count and the provider's have diverged, which is an accounting bug worth chasing.

Rejected teaching the acquire statement to report the cause itself (a CTE returning row state
alongside the upsert): one round trip instead of two and no staleness, but it complicates the one
statement whose single-statement atomicity *is* the concurrency guarantee, in exchange for a log
line. The extra read costs one query per exhaustion episode, not per poll, because the poller
sleeps after the first denial.

Consequence: that read sits outside the atomic statement, so a rejection landing — or the period
rolling — between the two queries makes the message stale. Acceptable for a log line, wrong for
anything that decides; the code says so where the read happens. Enforced by
`Denial_after_provider_rejection_names_the_provider` and its negative twin
`Denial_on_a_spent_budget_does_not_blame_the_provider`, which together stop the branch collapsing
back to a single message.

### Clamping a period with no row

**`ReportProviderRejectionAsync` is an upsert, not an update.**

It was an `ExecuteUpdateAsync` filtered by (source, period), which changes zero rows and reports
success when no row exists for that period. The clamp vanished silently. Reachable when a request
straddles a period boundary: the acquire is charged to the old period, the response arrives in the
new one, and the rejection is written against a period key nothing has touched.

Whether to clamp at all in that case is a real question — the 429 was about the previous period's
budget, and the new period's may genuinely be fresh. We clamp, on the same asymmetry as the refund
decision: an over-clamp costs one poll cycle and self-corrects at the next boundary; an under-clamp
means hammering a provider that is already rejecting us, and on GoldAPI free that costs the month.

The inserted row carries `RequestsUsed = 0`, which is the honest count — nothing was ever charged
to this period. A row reading "0 used of 100, rejected" is the loudest possible drift signal, and
consistent with the rule below that the clamp never lives in the counter. Enforced by
`Rejection_before_any_acquire_creates_a_clamped_window`.

### Authoritative clock

**The application clock (`TimeProvider`), exclusively.** `now()` never appears in the governor's SQL;
every timestamp written to `api_quota_windows` — `PeriodStartsAt`, `PeriodEndsAt`, `CreatedAt`,
`UpdatedAt` — is a parameter bound from the injected clock.

The period key is computed in C#, so the database clock could only be authoritative if the key were
computed in SQL too — which would make `FakeTimeProvider` unable to roll a period, and `ResolvePeriod`
untestable as a pure function. The failure this avoids is the mixed one: a key derived from the app
clock enforced against a boundary written under the database clock, which disagree by exactly the
skew and only near a rollover.

This claim was false below the governor until now. `AurumDbContext.ApplyAuditFields` stamped
`CreatedAt`/`UpdatedAt` from `DateTimeOffset.UtcNow`, so rows written through EF carried wall-clock
audit timestamps while rows written by the governor's raw SQL carried clock-true ones — two rows in
one table from two clocks. Invisible in production, where they agree to within microseconds, and a
month apart under `FakeTimeProvider`. `AurumDbContext` now takes `TimeProvider` as a required
constructor parameter and `ApplyAuditFields` reads it.

Rejected making that parameter optional with a `TimeProvider.System` fallback: it compiles
everywhere and silently reverts to wall clock the moment a registration is dropped or a context is
constructed by hand — reintroducing exactly this bug in exactly the way that hid it the first time.
Required means the compiler names every construction site. There are two: `Program.cs`, where
`AddDbContext` resolves it from DI, and `PostgresFixture.CreateDbContext`, where it is optional
*there* only because a wrong default fails a test rather than shipping.

Enforced by `Audit_timestamps_come_from_the_injected_clock`.

Residual risk: two instances with skewed clocks could briefly create separate rows either side of a
boundary, over-allocating budget for the width of the skew. Acceptable while `PricePollingService` is
single-instance by construction. If the API is ever scaled out, revisit this together with that
service's hosting note.

### Reading remaining budget

`ReportProviderRejectionAsync` stamps `ProviderRejectedAt` and deliberately leaves `RequestsUsed`
alone, so **`Limit - Used` is not the remaining budget** once the provider has rejected us — the
`ProviderRejectedAt IS NULL` predicate in the acquire statement is what clamps it to zero.

Setting `RequestsUsed = RequestLimit` would have made remaining arithmetically correct for any
reader, but it destroys the gap between what we counted and what the provider counted — the only
evidence we get that our accounting is drifting, and exactly what `QuotaHandler` logs on a 429.

---

## D-8 — GoldAPI tier

**DECIDED: stay on the free tier for development. Buy the paid tier as a gate on Phase 1
go-live, before anything user-facing ships.**

100 requests/month at an 8-hour cadence is sufficient for what the foundation work had to prove — that
ticks land, that the governor's accounting survives a restart, that the compose topology comes up.
None of those need a fast cadence; they need a real provider on the other end of the wire, which the
free tier is.

What makes deferring safe is that the tier is a configuration number, not an assumption anywhere in
the code. `MonthlyRequestLimit`, `QuotaPeriod` and `PollInterval` bind from `PriceSources:GoldApiIo`
and are overridable per environment, and the validator recomputes the cadence floor from whatever
limit it is given. Moving to paid is two environment variables and a restart. Had the free-tier
limit been baked into the polling logic, this decision would have to be made now.

*Amended by D-9:* "nothing branches on 100" was not true when this was written — the governor
carried a `DefaultRequestLimit = 100` that any source without a matching `switch` arm fell through
to. The claim holds now that the fall-through is gone.

The risk being accepted, stated plainly so it is not later mistaken for a defect: **Phase 1's "price
visible within 2× polling interval" (§8) is unmeetable until the tier changes.** A dashboard running
against the free tier will show an 8-hour-old price and that is correct behaviour. The criterion
stays unmet by choice, and the paid-tier purchase is what closes it — not a code change.

Second consequence: development produces ~3 ticks a day, which is not enough data to lay out a chart
against. The D-2 spike already plans on 30k synthetic ticks; anything else needing volume should
generate it rather than wait for the poller.

Rejected — **switch primary source now.** It buys cadence at the cost of a provider integration
chosen before we know what cadence Phase 1 actually needs, and Phase 1's failover chain wants a
second source regardless. Picking it then, with real requirements, is a better-informed choice than
picking it now to dodge a bill.

Rejected — **re-scope Phase 1 to a delayed price.** That reshapes the product around a development
constraint that money removes. If delayed pricing turns out to be the right product, it should be
decided on its merits and not because of a free tier.

Trigger for revisiting, so this does not quietly expire: the first Phase 1 work that puts a price in
front of a user. At that point set the real limit and interval from the purchased plan's numbers —
read them off the account page, not the pricing page — and note the §12 cost-model update.

---

## D-9 — Per-source configuration lookup and nested options validation

> The Phase 1 plan's provisional list allocated `D-9` to the source-resolution decision and `D-10`
> to this one. IDs go to decisions in the order they are actually taken, so that list shifts by one
> from here — this entry is already cited from `README.md` and from D-7 above.

**DECIDED: `PriceSources` binds to a map of sources looked up by `SourceCode`, and a custom
`IValidateOptions<PriceSourcesOptions>` validates each entry. Both replace mechanisms that failed by
succeeding.**

Two defects, one shape: a guarantee that was asserted in prose and provided by nothing.

### The fall-through arm

`PostgresQuotaGovernor.GetCurrentPeriod` resolved a source's budget with a `switch` on the source
code, one arm for `goldapi.io`, and a default arm returning `null` — which the caller turned into a
hardcoded 100-request calendar-month period. A second source would have been accounted against 100
regardless of its own configuration, and against calendar-month boundaries regardless of how its
provider actually resets.

Neither is a safe guess. Guessing the limit high lets a source spend budget it does not have; the
provider's 429 eventually clamps the period, so the safety net catches it — after the requests are
gone. Guessing the period kind rolls our counter on a different day from the provider's, so the
ledger and the account disagree and nothing in either says so. The fall-through's real cost is that
its output is indistinguishable downstream from a real configuration.

The switch existed because the options had a property per provider, so translating a runtime source
code into a compile-time member was the only way to read them. Making the sources a map removes the
translation: lookup is `TryGetByCode` / `RequireByCode`, and a miss throws.

Rejected — **keep the switch, add an arm per source.** Cheapest edit, and it leaves the fall-through
in place; the bug returns the first time someone adds a source and forgets. The invariant wanted is
"a source's budget always comes from that source's configuration", and a switch can only re-satisfy
that by hand, each time.

Rejected — **key the map by source code** (`PriceSources:goldapi.io:ApiKey`). Reads better and makes
the duplicate-code check unnecessary, but puts a dot in every environment variable name, and the
dotenv parsers in the compose toolchain are inconsistent about those. The friendly key
(`PriceSources:GoldApiIo`) keeps every variable already in `.env` and `docker-compose.yml` working
unchanged; its cost is that nothing structural prevents two entries declaring the same `SourceCode`,
so the validator rejects duplicates explicitly. Two entries sharing a code would collide on one
`api_quota_windows` row — the second spending the first's budget, which is the accounting failure
this whole module exists to prevent, reintroduced through configuration.

`PostgresQuotaGovernor`'s `IOptions` parameter was also optional (`= null`), which was the same bug
a second time: any construction site that omitted it got the invented configuration. It is required
now, and the tests state the limits they assert against instead of inheriting a hardcoded one.

### The validation that never ran

`ValidateDataAnnotations()` runs `Validator.TryValidateObject(..., validateAllProperties: true)`.
"All properties" means the attributes declared on *that object's own* properties; it does not
descend into the objects those properties hold. `PriceSourcesOptions` had one property,
`GoldApiIo`, carrying no attributes — so validation inspected it, found nothing, and stopped. Every
`[Required]` and `[Range]` one level down had never executed. The error string in
`ApiKey`'s `[Required]` had never been printed by anything.

The failure this allowed is worse than a boot error. A missing key bound to the empty string,
`DefaultRequestHeaders.Add` accepted an empty value, boot succeeded, and every poll thereafter took
a lease, sent an unauthenticated request, and got a 401. A 401 is not a quota rejection, so nothing
clamped; a spent lease is never refunded (see D-7). The month drained one request per poll on
responses that were never going to work.

Chosen — **a hand-written `IValidateOptions<PriceSourcesOptions>`.** It is the only option that
composes with a map: it iterates whatever is configured rather than needing a registration per
source. It is also where the cross-field rules belong — the cadence-vs-budget guard, moved out of
`PricePollingService.GuardPollBudget`, and the rule that `RollingThirtyDays` requires a
`PeriodAnchor`.

Rejected — **register each source as its own options type** (`AddOptions<GoldApiIoOptions>()` bound
to the leaf section). Two lines, no new dependency, and it does fix the `ApiKey` hole. But it needs
one registration per source resolved by name, and it has nowhere to put a cross-field check.

Rejected — **`[ValidateObjectMembers]` with the `[OptionsValidator]` source generator.**
Declarative and genuinely recursive, but a new build-time dependency and a new pattern in a
codebase that uses none, to replace about forty lines.

### Consequences

- `GuardPollBudget` is gone from `PricePollingService`. It ran after the host reported healthy and
  only ever looked at the one hardcoded source; boot is the honest place to refuse a cadence that
  cannot fit its budget.
- `appsettings.json` ships `ApiKey` as the empty string. The previous placeholder would satisfy
  `[Required]` and put the hole straight back — the key has to come from the environment.
  `dotnet ef` is unaffected: it builds the host but does not run it, so `ValidateOnStart` never
  fires.
- A source with `Enabled: false` is exempt from the credential and cadence checks, so a
  half-configured provider can sit in the file switched off. `SourceCode` is still required.
- `GoldApiIoOptions` is gone; the source code constant lives on `GoldApiIoSource`, where it
  identifies the implementation rather than selecting a switch arm.

---

## D-10 — Poll cadence placement and whose budget has to cover it

**DECIDED: the poll cadence is one feed-wide `PricePolling:PollInterval`, and only the primary
source — the enabled entry with the lowest `Priority` — has to fund it for a whole period. Backups
are exempt and may exhaust mid-period.**

Two things were wrong at once. `PollInterval` sat on each source, but `PricePollingService` builds
one `PeriodicTimer` and asks for one price per tick, so every value but the primary's was read by
nothing — MetalpriceAPI's ten-minute entry failed the boot check while having no effect on how often
anything was polled. And the cadence guard held every enabled source to "this source serves every
poll", which pegs the achievable cadence to the smallest budget in the file: with GoldAPI's 100
requests a month enabled anywhere in the chain, the fastest legal cadence is one poll every 7h26m,
and the other two providers' 11,000 requests buy nothing. The plan asked for three free sources so
the aggregate cadence would be usable at $0; the guard made that unreachable.

The exemption is safe because of what the governor already guarantees. The failure this module
exists to prevent is *uncounted* spend — an in-memory counter that resets with the container, or a
retry that slips past the ledger. A backup that empties its budget during a sustained primary
outage is counted, clamped and visible: `AcquireAsync` denies, the chain moves on, and the poller
sleeps until the earliest reset. That is the governor working, not the failure it guards against.

A tie for the lowest `Priority` among enabled sources is refused. The chain resolves
`IEnumerable<IPriceSource>` ordered by `Priority`, so tied entries are separated by DI registration
order — the boot check would then guarantee the budget of a source nobody chose, and the feed could
poll the other one. Ties further down are allowed: they only decide which backup spends first.
A configuration with no enabled source is refused for the same class of reason — the poller logged
one warning and exited while the API reported healthy and served nothing.

Rejected — **hold every enabled source to the full period.** The literal reading of the Phase 1
plan, and the rule that was in the code. It makes the failover chain buy reliability and no speed:
every added provider can only lower the ceiling, never raise it, because the constraint is a
minimum over budgets.

Rejected — **per-source minimum spacing**: keep a `PollInterval` on each source as "may be called
at most this often", and have the feed skip sources called too recently. It keeps every budget
honest and lets the tick run fast, but the feed then rotates between providers as each becomes due.
Providers differ on the level of gold by a few dollars, so a routine mixed-source feed turns the
cross-source offset into a constant input to the delta engine, which fabricates events of exactly
that magnitude. Trading a budget problem for a data-quality problem in Phase 1's core piece.

Rejected — **pick the primary by `Priority == 1`** rather than by position among enabled entries.
Simpler to read, and wrong the moment the top provider is parked with `Enabled: false`: the check
would keep validating the disabled entry's generous budget while the source actually serving every
poll overspends. `Disabling_the_top_source_promotes_the_next_one` pins this.

### Consequences

- `PollInterval` is gone from `PriceSourceOptions`. A stale `PriceSources__GoldApiIo__PollInterval`
  in an operator's `.env` binds to nothing and raises no error, so the rename has to reach
  `.env.example` and `docker-compose.yml` in the same pass.
- `PriceSourcesOptionsValidator` depends on `IOptions<PricePollingOptions>`. The dependency is
  one-way by design: the polling options' own validation must never read the source map, or the two
  validators recurse through each other at boot.
- What a backup's exemption actually costs is invisible in the config, so `PricePollingService`
  logs each backup's coverage in days once at startup.
- `PricePollingService` no longer keys "is anything enabled?" off GoldAPI specifically. That check
  exited the poller whenever GoldAPI was parked, even with two other providers enabled.

---

## D-11 — Three free sources rather than the paid tier

**DECIDED: the failover chain is GoldAPI.io, API Ninjas and MetalpriceAPI, all on free tiers,
totalling ~11,100 requests a month. The paid GoldAPI upgrade stays a go-live gate (D-8), not a
build gate.**

The chain exists for reliability, but the reason it is three *free* providers is arithmetic.
GoldAPI's 100 requests a month funds one poll every 7h26m. Phase 1's delta engine computes 1m and
5m windows; at that cadence every short window holds one sample and D-13's null-window invariant
returns `null` for all of them, forever. The engine would be untestable against anything but a
replayed fixture, and the significance classifier would never fire in development. API Ninjas'
10,000 and MetalpriceAPI's 1,000 are what make a minute-scale cadence reachable at $0, which is
what makes items 5 and 6 verifiable against a live feed rather than only against a fixture.

What it does not buy, and the reason this is worth recording rather than assuming:

- **Only one source has real headroom, and it is gold-only.** API Ninjas' `/v1/goldprice` returns
  gold and nothing else — `ApiNinjasSource` rejects a non-`XAUUSD` symbol explicitly rather than
  returning gold for whatever was asked. Phase 6's multi-metal work therefore cannot lean on the
  provider carrying 90% of the budget. Silver arrives on a 1,100-request month across two
  providers, which is one poll every ~40 minutes — usable for a chart, not for 5m deltas. Phase 6
  needs its own source decision; it does not inherit this one.
- **The aggregate is not a budget.** 11,100 is the sum of three ledgers that clamp independently.
  The primary still funds the cadence alone (D-10), so the cadence is set by GoldAPI's 100 unless
  GoldAPI is demoted. The other 11,000 buy outage coverage, not speed.
- **Three providers means three level offsets.** Gold's quoted level differs by a few dollars
  between providers, so every failover injects an apparent move of exactly that offset into the
  delta engine. D-13's cross-source flag mitigates this; it does not fix it.

Rejected — **buy the GoldAPI paid tier now.** One provider, one set of response semantics, one
quota model, and a cadence that supports the delta engine immediately. It also removes the only
forcing function for the failover chain: with a comfortable budget on a single source, the chain
would be written against a provider that never fails, and its first real exercise would be in
production. D-8 already fixed the tier as a go-live gate; paying earlier trades a build-time
constraint for an untested code path.

Rejected — **two sources.** GoldAPI plus API Ninjas covers the cadence and is less work. It leaves
the chain with no third leg, so the "every source failed" path — `AllSourcesFailedException` and
the poller's sleep-until-earliest-reset — is reachable only by disabling one of two providers, and
a two-element chain does not distinguish "try the next one" from "try the last one". The third
source is what makes the failover logic general rather than a fallback.

Rejected — **scrape a public spot page as the third source.** Free and unlimited in practice. No
quota to account, no contract, and no `ObservedAt` — the staleness figure the UI owes the user
(§14) would be fabricated, which is the misleading-data failure this phase is built to avoid.

---

## D-12 — Sources resolve as `IEnumerable<IPriceSource>`, not keyed DI

**DECIDED: every source registers as an additional `IPriceSource` transient; the failover chain
resolves them all and orders by `Priority`. Registration goes through one private
`AddPriceSource<T>` helper in `PricingModule`.**

`Priority` already expresses the order of the chain, and it is bound from configuration, so an
operator can demote a failing provider with one environment variable. Resolving the whole set and
sorting on it means there is exactly one place that ordering is decided, and it is data.

Keyed DI (`GetRequiredKeyedService<IPriceSource>("goldapi.io")`) would force the chain to carry its
own list of key strings in code. That list is a second declaration of which sources exist, ordered
implicitly by where it is written — so a source added to configuration and to `PricingModule` but
not to the chain's list is silently absent from failover, with no error at boot and no symptom
until the primary goes down. The validator's registered-vs-configured check would not catch it:
the source *is* registered, it is just never asked for.

The helper is the other half of the same reasoning. `AddPriceSource<T>` attaches the `QuotaHandler`
as part of registering the client, because a source registered without it compiles, works, and
spends its budget uncounted — the one wiring mistake in this module that produces no symptom at all
until the provider starts rejecting requests. Auth is the only genuinely per-provider part
(`x-access-token`, `X-Api-Key`, and a query parameter), so it is the only thing the helper takes as
a callback. The helper also emits a `RegisteredPriceSource` tag per source, which is what lets
`PriceSourcesOptionsValidator` assert at boot that every code registered in code has a
configuration entry — closing the gap left open in item 1, where `RequireByCode` threw from a field
initialiser during DI construction rather than at `ValidateOnStart`.

Rejected — **keyed DI.** Above. The duplicated key list is the cost; it buys the ability to resolve
one named source directly, which nothing in Phase 1 needs.

Rejected — **an explicit `IReadOnlyList<IPriceSource>` registered as a singleton**, built once at
startup in the intended order. Removes the per-resolution sort and makes the order inspectable in
one place. But the sources are transient by construction — they wrap typed `HttpClient`s whose
handlers `IHttpClientFactory` recycles — so a singleton list would pin one instance of each for the
process lifetime, which is the same handler-lifetime mistake `QuotaHandler` takes
`IServiceScopeFactory` to avoid.

Rejected — **ordering by registration order in `PricingModule`** and dropping `Priority` from
configuration. Simplest to read, and it makes demoting a provider a code change and a deploy. The
ordering also stops being visible to `PriceSourcesOptionsValidator`, which needs `Priority` to
identify the primary whose budget funds the cadence (D-10).

## D-13 — The per-attempt timeout lives inside the resilience pipeline, not on `HttpClient.Timeout`

**DECIDED: `http.Timeout` is set to `Timeout.InfiniteTimeSpan`. The per-attempt budget is a
`AddTimeout(RequestTimeout)` strategy innermost in the pipeline, and a second
`AddTimeout(TotalTimeout)` outermost bounds the whole retry sequence. `PriceSourcesOptionsValidator`
refuses `TotalTimeout <= RequestTimeout` and `TotalTimeout >= PricePolling:PollInterval`.**

`HttpClient.Timeout` is a total budget. `SendAsync` starts it before the handler chain runs, so it
sits outside the resilience handler and outside `QuotaHandler` both. With retries in place it stops
meaning "how long one attempt may take" and starts meaning "how long every attempt plus every
backoff delay may take together" — at `00:00:10` with three attempts and 1s/2s backoff, attempt
three is cancelled before it opens a socket, and which attempt gets killed depends on how slow the
earlier ones were. The retry strategy is configured, reads as configured, and does nothing.

The diagnostic half is worse than the arithmetic. When `Timeout` fires it throws
`TaskCanceledException` *above* the pipeline, so Polly's predicate never observes it: the failure
cannot be retried, cannot be classified, and reaches the failover chain with no status code and
nothing naming the source. Meanwhile `QuotaHandler` has already charged a lease for each attempt
that did leave the process, and there are no refunds (D-7) — so the budget is spent and the log
line says a task was canceled.

Moving the budget inside the pipeline fixes both. `AddTimeout` throws `TimeoutRejectedException`, a
typed exception the retry predicate handles and the chain can attribute to a source, and because the
strategy sits inside the retry loop each attempt gets a fresh `RequestTimeout`.

The resilience handler is registered *before* `QuotaHandler` so the retry loop sits above the
governor and every attempt is charged its own lease. Inverting the two would retry below the
accounting and spend the budget uncounted — the same failure the `DelegatingHandler` placement in
D-7 exists to prevent.

`TotalTimeout` is a separate configured value rather than one derived from `RequestTimeout` and the
retry count. Deriving it means changing `MaxRetryAttempts` silently changes the ceiling; an explicit
value is one more key to get wrong, but it is a key the validator can check at boot. Both new rules
guard failures that are otherwise invisible in production: a total at or below the per-attempt
budget disables retries silently, and a total at or above the cadence lets one tick's retry sequence
still be running when the next tick starts, spending quota at twice the rate the D-10 cadence guard
was told to expect. The timeout rules are checked per source, not for the primary only, because a
backup's retry sequence runs on the same poller tick.

Rejected — **`AddStandardResilienceHandler()`**, the no-argument preset bundling rate limiter, total
timeout, retry, circuit breaker and attempt timeout. Fewer lines, and its defaults are sensible for
a service with an ordinary request budget. They are not sensible for ~100 requests a month: its
retry defaults to 3 attempts with jitter and its circuit breaker opens on a failure ratio, so the
preset picks the spend rate. Under this quota every attempt is a decision, which is what makes the
explicit pipeline worth its verbosity.

Rejected — **keeping `http.Timeout` as an outer backstop** alongside the pipeline timeouts, on the
grounds that an infinite client timeout has no safety net if the total-timeout strategy is ever
removed. It reintroduces exactly the cancellation this decision exists to move: a backstop that
fires throws the same unobservable `TaskCanceledException` above the pipeline. The backstop is
`AddTimeout(TotalTimeout)`, and the validator is what keeps it meaningful.
