# Aurum decision log

This file records the big decisions in this project: what I chose, why, and what I turned down.
It's the record that lasts. The plan documents in `plans/` are not tracked by git, so a fresh copy of
the repository has this file and nothing else explaining why the code looks the way it does.

D-1 to D-8 started life as the decisions I made before building, in `plans/aurum-phased-plan.md`.
From D-9 on, each one is written down when it's made.

**The numbers are permanent.** Code and other docs point to them. For example, the notes on
`PostgresQuotaGovernor` point to D-7, and `README.md` points to D-3 and D-8. So when a decision
changes, I edit its entry instead of renumbering. A number is only handed out when a decision is
actually made, never held back for planned work.

A few terms used throughout:

- **Poll**: one scheduled "go get the gold price" check.
- **Attempt**: one request sent to a price service. A poll can take up to three attempts if the
  first ones fail.
- **Lease**: one request taken from a service's monthly allowance. The app takes a lease just before
  each attempt goes out.
- **Primary**: the price service tried first. **Backups** are the ones tried after it.
- **Plan references** such as §8, FR-4.2 or BO-4 point to sections, requirements and business goals
  in the untracked `plans/` documents.

## The machine these decisions were made on

| | |
| --- | --- |
| Graphics card | NVIDIA GeForce RTX 5070 Ti Laptop, **12 GB of video memory** |
| .NET SDK | 10.0.110 |
| Docker / Compose | 29.6.2 / v5.3.1 |
| Node | 24.11.0 |

## D-1 — Expo or plain React Native

**DECIDED: Expo (its toolkit plus its cloud build service, EAS Build).**

**I made this call without running the planned experiment.** No app was ever created to test it, so
this is a judgement based on what each option is documented to do, not on a measurement. Here's what
that costs, so nobody later mistakes it for a tested choice: I have not checked that every phone
feature the app needs is available in Expo. If one turns out not to be, I'd find out halfway through
Phase 4. At that point I'd have to take the app out of Expo's managed setup (`expo prebuild`) and
build three separate build setups by hand, right when getting into the app stores is the most urgent
thing.

I accepted that because the other option is worse for one developer working alone. Plain React
Native means looking after the web, iPhone and Android build setups from day one. Expo gives these for
free: running on the web, push notifications (FR-4.2), and automated builds for both app stores
(Phase 4). The risk can also be dealt with early. Checking that the needed phone features exist is
cheap, and the right time to do it is when the app is first created, not in Phase 4.

## D-2 — Which charting library

**PARTLY DECIDED: TradingView's `lightweight-charts` for the website only. The phone chart is put off
until Phase 4.**

**This is a postponement, not a measurement.** The comparison experiment was never run. The `app/`
folder was created, but the test harness, the scripted swipes and pinches, and the three trial chart
versions were never written. So there are no speed numbers behind this choice, and no target that
`lightweight-charts` was shown to meet.

What I actually decided is the smaller question. The website comes first and is web-only, and
`lightweight-charts` is the best option for the web. Choosing it needs no experiment, because on the
web none of the other candidates come close. The chart sits behind its own component with a narrow
interface, so the phone decision stays cheap to make later.

### What is put off, and why Phase 4 is the right time

The real question the experiment was meant to answer: can one chart work on both web and phones, or
does the product need two? That can only be answered on a real phone with real finger gestures, and
there's no phone app until Phase 4. Running it now would mean measuring on a phone app I don't have
yet, with code I haven't written yet.

The two options Phase 4 will choose between:

- **One chart everywhere**: build the chart by hand with `@shopify/react-native-skia` (a drawing
  library that runs on phones, and on the web through WebAssembly). Choosing this in Phase 4 means
  replacing the web chart too.
- **Separate web and phone charts**: keep `lightweight-charts` on the web and add `victory-native`
  (or similar) on phones. Two charts to keep matching for as long as the product exists.

**Decide the pass mark before taking the first measurement.** Write down how slow a frame has to be
(for the slowest 5% of frames) before two separate charts are worth it, before any numbers exist. A
pass mark picked after seeing the numbers just justifies whichever option happened to win.

Record these alongside each set of measurements, because the numbers can't be compared without them:

- **What "frame finished" means for each option.** They finish drawing in different places. Skia
  draws on its own thread, `lightweight-charts` goes through the browser, and `victory-native` goes
  through a React re-render. One shared frame counter across all three would compare different things
  and crown a winner that only exists because of how it was measured.
- **Whether the option was thinning out the data, and how.** `lightweight-charts` skips points
  it can't show, and that can't be turned off. A hand-built Skia chart only does that if you write
  it. Full-detail Skia against thinned-out `lightweight-charts` is not a fair comparison.
- **Which phone, and in what order the tests ran.** A phone that has been running for several
  minutes has heated up and slowed itself down. It's not the same phone that produced the first row.

The test data is worth keeping for it: 30,000 made-up one-minute gold prices, with weekend gaps,
generated from the fixed starting value `0x601d` so the same data comes out every time
(`app/src/fixture/`). It can also be reused later to replay prices into the "how much did the price
move" feature. It's the only part of the experiment that still exists. The test harness, the trial
screens and the per-library code were deleted, along with the two chart libraries that weren't
chosen. Phase 4 starts its comparison from that test data and an empty folder.

### What this costs BO-4, not yet paid

BO-4 is the business goal of "one codebase reaching web, iPhone and Android". Using
`lightweight-charts` on the web doesn't break that yet, because there's no phone code for it to
differ from. But it does rule out the cheapest outcome. If Phase 4 picks one chart everywhere, the web
chart gets rewritten in Skia instead of kept.

If Phase 4 picks separate charts instead, this is what giving up on BO-4 costs:

- Two charts to keep with the same features forever. Every axis label, tooltip, annotation and
  gesture gets built twice, and the two drift apart.
- A chart bug has to be reproduced and tested on each platform separately.
- `lightweight-charts` only works in a web page, so the line between the two charts is a hard one.
  Any code above the chart that needs to reach into it has to be written against two different
  interfaces. The chart's own component exists to contain that, so keep its interface small and keep
  chart-library types out of it.

When Phase 4 settles this, write down the trade-off plainly. If one chart everywhere wins, this entry
should say that separate charts were measured and turned down, not that they were never considered.

## D-3 — Which database image

**DECIDED: `timescale/timescaledb-ha:pg17`.** I confirmed the tag exists on Docker Hub. It's
Postgres with two add-ons: TimescaleDB, for storing prices over time efficiently, and pgvector, for
the AI search planned in Phase 2. Both add-ons are switched on in `ops/db/init/01-extensions.sql`.
That script runs once, as the database's all-powerful admin user, the first time the database starts
on an empty disk. So if the image ever stops including either add-on, the very first start fails
loudly.

`SchemaTests.Pgvector_is_available_for_phase_two` checks that pgvector really is there. **It has been
run and passes** against the real image (2026-08-10).

Note for Phase 5: `AurumDbContext.OnModelCreating` also switches both add-ons on, so the first
database change script says "create this add-on if it isn't there already". That's harmless today
only because the app currently logs into the database as that same admin user. When the app gets its
own user with only the permissions it needs, that line in the change script will start to fail, and
the startup script will have to become the only place that switches the add-ons on.

## D-4 — A symbol on every price

**DONE.** Every price row has a `Symbol` (default `XAUUSD`, meaning gold priced in US dollars, from
`SupportedSymbol.Gold`; it was called `PriceSymbols.Gold` when this was written). It's in the very first database change.

Two things that follow from it:

- The `price_ticks` table is identified by two columns together, `(ObservedAt, Id)`, not by `Id`
  alone. TimescaleDB stores the table in one chunk per day, sorted by time. It refuses any "must be
  unique" rule that leaves out the time column, so a key on `Id` alone makes setting up the table
  fail.
- Turning `price_ticks` into a TimescaleDB table, and the rule that deletes prices after 30 days, were
  **moved into the very first database change**. The phase plan had them in Phase 1. On an empty
  table that's a quick structural change. On three months of stored prices it becomes a slow job of
  moving all that data.

