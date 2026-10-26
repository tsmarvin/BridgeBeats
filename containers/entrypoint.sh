#!/bin/bash
set -eu

# ---- Helper function to read Docker secrets ----
read_secret() {
    secret_path="/run/secrets/$1"
    if [ -f "$secret_path" ]; then
      # Strip UTF-8 BOM (EF BB BF) and all newlines/carriage returns to prevent
      # JSON/config corruption. All secrets in this app are single-line ASCII values.
      sed 's/^\xEF\xBB\xBF//' < "$secret_path" | tr -d '\r\n'
    else
      echo ""
    fi
}

# ---- Configure .NET environment (Production if not set) ----
export DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Production}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-$DOTNET_ENVIRONMENT}"

# ---- Configure Aspire Dashboard ----
# Allow unsecured transport since we use HTTP behind Caddy reverse proxy (Caddy handles HTTPS)
export ASPIRE_ALLOW_UNSECURED_TRANSPORT="true"
# Dashboard auth is handled by Caddy forward_auth, so we disable all dashboard auth
# MCP server is disabled as we don't need AI tooling features
# OTLP endpoint is required by Aspire for dashboard initialization (internal only)
export ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL="http://localhost:18889"
export ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS="true"
export DASHBOARD__MCP__DISABLED="true"
# Aspire 13.4+ persists local state (IAspireStore) under the AppHost project's
# build-time intermediate output path (/build), which does not exist at runtime.
# Redirect it to the writable persistent data directory (handled in AppHost Program.cs).
export ASPIRE_STORE_PATH="/app/data"

# ---- Web app port configuration ----
# Use explicit WEB_PORT if set, default to 10000 for production.
# Do not use ASPNETCORE_HTTP_PORTS as the base image sets it to 8080.
WEB_PORT="${WEB_PORT:-10000}"

# ---- Configure defaults ----
DOMAIN="${DOMAIN:-bridgebeats.link}"
NODE_NUMBER="${NODE_NUMBER:-0}"
DEFAULT_LOGLEVEL="${DEFAULT_LOGLEVEL:-Information}"
HOSTING_DEFAULT_LOGLEVEL="${HOSTING_DEFAULT_LOGLEVEL:-Information}"

# External Aspire dashboard URL for subdomain-based access
export DASHBOARD__FRONTEND__PUBLICURL="https://dashboard.${DOMAIN}"

# Optional music provider credentials
# Read from Docker secrets if available, otherwise use environment variables
APPLE_TEAM_ID="${APPLE_TEAM_ID:-}"
APPLE_KEY_ID="${APPLE_KEY_ID:-}"
APPLE_KEY_PATH="${APPLE_KEY_PATH:-}"

# Try to read Spotify client secret from Docker secret
SPOTIFY_CLIENT_ID="${SPOTIFY_CLIENT_ID:-}"
SPOTIFY_CLIENT_SECRET="$(read_secret "spotify_client_secret")"

# Try to read Tidal client secret from Docker secret
TIDAL_CLIENT_ID="${TIDAL_CLIENT_ID:-}"
TIDAL_CLIENT_SECRET="$(read_secret "tidal_client_secret")"

# Try to read Discord token from Docker secret
DISCORD_TOKEN="$(read_secret "discord_token")"

# Optional ATProto PDS configuration
ATPROTO_IDENTIFIER="${ATPROTO_IDENTIFIER:-}"
ATPROTO_USER_DID="${ATPROTO_USER_DID:-}"
# Try to read ATProto password from Docker secret
ATPROTO_PASSWORD="$(read_secret "atproto_password")"
ATPROTO_PDS_URI="${ATPROTO_PDS_URI:-https://pds.bridgebeats.link}"

# ATProto OAuth signing key path (file-based, read directly by the app)
ATPROTO_OAUTH_KEY_PATH="/run/secrets/atproto_oauth_key"

