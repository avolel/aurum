# Aurum

Aurum tracks the price of gold. It asks outside price services for the current price on a regular
schedule, stores every answer, and will later show those prices in an app.

**Where things stand: the groundwork is done, and Phase 1 items 0 to 3 are finished.**

What has been built and checked by hand:

- The database is set up. Prices are stored in a table called `price_ticks`. You read and write it
  like any other table, but behind the scenes a database add-on called TimescaleDB stores each day's
  prices separately. That lets the database delete prices older than 30 days by throwing away whole
  days at once, which is much faster than finding and deleting old rows one by one. This cleanup
  runs on its own; no code in this app does it.
- A request counter keeps track of how many requests the app has sent to each price service this month,
  so it never goes over the free allowance. The count is saved in the database, so it survives a
  restart. I tested that with a real restart, not only in the test suite.
- Starting from a completely empty database, `docker compose up` brings the whole system up, and both
  health-check pages (`/health` and `/health/ready`) answer "OK".
- Real prices have come in from the live GoldAPI account. On 2026-09-07 I compared one against a
  public gold price and they matched.
- All tests pass.

The numbers in brackets, like (D-7), point to entries in the decision log,
`ops/decisions/decisions.md`. Each entry explains a choice I made and what I turned down.

## The three price services

I use the free plans of three services instead of paying for one. That gets prices often enough
at no cost, and gives the app somewhere to turn when one service stops answering (D-11).

| Order tried | Service | Free allowance | How long it can cover for the others | Notes |
|---|---|---|---|---|
| 1st | `api-ninjas` | about 10,000 requests a month | it is the main one | one price only; **gold only** |
| 2nd | `goldapi.io` | 100 requests a calendar month | about 1 day | buy price, sell price and a middle price |
| 3rd | `metalprice-api` | about 1,000 requests a month | about 10 days | one price only; the API key goes in the web address; its answer also has an upside-down ounces-per-dollar figure, which the app ignores |

"How long it can cover" assumes the backup works on the first try. A backup that keeps failing and
retrying uses up its allowance up to three times faster.

**Why API Ninjas goes first:** it is the only free plan big enough to let the app check the price every 15
minutes. Every check can take up to three tries, and each try counts against the allowance, so a
31-day month can need about 8,928 requests. GoldAPI's 100 requests would allow one check roughly
every 22 hours. That is too slow for the "how much did the price move in the last 1 or 5 minutes"
feature planned for later, which would never have enough readings to work.

The downside: API Ninjas gives one price, not separate buy and sell prices. The app only sees buy and sell
prices when GoldAPI is standing in. Those prices can't be compared across services anyway (see the
note under D-1).

**Only the first service has to last the whole month.** The backups are allowed to run out during a
long outage. When they do, the counter notices and stops sending them requests (D-10). So how often
the app checks the price is set by the first service alone. The extra 11,000 or so requests from the
others buy time during an outage, not faster updates.

### How much of this I have actually confirmed

I checked GoldAPI's limit on the account page itself: **100 requests per calendar month, reset at
midnight UTC on the 1st.** If you set a check interval that the first service can't afford for a
whole month, the app refuses to start and tells you the shortest interval that would work. Moving to
a paid plan later means changing two settings and restarting. No code depends on the number 100
(D-8, D-9).

> **The other two limits come from each service's published documentation. Nobody has checked them
> on an account page.** That matters most for API Ninjas, because the whole schedule depends on its
> 10,000. If the real limit is lower, or resets on a different day, the app's counter and theirs will
> disagree. You would see it as a row in the counter table that says the app still has requests left but
> also says the service turned it away. Check both account pages before relying on them.

## What happens when a service fails (item 3, done)

The part of the app that checks the price on a schedule no longer picks a service itself. It asks
for "a price", and a separate piece goes down the list:

1. Try the services in the order above, skipping any that are switched off in the settings.
2. Skip any service that has failed several times recently. It gets a rest before the app tries it again.
   This is called a **circuit breaker**, after the electrical kind: it "trips" to stop sending
   requests to something broken.
3. If a service has used up its monthly allowance, move straight to the next one.
4. Return the first price any service gives back.

What changed as a result:

- **Switching a service off in the settings now works.** Before, the setting existed but nothing
  read it.
- **One service running out no longer stops the whole feed.** The app only pauses when *every*
  service has run out, and then only until the *first* one resets. A single failure or a single
  tripped breaker doesn't pause anything, because those clear up in minutes and an allowance can
  take a month to come back.