## D-5 — How the code is divided up

**REPLACED (2026-09-26). Originally: one project, `Aurum.Api`, with a folder per feature area, and
each area setting up its own services through one `Add<Module>Module` method. Now: five projects
following the layer table in `docs/best-practices-api.md`, with every service set up in
`src/Aurum.Api/Program.cs`.**

The original reasoning still stands on its own terms, and I've kept it here rather than deleting it.
The change was a change of direction, not a discovery that the first choice was wrong. One project
meant one build, one thing to deploy, and one set of library versions. And the `Add<Module>Module`
methods meant `Program.cs` didn't need to know what was inside each area.

What changed is the shape I'm aiming for. The guides in `docs/` now describe a layered solution:
shared basics, database code, business rules, and the web layer. Requests are handled by the MediatR
library, with reading and writing kept apart (see `docs/cqrs-guide.md`). That shape only means
something if the layers are separate projects. "The business rules layer never makes web calls" is
only a comment when the layers are folders. When they're projects, breaking it won't compile. The
rule for setting up services changed at the same time: everything goes in `Program.cs`.

The layout, and the two places it differs from the table:

```
src/Aurum.App.SharedKernel/            depends on nothing, by design
src/Aurum.App.Infrastructure.Data/     the database: context, tables, changes, saving
src/Aurum.App.Infrastructure.Pricing/  price services, request counting, circuit breakers, the timer
src/Aurum.App.Application/             request handling rules, pipeline steps, app logs
src/Aurum.Api/                         Program.cs, controllers
```

`Aurum.App.Infrastructure.Pricing` is a fifth project the table doesn't list. The price services are
outside web services the app calls, and the price timer is a background job. Neither is "database
queries and saving", and neither is a business rule. Putting them in `Infrastructure.Data` would have
broken that project's own "never makes web calls" rule on day one.

`Aurum.Api` can see all four other projects, not just the two the table allows. That's because it's
where everything gets connected together, and it has to be able to name everything it connects. The
table's rule still applies to the code in `Controllers/`. The thing it actually forbids is a
controller going around MediatR to reach database or pricing code directly.

**What moving all setup into `Program.cs` costs, written down so nobody mistakes it for a bug later:**

- `Aurum.App.Infrastructure.Pricing` lets `Aurum.Api` see its internal types (with
  `InternalsVisibleTo("Aurum.Api")`). Five types stay internal: `SourceCircuitStore`,
  `QueryKeyAuthHandler`, `RegisteredPriceSource`, and the two settings checkers. `Program.cs` needs to
  name all of them. The other option was making them public just so they can be set up, which shows
  far more to the rest of the code than this one exception does.
- `Program.AddPriceSource<T>` had to become a method on the `Program` class, not a function written
  inside `Program.cs`'s top-level code. Functions written that way can't be reached from the test
  project. `ResilienceWiringTests` pins down the order of two lines in that method: the retry setup
  must come before `QuotaHandler`, the request counter. Without the test, that would be the one piece
  of setup in the pricing code with no test, and getting it wrong means requests go uncounted (D-7).
- `Program.cs` grows with every feature, and two people changing it at once will clash every time.
  That's the real ongoing cost. This decision does nothing to reduce it. I accept it.

Turned down: **keeping the `Add<Module>Module` methods alongside the new projects.** It suits the
feature boundaries, and the layered guides don't actually forbid it. I turned it down because the
instruction was clear, and applying a setup rule halfway is worse than either rule applied fully. If
some setup is in `Program.cs` and some is hidden behind methods, anyone adding a third thing has to
read both first.

Turned down: **one setup file per layer**, which `cqrs-guide.md` itself suggests for repositories.
Same reason. If clashes in `Program.cs` become the biggest cost, this is the first thing to
reconsider. Reconsidering it means updating this entry, not quietly adding a file.

## D-6 — How big a machine the AI models need

**PUT OFF until Phase 2 planning.** It can safely wait: nothing uses Ollama (the program that runs AI
models locally) until the Phase 2 feature that explains why the price moved, so no code written
before then depends on the answer. The hardware limit below can't wait, though. It already limits
what Phase 2 can plan for, and it's why this entry stays open instead of being closed as "decide
later".

**The plan's assumption doesn't hold up on the actual hardware.**

The graphics card has 12 GB of memory. A large AI model (around 30 billion settings, compressed to
the usual 4-bit level, called q4_K_M) needs about 18 GB and won't fit. So the plan's fallback of
"use bigger local models" (§10) isn't available. The largest model that fits comfortably is
**about 14 billion settings at q4_K_M, needing about 9 GB**.

The less obvious consequence, for the list of models Phase 2 will use: 12 GB holds roughly *one*
useful model at a time. A 14-billion-setting main model (about 9 GB) plus a small 4-billion one for
checking relevance (about 3 GB) is right at the memory limit, and Ollama will start swapping models in
and out. That turns a relevance check that should take 0.2 seconds into a 15-second model load.
**So the real rule for choosing models is to keep as few different models loaded as possible.** That
points to one 14-billion-setting model doing the explaining, checking and summarising (with different
instructions for each job), plus one model for turning text into searchable vectors.

Speed isn't the risk. A 14-billion-setting model writes about 40 to 60 words-pieces a second, so a
400-piece explanation takes about 10 seconds, well inside the 5-minute target (§12, §16). **The risk is
Phase 2's target: at least 70% of explanations should pass checking on the first try, at 60%
confidence or more.** Measure that early. If it misses, the fix has to be better instructions and
better supporting information, not a bigger model.

Still to do, as the first step of Phase 2 planning: download the candidate models with `ollama pull`
and measure real speeds against the estimates above.

---

## Findings that changed the plan

These are kept as history. The numbers in them were true when written. Later entries changed them.

### GoldAPI's free plan can't support a live dashboard

GoldAPI's free plan allows about **100 requests a month**. Staying inside that meant checking the
price about every 8 hours. At the time, that was the default in `appsettings.json`, and it's why
`PriceSourcesOptionsValidator` refuses to start with a schedule that would overspend.

*Since changed:* API Ninjas is now tried first (D-11), and the app checks every 15 minutes (D-15).

Three checks a day is not a "live price". It can't meet Phase 1's goal of "price visible within twice
the check interval" (§8), or the live-price requirements (FR-1.x), in any meaningful way. The request
counter keeps this honest instead of letting it fail quietly, but it doesn't make the free plan
usable. **Phase 1 needed either a paid plan or a different first service, and the cost model (§12)
needed another look.**

I confirmed the free limit on the account page: 100 requests a month.
**Settled by D-8: stay free during development, and buy the paid plan before Phase 1 goes live.**

### When GoldAPI's allowance resets hadn't been checked

`PriceSourceOptions.QuotaPeriod` defaults to `CalendarMonthUtc` (resets on the 1st of the month). Whether
the service really resets then, or every 30 days from signup, decides which month each request is
counted against. Getting it wrong fails quietly, once a year or so. **Check the account page, not the
documentation.**

*Since checked:* the README records that the account page says 100 requests per calendar month, reset
at midnight UTC.

---

## What the groundwork actually proved

Kept because the decisions above depend on these claims, and the difference between "proved" and
"assumed" is the part that gets forgotten first.

The earlier Docker problem ("permission denied" on `/var/run/docker.sock`) is **fixed**. My account is
in the `docker` group and the tests can start containers. `dotnet test` passes against a real
`timescale/timescaledb-ha:pg17` database.

What that test run proved:

- The first database change applies to the real image. The tests apply it before every group runs.
- `price_ticks` is a TimescaleDB table, stored in one chunk per day, with the 30-day deletion rule.
- pgvector is there, which is the whole reason for D-3.
- The request counter survives a restart. `Budget_survives_a_restart` spends three of five requests,
  throws away its database connection, and a second counter with a fresh connection reads 3, not 0.

