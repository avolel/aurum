# Phase 2 — AI explanations, the server side

This is my list of the Phase 2 work. Phase 1's server side is finished (`ops/phase-1-todo.md`). See
`ops/decisions/decisions.md` for what's already decided. Nothing in this list has been started yet.
I last checked it against the code on 2026-10-23.

**The goal:** every significant price move gets an explanation within 5 minutes of being detected. An
explanation is written by an AI model running on my own machine through Ollama (the program that runs
AI models locally). It names one main cause and any other causes, gives a confidence for each, a 2 to
4 sentence explanation, and 3 to 5 supporting news links (BR-04). One that fails its check is
published as "Awaiting Confirmation", not hidden.

Aurum is a personal project, used by me alone. The local model is for that. If Aurum goes live, the
explanations come from a paid cloud AI service instead, most likely Anthropic's Claude (D-6).

**This is server-side only**, the same split as Phase 1. The explanation feed screen goes into one
later batch of app work, together with the Phase 1 dashboard.

**Order.** Item 0 is finished: the main model is `gemma4:12b-it-qat` (D-6). After that:

- Items 1, 2, 3 and 4 don't depend on each other.
- Item 5 needs item 1 (economic figures for past dates) and item 3 (embeddings).
- Item 6 needs 1 to 5. Items 7 and 8 need 6.

Decisions get written into `ops/decisions/decisions.md` **as each item is finished**, not at the end.
New numbers start at D-22. Item 0 closed D-6.

A few terms used below:

- **Move**: one price move, made of every `PriceEvent` it fired. One sharp move can fire a 5-minute,
  a 15-minute and a 1-hour event. They are grouped and explained once.
- **Explanation**: the AI's answer for one move: a main cause and other causes, confidences, a 2 to 4
  sentence explanation, and links.
- **Check**: a second pass by the AI that scores whether the explanation is backed by the facts it
  was given.
- **Background**: everything the model is given to write from: economic releases, news, the latest
  daily figures and similar past moves.
- **Embedding**: a list of numbers an AI model makes from text, so two pieces of text can be compared
  by meaning. pgvector stores them in PostgreSQL.
- **Context window**: how much text the model reads at once. Anything past it is cut off.

---

## Found while planning: changes to the original Phase 2 outline

These come from reading the code and the requirements against the original plan in
`plans/aurum-phased-plan.md`.

1. **One model writes and checks.** The requirements (FR-2.4) ask for a second, lighter model for
   checking. D-6 found the graphics card's 12 GB holds about one 14-billion-setting model. Swapping
   between two models costs about 15 seconds each time. So one model does both jobs, with different
   instructions. This departs from the requirements, and D-6 records it. It applies to the personal
   build only: a cloud AI service in production could use a separate checking model again.
2. **`docker compose down -v` deletes the downloaded models** (fixed in item 0). The comment in `docker-compose.yml`
   says the models' volume survives it. It doesn't: `-v` removes every named volume the file
   declares. The "every phase" check runs exactly that command, and would throw away about 10 GB of
   model files each time.
3. **The Ollama version isn't fixed.** `docker-compose.yml` uses `ollama/ollama:latest`. The same
   model can answer differently under a different Ollama version, which breaks "any explanation can
   be recreated" (§8).
4. **The main price service is 15 minutes behind** (API Ninjas, D-11). The move happened before the
   app noticed it. So news and economic background are gathered back from the move's start
   (`WindowStartedAt`), not from when it was detected. The 5-minute target still counts from
   detection.
5. **"The current dollar index" isn't available free in real time.** FRED's dollar index, 10-year
   yield and VIX figures are daily, and the dollar index runs about a week behind. FRED's dollar
   index is the Federal Reserve's broad index (`DTWEXBGS`), not the ICE index (DXY) that markets
   quote. The explanation gets the latest daily figure with its date. The instructions to the model
   must say how old each figure is, or it will treat a week-old number as today's.
