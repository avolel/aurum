# Phase 2 — AI explanations, the server side

This is my list of the Phase 2 work. Phase 1's server side is finished (`ops/phase-1-todo.md`). See
`ops/decisions/decisions.md` for what's already decided.

**The goal:** every significant price move gets an explanation within 5 minutes of being detected. An
explanation is written by an AI model running on my own machine through Ollama (the program that runs
AI models locally). It gives the likely causes, a confidence for each, a short explanation, and 3 to
5 supporting news links. One that fails its check is published as "Awaiting Confirmation", not hidden.

**This is server-side only**, the same split as Phase 1. The explanation feed screen goes into one
later batch of app work, together with the Phase 1 dashboard.

The items are in order. Item 0 comes first and can stop the phase: if the model can't come near the
quality target, the pipeline isn't worth building as planned. Items 1, 2 and 3 don't depend on each
other. Item 6 needs all of 1 to 5.

Decisions get written into `ops/decisions/decisions.md` **as each item is finished**, not at the end.
New numbers start at D-22. Item 0 closes D-6, which was put off until now.

A few terms used below:

- **Move**: one price move, made of every `PriceEvent` it fired. One sharp move can fire a 5-minute,
  a 15-minute and a 1-hour event. They are grouped and explained once.
- **Explanation**: the AI's answer for one move: causes, confidences, a 2 to 4 sentence explanation,
  and links.
- **Check**: a second pass by the AI that scores whether the explanation is backed by the facts it
  was given.
- **Embedding**: a list of numbers an AI model makes from text, so two pieces of text can be compared
  by meaning. pgvector stores them in PostgreSQL.
- **Scored test set**: about 20 past moves, each with a cause written by me, used to grade the AI's
  explanations.

---

## Found while planning: changes to the original Phase 2 outline

These come from reading the code and the requirements against the original plan in
`plans/aurum-phased-plan.md`.

1. **One model writes and checks.** The requirements (FR-2.4) ask for a second, lighter model for
   checking. D-6 found the graphics card's 12 GB holds about one 14-billion-setting model. Swapping
   between two models costs about 15 seconds each time. So one model does both jobs, with different
   instructions. This departs from the requirements, so it gets its own decision entry.
2. **`docker compose down -v` deletes the downloaded models.** The comment in `docker-compose.yml`
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
   yield and VIX figures are daily, and the dollar index runs about a week behind. The explanation
   gets the latest daily figure with its date. The instructions to the model must say how old each
   figure is, or it will treat a week-old number as today's.
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

### The main design choice: where moves wait to be explained

Moves waiting for an explanation are stored as `explanations` rows with a status, in the database.
One background worker checks every few seconds for the oldest waiting row.

I turned down an in-memory queue (`Channel<T>`) filled by the poller. It avoids checking the
database, but a restart silently loses every move waiting in it, and the 5-minute target is missed
with no record that the move was ever waiting. This is the same reasoning as D-18, where the waiting
period between events moved into the database because memory doesn't survive a restart. A check every
few seconds is well inside a 5-minute target. There is one worker only, the same single-copy
assumption the price timer already makes.

---

## 0. Measure the models, and build the scored test set · must pass before item 6

- [ ] Fix the Ollama version in `docker-compose.yml` to an exact release instead of `latest`.
- [ ] Make the models' volume survive `docker compose down -v`: mark it `external` and create it once
      by hand. Correct the comment that says it already survives.
- [ ] Download 2 or 3 candidate models of about 14 billion settings, plus `nomic-embed-text` for
      embeddings.
- [ ] Measure each model's speed and memory use on the real card, against D-6's estimates (about 9
      GB, 40 to 60 word-pieces a second).
- [ ] Write the scored test set: about 20 past moves, each with the cause written by me. It's the
      only thing that notices quality dropping when a model or the instructions change.
- [ ] Early read on quality: a quick hand-written prompt against the scored test set, for each
      candidate model. The target is 70% passing the check first time, at 60% confidence or more.
      **If the best result is far below that, stop and rethink before building item 6.**
- [ ] **D-6 closed** with the measured numbers and the chosen main and embedding models.
- [ ] Decision written up: one model writes and checks (finding 1).

---

## 1. Economic data · BR-07

- [ ] FRED client: consumer prices (CPI), PCE inflation, the Fed's rate, 10-year and 2-year Treasury
      yields, the dollar index, VIX.
- [ ] BLS client: detailed consumer prices, the jobs report.
- [ ] Treasury FiscalData client: Treasury yields.
- [ ] All three fill the existing `MacroSeries` and `MacroObservation` tables
      (`src/Aurum.App.Infrastructure.Data/Entities/Macro/`).
- [ ] A release calendar, so a new figure is fetched right after its known publish time (8:30 New
      York time for most) instead of at the next scheduled check. `MacroObservation.ReleasedAt` is
      what the 4-hour background window searches on, so it must be the real publish time.
- [ ] Check each provider's limits. FRED and FiscalData have no monthly allowance. BLS has a daily
      one: decide whether it goes through `IQuotaGovernor`, like the price services.
- [ ] Each figure keeps its date, so an explanation can say how old it is (finding 5).
- [ ] `/api/v1/macro/snapshot`, with controller, handler and validator tests.
- [ ] Decision written up: the release calendar, and how old figures are presented.