At the time, **not yet proved**: that `docker compose up` produces a running API. The tests covered
the database image and the tables, not the full set of containers or the API container.

*Since proved:* the README records `docker compose up --build` from an empty database reaching a
healthy API, with both health checks answering OK.

---

## D-7 — The request counter

**DONE.** `PostgresQuotaGovernor` (in `src/Aurum.App.Infrastructure.Pricing/Quota/`) is the request
counter. The code calls it the "quota governor". `QuotaGovernorTests` and `QuotaHandlerTests` pass.

The design is option A from the Phase 0 review: the database itself holds the count. Taking one
request from the allowance is a single database statement (`INSERT … ON CONFLICT DO UPDATE … WHERE …
RETURNING`). It checks the allowance and adds one in the same step, while that row is locked, so a
second caller can never act on an out-of-date count. The first sketch had three separate cases to
tell apart, and they all fold into that one statement:

- No row for this month yet: the statement creates one.
- Allowance used up: the statement's condition fails.
- The service has turned the app away: the statement's condition fails too.

The notes on the class explain why this is hand-written SQL in a project that otherwise uses Entity
Framework (the library the app uses to talk to the database).

### The start date for services that reset every 30 days

**It comes from the settings. If it's missing, `ResolvePeriod` stops with an error.** The start date
is the day I signed up with that service, which only the service knows. There's no safe default.

Turned down: using `PriceSource.CreatedAt`. That's when the service was added to *this app's*
database, which is a different event. Using it would shift every reset date by the gap between
signing up and first deploying, quietly and forever, with nothing ever pointing it out.

Turned down: working it out from the oldest row in `api_quota_windows`. That's circular, and it would
make reset dates depend on when the app first happened to start.

Put in place by D-9: `PriceSourceOptions.PeriodAnchor` holds the date, and
`PriceSourcesOptionsValidator` won't start the app if a 30-day service has none. Before that, the date
was never passed through at all. `GetCurrentPeriod` always passed nothing, so choosing the 30-day
option made every single request fail. Calendar month remained the default until the account page
was checked (see the finding above).

### No refunds when a request fails on the way

**There's no way to give a request back.** If a request is counted and then fails on the network, it
stays counted.

The two ways of getting this wrong aren't equally bad:

- **Never refunding** wastes one request per network hiccup. That's limited, stops by itself, and
  shows up as the count creeping a little above real use.
- **Refunding** risks spending far more than the monthly limit, and you can't see that happening
  until the service starts answering "too many requests" (HTTP 429). On GoldAPI's free plan, that
  means no prices for the rest of the month.

Counting one too many costs one check. Counting too few can cost the month. At the time, the 8-hour
schedule left about 7 spare requests out of 100, which covered the expected waste.

So `IQuotaGovernor` deliberately has no "give it back" method. Adding one later means changing the
interface, which is the right amount of effort for a decision this lopsided.

`QuotaHandlerTests.Transport_failure_still_spends_the_lease` enforces this. Before that test existed,
the only thing enforcing it was a missing `catch` block in `QuotaHandler`, which no reviewer would
notice. Phase 1 later added retries (using the Polly library) around this code. The test is what
makes a refund added there fail loudly, instead of quietly weakening the promise that the limit is
never exceeded.

### Logging when a request is refused

**The counter writes a low-level debug message that says why it refused. Nothing stops that message
from repeating.**

I thought about having the counter remember "I already logged this month" and discarded it. A new
counter is created for each unit of work, so that memory would start empty every time and never stop
anything. The message people actually need already exists one level up. `PricePollingService` (the
price timer) catches `QuotaExhaustedException`, logs once as an error, and then waits until the
allowance resets. So a used-up allowance produces one line per episode, not one per check.

This entry used to note a flaw: the message said "no budget left" whatever the real reason. That's
fixed. When it refuses, `AcquireAsync` reads the row back and logs "allowance used up" and "the
service turned the app away" differently. They need different responses. The first means wait for the
reset. The second means the app's count and the service's count have drifted apart, which is a
counting bug worth chasing.

Turned down: making the one counting statement also report the reason. That would save a second
database trip and could never be out of date. But it complicates the one statement whose "all in one
step" nature *is* the protection against two requests slipping through at once, just to improve a log
line. The extra read happens once per episode, not once per check, because the timer stops after the
first refusal.

The catch: that read happens outside the counting statement. If the service turns the app away, or
the month rolls over, between the two, the message can be out of date. That's fine for a log line.
It would be wrong for anything that makes a decision, and the code says so where the read happens.
`Denial_after_provider_rejection_names_the_provider` and its opposite,
`Denial_on_a_spent_budget_does_not_blame_the_provider`, stop the two messages from being merged back
into one.

### Marking a month as refused when it has no row yet

**`ReportProviderRejectionAsync` creates the row if it's missing, then updates it.** This is often
called an "upsert".

It used to only update, filtered by service and month. When that month had no row, it changed
nothing and still reported success, so the "turned away" mark silently disappeared. That can happen
when a request crosses the month boundary. The request is counted against the old month, the answer
comes back in the new month, and the "turned away" mark is written against a month nobody has
touched yet.

Whether to mark the new month at all is a fair question. The "too many requests" answer was about
last month's allowance, and the new month's may really be fresh. I mark it anyway, for the same
reason as the refund decision. Over-marking costs one check and fixes itself at the next reset.
Under-marking means repeatedly calling a service that's already turning the app away, and on GoldAPI's
free plan that costs the month.

The new row has a count of 0, which is honest: nothing was counted against this month. A row reading
"0 used out of 100, turned away" is the loudest possible sign that the counts have drifted, and it
matches the rule below that the "turned away" mark never lives in the count.
`Rejection_before_any_acquire_creates_a_clamped_window` enforces this.

### Which clock is in charge

**The app's clock (a `TimeProvider` object handed in to the code), and only that.** The database's own
`now()` never appears in the counter's SQL. Every time written to `api_quota_windows` is passed in
from the app's clock: `PeriodStartsAt`, `PeriodEndsAt`, `CreatedAt` and `UpdatedAt`.

The app works out which month a request belongs to in C#. The database clock could only be in charge
if that were worked out in SQL as well. But then tests couldn't use a fake clock to jump to the next
month, and `ResolvePeriod` couldn't be tested on its own. The problem this avoids is mixing the two:
a month worked out by the app's clock, checked against a boundary written by the database's clock.
They'd disagree by however far apart the two clocks are, and only right around a month change.

Below the counter, this wasn't actually true until recently. `AurumDbContext.ApplyAuditFields` filled
in `CreatedAt` and `UpdatedAt` from the computer's own clock (`DateTimeOffset.UtcNow`). So rows saved
through Entity Framework had real-world times, while rows written by the counter's SQL had the app
clock's times: two clocks in one table. You'd never notice in production, where the two agree to
within microseconds. Under a fake clock in tests, they were a month apart. `AurumDbContext` now
requires a `TimeProvider` when it's created, and `ApplyAuditFields` reads it.

Turned down: making that clock optional, falling back to the real clock. It compiles everywhere, and
the moment someone drops a setup line or creates a database context by hand, it quietly goes back to
the computer's clock. That's this exact bug, returning in exactly the way it hid the first time.
Making it required means the compiler points out every place a context is created. There are two:
`Program.cs`, where it comes from the app's setup, and `PostgresFixture.CreateDbContext` in the tests.
It's optional *there* only because a wrong default in tests fails a test instead of shipping.

`Audit_timestamps_come_from_the_injected_clock` enforces this.

What's left: if two copies of the app ran with clocks that disagree, they could each create a row on
either side of a month boundary. That hands out a little extra allowance, for as long as the clocks
disagree. That's acceptable while the price timer only ever runs as one copy. If the API ever runs as
more than one copy, look at this again together with the timer's own note on running as one copy.

### Reading how much allowance is left

