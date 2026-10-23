# Caching and storage

This guide explains how BridgeBeats caches and stores lookup results, and how the separate genre
cache behaves. It is grounded in the current implementations
(`src/BridgeBeats.Core/Infrastructure/Cache/RedisMediaLinkCache.cs`,
`RedisGenreCache.cs`, and `src/BridgeBeats.Core/Infrastructure/Queue/RedisRequestDeduplicator.cs`).

## Two caches, different rules

BridgeBeats runs two distinct Redis-backed caches with different lifetime rules. Keep them straight:

- **Media-link cache** (`RedisMediaLinkCache`): indexes lookup results. Redis holds pointer keys to
  the durable record; the result body lives on the ATProto PDS. These keys expire on a TTL.
- **Genre cache** (`RedisGenreCache`): caches track and artist genres plus an artist-refresh queue.
  These keys have **no TTL** — they persist until overwritten.

## Media-link cache

### Architecture

The media-link cache is a two-tier system:

1. **Redis** holds derived lookup pointers (key → PDS record URI) and per-entry bookkeeping. It can
   be rebuilt from the PDS at any time.
2. **ATProto PDS** holds the durable `MediaLinkResult` body and is the source of truth.

On a hit, Redis resolves a lookup key to a record URI, and the body is fetched from the PDS.

### Configuration

The relevant settings live under the `BridgeBeats` configuration section:

| Setting | Default | Effect |
|---|---|---|
| `ATProtoIdentifier` | — | The ATProto handle or DID that posts records. |
| `ATProtoPassword` | — | The ATProto password or app password. Use an app password. |
| `CacheDays` | `7` | Pointer lifetime and freshness window, in days. |
| `RedisConnectionString` | — | Redis connection (supplied by Aspire in development). |
| `RefreshIntervalHours` | `24` | Hours between stale-cache refresh sweeps. |
| `MaxRecordsPerRun` | `100` | Maximum stale records re-enqueued per refresh sweep. |

Defaults are from `src/BridgeBeats.Web/Configuration/AppSettings.cs`. Use an ATProto app password
rather than your main account password.

### Stale-cache refresh sweep

The `StaleCacheRefreshBackgroundService` (in `BridgeBeats.Worker.Maintenance`) runs on a
periodic timer, defaulting to every 24 hours (`RefreshIntervalHours`). Each run queries Redis for
the oldest stale or expired media-link records and re-enqueues up to `MaxRecordsPerRun` of them at
bulk priority. The provider workers process these lookups and write refreshed records back to the
PDS, after which the cache pointer TTLs are renewed. This sweep causes the cache to converge toward
fresh data without requiring a lookup request to trigger revalidation.

Set `REFRESH_INTERVAL_HOURS` and `MAX_RECORDS_PER_RUN` in `.env` (or the corresponding
`BridgeBeats:RefreshIntervalHours` / `BridgeBeats:MaxRecordsPerRun` configuration keys) to tune
throughput and frequency for your deployment volume.

### Redis key patterns

`RedisMediaLinkCache` writes these pointer and bookkeeping keys (the literal prefixes are constants
in the class):

| Key pattern | Holds | Example |
|---|---|---|
| `lookup:isrc:{ISRC}` | Record URI, keyed by upper-cased ISRC | `lookup:isrc:USRC17607839` |
| `lookup:upc:{UPC}` | Record URI, keyed by upper-cased UPC | `lookup:upc:00602537518357` |
| `lookup:url:{urlHash}` | Record URI, keyed by hashed input URL | `lookup:url:abc123...` |
| `lookup:card:{cardId}` | Record URI, keyed by card id | `lookup:card:xyz789` |
| `lookup:provider:{provider}:{id}` | Record URI, keyed by provider and provider id | `lookup:provider:Spotify:track123` |
| `lookup:metadata:{metadataHash}` | Record URI, keyed by hashed title/artist | `lookup:metadata:def456...` |
| `meta:{rkey}` | Per-entry bookkeeping (JSON: rkey, cardId, recordUri, createdAt, lastLookedUpAt) | `meta:track:USRC17607839` |
| `keys:{rkey}` | Set of all keys written for an entry, for refresh and cleanup | `keys:track:USRC17607839` |

Every key written for an entry shares a TTL of `CacheDays` and is recorded in the entry's
`keys:{rkey}` set so the whole set can be refreshed or removed together.

### Lookup flow

`RedisMediaLinkCache` exposes one method per lookup strategy
(`TryGetCachedResultByISRCAsync`, `...ByUPCAsync`, `...ByMetadataAsync`,
`...ByProviderIdAsync`, `...ByCardIdAsync`, and `TryGetCachedResultAsync` for the input URL). Each:

