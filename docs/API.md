# API Reference

BridgeBeats exposes a REST API for cross-provider music link conversion and lookup. This reference
covers authentication, the lookup endpoints, rate limiting, the response shape, error codes, and the
Open Graph card routes.

The routes and authentication below are documented from the current controllers
(`MusicLookupController`, `AccountController`, `CardApiController`, `OpenGraphCardController` in
`src/BridgeBeats.Web/Controllers`).

## Authentication

The two URL lookup endpoints (`music/lookup/url` and `music/lookup/urlList`) are public and accept
anonymous requests. The identifier and name-search endpoints (`music/lookup/isrc`,
`music/lookup/upc`, `music/lookup/title`) require authentication.

`MusicLookupController` is decorated with `[Authorize]` for the API-key and internal-service schemes
at the class level; the two URL actions override that with `[AllowAnonymous]`. The scheme is chosen
per request by the header present (`StartupExtensions.ConfigureApiKeyAuth`):

| Header present | Scheme used |
|---|---|
| `X-Service-Key` | Internal service-to-service |
| `X-API-Key` | API key |
| neither | Identity cookie (web UI) |

To obtain an API key, register an account.

### Creating an account

`account/register` issues a one-time API key and signs the new user in with a cookie. The endpoint
requires a valid anti-forgery token (`[ValidateAntiForgeryToken]`); fetch one from
`account/antiforgery-token` first and send it in the `X-XSRF-TOKEN` header.

```http
POST /account/register
Content-Type: application/json
X-XSRF-TOKEN: <anti-forgery-token>

{
  "email": "user@example.com",
  "password": "SecurePassword123"
}
```

Response:

```json
{
  "userId": "...",
  "apiKey": "YOUR_API_KEY_HERE",
  "message": "Registration successful. Save your API key - it will not be shown again."
}
```

Save the API key immediately. Only its hash is stored, so the key cannot be retrieved later — it can
only be regenerated.

### Using your API key

Send the key in the `X-API-Key` header on the protected endpoints:

```http
X-API-Key: YOUR_API_KEY_HERE
```

The protected endpoints also accept internal service-to-service authentication via the
`X-Service-Key` header (used by worker services, not by end users).

### Logging in

`account/login` authenticates an existing account and signs it in. It also requires a valid
anti-forgery token. If the account has no API key yet, a new one is generated and returned; if it
already has one, the response returns the placeholder `***EXISTING_KEY***` rather than exposing the
stored key.

```http
POST /account/login
Content-Type: application/json
X-XSRF-TOKEN: <anti-forgery-token>

{
  "email": "user@example.com",
  "password": "SecurePassword123"
}
```

### Regenerating your API key

`account/regenerate-api-key` replaces the stored key and returns the new one. It requires an
authenticated session (`[Authorize]`) and a valid anti-forgery token. The previous key stops working
once the update succeeds.

```http
POST /account/regenerate-api-key
X-XSRF-TOKEN: <anti-forgery-token>
```

### Other account routes

| Route | Method | Auth | Purpose |
|---|---|---|---|
| `account/antiforgery-token` | GET | anonymous | Returns an anti-forgery request token and stores its companion cookie. |
| `account/status` | GET | anonymous | Reports whether the caller is authenticated, with user id and email when signed in. |
| `account/logout` | POST | anti-forgery token | Signs the current user out. |

## Rate limiting

`RateLimitingMiddleware` enforces a per-user hourly quota on the identifier and name-search lookups.

- **Limit**: 20 requests per hour per authenticated user. Configurable via
  `RateLimitRequestsPerHour` (`src/BridgeBeats.Web/Configuration/AppSettings.cs`; default `20`).
- **Applies to**: `music/lookup/isrc`, `music/lookup/upc`, `music/lookup/title` (POST only).
- **Exempt**: the two URL lookup endpoints (`music/lookup/url` and `music/lookup/urlList`). They are
  anonymous and not counted.