`ReportProviderRejectionAsync` records when the service turned the app away (`ProviderRejectedAt`) and
deliberately leaves the count (`RequestsUsed`) alone. So **"limit minus used" is not what's left**
once the service has turned the app away. The condition "has not been turned away" in the counting
statement is what makes what's left zero.

Setting the count equal to the limit would have made "limit minus used" correct for anyone reading
it. But it wipes out the gap between what the app counted and what the service counted. That gap is
the only evidence that the app's counting is drifting, and it's exactly what `QuotaHandler` logs when
it gets a "too many requests" answer.

---

## D-8 — Free or paid GoldAPI plan

**DECIDED: stay on the free plan during development. Buy the paid plan as a condition of Phase 1 going
live, before anything customers see ships.**

At the time, 100 requests a month with a check every 8 hours was enough for what the groundwork had
to prove: that prices arrive, that the counter survives a restart, and that all the containers come
up. None of that needs frequent checks. It needs a real service on the other end, which the free plan
is.

Waiting is safe because the plan's limit is a number in the settings, not an assumption buried in the
code. `MonthlyRequestLimit` and `QuotaPeriod` come from each service's settings and can be changed per
environment. The settings checker recalculates the shortest safe check interval from whatever limit it
is given. Moving to a paid plan is two settings and a restart. If the free limit had been built into
the timer code, this decision would have had to be made then.

*Updated by D-9:* "no code depends on the number 100" wasn't true when this was first written. The
counter had a built-in default of 100 that any service without its own case fell back to. It's true
now that the fallback is gone.

*Updated by D-10:* the check interval is no longer set per service. It's one setting for the whole
feed.

The risk I'm accepting, stated plainly so nobody later mistakes it for a bug: **Phase 1's goal of
"price visible within twice the check interval" (§8) can't be met until the plan changes.** At the
time, a dashboard running on the free plan would show a price up to 8 hours old, and that was correct
behaviour. The goal stays unmet by choice. Buying the paid plan is what meets it, not a code change.

Second consequence: development produced about 3 prices a day, which isn't enough to test a chart
layout. The D-2 experiment already planned on 30,000 made-up prices. Anything else that needs lots of
data should generate it rather than wait for the timer.

Turned down: **switching to a different first service right away.** It would buy more frequent checks,
but I'd be choosing a service before knowing how often Phase 1 actually needs to check. Phase 1's
fallback list needs a second service anyway. Choosing it then, with real requirements, is a
better-informed choice than choosing it now to avoid a bill.

*This is what happened in the end:* D-11 added two more free services, and D-10 lets the busiest one
go first.

Turned down: **cutting Phase 1 back to a delayed price.** That reshapes the product around a limit that
exists only during development, and money removes it. If a delayed price turns out to be the right
product, decide it on its own merits, not because of a free plan.

When to look at this again, so it doesn't quietly expire: the first Phase 1 work that puts a price in
front of a customer. At that point, set the real limit and interval from the paid plan's numbers.
Read them off the account page, not the pricing page, and update the cost model (§12).

---

## D-9 — Looking up each service's settings, and checking settings that are nested

> The Phase 1 plan's draft list gave D-9 to the fallback-order decision and D-10 to this one.
> Numbers go to decisions in the order they're actually made, so from here that list is off by one.
> This entry is already referred to from `README.md` and from D-7 above.

**DECIDED: the `PriceSources` settings are a list of services, each looked up by its code (such as
`goldapi.io`). A hand-written settings checker checks each entry. Both replace things that failed by
appearing to work.**

Two bugs of the same kind: a promise written in the docs that nothing in the code actually kept.

### The catch-all case

`PostgresQuotaGovernor.GetCurrentPeriod` found a service's allowance with a `switch` on the service's
code. It had one case for `goldapi.io` and a catch-all case that returned nothing, which the caller
then turned into a built-in "100 requests, resetting monthly". A second service would have been
counted against 100 whatever its own settings said, and reset on the 1st however its provider actually
resets.

Neither is a safe guess:

- **Guessing the limit too high** lets a service spend requests it doesn't have. The service's "too
  many requests" answer eventually shuts it off, so the safety net catches it, but only after the
  requests are gone.
- **Guessing the reset schedule wrong** resets the app's count on a different day from the service's.
  The app's records and the account disagree, and neither says so.

The real problem with the catch-all is that its made-up answer looks exactly like real settings to
everything that uses it.

The `switch` existed because the settings had one property per provider, so turning a service code
known only while running into a property name fixed in the code was the only way to read them. Making
the services a list removes that step. Lookup is `TryGetByCode` or `RequireByCode`, and if the code
isn't found, it stops with an error.

Turned down: **keeping the `switch` and adding a case per service.** The smallest edit, and it leaves
the catch-all in place. The bug comes back the first time someone adds a service and forgets. What I
want is "a service's allowance always comes from that service's own settings", and a `switch` can only
keep that promise by hand, every time.

Turned down: **using the service code as the list key** (`PriceSources:goldapi.io:ApiKey`). It reads
better and makes the duplicate check unnecessary. But it puts a dot in every environment variable
name, and the `.env` file readers used by Docker Compose treat dots differently from each other. The
friendly key (`PriceSources:GoldApiIo`) keeps every setting already in `.env` and
`docker-compose.yml` working as-is. The cost is that nothing stops two entries from having the same
`SourceCode`, so the checker rejects duplicates itself. Two entries with the same code would share one
row in `api_quota_windows`, and the second would spend the first's allowance. That's the exact
counting failure this whole area exists to prevent, sneaking back in through the settings.

`PostgresQuotaGovernor`'s settings parameter was also optional, which was the same bug a second
time: anything that created the counter without it got the made-up settings. It's required now, and
the tests state the limits they check against instead of relying on a built-in one.

### The check that never ran

The standard .NET settings check (`ValidateDataAnnotations()`) only looks at the rules on the top
settings object's own properties. It doesn't look inside the objects those properties hold. At the
time, `PriceSourcesOptions` had one property, `GoldApiIo`, with no rules on it. So the check looked,
found nothing, and stopped. Every `[Required]` and `[Range]` rule one level down had never run. The
error message on `ApiKey`'s `[Required]` rule had never been shown by anything.

What this allowed was worse than a failure at startup:

1. A missing API key became an empty string.
2. The web client accepted an empty key in its headers.
3. The app started fine.
4. Every check after that took a request from the allowance, sent a request with no valid key, and
   got "not authorised" (HTTP 401).
5. "Not authorised" isn't "too many requests", so nothing shut the service off, and a counted request
   is never refunded (D-7).

The month drained one request per check, on requests that were never going to work.

Chosen: **a hand-written settings checker** (an `IValidateOptions<PriceSourcesOptions>`). It's the
only option that works with a list: it goes through whatever services are set up, instead of needing
one setup line per service. It's also the right home for the rules that compare one setting with
another. One is the check that the allowance can afford the schedule, moved out of
`PricePollingService.GuardPollBudget`. The other is the rule that a 30-day service needs a start date
(`PeriodAnchor`).

Turned down: **setting up each service's settings as their own separate type**, checked on their own.
Two lines, nothing new to install, and it does fix the missing-key hole. But it needs a setup line per
service, looked up by name, and there's nowhere to put a rule that compares settings.

Turned down: **the .NET code generator that checks nested settings automatically**
(`[ValidateObjectMembers]` with `[OptionsValidator]`). Neat, and it really does look inside nested
objects. But it's a new build-time tool and a new pattern in a project that uses neither, to replace
about forty lines.

### What changed as a result

- `GuardPollBudget` is gone from `PricePollingService`. It ran after the app had already reported
  itself healthy, and it only ever looked at the one built-in service. Startup is the honest place to
  refuse a schedule the allowance can't afford.
- `appsettings.json` ships every `ApiKey` blank. The old placeholder text satisfied `[Required]` and
  would bring the hole straight back. The key has to come from the environment. The database tooling
  (`dotnet ef`) isn't affected: it builds the app but doesn't run it, so the startup checks never run.
