# Driving Licence Onboarding Workflow

A citizen submits a driving-licence application (personal details + face photo). The API stores it and returns
immediately; a background worker drives it through an auditable workflow — data validation → simulated photo
check → risk assessment — ending in `Approved`, `Failed`, or `PendingManualReview`, where a reviewer approves or
rejects it.

.NET 10 · ASP.NET Core Minimal APIs · EF Core + PostgreSQL · RabbitMQ (`RabbitMQ.Client`) · Docker Compose · xUnit v3

---

## Run it

Requirements: Docker (Compose v2). The .NET 10 SDK is only needed to run the unit tests outside Docker.

```bash
docker compose up --build
```

Starts `db` (PostgreSQL), `queue` (RabbitMQ), `api` and `worker`. The API applies the database schema on startup;
the worker starts once the API is healthy.

| What | Where |
|---|---|
| Swagger UI | http://localhost:8080/swagger |
| OpenAPI document | http://localhost:8080/openapi/v1.json |
| API health | http://localhost:8080/health (the worker has its own, checked by Compose) |
| RabbitMQ management | http://localhost:15672 (user `onboarding`, password `onboarding`) |

### Try it

In Swagger UI, open `POST /api/applications` → *Try it out*. The form is pre-filled with a valid applicant; pick
any JPEG or PNG of at least 10 KB as `photo` and *Execute*. Then call `GET /api/applications/{id}` and
`GET /api/applications/{id}/history`.

Or with curl (Git Bash / Linux / macOS; on Windows PowerShell use `curl.exe`):

```bash
curl -F firstName=Anna -F lastName=Muster -F dateOfBirth=1996-05-10 -F nationalId=AT1234567 \
     -F email=anna@example.com -F phoneNumber=+436601234567 -F "address=Hauptstrasse 1" \
     -F licenceCategory=B -F "photo=@face.jpg;type=image/jpeg" \
     http://localhost:8080/api/applications
# {"applicationId":"…","status":"Submitted"}

curl http://localhost:8080/api/applications/<id>/history
```

Useful inputs: `dateOfBirth` within the last 21 years or `licenceCategory=C` → `PendingManualReview`;
`nationalId=WL000001` (on the default watchlist) → `PendingManualReview`; under 18 or an invalid email → `Failed`.
Whether a given photo passes the quality rules depends on its bytes (see *Simulated rules*), so a photo may also
land in manual review or fail on quality.

Manual review:

```bash
curl -X POST http://localhost:8080/api/applications/<id>/approve
curl -X POST -H "Content-Type: application/json" \
     -d '{"reason":"Photo does not meet quality requirements"}' \
     http://localhost:8080/api/applications/<id>/reject
```

### Tests

```bash
dotnet test
```

781 unit tests (state machine, eligibility rules, manual review) in `tests/Onboarding.UnitTests`. They need no
Docker. `global.json` opts into Microsoft.Testing.Platform, which `dotnet test` requires on the .NET 10 SDK.

### Reset

```bash
docker compose down -v   # removes database, broker and photo volumes
```

---

## API

| Endpoint | Success | Errors |
|---|---|---|
| `POST /api/applications` (multipart/form-data) | `202` `{applicationId, status}` + `Location` | `400` not multipart / unparseable `dateOfBirth` / field sent twice; `413` > 10 MB |
| `GET /api/applications/{id}` | `200` `{applicationId, status, createdAt, updatedAt}` | `404` |
| `GET /api/applications/{id}/history` | `200` `[{timestamp, fromStatus, toStatus, event, details}]` | `404` |
| `POST /api/applications/{id}/approve` | `200` `{applicationId, status}` | `404`; `409` not pending review |
| `POST /api/applications/{id}/reject` (optional `{"reason"}`) | `200` `{applicationId, status}` | `400` reason > 1000 chars / bad JSON; `404`; `409` |

Errors are RFC 9457 ProblemDetails. A `409` names the current status (`detail` and `currentStatus`).

