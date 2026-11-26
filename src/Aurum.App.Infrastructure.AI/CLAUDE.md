# AI: the Ollama client (D-30)

Phase 2 item 3. One client every later item uses to reach the local model, plus the prompt loader
and a startup check. References Logging and SharedKernel only; writes nothing to the database.
Nothing outside `Aurum.Api` references it yet (item 6's worker will). **None of it has run against a
real Ollama yet**; D-30's "Not checked" lists what that leaves open.

```
caller ─► IOllamaClient (OllamaClient, singleton)
            ├─ ChatAsync<T> ─┐
            ├─ EmbedAsync  ──┼─► PostAsync: _lock ─► named HttpClient "Ollama"
            ├─ LoadAsync   ──┘                        └─ standard resilience handler (2 attempts × 3 min)
            └─ GetModelInfoAsync ─► GET api/tags, api/version   (same client, no lock)
OllamaStartupCheck (BackgroundService) ─► GetModelInfoAsync × 3 jobs ─► LoadAsync × 3 jobs ─► app_logs
```

## The lock (`OllamaClient.cs`)

- **`OllamaClient` is a singleton and the lock is its field.** It takes a *named* client from
  `IHttpClientFactory`, not a typed one: a typed client is transient, so each instance would get its
  own lock. `Program.AddOllama` registers it; don't change the lifetime.
- **Every POST goes through `PostAsync`, which takes the lock.** Chat, embed and load share it. A new
  POST must go through it too, or it races a running answer on the GPU.
- **The lock sits outside the `HttpClient` call**, so the resilience handler's retries and timeouts
  run inside it: a retry keeps the lock, and the attempt timeout starts only once the lock is held.
  Waiting for the lock is bounded only by `ct`. Three tests pin it, and all three fail with the lock
  removed (checked): `ChatAsync_TwoCallersAtOnce_OnlyOneRequestInFlight`,
  `ChatAsync_ServerErrorThenSuccess_RetriesOnceAndKeepsTheLock` and
  `EmbedAsync_WhileAChatIsInFlight_WaitsForTheLock`.
- **`GetModelInfoAsync` takes no lock**: tags and version answer at once mid-answer, so the startup
  check doesn't wait behind a 30 s answer. `GetModelInfoAsync_WhileAChatIsInFlight_DoesNotWait` pins it.
- The answer is checked after the lock is released.
- **No web request may call any client method.** One call can take 6 min 2 s, and a dead Ollama is
  retried. `GetModelInfoAsync` included.

## Settings (`OllamaOptions.cs`, `OllamaOptionsValidator.cs`)

`Ollama:BaseUrl`, `Ollama:Jobs:{Write,Check,Embed}:{Model,ContextWindow}`, `KeepAlive` (`-1`),
`RequestTimeout` (3 min), `MaxAttempts` (2), `Temperature` (0), `Seed` (42).

- **`ValidateDataAnnotations()` doesn't descend into the job map**, so `OllamaOptionsValidator` does:
  every job present, each `Model` non-blank, each `ContextWindow` positive, `RequestTimeout` positive,
  `MaxAttempts >= 2`, `Temperature >= 0`. A failure stops the app at startup, naming the key.
- **`MaxAttempts >= 2`**: Polly refuses zero retries, and the standard handler refuses a total timeout
  not longer than one attempt. `Program.AddOllama` passes `MaxAttempts - 1` as retries.
- **`TotalTimeout` is computed** (`RequestTimeout × MaxAttempts + RetryDelay × (MaxAttempts − 1)`),
  with a constant 2 s `RetryDelay` and `UseJitter = false`, so it matches the real schedule.
  `HttpClient.Timeout` is infinite so its 100 s default can't cut in first. The circuit breaker's
  `SamplingDuration` is `RequestTimeout × 2`, the minimum the handler accepts.
- Compose forwards `Ollama__BaseUrl` and the three `Ollama__Jobs__*__Model`; the rest are
  `appsettings.json` only.

## Requests and answers

- **Every chat and embed request names model, `num_ctx`, temperature, seed and `keep_alive`**; chat
  also sends `stream:false`, `think:false` and the schema as `format`. Never rely on Ollama's defaults
  (finding 11: its default window is a few thousand tokens and it truncates silently).
- **`keep_alive` goes out as a number when the setting parses as one** (`KeepAliveValue`). Ollama
  reads a string as a Go duration, which needs a unit, so `"-1"` would be refused.
- **One options object, `JsonSerializerOptions.Strict`, builds the schema (`AnswerSchema<T>`) and
  reads the answer.** Don't give either side its own options: the shape Ollama is told and the shape
  accepted would drift. Field names are therefore the C# names (`MainCause`), not camelCase.
  `TreatNullObliviousAsNonNullable` keeps the top level from allowing `null`.
- **`Check<T>` order**: prompt filled the window (`prompt_eval_count >= ContextWindow`) →
  `done_reason != "stop"` → strict deserialize → bare `null`. Each throws `OllamaAnswerException` with
  `RawJson`. **Not retried here**; item 6 owns retries and the "Failed" status.
- Ollama's own wire records (`OllamaWire.cs`) are snake_case and **not** strict: Ollama adds reply
  fields between versions.
- A non-success status throws `HttpRequestException` with Ollama's error body in the message.
- **Embed sends `truncate:false`** so long text fails with Ollama's 400 (not retried: 400 isn't
  transient) instead of embedding the cut part. Anything but exactly one non-empty vector throws.