- A service with `Enabled: false` skips the key and schedule checks, so a half-set-up service can sit
  in the settings file switched off. `SourceCode` is still required, because it's what identifies the
  entry.
- `GoldApiIoOptions` is gone. The service code constant now lives on `GoldApiIoSource`, where it names
  the code that talks to that service, instead of picking a `switch` case.

---

## D-10 — Where the check interval lives, and whose allowance has to pay for it

**DECIDED: there's one check interval for the whole feed, `PricePolling:PollInterval`. Only the
primary service, meaning the switched-on service with the lowest `Priority` number, has to afford it
for a whole month. Backups don't, and may run out partway through.**

Two things were wrong at the same time:

- **Each service had its own `PollInterval`, but only one was ever used.** `PricePollingService` runs
  one timer and asks for one price each time it fires, so every service's interval except the
  primary's was read by nothing. MetalpriceAPI's ten-minute setting failed the startup check while
  having no effect on how often anything was checked.
- **The startup check assumed every switched-on service would handle every check.** That ties the
  schedule to the smallest allowance in the settings. With GoldAPI's 100 requests switched on anywhere
  in the list, the fastest allowed schedule was one check every 7 hours 26 minutes, and the other two
  services' 11,000 requests bought nothing. The plan asked for three free services so the combined
  schedule would be usable at no cost. That check made it impossible.

*Updated by D-15:* every figure here counts one request per check, which was true when written and
isn't now. A check can use up to `MaxAttempts` requests, so at the default of 3, the GoldAPI-limited
schedule above is one check every 22 hours 19 minutes, not 7 hours 26 minutes. The decision itself
doesn't change. The extra tries make the case for letting backups run out stronger, not weaker.

Letting backups run out is safe because of what the counter already guarantees. The failure this
area exists to prevent is *uncounted* spending: a count kept in memory that resets with the container,
or a retry that slips past the count. A backup that uses up its allowance during a long outage of the
primary is counted, cut off, and visible. `AcquireAsync` refuses, the fallback list moves on, and the
timer waits until the earliest reset. That's the counter doing its job, not the failure it guards
against.

The app refuses to start if two switched-on services tie for the lowest `Priority`. The fallback list
sorts services by `Priority`, so a tie would be broken by the order they happen to be set up in. The
startup check would then guarantee the allowance of a service nobody chose, while the feed might
actually use the other one. Ties further down are allowed. They only decide which backup gets used
first.

The app also refuses to start with no services switched on, for a similar reason. Before, the timer
logged one warning and stopped, while the API reported itself healthy and served nothing.

Turned down: **making every switched-on service afford the full month.** That's the literal reading
of the Phase 1 plan, and it was the rule in the code. It means the fallback list buys reliability but
never speed. Every added service can only lower the ceiling, never raise it, because the limit is
whichever allowance is smallest.

Turned down: **a minimum gap per service**: keep a `PollInterval` on each service meaning "call this at
most this often", and have the feed skip services that were called too recently. It keeps every
allowance honest and lets checks run often. But the feed would then take turns between services as
each became due. Services disagree on the price of gold by a few dollars, so the price would jump by
that amount every time the service changed. The "how much did the price move" feature would then
report moves of exactly that size that never happened. That swaps an allowance problem for a
bad-data problem, in the most important piece of Phase 1.

Turned down: **treating whichever service has `Priority` 1 as the primary**, instead of the first one
that's switched on. Simpler to read, and wrong as soon as the top service is switched off with
`Enabled: false`. The check would keep approving the switched-off service's large allowance while the
service actually handling every check overspends. `Disabling_the_top_source_promotes_the_next_one`
pins this down.

### What changed as a result

- `PollInterval` is gone from each service's settings. An old `PriceSources__GoldApiIo__PollInterval`
  left in someone's `.env` does nothing and gives no error, so the rename had to reach `.env.example`
  and `docker-compose.yml` at the same time.
- `PriceSourcesOptionsValidator` reads the timer's settings. That only goes one way, on purpose. If
  the timer settings' own check ever read the service list, the two checkers would call each other in
  a loop at startup.
- Nothing in the settings shows what a backup running out would cost. So `PricePollingService` logs,
  once at startup, how many days each backup could cover on its own.
- `PricePollingService` no longer checks "is anything switched on?" by looking at GoldAPI alone. That
  check stopped the timer whenever GoldAPI was switched off, even with two other services switched on.

---

## D-11 — Three free services instead of the paid plan

**DECIDED: the fallback list is GoldAPI.io, API Ninjas and MetalpriceAPI, all on free plans, about
11,100 requests a month in total. Buying GoldAPI's paid plan is still a condition of going live (D-8),
not of building.**

The fallback list exists for reliability, but the reason it's three *free* services is arithmetic.
GoldAPI's 100 requests a month pays for one check every 7 hours 26 minutes. Phase 1's "how much did the
price move" feature compares prices over 1-minute and 5-minute windows. At that schedule, every short
window would have one reading in it, and D-17's rule of "not enough readings means no answer" would
return nothing for all of them, forever. That feature could only be tested against replayed made-up
data, and the part that flags big moves would never go off during development. API Ninjas' 10,000
requests and MetalpriceAPI's 1,000 are what make checks every few minutes possible at no cost. That
in turn is what lets items 5 and 6 be tested against real prices, not only made-up ones.

*Updated by D-15:* the 7 hours 26 minutes above counts one request per check. At the default of 3
tries it's 22 hours 19 minutes, and "every few minutes" becomes every 15 minutes rather than every 5.
The argument points the same way and gets stronger. The extra tries hurt GoldAPI's small allowance
far more than API Ninjas' large one, because what matters is the ratio of allowance to checks.

What it doesn't buy, and why that's worth writing down instead of assuming:

- **Only one service has real room to spare, and it only does gold.** API Ninjas' `/v1/goldprice`
  returns gold and nothing else. `ApiNinjasSource` refuses any symbol other than `XAUUSD` instead of
  returning gold for whatever was asked. So Phase 6's other-metals work can't lean on the service
  carrying 90% of the allowance. Silver would get 1,100 requests a month across two services, which is
  one check about every 40 minutes. That's fine for a chart, not for 5-minute moves. Phase 6 needs its
  own decision about services. It doesn't inherit this one.
- **The total isn't something you can spend freely.** 11,100 is three separate allowances, each cut
  off on its own. The primary still pays for the schedule alone (D-10). *When this was written*, that
  meant the schedule was set by GoldAPI's 100, unless GoldAPI was moved down the list. *Since then*,
  API Ninjas has been moved to first place (see the README). The other 11,000 buy cover during
  outages, not faster updates.
- **Three services means three slightly different prices.** Each service quotes gold a few dollars
  differently, so every switch to a backup looks like the price moved by exactly that amount. D-17's
  "this came from a different service" flag reduces that problem. It doesn't fix it.

Turned down: **buying GoldAPI's paid plan now.** One service, one answer format, one way of counting,
and a schedule that supports the price-move feature immediately. But it removes the only thing forcing
the fallback list to be built properly. With plenty of allowance on one service, the fallback list
would be written against a service that never fails, and its first real test would be in production.
D-8 already made the paid plan a condition of going live. Paying earlier swaps a limit during
development for code that has never really been tried.

Turned down: **two services.** GoldAPI plus API Ninjas covers the schedule and is less work. But with
no third service, the "every service failed" path (`AllSourcesFailedException`, and the timer waiting
until the earliest reset) can only be reached by switching off one of the two. And with only two, "try
the next one" and "try the last one" are the same thing, so the code never has to tell them apart. The
third service is what makes the fallback logic general, not just a single backup.

Turned down: **reading the price off a public web page as the third service.** Free and effectively
unlimited. But there's no allowance to count, no agreement with anyone, and no time for when the price
was taken (`ObservedAt`). The "how old is this price" figure the app owes the user (§14) would be made
up, which is exactly the misleading-data problem this phase is built to avoid.

---

## D-12 — Finding the price services: all of them, sorted, not each by name