---

## Architecture

```
                 multipart POST
  Client ───────────────────────────►  api (ASP.NET Core)
     ▲                                   │  1. write photo to shared volume (GUID name)
     │  GET status / history             │  2. one DB transaction: application + history + outbox row
     │  POST approve / reject            │  3. 202 Accepted
     │                                   │
     │                     Outbox relay (BackgroundService in api)
     │                                   │  claim unsent rows (FOR UPDATE SKIP LOCKED),
     │                                   │  publish with publisher confirms, mark sent
     │                                   ▼
     │                  RabbitMQ  exchange "onboarding" ─► queue "applications.submitted"
     │                                                         │        ▲
     │                                                         │        │ TTL expiry
     │                                                         ▼        │
     │                  worker (consumer, manual ack) ── retry ──► applications.retry.{1,2,3}
     │                    │  per step: load → rule → transition + history → commit
     │                    │  exhausted ──────────────────────► onboarding.dlx ─► applications.dlq
     │                    │  stale-application sweeper (safety net)
     │                    ▼
     └──────────────  PostgreSQL  (applications, application_history, outbox_messages)
```

| Project | Responsibility |
|---|---|
| `Onboarding.Core` | `Application` aggregate and state machine, history, eligibility rules. No infrastructure dependencies. |
| `Onboarding.Infrastructure` | EF Core `DbContext` + migration, outbox entity, RabbitMQ connection / topology / confirmed publisher, file-system photo store, health checks, DI wiring. |
| `Onboarding.Api` | HTTP endpoints, outbox relay, startup migration, OpenAPI + Swagger UI. |
| `Onboarding.Worker` | `ApplicationSubmitted` consumer, `WorkflowProcessor`, retry / dead-letter handling, stale-application sweeper. Slim web host exposing only `/health`. |
| `Onboarding.UnitTests` | State machine, eligibility rules and manual review. |

### Workflow / state machine

A hand-written transition table inside the `Application` aggregate (`src/Onboarding.Core/Applications/Application.cs`):

| From | To | Event |
|---|---|---|
| — | Submitted | `ApplicationCreated` |
| Submitted | ValidatingData | `ValidationStarted` |
| ValidatingData | CheckingPhoto / Failed | `ValidationPassed` / `ValidationFailed` |
| CheckingPhoto | RiskAssessment / Failed | `PhotoCheckPassed` / `PhotoCheckFailed` |
| RiskAssessment | Approved / PendingManualReview | `AutoApproved` / `ManualReviewRequired` |
| PendingManualReview | Approved / Rejected | `ManuallyApproved` / `ManuallyRejected` |
| Submitted, ValidatingData, CheckingPhoto, RiskAssessment | Failed | `ProcessingFailed` (retries exhausted or stale) |

`Approved`, `Rejected` and `Failed` are terminal. `Status` has no public setter; the only way to change it is
`TransitionTo(to, event, details, at)`, which accepts a transition only if the whole *(from, to, event)* triple is
in the table — so even a legal target reached with the wrong event is refused — and appends exactly one history
entry in the same change. The worker's `WorkflowProcessor` maps each in-progress status to one step:

| Status | Step |
|---|---|
| Submitted | start validation |
| ValidatingData | `DataValidator` — all violations reported together |
| CheckingPhoto | read photo, compute and store quality score, `PhotoChecker` — stops at first failure |
| RiskAssessment | `RiskAssessor` — all raised flags reported together |

### Simulated rules

Ages are computed on the submission date (UTC); someone born on 29 February turns a year older on 1 March in non-leap years.

| Check | Fails / flags when | Outcome |
|---|---|---|
| Data validation | any field missing or blank; email invalid; national ID missing; under 18 | `Failed` (`ValidationFailed`) |
| Photo check | no photo; type not JPEG/PNG; < 10 240 bytes; quality score < 40 | `Failed` (`PhotoCheckFailed`) |
| Risk assessment | under 21; category ≠ `B`; national ID on watchlist; quality score < 70 | `PendingManualReview` |

