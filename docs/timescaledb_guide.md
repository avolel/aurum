# TimescaleDB in this app

TimescaleDB is an add-on for Postgres. It changes how one table, `price_ticks`, is stored on disk.
Queries against that table are ordinary SQL, and the app reads and writes it through EF Core like
any other table.

The database image is `timescale/timescaledb-ha:pg17` (decision D-3). It ships with TimescaleDB and
with pgvector, a second add-on kept for the AI search planned in Phase 2.

## What it does for `price_ticks`

`price_ticks` holds every price the app fetches. It is a hypertable: a table that TimescaleDB splits
into one hidden piece, called a chunk, for each day of `ObservedAt`. The app still sees one table.

The split matters because of the retention policy, a TimescaleDB background job that deletes old
rows. Here it deletes prices older than 30 days. Because each day is its own chunk, the job removes
whole days at once instead of deleting rows one by one.

Both are set up by hand-written SQL in the first migration,
`Migrations/20260730032630_InitialSchema.cs`:

```sql
SELECT create_hypertable('price_ticks', 'ObservedAt',
    chunk_time_interval => INTERVAL '1 day', if_not_exists => TRUE);

SELECT add_retention_policy('price_ticks', INTERVAL '30 days', if_not_exists => TRUE);
```

Three things follow from this:

- **It lives in the first migration on purpose (D-4).** Turning an empty table into a hypertable is
  quick. Turning a table that already holds months of prices into one means moving all that data.
- **EF Core knows nothing about it.** Its model snapshot has no idea the table is a hypertable, so a
  later migration will not undo it. `SchemaTests` checks that the daily chunks and the 30-day policy
  are still there.
- **The 30 days are written in two places.** The migration says `INTERVAL '30 days'`, and the code
  says `PriceTick.Retention`. The history endpoint refuses dates older than `PriceTick.Retention`.
  If the two drift apart, the endpoint serves gaps without saying so.
  `SchemaTests.PriceTicks_retention_policy_matches_PriceTick_Retention` reads the job and compares
  them.

## Why the key is `(ObservedAt, Id)`

`price_ticks` is identified by two columns together, `ObservedAt` and `Id`, not by `Id` alone.
TimescaleDB refuses any "must be unique" rule that leaves out the time column, because it cannot
check uniqueness across chunks. A key on `Id` alone makes `create_hypertable` fail (D-4).

## Which tables are not hypertables

Every other table is an ordinary Postgres table: `price_events`, `app_logs`, `price_sources`,
`api_quota_windows`, `macro_series` and `macro_observations`.

`price_events` holds the big price moves the app detects. It gets a few dozen rows a day, so it gains
nothing from chunks. More importantly, events are the product's history and must last longer than
30 days. A second hypertable would invite someone to add the same 30-day deletion "for consistency",
which would quietly wipe that history (D-18). The `AddPriceEvents` migration says this in a comment
at the top.

## The trap in tests

The retention job runs on the database server's real clock. It ignores the fake clock the tests use.

So a test that stores a tick dated more than 30 days before today can lose that row whenever the job
runs mid-test. The test then fails only sometimes, which is hard to trace. Every test class that
stores ticks dates them from `PostgresFixture.RecentMinute`, which is the real time cut down to the
whole minute.

## How to check it in a running database

Connect to the database (Compose publishes it on `localhost:5432`) and run:

```sql
-- Which tables are hypertables. Expect one row: price_ticks.
SELECT hypertable_name, num_chunks FROM timescaledb_information.hypertables;

-- The chunk size. Expect 1 day on ObservedAt.
SELECT column_name, time_interval FROM timescaledb_information.dimensions
 WHERE hypertable_name = 'price_ticks';

-- The retention job. Expect drop_after of 30 days.
SELECT job_id, schedule_interval, config FROM timescaledb_information.jobs
 WHERE proc_name = 'policy_retention';
```

## What the app does not use

The app does not use compression, continuous aggregates (summary tables that TimescaleDB keeps up to
date by itself), or the steps for converting a table that already has data. If one of those is ever
needed, the TimescaleDB documentation covers it, and the decision belongs in
`ops/decisions/decisions.md`.