**DECIDED: each service is registered as one more `IPriceSource`. The fallback list asks for all of
them and sorts them by `Priority`. Registration goes through one shared helper, `AddPriceSource<T>`.**

*Since D-5:* that helper was originally a private method in `PricingModule`. It's now
`Program.AddPriceSource<T>` in `src/Aurum.Api/Program.cs`, and `PricingModule` no longer exists.

`Priority` already says what order the fallback list goes in, and it comes from the settings, so
whoever runs the app can move a failing service down the list by changing one environment variable.
Asking for every service and sorting on that means there's exactly one place the order is decided,
and it's a setting.

The other option is looking each service up by name, such as
`GetRequiredKeyedService<IPriceSource>("goldapi.io")`. That forces the fallback list to keep its own
list of names in the code. That list is a second record of which services exist, ordered by wherever
each line happens to be written. A service added to the settings and to the setup code, but not to that
list, would be quietly missing from the fallback list. No error at startup, and no sign of it until the
primary goes down. The settings checker's "every registered service has settings" check wouldn't catch
it either: the service *is* registered, it's just never asked for.

The shared helper follows from the same reasoning. `AddPriceSource<T>` attaches `QuotaHandler`, the
request counter, as part of registering each service. A service registered without it compiles, works,
and spends its allowance uncounted. It's the one setup mistake in this area that shows no sign at all
until the service starts refusing requests.

The only part that really differs per service is how the API key is sent: in an `x-access-token`
header, an `X-Api-Key` header, or in the web address. So that's the only thing the helper takes as a
parameter. The helper also leaves a `RegisteredPriceSource` marker for each service. That lets
`PriceSourcesOptionsValidator` check at startup that every service registered in code has settings.
That closed a gap left by item 1, where a missing service threw an error deep inside setup instead of
during the startup checks.

Turned down: **looking each service up by name.** Explained above. The cost is the duplicate list of
names. What it buys is the ability to get one named service directly, which nothing in Phase 1 needs.

Turned down: **one fixed list of services, built once at startup** in the right order. It removes the
sorting and puts the order in one place you can read. But each service wraps a web client whose
connection the .NET web client factory swaps out every few minutes, so services are created fresh
each time on purpose. A fixed list would hold one copy of each for as long as the app runs. That's
the same mistake `QuotaHandler` avoids by creating its counter fresh for each request.

Turned down: **using the order services are set up in, and dropping `Priority` from the settings.**
Simplest to read, but moving a service down the list becomes a code change and a deploy. And the
settings checker could no longer see the order. It needs `Priority` to know which service is primary,
because that's the one whose allowance pays for the schedule (D-10).

## D-13 — The time limit per try lives in the retry setup, not on the web client

**DECIDED: the web client's own time limit (`http.Timeout`) is switched off
(`Timeout.InfiniteTimeSpan`). Each try gets its own time limit (`RequestTimeout`), set innermost in
the retry setup, and a second limit (`TotalTimeout`), set outermost, caps the whole run of tries.
`PriceSourcesOptionsValidator` refuses a total that's no longer than one try, and a total that's as
long as the check interval or longer.**

"Retry setup" here means the chain of steps from the Polly library (via
`Microsoft.Extensions.Http.Resilience`) that each request passes through: overall time limit, then
retry, then per-try time limit.

The web client's time limit is a limit on everything together. The clock starts before any of the
request steps run, so it sits outside both the retries and `QuotaHandler`. Once retries are in place,
it no longer means "how long one try may take". It means "how long all tries plus all the waits
between them may take". At 10 seconds, with three tries and waits of 1 and 2 seconds, the third try is
cancelled before it even connects. Which try gets killed depends on how slow the earlier ones were.
The retry setting is there, looks right, and does nothing.

What you see when it goes wrong is worse than the maths:

- When the web client's limit runs out, it throws a plain "task cancelled" error *above* the retry
  steps, so they never see it.
- That failure can't be retried or identified. It reaches the fallback list with no status code and
  nothing saying which service it came from.
- Meanwhile `QuotaHandler` has already counted each try that did go out, and there are no refunds
  (D-7).

So the allowance is spent and the log just says a task was cancelled.

Moving the limit into the retry setup fixes both. Its time limit step throws a specific "timed out"
error (`TimeoutRejectedException`) that the retry step handles and the fallback list can pin on a
service. And because it sits inside the retry loop, each try gets a fresh `RequestTimeout`.

The retry setup is registered *before* `QuotaHandler`, so the retry loop sits above the counter and
every try is counted. Swapping them would retry beneath the counter and spend the allowance
uncounted. That's the same failure that putting the counter at the very edge of the web client (D-7)
exists to prevent.

`TotalTimeout` is its own setting, not worked out from `RequestTimeout` and the number of tries. If it
were worked out, changing the number of tries would quietly change the cap. A separate setting is one
more thing to get wrong, but it's one the settings checker can check at startup. Both new rules catch
problems you'd otherwise never see in production:

- **A total no longer than one try** switches retries off without anyone noticing.
- **A total as long as the check interval** lets one check's tries still be running when the next
  check starts. That spends the allowance twice as fast as the D-10 check was told to expect.

These time limit rules are checked for every service, not only the primary, because a backup's tries
run inside the same check.

Turned down: **the one-line preset retry setup, `AddStandardResilienceHandler()`.** It bundles a
request rate limit, an overall time limit, retries, a circuit breaker and a per-try time limit. Fewer
lines, and its defaults are sensible for a service with a normal allowance. They aren't sensible for
about 100 requests a month. It retries 3 times with random delays, and its circuit breaker trips on a
percentage of failures, so the preset would decide how fast the allowance is spent. With an allowance
this small, every try is a decision, which is what makes spelling the setup out worth the extra lines.

Turned down: **keeping the web client's time limit as an outer safety net** alongside the new limits,
in case the overall limit is ever removed. It brings back exactly the cancellation this decision
exists to move. If that safety net ever went off, it would throw the same unidentifiable "task
cancelled" error above the retry steps. The safety net is `TotalTimeout`, and the settings checker is
what keeps it meaningful.

---

## D-14 — The fallback list's circuit breaker is my own, not Polly's

**DECIDED: `SourceCircuit` has two states, closed (use the service) and open (skip the service). There's
one per service, kept in `SourceCircuitStore`, and `FailoverPriceFeed` (the fallback list) drives it.**

How it works:

1. It counts failures in a row.
2. At `FailureThreshold` failures (3 by default), it opens and remembers when.
3. Once `BreakDuration` has passed (15 minutes by default), it closes again, but it leaves the failure
   count at one below the threshold.
4. Running out of allowance doesn't touch it at all.
5. Its state lives only in memory. It's never saved, and never reloaded at startup.

The Polly library that the retry setup uses (`Microsoft.Extensions.Http.Resilience`) comes with a
circuit breaker. It's one line, and the retry setup is already in place. But it can't see the failure
that matters here:

- Every service reads the price out of the answer *after* the request steps have finished.
- So a service that answers "OK" (`200 OK`) with a broken answer, or a price of zero or less, raises
  `PriceSourceException` only after the request has already returned and been judged a success.
- That's exactly how a free service tends to fall apart.

Polly's breaker would show 0% failures while the fallback list spent a request every check on a
service that hadn't given a usable price in a day. The breaker has to sit above the services, where
that error can be seen, and that's `FailoverPriceFeed`.

The second reason is the clock. Polly measures its rest period against the computer's real clock,
and there's no way to hand it a different one. So an open breaker could only be tested by actually
waiting. That breaks the one rule this project doesn't bend: the `TimeProvider` handed in to the code
is the only source of time for everything the app writes, and the counter's month arithmetic already
uses it. `SourceCircuit` holds one "opened at" time and compares it with a clock reading. That means
every step (open, rest over, try again, open again) can be tested with a fake clock in microseconds.

