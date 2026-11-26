---
name: audit
description: Full security audit of the Aurum repository. Reads every code, config and doc file (never plans/), scans the git history for secrets, checks every place outside data enters the app, then looks for drift between docs and code, tech debt and bugs. Rates each finding high, medium or low and files the confirmed ones as GitHub issues. Use only when the owner runs /audit.
disable-model-invocation: true
---

# Repository audit

The audit reads the whole repository, writes down every problem it can prove, and turns each one
into a GitHub issue with a proposed fix. The owner decides whether to use the fix and applies it by
hand. The audit itself changes no code.

Security comes first. The security pass in step 5 is done before the drift and bug passes, and it
gets the most care. Aurum is a single-user app that today runs only on `127.0.0.1`. Judge each
security finding against how the app is deployed now. If a problem only matters once the app is
reachable from a network, say so in the issue, and still file it.

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
5. **Deliberate decisions are not bugs.** The `CLAUDE.md` files and `ops/decisions/decisions.md` record many
   choices that look wrong out of context: the raw SQL in `PostgresQuotaGovernor`, no AutoMapper,
   MediatR pinned to 12.5.0, every registration in `Program.cs`, the cache being a
   `ConcurrentDictionary`. Flag one of these only when the code no longer matches the recorded
   decision, or when the reason given for it is no longer true.
6. **Never copy a secret into an issue, a scratch file or the terminal.** If a key, password or token
   turns up in a tracked file or in the git history, record only the `file:line` (or commit), the
   setting name and the first four characters. Tell the owner in the terminal **straight away**,
   before going on. A leaked live key must be rotated (replaced with a new one at the provider)
   before anything about it is posted. Deleting it from the file does not help, because it stays in
   the git history.

## Step 1: Check the tools

Run these. If any fails, stop and tell the owner what to fix.

```bash
gh --version          # GitHub CLI. Missing → sudo apt install gh
gh auth status        # Not logged in → gh auth login
gh repo view --json nameWithOwner -q .nameWithOwner   # expect avolel/aurum
gh repo view --json visibility -q .visibility         # PUBLIC changes step 7b
command -v gitleaks   # optional secret scanner. Missing → use the git log scan in step 3
```

If the repository is **public**, anyone can read the issues. Step 7b then holds back every
high security finding: it goes to the owner in the terminal and is not posted.

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

Then the secret scan. It covers every commit, because a key removed from a file stays in the history.
`-l` and `--name-only` print only where something matched, never the matched text (rule 6).

```bash
# With gitleaks: --redact hides the values in its report
gitleaks git --redact --report-path "$SCRATCH/gitleaks.json" .

# Without it: list the commits and files where a likely secret was added
git log --all -p -G '(api[_-]?key|apikey|secret|password|token|registrationkey)\s*[:=]\s*["'"'"']?[A-Za-z0-9_\-]{12,}' \
  --name-only --format='%h %ad' --date=short -i   # each commit line is followed by its files
git log --all --diff-filter=A --name-only --format= | grep -E '(^|/)\.env$|\.pem$|\.pfx$|\.key$' | sort -u
```

Open each match with `git show <commit>:<path>` piped through `cut -c1-40`, not in full, to judge
whether it is a real value or a placeholder.

Do not run `dotnet test`. It needs Docker and minutes of real time, and a test failure belongs in its
own debugging session, not in an audit. Write "Tests: not run" in the final report.

## Step 4: Read, in this order

Read the rules first, so the code is judged against what the repository says about itself.

1. Every `CLAUDE.md`: the root one, then each one under `src/` (they hold the rules for their
   folder). Then the owner's `plain-english-kiss` skill
   (`~/.claude/skills/plain-english-kiss/`), which holds the plain-English and K.I.S.S. policies.
2. `README.md` and `app/README.md`
3. Every file in `docs/`
4. `ops/decisions/decisions.md`, then the rest of `ops/`
5. Configuration: `docker-compose.yml`, `.env.example`, `appsettings*.json`, `.csproj` files, the
   `Dockerfile`
6. Backend code, project by project: `SharedKernel`, `Infrastructure.Data` (migrations included),
   `Logging`, `Infrastructure.Pricing`, `Application`, `Api`
7. Tests, `src/Aurum.Api.Tests/`
8. Frontend, everything under `app/` on the list

While reading the docs, write down every claim that can be checked: a path, a class name, a test
name, a decision number, a setting, a number. Step 5 checks each one.

