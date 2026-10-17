# Spotify bulk-batch and queue routing

This is a developer-facing explanation of how BridgeBeats batches single-id Spotify
lookups and orders work across the priority queue. It covers the design and the
reasoning behind the constants, not a step-by-step task. Type and member names below
are links by name into the source; line numbers are deliberately omitted because they
go stale.

## Overview

Spotify's Web API lets a caller fetch many ids in one request: up to 50 tracks per
`GET /tracks?ids=...` call and up to 20 albums per `GET /albums?ids=...` call. Issuing
one HTTP round-trip per id wastes that capacity and burns rate-limit budget faster than
necessary. The batch path exists to amortize those round-trips: single-id track and
album lookups that do not need an immediate answer are diverted onto dedicated Redis
streams, accumulated, and then resolved in multi-id batch calls.

The moving parts:

- `SpotifyBulkQueueDecorator` (in `BridgeBeats.Core.Infrastructure.Queue`) wraps the
  Spotify request queue and re-routes non-interactive `SongIdLookup` and `AlbumIdLookup`
  enqueues onto type-specific bulk streams.
- `SpotifyBatchQueueHelper` owns the consumer-group lifecycle, depth and age inspection,
  batch dequeue, acknowledgement, and bounded requeue for those bulk streams.
- `SpotifyBulkProcessorService` is the background service that decides when to flush each
  stream, calls the bulk API, and applies each result to its saga.
- `RedisRequestQueue<T>` is the generic per-provider queue. It does not handle the
  type-specific bulk streams; it governs how the interactive, background, and bulk lanes
  of the generic queue are ordered during dequeue.
- `JetStreamWatcherService` is one producer that feeds the bulk lane: it discovers music
  links on the Bluesky firehose and enqueues them at bulk priority.

The single-id-to-bulk diversion applies only to Spotify, because only the Spotify path
has a decorator and a bulk processor wired up for it.

## Queue lanes and dequeue ordering

Each provider's generic queue (`RedisRequestQueue<T>`) has four Redis streams, keyed by
the lower-cased provider name:

- `queue:{provider}:interactive` — highest priority; a user is waiting.
- `queue:{provider}:background` — work that can wait.
- `queue:{provider}:bulk` — lowest priority; the generic bulk lane.
- `queue:{provider}:dlq` — the dead-letter queue (read by range, no consumer group).

Ordering is deterministic, not weighted-random. `QueueSettings.Weights`
(`PriorityWeights`) is retained for backward-compatible configuration but no longer drives
selection; the field's own remarks say so. The order for a given dequeue is produced by
`GetStreamDequeueOrder`, a pure method that takes the current dequeue counter and the
current queue depth.

### Interactive-first

On a normal (non-aging) call, the order is interactive, then background, then bulk. The
dequeue visits streams in that order and returns the first message it finds, so as long as
interactive work exists it is always served first.

### The aging escape hatch

Strict interactive-first ordering would let a steady stream of interactive work starve the
background and bulk lanes forever. To prevent that, every Nth dequeue is an "aging slot"
that demotes interactive and promotes a lower tier to the head of the order. N is
`QueueSettings.InteractiveAgingInterval`, default 8. A call is an aging slot when
`dequeueCounter % agingInterval == 0`.

The aging slots alternate which lower tier leads, so neither background nor bulk starves:

- Odd-numbered aging slots: background leads (`background, interactive, bulk`).
- Even-numbered aging slots: bulk leads (`bulk, interactive, background`).

The slot index is `dequeueCounter / agingInterval`, and its parity selects which tier
leads. A configured interval of 1 or less is treated as misconfiguration — at N=1 every
single dequeue would demote interactive — so the queue logs a warning and falls back to
the default of 8 (`RedisRequestQueue<T>` validates this in its constructor; `QueueSettings`
documents the same fallback).

### Bulk gating

Bulk is only worth serving once enough has accumulated to batch. `GetStreamDequeueOrder`
gates the bulk lane: bulk is included in the order only when its depth reaches the
per-provider minimum threshold. Below the threshold, bulk is dropped from the order
entirely (and on an even aging slot, background leads instead, since there is no eligible
bulk to alternate with).