**Failures in a row, not a failure percentage over a time window.** The feed checks once every few
minutes to once every few hours (D-10). A window short enough to react quickly holds one reading, so
the percentage is always 0% or 100%. A window long enough to hold several readings covers hours of a
service's life, and keeps reporting a service as failing after it has recovered. With so few readings,
the only number that means anything is "the last few checks in a row failed".

**When the rest period ends, the count is left one below the threshold.** That's what makes a
two-state breaker behave like the textbook three-state one.

- **If the count went back to 0**, a permanently broken service would get 3 real calls every time the
  rest period ended, and those calls take 3 checks of their own to happen. At a threshold of 3, a
  15-minute rest and a 5-minute schedule, half of all checks would hit a service already known to be
  dead.
- **Leaving the count at 2** makes the next ordinary check the test. One failure opens it again for a
  full rest period. In the long run that wastes one call per rest period plus one check, or a quarter
  of checks at the same numbers.

It's the same result as the textbook "half-open" state, without the extra field that state needs.

Not having that field is the point, not a side effect. A half-open state lets exactly one caller
through and waits for its result. So it needs a third kind of result for "called, but learned
nothing", and the first "out of allowance" after a rest period is exactly that. Without it, the test
slot is taken by a call that never reports success or failure, and the breaker never closes again.
The two-state version has no test slot to lose, so running out of allowance simply doesn't touch it.

**Running out of allowance is not a failure.** The service is healthy, just out of requests. Opening
the breaker on it would keep the service off the list even after the month rolls over and its
allowance is fresh. It's recorded as `SourceAttemptOutcome.QuotaExhausted`, and it clears on the
counter's schedule, not the breaker's.

**The breaker's state isn't saved, which is the opposite of the request counter in the next folder.**
The counter saves because a spent request is a fact about the service's records that outlasts the
app. A breaker is about what this app has recently seen. A freshly started app hasn't seen anything,
and a service that went down an hour before a deploy is very likely back. Loading an open breaker at
startup would make a new app ignore a healthy service for a rest period it never earned. The notes on
the class say this too, because next to the counter's "always save" rule, not saving looks like a
mistake.

The `price_sources` table's `LastFailureAt` and `LastFailureReason` columns are there for people
looking at the database. The timer writes them once per check, and nothing ever reads them back into
the breaker.

Turned down: **Polly's circuit breaker in the retry setup.** Covered above: it can't see
`PriceSourceException`, and its rest period can't be tested with a fake clock. It would also break per
web client, which is already per service. So the one thing it gets right, `SourceCircuitStore` gets
right for free.

Turned down: **saving the breaker's state to `price_sources` and reloading it.** The breaker would
survive a restart. That sounds useful until a deploy during a service outage leaves the new app
refusing a recovered service, for a rest period it never saw. It would also make the breaker a second
writer of a row the timer already writes, with no way to tell an out-of-date "open" from a current one.

Turned down: **the textbook half-open state with a single test slot.** Clearer to read, and it's the
standard design. But it needs a slot that must be released on every way out of the test, including
the ones that give no result: running out of allowance, cancellation, and the app shutting down during
the call. A slot that isn't released means a breaker that never closes again, and you can't see it
until a service quietly stops being tried.

---

## D-15 — The startup check counts tries, not checks

**DECIDED:**

- `PriceFeed:Resilience` has two settings: `MaxAttempts` (default 3) and `RetryBackoffBase` (default
  2 seconds), the starting wait between tries.
- `Program.AddPriceSource` sets Polly's retry count to `MaxAttempts - 1`, and its wait from
  `RetryBackoffBase`.
- `PriceSourcesOptionsValidator.ValidatePollBudget` multiplies checks per month by `MaxAttempts`
  before comparing with `MonthlyRequestLimit`.
- `ValidateTimeouts` refuses a `TotalTimeout` shorter than
  `MinimumTotalTimeout(RequestTimeout, MaxAttempts, RetryBackoffBase)`.
- The shipped check interval moves to 15 minutes, and the shipped `TotalTimeout` to 40 seconds.

D-13 put the retry loop above `QuotaHandler`, so every try is counted, because that's what the service
charges for. The startup check from D-10 kept counting one request per check. Put together, settings
that looked like 8,928 requests a month against `api-ninjas`' 10,000 could actually spend 26,784. That
undercounts by a factor of three, in the direction that costs a month rather than a check. That's
exactly what the startup check exists to make impossible.

**The setting counts tries, because that's what the check multiplies by.** Polly counts retries, and
the two differ by one (three tries is two retries). The conversion happens once, where the retry setup
is registered. If the setting stored retries and the checker added one, the same arithmetic would live
in two files. Getting it wrong would undercount by exactly one try per check: small enough to get past
review, big enough to empty an allowance early.

**The circuit breaker doesn't limit this, even though it looks like it should.** When a service keeps
failing outright, the breaker comes close:

- At D-14's threshold of 3 and a 15-minute rest, a 5-minute schedule is cut to one test call every
  three checks.
- One test call in three, each using three requests, costs the same as every check using one. The two
  cancel out.

What the breaker can't see is a service that fails twice and then works on the third try. The check
*succeeded*, so `RecordSuccess` resets the count and the breaker never opens, while that service spends
three requests every check forever. `SourceCircuit` sees whether each check worked in the end. Retries
happen underneath it, where it can't see them. So the most expensive way for a service to behave is
exactly the one the breaker is designed to put up with: unreliable, but always working eventually. Nothing
catches that while the app runs. The startup check has to.

**`RetryBackoffBase` is a setting instead of Polly's default**, even though nothing needs to tune it.
`MinimumTotalTimeout` rebuilds the schedule of waits between tries from outside Polly, to decide
whether a `TotalTimeout` leaves room for every try. If that schedule were built from a default that
isn't written anywhere in this project, a library upgrade could change it quietly, turning a correct
check into a wrong one with no code change to review. Stating it makes `Program.cs` and the checker
agree on a value both can see.

**Random waits (`UseJitter`) are switched off on purpose, and the test that pins this found a real bug
rather than confirming there wasn't one.** The web retry options (`HttpRetryStrategyOptions`) switch
random waits **on** by default, unlike Polly's basic retry options. `Program.AddPriceSource` never
switched them off. So from the day it was written, `MinimumTotalTimeout` assumed waits of exactly 2 then
4 seconds (doubling each time) while the real retries used random ones.

The difference went the harmful way. With a starting wait of 200 milliseconds, the measured waits were
(282, 141), (79, 503) and (214, 251) milliseconds, against an expected (200, 400). Randomness both
reorders the waits and makes them *longer*, so the real run of tries can outlast any fixed estimate,
and the method underestimates how long a `TotalTimeout` needs to be. An underestimate is exactly what
lets the checker approve a `TotalTimeout` that cuts off the last try after `QuotaHandler` has already
counted it.

Why switch randomness off rather than plan for its worst case: random waits exist to stop many
programs from all retrying at the same instant. This feed is one timer, running as one copy, sending
one request at a time. `PricePollingService` already assumes that, as does `Program.cs` updating the
database at startup. There's no crowd to spread out. Planning for the worst case instead would mean
inflating the wait estimate by a number read out of Polly's internal code. That's the same kind of
hidden dependency `RetryBackoffBase` exists to remove.

This is the one decision here that wasn't reasoned out in advance.
`Backoff_schedule_matches_what_MinimumTotalTimeout_models` was written to tick off a "not tested" note,
and I expected it to pass. It failed on its first run, and the default was found by measuring. That's
worth saying plainly, because the note it replaced described the risk as possible, when it was
already happening.

**This doesn't undo D-13's choice not to work out `TotalTimeout` from the number of tries.** It's still
a separate setting, for the reason given there: a worked-out cap moves quietly whenever the number of
tries moves. What's new is that the checker now *checks* the setting against the number of tries.
Working it out hides the link between the two settings. Checking it shows the link at startup and makes
whoever runs the app deal with it.

**The new time limit rule exists because the earlier arithmetic was done by eye.** Three 10-second tries
need 30 seconds, plus 2 and 4 seconds of waiting: 36 seconds. Every service shipped with 35 seconds,
which is that sum with the waits forgotten, and the default had the same number. It didn't crash:

