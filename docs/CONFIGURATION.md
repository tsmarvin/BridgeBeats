# Configuration Guide

This guide covers how to configure BridgeBeats with API credentials and environment variables.

## Environment Variables

BridgeBeats requires API credentials for at least one music provider (Apple Music, Spotify, or Tidal). Discord and ATProto integrations are optional.

### Required Music Provider Credentials

At least one complete set of music provider credentials is required:

| Variable | Description | Required |
|----------|-------------|----------|
| `APPLE_TEAM_ID` | Your Apple Developer Team ID | No* |
| `APPLE_KEY_ID` | Your Apple Music API Key ID | No* |
| `APPLE_KEY_PATH` | Container path to the Apple Music private key (`.p8`); the compose file mounts the secret at `/run/secrets/apple_key` | No* |
| `SPOTIFY_CLIENT_ID` | Your Spotify API Client ID | No* |
| `SPOTIFY_CLIENT_SECRET` | Your Spotify API Client Secret (provided via the `spotify_client_secret` Docker secret) | No* |
| `TIDAL_CLIENT_ID` | Your Tidal API Client ID | No* |
| `TIDAL_CLIENT_SECRET` | Your Tidal API Client Secret (provided via the `tidal_client_secret` Docker secret) | No* |

\* At least one complete set of music provider credentials is required.

In the Docker topology, the public half of each provider's credentials (client IDs, Apple Team/Key IDs) is set as an environment variable in `.env`, and the secret half (client secrets, the Apple `.p8` key) is supplied as a Docker secret. The container entrypoint reads the secrets and assembles the application configuration.

### Required Service Credentials

| Variable | Description | Required |
|----------|-------------|----------|
| `INTERNAL_SERVICE_KEY` | Shared key worker services present to authenticate to the web API. Read from the environment or the `internal_service_key` Docker secret. **The container refuses to start when this is empty.** Must persist across restarts. | Yes |

The installation script auto-generates `internal_service_key.txt`. When configuring by hand, generate a value (for example `openssl rand -base64 32`) and provide it through the secret or the environment variable.

### Optional Integration Credentials

| Variable | Description | Required |
|----------|-------------|----------|
| `ATPROTO_IDENTIFIER` | ATProto account identifier (handle) | No* |
| `ATPROTO_PASSWORD` | ATProto app password (provided via the `atproto_password` Docker secret) | No* |
| `ATPROTO_USER_DID` | DID of the ATProto user whose records are read and written | No* |
| `ATPROTO_PDS_URI` | Base URI of the ATProto Personal Data Server. Default `https://pds.bridgebeats.link` | No |

\* Required only when using ATProto PDS storage for caching lookup results. See the [ATProto Lexicon Resolution Setup Guide](ATPROTO_LEXICON.md) for complete configuration instructions.

The ATProto OAuth signing key is supplied separately as the `atproto_oauth_key` Docker secret (an ES256 JWK). The container reads it from `/run/secrets/atproto_oauth_key`; the application setting is `ATProtoOAuthSigningKeyPath`. When the file is absent, empty, or `{}`, the ATProto OAuth service runs as a public client.

### Discord Worker Configuration

The Discord integration runs as a separate worker service (`BridgeBeats.Worker.Discord`). These settings apply when deploying the Discord worker:

| Variable | Description | Required |
|----------|-------------|----------|
| `DISCORD_TOKEN` | Your Discord bot token (provided via the `discord_token` Docker secret) | Yes** |
| `NODE_NUMBER` | Node number for Discord sharding | No (default: `0`) |
| `DOMAIN` | Base host the worker calls back to (for example `bridgebeats.link`) | Yes** |

\*\* Required only when deploying the Discord worker service.

### Optional Configuration