The counter is tracked per user over a rolling one-hour window. When the quota is exceeded the
request is rejected with HTTP 429 before reaching the controller:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 2700
Content-Type: application/json

{
  "error": "Rate limit exceeded",
  "message": "Maximum 20 requests per hour allowed. Please try again in 45 minutes.",
  "retryAfter": 2700
}
```

The `Retry-After` header and `retryAfter` field report the seconds remaining until the window resets.

## Lookup endpoints

All five lookup endpoints live under the `music/lookup` route. The table is the authoritative list of
what exists now:

| Endpoint | Method | Auth | Rate-limited | Returns |
|---|---|---|---|---|
| `music/lookup/url` | POST | anonymous | no | Streamed sequence of `MediaLinkResult` |
| `music/lookup/urlList` | POST | anonymous | no | JSON array of `MediaLinkResult` |
| `music/lookup/isrc` | POST | API key or service key | yes | A single `MediaLinkResult` |
| `music/lookup/upc` | POST | API key or service key | yes | A single `MediaLinkResult` |
| `music/lookup/title` | POST | API key or service key | yes | A single `MediaLinkResult` |

There is no batch URL endpoint beyond `urlList`. The URL endpoints extract every `https://` provider
link found in the request text, so a single request can carry several links; results are
deduplicated on external IDs (ISRC for tracks, UPC for albums).

### Lookup by URL, streaming (public)

`music/lookup/url` returns results progressively. The action is declared as
`IAsyncEnumerable<MediaLinkResult>`, which ASP.NET Core serializes as a JSON array streamed to the
client as each result is produced. This is a streamed JSON array, not Server-Sent Events.

```http
POST /music/lookup/url
Content-Type: application/json

{
  "uri": "https://music.apple.com/us/album/..."
}
```

### Lookup by URL, list (public)

`music/lookup/urlList` buffers all results and returns them as a single JSON array of
`MediaLinkResult` objects.

```http
POST /music/lookup/urlList
Content-Type: application/json

{
  "uri": "https://music.apple.com/us/album/..."
}
```

### Lookup by ISRC (protected)

```http
POST /music/lookup/isrc
Content-Type: application/json
X-API-Key: YOUR_API_KEY

{
  "isrc": "USVI20000123"
}
```

When nothing matches, the endpoint returns 200 with a `MediaLinkResult` whose `messages` array holds
`"No results found for ISRC."`.

### Lookup by UPC (protected)

```http
POST /music/lookup/upc
Content-Type: application/json
X-API-Key: YOUR_API_KEY

{
  "upc": "012345678901"
}
```

When nothing matches, the endpoint returns 200 with a `MediaLinkResult` whose `messages` array holds
`"No results found for UPC."`.

### Lookup by title and artist (protected)

```http
POST /music/lookup/title
Content-Type: application/json
X-API-Key: YOUR_API_KEY

{
  "title": "Song Name",
  "artist": "Artist Name"
}
```

When nothing matches, the endpoint returns 200 with a `MediaLinkResult` whose `messages` array holds
`"No results found for title/artist."`.

## Response shape

The lookup endpoints serialize the `MediaLinkResult` type (`src/BridgeBeats.Contracts/DTOs`). The
single-result endpoints (`isrc`, `upc`, `title`) return one object; `urlList` returns an array of
them; `url` streams them.

`MediaLinkResult` has these public members:

| Field | Type | Notes |
|---|---|---|
| `results` | object (map) keyed by provider name | Each key is a `SupportedProviders` member name (`AppleMusic`, `Spotify`, `Tidal`); each value is the provider's result. Populated as each provider responds. |
| `messages` | array of strings or `null` | Informational messages; `null` when there are none. |
| `lookedUpAt` | string (ISO 8601, UTC) | When the aggregate was looked up. |
| `isPartial` | boolean | `true` when this response was returned before its lookup saga finalized and a more complete provider set may follow. This is transient API metadata and is not persisted in PDS records. |
| `rateLimitedProviders` | array of provider names or `null` | The providers still rate-limited when the result is incomplete; `null` when no providers are rate-limited. |