- The last try started at about 26 seconds and was cut off at 35.
- So it ran on 9 seconds of its 10, still counted against the allowance, and succeeded often enough
  that nothing looked wrong.

`MaxAttempts` said one number and behaved like another. That's the same kind of quiet mismatch the
other two rules in `ValidateTimeouts` guard against, which is why it sits with them rather than with
the allowance check.

`MinimumTotalTimeout` is internal for the same reason `LongestPeriod` is. `ShippedConfigurationTests`
checks the rule against the real `appsettings.json`. That test exists because unit tests made up their
own settings and missed a mistake in the real file. A test that rewrote the formula itself would only
prove it agrees with itself, while the two drifted apart.

### What changed as a result

- `PricePolling:PollInterval` moves from 5 minutes to 15. At three tries, 2,976 checks a month is 8,928
  requests, inside `api-ninjas`' 10,000. Five minutes meant 26,784 and would have emptied the month
  around day twelve.
- `TotalTimeout` moves from 35 seconds to 40, on all three services and as the default.
- The tests' own interval had to change too. The helper `WithPolling` used 12 hours, with a comment
  saying that was under every limit those tests set. At 3 tries, 62 checks a month became 186 requests
  against the default limit of 100, so the setting meant to keep tests clear of this rule was the first
  thing to break it. It's now 24 hours.

Turned down: **`MaxAttempts: 1`, keeping the 5-minute interval.** The numbers work: 8,928 requests
against 10,000, no interval change, no settings churn. But every small network hiccup then becomes a
switch to a backup instead of a retry, and the backup is `goldapi.io`, with 100 requests a month. At
8,928 checks a month, failing the first try just 1% of the time uses up that whole allowance. A retry
spends one request out of ten thousand. A switch to the backup spends one out of a hundred. The cost
doesn't go away, it moves onto the smallest allowance in the settings, which D-10 deliberately leaves
unchecked. So the startup check would say all is well while the fallback list quietly ran out of
backups.

Turned down: **having the checker read the actual Polly retry setup** instead of rebuilding the
schedule, so the two could never drift apart. That's the right design, and the app's startup order
doesn't allow it. The retry setup is built from inside `AddResilienceHandler`, using the app's
services, and the settings check runs while those same settings are still being loaded. Stating
`RetryBackoffBase` is the half of the guarantee that's affordable.

Turned down: **leaving the startup check at one request per check and treating retries as something to
handle while running**, since the counter already counts every request permanently and cuts off a month
that's used up. It does, and that's what stops the overspending from reaching the service. But the
cut-off arrives after the allowance is gone, twenty days before the month rolls over. The startup check
exists to refuse settings that *can't possibly fit*, and settings that spend three times what they
claim are exactly that, whether or not something further down saves the day.

---

## D-16 — The latest price is kept in the app's memory, and its age is worked out when it's read

**DECIDED:**

- `LatestQuoteCache` keeps the newest price for each symbol in memory, for the whole time the app is
  running. Item 8's `/v1/price/live` will answer from it without reading the database.
- It only accepts a price that is newer than the one it already holds, judged by `ObservedAt` (the
  time the service says the price was taken). An older or equal price is dropped.
- How old a price is (`Age`) is worked out at the moment someone asks: the app's clock minus
  `ObservedAt`. It's never stored.
- A price counts as out of date (`IsStale`) once its age passes `PricePolling:StaleAfter`. That setting
  defaults to twice `PollInterval`, and the app refuses to start if it's set at or below
  `PollInterval`.
- At startup, `EnsureWarmAsync` reloads the newest saved price per symbol from the database.
  `PricePollingService` waits for it before its first check.
- After each successful check, the order is: save in memory, then save to the database, then (from
  item 7) send to connected apps.

*Status on 2026-09-30:* the in-memory part and the `StaleAfter` setting are built and tested. The
startup reload and the change to `PricePollingService` are item 4's next two steps.

**Why the app needs this at all.** Nothing in the running app held "the current price":

- The fallback list (`FailoverPriceFeed`) is created fresh for each unit of work and forgets
  everything afterwards.
- The prices table deletes anything older than 30 days.
- Prices only arrive every 15 minutes (D-15). Reading the table on every request to show one number
  that changes four times an hour is wasted work.

**Age is measured from when the price was taken, not from when the app received it.** That is the
whole age of the price, including any delay at the service. One failure in particular needs this: a
service that keeps answering "OK" but stops updating its price. Each answer arrives on time, so an
age measured from `ReceivedAt` would look fresh forever. Measured from `ObservedAt`, the age keeps
climbing and the price turns stale, which is the truth. The app's own clock (`TimeProvider`) is used,
never the database's `now()`, for the same reason as the request counter (D-7).

**Only newer prices are accepted.** The backups don't all update at the same speed. Without this
rule, a switch to a slower backup would move the displayed price backwards in time between two
checks. Item 5's short-term price history (a ring buffer, which keeps the last few readings and
overwrites the oldest) uses the same rule. If the saved price accepted readings that the history
drops, the latest price and the chart beside it would disagree. The cost, stated plainly: when a
service gets stuck, the held price stays where it is and its age keeps climbing. That's the correct
reading, not a bug.

The check and the swap happen in one step (`ConcurrentDictionary.AddOrUpdate`, with the comparison
inside it). Checking first and writing afterwards leaves a gap where two writers both see the old
price and the slower one overwrites the newer one. I checked that the test for this
(`Concurrent_records_leave_a_consistent_entry`) actually catches that gap. I swapped in the
check-then-write version and the test failed on every run. Its first version only looked at the final
price and passed against the broken code every time, so it was rewritten to check after every round
of writes.

**The stored record leaves out the details of failed tries.** Each try (`SourceAttempt`) carries the
full error for logging. Keeping those would hold on to error details and everything attached to them
for as long as the price stays current, which can be hours. The saved price keeps only the list of
services that were called (`AttemptedSources`).

**A reloaded price is honest about what this run of the app didn't see.**

- Its `AttemptedSources` is empty. That means "this run of the app didn't see which services were
  called", not "no service was called". Item 8 must not show it as a failed fallback list.
- Its `IsFallback` is worked out by comparing the price's service with the first enabled service in
  the current settings. So if the service order changes across a restart, the flag can flip on a price
  that didn't change.

**The reload is a method the poller waits for, not a separate background service.** Background
services start in the order they're registered. Relying on that is a rule nobody re-reads before
adding another one, and getting it wrong shows up as an empty `/v1/price/live` that looks like a
normal fresh start. Item 5's history needs the same startup read, so matching it now means the two can
later be merged by deleting one of them.

Turned down: **.NET's built-in memory cache (`IMemoryCache`).** It throws entries away after a set
time, which is exactly wrong when checks can be hours apart. A thrown-away entry looks the same as
"the app never had a price", when the honest answer is "here's the price, and it's eight hours old".
Showing the age is the requirement. A cache that enforces freshness by deleting can only delete, so
the age a person needs to see is the one thing it destroys.

Turned down: **item 8 reading the newest price from the database on every request.** It's correct and
simple. But every `/v1/price/live` call becomes a read of the prices table. And because that table
deletes anything over 30 days old, the endpoint's answer after a long outage would depend on a cleanup
job.

Turned down: **keeping the latest price on `FailoverPriceFeed`.** It's created fresh for each unit of
work, so each one would start with its own empty copy.

Turned down: **always keeping the latest write instead of only newer prices.** One swap with no
comparison, which is simpler. But a slower backup then moves the displayed price backwards in time.

Turned down: **measuring age from `ReceivedAt`.** It gives a neater threshold, because the age then
lines up with the check interval. But it can't see a service that keeps answering successfully with a
frozen price.

Turned down: **putting `StaleAfter` in its own settings section.** Its default comes from
`PollInterval`. In separate sections the two could drift apart, and nothing would read both.