1. Hashes or normalizes the input into the matching pointer key.
2. Reads the pointer from Redis. On a hit, fetches the body from the PDS.
3. Returns the result, its record URI, and an `isStale` flag.

The ISRC and UPC lookups add a fallback: on a Redis pointer miss they derive the expected record URI
from the external id and query the PDS directly, re-indexing in Redis when the record is found. A
record present on the PDS but missing from Redis can still be served.

### Staleness

`CheckRecordFreshness` marks a record stale on age alone: the record is stale when its `lookedUpAt`
timestamp is older than `CacheDays`. Nothing else drives the freshness check.

Partiality does not affect cache freshness. Interactive lookups may publish an interim PDS result
while secondary provider legs are outstanding so their synchronous waiters can receive available
links. The terminal write replaces that interim body once every leg reaches a terminal state.

Maintenance refreshes are different: a pending refresh context suppresses all interim PDS writes.
The saga still records each provider leg independently, and only failed legs are retried. Once all
legs are terminal, one refresh write preserves every successful sibling result even if another
provider exhausted its retries. Only a refresh with no successful provider result is promoted to the
unresolved-review workflow. Thus one provider outage neither discards other providers nor causes an
incomplete refresh body to replace the currently served record.

### Storage flow

`CacheResultAsync` stores and indexes a result:

1. Writes the body to the ATProto PDS first, with a deterministic rkey (the durable step).
2. Removes any stale lookup keys recorded under `keys:{rkey}`.
3. Calls `AddInputLinksAsync` to write the full pointer set: a `meta:{rkey}` record, a `lookup:card:`
   pointer, `lookup:url:` pointers for every input and provider URL, ISRC or UPC pointers from each
   provider's external id, title/artist metadata pointers, and provider-id pointers.

When an entry for the same record already exists with the same record URI, `AddInputLinksAsync`
refreshes the TTLs on the existing keys and skips the rewrite.

### Record key generation

Record keys (rkeys) are deterministic, so the same logical content always maps to the same PDS
record and writes are idempotent (`src/BridgeBeats.Core/Infrastructure/Storage/RecordKeyGenerator.cs`):

- **With an external id**: `album:{sanitizedUPC}` or `track:{sanitizedISRC}`. The id is sanitized to
  letters, digits, and hyphen and used directly — it is not hashed.
- **Without an external id**: `metadata:{hash}`, where the hash is the first 16 characters of the
  lowercase base32 encoding of `SHA-256(title|artist|kind)`.

The card id is the lowercase base32 encoding of `SHA-256(rkey)`, truncated to 32 characters, which
keeps it URL-safe and case-insensitive.

### URL hashing and privacy

Input URLs are not stored on the PDS. They are normalized
(`LinkNormalizer.Normalize`, which strips scheme, host casing, `www.`, fragments, and
non-significant query parameters) and then hashed with `SHA-256`, encoded as lowercase base32
(`HashUtility.HashUrl`). Only that hash appears in Redis, under `lookup:url:{urlHash}`.

This keeps tracking-laden query parameters out of both Redis and the public ATProto network: the
cache can recognize a URL it has seen before without retaining the URL itself.

### Stale-while-revalidate

When a stale record is found, the cache returns it immediately and the caller can enqueue a
background refresh. A refresh writes a new PDS record and rebuilds the Redis pointers for the rkey,
so later requests get the refreshed data. This serves fast responses while keeping data current.

## Request deduplication

`RedisRequestDeduplicator` collapses concurrent identical lookups into a single in-flight request and
lets the others wait for its result. It uses SETNX for the lock and Redis Pub/Sub for completion
notification.

The request key is canonical: `GenerateRequestKey(type, value)` produces `{type-lower}:{value-upper}`
so duplicate lookups collapse to the same key.

The flow:

1. **Acquire** (`TryAcquireAsync`): the first caller sets `inflight:{requestKey}` with `NX` and a TTL,
   storing this process instance's id as the holder. It gets `Acquired = true`. A caller that finds
   the lock held gets `AlreadyInFlight = true` and a deduplication metric is recorded.
2. **Wait** (`WaitForCompletionAsync` / `WaitForFinalCompletionAsync`): a waiter subscribes to
   `complete:{requestKey}`, then re-checks durable state to close the race where the result was
   published before the subscription became active. Empty completion messages are ignored.