# Redis configuration
REDIS_HOST="${REDIS_HOST:-redis}"
REDIS_PORT="${REDIS_PORT:-6379}"
# Try to read Redis password from Docker secret
REDIS_PASSWORD="$(read_secret "redis_password")"

CACHE_DAYS="${CACHE_DAYS:-7}"
CARD_CACHE_EXPIRATION_HOURS="${CARD_CACHE_EXPIRATION_HOURS:-1}"
CARD_CACHE_CLEANUP_INTERVAL="${CARD_CACHE_CLEANUP_INTERVAL:-500}"
CARD_CACHE_MAX_ENTRIES="${CARD_CACHE_MAX_ENTRIES:-25000}"
REFRESH_INTERVAL_HOURS="${REFRESH_INTERVAL_HOURS:-24}"
MAX_RECORDS_PER_RUN="${MAX_RECORDS_PER_RUN:-100}"

# Authentication and rate limiting configuration
IDENTITY_CONNECTION_STRING="${IDENTITY_CONNECTION_STRING:-Data Source=/app/data/bridgebeats.db}"
# Try to read API key salt from Docker secret
API_KEY_SALT="$(read_secret "api_key_salt")"
# Internal service key for worker-to-API authentication (must persist across restarts)
# Read from environment variable or Docker secret (required)
INTERNAL_SERVICE_KEY="${INTERNAL_SERVICE_KEY:-}"
if [ -z "$INTERNAL_SERVICE_KEY" ]; then
    INTERNAL_SERVICE_KEY="$(read_secret "internal_service_key")"
fi
if [ -z "$INTERNAL_SERVICE_KEY" ]; then
    echo "ERROR: INTERNAL_SERVICE_KEY is not set. Configure it via environment variable or Docker secret 'internal_service_key'." >&2
    exit 1
fi
RATE_LIMIT_REQUESTS_PER_HOUR="${RATE_LIMIT_REQUESTS_PER_HOUR:-20}"
OAUTH_TRUSTED_PROXY_HOST="${OAUTH_TRUSTED_PROXY_HOST:-bridgebeats-caddy}"

# Logging configuration
LOG_DIR_PATH="${LOG_DIR_PATH:-/app/data/logs}"

# Data Protection key persistence
DATA_PROTECTION_KEY_PATH="${DATA_PROTECTION_KEY_PATH:-/app/keys}"

# Resilience configuration
RESILIENCE_MAX_RETRY_AFTER_SECONDS="${RESILIENCE_MAX_RETRY_AFTER_SECONDS:-120}"
RESILIENCE_MAX_RETRY_ATTEMPTS="${RESILIENCE_MAX_RETRY_ATTEMPTS:-5}"
RESILIENCE_TOTAL_TIMEOUT_MINUTES="${RESILIENCE_TOTAL_TIMEOUT_MINUTES:-10}"
RESILIENCE_ATTEMPT_TIMEOUT_SECONDS="${RESILIENCE_ATTEMPT_TIMEOUT_SECONDS:-120}"

# escape backslashes (for path safety) ----
escape_bs() { printf '%s' "$1" | sed 's/\\/\\\\/g'; }

# 0) Create required directories
mkdir -p "$LOG_DIR_PATH"
mkdir -p "$DATA_PROTECTION_KEY_PATH"

# 1) Remove existing appsettings.json from /src/BridgeBeats.Web/ if present
[ -f /src/BridgeBeats.Web/appsettings.json ] && rm -f /src/BridgeBeats.Web/appsettings.json

# 2) Build Redis connection string for use in appsettings.json and environment variables
if [ -n "$REDIS_PASSWORD" ]; then
    REDIS_CONNECTION_STRING="${REDIS_HOST}:${REDIS_PORT},password=${REDIS_PASSWORD}"
else
    REDIS_CONNECTION_STRING="${REDIS_HOST}:${REDIS_PORT}"
fi

