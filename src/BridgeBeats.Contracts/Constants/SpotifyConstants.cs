namespace BridgeBeats.Contracts.Constants;

/// <summary>
/// Constants for Spotify bulk lookups: per-batch item caps (mirroring Spotify Web API page limits),
/// rate-limit endpoint labels, and the Redis stream names that carry bulk id work.
/// </summary>
public static class SpotifyConstants {

    /// <summary>
    /// Maximum number of tracks (<c>50</c>) requested in a single bulk track lookup, matching the
    /// Spotify Web API page limit.
    /// </summary>
    public const int MaxTracksPerBatchLookup = 50;

    /// <summary>
    /// Maximum number of albums (<c>20</c>) requested in a single bulk album lookup, matching the
    /// Spotify Web API page limit.
    /// </summary>
    public const int MaxAlbumsPerBatchLookup = 20;

    /// <summary>
    /// Maximum number of artists (<c>50</c>) requested in a single bulk artist lookup, matching the
    /// Spotify Web API page limit.
    /// </summary>
    public const int MaxArtistsPerBatchLookup = 50;

    /// <summary>
    /// Rate-limit endpoint key <c>"BulkTracks"</c>, used to track bulk track lookups separately from
    /// single-track lookups.
    /// </summary>
    public const string BulkTracksEndpoint = "BulkTracks";

    /// <summary>
    /// Rate-limit endpoint key <c>"BulkAlbums"</c>, used to track bulk album lookups separately from
    /// single-album lookups.
    /// </summary>
    public const string BulkAlbumsEndpoint = "BulkAlbums";

    /// <summary>
    /// Rate-limit endpoint key <c>"BulkArtists"</c>, used to track bulk artist lookups separately from
    /// single-artist lookups.
    /// </summary>
    public const string BulkArtistsEndpoint = "BulkArtists";

    // Type-specific bulk stream names

    /// <summary>
    /// Redis stream name <c>"queue:spotify:interactive"</c> for live-user Spotify lookups.
    /// </summary>
    public const string InteractiveStream = "queue:spotify:interactive";

    /// <summary>Redis stream for single-item background Spotify lookups.</summary>
    public const string BackgroundStream = "queue:spotify:background";

    /// <summary>Consumer group shared by Spotify generic and bulk streams.</summary>
    public const string ConsumerGroup = "spotify-workers";

    /// <summary>Redis pub/sub channel that wakes the generic Spotify queue consumer.</summary>
    public const string WorkSignalChannel = "queue:spotify:work";

    /// <summary>Redis pub/sub channel that wakes the Spotify bulk batch consumer.</summary>
    public const string BulkWorkSignalChannel = "queue:spotify:bulk:work";

    /// <summary>
    /// Redis stream name <c>"queue:spotify:bulk:track-id"</c> for <c>SongIdLookup</c> messages routed
    /// by <c>SpotifyBulkQueueDecorator</c> and consumed by <c>SpotifyBulkProcessorService</c>.
    /// </summary>
    /// <remarks>
    /// Using this constant everywhere that reads or writes the stream prevents the silent key-mismatch
    /// failure where a producer and consumer disagree on the stream name and no messages are delivered.
    /// </remarks>
    public const string BulkTrackIdStream = "queue:spotify:bulk:track-id";

    /// <summary>
    /// Redis stream name <c>"queue:spotify:bulk:album-id"</c> for <c>AlbumIdLookup</c> messages routed
    /// by <c>SpotifyBulkQueueDecorator</c> and consumed by <c>SpotifyBulkProcessorService</c>.
    /// </summary>
    public const string BulkAlbumIdStream = "queue:spotify:bulk:album-id";

}