The `results` keys are the `SupportedProviders` enum member names in PascalCase — `AppleMusic`,
`Spotify`, `Tidal` — not numeric values. System.Text.Json serializes enum dictionary keys as the
member name by default, and no value converter overrides that for this response. Match on these exact
key strings.

Each provider entry is a `MusicLookupResult`:

| Field | Type | Notes |
|---|---|---|
| `artist` | string | Primary artist name. |
| `title` | string | Track or album title. |
| `externalId` | string | ISRC for tracks, UPC for albums; empty string when the provider returns none. |
| `url` | string | The provider's web link. |
| `artUrl` | string | Cover artwork URL; empty when unavailable. |
| `marketRegion` | string | ISO 3166-1 alpha-2 region code; defaults to `"us"`. Case is provider-dependent (for example, Apple Music and Spotify return `"us"` while Tidal returns `"US"`). |
| `isAlbum` | boolean or null | `true` for albums, `false` for tracks, `null` when not determined. |

### Example response

A `POST /music/lookup/urlList` call returns a JSON array. Each element is a `MediaLinkResult`:

```json
[
  {
    "results": {
      "AppleMusic": {
        "artist": "Rick Astley",
        "title": "Never Gonna Give You Up",
        "externalId": "GBARL9300135",
        "url": "https://music.apple.com/us/album/never-gonna-give-you-up/1485596039?i=1485596041",
        "artUrl": "https://is1-ssl.mzstatic.com/image/thumb/Music123/v4/17/3c/cb/173ccb70-6d48-24cc-3b5a-ff41560a0fda/4050538545074.jpg/4000x4000bb.jpg",
        "marketRegion": "us",
        "isAlbum": false
      },
      "Spotify": {
        "artist": "Rick Astley",
        "title": "Never Gonna Give You Up",
        "externalId": "GBARL9300135",
        "url": "https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT",
        "artUrl": "https://i.scdn.co/image/ab67616d0000b273baf89eb11ec7c657805d2da0",
        "marketRegion": "us",
        "isAlbum": false
      },
      "Tidal": {
        "artist": "Rick Astley",
        "title": "Never Gonna Give You Up",
        "externalId": "GBARL9300135",
        "url": "https://tidal.com/browse/track/491303910",
        "artUrl": "https://resources.tidal.com/images/cd490142/ab29/4a53/92b9/843b6895b3e4/1280x1280.jpg",
        "marketRegion": "US",
        "isAlbum": false
      }
    },
    "messages": null,
    "lookedUpAt": "2026-06-14T20:13:53.2582978Z",
    "isPartial": false,
    "rateLimitedProviders": null
  }
]
```