The threshold comes from `QueueSettings.GetMinBulkThreshold`, which returns the
per-provider override from `ProviderMinBulkThresholds` or falls back to
`DefaultMinBulkQueueThreshold`, default 5. Setting the effective threshold to 0 or 1
disables gating, so bulk is always eligible.

Note the two distinct meanings of "bulk" here. This gating governs the generic
`queue:{provider}:bulk` lane inside `RedisRequestQueue<T>`. The Spotify type-specific
bulk streams described next are separate, and the bulk processor applies its own
size-or-age flush rather than this dequeue-order gating.

## Bulk batching

`SpotifyBulkProcessorService` runs a loop on a 500 ms check interval. Each cycle it asks
whether the track-id stream and the album-id stream should flush, and processes whichever
qualify. The flush decision is a pure predicate, `ShouldFlush`, so it can be tested in
isolation.

### Size-or-age flush

`ShouldFlush(count, oldestAge, threshold, linger)` returns true when either condition
holds:

- Size: `count >= threshold`. For tracks the threshold is
  `SpotifyConstants.MaxTracksPerBatchLookup` (50); for albums it is
  `SpotifyConstants.MaxAlbumsPerBatchLookup` (20). These match the Spotify Web API page
  limits, so a full batch is exactly one maximal API call.
- Age: there is at least one queued item and the oldest item's age has reached the linger
  window. The processor only computes the oldest-item age when the stream is below its size
  threshold, because at or above the threshold the size condition already fires.

Size is the primary trigger. A stream that fills to 50 tracks (or 20 albums) flushes
immediately; the age path is the backstop for low-volume streams that never reach the
size threshold. The oldest enqueue time is read from the stream's first entry —
Redis streams are time-ordered, so the first entry is the oldest
(`SpotifyBatchQueueHelper.GetOldestEnqueuedAtAsync`). A malformed `enqueuedAt` field is
logged and treated as absent so a bad timestamp cannot wedge the age decision.

A flush is also suppressed when the relevant bulk operation's in-process lane is rate-limited
or the stream is in request-failure cooldown (see Reliability). The local lane distinction lets
track and album batching recover independently; the shared Redis cooldown described below is
provider-wide for Spotify data requests.

### The linger default and saga-TTL reconciliation

The linger window is `SpotifyBatchSettings.LingerMs`, config key
`BridgeBeats:Spotify:Batch:LingerMs`, bound from the section
`BridgeBeats:Spotify:Batch`. Its default, `SpotifyBatchSettings.DefaultLingerMs`, is
`86_400_000` ms — 24 hours. A non-positive configured value is clamped back to that
default, so batching's age backstop cannot be disabled by misconfiguration.

A 24-hour age backstop is much larger than a latency bound would be, and that is
intentional. Because size is the real flush trigger, the linger is not there to bound how
long a request waits; it is a staleness drain for streams that never fill. Setting it that
high effectively turns off time-based flushing for normally-trafficked streams while still
guaranteeing a low-volume stream eventually drains.

The size of that default is constrained from below by saga expiry. `QueueSettings.JobExpirationMinutes`
is the number of minutes before an incomplete job or saga expires; its default is `2880`
(48 hours). The relationship the `JobExpirationMinutes` comment references: a saga can be
created when a request is enqueued and then sit in a bulk stream until the next flush. If
the saga's time-to-live were shorter than the linger, the saga could be evicted before the
flush ever writes its result. `JobExpirationMinutes` must therefore exceed the linger
backstop. The 48-hour default is 2× the 24-hour linger, which keeps visible margin between
"latest a batch can flush" and "earliest the saga can expire."

If you raise `LingerMs` past `JobExpirationMinutes`, you reintroduce that eviction race;
keep the saga TTL comfortably above the linger.

## Routing

### How typed id lookups reach the bulk streams

`SpotifyBulkQueueDecorator` decorates the Spotify `IRequestQueue<QueuedLookupRequest>`.
Its `EnqueueAsync` re-routes a request to a type-specific bulk stream only when both
conditions hold:

