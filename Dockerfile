# syntax=docker/dockerfile:1

# ---- source: SDK image with the full solution and a warm NuGet cache ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS source
RUN useradd --create-home --uid 10001 appuser
WORKDIR /workspace
COPY --chown=appuser:appuser . .
RUN chown appuser:appuser /workspace
USER appuser
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
RUN dotnet restore LinkForge.sln

# ---- test: `docker compose --profile test run --rm test` ----
FROM source AS test
ENTRYPOINT ["dotnet", "test", "LinkForge.sln", "--nologo"]

# ---- publish ----
FROM source AS publish
RUN dotnet publish source/LinkForge.Service/LinkForge.Service.csproj -c Release -o /home/appuser/publish --no-restore

# ---- runtime ----
# The SDK image is intentional: the change validator runs `dotnet build` and `dotnet test` against an
# isolated copy of /workspace, so the runtime needs the SDK. Do not trim this to the aspnet runtime image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS final
RUN useradd --create-home --uid 10001 appuser && mkdir -p /app /data && chown -R appuser:appuser /app /data
WORKDIR /app
COPY --from=publish --chown=appuser:appuser /home/appuser/publish ./
COPY --from=source --chown=appuser:appuser /workspace /workspace
COPY --from=source --chown=appuser:appuser /home/appuser/.nuget /home/appuser/.nuget
USER appuser
ENV ASPNETCORE_URLS=http://+:8080 \
    DATA_DIRECTORY=/data \
    Workspace__SourceRoot=/workspace \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "LinkForge.Service.dll"]
