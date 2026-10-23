namespace BridgeBeats.Worker.Spotify.Logging;

/// <summary>
/// Stable log event identifiers for the Spotify worker's structured log messages, in the 5250-5499
/// range. The range continues from the shared
/// <see cref="Core.Infrastructure.Logging.LogEventIds"/> defined for the rest of the system.
/// </summary>
/// <remarks>
/// Ids are grouped by subsystem: the 5250 range covers <see cref="SpotifyArtistGenreService"/>,
/// the 5300 range covers <see cref="SpotifyBatchQueueHelper"/> (bulk-stream queue operations),
/// and the 5350 range covers <see cref="SpotifyBulkProcessorService"/> (batch processing). The
/// numeric values are part of the log contract; changing them breaks downstream log queries and
/// alerting.
/// </remarks>
public static class LogEventIds {
    // SpotifyArtistGenreService (5250-5299)

    /// <summary>Artist-genre background service has started.</summary>
    public const int ArtistGenreServiceStarting = 5250;

    /// <summary>A scheduled (every-seven-days) artist-genre refresh run is beginning.</summary>
    public const int StartingScheduledRefresh = 5251;

    /// <summary>A scheduled artist-genre refresh run has finished.</summary>
    public const int CompletedScheduledRefresh = 5252;

    /// <summary>An unhandled error occurred in the artist-genre processor loop.</summary>
    public const int ArtistProcessorLoopError = 5253;

    /// <summary>Artist-genre background service is stopping.</summary>
    public const int ArtistGenreServiceStopping = 5254;

    /// <summary>Reports the current depth of the artist-refresh queue.</summary>
    public const int ArtistQueueLength = 5255;

    /// <summary>The artist-refresh queue drained empty, ending the refresh run.</summary>
    public const int ArtistQueueEmpty = 5256;

    /// <summary>A batch of artists was processed during a refresh run.</summary>
    public const int ArtistBatchProcessed = 5257;

    /// <summary>An artist-genre refresh run completed, with totals.</summary>
    public const int ArtistRefreshComplete = 5258;

    /// <summary>A requested artist id was not found by the lookup service.</summary>
    public const int ArtistNotFound = 5259;

    /// <summary>An error occurred while processing a batch of artists.</summary>
    public const int ArtistBatchError = 5260;

    // SpotifyBatchQueueHelper (5300-5349)

    /// <summary>A Redis consumer group was created for a bulk stream.</summary>
    public const int ConsumerGroupCreated = 5300;

    /// <summary>A Redis consumer group already existed for a bulk stream (benign).</summary>
    public const int ConsumerGroupExists = 5301;

    /// <summary>A request was routed onto a type-specific bulk stream.</summary>
    public const int EnqueuedToBulkStream = 5302;

    /// <summary>
    /// Reserved id for a collection-warning log event; no longer used by
    /// <see cref="SpotifyBatchQueueHelper"/>.
    /// </summary>
    public const int CollectionWarning = 5303;

    /// <summary>A stream entry payload failed to deserialize and was discarded as poison.</summary>
    public const int DeserializationError = 5304;

    /// <summary>A non-fatal error occurred while reading from a bulk stream.</summary>
    public const int StreamReadWarning = 5305;

    /// <summary>A bulk-stream message was acknowledged and deleted.</summary>
    public const int MessageAcknowledged = 5306;

    /// <summary>A message targeted for requeue was not found in its stream.</summary>
    public const int MessageNotFound = 5307;

    /// <summary>A bulk-stream message was requeued with an incremented attempt count.</summary>
    public const int MessageRequeued = 5308;

    /// <summary>Pending bulk-stream entries were reclaimed via XAUTOCLAIM.</summary>
    public const int AutoClaimRecovered = 5309;

    /// <summary>XAUTOCLAIM is unsupported by the Redis server; pending-entry recovery is disabled.</summary>
    public const int AutoClaimNotSupported = 5310;

    /// <summary>A message exceeded the maximum retry attempts and was discarded.</summary>
    public const int MaxRetriesExceeded = 5311;