Write each finding to `$SCRATCH/audit-findings.md` the moment you find it. The context may be
summarised during a long read, and a finding held only in memory can be lost.

## Step 5: What to look for

Do the security pass first. For each area below, find the code that does it, open it, and decide.
Write "checked, nothing found" in `$SCRATCH/audit-coverage.md` for an area with no finding, so the
report shows it was looked at and not skipped.

### Security

**Secrets**

- Keys, passwords or connection strings in tracked files or the git history (step 3).
- `.dockerignore` keeps `.env` out of the image. `.gitignore` keeps `.env` out of git.
- A key that ends up somewhere it can be read. `QueryKeyAuthHandler` puts keys in the URL, so check
  whether a request URL can reach `app_logs` through an exception message, a `details:` string or a
  `HttpClient` log line. Do the same for the BLS key in the request body.
- Error responses. Nothing a client can see carries an exception message, a stack trace, a
  connection string or a key. The controller catch blocks return
  `ErrorMessages.UnexpectedErrorMessage`. Check that each one really does.
- Frontend. Any `EXPO_PUBLIC_*` variable, or any value in `app.json`, ships inside the app, where
  anyone can read it. No key may be there.

**Data from outside the app.** Each of these is untrusted. A provider can be broken, or
taken over.

- RSS feeds (`News/RssClient.cs`, `RssRead.cs`). The XML reader must not process DTDs (document type
  definitions, which can make the parser fetch files or expand into gigabytes). Check the size limit,
  the timeout, and where redirects can lead, for example to `localhost` or to the Ollama port.
- Price, FRED (Federal Reserve Economic Data) and BLS (Bureau of Labor Statistics) responses. Check
  what happens with a missing field, a negative or huge number, a far-future date, or a body that is
  gigabytes long. A bad price must not be saved as a good one.
- News text sent to Ollama (`ops/prompts/`, `Infrastructure.AI`). An article can contain text that
  tries to steer the model ("ignore your instructions…"). This is called prompt injection. Check that
  the prompt marks article text as quoted data, and that the app checks the model's answer before
  saving it or acting on it. Model output must never be used as SQL, a URL or a file path.

**Requests into the app**

- Every endpoint and the SignalR hub (`/hubs/price`) is open by design for Phase 1 (D-20). Do not
  flag that. Do flag an open endpoint that can spend quota, call `IOllamaClient`, write to the
  database, or return more than the Phase 1 data. Each of those breaks an invariant in the root
  `CLAUDE.md`.
- Paging and query values. A client must not be able to ask for an unlimited page size, a huge date
  range, or a cursor that makes the database scan every row. Check the validators set upper limits.
- Hub methods a client can call. Check what a client can send, for example a group name, and
  whether a client can join many groups or send large messages.
- Environment. Swagger and the developer error page are on only in `Development`. Check CORS
  (cross-origin resource sharing: which websites may call the API from a browser), HTTPS redirect
  and HSTS (HTTP Strict Transport Security), and the forwarded-headers note in
  `HttpRequestContextAccessor.cs`.

**SQL.** Every raw SQL call passes values as parameters. `ExecuteSqlRaw`, `FromSqlRaw` or
`SqlQueryRaw` built with string concatenation or `$"..."` is SQL injection, rated high whatever the
input looks like today. `ExecuteSqlInterpolated` and `FromSql` turn `$"..."` into parameters, so they
are safe.

**Containers and the database**

- Every port in `docker-compose.yml` is bound to `127.0.0.1`, not `0.0.0.0` or a bare `"8080:8080"`.
- The `Dockerfile` ends on a user that is not root. Base images are pinned to a version. A floating
  tag such as `pg17` or `latest` is a low finding.
- The connection string does not set `Trust Server Certificate` or `Include Error Detail` outside
  `Development`. The app's database user does not need superuser rights to run.

**Logs and personal data.** `app_logs` stores IP address and user agent. Check how long they are
kept, and that no `details:` string holds a key, a full URL with a key in it, or a whole request
body.

**Dependencies.** Results from step 3. Also any package or image pulled without a version.

### Drift: the docs say one thing and the code does another

- Every path, class, method and test named in the `CLAUDE.md` files, `README.md`, `docs/` and the decision log
  exists, and does what the text says. Check by opening the file, not by trusting the name.
