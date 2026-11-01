# job-platform-app-svc
.NET Web API App — part of **Vietnam Job Platform** (`pbl6`) under [`dut-pbl6-2026`](https://github.com/dut-pbl6-2026).
- Tech: .NET Web API App
- Branch flow: `feature/* → main` (see job-platform-docs/.github/git-strategy.md)
- Jira PBL6 skid.atlassian.net, Master plan docs/master-plan.md
- TM: TM1 Hoai, TM2 Thanh, TM3 Chi Bao, TM4 Khoa

## Health, logging & code quality
- `GET /health` - static liveness (`{"status":"ok","service":"..."}`).
- `GET /health/ready` - readiness with short-timeout dependency checks: database (503 when unreachable), Kafka (degraded, only when `KAFKA_BOOTSTRAP_SERVERS` is set).
- Logging: Serilog, compact JSON on the console outside Development, request logging, `Service` + `CorrelationId` properties. `X-Correlation-Id` is reused if sent (and well-formed), otherwise generated, and always echoed in the response. Headers and bodies are never logged.
- SonarCloud (optional): CI runs `dotnet-sonarscanner` only when the `SONAR_TOKEN` secret is available (skipped on fork PRs). Optional repo variables `SONAR_ORGANIZATION` (default `dut-pbl6-2026`) and `SONAR_PROJECT_KEY` (default `dut-pbl6-2026_job-platform-app-svc`).
