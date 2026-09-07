# AGENTS.md - Application Service Guidelines

## Architecture - Clean Api / Core / Infrastructure

```
src/App.Api              Web API (Program.cs, Endpoints, DevAuthMiddleware, /health)
src/App.Core             Domain (Application, StatusHistory, ApplicationStatus, DTOs)
src/App.Infrastructure   Data (AppDbContext, Migrations) + LocalCvStorageService
tests/App.Tests          xUnit integration & unit tests
AppService.sln           mise run build/test
```

Dependency: `Api -> Infrastructure -> Core -> SharedKernel` (`PackageReference JobPlatform.SharedKernel 0.1.0` via `local-feed`).

## SRS Mapping (APP-01)
- `POST /api/applications` - Candidate applies with CV upload (multipart/form-data: job_id, cover_letter, cv_file). Prevents duplicate application per (job_id, applicant_id) -> 409 Conflict.
- `GET /api/applications/me` - Candidate views their application history (paginated, filterable by status).
- `GET /api/applications/{id}` - View application details and complete status transition audit history.
- `GET /api/applications/job/{jobId}` - Recruiter views applications submitted for their job.
- `PUT /api/applications/{id}/status` - Recruiter updates application status (pending, reviewed, shortlisted, accepted, rejected) and appends to status_history.
- `GET /api/applications/cv/{fileName}` - Secure file download/streaming for CV files.
- `GET /health` - Service healthcheck returning `{"status":"ok","service":"application"}`.

## Database (app_db)
- Database schema: `applications`, `status_history`
- Unique index: `IX_applications_job_applicant_unique` on `(JobId, ApplicantId)`
- Port: `5004` (gateway upstream `http://localhost:5004`)
- Env vars: `DATABASE_URL_APP`, `APP_PORT`, `APP_CV_STORAGE_PATH`
