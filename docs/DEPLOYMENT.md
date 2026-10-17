# Deployment Guide

This guide covers production deployment of BridgeBeats with Docker Compose.

> **Note**: For local development with the Aspire Dashboard, see the [Local Development Guide](LOCAL_DEVELOPMENT.md).

## Quick Start with Installation Script

The installation script is the supported deployment path. It downloads the configuration files, creates the directory layout with correct ownership, generates secrets, and registers the PDS maintenance cron job. You do not need to clone the repository.

### Linux / macOS

```bash
curl -sSL https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.sh | bash
```

### Windows (PowerShell)

```powershell
iwr -useb https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.ps1 | iex
```

**Reviewing the installer before running it.** The one-liners above download and execute a script in a single step. To inspect it first:

```bash
curl -fsSL -o install.sh https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.sh
less install.sh   # review
bash install.sh   # run after review
```

```powershell
iwr -useb https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.ps1 -OutFile install.ps1
# review install.ps1, then:
.\install.ps1
```

Piping straight to `bash`/`iex` runs unreviewed remote code as your user. Prefer the reviewed flow on hosts you do not control. If signed releases with published checksums become available, verify the checksum before executing.

### Installation Options

Customize the installation with parameters:

```bash
# Linux/macOS - specify branch and directory
curl -sSL https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.sh | bash -s -- --branch main --directory /opt/bridgebeats

# Windows - specify branch and directory
.\install.ps1 -Branch main -Directory C:\BridgeBeats
```

### What the Installation Script Does

