# Local Development with Aspire

BridgeBeats uses [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) for local development orchestration, providing a dashboard with distributed tracing, structured logging, and resource monitoring.

## Prerequisites

- .NET 10.0 SDK or later
- Docker Desktop (for running Redis locally)
- API credentials configured (see [Configuration](CONFIGURATION.md))
- A running Redis instance reachable from your machine (see [Provide Redis](#provide-redis) below)

## Provide Redis

The AppHost adds Redis as a **connection-string resource only** (`AddConnectionString("redis")`). Aspire does not start Redis for you, in development or in production. You must run Redis yourself and tell the AppHost where to find it through the `redis` connection string. If the connection string is missing, the AppHost writes an error to stderr and the components that depend on Redis will fail.

Start a local Redis with Docker:

```bash
docker run -d --name bridgebeats-redis -p 6379:6379 redis:8-alpine
```

Then point the AppHost at it with user secrets (recommended) or an environment variable:

```bash
# User secrets (scoped to the AppHost project)
dotnet user-secrets set "ConnectionStrings:redis" "localhost:6379" \
  --project src/BridgeBeats.AppHost/BridgeBeats.AppHost.csproj

# Or an environment variable for the current shell
export ConnectionStrings__redis="localhost:6379"
```

> Verify: the connection-string key the AppHost reads is `ConnectionStrings:redis` (env-var form `ConnectionStrings__redis`), confirmed in `src/BridgeBeats.AppHost/Program.cs`. The exact local Redis host/port is your choice; `localhost:6379` matches the Docker command above.

## Quick Start

From the repository root, with Redis running and its connection string set:

```bash
aspire run
```

The terminal displays a **Dashboard URL with an authentication token**:

```
Dashboard:  https://localhost:17239/login?t=abc123tokenhere
```

Open this URL to access the Aspire Dashboard. The token is part of the URL; copy the whole line.

If you prefer to run the host directly without the Aspire CLI, use `dotnet run` from the AppHost project. You still get the orchestrated processes, but `aspire run` is the recommended path for the dashboard experience.

## Aspire Dashboard Features

The dashboard provides:

- **Resources view**: monitor each application component and its health status
- **Structured logs**: view application logs with filtering and search
- **Distributed traces**: trace requests across services
- **Metrics**: view application performance metrics

## Project Structure

The Aspire integration consists of:

| Project | Purpose |
|---------|---------|
| `BridgeBeats.AppHost` | Orchestration entry point - declares every component and its dependencies |
| `BridgeBeats.ServiceDefaults` | Shared service configuration (OpenTelemetry, resilience, health checks) |

The AppHost provisions the web application plus the provider workers (Spotify, Apple Music, Tidal), the Discord worker, the saga coordinator, the JetStream watcher, and the cache bootstrap worker. A provider worker is added only when its credentials are configured. All components share the single Redis resource.

## Environment Variables

The AppHost threads configuration and secrets through as Aspire parameters using the `BridgeBeats__Section__Key` (double-underscore) convention. Provide them through any of:

1. **User Secrets** (recommended for development):

   ```bash
   dotnet user-secrets set "Parameters:SpotifyClientId" "your-client-id" \
     --project src/BridgeBeats.AppHost/BridgeBeats.AppHost.csproj
   ```

   The AppHost reads provider credentials from the `Parameters:` configuration section (env-var form `Parameters__SpotifyClientId`).

2. **Environment variables**: set in your shell or IDE launch configuration.

3. **appsettings.json**: for non-sensitive configuration only.

## Aspire vs Production Docker Compose

| Aspect | Aspire (`aspire run`) | Docker Compose (`docker compose up`) |
|--------|----------------------|--------------------------------------|
| **Purpose** | Local development and debugging | Production deployment |
| **Dashboard** | Built-in with full telemetry | Aspire Dashboard fronted by Caddy |
| **Hot reload** | Supported (project resources) | Requires rebuild |
| **Reverse proxy** | Not included | Caddy with SSL/TLS |
| **Redis** | You run it; set the connection string | Run by the compose file |
| **Secrets** | Environment / user secrets | Docker secrets |

### When to Use Each

- **Use `aspire run`** for:
  - Day-to-day development
  - Debugging and testing
  - Exploring telemetry and logs

- **Use `docker compose up`** for:
  - Production deployments
  - CI/CD pipeline testing
  - Replicating the production environment locally

When using Docker Compose, change to the `containers/` directory first:

```bash
cd containers
docker compose up -d
```

In the compose topology, Redis runs as its own service, so the manual Redis step above does not apply.

## Generating Deployment Artifacts

Aspire can generate Docker Compose files for deployment prototyping:

```bash
aspire publish
```

> Verify: the output location for `aspire publish` is set by the Aspire CLI, not by the AppHost code. Check the command output for the generated path before relying on it.

> **Note**: Generated compose files are for development and prototyping. The production `docker-compose.yml` in `containers/` is the supported deployment, with Caddy, health checks, persistent volumes, and Docker-secret management.

## Manual PDS Cache Scale Test

`LivePdsCacheBootstrapScaleTests` downloads one real repository CAR, runs the production cache
bootstrap indexing path into an isolated Redis Testcontainer, and then measures rate-limit admission
latency against the populated keyspace. It never connects to production Redis and is guarded by an
explicit run switch, so normal test runs skip it even when the PDS variables are configured.

From the repository root:

```bash
BRIDGEBEATS_RUN_MANUAL_CACHE_BOOTSTRAP=1 \
BRIDGEBEATS_TEST_PDS_URI=https://pds.bridgebeats.link \
BRIDGEBEATS_TEST_USER_DID=did:plc:replace-me \
dotnet test Tests/BridgeBeats.Tests.csproj \
  --filter "FullyQualifiedName~LivePdsCacheBootstrapScaleTests" \
  --logger "console;verbosity=detailed"
```

The test reports bootstrap duration, records, albums, tracks, successfully indexed records,
per-record errors, Redis key count, and admission latency for empty/active rate-limit indexes,
cache hits, and 32-way concurrent cold reads. Docker must be running. The CAR endpoint is public,
so the test requires a PDS URI and repository DID but no ATProto password.

## Troubleshooting

### Redis connection errors on startup

If components log Redis connection failures, confirm Redis is running and the `redis` connection string is set (see [Provide Redis](#provide-redis)). The AppHost does not start Redis for you.

### Port conflicts

If you see port-binding errors, ensure no other instance is already running. The Aspire CLI prompts when an instance is detected.

### Missing dependencies

Confirm the prerequisites are installed:

```bash
dotnet --version  # Should be 10.0+
docker --version  # Docker must be running
```

### Dashboard not loading

1. Use the full URL including the authentication token (`?t=...`).
2. Confirm the terminal shows the application started successfully.
3. Stop and restart with `aspire run`.

## Related Documentation

- [Configuration](CONFIGURATION.md) - API credentials and environment variables
- [Deployment](DEPLOYMENT.md) - production deployment guide
- [Quick Start](QUICKSTART.md) - Docker Compose onboarding
