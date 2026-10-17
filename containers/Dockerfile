# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS base


# This stage is used to build and publish all projects
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
ARG TARGETARCH
WORKDIR /build

# Copy source files (projects directly in /build/) and build scripts (containers/ as sibling)
COPY src/ /build/
COPY containers/ /build/containers/

# Build and publish all projects using the standardized script
# The script handles lock file regeneration and validation for platform-specific packages
RUN DOTNET_RID="linux-x64"; \
    if [ "$TARGETARCH" = "arm64" ]; then DOTNET_RID="linux-arm64"; fi; \
    pwsh /build/containers/Build-Publish.ps1 -Runtime $DOTNET_RID

# Copy Aspire DCP and Dashboard tools from NuGet cache
RUN DOTNET_RID="linux-x64"; \
    if [ "$TARGETARCH" = "arm64" ]; then DOTNET_RID="linux-arm64"; fi; \
    mkdir -p /build/publish/tools/dcp && \
    mkdir -p /build/publish/tools/dashboard && \
    DCP_PACKAGE_DIR=$(ls -d /root/.nuget/packages/aspire.hosting.orchestration.${DOTNET_RID}/* | sort -V | tail -n 1) && \
    DASHBOARD_PACKAGE_DIR=$(ls -d /root/.nuget/packages/aspire.dashboard.sdk.${DOTNET_RID}/* | sort -V | tail -n 1) && \
    cp -r "${DCP_PACKAGE_DIR}/tools"/* /build/publish/tools/dcp/ && \
    cp -r "${DASHBOARD_PACKAGE_DIR}/tools"/* /build/publish/tools/dashboard/ && \
    chmod +x /build/publish/tools/dcp/dcp /build/publish/tools/dashboard/Aspire.Dashboard


# Final runtime stage
FROM base AS final

# Install curl for healthcheck (as root, before switching to app user)
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*

# Create necessary directories with proper permissions (as root, before switching to app user)
RUN mkdir -p /app/data && \
    mkdir -p /app/keys && \
    mkdir -p /src

# Copy published application (publish/src/* -> /src/*, publish/entrypoint.sh -> /entrypoint.sh)
COPY --from=build /build/publish/src /src
COPY --from=build /build/publish/entrypoint.sh /entrypoint.sh

# Copy Aspire DCP and Dashboard tools
COPY --from=build /build/publish/tools/dcp /app/tools/dcp
COPY --from=build /build/publish/tools/dashboard /app/tools/dashboard

RUN chown -R $APP_UID:$APP_UID /src && \
    chown -R $APP_UID:$APP_UID /app/ && \
    chown $APP_UID:$APP_UID /entrypoint.sh && \
    chmod +x /entrypoint.sh && \
    chmod +x /app/tools/dcp/dcp /app/tools/dashboard/Aspire.Dashboard

USER $APP_UID

# Aspire Dashboard configuration
# Dashboard binds to all interfaces on 18888 for reverse proxy access
# ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS disables all dashboard auth (Frontend, OTLP, MCP)
# Auth is handled externally by Caddy forward_auth in production
ENV ASPNETCORE_URLS=http://+:18888
ENV ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true
ENV DASHBOARD__MCP__DISABLED=true

# Expose ports
# 10000: BridgeBeats main web application
# 18888: Aspire Dashboard web UI
EXPOSE 10000
EXPOSE 18888

# Set the AppHost as the entrypoint via the startup script
ENTRYPOINT ["/entrypoint.sh"]