1. **Validates dependencies** - Checks for Docker, Docker Compose v2+, curl/wget, and optionally openssl
2. **Downloads configuration files** - Fetches `docker-compose.yml`, `Caddyfile`, `.env.example`, and `truncate_seq.sh` from GitHub
3. **Sets up logs and data directories** - Creates `./logs/`, `./logs/caddy/`, and `./data/{app,dp-keys,redis,pds}/`; sets ownership for each directory to match the writing UID (`1654` for the app's `./data/app` and `./data/dp-keys`, `root` for `./data/pds`, `./data/redis`, and `./logs/caddy`)
4. **Sets up secrets** - Creates the `secrets/` directory. Auto-generates `api_key_salt.txt`, `redis_password.txt`, `internal_service_key.txt`, and the `atproto_oauth_key.json` ES256 signing key; writes placeholders for the credentials you supply (`apple_key.p8`, `spotify_client_secret.txt`, `tidal_client_secret.txt`, `discord_token.txt`, `atproto_password.txt`, `cloudflare_api_token.txt`)
5. **Configures environment** - Creates `.env` from `.env.example` if not present
6. **Registers PDS sequencer trim** - Installs `sqlite3` on the host if missing and writes `/etc/cron.d/bridgebeats-pds-trim` (daily at 04:17, 14-day retention); see [PDS Sequencer Retention](#pds-sequencer-retention) below
7. **Handles updates** - On subsequent runs:
   - Backs up existing `docker-compose.yml` and `Caddyfile` with timestamps
   - Backs up the `secrets/` directory (keeps the three most recent backups)
   - Logs current container image SHAs to `upgrade.log` for rollback reference
   - Detects new environment variables in `.env.example` and displays them for manual addition
   - Prompts to pull latest container images

After running the script, edit your secrets and `.env` file, then start with `docker compose up -d`.

---

## Manual Setup with Docker Compose

The installation script handles secret generation, directory ownership, and the PDS cron job for you. Set up manually only if you need to. The compose stack mounts each secret as a file under `./secrets/` and persists data under `./data/`, so a working deployment needs all secret files present, the data directories owned by the correct UIDs, and the PDS environment variables set in `.env`.

```bash
git clone https://github.com/tsmarvin/BridgeBeats.git
cd BridgeBeats/containers
cp .env.example .env
# Edit .env: set DOMAIN, CADDY_ADMIN_EMAIL, the PDS_* values, and at least one music provider
```

Create the `secrets/` directory and populate every file the compose stack mounts (see the `volumes:` block in `docker-compose.yml`). The script's secret generation is the reference for what each file holds; replicating it by hand is error-prone, which is why the script is the supported path.

Once secrets, `.env`, and the `./data/` ownership are in place:

```bash
docker compose up -d
```

Visit `https://localhost` (or your configured domain) to reach BridgeBeats. The Aspire Dashboard is served on a dedicated subdomain: `https://dashboard.<your-domain>`.

For step-by-step instructions, see the [Quick Start Guide](QUICKSTART.md).

## Docker Deployment

### Using Docker Compose (Recommended)

Docker Compose deployment includes:
- **Caddy reverse proxy** - Automatic HTTPS via the custom `caddy-cloudflare` image
- **Security headers** - HSTS, CSP, X-Content-Type-Options, Referrer-Policy, and more
- **Docker secret files** - Credentials mounted read-only from `./secrets/`
- **Health checks** - Automatic monitoring and restarts
- **Persistent volumes** - Application data, Redis persistence, PDS storage, and Caddy certificates

#### Architecture

The Docker Compose stack defines four services (see `containers/docker-compose.yml`):

1. **bridgebeats** - The .NET application container. Its entrypoint launches the Aspire AppHost orchestrator, which runs the Web app (port 10000) and the in-process worker services (provider workers, Discord, saga coordinator) together. Depends on `redis` being healthy.
2. **redis** (`redis:8-alpine`, container `bridgebeats-redis`) - Backs the media-link cache, the request queue (Redis Streams), the rate-limit tracker, and saga state. Runs with `appendonly yes`; password-protected when `redis_password.txt` is present.
3. **bridgebeats-pds** (`ghcr.io/bluesky-social/pds:latest`) - The ATProto Personal Data Server used to store lookup results.
4. **bridgebeats-caddy** (`tsmarvin/caddy-cloudflare`) - Reverse proxy terminating HTTPS on ports 80/443. See [Caddy + Cloudflare Guide](CADDY_CLOUDFLARE.md).

The `bridgebeats` and `redis` services join an `internal` bridge network. The Caddy service joins both `public` (ports 80/443) and `internal`.

#### Configuration

Edit `.env` to configure your deployment:

```bash
# Domain for HTTPS certificates (use your actual domain in production)
DOMAIN=yourdomain.com
CADDY_ADMIN_EMAIL=admin@yourdomain.com

# API credentials (at least one provider required)
APPLE_TEAM_ID=your_team_id
APPLE_KEY_ID=your_key_id
SPOTIFY_CLIENT_ID=your_client_id
TIDAL_CLIENT_ID=your_client_id
```

Sensitive values go in the `secrets/` directory (created by `install.sh`, or manually with `mkdir -p secrets && chmod 700 secrets`). See the [Configuration Guide](CONFIGURATION.md) for the full environment-variable and secret reference.

### Using Pre-built Images

Pull the application image from Docker Hub:

```bash
# main branch builds publish :latest; develop builds publish :develop
docker pull tsmarvin/bridgebeats:latest
```

The supported way to run the full stack is `docker compose up -d`, because the application depends on Redis and reads its credentials from mounted secret files. A bare `docker run` of `tsmarvin/bridgebeats` requires a reachable Redis instance and the secret mounts (including `internal_service_key`, which the entrypoint treats as required), so use the compose stack rather than a standalone container.

**Note**: Images are built for `linux/amd64` and `linux/arm64` platforms.

### SBOM and Provenance

The application and Caddy images carry Software Bill of Materials (SBOM) and provenance attestations generated at build time. These attestations let you:
- Trace an image back to the source repository and commit
- Inspect the software components and dependencies
- Scan for known vulnerabilities

See the [SBOM and Provenance Guide](SBOM_AND_PROVENANCE.md) for instructions on accessing and inspecting these attestations.

### Credentials and Secrets

The compose stack mounts credentials as read-only files under `/run/secrets/` from the host `./secrets/` directory. The entrypoint script reads each secret file (stripping any BOM and trailing newlines) and assembles the application's `appsettings.json` at startup. Provider workers are enabled only when both the client ID (from `.env`) and the matching client secret (from `./secrets/`) are present.

For Docker Swarm, the same files can be supplied as Swarm secrets:

```bash
# Deploy with Docker Swarm
docker stack deploy -c docker-compose.yml bridgebeats
```

## Environment Configuration

### Production Settings

Set `DOMAIN` in `.env` to your public domain. The container entrypoint reads it into `BridgeBeats:Domain`, which governs auth-cookie scoping and OpenGraph card URL generation. `SHORT_DOMAIN` (default `bbeats.link`) sets the short-link host served by Caddy.

> **Migration note:** The `bridgebeats` service environment variable was renamed from `BASEURL` to `DOMAIN`. Deployments that set `BASEURL` must rename it to `DOMAIN`.

```bash
DOMAIN=bridgebeats.link
```

### Logging

Set log levels for production:

```bash
DEFAULT_LOGLEVEL=Warning
HOSTING_DEFAULT_LOGLEVEL=Warning
```

## Monitoring

### Application Logs

BridgeBeats writes logs both to stdout/stderr and to the bind-mounted `./logs/` directory (Serilog file sink). Inspect container logs with:

```bash
docker logs -f bridgebeats
```

Or read the host-side log files directly under `./logs/`.

### Health Checks

The `bridgebeats` service exposes `/health`, which the compose health check and Caddy both poll. Check service health with:

```bash
docker compose ps
```

## Scaling

### Horizontal Scaling

BridgeBeats keeps its durable state in Redis (cache, request queue, rate-limit tracker, saga state) and SQLite (the Identity store) plus the ATProto PDS. The web tier itself holds no per-request session state beyond the Data Protection key ring under `./data/dp-keys/`. Running multiple application instances against shared Redis and a shared Identity store is possible, but it is not a configuration the current single-host compose stack sets up for you, and the in-process worker model (below) means each instance also runs its own workers.

> **Verify before relying on multi-instance scaling:** the shipped `docker-compose.yml` defines a single `bridgebeats` instance. A multi-instance topology (shared Redis, shared Identity DB, a single Discord shard owner, deduplicated workers) is not provided as a turnkey configuration. Confirm the worker and Discord-shard behavior against `src/BridgeBeats.AppHost/Program.cs` before scaling horizontally.

### Discord Bot Sharding

The Discord integration runs as a worker **inside** the application container, spawned by the Aspire AppHost when a Discord token is present (`src/BridgeBeats.AppHost/Program.cs`). It is not a separate container or a separate published image. The worker's shard identity comes from the `NODE_NUMBER` environment variable (`BridgeBeats__NodeNumber`).

If you run more than one BridgeBeats instance that connects to Discord, each must have a unique `NODE_NUMBER`. Two instances sharing a node number will produce duplicate responses to the same messages.

```bash
# .env for instance 1
NODE_NUMBER=0

# .env for instance 2
NODE_NUMBER=1
```

> **Verify:** multi-shard Discord operation depends on the node-number wiring in `src/BridgeBeats.AppHost/Program.cs` and the Discord worker. The single-host compose stack runs one instance (default `NODE_NUMBER=0`); confirm shard assignment before deploying multiple Discord-connected instances.

## Security Considerations

1. **Use HTTPS** - The Caddy service terminates TLS; do not expose the app container directly
2. **Secure secrets** - Keep `./secrets/` owned by the container UID and mode `600`; use Swarm/orchestrator secrets in production
3. **Rate limiting** - The app enforces a per-user hourly limit (`RATE_LIMIT_REQUESTS_PER_HOUR`); a reverse-proxy layer can add another
4. **API key rotation** - Regularly regenerate API keys
5. **Minimal permissions** - The app runs as non-root UID 1654

## Backup and Recovery

### Persistent Data

All persistent state lives under the host `./data/` bind-mount tree, alongside `./secrets/` and `.env`. Backup is a host-side file copy — no `docker cp` is needed.

Key paths:
- `./data/app/bridgebeats.db` — SQLite Identity store (users, API keys, ATProto OAuth state)
- `./data/dp-keys/` — Data Protection key ring (losing these forces a session re-auth for all users)
- `./data/pds/` — PDS repo SQLite databases, DID/key material, and blob store
- `./data/redis/` — Redis AOF/RDB persistence (cache, queue, rate-limit, saga state)

A concrete backup recipe (run from the deployment directory):

```bash
# For a consistent snapshot, bring the stack down first (avoids WAL-mode split copies)
docker compose down

# Archive everything needed to restore the deployment
tar -czf "../bridgebeats-backup-$(date +%Y%m%d-%H%M%S).tar.gz" \
    data secrets .env docker-compose.yml Caddyfile

# Bring the stack back up
docker compose up -d
```

For a hot copy without downtime, copy each SQLite database together with its `-wal` and `-shm` sidecar files (the databases run in WAL mode). A brief `docker compose stop` is still the safest path.

### Configuration

Keep a secure backup of:
- API credentials
- Private keys (`.p8` files, the ATProto OAuth signing key)
- Configuration files (`.env`, `docker-compose.yml`, `Caddyfile`)

### PDS Sequencer Retention

The PDS `sequencer.sqlite` holds the firehose event log in its `repo_seq` table. The stock Bluesky PDS never trims this table, so at bot-write volume it grows without bound — the BridgeBeats deployment reached ~14 GB before this was addressed.

`install.sh` registers a daily cron job at `/etc/cron.d/bridgebeats-pds-trim` that runs the vendored `truncate_seq.sh` (by [Bailey Townsend](https://tangled.org/strings/did:plc:rnpkyqnmsw4ipey6eotbdnnf/3milxyxx2hl22)) with a 14-day (336-hour) retention window. The job runs daily at 04:17 on the host using the host `sqlite3` binary, which `install.sh` installs if missing. Trim output appends to `./logs/pds-trim.log`.

The script runs `DELETE` only, never `VACUUM`. `DELETE` frees pages for SQLite to reuse but does not return space to the operating system, so the daily trim keeps `repo_seq` bounded and the file size plateaus near its 14-day steady state — it does not shrink an already-bloated file. `VACUUM` is not scheduled because it rewrites the whole database, needs roughly twice the file size in free disk, and holds an exclusive lock that stalls the PDS for its duration. Run `VACUUM` by hand if the file ever needs hard reclamation after a period of unmanaged growth.

> **Relay-cursor caveat:** Trimming `repo_seq` below a downstream consumer's last-seen cursor forces that consumer (a relay or AppView) to re-crawl or backfill rather than resume. With 14-day retention this matters only if a consumer is offline for more than 14 consecutive days.

## Troubleshooting

### Container won't start

Check logs:
```bash
docker logs bridgebeats
```

Common issues:
- `INTERNAL_SERVICE_KEY is not set` — the `internal_service_key` secret is missing; the entrypoint requires it and exits. Re-run `install.sh` or create `./secrets/internal_service_key.txt`.
- Redis not healthy — the app `depends_on` a healthy `redis`; check `docker logs bridgebeats-redis`.
- Missing required environment variables or invalid provider credentials.
- Missing `.p8` key file mount for Apple Music.

### Connection refused

Verify:
- Containers are running: `docker compose ps`
- Caddy is reachable on ports 80/443 and DNS points at the host
- The app's internal port is 10000 (proxied by Caddy, not published directly)

### Discord bot not responding

Check:
- `discord_token.txt` secret is set correctly
- Bot has required permissions in the Discord server and is invited
- For multiple instances, each has a unique `NODE_NUMBER`

## Updates

### Using the Installation Script

The installation script handles updates gracefully:

```bash
# Navigate to your BridgeBeats installation directory
cd /path/to/bridgebeats

# Re-run the installation script against the current directory
curl -sSL https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.sh | bash -s -- --directory .
```

The script will:
- Back up your existing `docker-compose.yml` and `Caddyfile` with timestamps
- Back up the `secrets/` directory (retaining the three most recent backups)
- Log current container image digests to `upgrade.log` before pulling new images
- Detect any new environment variables in `.env.example` and show you what to add
- Prompt whether to pull the latest container images
- Preserve your existing secrets and `.env` configuration

### Manual Update

> **Compatibility ordering:** this release adds the `NO_RESULT` and `RESULT_NOT_PERSISTED`
> completion payloads shared by Saga Coordinator publishers and Web consumers. During a rolling
> update, deploy Web before Saga Coordinator (or update them atomically). An older Web instance can
> otherwise interpret a new sentinel as a record URI. The standard Compose command below recreates
> the stack as one update; avoid separately upgrading Saga Coordinator first.

```bash
# From the deployment directory, pull the latest images and recreate
docker compose pull
docker compose up -d
```

### Rollback

If you need to roll back after an update:

1. Check `upgrade.log` for the previous container image digests (the log records each container's `image` and `RepoDigests` entry before the pull)
2. Restore the backed-up configuration files (for example, `docker-compose.yml.bak.20260204-143022`)
3. Pin the image to the recorded digest in `docker-compose.yml`, for example `tsmarvin/bridgebeats@sha256:<digest>`, then run `docker compose up -d`

> **Note:** The publish workflow tags images as `latest` (main), `develop` (develop), and per-PR refs. It does not publish per-commit `sha-<commit>` tags, so roll back by digest (recorded in `upgrade.log`) rather than by a commit-SHA tag.