- **`LoadAsync` sends the job's real `num_ctx`.** Ollama reloads a model asked for a different
  window, so a load at the default would be thrown away by the first real call. Chat loads send
  `messages: []` and **omit** `format` (`JsonIgnore(WhenWritingNull)` on `ChatRequest.Format`);
  embed loads send `input: ""`.
- **Model names**: Ollama lists an untagged pull as `name:latest`; `WithTag` adds it, reading the tag
  only after the last `/` so a registry port isn't one. A model not in `/api/tags` throws
  `OllamaModelMissingException`.
- The client doesn't log. Callers do.

## Answer shapes (`Answers/`)

Drafts for item 6: `ExplanationAnswer(Summary, MainCause, OtherCauses, ArticleNumbers)`,
`CauseAnswer(Cause, Confidence)`, `CheckAnswer(Score, GivesAdvice, Reason)`. Public, because item 6
calls `ChatAsync<T>` with them from another project.

- **Any enum in an answer needs `[JsonConverter(typeof(NamesOnlyEnumConverter<T>))]`.** `Strict` has
  no enum converter, so without it the schema says integer. The stock `JsonStringEnumConverter`
  accepts `99` and `"1"`; the subclass passes `allowIntegerValues: false`. Name matching is
  case-insensitive (harmless).
- **No `$ref` in a schema**: nobody has checked Ollama's grammar follows one. The exporter only writes
  one for a type that contains itself. `AnswerSchemas_HaveNoRefs` fails if one appears.
- The schema carries no ranges or list lengths (`[Range]` is ignored by the exporter): 0–100, 3–5
  articles and 2–4 sentences are item 6's checks.

## Prompts (`PromptTemplate.cs`)

`Load(name)` returns the highest `ops/prompts/{name}.v{n}.md` and `"v{n}"`, comparing `n` as a number.
Names not exactly `{name}.v{n}.md` are skipped silently; none at all throws `FileNotFoundException`.

- The csproj copies `ops/prompts/*.md` to `prompts/` in this project's output, and referencing
  projects (Api, tests) copy it on. Read from `AppContext.BaseDirectory`, not the working directory.
- **Docker needs two things**: `!ops/prompts/*.md` last in `.dockerignore` (`ops/` and `**/*.md` both
  exclude it) and `COPY ops/prompts/` in the Dockerfile. Without them the wildcard matches nothing,
  publish still succeeds, and `Load` fails at runtime.
- The whole file is the prompt: no comments in it, or the model reads them.
- Not cached; read on every call.

# The startup check (`OllamaStartupCheck.cs`)

A `BackgroundService`, registered in `Program.AddOllama`. `ExecuteAsync` yields at its first await,
so startup doesn't wait.

1. `GetModelInfoAsync` per job. A missing model → `OllamaModelMissing` (`Error`, `SystemLog`), per
   job, so a model shared by Write and Check logs twice.
2. Any missing → stop; nothing is loaded.
3. `LoadAsync` per job, then `OllamaModelsLoaded` with Ollama's version and each job's model and digest.
4. **Every other exception is caught** → `OllamaStartupCheckFailed`. An exception out of
   `ExecuteAsync` stops the host by default, which would take the price feed down.
   `OllamaStartupCheckTests` fails with the catch removed (checked).

Runs once. An Ollama restart later isn't noticed. Compose's `depends_on: ollama` is `service_healthy`.

## Tests

`Modules/AI/` (no container). `OllamaClientTests` drives `Program.AddOllama` with only the primary
handler swapped, the `ResilienceWiringTests` pattern; the retry test waits the real 2 s.
`FakeOllamaClient` (in `Tests/Infrastructure/`) stands in for the whole client in `AurumApiFactory`.
Per-class notes are in the tests' `CLAUDE.md`.
