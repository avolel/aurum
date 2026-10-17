---
name: audit
description: Full audit of the Aurum repository. Reads every code, config and doc file (never plans/), finds drift between docs and code, tech debt, and bugs, rates each finding high, medium or low, and files the confirmed ones as GitHub issues. Use only when the owner runs /audit.
disable-model-invocation: true
---

# Repository audit

The audit reads the whole repository, writes down every problem it can prove, and turns each one
into a GitHub issue that the owner fixes by hand. The audit itself changes no code.

## Hard rules

1. **Never read anything under `plans/`.** That means no Read, no Grep, no Glob and no shell command
   that opens, lists or searches it. If a doc links into `plans/`, do not follow the link. Note "links
   to plans/" and move on. `plans/` is untracked on purpose, so a finding can never depend on it.
2. **Never print or read the values in `.env`.** It holds real API keys. Compare it by setting name
   only: `cut -d= -f1 .env | grep -v '^#'`. Read `.env.example` in full instead.
3. **Do not change, stage or commit any file.** The only writes allowed are scratch files in the
   scratchpad directory and the GitHub issues in step 7.
4. **Report only what you checked.** Every finding cites a `file:line` you opened during this run.
   A finding you cannot point at is dropped.
5. **Deliberate decisions are not bugs.** `CLAUDE.md` and `ops/decisions/decisions.md` record many
   choices that look wrong out of context: the raw SQL in `PostgresQuotaGovernor`, no AutoMapper,
   MediatR pinned to 12.5.0, every registration in `Program.cs`, the cache being a
   `ConcurrentDictionary`. Flag one of these only when the code no longer matches the recorded
   decision, or when the reason given for it is no longer true.

## Step 1: Check the tools

Run these. If any fails, stop and tell the owner what to fix.

```bash
gh --version          # GitHub CLI. Missing → sudo apt install gh
gh auth status        # Not logged in → gh auth login
gh repo view --json nameWithOwner -q .nameWithOwner   # expect avolel/aurum
```

## Step 2: Build the file list

```bash
git ls-files --cached --others --exclude-standard \
  | grep -v -E '^plans/' \
  | grep -v -E '^\.env$' \
  > "$SCRATCH/audit-files.txt"
```

`$SCRATCH` is this session's scratchpad directory. `--exclude-standard` drops everything
`.gitignore` hides, such as `bin/`, `obj/` and `node_modules/`. The two `grep` lines are a second
lock on rules 1 and 2.

Mark these as **not read, checked another way**, and list them in the final report:

- Images and other binary files (`app/assets/*.png`): confirm each one is referenced from
  `app.json` or code. An unreferenced asset is a low finding.
- `app/package-lock.json`: machine-written. `npm audit` in step 3 covers it.

Every other file on the list is read **in full**. Read long files in chunks. Do not skim, and do
not stop at the first screen. That includes migrations, the model snapshot, `.csproj` files, the
`Dockerfile`, `docker-compose.yml`, `.gitignore`, `.dockerignore`, `appsettings*.json`,
`launchSettings.json`, the test CSV and every test file.

Keep a running checklist in `$SCRATCH/audit-coverage.md`, one line per file, ticked when read. The
audit is not finished until every line is ticked or carries a reason it was skipped.

## Step 3: Run the cheap checks

```bash
dotnet build 2>&1 | tail -50                              # warnings count as findings
dotnet list package --vulnerable --include-transitive
dotnet list package --outdated
(cd app && npm audit --omit=dev)
(cd app && npx tsc --noEmit)
```

Do not run `dotnet test`. It needs Docker and minutes of real time, and a test failure belongs in its
own debugging session, not in an audit. Write "Tests: not run" in the final report.

## Step 4: Read, in this order

Read the rules first, so the code is judged against what the repository says about itself.

1. `CLAUDE.md`
2. `README.md` and `app/README.md`
3. Every file in `docs/`
4. `ops/decisions/decisions.md`, then the rest of `ops/`
5. Configuration: `docker-compose.yml`, `.env.example`, `appsettings*.json`, `.csproj` files, the
   `Dockerfile`
6. Backend code, project by project: `SharedKernel`, `Infrastructure.Data` (migrations included),
   `Infrastructure.Pricing`, `Application`, `Api`
7. Tests, `src/Aurum.Api.Tests/`
8. Frontend, everything under `app/` on the list

While reading the docs, write down every claim that can be checked: a path, a class name, a test
name, a decision number, a setting, a number. Step 5 checks each one.

Write each finding to `$SCRATCH/audit-findings.md` the moment you find it. The context may be
summarised during a long read, and a finding held only in memory can be lost.

## Step 5: What to look for

### Drift: the docs say one thing and the code does another

- Every path, class, method and test named in `CLAUDE.md`, `README.md`, `docs/` and the decision log
  exists, and does what the text says. Check by opening the file, not by trusting the name.
- Every decision number cited in code or docs (`D-5`, `D-20`, …) exists in `decisions.md`, and
  says what the citing text claims.
- **The compose list.** Every setting in `.env.example` that the app needs has a line under
  `api.environment` in `docker-compose.yml`. The names in `.env` match the names in `.env.example`.
  `CLAUDE.md` lists what should be forwarded; check that list too.
- `PriceTick.Retention` matches the `INTERVAL` in the migration.
- Package versions in `.csproj` match what the docs say: MediatR 12.5.0, no AutoMapper.
- `docs/best-practices*.md` describe a target shape for later phases. Flag text that presents that
  target as something that exists today. Do not flag the missing code itself.