    /// <summary>
    /// Retired id 5312 (formerly <c>LogSagaUpdateError</c>); no longer emitted after saga writes
    /// moved to the service layer.
    /// </summary>
    public const int SagaUpdateError = 5312;

    // SpotifyBulkProcessorService (5350-5399)

    /// <summary>Bulk processor background service has started.</summary>
    public const int BulkProcessorStarting = 5350;

    /// <summary>An unhandled error occurred in the bulk processor loop.</summary>
    public const int BulkProcessorLoopError = 5351;

    /// <summary>Bulk processor background service is stopping.</summary>
    public const int BulkProcessorStopping = 5352;

    /// <summary>A bulk endpoint is currently rate-limited, so its batch was deferred.</summary>
    public const int BulkRateLimited = 5353;

    /// <summary>The processor is starting a bulk track-id flush.</summary>
    public const int ProcessingBulkTracks = 5354;

    /// <summary>No track-id lookups were available to process.</summary>
    public const int NoTrackLookups = 5355;

    /// <summary>Reports the number of track-id lookups in the current batch.</summary>
    public const int ProcessingTrackCount = 5356;

    /// <summary>A batch of track-id lookups was processed successfully.</summary>
    public const int TrackLookupsSuccess = 5357;

    /// <summary>An error occurred while processing bulk track-id lookups.</summary>
    public const int TrackLookupsError = 5358;

    /// <summary>The processor is starting a bulk album-id flush.</summary>
    public const int ProcessingBulkAlbums = 5359;

    /// <summary>No album-id lookups were available to process.</summary>
    public const int NoAlbumLookups = 5360;

    /// <summary>Reports the number of album-id lookups in the current batch.</summary>
    public const int ProcessingAlbumCount = 5361;

    /// <summary>A batch of album-id lookups was processed successfully.</summary>
    public const int AlbumLookupsSuccess = 5362;

    /// <summary>An error occurred while processing bulk album-id lookups.</summary>
    public const int AlbumLookupsError = 5363;

    /// <summary>A single bulk result was applied to its saga.</summary>
    public const int BulkResultProcessed = 5364;

    /// <summary>An error occurred while applying a single bulk result to its saga.</summary>
    public const int BulkResultError = 5365;

    /// <summary>A rate limit was encountered while calling a bulk endpoint.</summary>
    public const int RateLimitEncountered = 5366;

    /// <summary>An error occurred while requeuing a bulk-stream message.</summary>
    public const int RequeueError = 5367;

    /// <summary>A saga became complete; a completion event is being published.</summary>
    public const int SagaComplete = 5368;

    /// <summary>An error occurred while checking or publishing saga completion.</summary>
    public const int SagaCompletionCheckError = 5369;

    /// <summary>An error occurred while publishing a per-lookup completion event.</summary>
    public const int PublishCompletionError = 5370;

    /// <summary>A saga was marked partial because a bulk endpoint was rate-limited.</summary>
    public const int BulkSagaMarkedPartial = 5371;

    /// <summary>The whole current batch is being requeued (for example after a rate limit).</summary>
    public const int BulkDispatchRequeuingAll = 5372;

    /// <summary>A single message is being requeued (for example an absent key in the batch result).</summary>
    public const int BulkDispatchRequeuingOne = 5373;

    /// <summary>An error occurred while publishing the rate-limit sentinel for a saga.</summary>
    public const int BulkPublishRateLimitSentinelError = 5374;

    /// <summary>A message with an unserializable payload was discarded as poison.</summary>
    public const int PoisonPayloadDiscarded = 5375;

    /// <summary>A stream entry carried a malformed <c>enqueuedAt</c> field; it was treated as absent.</summary>
    public const int MalformedEnqueuedAt = 5376;

    /// <summary>The bulk endpoint returned a deterministic 4xx; each item is being re-enqueued as an individual lookup.</summary>
    public const int BulkBatchRejected = 5377;
}