6. **No priority queue yet.** The original plan has explanations jumping ahead of summaries.
   Summaries arrive in Phase 4, so in Phase 2 there's nothing to jump ahead of. One queue, oldest
   first. Phase 4 adds priority when it adds summaries.
7. **The store of past moves can only be seeded with daily moves.** There are only 30 days of
   minute-by-minute prices. Older history (Stooq) is one price a day. So a short intraday move will
   be compared against daily moves. Still worth doing, but the expected benefit gets written down
   honestly.
8. **Tests never call Ollama**, for the same reason they never call the price services.
   `AurumApiFactory` gets a stand-in Ollama client. A separate set of tests, run by hand, calls the
   real model.
9. **A bigger model is not the way out if quality is poor.** The phased plan says to move to bigger
   local models (§10) if the 70% target isn't met. D-6 found nothing bigger fits the card. If the
   explanations are poor, the fix is better instructions and better background.
10. **Past news can't be fetched later.** RSS feeds only show recent articles. So the real pipeline
    can't gather background for a move from months ago. This was the reason a scored test set would
    have needed its news saved by hand, and part of why I dropped it (D-6).
11. **Ollama cuts off long prompts without saying so.** Its default context window is small (a few
    thousand word-pieces). Eight hours of headlines from three feeds, plus figures and past moves,
    can be longer than that. The model then answers from part of the background, and nothing
    reports it. The window size has to be set explicitly, and the background has to fit it.
12. **Explanations must not give advice.** The requirements (§13) rule out financial advice, price
    targets, and buy or sell signals. The model's instructions must say so, and the check must look
    for it.

### The main design choice: where moves wait to be explained

Moves waiting for an explanation are stored as `explanations` rows with a status, in the database.
One background worker checks every few seconds for the oldest waiting row.

I turned down an in-memory queue (`Channel<T>`) filled by the poller. It avoids checking the
database, but a restart silently loses every move waiting in it, and the 5-minute target is missed
with no record that the move was ever waiting. This is the same reasoning as D-18, where the waiting
period between events moved into the database because memory doesn't survive a restart. A check every
few seconds is well inside a 5-minute target. There is one worker only, the same single-copy
assumption the price timer already makes.

Because item 4 is what queues a move, the `explanations` table is created in item 4 with only what
the queue needs (the move, a status, when it was queued). Item 6 adds the rest.

---

## 0. Measure the models and choose one · done

- [x] Fix the Ollama version in `docker-compose.yml` to an exact release instead of `latest`. It
      must be new enough to accept a JSON schema in the `format` option (item 3).
- [x] Make the models' volume survive `docker compose down -v`: mark it `external` and create it once
      by hand. Correct the comment that says it already survives.
      **Keep the existing name.** Compose put the project name in front, so today the volume is
      called `aurum_aurum-ollama-models`. An `external` volume is looked up by its exact name, so
      `name: aurum_aurum-ollama-models` keeps the models already downloaded. Without it, compose
      looks for a volume that doesn't exist and refuses to start.
- [x] Download 2 or 3 candidate models of about 14 billion settings, plus `nomic-embed-text` for
      embeddings.
- [x] Measure each model's speed and memory use on the real card, against D-6's estimates (about 9
      GB, 40 to 60 word-pieces a second). Measure at the context window size the pipeline will
      actually use: a bigger window takes more of the card's memory (finding 11).
- [x] Measure a cold start: how long the first answer takes when the model isn't loaded yet.
- [x] **D-6 closed** with the measured numbers and the chosen main and embedding models:
      `gemma4:12b-it-qat` and `nomic-embed-text`.
- **Dropped:** the scored test set of about 20 past moves, and the early read on quality against
  it. This is a personal project that won't run in production, and saving the news for each past
  move by hand isn't worth the time. Nothing measures explanation quality now. D-6 lists what that
  costs.
- [x] Decision written up: one model writes and checks (finding 1). It's part of D-6, not an entry
      of its own.