- the lookup type is `SongIdLookup` or `AlbumIdLookup`, and
- the priority is not `Interactive`, and
- `BypassBulkRouting` is false.

When routed, `SongIdLookup` goes to `SpotifyConstants.BulkTrackIdStream`
(`queue:spotify:bulk:track-id`) and `AlbumIdLookup` goes to
`SpotifyConstants.BulkAlbumIdStream` (`queue:spotify:bulk:album-id`). The request is
serialized and written directly with `XADD`, carrying the `payload` and `enqueuedAt` fields.
After Redis returns a non-null stream id, the bulk enqueue acceptance metric is recorded at
bulk priority so dashboards can tell bulk-stream enqueues apart from generic ones.

Everything else passes through to the inner queue unchanged: interactive `SongIdLookup`
and `AlbumIdLookup` (so an interactive caller is served on the interactive lane with a
single-item call, preserving its latency budget), and every other lookup type at any
priority. All non-enqueue operations on the decorator delegate straight to the inner
queue. `BypassBulkRouting` is used only by deterministic bulk-rejection isolation: the
replacement stays on the background lane and cannot be routed straight back to the bulk
stream that rejected it.

Using the shared stream-name constants on both the producer and consumer side matters:
the constant's own remarks call out that a producer and consumer disagreeing on the stream
name is a silent failure where no messages are ever delivered.

### How firehose links land in the bulk lane

`JetStreamWatcherService` consumes the Bluesky Jetstream firehose, subscribing to the
`app.bsky.feed.post` and `app.bsky.feed.repost` collections. For each newly created record
it extracts candidate links from post facets, embedded external link cards, and the
subjects of quote posts and reposts (hydrating those through the Bluesky API). The watcher
is producer-only: it feeds the queue and never consumes results.

For each candidate link, `IdentifyProviderAsync` decides the provider and lookup type. The
checks run in a deliberate order — `spotify.link` short links first, then Spotify
track/album ids, then Tidal, then Apple Music. A Spotify track link becomes a
`SongIdLookup` and a Spotify album link becomes an `AlbumIdLookup`, both carrying the
resolved Spotify id as the lookup value. (Spotify prerelease links stay as `UriLookup` for
the generic worker; artist and playlist links are not recognized and are dropped.)

Every link the watcher enqueues is built as a `QueuedLookupRequest` with
`OriginPriority` set to `QueuePriority.Bulk` and enqueued at bulk priority through the
resolved provider queue. For Spotify track and album ids, that bulk-priority enqueue is
exactly the case `SpotifyBulkQueueDecorator` intercepts, so firehose-discovered Spotify
track/album links flow into the type-specific bulk streams rather than the generic
`queue:spotify:bulk` lane. Bulk origin priority is persisted into the saga so any
secondary lookups the coordinator spawns also stay out of the interactive lane. Enqueue
failures are logged and swallowed so one bad link does not interrupt the firehose.

## Reliability

The bulk path keeps work moving across crashes, bad data, and transient provider failures.

### Stale-entry recovery with XAUTOCLAIM

`SpotifyBatchQueueHelper` dequeues in three bounded-budget stages. It first reads the current
consumer's pending entries for immediate recovery, then runs `XAUTOCLAIM` for entries owned by
dead consumers that have been idle longer than `AutoClaimMinIdleMs` (15 minutes), and finally
reads new entries with `XREADGROUP` using `>`. Entries recovered or reclaimed in either pending
stage count against the requested batch size, so a single dequeue never returns more than asked.
The 15-minute idle threshold is chosen to be long enough that a slow-but-alive processing cycle
is never reclaimed, yet short enough that a crashed consumer does not block the stream for more
than 15 minutes. There is exactly one consumer service reading these streams, which is what
makes reclaiming another consumer's pending entries safe after the idle window.