| Variable | Description | Default |
|----------|-------------|---------|
| `DEFAULT_LOGLEVEL` | Default logging level | `Information` |
| `HOSTING_DEFAULT_LOGLEVEL` | ASP.NET hosting logging level | `Information` |
| `LOG_DIR_PATH` | Directory for log files | `/app/data/logs` |
| `CACHE_DAYS` | Days to cache ATProto PDS lookup results | `7` |
| `REDIS_HOST` | Redis hostname | `redis` |
| `REDIS_PORT` | Redis port | `6379` |
| `IDENTITY_CONNECTION_STRING` | SQLite connection string for the identity database | `Data Source=/app/data/bridgebeats.db` |
| `RATE_LIMIT_REQUESTS_PER_HOUR` | Rate-limited lookup requests permitted per user per hour | `20` |
| `DOMAIN` | Base domain for the application (HTTPS certificates, cookie scope, OpenGraph card URLs) | `bridgebeats.link` (container default) |
| `DATA_PROTECTION_KEY_PATH` | Directory where ASP.NET Data Protection keys are persisted | `/app/keys` |
| `CARD_CACHE_EXPIRATION_HOURS` | Hours to cache OpenGraph cards in memory | `1` |
| `CARD_CACHE_CLEANUP_INTERVAL` | Operations between cleanup cycles for expired cards | `500` |
| `REFRESH_INTERVAL_HOURS` | Hours between stale-cache refresh sweeps | `24` |
| `MAX_RECORDS_PER_RUN` | Maximum stale records re-enqueued per refresh sweep | `100` |

> Note: `RATE_LIMIT_REQUESTS_PER_HOUR` defaults to `20` in the container entrypoint and application code. The shipped `.env.example` sets it to `100`; whichever value you place in `.env` wins for a Docker deployment.

`IDENTITY_CONNECTION_STRING` points at the SQLite identity database (`bridgebeats.db`), which stores user accounts and protected personal data. SQLite remains the identity store; Redis holds the media-link cache, request queue, and rate-limit tracker, not identity data.

`DATA_PROTECTION_KEY_PATH` must be a persistent location. The keys it holds decrypt Identity personal-data fields, so they must survive container restarts. In Docker this is a mounted volume (`./data/dp-keys:/app/keys`).

The container entrypoint reads `REDIS_HOST` and `REDIS_PORT` (plus the `redis_password` secret) and assembles the connection string the application consumes as `ConnectionStrings:redis`. There is no single `REDIS_CONNECTION_STRING` variable to set in the compose environment.

The container image fixes `AllowedHosts` to `*` and reads no `ALLOWED_HOSTS` environment variable; host filtering is Caddy's responsibility in this topology.

Job-queue tuning (`RateLimitRetryThreshold`, default `00:02:00`; `JobExpirationMinutes`, default `2880`) lives under the `BridgeBeats:Queue` configuration section. Set these through `appsettings.json` rather than the container environment. See the [Spotify Batch and Routing Guide](SPOTIFY_BATCH_AND_ROUTING.md) for the queue-tuning constants.

**Note**: Environment variables use double underscores (`__`) to denote nested configuration sections (for example, `BridgeBeats__Domain` maps to `BridgeBeats:Domain` in configuration). The container entrypoint accepts the flat aliases shown above (`DOMAIN`, `CACHE_DAYS`, and so on) and maps them to the nested keys.

`DOMAIN` must be a host-only value (no `http://`/`https://`, no path, and no port).

## Obtaining API Credentials

### Apple Music API Credentials