---

## 1. Economic data · BR-07

- [ ] FRED client: consumer prices (CPI), PCE inflation, the Fed's rate, 10-year and 2-year Treasury
      yields, the dollar index (finding 5), VIX.
- [ ] BLS client: detailed consumer prices, the jobs report.
- [ ] Treasury FiscalData client: Treasury yields.
- [ ] All three fill the existing `MacroSeries` and `MacroObservation` tables
      (`src/Aurum.App.Infrastructure.Data/Entities/Macro/`). Nothing writes to them yet.
- [ ] Load past figures too, not only the latest, back as far as item 5's seed needs. Without them
      the seed has prices but no economic background.
- [ ] A release calendar, so a new figure is fetched right after its known publish time (8:30 New
      York time for most; the Fed's rate decisions come out at 14:00) instead of at the next
      scheduled check. `MacroObservation.ReleasedAt` is what the 4-hour background window searches
      on, so it must be the real publish time. FRED gives a release date but no time of day, so the
      time comes from the calendar.
- [ ] Daily market figures (yields, VIX, the dollar index) are not releases. They leave `ReleasedAt`
      empty and are used only as "the latest daily figure".
- [ ] Check each provider's limits. FRED and FiscalData have no monthly allowance. BLS has a daily
      one (about 500 requests with a free key, about 25 without; confirm on the BLS site). Decide
      whether it goes through `IQuotaGovernor`, like the price services.
- [ ] FRED and BLS need free API keys. FiscalData needs none.
- [ ] Each figure keeps its date, so an explanation can say how old it is (finding 5).
- [ ] `/api/v1/macro/snapshot`, with controller, handler and validator tests.
- [ ] Decision written up: the release calendar, and how old figures are presented.

---

## 2. A minimal news feed

Only enough news for explanations to cite. Scoring, tagging and the other sources are Phase 3.

- [ ] RSS readers for 3 feeds: Kitco, MarketWatch, CNBC. Check each feed's address still works and
      its terms allow saving headlines and links (§12).
- [ ] A `news_articles` table storing only the headline, link, a short extract, publish time and
      source (FR-3.7).
- [ ] Duplicates removed by link only. Text fingerprints (SimHash) are Phase 3.
- [ ] Each feed is read in its own try, so one broken feed can't stop the others.
- [ ] Decide how often the feeds are read. Eight hours of news is searched back from a move's start,
      so a feed read too rarely can miss articles that fell off it.
- [ ] Decide what counts as gold-related news (FR-2.1). MarketWatch and CNBC cover everything, and
      tagging is Phase 3. The simplest rule is a short keyword list. Without one, most of the eight
      hours is unrelated news that fills the context window (finding 11).

---

## 3. Connecting to Ollama

- [ ] `IOllamaClient`: chat and embeddings, a time limit per request, retries. It reads the existing
      `Ollama:BaseUrl` setting, which is already in `appsettings.json` and `docker-compose.yml` but
      read by nothing yet.
- [ ] One request at a time. The card holds one model, and two at once would only queue inside
      Ollama.
- [ ] A settings map from *job* (write, check, embed) to model name. No model name written in code,
      so the model review every three months (§9.4) is a settings change. That's true for the
      writing model only: changing the embedding model means rebuilding the store of past moves
      (item 5).
- [ ] The context window size set on every request, not left to Ollama's default (finding 11).
- [ ] The model kept loaded between moves, so a move doesn't wait for a cold start (item 0's
      measurement). Ollama unloads an idle model after a few minutes by default.
- [ ] A fixed temperature and seed on every request, saved with each explanation. Even then, a
      graphics card doesn't always give the exact same answer twice. "Recreated" means the same
      inputs and settings, and that limit gets written down.
- [ ] The model's exact version read from Ollama (its digest, a fingerprint of the model file), not
      only its name. A name like `qwen2.5:14b` can point to a different file after a new download.
