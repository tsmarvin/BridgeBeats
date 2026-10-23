# BridgeBeats

[![Build and Deploy Documentation](https://github.com/tsmarvin/BridgeBeats/actions/workflows/docs.yml/badge.svg)](https://github.com/tsmarvin/BridgeBeats/actions/workflows/docs.yml)
[![docker-publish](https://github.com/tsmarvin/BridgeBeats/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/tsmarvin/BridgeBeats/actions/workflows/docker-publish.yml)
[![Tests](https://github.com/tsmarvin/BridgeBeats/actions/workflows/tests.yml/badge.svg)](https://github.com/tsmarvin/BridgeBeats/actions/workflows/tests.yml)
[![CodeQL](https://github.com/tsmarvin/BridgeBeats/actions/workflows/github-code-scanning/codeql/badge.svg)](https://github.com/tsmarvin/BridgeBeats/actions/workflows/github-code-scanning/codeql)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/tsmarvin/BridgeBeats/badge)](https://scorecard.dev/viewer/?uri=github.com/tsmarvin/BridgeBeats)

**Music is universal. Your links should be too.**

BridgeBeats is a cross-platform music link converter that helps you share music effortlessly across streaming platforms. Share a link from Apple Music, Spotify, or Tidal, and BridgeBeats finds the same track or album on all supported services-ensuring every listener can enjoy the music, regardless of their preferred platform.

## ✨ Share once. Play anywhere.

Ever wanted to share your favorite song, only to realize your friend uses a different streaming service? BridgeBeats solves this by automatically finding the same track or album on all major platforms-so everyone can listen, no matter where they stream.

## 🎵 Features

- **Instant Conversion** - Drop a music link and get matches across Apple Music, Spotify, and Tidal
- **Discord Bot** - Automatically converts music links in your Discord server
- **Web Interface** - Simple browser-based tool for quick conversions
- **RESTful API** - Integrate music link conversion into your own apps
- **Accurate Matching** - Uses ISRC (tracks) and UPC (albums) for precise cross-platform matches
- **Rich Previews** - OpenGraph cards that work everywhere-Discord, Slack, Twitter, and more
- **Aspire Dashboard** - Built-in observability and telemetry dashboard (role-based access)

## 🚀 Quick Start

### Using the one-line installer

The installer downloads the Compose files, creates `.env` and the `secrets/` directory, and generates the secrets that must be auto-generated (including the required `INTERNAL_SERVICE_KEY` and the ATProto OAuth signing key):

```bash
# Linux / macOS
curl -sSL https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.sh | bash
```

```powershell
# Windows (PowerShell)
iwr -useb https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.ps1 | iex
```

Then add at least one music provider's credentials and start the stack.

### Using Docker Compose manually

```bash
git clone https://github.com/tsmarvin/BridgeBeats.git
cd BridgeBeats/containers

# Create the secrets directory and placeholder files
mkdir -p secrets && chmod 700 secrets
touch secrets/apple_key.p8 secrets/spotify_client_secret.txt \
      secrets/tidal_client_secret.txt secrets/discord_token.txt \
      secrets/atproto_password.txt
openssl rand -base64 32 > secrets/api_key_salt.txt
openssl rand -base64 32 > secrets/redis_password.txt
openssl rand -base64 32 > secrets/internal_service_key.txt
chmod 600 secrets/*
# Edit the secret files with your credentials

cp .env.example .env
# Edit .env with your configuration
docker compose up -d
```

`secrets/internal_service_key.txt` is required: it is the shared key the worker processes present to authenticate to the web API, and the container will not start without it. The installer generates it for you.

`docker compose up -d` starts four containers: `bridgebeats` (the app, which also spawns the worker processes), `redis` (cache, request queue, and rate-limit tracking), `bridgebeats-pds` (a Bluesky Personal Data Server for ATProto caching), and `bridgebeats-caddy` (the reverse proxy that terminates HTTPS). Only Caddy publishes ports to the host (80 and 443); the app listens on port 10000 inside the internal Docker network and is reached through Caddy.

Visit `https://localhost` to start converting links (accept the self-signed certificate warning for `localhost`).

📖 **[Complete Quick Start Guide →](docs/QUICKSTART.md)**

### Running Locally

1. Install the [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10)
2. Clone the repository
3. Provide an external Redis and set its connection string. Aspire does not start
   Redis for you. Run one with Docker and point the AppHost at it:

   ```bash
   docker run -d --name bridgebeats-redis -p 6379:6379 redis:8-alpine
   dotnet user-secrets set "ConnectionStrings:redis" "localhost:6379" \
     --project src/BridgeBeats.AppHost/BridgeBeats.AppHost.csproj
   ```

4. Add at least one provider's credentials through user secrets or environment
   variables (see [Configuration Guide](docs/CONFIGURATION.md))

Then start the host from the repository root:

```bash
aspire run
```

This starts the app and the worker processes with the Aspire Dashboard for monitoring, tracing, and structured logging. If you prefer to run without the Aspire CLI, use `dotnet run --project src/BridgeBeats.AppHost`. See the [Local Development Guide](docs/LOCAL_DEVELOPMENT.md) for details.

## 📁 Project Structure

```
BridgeBeats/
├── src/
│   ├── BridgeBeats.Web/                     # ASP.NET Core web app (MVC, controllers, views, API)
│   ├── BridgeBeats.Core/                    # Domain logic and infrastructure (caching, identity, storage, queue)
│   ├── BridgeBeats.Contracts/               # Shared DTOs, interfaces, enums, constants, records
│   ├── BridgeBeats.AppHost/                 # .NET Aspire orchestration
│   ├── BridgeBeats.Worker.Spotify/          # Spotify lookup worker (incl. batch processor)
│   ├── BridgeBeats.Worker.AppleMusic/       # Apple Music lookup worker
│   ├── BridgeBeats.Worker.Tidal/            # Tidal lookup worker
│   ├── BridgeBeats.Worker.Discord/          # Discord bot worker
│   ├── BridgeBeats.Worker.JetStreamWatcher/ # AT Protocol firehose watcher
│   ├── BridgeBeats.Worker.SagaCoordinator/  # Cross-provider lookup saga coordinator
│   └── BridgeBeats.Worker.Maintenance/      # Cache warm-up/refresh/assorted maintenance worker
├── Tests/                                   # Single test project (BridgeBeats.Tests.csproj)
│   ├── Unit/                                # Unit tests
│   ├── Integration/                         # Integration tests (service interactions)
│   └── EndToEnd/                            # End-to-end tests (full request/response flows)
├── docs/                                    # Documentation (guides, API reference)
├── containers/                              # Docker deployment configuration
├── .github/                                 # GitHub templates, workflows, and community files
```

## 📖 Documentation

- **[Quick Start Guide](docs/QUICKSTART.md)** - Get up and running with Docker Compose
- **[Local Development Guide](docs/LOCAL_DEVELOPMENT.md)** - Develop with .NET Aspire and the Aspire Dashboard
- **[Configuration Guide](docs/CONFIGURATION.md)** - Set up API credentials and environment variables
- **[API Reference](docs/API.md)** - Integrate BridgeBeats into your applications
- **[Deployment Guide](docs/DEPLOYMENT.md)** - Deploy to Docker, Kubernetes, or cloud platforms
- **[Discord Bot Guide](docs/DISCORD_BOT.md)** - Set up and run the Discord bot
- **[Testing Guide](docs/TESTING.md)** - Run and write the test suite
- **[Caching Guide](docs/CACHING.md)** - Configure ATProto PDS caching
- **[Spotify Batch and Routing Guide](docs/SPOTIFY_BATCH_AND_ROUTING.md)** - How Spotify lookups are batched and routed through the queue
- **[ATProto Lexicon Setup](docs/ATPROTO_LEXICON.md)** - Configure lexicon resolution and DNS for ATProto compliance
- **[Caddy Cloudflare Guide](docs/CADDY_CLOUDFLARE.md)** - Configure Cloudflare DNS for wildcard certificates
- **[SBOM and Provenance Guide](docs/SBOM_AND_PROVENANCE.md)** - Verify supply chain security and inspect dependencies
- **[Contributing Guidelines](.github/CONTRIBUTING.md)** - Learn how to contribute to BridgeBeats

## 🎯 How It Works

BridgeBeats connects to official APIs from music streaming services. When you provide a link:

1. **Extract** - Identifies the track or album from the URL
2. **Match** - Uses external IDs (ISRC/UPC) or metadata to find equivalents
3. **Return** - Provides links for all available platforms

You share the music, not the platform.

## 🤖 Discord Bot

Add BridgeBeats to your Discord server to automatically convert music links in conversations:

1. Get a Discord bot token (see [Configuration Guide](docs/CONFIGURATION.md#discord-bot-token))
2. Put the token in `secrets/discord_token.txt` and restart the stack
3. Invite the bot to your server

When someone shares a Spotify link, BridgeBeats responds with a card showing Apple Music and Tidal alternatives, and vice versa. See the [Discord Bot Guide](docs/DISCORD_BOT.md) for full setup.

## 🛠️ Built With

- **.NET 10.0** - Cross-platform framework
- **.NET Aspire** - Orchestration of the app and its worker processes
- **Redis** - Cache, request queue (Redis Streams), and rate-limit tracking
- **Caddy** - Reverse proxy and automatic HTTPS
- **SQLite** - Identity / user store
- **Apple MusicKit API** - Apple Music integration
- **Spotify Web API** - Spotify integration
- **Tidal API** - Tidal integration
- **NetCord** - Discord bot library

## 🔐 Privacy & Security

- API keys are hashed and never stored in plain text
- Input URLs with tracking parameters are kept private (not stored on ATProto PDS)
- The identifier and name-search lookup endpoints (`isrc`, `upc`, `title`) are rate limited per user (default 20 requests/hour); the public URL endpoints are not rate limited
- Worker processes authenticate to the web API with a shared internal service key
- Credentials are provided as Docker secrets (and as environment variables or user secrets in local development)

## 🌍 Deployment Options

BridgeBeats can be deployed anywhere:

- **Docker** - Simple containerized deployment
- **Cloud Services** - Azure Container Apps, AWS ECS, Google Cloud Run
- **Kubernetes** - Scalable orchestration
- **Self-hosted** - Run natively on Linux, Windows, or macOS

See the [Deployment Guide](docs/DEPLOYMENT.md) for platform-specific instructions.

## 📊 API Usage

Convert a link by URL. This endpoint is public, so no API key is required (`-k`
accepts the self-signed `localhost` certificate):

```bash
curl -k -X POST https://localhost/music/lookup/urlList \
  -H "Content-Type: application/json" \
  -d '{"uri":"https://open.spotify.com/track/..."}'
```

For the identifier and name-search endpoints (`isrc`, `upc`, `title`), register an account to get a one-time API key and send it in the `X-API-Key` header:

```bash
curl -k -X POST https://localhost/account/register \
  -H "Content-Type: application/json" \
  -d '{"email":"user@example.com","password":"SecurePassword123"}'

curl -k -X POST https://localhost/music/lookup/isrc \
  -H "Content-Type: application/json" \
  -H "X-API-Key: YOUR_API_KEY" \
  -d '{"isrc":"USRC17607839"}'
```

See the [API Reference](docs/API.md) for complete documentation.

## 🧪 Testing

```bash
# Run all tests
dotnet test

# Run unit tests only (no API credentials needed)
dotnet test --filter "FullyQualifiedName~Unit"
```

For more details on testing, see the [Testing Guide](docs/TESTING.md) or the [Contributing Guidelines](.github/CONTRIBUTING.md#testing-guidelines).

## 🤝 Contributing

We welcome contributions! Whether you're fixing bugs, adding features, improving documentation, or helping others, your contributions make BridgeBeats better for everyone.

Please read our [Contributing Guidelines](.github/CONTRIBUTING.md) to get started. Key points:

- Follow our [Code of Conduct](.github/CODE_OF_CONDUCT.md)
- Check existing issues before opening a new one
- Use our issue templates for bug reports and feature requests
- Keep pull requests focused and well-tested
- Respect our security practices - see [Security Policy](.github/SECURITY.md)

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 👤 Author

**Taylor Marvin**

---

*Because music connects us - no matter where we listen.*