1. Visit the [Apple Developer Portal](https://developer.apple.com/account)
2. Follow the guide to [Create a Media Identifier and Private Key](https://developer.apple.com/help/account/configure-app-capabilities/create-a-media-identifier-and-private-key/)
3. Download your `.p8` private key file
4. Note your Team ID and Key ID

### Spotify API Credentials

1. Visit the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard)
2. Follow the guide to [Register Your App](https://developer.spotify.com/documentation/general/guides/app-settings/#register-your-app)
3. Note your Client ID and Client Secret

### Tidal API Credentials

1. Visit the [Tidal Developer Portal](https://developer.tidal.com/)
2. Create a new application to get API access
3. Note your Client ID and Client Secret

### Discord Bot Token

1. Visit the [Discord Developer Portal](https://discord.com/developers/applications)
2. Follow the [Getting Started Guide](https://discord.com/developers/docs/quick-start/getting-started)
3. Create a bot and copy its token
4. Invite the bot to your server with appropriate permissions (Read Messages, Send Messages, Embed Links, Manage Messages)

### ATProto PDS Credentials (Optional - for caching)

To store lookup results on an ATProto PDS for persistent caching:

1. Create an ATProto account at [bsky.app](https://bsky.app) if you do not have one
2. Go to Settings → App Passwords
3. Create a new app password for BridgeBeats
4. Use your handle (for example `yourname.bsky.social`) as `ATPROTO_IDENTIFIER`
5. Use the generated app password as the `atproto_password` secret

**Note**: Lookup results are stored as custom AT Protocol lexicon records on the PDS. Input links with tracking parameters are kept private in Redis (URL hashes only) and are never exposed publicly.

### Redis Configuration (Required)

Redis is used for the media-link cache, request queue, and rate-limit tracking. Redis is required in every environment; nothing provisions it for you.

- **Docker Compose**: the supplied `docker-compose.yml` runs Redis as the `redis` service, secured with the `redis_password` secret. No extra steps are needed.
- **Local development with Aspire**: the AppHost adds Redis as a connection-string resource only. Run Redis yourself and set the `redis` connection string. See the [Local Development Guide](LOCAL_DEVELOPMENT.md#provide-redis).
- **Production (non-compose)**: deploy a Redis instance, set `REDIS_HOST`/`REDIS_PORT`, and provide the `redis_password` secret. Enable persistence (RDB or AOF) for queue durability, and enable TLS and authentication.

**Note**: Redis stores lookup indices and queue messages. Cached `MediaLinkResult` data is stored on the ATProto PDS.

## Database-backed application settings

BridgeBeats has a typed application-settings service backed by the Identity SQLite database. The
service stores one versioned settings aggregate so validation, non-sensitive values, and secret
changes are committed atomically. Runtime adoption of this store is being introduced separately;
until that transition is complete, the existing configuration sources described below remain
authoritative at startup.

The aggregate separates settings into these classes:

| Class | Examples | Storage behavior |
|---|---|---|
| Non-sensitive | Provider identifiers, domain and PDS URI, rate limits, cache, queue, resilience, worker scheduling | Stored as typed JSON |
| Sensitive | Provider client secrets, Discord token, ATProto service-account app password, Apple private key, ATProto OAuth private signing JWK | Serialized separately and protected with ASP.NET Core Data Protection purpose `BridgeBeats.ApplicationSettings.Secrets.v1` before database persistence |
| Generated sensitive | API-key salt and internal-service key | Generated by setup workflows and stored in the protected secret document |
| Bootstrap/instance-only | Identity database connection/location, Data Protection key-ring path, and per-process Discord node number | Remains outside the settings database |

The database location and key-ring location are bootstrap-only because both are required before the
settings row can be opened and decrypted. The Discord node number remains external because it identifies
one process instance rather than the app-wide settings aggregate. In the current Aspire topology, the
Redis connection string also remains orchestration configuration supplied outside this store. Host-level settings such as
ASP.NET Core environment selection and OpenTelemetry export configuration remain normal platform
configuration rather than application settings.

Secret values are never returned by the administration service's redacted status contract. It exposes
only whether each secret is configured. A separate runtime-reader contract is available to trusted
composition code and returns secret values through wrappers whose string, debugger, and JSON display
paths remain redacted. Updates use explicit keep, replace, and remove operations, and require the
revision from the preceding read. A stale revision rejects the complete update; it never partially
changes settings.

The Data Protection key directory must be persistent, access-restricted, and excluded from source
control. Deleting or replacing the key ring makes existing protected settings unreadable. A decryption
failure instructs the operator not to overwrite the settings row and to restore or reconnect the original
key ring first. Malformed JSON, unsupported schema versions, and unsupported protection-scheme markers
produce a separate sanitized stored-data diagnostic. Neither path falls back to plaintext.

## Configuration Files

### appsettings.json

For local development, you can use an `appsettings.json` file instead of environment variables:

```json
{
  "ConnectionStrings": {
    "redis": "localhost:6379"
  },
  "BridgeBeats": {
    "NodeNumber": 100,
    "AppleTeamId": "your_team_id",
    "AppleKeyId": "your_key_id",
    "AppleKeyPath": "/path/to/AuthKey.p8",
    "SpotifyClientId": "your_client_id",
    "SpotifyClientSecret": "your_client_secret",
    "TidalClientId": "your_tidal_client_id",
    "TidalClientSecret": "your_tidal_client_secret",
    "DiscordToken": "your_bot_token",
    "IdentityConnectionString": "Data Source=bridgebeats.db",
    "ApiKeySalt": "your_api_key_salt",
    "InternalServiceKey": "your_internal_service_key",
    "RateLimitRequestsPerHour": 20,
    "ATProtoIdentifier": "your-handle.bsky.social",
    "ATProtoPassword": "your-app-password",
    "ATProtoUserDID": "",
    "ATProtoPdsUri": "https://pds.bridgebeats.link",
    "ATProtoOAuthSigningKeyPath": "/path/to/atproto_oauth_key.json",
    "CacheDays": 7,
    "Domain": "localhost",
    "LogDirPath": "./logs",
    "DataProtectionKeyPath": "./keys",
    "CardCacheExpirationHours": 1,
    "CardCacheCleanupInterval": 500,
    "Resilience": {
      "MaxRetryAfterSeconds": 120,
      "MaxRetryAttempts": 5,
      "TotalTimeoutMinutes": 10,
      "AttemptTimeoutSeconds": 120
    },
    "Workers": {
      "UseWorkerServices": true,
      "SpotifyWorkerEnabled": true,
      "AppleMusicWorkerEnabled": false,
      "TidalWorkerEnabled": false
    },
    "Queue": {
      "RateLimitRetryThreshold": "00:02:00",
      "JobExpirationMinutes": 2880
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "AllowedHosts": "*"
}
```

### Worker Settings

The `BridgeBeats:Workers` block controls how provider lookups are dispatched:

| Key | Default | Meaning |
|---|---|---|
| `UseWorkerServices` | `false` in code; `true` in the container | When `true`, the web host calls remote provider workers over HTTP. When `false`, provider lookups run in-process from the configured credentials. |
| `SpotifyWorkerEnabled` | `false` | Enables the Spotify worker when `UseWorkerServices` is set. |
| `AppleMusicWorkerEnabled` | `false` | Enables the Apple Music worker when `UseWorkerServices` is set. |
| `TidalWorkerEnabled` | `false` | Enables the Tidal worker when `UseWorkerServices` is set. |

In the container, the entrypoint sets `UseWorkerServices` to `true` and turns each provider flag on only when that provider's credentials are present.

### HTTP Resilience

The `BridgeBeats:Resilience` block tunes the HTTP resilience pipeline applied to outbound HTTP clients: provider APIs, the Discord worker's calls to the web API, and CAR repo downloads.

| Key | Code default | Container default | Meaning |
|---|---|---|---|
| `AttemptTimeoutSeconds` | `10` | `120` | Per-attempt timeout. Keep it above the server-side lookup budget so slow but legitimate lookups are not killed mid-flight. The container sets `120`. |
| `TotalTimeoutMinutes` | `10` | `10` | Ceiling across all retry attempts for one logical request. Must exceed `AttemptTimeoutSeconds`. |
| `MaxRetryAttempts` | `5` | `5` | Retry count for transient failures on safe (GET) requests. POST is never retried automatically. |
| `MaxRetryAfterSeconds` | `120` | `120` | Largest `Retry-After` value honored before failing fast. |

The application code defaults `AttemptTimeoutSeconds` to `10`, but the container entrypoint overrides it to `120` (env var `RESILIENCE_ATTEMPT_TIMEOUT_SECONDS`). Use the container value as the operational default for Docker deployments.

### Docker Configuration

In the supported deployment, configuration is supplied through `.env` and Docker secrets, and the entrypoint assembles `appsettings.json` at container start. See the [Quick Start Guide](QUICKSTART.md) and [Deployment Guide](DEPLOYMENT.md) for the full workflow.

## Security Best Practices

1. **Never commit credentials to source control** - use environment variables or Docker secrets.
2. **Use app passwords for ATProto** - generate dedicated app passwords instead of your main account password.
3. **Keep `INTERNAL_SERVICE_KEY` and the Data Protection keys stable** - rotating them between restarts breaks worker authentication and prevents decryption of stored personal data.
4. **Rotate provider API keys periodically** and update the matching secrets.
5. **Restrict API key permissions** - grant only the minimum required for each service.
6. **Use HTTPS in production** - deploy behind Caddy with SSL/TLS enabled.

## Validation

BridgeBeats validates configuration at startup. The container aborts when `INTERNAL_SERVICE_KEY` is missing. Other missing or invalid credentials produce warnings and disable the dependent features rather than stopping startup.

Check the application logs on startup for configuration warnings:

```
[Warning] Apple Music credentials not configured - Apple Music lookups will be unavailable
[Warning] Spotify credentials not configured - Spotify lookups will be unavailable
[Information] Discord token not provided - Discord bot will not be started
```

## Logging Configuration

BridgeBeats writes logs to two destinations that work simultaneously.

### File Logging

Logs are written to persistent files with automatic rotation and retention:

- **Location**: the directory set by `LOG_DIR_PATH` (default `/app/data/logs`); files are named `bridgebeats-<date>.log`
- **Rotation**: daily rotation plus size-based rotation (50MB per file)
- **Retention**: a maximum of 5 log files per service (oldest files are deleted)
- **Total size**: up to roughly 250MB per service

File logging provides a local diagnostic backup when the Aspire Dashboard is unavailable.

### OpenTelemetry (OTLP) Logging

Logs are sent to the Aspire Dashboard for real-time observability over the OpenTelemetry Protocol (OTLP). In the container topology, the Aspire Dashboard is configured by the AppHost and fronted by Caddy; dashboard access is gated by Caddy forward-auth.

### Log Levels

Configure log verbosity with these environment variables:

- `DEFAULT_LOGLEVEL`: overall application log level (`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`)
- `HOSTING_DEFAULT_LOGLEVEL`: ASP.NET hosting infrastructure log level

Example in `.env`:

```bash
DEFAULT_LOGLEVEL=Debug
HOSTING_DEFAULT_LOGLEVEL=Information
LOG_DIR_PATH=/app/data/logs
```

### Accessing Logs

**File logs** (Docker):

```bash
# View logs from the persistent volume
docker compose exec bridgebeats cat /app/data/logs/bridgebeats-*.log

# Tail logs in real time
docker compose exec bridgebeats tail -f /app/data/logs/bridgebeats-*.log
```

Logs are also bind-mounted to `./logs` on the host by the compose file, so you can read them directly without entering the container.

**Aspire Dashboard logs**:

1. Local development: open the Aspire Dashboard URL printed by `aspire run`.
2. Production: open `https://dashboard.<your-domain>`.
3. Navigate to the Logs section to view real-time logs with filtering and search.

### Log Rotation Details

Log files are managed automatically:

1. **Daily rotation**: a new file is created each day (for example `bridgebeats-20260114.log`).
2. **Size-based rotation**: when a file reaches 50MB, a new file is created.
3. **Retention**: only the 5 most recent files are kept.
4. **Automatic cleanup**: old files are deleted when the retention limit is reached.

This keeps the container from accumulating excessive log data while preserving recent diagnostic history.