---

## 2. A minimal news feed

Only enough news for explanations to cite. Scoring, tagging and the other sources are Phase 3.

- [ ] RSS readers for 3 feeds: Kitco, MarketWatch, CNBC.
- [ ] A `news_articles` table storing only the headline, link, a short extract, publish time and
      source (FR-3.7).
- [ ] Duplicates removed by link only. Text fingerprints (SimHash) are Phase 3.
- [ ] Each feed is read in its own try, so one broken feed can't stop the others.

---

## 3. Connecting to Ollama

- [ ] `IOllamaClient`: chat and embeddings, a time limit per request, retries.
- [ ] One request at a time. The card holds one model, and two at once would only queue inside
      Ollama.
- [ ] A settings map from *job* (write, check, embed) to model name. No model name written in code,
      so the model review every three months (§9.4) is a settings change.
- [ ] Answers forced into a fixed JSON shape with Ollama's `format` option, then checked against that
      shape before use. An answer that doesn't fit is a failure, not something to guess at.
- [ ] Instructions to the model (prompt templates) kept as versioned files in `ops/prompts/`, not as
      text inside the code.
- [ ] A stand-in Ollama client for tests, used by `AurumApiFactory` (finding 8).
- [ ] Decision written up: the job-to-model map and the fixed answer shape.

---

## 4. Grouping events into moves

- [ ] A `price_moves` table, and a `MoveId` on `price_events`.
- [ ] An event joins the open move for its symbol when their time spans overlap. Otherwise it starts
      a new move.
- [ ] A move is queued for explanation when it's first created. Events that join it later are
      attached, but don't trigger a second explanation. That limit is written down, not hidden.
- [ ] Grouping runs in `PricePollingService.SaveEventsAsync`, after the events are saved. The poll
      order stays: memory, then the price saved, then apps told, then events.
- [ ] Decision written up: one explanation per move, and the rule for when events belong together.

---

## 5. The store of past moves · FR-2.2

- [ ] A `move_embeddings` table with a `vector` column and an HNSW index (a fast index for finding
      nearest matches), compared by cosine distance (how closely two lists of numbers point the same
      way).
- [ ] Seed it from daily price history plus the economic figures for those dates.
- [ ] Write down how many moves the seed holds, and that they're daily only (finding 7).
- [ ] Decision written up: what the seed holds and what it can't do.

---

## 6. The explanation pipeline · FR-2.1 to FR-2.6, BR-03, BR-04, BR-13

- [ ] One background worker takes the oldest waiting move and:
      1. gathers the background: economic releases from the previous 4 hours, news from the previous
         8 hours, the latest dated daily figures, and similar past moves from item 5. "Previous" is
         counted from the move's start (finding 4).
      2. writes the explanation.
      3. checks it.
      4. saves it.
- [ ] Tables:
      - `explanations`: status, model name and version, Ollama version, prompt version, confidence,
        and check score (§8).
      - `explanation_factors`: the cause categories from FR-2.5, each with a confidence.
      - `explanation_sources`: 3 to 5 links (BR-04).
- [ ] A failed check sets the status to "Awaiting Confirmation". The explanation is still published,
      clearly marked (BR-13).
- [ ] Moves still waiting after a restart are picked up again (the design choice above).
- [ ] After saving, apps connected to `PriceHub` are told "explanation ready".
- [ ] Decision written up: the queue in the database, and the explanation's statuses.

---

## 7. Endpoints

- [ ] `/api/v1/explanations`: newest first, with the same cursor paging as `/api/v1/events`.
- [ ] `/api/v1/explanations/{id}`.
- [ ] Controller, handler and validator tests, as for Phase 1 item 8.

---

## 8. Measuring quality

Both of these are run by hand against the real model, and kept out of `dotnet test`.

- [ ] Run the scored test set from item 0 through the real pipeline. Report the first-pass rate.
- [ ] Replay a busy week of moves and confirm every one is explained within 5 minutes.

---

## 9. Deployment and docs

- [ ] New settings and API keys in `appsettings.json`, `.env.example` and `docker-compose.yml`.
      Compose passes on only the variables it lists, so a new key in `.env` needs its line there too.
- [ ] `CLAUDE.md`, README and `docs/` brought up to date.
- [ ] Run `dotnet format`.

---

## Done when

- [ ] `dotnet test` passes, and no test reaches the real Ollama or any real outside service.
- [ ] `docker compose down -v && docker compose up --build` works from empty, **and the models are
      still there afterwards** (0, 9).
- [ ] With the real model: a move made by hand is explained, checked and saved within 5 minutes, and
      appears at `/api/v1/explanations` and through `PriceHub` (6, 7).
- [ ] The scored test set reaches 70% passing the check first time at 60% confidence or more (8). If
      it doesn't, the result is written up, not hidden.
- [ ] Any explanation can be recreated from its saved prompt version, model version and Ollama
      version (3, 6).
- [ ] Every decision from this phase written up with the options turned down, and D-6 closed.

**Deliberately not in this batch:** the app screens, a priority queue, re-explaining a move that keeps
growing, more than 3 news feeds, scoring or tagging news, and running more than one price timer or
explanation worker.