- [ ] Answers forced into a fixed JSON shape with Ollama's `format` option, then checked against that
      shape before use. An answer that doesn't fit is a failure, not something to guess at.
- [ ] Instructions to the model (prompt templates) kept as versioned files in `ops/prompts/`, not as
      text inside the code.
- [ ] A missing model is reported clearly when the app starts, not on the first move.
- [ ] A stand-in Ollama client for tests, used by `AurumApiFactory` (finding 8).
- [ ] Decision written up: the job-to-model map and the fixed answer shape.

---

## 4. Grouping events into moves

- [ ] A `price_moves` table, and a `MoveId` on `price_events`.
- [ ] `PriceEventSql.InsertAsync` returns the new event's `Id`. Today it returns only whether a row
      was written, and grouping needs the row.
- [ ] An event joins the open move for its symbol when their time spans overlap. Otherwise it starts
      a new move. The rule must also settle:
      - **When a move closes.** Without an end, one long trend chains every event into one move, and
        a day's moves get one explanation.
      - **Long windows.** A 4-hour or 1-day event spans hours, so it can overlap a move that ended
        long ago. A fresh move would then join the old one and never be explained.
      - **Direction.** A rise followed by a fall should probably be two moves.
      Each case gets a test.
- [ ] A move is queued for explanation when it's first created, by writing a waiting `explanations`
      row (the design choice above). Events that join it later are attached, but don't trigger a
      second explanation. That limit is written down, not hidden.
- [ ] Grouping runs in `PricePollingService.SaveEventsAsync`, after the events are saved. The poll
      order stays: memory, then the price saved, then apps told, then events.
- [ ] The event, its move and the waiting row are saved together, or not at all. Today a failure
      after an event is saved falls into the poll's catch. If grouping were separate, that event
      would be saved with no move, and never explained.
- [ ] Decision written up: one explanation per move, and the rule for when events belong together.

---

## 5. The store of past moves · FR-2.2

- [ ] A `move_embeddings` table with a `vector` column and an HNSW index (a fast index for finding
      nearest matches), compared by cosine distance (how closely two lists of numbers point the same
      way). The column's length is fixed by the embedding model (768 numbers for
      `nomic-embed-text`). Each row records which model made it.
- [ ] One way of turning a move into text, used for both the seed and live moves. If the two are
      written differently, the distances compare the wording, not the moves.
- [ ] Get the daily price history. Nothing in the repository reads Stooq yet. Since March 2026
      Stooq asks for an API key before it hands over the CSV, and a script gets a browser check
      instead of the file. Find out whether the key is free, or pick another source, and decide
      whether this is a one-off import or a client.
- [ ] Seed it from daily price history plus the economic figures for those dates (item 1).
- [ ] Write down how many moves the seed holds, and that they're daily only (finding 7).
- [ ] Decision written up: what the seed holds and what it can't do.

---

## 6. The explanation pipeline · FR-2.1 to FR-2.6, BR-03, BR-04, BR-13

- [ ] One background worker takes the oldest waiting move and:
      1. gathers the background: economic releases from the previous 4 hours, news from the previous
         8 hours, the latest dated daily figures, and similar past moves from item 5. "Previous" is
         counted from the move's start (finding 4). The background is cut to fit the context window,
         and what was cut is logged.
      2. writes the explanation.
      3. checks it.
      4. saves it.
- [ ] The model cites news by the number it was given for each article, and the app fills in the
      link. A number it wasn't given fails the shape check. This stops it making up links.
- [ ] Decide what happens when fewer than 3 articles are found. BR-04 asks for 3 to 5 links. The
      simplest answer is to write the explanation anyway and mark it "Awaiting Confirmation", with
      the reason.
- [ ] The cause categories from FR-2.5 kept as named constants, not loose text: Fed and interest
      rates, inflation data, dollar strength, Treasury yields, geopolitical risk, central bank
      buying, appetite for risk, economic surprises, chart breakouts, gold fund flows. Exactly one
      factor is marked as the main cause (BR-04).