- Every decision number cited in code or docs (`D-5`, `D-20`, …) exists in `decisions.md`, and
  says what the citing text claims.
- **The compose list.** Every setting in `.env.example` that the app needs has a line under
  `api.environment` in `docker-compose.yml`. The names in `.env` match the names in `.env.example`.
  The `CLAUDE.md` files list what should be forwarded; check that list too.
- `PriceTick.Retention` matches the `INTERVAL` in the migration.
- Package versions in `.csproj` match what the docs say: MediatR 12.5.0, no AutoMapper.
- `docs/best-practices*.md` describe a target shape for later phases. Flag text that presents that
  target as something that exists today. Do not flag the missing code itself.
- Comments that describe behaviour the code below them no longer has.

### Rule breaks: the code breaks a rule in a `CLAUDE.md` file or the `plain-english-kiss` skill

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

### Bugs and tech debt

- Logic errors, off-by-one errors, missed `await`, swallowed exceptions, a `catch` that hides a
  cancellation, races, a `DbContext` shared across threads.
- Real hosts committed to tracked files. Wide-open settings with no note saying they are deliberate
  for Phase 1.
- Build warnings and badly outdated packages.
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

Security findings use the same three ratings. Read them like this:

| Rating | Security finding |
|---|---|
| **High** | A live secret in a file or the git history. SQL injection. Anything a request to the API can use, today, to read secrets, write data, spend quota or call Ollama. A known vulnerability in a package the app runs that has a fix available. |
| **Medium** | A missing protection that needs a second problem, or access to the machine, before it can be used. For example, a DTD-processing XML reader on feeds that are HTTPS-only today, or no upper limit on a page size. |
| **Low** | Hardening. A floating image tag, a missing security header, logs kept longer than needed. |

A problem that only matters once the app is reachable from a network is rated one level lower. The
issue says "matters once deployed" in **Why it matters**.

Then, for every finding:

1. Open the cited lines again and confirm the problem is really there.
2. Check it is not a recorded decision (rule 5). Search `decisions.md` and the `CLAUDE.md` files for the
   class or setting name.
3. Merge findings that share one cause into a single issue that lists every place.
4. Drop anything that is a guess. Put it under "Not filed: unconfirmed" in the final report.
5. Write the proposed fix. It must keep every invariant in the `CLAUDE.md` files and every recorded
   decision, and follow the K.I.S.S. policy in the `plain-english-kiss` skill: the smallest change
   that removes the problem. If the only fix
   would break a recorded decision, say so in the issue and propose updating the decision instead.
   If a fix is a real design choice, give two options with the tradeoff of each and recommend one.

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

List high security findings in a separate table under the main one. If the repository is public
(step 1), they are not posted. Give the owner the full write-up in the terminal instead. If it is
private, ask about them separately: "Post these N security issues too?" A finding about a leaked
key is never posted until the owner says the key has been rotated.

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

Body template. The issues are for the owner to read, so the plain-English policy in the
`plain-english-kiss` skill applies: everyday words, short sentences, no "we", every acronym spelled
out the first time.

````markdown
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

For a security finding, say instead who can reach the problem (anyone who can reach port 8080, a
news feed, a price provider, someone on the machine) and the steps they would take. Never include a
working secret. Redact it as in rule 6.

## Proposed fix
<details>
What to change and why, in two to four sentences. Then a before-and-after snippet for each place,
with the file path and enough lines around it to find the spot:

```csharp
// src/Aurum.Api/Program.cs — lines 140-144
// BEFORE
...
// AFTER
...
```

For a doc fix, quote the old sentence and the new one. Name any test that should be added or that
will change. Say what the fix does not cover. End with "Not applied or compiled."

</details>

---
Found by `/audit` on <date> at commit `<short sha>`.
````

Use `git rev-parse HEAD` for `<commit>` so the links keep pointing at the lines that were audited.

## Step 8: Final report

Print this in the terminal:

1. Any leaked secret first, with the setting name and what to rotate.
2. Counts by rating and by type.
3. Every issue filed, with its link. High security findings that were held back, in full.
4. Findings skipped as duplicates, with the open issue number.
5. Unconfirmed findings that were not filed.
6. Security coverage: each area under "Security" in step 5, with "finding #N" or "checked, nothing
   found".
7. File coverage: how many files were read in full, and every file not read with the reason.
8. "Tests: not run", and the result of each check in step 3, the secret scan included.