# 3) Create new appsettings.json
cat > /src/BridgeBeats.Web/appsettings.json <<EOF
{
  "ConnectionStrings": {
    "redis": "$REDIS_CONNECTION_STRING"
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:$WEB_PORT"
      }
    }
  },
  "BridgeBeats": {
    "NodeNumber": $NODE_NUMBER,
    "AppleTeamId": "$APPLE_TEAM_ID",
    "AppleKeyId": "$APPLE_KEY_ID",
    "AppleKeyPath": "$(escape_bs "$APPLE_KEY_PATH")",
    "SpotifyClientId": "$SPOTIFY_CLIENT_ID",
    "SpotifyClientSecret": "$SPOTIFY_CLIENT_SECRET",
    "TidalClientId": "$TIDAL_CLIENT_ID",
    "TidalClientSecret": "$TIDAL_CLIENT_SECRET",
    "DiscordToken": "$DISCORD_TOKEN",
    "IdentityConnectionString": "$(escape_bs "$IDENTITY_CONNECTION_STRING")",
    "ApiKeySalt": "$API_KEY_SALT",
    "InternalServiceKey": "$INTERNAL_SERVICE_KEY",
    "RateLimitRequestsPerHour": $RATE_LIMIT_REQUESTS_PER_HOUR,
    "OAuthTrustedProxyHost": "$OAUTH_TRUSTED_PROXY_HOST",
    "ATProtoIdentifier": "$ATPROTO_IDENTIFIER",
    "ATProtoUserDID": "$ATPROTO_USER_DID",
    "ATProtoPassword": "$ATPROTO_PASSWORD",
    "CacheDays": $CACHE_DAYS,
    "Domain": "$DOMAIN",
    "ATProtoOAuthSigningKeyPath": "$ATPROTO_OAUTH_KEY_PATH",
    "LogDirPath": "$(escape_bs "$LOG_DIR_PATH")",
    "DataProtectionKeyPath": "$(escape_bs "$DATA_PROTECTION_KEY_PATH")",
    "CardCacheExpirationHours": $CARD_CACHE_EXPIRATION_HOURS,
    "CardCacheCleanupInterval": $CARD_CACHE_CLEANUP_INTERVAL,
    "CardCacheMaxEntries": $CARD_CACHE_MAX_ENTRIES,
    "Resilience": {
      "MaxRetryAfterSeconds": $RESILIENCE_MAX_RETRY_AFTER_SECONDS,
      "MaxRetryAttempts": $RESILIENCE_MAX_RETRY_ATTEMPTS,
      "TotalTimeoutMinutes": $RESILIENCE_TOTAL_TIMEOUT_MINUTES,
      "AttemptTimeoutSeconds": $RESILIENCE_ATTEMPT_TIMEOUT_SECONDS
    },
    "Workers": {
      "UseWorkerServices": true,
      "SpotifyWorkerEnabled": $( [ -n "$SPOTIFY_CLIENT_ID" ] && [ -n "$SPOTIFY_CLIENT_SECRET" ] && echo "true" || echo "false" ),
      "AppleMusicWorkerEnabled": $( [ -n "$APPLE_TEAM_ID" ] && [ -n "$APPLE_KEY_ID" ] && [ -n "$APPLE_KEY_PATH" ] && echo "true" || echo "false" ),
      "TidalWorkerEnabled": $( [ -n "$TIDAL_CLIENT_ID" ] && [ -n "$TIDAL_CLIENT_SECRET" ] && echo "true" || echo "false" )
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "$DEFAULT_LOGLEVEL",
      "Microsoft.Hosting.Lifetime": "$HOSTING_DEFAULT_LOGLEVEL",
      "Microsoft.AspNetCore.Hosting.Diagnostics": "Warning",
      "Microsoft.AspNetCore.Routing.EndpointMiddleware": "Warning"
    }
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "$DEFAULT_LOGLEVEL",
      "Override": {
        "Microsoft.AspNetCore.Hosting.Diagnostics": "Warning",
        "Microsoft.AspNetCore.Routing.EndpointMiddleware": "Warning"
      }
    }
  },
  "AllowedHosts": "*"
}
EOF

