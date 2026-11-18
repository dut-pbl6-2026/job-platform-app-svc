# AGENTS.md - Application Service Guidelines

## Architecture - Clean Api / Core / Infrastructure

```
src/App.Api              Web API (Program.cs, Endpoints, DevAuthMiddleware, /health)
src/App.Core             Domain (Application, StatusHistory, ApplicationStatus, DTOs)
src/App.Infrastructure   Data (AppDbContext, Migrations) + LocalCvStorageService
tests/App.Tests          xUnit integration & unit tests
AppService.sln           mise run build/test
```

Dependency: `Api -> Infrastructure -> Core -> SharedKernel` (`PackageReference JobPlatform.SharedKernel 0.4.0` via the GitHub Packages feed in `nuget.config`).

## SRS Mapping (APP-01)
- `POST /api/applications` - Candidate applies with CV upload (multipart/form-data: job_id, cover_letter, cv_file). Prevents duplicate application per (job_id, applicant_id) -> 409 Conflict.
- `GET /api/applications/me` - Candidate views their application history (paginated, filterable by status).
- `GET /api/applications/status-flow` - System-wide status flow state machine metadata and transition map.
- `GET /api/applications/{id}` - View application details, complete status transition audit history, and next allowed transitions.
- `GET /api/applications/{id}/history` - Candidate/Recruiter views chronological status transition audit trail.
- `GET /api/applications/{id}/allowed-transitions` - Query valid next status transitions for a specific application.
- `GET /api/applications/job/{jobId}` - Recruiter views applications submitted for their job.
- `PUT /api/applications/{id}/status` - Recruiter updates application status following the status flow state machine (`pending -> reviewed -> shortlisted -> accepted`, with `rejected` at any non-terminal stage, terminal states: `accepted`, `rejected`), optionally updating recruiter notes, and appends to status_history.
- `GET /api/applications/cv/{fileName}` - Secure file download/streaming for CV files.
- `GET /health` - Service healthcheck returning `{"status":"ok","service":"application"}`.

## Database (app_db)
- Database schema: `applications`, `status_history`, `outbox_events`, `scoring_jobs`
- Schema migration is a separate deploy step: `dotnet App.Api.dll migrate` (`Bootstrap/MigrateCommand.cs`, compose service `app-migrate`). The web host never migrates on startup.
- Unique index: `IX_applications_job_applicant_unique` on `(JobId, ApplicantId)`
- Port: `5004` (gateway upstream `http://localhost:5004`)
- Env vars: `APP_INTERNAL_TOKEN_FOR_JOB`, `APP_INTERNAL_TOKEN_FOR_AUTH`, `JOB_INTERNAL_TOKEN_FOR_APP` (per-caller internal tokens), `DATABASE_URL_APP`, `APP_PORT`, `APP_CV_STORAGE_PATH`, `AI_SERVICE_URL` and `AI_SCORING_SERVICE_TOKEN` (automatic CV scoring; the worker stays disabled until both are set; Production refuses a token shorter than 32 characters)

## Automatic CV scoring
- Applying creates one `scoring_jobs` row (Pending) in the same SaveChanges as the application. Nothing else triggers scoring.
- `ResumeScoringWorkerService` claims due rows with `FOR UPDATE SKIP LOCKED` under a 10-minute lease, reads the CV through `IFileStorageService`, and calls ai-svc `POST /api/ai/score-resume` (`cv_file`, `job_id`) with `Authorization: Bearer <AI_SCORING_SERVICE_TOKEN>`, a per-caller token that ai-svc accepts on that route only. The token is read from configuration; no JWT is minted. ai-svc fetches the job description itself.
- Counted failures back off (30 s, doubling, capped at 15 min); after 8 the row is Failed and logged at Error with its id. Rate limit or busy responses are not counted.
- Recruiters see `score` and `scoreStatus` (pending, done, failed) on the job list and the detail. Applicants never see either. Applications created before scoring have no row: `scoreStatus` is null.
- The whole ai-svc response (band, component breakdown, matched/missing skills, suggestions) is stored in `scoring_jobs."ScoreDetail"` and returned by `GET /api/applications/{id}` as `scoreDetail` for recruiters/admins. It is the report the deleted trial-scoring screen used to show; the web renders it on the application page and drops the improvement advice. A row scored before this column existed has no detail; the score still shows.

## Engagement: templates, email and interviews

- `email_templates` (per recruiter: name, subject, body; private to the author) and `interviews`
  (application/job/company snapshot, candidate and recruiter identity, UTC start, duration, mode,
  location, notes, status, stable calendar UID) are owned by app-svc. Migration:
  `AddInterviewsAndEmailTemplates`.
- Applications now snapshot `ApplicantName` / `ApplicantEmail` from the verified token at submission.
  The gateway forwards `X-User-Email` / `X-User-Name` (percent-encoded) to dev-auth services; production
  reads the JWT `email` and `full_name` claims. Older rows are backfilled by the demo seed.
- `GET/POST /api/email-templates`, `PUT/DELETE /api/email-templates/{id}` — recruiter only, own rows.
- `POST /api/applications/{id}/email` — subject/body (`{{candidate_name}}`, `{{job_title}}`,
  `{{recruiter_name}}` resolved server-side), delivered through notif-svc's
  `POST /api/internal/email`. A delivery failure returns 502 and sends nothing.
- `GET/POST /api/interviews`, `GET/PUT /api/interviews/{id}`, `POST /api/interviews/{id}/cancel`,
  `GET /api/interviews/{id}/ics` — recruiters schedule/reschedule/cancel for jobs their company owns;
  participants (candidate, recruiter, admin) read and download. The `.ics` is built in app-svc
  (`IcsBuilder`, RFC 5545, UTC, folded at 75 octets) and mailed as a `text/calendar` attachment.
  Invites are best-effort: the interview is saved first, and the response carries `inviteSent`.
- Configuration: `NOTIF_SERVICE_URL`, `NOTIF_INTERNAL_TOKEN` (shared with notif-svc). Missing values
  disable sending; interviews still save.
