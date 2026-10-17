using System.Text;
using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Infrastructure.Utilities;
using BridgeBeats.Providers.AppleMusic;
using BridgeBeats.Providers.Spotify;
using BridgeBeats.Providers.Tidal;
using BridgeBeats.Worker.JetStreamWatcher.Logging;
using idunno.AtProto;
using idunno.AtProto.Jetstream;
using idunno.Bluesky;
using idunno.Bluesky.Embed;
using idunno.Bluesky.Feed;
using idunno.Bluesky.RichText;

namespace BridgeBeats.Worker.JetStreamWatcher;

/// <summary>
/// Background consumer of the Bluesky Jetstream (the ATProto firehose). It subscribes to the
/// <c>app.bsky.feed.post</c> and <c>app.bsky.feed.repost</c> collections and, for each newly created
/// record, extracts candidate music links from post facets, embedded external cards, and the
/// subjects of quote posts and reposts (hydrating those via the Bluesky API). Each recognized
/// Spotify, Tidal, or Apple Music link is turned into a <see cref="QueuedLookupRequest"/> with
/// <see cref="QueuePriority.Bulk"/> origin priority and enqueued through the
/// durable dispatch staging component, so firehose-discovered links use the lowest-priority lane
/// and remain recoverable if publication is interrupted. This worker is producer-only: it feeds the
/// queue and never consumes results. The connection is self-healing, reconnecting after a short
/// backoff on failure.
/// </summary>
/// <param name="logger">The logger for connection lifecycle and processing diagnostics.</param>
/// <param name="sagaManager">Creates the fenced saga before its queue delivery is published.</param>
/// <param name="dispatchOutbox">Stages and publishes the provider delivery for crash recovery.</param>
public sealed partial class JetStreamWatcherService(
    ILogger<JetStreamWatcherService> logger,
    ISagaStateManager sagaManager,
    ILookupDispatchOutbox dispatchOutbox
) : BackgroundService {

    /// <summary>
    /// Runs the watcher until cancellation. Wraps the Jetstream connection in an outer reconnect
    /// loop: connection errors are logged and retried after a five-second backoff, while a
    /// cancellation requested during shutdown ends the loop cleanly.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task that completes when the watcher stops.</returns>
    protected override async Task ExecuteAsync( CancellationToken stoppingToken ) {
        Console.OutputEncoding = Encoding.UTF8;
        LogWatcherStarting( logger );
        LogWatchingForLinks( logger );

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await RunJetstreamConnectionAsync( stoppingToken );
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                // Graceful shutdown - expected
                break;
            } catch (Exception ex) {
                LogConnectionError( logger, ex );
                await Task.Delay( TimeSpan.FromSeconds( 5 ), stoppingToken )
                    .ConfigureAwait( ConfigureAwaitOptions.SuppressThrowing );
            }
        }

        LogWatcherStopped( logger );
    }

    /// <summary>
    /// Establishes one Jetstream connection and processes records until cancellation or disconnect.
    /// Subscribes to the post and repost collections, dispatches each created record through
    /// <see cref="HandleCommitRecordAsync"/>, then polls connection state every 500 ms and closes
    /// cleanly when the loop ends. Known benign parser errors are suppressed (see
    /// <see cref="IsExpectedParsingError"/>).
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task that completes when the connection closes.</returns>
    private async Task RunJetstreamConnectionAsync( CancellationToken stoppingToken ) {
        // Create Bluesky agent for fetching posts (no authentication required for public posts)
        using BlueskyAgent blueskyAgent = new( );

        // Create jetstream instance, filtered to watch for Bluesky posts and reposts
        using AtProtoJetstream jetStream = new( collections: ["app.bsky.feed.post", "app.bsky.feed.repost"] );

        jetStream.RecordReceived += async ( sender, e ) => {
            if (e.ParsedEvent is AtJetstreamCommitEvent commitEvent &&
                string.Equals( commitEvent.Commit.Operation, "create", StringComparison.OrdinalIgnoreCase ) &&
                commitEvent.Commit.Record is not null
            ) {
                await HandleCommitRecordAsync( commitEvent.Commit.Record, commitEvent.Commit.Collection.ToString( ), blueskyAgent, stoppingToken );
            }
        };

        // Connect to jetstream
        await jetStream.ConnectAsync( cancellationToken: stoppingToken );
        LogConnected( logger );

        // Keep running until cancellation or disconnect
        while (!stoppingToken.IsCancellationRequested && jetStream.IsConnected) {
            await Task.Delay( 500, stoppingToken ).ConfigureAwait( ConfigureAwaitOptions.SuppressThrowing );
        }

        await jetStream.CloseAsync( cancellationToken: stoppingToken );
        LogDisconnected( logger );
    }

    /// <summary>
    /// Dispatches a single committed record to the appropriate handler based on its collection, skipping
    /// malformed or unrecognized records cleanly. Known benign parsing errors are swallowed silently;
    /// unexpected errors are logged at Warning so they are visible above the file-sink floor.
    /// </summary>
    /// <param name="record">The raw record document from the commit event.</param>
    /// <param name="collection">The collection name (e.g. <c>app.bsky.feed.post</c>).</param>
    /// <param name="blueskyAgent">The agent used to hydrate quoted posts and reposts.</param>
    /// <param name="cancellationToken">Signals host shutdown.</param>
    /// <returns>A task that completes when the record has been processed or skipped.</returns>
    internal async Task HandleCommitRecordAsync(
        JsonDocument record,
        string collection,
        BlueskyAgent blueskyAgent,
        CancellationToken cancellationToken
    ) {
        try {
            if (collection == "app.bsky.feed.post") {
                await ProcessPostAsync( record, blueskyAgent, cancellationToken );
            } else if (collection == "app.bsky.feed.repost") {
                await ProcessRepostAsync( record, blueskyAgent, cancellationToken );
            }
        } catch (JsonException) {
            // Skip records that can't be deserialized - this is expected for some record types
        } catch (OperationCanceledException) {
            // Shutdown in progress
        } catch (Exception ex) {
            // Log unexpected errors but continue processing
            if (!IsExpectedParsingError( ex )) {
                LogRecordProcessingError( logger, ex );
            }
        }
    }

    /// <summary>
    /// Identifies record-parsing exceptions that are expected and benign for firehose traffic (empty
    /// strings, null values, and empty image or feature collections), so they can be swallowed
    /// silently instead of logged.
    /// </summary>
    /// <param name="ex">The exception raised while processing a record.</param>
    /// <returns><see langword="true"/> when the exception is a known, ignorable parsing error.</returns>
    private static bool IsExpectedParsingError( Exception ex ) {
        return ex.Message.StartsWith( "The value cannot be an empty string", StringComparison.Ordinal ) ||
               ex.Message.StartsWith( "Value cannot be null.", StringComparison.Ordinal ) ||
               ex.Message.StartsWith( "images.Count", StringComparison.Ordinal ) ||
               ex.Message.StartsWith( "features.Count", StringComparison.Ordinal );
    }

    /// <summary>
    /// Processes a created <c>app.bsky.feed.post</c> record: deserializes it, extracts links from its
    /// rich-text facets, and inspects its embedded record for an external card or a quoted post
    /// (including the quote-with-media variant), enqueuing any music links found.
    /// </summary>
    /// <param name="record">The raw post record from the commit event.</param>
    /// <param name="blueskyAgent">The agent used to hydrate quoted posts.</param>
    /// <param name="cancellationToken">Signals host shutdown.</param>
    /// <returns>A task that completes when the post has been processed.</returns>
    private async Task ProcessPostAsync(
        JsonDocument record,
        BlueskyAgent blueskyAgent,
        CancellationToken cancellationToken
    ) {
        Post? post = JsonSerializer.Deserialize<Post>(
            record,
            BlueskyServer.BlueskyJsonSerializerOptions
        );

        if (post is null) {
            return;
        }

        // Extract links from facets (inline links in text)
        await ExtractLinksFromFacetsAsync( post.Facets, cancellationToken );

        // Extract links from embedded external content (link cards)
        if (post.EmbeddedRecord is EmbeddedExternal embeddedExternal) {
            await ProcessEmbeddedExternalAsync( embeddedExternal, cancellationToken );
        }

        // Handle quote posts (posts with EmbeddedRecord pointing to another post)
        if (post.EmbeddedRecord is EmbeddedRecord embeddedRecord) {
            await ProcessQuotePostAsync( embeddedRecord, blueskyAgent, cancellationToken );
        }

        // Handle embedded record with media (quote post with images/video)
        if (post.EmbeddedRecord is EmbeddedRecordWithMedia embeddedRecordWithMedia) {
            await ProcessQuotePostAsync( embeddedRecordWithMedia.Record, blueskyAgent, cancellationToken );
        }
    }

    /// <summary>
    /// Processes a created <c>app.bsky.feed.repost</c> record: reads the reposted subject URI,
    /// hydrates the original post via the Bluesky API, and enqueues any music links found in its
    /// facets or embedded external card.
    /// </summary>
    /// <param name="record">The raw repost record from the commit event.</param>
    /// <param name="blueskyAgent">The agent used to fetch the reposted post.</param>
    /// <param name="cancellationToken">Signals host shutdown.</param>
    /// <returns>A task that completes when the repost has been processed.</returns>
    private async Task ProcessRepostAsync(
        JsonDocument record,
        BlueskyAgent blueskyAgent,
        CancellationToken cancellationToken
    ) {
        if (!record.RootElement.TryGetProperty( "subject", out JsonElement repostSubject )) {
            return;
        }

        if (repostSubject.ValueKind != JsonValueKind.Object) {
            return;
        }

        if (!repostSubject.TryGetProperty( "uri", out JsonElement uriElement )) {
            return;
        }

        if (uriElement.ValueKind != JsonValueKind.String) {
            return;
        }

        string repostedUriString = uriElement.GetString( ) ?? string.Empty;
        if (string.IsNullOrEmpty( repostedUriString )) {
            return;
        }

        // Fetch the original post to extract links
        AtUri repostedUri = new( repostedUriString );
        AtProtoHttpResult<PostView> postResult = await blueskyAgent.GetPostView( repostedUri, cancellationToken: cancellationToken );

        if (postResult.Succeeded && postResult.Result is not null) {
            PostView postView = postResult.Result;

            // Extract links from the original post's facets
            await ExtractLinksFromFacetsAsync( postView.Record.Facets, cancellationToken );

            // Extract links from embedded external content
            if (postView.Record.EmbeddedRecord is EmbeddedExternal embeddedExternal) {
                await ProcessEmbeddedExternalAsync( embeddedExternal, cancellationToken );
            }
        }
    }

    /// <summary>
    /// Processes the post quoted by an embedded record: hydrates the quoted post via the Bluesky API
    /// and enqueues any music links found in its facets or embedded external card.
    /// </summary>
    /// <param name="embeddedRecord">The embedded record pointing at the quoted post.</param>
    /// <param name="blueskyAgent">The agent used to fetch the quoted post.</param>
    /// <param name="cancellationToken">Signals host shutdown.</param>
    /// <returns>A task that completes when the quoted post has been processed.</returns>
    private async Task ProcessQuotePostAsync(
        EmbeddedRecord embeddedRecord,
        BlueskyAgent blueskyAgent,
        CancellationToken cancellationToken
    ) {
        if (embeddedRecord.Record?.Uri is null) {
            return;
        }

        // Fetch the quoted post to extract links
        AtProtoHttpResult<PostView> postResult = await blueskyAgent.GetPostView( embeddedRecord.Record.Uri, cancellationToken: cancellationToken );

        if (postResult.Succeeded && postResult.Result is not null) {
            PostView postView = postResult.Result;

            // Extract links from the quoted post's facets
            await ExtractLinksFromFacetsAsync( postView.Record.Facets, cancellationToken );

            // Extract links from embedded external content
            if (postView.Record.EmbeddedRecord is EmbeddedExternal embeddedExternal) {
                await ProcessEmbeddedExternalAsync( embeddedExternal, cancellationToken );
            }
        }
    }

    /// <summary>
    /// Scans a post's rich-text facets and enqueues each HTTPS link feature found. Does nothing when
    /// the facet collection is <see langword="null"/>.
    /// </summary>
    /// <param name="facets">The post's facets, or <see langword="null"/> when it has none.</param>
    /// <param name="cancellationToken">Signals host shutdown.</param>
    /// <returns>A task that completes when all facets have been scanned.</returns>
    private async Task ExtractLinksFromFacetsAsync(
        ICollection<Facet>? facets,
        CancellationToken cancellationToken
    ) {
        if (facets is null) {
            return;
        }

        foreach (Facet facet in facets) {
            foreach (FacetFeature feature in facet.Features) {
                if (feature is LinkFacetFeature linkFeature &&
                    linkFeature.Uri is not null &&
                    linkFeature.Uri.ToString( ).StartsWith( "https://", StringComparison.OrdinalIgnoreCase )
                ) {
                    string link = linkFeature.Uri.ToString( );
                    await EnqueueMusicLinkAsync( link, cancellationToken );
                }
            }
        }
    }

    /// <summary>
    /// Enqueues the link from an embedded external card (the link-preview card on a post) when its
    /// URI is an HTTPS link.
    /// </summary>
    /// <param name="embeddedExternal">The embedded external card.</param>
    /// <param name="cancellationToken">Signals host shutdown.</param>
    /// <returns>A task that completes when the card has been processed.</returns>
    private async Task ProcessEmbeddedExternalAsync(
        EmbeddedExternal embeddedExternal,
        CancellationToken cancellationToken
    ) {
        if (embeddedExternal.External.Uri.ToString( ).StartsWith( "https://", StringComparison.OrdinalIgnoreCase )) {
            string link = embeddedExternal.External.Uri.ToString( );
            await EnqueueMusicLinkAsync( link, cancellationToken );
        }
    }

    /// <summary>
    /// Normalizes a candidate link, identifies its provider and lookup type, derives the lookup key
    /// and deterministic saga id (via <see cref="LookupKeyBuilder"/> and
    /// <see cref="ISagaStateManager.GenerateSagaId"/>), and enqueues a
    /// <see cref="QueuedLookupRequest"/> with <see cref="QueuePriority.Bulk"/> origin priority to the
    /// resolved provider queue. Links that match no provider are ignored. Enqueue failures are logged
    /// and swallowed so one bad link does not interrupt the firehose.
    /// </summary>
    /// <param name="link">The raw candidate link discovered in a post.</param>
    /// <param name="cancellationToken">Signals host shutdown.</param>
    /// <returns>A task that completes once the link has been enqueued or skipped.</returns>
    internal async Task EnqueueMusicLinkAsync( string link, CancellationToken cancellationToken ) {
        // Normalize link by removing query parameters (except Apple Music song IDs)
        string normalizedLink = NormalizeMusicLink( link );

        // Try to identify and enqueue to the appropriate provider queue
        (SupportedProviders? provider, LookupRequestType lookupType, bool isAlbum, string lookupValue) =
            await IdentifyProviderAsync( normalizedLink );

        if (provider is null) {
            // Not a recognized music link - skip silently
            return;
        }

        // Build the saga ID using LookupKeyBuilder — the single source of truth for key
        // shape. All producers (orchestrator, queue workers, JetStream watcher) consume
        // this builder so keys for the same entity are always identical, enabling proper
        // deduplication and cross-producer saga state sharing.
        string lookupKey = lookupType is LookupRequestType.SongIdLookup or LookupRequestType.AlbumIdLookup
            ? LookupKeyBuilder.TypedKey( lookupType, provider.Value, lookupValue )
            : LookupKeyBuilder.UrlKey( lookupValue );
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );
        LookupSagaState saga = await sagaManager.GetOrCreateAsync(
            sagaId, lookupKey, lookupType, lookupValue, QueuePriority.Bulk, cancellationToken );

        // Create the lookup request with a saga ID for coordinating cross-provider lookups.
        // Bulk origin priority is persisted into the saga so secondary lookups spawned by
        // the coordinator stay out of the interactive lane.
        QueuedLookupRequest request = new( ) {
            RequestId = Guid.NewGuid( ).ToString( "N" ),
            Provider = provider.Value,
            LookupType = lookupType,
            LookupValue = lookupValue,
            SagaId = sagaId,
            SagaInstanceToken = saga.InstanceToken,
            IsAlbum = isAlbum,
            OriginPriority = QueuePriority.Bulk
        };

        // Stage before publication so a process interruption cannot leave the saga without work.
        // The dispatch component applies the same Spotify type-specific bulk routing as the queue
        // decorator and the coordinator relay retries any interrupted immediate publication.
        try {
            ProviderDispatchStageOutcome outcome = await dispatchOutbox.StageAsync(
                request, QueuePriority.Bulk, cancellationToken );
            if (outcome == ProviderDispatchStageOutcome.Staged) {
                _ = await dispatchOutbox.DispatchAsync(
                    sagaId, provider.Value, cancellationToken );
            }
        } catch (Exception ex) {
            LogEnqueueError( logger, ex, normalizedLink );
        }
    }

    /// <summary>
    /// Strips tracking query parameters from a link by truncating at the first ampersand. Apple Music
    /// links are returned unchanged because their query string is significant to identification.
    /// </summary>
    /// <param name="link">The raw link to normalize.</param>
    /// <returns>The normalized link.</returns>
    private static string NormalizeMusicLink( string link ) {
        // For most links, remove everything after &
        // Exception: Apple Music links need to preserve ?i= for song IDs within albums
        if (link.Contains( "music.apple.com", StringComparison.OrdinalIgnoreCase )) {
            // Keep ?i= query parameter for Apple Music song IDs
            return link;
        }

        // Strip tracking/share parameters from other providers
        int ampersandIndex = link.IndexOf( '&' );
        return ampersandIndex > 0 ? link[..ampersandIndex] : link;
    }

    /// <summary>
    /// Identifies which provider a link belongs to and how it should be looked up. The checks run in
    /// a deliberate order: <c>spotify.link</c> short links are matched first (before the generic
    /// Spotify parser, which cannot resolve them), then Spotify track/album ids, then Tidal, then
    /// Apple Music. Spotify track and album links resolve to id lookups
    /// (<see cref="LookupRequestType.SongIdLookup"/> / <see cref="LookupRequestType.AlbumIdLookup"/>);
    /// all other matches resolve to a <see cref="LookupRequestType.UriLookup"/>.
    /// </summary>
    /// <param name="link">The normalized candidate link.</param>
    /// <returns>
    /// A tuple of the matched <see cref="SupportedProviders"/> (or <see langword="null"/> when no
    /// provider matched), the <see cref="LookupRequestType"/> to use, whether the item is an album,
    /// and the lookup value (an id for id lookups, otherwise the normalized URL).
    /// </returns>
    internal static async Task<(SupportedProviders? provider, LookupRequestType lookupType, bool isAlbum, string lookupValue)> IdentifyProviderAsync(
        string link
    ) {
        string normalizedLink = LinkNormalizer.Normalize( link );

        // Check Spotify (including short links)
        if (normalizedLink.Contains( "spotify.link", StringComparison.OrdinalIgnoreCase )) {
            // Short link — the Spotify worker resolves these via HTTP redirect; keep as UriLookup
            return (SupportedProviders.Spotify, LookupRequestType.UriLookup, false, link);
        }

        (bool success, SpotifyEntity kind, string id) = await SpotifyLinkParser.TryParseUriAsync( link );
        if (success && !string.IsNullOrEmpty( id )) {
            // Track and album links carry a resolvable Spotify ID. Emit typed ID lookups so
            // the SpotifyBulkQueueDecorator routes them into the type-specific bulk streams
            // (queue:spotify:bulk:track-id / queue:spotify:bulk:album-id) where
            // SpotifyBulkProcessorService can aggregate and flush them in batch API calls.
            // Prerelease links are not supported by the batch API and remain as UriLookup so
            // the generic worker handles them. Artist and playlist links are not recognised
            // by SpotifyLinkParser (the regex does not capture them) and are therefore dropped
            // entirely — neither entity type is processed anywhere in the system.
            if (kind is SpotifyEntity.Track or SpotifyEntity.Album) {
                bool isAlbum = kind == SpotifyEntity.Album;
                LookupRequestType lookupType = isAlbum ? LookupRequestType.AlbumIdLookup : LookupRequestType.SongIdLookup;
                return (SupportedProviders.Spotify, lookupType, isAlbum, id);
            }

            // Prerelease: resolve via the generic URI path.
            // Use normalizedLink so the lookupValue matches the Tidal/Apple branches above;
            // HashUtility.HashUrl (used downstream for the saga ID) normalizes again, so both
            // `link` and `normalizedLink` would produce the same hash — but being explicit here
            // avoids any future divergence if the calling site changes.
            return (SupportedProviders.Spotify, LookupRequestType.UriLookup, false, normalizedLink);
        }

        // Check Tidal
        if (TidalLinkParser.TryParseUri( normalizedLink, out TidalEntity tidalKind, out string tidalId ) &&
            !string.IsNullOrEmpty( tidalId )
        ) {
            bool isAlbum = tidalKind == TidalEntity.Album;
            return (SupportedProviders.Tidal, LookupRequestType.UriLookup, isAlbum, normalizedLink);
        }

        // Check Apple Music
        if (AppleMusicLinkParser.TryParseUri( normalizedLink, out _, out _, out bool appleIsAlbum )) {
            return (SupportedProviders.AppleMusic, LookupRequestType.UriLookup, appleIsAlbum, normalizedLink);
        }

        return (null, default, false, link);
    }

    #region LoggerMessage Methods

    /// <summary>Logs that the watcher service is starting.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.WatcherStarting,
        Level = LogLevel.Information,
        Message = "BridgeBeats Jetstream Watcher starting..." )]
    private static partial void LogWatcherStarting( ILogger logger );

    /// <summary>Logs that the watcher has begun scanning for music links.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.WatchingForLinks,
        Level = LogLevel.Information,
        Message = "Watching for music links in Bluesky posts, reposts, and quote posts." )]
    private static partial void LogWatchingForLinks( ILogger logger );

    /// <summary>Logs a Jetstream connection error that will trigger a reconnect.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The connection exception.</param>
    [LoggerMessage(
        EventId = LogEventIds.ConnectionError,
        Level = LogLevel.Error,
        Message = "Jetstream connection error. Reconnecting in 5 seconds..." )]
    private static partial void LogConnectionError( ILogger logger, Exception ex );

    /// <summary>Logs that the watcher has stopped.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.WatcherStopped,
        Level = LogLevel.Information,
        Message = "JetStream Watcher stopped." )]
    private static partial void LogWatcherStopped( ILogger logger );

    /// <summary>Logs an unexpected error while processing a single Jetstream record.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The processing exception.</param>
    [LoggerMessage(
        EventId = LogEventIds.RecordProcessingError,
        Level = LogLevel.Warning,
        Message = "Error processing Jetstream record" )]
    private static partial void LogRecordProcessingError( ILogger logger, Exception ex );

    /// <summary>Logs that the watcher has connected to the Jetstream firehose.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.Connected,
        Level = LogLevel.Information,
        Message = "Connected to Jetstream." )]
    private static partial void LogConnected( ILogger logger );

    /// <summary>Logs that the watcher has disconnected from the Jetstream firehose.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.Disconnected,
        Level = LogLevel.Information,
        Message = "Disconnected from Jetstream." )]
    private static partial void LogDisconnected( ILogger logger );

    /// <summary>Logs a failure to enqueue a discovered music link.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The enqueue exception.</param>
    /// <param name="link">The link that could not be enqueued.</param>
    [LoggerMessage(
        EventId = LogEventIds.EnqueueError,
        Level = LogLevel.Debug,
        Message = "Failed to enqueue music link: {Link}" )]
    private static partial void LogEnqueueError( ILogger logger, Exception ex, string link );

    #endregion
}