# 4) Set up environment variables for Aspire AppHost parameters
# These are read by the AppHost to configure workers
export Parameters__SpotifyClientId="$SPOTIFY_CLIENT_ID"
export Parameters__SpotifyClientSecret="$SPOTIFY_CLIENT_SECRET"
export Parameters__AppleTeamId="$APPLE_TEAM_ID"
export Parameters__AppleKeyId="$APPLE_KEY_ID"
export Parameters__AppleKeyPath="$APPLE_KEY_PATH"
export Parameters__TidalClientId="$TIDAL_CLIENT_ID"
export Parameters__TidalClientSecret="$TIDAL_CLIENT_SECRET"
export Parameters__DiscordToken="$DISCORD_TOKEN"
export Parameters__ATProtoIdentifier="$ATPROTO_IDENTIFIER"
export Parameters__ATProtoPassword="$ATPROTO_PASSWORD"
export Parameters__ATProtoUserDID="$ATPROTO_USER_DID"
export Parameters__ATProtoPdsUri="$ATPROTO_PDS_URI"
export Parameters__ATProtoOAuthSigningKeyPath="$ATPROTO_OAUTH_KEY_PATH"
export Parameters__ApiKeySalt="$API_KEY_SALT"
export Parameters__InternalServiceKey="$INTERNAL_SERVICE_KEY"

# Export Redis connection string for both AppHost parameters and direct service consumption
export Parameters__RedisConnectionString="$REDIS_CONNECTION_STRING"
export ConnectionStrings__redis="$REDIS_CONNECTION_STRING"
export Parameters__NodeNumber="$NODE_NUMBER"
export Parameters__Domain="$DOMAIN"
export Parameters__RateLimitRequestsPerHour="$RATE_LIMIT_REQUESTS_PER_HOUR"
export Parameters__OAuthTrustedProxyHost="$OAUTH_TRUSTED_PROXY_HOST"
export Parameters__CacheDays="$CACHE_DAYS"
export Parameters__RefreshIntervalHours="$REFRESH_INTERVAL_HOURS"
export Parameters__MaxRecordsPerRun="$MAX_RECORDS_PER_RUN"
export Parameters__IdentityConnectionString="$IDENTITY_CONNECTION_STRING"
export Parameters__LogDirPath="$LOG_DIR_PATH"
export Parameters__DataProtectionKeyPath="$DATA_PROTECTION_KEY_PATH"
export Parameters__CardCacheExpirationHours="$CARD_CACHE_EXPIRATION_HOURS"
export Parameters__CardCacheCleanupInterval="$CARD_CACHE_CLEANUP_INTERVAL"
export Parameters__CardCacheMaxEntries="$CARD_CACHE_MAX_ENTRIES"
export Parameters__ResilienceMaxRetryAfterSeconds="$RESILIENCE_MAX_RETRY_AFTER_SECONDS"
export Parameters__ResilienceMaxRetryAttempts="$RESILIENCE_MAX_RETRY_ATTEMPTS"
export Parameters__ResilienceTotalTimeoutMinutes="$RESILIENCE_TOTAL_TIMEOUT_MINUTES"
export Parameters__ResilienceAttemptTimeoutSeconds="$RESILIENCE_ATTEMPT_TIMEOUT_SECONDS"

# 5) Configure Aspire DCP paths (required for containerized deployment)
export DcpPublisher__CliPath="/app/tools/dcp/dcp"
# Aspire 13.6 packages the dashboard as a native executable without a managed DLL or runtimeconfig.
export DcpPublisher__DashboardPath="/app/tools/dashboard/Aspire.Dashboard"

# 6) Launch the BridgeBeats AppHost (orchestrator)
echo "Starting BridgeBeats AppHost (Environment: $DOTNET_ENVIRONMENT)..."
exec /src/BridgeBeats.AppHost/BridgeBeats.AppHost
