# Data: the database and migration tooling

Postgres with TimescaleDB and pgvector (`timescale/timescaledb-ha:pg17`, D-3).

- `price_ticks` has a **composite primary key `(ObservedAt, Id)`**. TimescaleDB rejects any unique
  index that omits the partitioning column, so a surrogate-only key makes `create_hypertable` fail.
- The first migration does more than create tables: hypertable conversion, the 30-day retention
  policy and the price-source seed all live there. Read it before adding a migration that touches
  `price_ticks`.
- Extensions are declared in **both** `ops/db/init/01-extensions.sql` (superuser, once, on an empty
  volume) and `AurumDbContext.OnModelCreating`. Harmless only because the compose app role is the
  bootstrap superuser; a least-privilege role means the init script must become the sole owner.
- `Program.cs` migrates at startup. Correct for one instance, wrong the moment the API scales out.
  `PricePollingService` carries the same single-instance assumption.
- `AurumDbContext` takes `TimeProvider` as a **required** constructor parameter; see the Pricing
  CLAUDE.md for why. Raw SQL bypasses `ApplyAuditFields`, so pass `CreatedAt`/`UpdatedAt` there.

## How `dotnet ef` finds the database

**No `export` and no `--startup-project`.** `AurumDbContextFactory` is an
`IDesignTimeDbContextFactory<AurumDbContext>`, and EF prefers it over building the application host —
so the tooling never runs `Program.cs`, never hits its connection-string guard, and does not need to
be told where the host is. It is design-time only; nothing in it runs in production.

It resolves the connection string in this order:

1. An exported `ConnectionStrings__Aurum`, so CI and existing habits keep working.
2. `AURUM_DESIGN_CONNECTION` from `.env`.
3. `ConnectionStrings__Aurum` from `.env`.

`.env` is a Docker Compose convention, not a .NET one: Compose injects it as container environment,
and `dotnet ef` on the host never sees it. The factory parses the nearest `.env`, searching upward
from the working directory, which is why the commands work from the repository root or a project
directory.

**`AURUM_DESIGN_CONNECTION` exists because the two connection strings are not interchangeable.**
`.env`'s `ConnectionStrings__Aurum` says `Host=postgres`, which resolves only inside the compose
network; anything that opens a connection — `database update`, `dbcontext script` — runs on your
machine, where that name fails DNS with nothing in the error pointing at the cause. Compose publishes
5432, so the override is the same database at `Host=localhost`. Nothing in `docker-compose.yml` maps
it into a container, so it is tooling-only. `migrations add` and `migrations script` never connect.

`Microsoft.EntityFrameworkCore.Design` is referenced by both this project and `Aurum.Api`. The second
is not needed for the commands above, but `dotnet ef` still builds a startup project when one is
passed explicitly, and removing it makes that form fail confusingly.