The quality score is `SHA-256(photo bytes)` reduced to 0–100: deterministic (the same file always scores the same)
without pretending to analyse the image. Thresholds, supported types, minimum size and the watchlist are
configurable (see *Configuration*). The exact rule messages end up in history `details`, e.g.
`"Applicant is below 21"` or `"nationalId is required; Email address is invalid"`.

---

## Design choices and trade-offs

**Hand-rolled reliability on `RabbitMQ.Client` + EF Core, instead of MassTransit / Wolverine / Temporal.**
The assignment grades reasoning about reliability, so every mechanism — outbox, publisher confirms, idempotent
consumer, retry, dead-lettering — is explicit, small and readable in this repository rather than hidden behind
framework conventions. MassTransit would be less code (and is what I would weigh first for a production
system with many message types), but v9 is commercially licensed and v8's open-source support is winding down.
Cost: more infrastructure code (about 400 lines), and connection-recovery and confirm edge cases are ours to get right.

**Custom state machine, no library.** Eight states and thirteen transitions fit in one table that is trivially
unit-tested exhaustively (every allowed and every disallowed transition). A workflow engine would add a runtime,
persistence model and learning curve for no extra guarantee here.

**Photo as multipart upload, stored on a shared volume.** The brief allowed multipart, base64, a separate
upload endpoint or a simulated reference. Multipart keeps a single request that Swagger UI can drive with a file
picker, avoids base64's ~33 % size overhead and lets ASP.NET Core stream large bodies to disk. The file is stored
under a server-generated GUID (client file names are never used as paths); content type, size and storage key go
to the database. Trade-offs: the shared Docker volume stands in for blob storage; the photo is written *before*
the database transaction, and deleted again if that transaction fails — only a process crash between the two can
leave an orphan file (disk waste only — cleanup job listed below). Because the request is multipart rather than
the brief's JSON example, fields are form fields; a field sent twice is rejected with `400`.

**Business validation in the worker, not the API.** The API rejects only malformed requests (not multipart,
unparseable `dateOfBirth`, too large). Missing or invalid values are accepted and fail in `ValidatingData`, so the
outcome is recorded in the audit trail exactly as the brief's failure flow (`Submitted → ValidatingData → Failed`)
describes.

**`Rejected` vs `Failed`.** `Rejected` is a human decision only. Rule failures (validation, photo) and technical
failures (retries exhausted) are `Failed`, distinguished by event (`ValidationFailed`, `PhotoCheckFailed`,
`ProcessingFailed`) and `details`.

**The application's status is the idempotency record.** No inbox/processed-messages table: each step commits its
transition before the next starts, so a redelivered message simply resumes from the stored status, and a message
for a terminal or `PendingManualReview` application is acknowledged without effect.

**Optimistic concurrency with PostgreSQL `xmin`.** Two workers on the same application, or approve and reject
arriving together, cannot both commit; the loser reloads (worker) or gets `409` (API). No extra version column.

**Per-attempt delay queues for backoff.** `applications.retry.1..3` each have a fixed queue TTL (5 s, 20 s, 80 s)
and dead-letter back to the main exchange. Per-message TTLs on a single delay queue would block behind the
message at the head of the queue; the delayed-message plugin would add a broker dependency.

**Others:** EF migrations applied by the API on startup (single replica locally; a migration job in production);
.NET 10 (current LTS); `TimeProvider` everywhere for testable time; built-in JSON console logging with scopes;
first-party health checks plus a ~10-line RabbitMQ check rather than a third-party package.

---

## Reliability

### What happens if the same message is processed twice?

Nothing new happens on the second delivery. The worker loads the application and runs only the step for its
*current* status. If it is already terminal or `PendingManualReview`, the message is acked without any write. If
two deliveries race on the same in-progress application, both try the same transition; the `xmin` concurrency
token lets exactly one commit, and the other reloads and continues from the new status (or acks if it is done).
Verified: a duplicate published by hand for an `Approved` application was acked with status, history and row
version unchanged; two workers given 20 copies for 10 applications produced exactly 5 history rows each.