`XAUTOCLAIM` requires Redis 6.2 or newer. If the server is older, the reclaim step throws,
is logged, and is skipped. Current-consumer PEL recovery remains available because it uses the
ordinary pending-entry read; only dead-consumer recovery through `XAUTOCLAIM` is unavailable.
The dequeue still reads new entries, so a Redis version below 6.2 loses only that dead-consumer
recovery path.

### Poison-entry handling

An entry with a missing payload, a payload that deserializes to a literal JSON null, or a
payload that cannot be deserialized is poison: retrying it can never succeed and would just
loop forever under repeated `XAUTOCLAIM` re-claims. Such entries are acknowledged and
deleted (`XACK` + `XDEL`) on both the reclaim and the new-read paths rather than retried,
so they leave the pending list permanently.

### Deterministic bulk rejection

When Spotify rejects a bulk request with a deterministic 4xx, each source delivery is moved to
`queue:spotify:background` for single-item isolation. The replacement carries
`BypassBulkRouting = true`, so the decorator does not return it to the bulk stream. This avoids
promoting maintenance-origin traffic into the live-user lane. The destination `XADD` and source
`XACK`/`XDEL` execute in one Redis Lua script. The script first checks that the source entry still
exists, so retrying an ambiguously completed handoff cannot create a duplicate background copy.
This route change preserves `AttemptCount` and records the replacement as a requeue enqueue.

### Generic-provider throughput and delayed retries

Each generic provider worker uses one dequeue producer and a bounded set of processing tasks.
`QueueSettings.ProviderConcurrency` controls the per-provider limit; the default is 4 and values
are constrained to 1–32. Redis stream reads remain serialized while provider HTTP work can overlap.

Ordinary transient failures do not sleep inside a provider's consumer loop. The failed delivery is
replaced with an incremented `AttemptCount` and a `NotBefore` timestamp using the existing
1/2/4/8-second schedule, then the original is acknowledged. Dequeue skips a future-dated request
until it becomes eligible, so one failing lookup no longer blocks unrelated work for that provider.

Active rate-limit discovery uses a per-provider sorted-set index rather than scanning the Redis
keyspace. A short in-process cache further removes repeated Redis reads from the hot dequeue path;
set and clear operations invalidate it immediately.

Spotify data-request limits are tracked as one provider-wide window because Spotify applies the
relevant quota across endpoints. Authentication remains a distinct `auth` window. Tidal and Apple
Music retain endpoint-level windows. The outbound HTTP handler owns publication of these shared
Redis windows from `Retry-After`; queue and batch workers consume that common policy when deciding
whether work is eligible.

### Requeue cap and finalization

Ordinary request failures are bounded by `LookupConstants.MaxQueueRetryAttempts` (5), the same cap
the generic queue processor enforces. Rate-limit requeues are deferrals rather than failed attempts:
they preserve `AttemptCount` and remain bounded by the job's absolute
`QueueSettings.JobExpirationMinutes` deadline. `SpotifyBatchQueueHelper.RequeueAsync` handles
replacement and terminal-discard paths separately:

- for an ordinary retry below the cap, it re-adds a fresh entry with a new `enqueuedAt` and
  `AttemptCount + 1`; for a rate-limit deferral, it keeps the current attempt count. Once Redis
  accepts that replacement with `XADD`, the
  enqueue acceptance metric is incremented, then the original delivery is acknowledged and deleted
  (`XACK` + `XDEL`), and the helper returns `Requeued`;
- if the cap is reached, or the payload cannot be deserialized to read its attempt count,
  it terminally acknowledges and deletes the original message without adding a replacement, then
  returns `CapReached`;
- if the original entry no longer exists (already deleted, for example after an
  `XAUTOCLAIM` re-claim by a duplicate in flight), it returns `NotFound`.

The processor reacts to the outcome. On `CapReached` it writes a failed Spotify provider
state ("Bulk lookup failed after 5 attempts") and publishes saga-level and per-lookup
completion so the saga coordinator and any interactive waiter unblock instead of hanging.
On `NotFound` it leaves the saga untouched, since another consumer handled the entry.

### Rollout metrics