The `url` endpoint streams these same objects as a JSON array. The `isrc`, `upc`, and `title`
endpoints return a single such object (not wrapped in an array). On a miss, that single object
carries a `messages` entry and an empty `results` map — see [Lookup misses](#lookup-misses).

### Persisted record shape differs from the API response

The live REST response above is not the same shape as the ATProto record BridgeBeats persists. The
stored `link.bridgebeats.lookup` record (`MediaLinkResultRecord` /
`ProviderResultRecord` in `src/BridgeBeats.Contracts/Records`, also covered in
[CACHING.md](CACHING.md)) holds `results` as an **array** of rows, and each row carries a camelCase
`provider` **string** field (`appleMusic`, `spotify`, `tidal`). The REST response instead uses a
**map keyed by the PascalCase provider name** (`AppleMusic`, `Spotify`, `Tidal`) with no separate
`provider` field. Don't conflate the two: code that reads the API response keys on the map; code that
reads a stored record iterates the array and reads each row's `provider` string.

### Supported providers

`SupportedProviders` (`src/BridgeBeats.Contracts/Enums/SupportedProviders.cs`):

- Apple Music
- Spotify
- Tidal

## Error responses

### 400 Bad Request

Returned for invalid request payloads. Account registration and login return the ASP.NET Core
`ModelState` error collection when validation fails (for example, a duplicate email on register).

### 401 Unauthorized

Returned when a protected lookup endpoint is called without a valid API key or service key, and when
`account/login` is given an unknown email or wrong password. The login error body is:

```json
{
  "message": "Invalid email or password"
}
```

### 429 Too Many Requests

Returned by the rate-limit middleware. See [Rate limiting](#rate-limiting) for the body shape.

### Lookup misses

The lookup endpoints do not return 404 for "not found." A miss returns 200 with a `MediaLinkResult`
carrying a message (for example `"No results found for ISRC."`) and an empty or partial `results`
map.

## Open Graph card endpoints

BridgeBeats renders shareable Open Graph cards for media-link results.

### Store a card

`CardApiController` (route `api/card`) is used by the Discord worker to persist a result and obtain a
card URL.

```http
POST /api/card/store
Content-Type: application/json

{ /* a MediaLinkResult */ }
```

Responses:

- `200 OK` with `{ "cardUrl": "..." }` on success.
- `503 Service Unavailable` with `{ "cardUrl": null }` when the card service is disabled.
- `400 Bad Request` with `{ "cardUrl": null }` when the result is null or has no provider results.

### View a card

`OpenGraphCardController` (route `card`) renders the card pages.

| Route | Method | Returns |
|---|---|---|
| `card/{id}` | GET | The full card page; `404 Not Found` when the card is unknown or expired. |
| `card/{id}/embed` | GET | The embeddable card view, no QR code. |
| `card/{id}/embed/qr` | GET | The embeddable card view with a QR code. |

When a card is not held in memory, the controller recovers it from the cache by card id and
re-stores it.

The full card page emits Open Graph metadata (built by `MediaLinkResult.ToOpenGraphMetadata`) so the
card renders correctly when shared on platforms such as Discord, Slack, and others.

Card lifetime is governed by `CardCacheExpirationHours` (`AppSettings`; default `1` hour), not a
fixed 24 hours.

## Code examples

The examples use the public `urlList` endpoint, which needs no API key.

### Python

```python
import requests

BASE_URL = "https://bridgebeats.example.com"

# Public endpoint — no API key required.
response = requests.post(
    f"{BASE_URL}/music/lookup/urlList",
    json={"uri": "https://open.spotify.com/track/..."}
)

if response.status_code == 200:
    for result in response.json():
        for provider, item in result["results"].items():
            print(f"{provider}: {item['url']}")
```

### JavaScript / Node.js

```javascript
const axios = require('axios');

const BASE_URL = 'https://bridgebeats.example.com';

// Public endpoint — no API key required.
async function lookup(uri) {
  try {
    const response = await axios.post(`${BASE_URL}/music/lookup/urlList`, { uri });
    return response.data; // array of MediaLinkResult
  } catch (error) {
    if (error.response?.status === 429) {
      console.log(`Rate limited. Retry after ${error.response.headers['retry-after']} seconds`);
    }
    throw error;
  }
}
```

### cURL

```bash
# Public endpoint — no API key required.
curl -X POST https://bridgebeats.example.com/music/lookup/urlList \
  -H "Content-Type: application/json" \
  -d '{"uri": "https://open.spotify.com/track/..."}'
```

## Practical notes

1. Prefer ISRC and UPC over title/artist when you have them — they match more reliably across
   providers.
2. Honor the `Retry-After` header on 429 responses.
3. Cache results on your side to avoid redundant calls.
4. Check `isPartial` on the response. When `true`, the coordinating saga had not finalized when
   the response was produced and a more complete provider set may follow. When
   `rateLimitedProviders` is non-null, it identifies providers delayed by rate limiting; `messages`
   contains user-facing explanations and retry timing.

## Swagger

When the application is running, interactive API documentation is served at `/swagger`. The Swagger
UI is generated from the XML doc comments and lets you browse endpoints, view request and response
schemas, and download the OpenAPI specification. The Swagger document defines an `X-API-Key` security
scheme for the protected endpoints.
</content>
</invoke>