3. **Release** (`ReleaseAsync`): the owner deletes the lock only if it still owns it, then publishes
   the result URI on `complete:{requestKey}` so all waiters wake. Completion is published even when
   the release is denied, so waiters are never stranded.

This is what turns the asynchronous saga pipeline into a synchronous response for interactive
callers.

## ATProto storage format

Results are stored as custom AT Protocol records under the `link.bridgebeats.lookup` lexicon.

### Lexicon

The lexicon is defined in `src/BridgeBeats.Web/wwwroot/.well-known/atproto-lexicon/link.bridgebeats.lookup`
and served at `https://<your-domain>/.well-known/atproto-lexicon/link.bridgebeats.lookup`. See
[ATPROTO_LEXICON.md](ATPROTO_LEXICON.md) for setup.

### Record structure

The record `results` field is an **array** of provider results (not a map), each carrying a
`provider` string (`appleMusic`, `spotify`, or `tidal`), and the record carries a `lookedUpAt`
timestamp. This is the persisted shape and differs from the API response shape, which keys results
by provider — see [API.md](API.md).

```json
{
  "results": [
    {
      "provider": "spotify",
      "artist": "Artist Name",
      "title": "Track Title",
      "externalId": "USRC12345678",
      "url": "https://open.spotify.com/track/...",
      "artUrl": "https://i.scdn.co/image/...",
      "marketRegion": "us",
      "isAlbum": false
    }
  ],
  "lookedUpAt": "2025-01-19T23:00:00Z"
}
```

The lexicon requires `results` and `lookedUpAt`, allows 1–10 provider results, and requires
`provider`, `artist`, `title`, `url`, and `marketRegion` on each provider result.

## Genre cache

`RedisGenreCache` is a separate cache for track and artist genres, plus a per-provider artist-refresh
queue. Its keys have **no TTL**: the class stores data until it is explicitly overwritten.

### Key patterns

| Key pattern | Type | Holds |
|---|---|---|
| `genre:{provider}:{id}` | hash | A track's genres (a `genres` JSON array and an ISO-8601 `cachedAt`) |
| `artist-genre:{provider}:{id}` | hash | An artist's genres (same shape) |
| `track-artists:{provider}:{trackId}` | list | A track's artist ids |
| `artist-refresh-queue:{provider}` | sorted set | Artists awaiting a genre refresh, scored by enqueue time |

### Behavior

- **Track genres** (`GetGenresAsync`): on a miss for a Spotify track, genres are resolved on demand
  by merging the genres of the track's artists (via the `track-artists` mapping) and back-filling the
  track entry. For other providers a miss returns null.
- **Artist refresh queue**: `EnqueueArtistsForRefreshAsync` adds artist ids to the sorted set only if
  not already present, so a queued artist keeps its original position. `DequeueArtistsForRefreshAsync`
  pops the oldest entries first (lowest score). The queue drains FIFO.

Because these entries have no TTL, they accumulate until overwritten. Track and artist genre writes
replace the prior hash; track-artist mappings are replaced atomically within a transaction.

## Performance and scaling

- A Redis pointer read is O(1). The result body fetch is a PDS API call, so a cache hit is one Redis
  read plus one PDS fetch.
- Redis stores only derived pointers and bookkeeping, not result bodies, so its footprint per entry
  is small.
- Multiple application instances share one Redis cache. Request deduplication prevents duplicate
  provider calls across instances, and stale-while-revalidate minimizes provider load.

Verification needed: the order-of-magnitude latency figures and ATProto PDS rate limits that appeared
in earlier revisions of this guide were not confirmed against the current code or an authoritative
ATProto specification. Measure against a running system, and cite the ATProto PDS limits from the
provider's documentation, before stating specific numbers.

## Maintenance

### Refresh

A stale media-link record is served immediately and refreshed in the background. The refresh writes a
new PDS record and rebuilds the rkey's Redis pointers (old keys removed via the `keys:{rkey}` set
before new ones are written).

### Manual invalidation

Media-link cache keys expire on their `CacheDays` TTL. To remove an entry early, read its
`keys:{rkey}` set and delete the listed keys along with the `keys:{rkey}` and `meta:{rkey}` keys:

```bash
redis-cli
SMEMBERS keys:track:USRC17607839
# delete the keys returned, then:
DEL keys:track:USRC17607839 meta:track:USRC17607839
```

Genre-cache entries have no TTL; remove them explicitly by key when needed.

## Security

- Use ATProto app passwords, not the main account password.
- Store credentials in environment variables or a secret manager.
- Secure Redis in production (password, network restrictions, TLS).
- Only URL hashes are stored, so tracking parameters in input URLs are not exposed.
</content>
