FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/App.Core/App.Core.csproj src/App.Core/
COPY src/App.Infrastructure/App.Infrastructure.csproj src/App.Infrastructure/
COPY src/App.Api/App.Api.csproj src/App.Api/
COPY nuget.config ./
COPY local-feed/ local-feed/
RUN dotnet restore src/App.Api/App.Api.csproj
COPY . .
RUN dotnet publish src/App.Api/App.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5004
ENV ASPNETCORE_URLS=http://+:5004
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
# CV storage (APP_CV_STORAGE_PATH, a named volume in compose). Docker seeds an empty named volume with
# this directory's ownership, so it must exist and belong to the non-root app user; otherwise the
# volume is root-owned and every upload fails with "Permission denied".
RUN mkdir -p /data/cv && chown app:app /data/cv
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:5004/health || exit 1
ENTRYPOINT ["dotnet", "App.Api.dll"]