### What happens if the API saves the application but fails to publish the message?

It cannot happen that way: the API never publishes. In the same database transaction as the application and its
first history entry it writes an `outbox_messages` row. A relay inside the API claims unsent rows
(`FOR UPDATE SKIP LOCKED`, so several API replicas don't double-publish), publishes each with publisher confirms,
and marks it sent only after the broker confirmed it. If RabbitMQ is down, submissions still return `202`; the
row stays unsent (attempt count and last error recorded) and is published once the broker is back. If the relay
crashes after the broker confirmed but before it marked the row, the message is published again — a duplicate,
absorbed by the idempotent consumer (previous answer). Verified with `queue` stopped: `202`, outbox row pending,
processed to `Approved` within seconds of the broker restarting.

### What happens if the worker crashes during processing?

Messages are acknowledged manually, only after the application reaches a resting state. A crash leaves the
message unacked, so RabbitMQ redelivers it. Because each step's transition + history entry is committed before the
next step starts, the redelivered message resumes at the first step not yet committed — earlier transitions are
not repeated. A crash mid-step loses only that uncommitted step, which is re-run (all steps are deterministic and
side-effect free apart from the commit). Verified: `SIGKILL` of the worker while it was working through 40 queued applications —
16 messages were redelivered, all 40 ended `Approved` with exactly 5 history entries each.

### How are invalid workflow transitions prevented?

In the domain model: `Application.Status` can only change through `TransitionTo`, which checks the
*(from, to, event)* triple against the transition table and throws `InvalidTransitionException` otherwise,
leaving state and history untouched. Terminal states have no outgoing transitions. The API checks the status
before manual actions and returns `409 Conflict` naming the current status; the database concurrency token covers
the race between the check and the commit. Unit tests cover all 13 allowed transitions and every other
status/target/event combination.

### How are failures recorded?

As workflow transitions with a reason, in the same audit trail as everything else
(`GET /api/applications/{id}/history`):

- Rule failures: `ValidationFailed` (all violations, `; `-separated) or `PhotoCheckFailed` (the first failing check).
- Technical failures after retries are exhausted: `ProcessingFailed` with `"<ExceptionType>: <message>"`
  (truncated to 500 characters); the message itself is dead-lettered with an `x-error` header.
- Applications stuck in progress: `ProcessingFailed` with `"Processing timed out after 15 minutes without progress"`.
- Manual rejection: `ManuallyRejected` with the reviewer's reason.

Every log line about an application carries `applicationId` (and `messageId` where relevant) as structured JSON
fields, so failures can be correlated across API and worker logs.

### How would retries or dead-letter handling work?

Implemented:

- Business-rule outcomes are never retried — they are transitions, not exceptions.
- Any exception while processing is treated as transient. The worker republishes the message to
  `applications.retry.{attempt}` with `x-attempt` incremented, waits for the broker's confirm, then acks the
  original. That queue's TTL (5 s → 20 s → 80 s, configurable) expires the message back to the main queue.
- After the last attempt (4 by default, ~105 s in total) the worker marks the application `Failed`
  (`ProcessingFailed`, error summary), publishes a copy with `x-attempt`/`x-error` headers to `onboarding.dlx` →
  `applications.dlq`, and acks. If that publish fails it rejects the message instead; the main queue's own
  dead-letter exchange still routes it to the DLQ (without `x-error`).
- Unparseable messages and messages for unknown applications are rejected straight to the DLQ (no point retrying).
- If marking the application `Failed` itself fails (typically the same database outage), a **stale-application
  sweeper** in the worker fails applications stuck mid-workflow (`ValidatingData`, `CheckingPhoto`,
  `RiskAssessment`) with no progress for 15 minutes and no unsent outbox row. A message that arrives later for such
  an application is acked as a no-op. It deliberately ignores `Submitted`: those may just be waiting in a queue
  backlog (e.g. after a worker outage), and failing them would reject applicants who were never checked. The
  trade-off: if even the very first step can never be saved (database down for the whole retry window), the
  application stays `Submitted` with its message in the DLQ for an operator to replay. The first sweep runs one
  interval after startup, so a restarted worker drains its backlog first.
- If scheduling a retry itself fails (broker trouble), the message is requeued after a 5-second pause so it cannot
  spin; if that never recovers, the sweeper ends it.
- The worker's `/health` is unhealthy whenever it is not subscribed to the queue. If the broker closes its channel
  or cancels the subscription (e.g. the queue is deleted), the worker re-declares the queues and resubscribes.

The DLQ is for inspection and manual replay (e.g. via the RabbitMQ management UI). There is no automated replay.

### What would you improve before production?

See *Production improvements* below.

---

## Configuration

Environment variables use `__` as separator (e.g. `Eligibility__Watchlist__0=AB123456`). A configured array
replaces the default rather than being appended to it.

| Key | Default | Used by |
|---|---|---|
| `Eligibility:SupportedContentTypes` | `["image/jpeg","image/png"]` | worker |
| `Eligibility:MinPhotoSizeBytes` | `10240` | worker |
| `Eligibility:MinPhotoQualityScore` | `40` | worker |
| `Eligibility:ReviewPhotoQualityThreshold` | `70` | worker |
| `Eligibility:Watchlist` | `["WL000001"]` | worker |
| `PhotoStorage:RootPath` | `/data/photos` | api, worker |
| `PhotoStorage:MaxUploadBytes` | `10485760` | api |
| `Outbox:PollIntervalMs` / `Outbox:BatchSize` | `1000` / `50` | api |
| `Messaging:MaxAttempts` | `4` | worker |
| `Messaging:RetryDelaysSeconds` | `[5,20,80]` (must have `MaxAttempts − 1` entries) | api, worker (queue declaration) |
| `Messaging:PrefetchCount` | `10` | worker |
| `Sweeper:IntervalSeconds` / `Sweeper:StaleAfterMinutes` | `60` / `15` | worker |
| `ConnectionStrings:Postgres`, `RabbitMq:*` | set in `docker-compose.yml` | api, worker |

Retry delays are queue properties: changing them requires recreating the queues (`docker compose down -v`), or
RabbitMQ refuses the redeclaration.

---

## Smoke test: retry → dead-letter path

Simulates a persistent technical failure by deleting the photo file before the worker reads it. Bash (Git Bash on
Windows). Takes about two minutes because of the default retry delays.

```bash
# 1. Hold processing so the photo can be removed first.
docker compose stop worker

# 2. Submit an application with a photo (any JPEG ≥ 10 KB) and keep its id.
ID=$(curl -s -F firstName=Anna -F lastName=Muster -F dateOfBirth=1996-05-10 -F nationalId=AT1234567 \
       -F email=anna@example.com -F phoneNumber=+436601234567 -F "address=Hauptstrasse 1" -F licenceCategory=B \
       -F "photo=@face.jpg;type=image/jpeg" http://localhost:8080/api/applications \
     | sed -E 's/.*"applicationId":"([^"]+)".*/\1/')
echo "$ID"

# 3. Delete its photo from the shared volume.
KEY=$(docker compose exec -T db psql -U onboarding -d onboarding -tAc \
      "select photo_storage_key from applications where id='$ID'")
MSYS_NO_PATHCONV=1 docker compose exec -T api rm "/data/photos/$KEY"

# 4. Resume processing and watch the attempts (about 5 s, 20 s and 80 s apart).
docker compose start worker
docker compose logs -f worker | grep --line-buffered "$ID"     # Ctrl+C after "Message dead-lettered"

# 5. The application is Failed with the error recorded...
curl -s http://localhost:8080/api/applications/$ID/history
#   … "toStatus":"Failed","event":"ProcessingFailed","details":"FileNotFoundException: Could not find file …"

# 6. …and the message is in the dead-letter queue with x-attempt and x-error headers.
curl -s -u onboarding:onboarding -H "content-type: application/json" \
     -X POST http://localhost:15672/api/queues/%2F/applications.dlq/get \
     -d '{"count":5,"ackmode":"ack_requeue_true","encoding":"auto"}'
```

`MSYS_NO_PATHCONV=1` stops Git Bash rewriting the container path; it is harmless elsewhere.

---

## Assumptions

- "Required fields" are all eight personal-detail fields; the photo is checked separately by the photo check.
- Ages are computed against the submission date in UTC; someone born on 29 February turns a year older on 1 March in non-leap years.
- The client-declared photo content type is trusted (the checks are simulated); the quality score comes from the bytes.
- Watchlist matching ignores letter case (`wl000001` is caught like `WL000001`). Licence category is exact (`b` is
  not `B`, so it goes to manual review — the safe direction). Content types compare case-insensitively.
- An empty file part counts as "no photo".
- One message per application drives the whole workflow; there are no per-step messages.
- Timestamps are UTC, ISO-8601 with an offset (`…+00:00`).

## Shortcuts

- No authentication or authorization — the manual-review endpoints are open.
- Swagger UI is enabled in every environment, and credentials are plain values in `docker-compose.yml`.
- No submission idempotency key: retrying the POST creates a second application.
- No integration tests in the repository; the end-to-end, crash, race and failure scenarios above were run
  manually against Docker Compose.
- The API applies migrations at startup.
- A single photo volume shared by both containers stands in for blob storage.
- Retry-queue TTLs are fixed per queue, so changing the delays needs the queues recreated.

## Production improvements

- **Auth:** authentication for all endpoints; a reviewer role for approve/reject; the reviewer's identity recorded
  in the audit trail.
- **Submission idempotency key** (`Idempotency-Key` header with a unique index) so client retries don't create
  duplicates; optionally duplicate detection per national ID.
- **Photo storage:** blob storage (S3 / Azure Blob) with private access, a cleanup job for orphaned uploads,
  magic-byte sniffing instead of trusting the declared content type, malware scanning.
- **Data protection:** retention/purge for PII and sent outbox rows, encryption at rest, PII kept out of logs and
  out of error details exposed through the history endpoint.
- **Observability:** OpenTelemetry tracing across API → outbox → broker → worker; metrics for queue depth, DLQ
  size, retry counts and outbox lag, with alerts on DLQ growth and stale applications.
- **Tests:** integration tests with Testcontainers (PostgreSQL + RabbitMQ) automating the scenarios verified
  manually here; contract tests for the message.
- **Operations:** migrations as a separate deployment step; secrets from a secret store; DLQ replay tooling;
  RabbitMQ quorum queues; outbox relay with LISTEN/NOTIFY or CDC instead of polling.
- **Workflow:** if steps become slow or call external services, one message per step (or a durable workflow
  engine) instead of running the whole workflow in one delivery.

---

## AI usage

This project was vibe coded, as the brief allows: I built it by describing what I wanted to an AI coding tool and
steering it step by step. The AI generated most of the code, the unit tests and this README.

- **My part:** choosing the approach and trade-offs (hand-rolled reliability over a messaging framework, multipart
  photo upload, validation in the worker, `Rejected` vs `Failed`), setting the scope, and reviewing the result
  phase by phase.
- **Verification:** every phase was run against Docker Compose, not only unit-tested. Running it caught issues the
  code alone did not show — e.g. `.Accepts<T>()` making routing answer `415` instead of the required `400`,
  Swagger UI not offering a file picker, and a duplicate `status` key in `409` responses — and a final code review
  found more (the stale-application sweeper could fail applications that were only queued; the watchlist match was
  case-sensitive). All of these were fixed and re-verified.