Queue health is observable by provider, priority, and stream through live pending count
(`bridgebeats.queue.pending`), oldest pending age
(`bridgebeats.queue.pending.oldest_age`), and consumer-group lag
(`bridgebeats.queue.consumer.lag`). `bridgebeats.queue.sojourn.duration` measures enqueue-to-dequeue
age, while dequeue, PEL scan, rate-limit discovery, provider HTTP, saga update, and message wall
histograms isolate processing time sinks.

`bridgebeats.queue.terminal.total` records terminal delivery outcomes;
`bridgebeats.queue.saga.lifecycle.total` records fencing/finalization outcomes; and
`bridgebeats.queue.refresh.outcome.total` records maintenance selection and disposition.
Refresh enqueue volume is provider-leg based and is named
`bridgebeats.queue.refresh.leg.enqueued.total`. Source deliveries removed with `XACK` + `XDEL`
are counted by `bridgebeats.queue.delivery.removed.total`; this is deliberately not a success
counter. Dead-letter moves are counted independently by `bridgebeats.queue.dlq.total`, so the
terminal outcome counter remains one disposition per delivery. Poison destruction and
missing/ghost PEL entries use distinct outcome tags, and new
`XREADGROUP` deliveries are excluded from PEL-scan entry counts.

For every replacement requeue, the new stream entry is accepted with `XADD` before the original
delivery is removed with `XACK` and `XDEL`. This ordering preserves the retry if cleanup fails and
is also the boundary used by enqueue telemetry.

### Failure shapes in the bulk dispatch

The processor distinguishes three failure shapes when resolving a batch:

- A rate limit (`RetryAfterExceededException`) has already caused the outbound HTTP handler to
  record the provider-wide Spotify data cooldown. The processor marks each affected saga partial,
  merges a rate-limit info entry for Spotify into the saga, publishes
  the `RATE_LIMITED` sentinel (`LookupConstants.RateLimitedSentinel`) so synchronous waiters
  are not left hanging on a 429, and requeues the whole batch without consuming an attempt.
- An empty result dictionary is treated as a request-level failure (network or auth error).
  No saga state is written; the whole batch is requeued and the stream's request-failure
  cooldown is armed.
- An absent key inside a non-empty result is a per-id partial: only that one message is
  requeued. A key that is present with a null value is a genuine not-found and is written as
  a not-found provider state, not requeued.

### Exponential cooldown after request failures

After an empty-dict request failure, the processor arms a per-stream cooldown so it does
not re-flush and burn retries faster than Spotify can recover. The base is
`SpotifyBatchSettings.RequestFailureCooldownSeconds` (config key
`BridgeBeats:Spotify:Batch:RequestFailureCooldownSeconds`, default 5; non-positive values
are clamped back to the default). `ComputeCooldownSeconds` doubles the base once per prior
consecutive failure, clamping the exponent at 5 to avoid overflow, and caps the result at
`SpotifyBatchSettings.MaxRequestFailureCooldownSeconds` (60). The track-id and album-id
streams each keep their own cooldown and consecutive-failure counter, so a failure on one
endpoint does not suppress flushes on the other. A successful batch dispatch resets that
stream's counter and clears its cooldown.

## Genre resolution

`SpotifyArtistGenreService` keeps the artist-to-genre cache fresh on a slow, rolling
schedule, separate from the per-request bulk path. It waits 30 seconds after startup, then
wakes on a 5-minute check interval. A full refresh runs only when 7 days have elapsed since
the last full run (`s_scheduleInterval` is `TimeSpan.FromDays(7)`).

A full run drains the per-provider artist-refresh queue (a Redis sorted set) in batches of
`SpotifyConstants.MaxArtistsPerBatchLookup` (50, the Spotify page limit for artists),
looks up each batch's genres, and writes them back to the cache. It pauses 100 ms between
batches to be gentle on Spotify's rate limits, and stops when the queue drains empty. An
artist that is not found is left uncached so it can be retried on a later run; an empty
genre list is a valid result and is cached. Errors in a single batch are logged and the run
continues with the next batch; errors in the loop are logged and retried after a one-minute
delay rather than crashing the host.