- **I wrote my own circuit breaker instead of using the one in the Polly library** (D-14). Polly
  (the library that handles the app's retries and timeouts) only sees whether the request went through. A
  service can answer "OK" with a page of nonsense, and the app only finds that out later, when it tries to
  read the price out of it. Polly's breaker would think that service was fine and keep paying for
  requests to it. Mine sits where the nonsense is visible.
- **The breaker's memory is thrown away on restart, on purpose.** The request counter works the
  other way and is kept forever. The difference: a request the app spent is spent no matter what, but "this
  service was failing" is only the app's own recent impression, and a fresh start has none. For people
  looking at the database, the `price_sources` table shows each service's most recent failure and
  why.

Two smaller changes:

- A successful price no longer wipes out the reason for the last failure. Compare the time of the last
  failure with the time of the last success to tell whether the failure is current.
- When a service is skipped because its breaker is tripped, nothing is written, so the original
  failure reason stays visible.

**Not built yet:** the saved "latest price", the "how much did it move" feature, live updates to the
app, and the web endpoints. Those are items 4 onward in `ops/phase-1-todo.md`.

**The `IsEnabled` column in the `price_sources` table does nothing.** Nothing reads it and nothing
writes it. The settings file decides which services are on. Item 8 will either hook the column up
or delete it. Don't change it in the database and expect anything to happen.

## Decisions that were made without testing

Three early experiments were closed without being run. D-1 and D-2 are decisions I stated rather
than measured, and D-6 is put off until Phase 2. D-2 picks `lightweight-charts` for charts on the web
and leaves the phone version until Phase 4, when there is a real phone to test on. Phone simulators
give misleading speed numbers. Each entry says this in its status line, so read that before treating
one as a measured result.

The decision log has **D-1 to D-16** so far. `ops/phase-1-todo.md` lists the remaining work in an
order where each item can be checked when it is finished. Numbers D-17 and up are already assigned
there to the items that will need them.

## Where things are

```
docker-compose.yml                 starts the database, Ollama (a local AI model) and the API
ops/db/init/                       one-time database setup, runs only on an empty database
ops/decisions/                     the decision log (D-1 onward; the code refers to these numbers)
docs/                              guides on how the code is organised
src/Aurum.App.SharedKernel/        small pieces every project uses
src/Aurum.App.Infrastructure.Data/ the database: tables, changes to them, saving
src/Aurum.App.Infrastructure.Pricing/
  Sources/                         the three price services, the fallback order, the circuit breaker
  Quota/                           the request counter
  Jobs/                            the timer that checks the price
src/Aurum.App.Application/         the rules for handling requests, and logging
src/Aurum.Api/                     the web API; Program.cs sets everything up
src/Aurum.Api.Tests/               tests, run against a real database
app/                               the start of the mobile app; nothing much in it yet
```

The code is split into five projects, based on the layout in `docs/best-practices-api.md`, with two
differences recorded in D-5:

- The code that talks to outside price services and runs the timer has its own project,
  `Infrastructure.Pricing`, because it doesn't fit the database project.
- `Aurum.Api` can see every other project, because it is the one place where everything gets
  connected together.

**All of that connecting happens in one file: `src/Aurum.Api/Program.cs`.** No other file sets up
services. D-5 explains why, and what that costs.

`docs/best-practices.md` and `docs/best-practices-redux.md` describe a finished app that doesn't
exist here yet. `app/` is an empty starting point. Treat those two guides as the goal for Phase 4, not
as a description of code you can use today.

## Running it

```bash
cp .env.example .env      # fill in the database password and the three API keys
docker compose up --build
```

On startup the API brings the database up to date by itself. `/health` tells you the API is running.
`/health/ready` also checks it can reach the database.

**Every API key is required.** The settings file ships each key blank on purpose. A missing key stops
the app at startup. Without that, the app would start fine and then be turned away by the service
on every request for a month, and every turned-away request still counts against the allowance. A
fake key in the settings file would bring that problem back.

**Only run one copy of the API.** If two copies ran, both would check prices and spend the allowance
twice, and both would try to update the database at the same moment.

You need permission to use Docker. If `docker ps` says "permission denied", run
`sudo usermod -aG docker $USER` and log out and back in. Nothing here works without it.

## Tests

```bash
dotnet test
```

There are 83 tests. Most of them start a real copy of the database in Docker rather than a fake one,
because the special table types the app uses behave differently from a plain database and a fake would
prove nothing. One database is started per group of tests, and each test cleans up after itself.

Tests worth knowing about:

- **The circuit breaker and fallback tests** (`SourceCircuitTests`, `FailoverPriceFeedTests`) don't
  need Docker and finish in a few milliseconds. That's because the breaker reads the time from a clock
  the tests can control, instead of setting a real timer.
- **`ShippedConfigurationTests`** checks the real settings file. Every other test makes up its own
  settings, which is how a setting that the app couldn't afford once shipped and only failed when the
  app started for real.
- **`QuotaGovernorTests` and `QuotaHandlerTests`** describe exactly how the request counter should
  behave. One of them sends 40 requests at the same moment against a limit of 10, to prove none slip
  through.
- **`ResilienceWiringTests`** checks that every retry is counted against the allowance. It uses the
  real setup code from `Program.cs` rather than rebuilding it, because the thing being tested is the
  order of two lines in that code. The first time it ran it found that the app never retried when a
  service answered with an error code. The retry setting looked right and did nothing.

**Some tests guard a decision, not a bug.** For example, the app never gives back a request that failed,
because the service still counts it. Nothing in the code says so; the rule is simply that there is
no code to give it back. `Transport_failure_still_spends_the_lease` is what makes adding that code
fail loudly. If you delete a test like that because "it tests nothing", you remove the guard.

**Known random failure.** The tests sometimes start before the test database has finished being
created. When that happens, every test in the group fails with `database "aurum_test" does not
exist`. Running them again works. The fix is to wait for the database itself, not just the database
server.

## Why the request counter matters

Every service the app uses limits how many requests it gets per month, and the smallest limit is 100. A
counter kept only in memory starts from zero on every restart. It could spend a month of requests in
an afternoon, and each single request would look fine while it happened. So:

- **The count is saved in the database.**
- **It's checked on the way out the door.** The counter sits just before the request leaves the app,
  underneath the retries and the fallback logic. If it sat any higher, retries would slip past it
  uncounted.

**Each service is counted separately, and the counter knows nothing about any particular service.**
It looks up that service's limit and reset schedule in the settings. There is no shared limit and no
default. If a service has no settings, the counter stops with an error rather than guessing, because
a guessed limit looks exactly like a real one to everything downstream (D-9).

Services reset on different schedules, and each one is set to match its provider:

- **Calendar month:** resets at midnight UTC on the 1st.
- **Rolling 30 days:** resets every 30 days, counting from a start date set for that service.

If the app's counter reset on a different day from the service's, the two would drift apart with no sign
anywhere.

**How the count is stored.** The `api_quota_windows` table has one row per service per month. Each
service's rows are separate, so one service running out says nothing about the others. A new month
means a new row, so nothing ever has to be reset, and old rows stay as history.

Taking one request from the allowance is a single database statement that checks and adds one in the
same step, while the row is locked. Doing it in two steps (read the count, then add one) leaves a gap
where two requests can both see "one left" and both go through.

**When a service turns the app away for going over.** The app records the time it happened on that month's row
(`ProviderRejectedAt`) and leave the count alone. So a row can say "0 used out of 10,000, turned
away", and that mismatch is the point: it's the only sign the app's count and theirs have drifted apart.
It also means "limit minus used" is **not** how many requests are left. Once a row is marked as
turned away, none are left.

**How often the app checks, and the allowance, are linked.** Three settings go together: how often the app checks
the price, the first service's monthly limit, and how many tries it allows per check. If the first
service can't afford the schedule for a whole month, the app won't start, and it tells you the
shortest interval that would work. Changing one of these usually means changing another, or
changing which service goes first. Holding every service to a full month sounds safer but is worse:
the schedule would then be set by the smallest allowance, and adding a service could only ever slow
things down (D-10).

**One check can be up to three requests.** Every try is counted, so the startup check multiplies
checks per month by tries per check (D-15). Nothing stops that while the app is running. The circuit
breaker only sees whether a check worked in the end, so a service that fails twice and then succeeds
costs three requests every time while looking perfectly healthy. The overall time limit for a check
must also leave room for every try plus the waits between them. If it doesn't, the last try gets cut
off, is still counted, and "3 tries" really means fewer.

**There is one check interval for the whole feed.** Each service used to have its own interval
setting, but the app only ever makes one check at a time, so only the first service's setting was
ever used. That setting was removed. If an old `PriceSources__GoldApiIo__PollInterval` is still in
someone's `.env`, it does nothing and gives no warning.

**All times come from one clock.** Every time the app saves, including "created at" and "updated at"
on each row, comes from the same clock object that is handed to the database code. It's required,
not optional. An optional clock that falls back to the computer's own clock would work everywhere
and quietly give two different times in the same table.

If you're thinking of replacing the counter's database statement with ordinary Entity Framework code
(the library the app uses to talk to the database), first read the notes at the top of
`PostgresQuotaGovernor` and D-7 in the decision log.