- Comments that describe behaviour the code below them no longer has.

### Rule breaks: the code breaks a rule in `CLAUDE.md`

- Controllers: required attributes, `try`/`catch` in every action, `CancellationToken` on every
  action, `ApiResponse<T>` only, success logging, no database or HTTP access.
- Handlers: `AsNoTracking()` on every read, `CancellationToken` passed down every call, manual
  mapping, no pass-through handlers, constants instead of literal symbols and source codes.
- A validator, controller or handler without its matching test file.
- A registration anywhere other than `Program.cs`.
- Raw SQL that does not pass `CreatedAt` / `UpdatedAt` from the injected clock, or that calls `now()`.
- Anything that reads the wall clock instead of the injected `TimeProvider`.
- K.I.S.S. breaks: unused code, abstractions with one caller and no reason, guards for states the
  validators already rule out, long `<remarks>` blocks that belong in the decision log.

### Bugs, security and tech debt

- Logic errors, off-by-one errors, missed `await`, swallowed exceptions, a `catch` that hides a
  cancellation, races, a `DbContext` shared across threads.
- Secrets or real hosts committed to tracked files. Wide-open settings with no note saying they are
  deliberate for Phase 1.
- Build warnings, vulnerable or badly outdated packages, `npm audit` results.
- `TODO`, `FIXME`, `HACK`, and dead or commented-out code.
- Tests that cannot fail, or that test a copy of the logic instead of the real code.
- Frontend: TypeScript errors, magic numbers, code that does not match `docs/best-practices.md`
  where that guide already applies.

## Step 6: Rate and confirm each finding

| Rating | Use it when |
|---|---|
| **High** | Wrong results, lost or corrupt data, a security hole, an overspent request budget, a crash or a refusal to start, or a doc that would lead someone to make one of those mistakes. |
| **Medium** | A broken repository rule, a missing test on logic that matters, drift that would waste an hour of someone's time, or tech debt that makes the next change risky. |
| **Low** | Style, naming, stale comments, small doc mismatches, dead code, unused assets. |

Then, for every finding:

1. Open the cited lines again and confirm the problem is really there.
2. Check it is not a recorded decision (rule 5). Search `decisions.md` and `CLAUDE.md` for the
   class or setting name.
3. Merge findings that share one cause into a single issue that lists every place.
4. Drop anything that is a guess. Put it under "Not filed: unconfirmed" in the final report.

## Step 7: File the issues

### 7a. Skip duplicates

```bash
gh issue list --state all --label audit --limit 500 --json number,title,state,body > "$SCRATCH/existing-issues.json"
```

A finding that matches an **open** issue is skipped and listed with that issue's number. One that
matches a **closed** issue is a regression: file it again and link the old issue.

### 7b. Show the list and ask before posting

Show a table of every finding about to be filed: rating, type, title and location. Ask the owner
"Post these N issues?" and wait for an answer. They may remove some. Posting to GitHub is public
to anyone who can see the repository, so never skip this question.

### 7c. Create the labels once

```bash
gh label create audit              --color 5319E7 --description "Found by /audit" --force
gh label create "severity:high"    --color B60205 --force
gh label create "severity:medium"  --color FBCA04 --force
gh label create "severity:low"     --color 0E8A16 --force
gh label create "type:drift"       --color 1D76DB --force
gh label create "type:rule-break"  --color 1D76DB --force
gh label create "type:bug"         --color 1D76DB --force
gh label create "type:security"    --color 1D76DB --force
gh label create "type:tech-debt"   --color 1D76DB --force
gh label create "type:test-gap"    --color 1D76DB --force
```

### 7d. One issue per finding

Write each body to a file in the scratchpad and pass it with `--body-file`. Putting the body in a
shell string breaks on backticks and quotes.

```bash
gh issue create \
  --title "[High] Retry attempts are not charged against the GoldAPI budget" \
  --label audit --label "severity:high" --label "type:bug" \
  --body-file "$SCRATCH/issue-01.md"
```

Title: `[High|Medium|Low]` and a plain sentence that says what is wrong, not where.

Body template. The issues are for the owner to read, so the repository's plain-English policy
applies: everyday words, short sentences, no "we", every acronym spelled out the first time.

```markdown
## What is wrong
One or two sentences.

## Where
- [`src/Aurum.Api/Program.cs:142`](https://github.com/avolel/aurum/blob/<commit>/src/Aurum.Api/Program.cs#L142)
- (every other place, if the cause is shared)

## Why it matters
What goes wrong, for whom, and when. Name the failure, e.g. "the app spends three requests per
poll and the ledger records one."

## Evidence
The doc line and the code line that disagree, quoted. Or the build or audit output.

## Direction
One or two sentences on what a fix would need to do. No patch and no code: the owner writes the fix.

---
Found by `/audit` on <date> at commit `<short sha>`.
```

Use `git rev-parse HEAD` for `<commit>` so the links keep pointing at the lines that were audited.

## Step 8: Final report

Print this in the terminal:

1. Counts by rating and by type.
2. Every issue filed, with its link.
3. Findings skipped as duplicates, with the open issue number.
4. Unconfirmed findings that were not filed.
5. Coverage: how many files were read in full, and every file not read with the reason.
6. "Tests: not run", and the result of each check in step 3.