- [ ] The instructions forbid advice, price targets and buy or sell signals, and the check looks for
      them (finding 12).
- [ ] Tables:
      - `explanations`: status, model name and version, Ollama version, prompt version, temperature
        and seed, confidence, check score, the full prompt that was sent, and when the row was
        queued and finished (§8).
      - `explanation_factors`: the cause categories from FR-2.5, each with a confidence, one marked
        as the main cause.
      - `explanation_sources`: 3 to 5 links (BR-04).
- [ ] A failed check sets the status to "Awaiting Confirmation". The explanation is still published,
      clearly marked (BR-13). The score below which a check fails is a setting.
- [ ] A move that can't be explained after its retries (Ollama down, an answer that never fits the
      shape) is marked "Failed" with the reason, and the worker moves on. It isn't retried forever,
      and it isn't silently dropped.
- [ ] Moves still waiting after a restart are picked up again (the design choice above). A row only
      leaves "waiting" when its result is saved, so a move half-done at a crash is picked up again
      too.
- [ ] After saving, the move's embedding is added to the store of past moves (item 5). Otherwise the
      store stays the daily seed forever.
- [ ] After saving, apps connected to `PriceHub` are told "explanation ready", in the move's symbol
      group. D-19 lists "events over SignalR" as not handled, so this is the first message on the
      hub that isn't a price.
- [ ] Decision written up: the queue in the database, and the explanation's statuses.

---

## 7. Endpoints

- [ ] `/api/v1/explanations`: newest first, with the same cursor paging as `/api/v1/events`.
- [ ] `/api/v1/explanations/{id}`.
- [ ] Controller, handler and validator tests, as for Phase 1 item 8.

---

## 8. Measuring speed

Run by hand against the real model, and kept out of `dotnet test`. There is no quality measurement:
the scored test set was dropped (item 0).

- [ ] Replay a busy week of moves and confirm every one is explained within 5 minutes, measured from
      detection to the row's finish time. The week must come from after the news feeds started
      collecting, or there's no news to gather.

---

## 9. Deployment and docs

- [ ] New settings and API keys in `appsettings.json`, `.env.example` and `docker-compose.yml`.
      Compose passes on only the variables it lists, so a new key in `.env` needs its line there too.
      Known so far: the FRED and BLS keys, and the job-to-model map.
- [ ] The README's setup steps say to create the models' volume once, and to download the models.
      A fresh clone can't start without the volume (item 0).
- [ ] `CLAUDE.md`, README and `docs/` brought up to date.
- [ ] Run `dotnet format`.

---

## Done when

- [ ] `dotnet test` passes, and no test reaches the real Ollama or any real outside service.
- [ ] Starting from a new, empty database works: `docker compose down`, remove and recreate the
      `aurum_aurum-postgres-data` volume, then `docker compose up --build`. The models' volume is
      left alone, **and the models are still there afterwards** (0, 9). `down -v` can't do this
      check: both volumes are `external`, so it deletes neither.
- [ ] With the real model: a move made by hand is explained, checked and saved within 5 minutes, and
      appears at `/api/v1/explanations` and through `PriceHub` (6, 7).
- [ ] Any explanation can be recreated from its saved prompt, prompt version, model version, Ollama
      version, temperature and seed (3, 6), within the limit item 3 writes down.
- [ ] Every decision from this phase written up with the options turned down.

**Deliberately not in this batch:**

- the app screens, and answers sent in pieces as the model writes them (only the screen needs that);
- a priority queue;
- re-explaining a move that keeps growing;
- skipping moves that are already past the 5-minute target when the queue is behind;
- more than 3 news feeds, and scoring or tagging news;
- deleting old news and explanations. §11 keeps news 60 days and explanations 90 days in the
  database. At three feeds, the tables stay small for months. This needs doing before launch;
- running more than one price timer or explanation worker.
