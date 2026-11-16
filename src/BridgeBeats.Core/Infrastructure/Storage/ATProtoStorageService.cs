using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Logging;
using BridgeBeats.Core.Infrastructure.Storage.Car;
using BridgeBeats.Core.Infrastructure.Utilities;
using idunno.AtProto;
using idunno.AtProto.Repo;
using idunno.Bluesky;

namespace BridgeBeats.Core.Infrastructure.Storage;

/// <summary>
/// Stores, retrieves, and lists BridgeBeats media-link records on an atproto Personal Data Server
/// (PDS), the system of record for these results. Records are stored as custom
/// <c>link.bridgebeats.lookup</c> lexicon records via the idunno.Bluesky library.
/// </summary>
/// <remarks>
/// Single-record writes go through the service account's authenticated agent (supplied by the
/// session manager, which manages authentication centrally to reduce PDS API calls) and reads parse
/// an AT-URI and fetch the record. Listing the whole collection is done by downloading the entire
/// repository as a single CAR v1 file (<c>com.atproto.sync.getRepo</c>) and walking it locally,
/// which replaces paginated <c>listRecords</c> calls and reduces N/100 round-trips to a single HTTP
/// request. Records are converted between the public <see cref="MediaLinkResult"/> shape and the
/// on-PDS <c>MediaLinkResultRecord</c> shape on the way in and out.
/// </remarks>
/// <param name="sessionManager">The centralized session manager for authentication.</param>
/// <param name="logger">Logger for diagnostic information.</param>
/// <param name="httpClientFactory">Factory for creating named HTTP clients.</param>
/// <param name="carCacheTtl">
/// Time-to-live for the in-process full-collection CAR result cache. Defaults to no caching
/// (<see cref="TimeSpan.Zero"/>) when unset; the 5-minute production default is applied by
/// <c>AddATProtoStorage</c>.
/// </param>
public partial class ATProtoStorageService(
    IATProtoSessionManager sessionManager,
    ILogger<ATProtoStorageService> logger,
    IHttpClientFactory httpClientFactory,
    TimeSpan carCacheTtl = default
) : IATProtoStorageService {

    /// <summary>
    /// Name of the configured <see cref="System.Net.Http.IHttpClientFactory"/> client used to stream
    /// repository CAR downloads (com.atproto.sync.getRepo) from the PDS sync endpoint.
    /// </summary>
    internal const string ATProtoSyncHttpClientName = "atproto-sync";

    /// <summary>
    /// The collection NSID (<c>link.bridgebeats.lookup</c>) under which media-link records are stored.
    /// </summary>
    private static readonly Nsid s_mediaLinkResultCollection = new( "link.bridgebeats.lookup" );

    /// <summary>
    /// Lazily built, case-insensitive map from provider name variants (enum name, description, and the
    /// description without spaces) to the <see cref="SupportedProviders"/> value, used when reading
    /// records back. Initialized once for O(1) lookups.
    /// </summary>
    private static readonly Lazy<Dictionary<string, SupportedProviders>> s_providerStringToEnum = new( CreateProviderMappings );

    // Single-flight + TTL cache for the full-collection CAR read. Mirrors the in-process
    // single-flight in RedisATProtoSessionManager (_agentLock / WaitAsync / double-check / finally Release).
    private readonly SemaphoreSlim _carCacheLock = new( 1, 1 );
    private List<(string AtUri, MediaLinkResult Result)>? _cachedCarRecords; // null = never populated / evicted
    private DateTimeOffset _cachedCarTimestamp;                              // UTC stamp of the last accepted populate
    private Dictionary<string, string> _cachedRecordCids = new( StringComparer.Ordinal );

    /// <summary>
    /// Stores a media-link result on the PDS, upserting under a deterministic record key.
    /// </summary>
    /// <param name="result">The result to store.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The AT-URI of the written record.</returns>
    /// <remarks>
    /// The record key is derived deterministically from <paramref name="result"/>, so this both
    /// creates and updates: a <c>PutRecord</c> is attempted first and a <c>CreateRecord</c> is used as
    /// a fallback. Server-side validation is disabled because the collection uses a custom lexicon.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the PDS rejects the create fallback.</exception>
    public async Task<string> StoreMediaLinkResultAsync( MediaLinkResult result, CancellationToken cancellationToken = default ) {
        BlueskyAgent agent = await sessionManager.GetAuthenticatedAgentAsync( cancellationToken );

        try {
            // Generate deterministic rkey based on externalId or metadata
            string rkey = RecordKeyGenerator.GenerateRkey( result );

            // Convert MediaLinkResult DTO to custom record
            MediaLinkResultRecord record = ConvertToRecord( result );

            // Try to update existing record first (upsert logic)
            RecordKey recordKey = new( rkey );
            AtProtoHttpResult<PutRecordResult> putResult = await agent.PutRecord(
                record: record,
                collection: s_mediaLinkResultCollection,
                rKey: recordKey,
                validate: false, // PDS Resolution not enabled yet
                cancellationToken: cancellationToken
            );

            if (putResult.Succeeded && putResult.Result is not null) {
                string uri = putResult.Result.Uri.ToString( );
                await CacheRecordCidAsync( uri, putResult.Result.Cid.ToString( ), cancellationToken );
                if (logger.IsEnabled( LogLevel.Information )) {
                    LogUpdatedRecord( logger, uri );
                }
                return uri;
            }

            // If update failed, try to create new record
            AtProtoHttpResult<CreateRecordResult> createResult = await agent.CreateRecord(
                record: record,
                collection: s_mediaLinkResultCollection,
                rKey: recordKey,
                validate: false, // PDS Resolution not enabled yet
                cancellationToken: cancellationToken
            );

            if (!createResult.Succeeded || createResult.Result is null) {
                string errorMsg = createResult.AtErrorDetail?.Message ?? $"HTTP {createResult.StatusCode}";
                throw new InvalidOperationException( $"Failed to create record on ATProto PDS: {errorMsg}" );
            }

            string recordUri = createResult.Result.Uri.ToString( );
            await CacheRecordCidAsync( recordUri, createResult.Result.Cid.ToString( ), cancellationToken );
            if (logger.IsEnabled( LogLevel.Information )) {
                LogCreatedRecord( logger, recordUri );
            }

            return recordUri;
        } catch (Exception ex) {
            LogStoreError( logger, ex );
            throw;
        }
    }

    /// <summary>
    /// Retrieves and converts a single media-link record from the PDS by its AT-URI.
    /// </summary>
    /// <param name="recordUri">The AT-URI of the record to fetch.</param>
    /// <returns>
    /// The converted <see cref="MediaLinkResult"/>, or <see langword="null"/> when the record is not
    /// found, the fetch fails, the record has no valid providers, or an exception occurs.
    /// </returns>
    public async Task<MediaLinkResult?> GetMediaLinkResultAsync( string recordUri ) {
        BlueskyAgent agent = await sessionManager.GetAuthenticatedAgentAsync( );

        try {
            // Parse the AT-URI
            AtUri atUri = new( recordUri );

            // Get the record from ATProto PDS
            AtProtoHttpResult<AtProtoRepositoryRecord<MediaLinkResultRecord>> getRecordResult = await agent.GetRecord<MediaLinkResultRecord>(
                uri: atUri,
                cid: null
            );

            if (getRecordResult.Succeeded && getRecordResult.Result?.Value is not null) {
                // Convert the custom record back to MediaLinkResult DTO
                return ConvertFromRecord( getRecordResult.Result.Value );
            } else if (!getRecordResult.Succeeded) {
                string errorMsg = (getRecordResult.AtErrorDetail?.Message ?? $"HTTP {getRecordResult.StatusCode}").SanitizeForLogging( );
                LogGetRecordFailed( logger, errorMsg );
            }

            return null;
        } catch (Exception ex) {
            LogRetrieveError( logger, ex, recordUri );
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GetMediaLinkRecordCidAsync(
        string recordUri,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( recordUri );

        await _carCacheLock.WaitAsync( cancellationToken );
        try {
            if (_cachedRecordCids.TryGetValue( recordUri, out string? cachedCid )) {
                return cachedCid;
            }
        } finally {
            _ = _carCacheLock.Release( );
        }

        BlueskyAgent agent = await sessionManager.GetAuthenticatedAgentAsync( cancellationToken );
        AtUri atUri = new( recordUri );
        AtProtoHttpResult<AtProtoRepositoryRecord<MediaLinkResultRecord>> result =
            await agent.GetRecord<MediaLinkResultRecord>(
                uri: atUri,
                cid: null,
                cancellationToken: cancellationToken
            );

        if (result.Succeeded && result.Result is not null) {
            string cid = result.Result.Cid.ToString( );
            await CacheRecordCidAsync( recordUri, cid, cancellationToken );
            return cid;
        }

        return result.StatusCode == HttpStatusCode.NotFound
            || string.Equals( result.AtErrorDetail?.Error, "RecordNotFound", StringComparison.Ordinal )
                ? null
                : throw new InvalidOperationException(
                    $"Failed to read record CID from ATProto PDS: {(result.AtErrorDetail?.Message ?? $"HTTP {result.StatusCode}").SanitizeForLogging( )}" );
    }

    /// <inheritdoc/>
    public async Task<MediaLinkDeleteOutcome> DeleteMediaLinkResultAsync(
        string recordUri,
        string expectedCid,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( recordUri );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedCid );

        BlueskyAgent agent = await sessionManager.GetAuthenticatedAgentAsync( cancellationToken );
        AtUri atUri = new( recordUri );
        if (atUri.Collection != s_mediaLinkResultCollection || atUri.RecordKey is null) {
            throw new ArgumentException( "Record URI is not a BridgeBeats media-link record.", nameof( recordUri ) );
        }
        if (agent.Did is null
            || !string.Equals( atUri.Authority.ToString( ), agent.Did.ToString( ), StringComparison.Ordinal )) {
            throw new ArgumentException(
                "Record URI does not belong to the authenticated ATProto repository.",
                nameof( recordUri ) );
        }

        var deleteResult = await agent.DeleteRecord(
            collection: s_mediaLinkResultCollection,
            rKey: atUri.RecordKey,
            swapRecord: new idunno.AtProto.Cid( expectedCid ),
            swapCommit: null,
            serviceProxy: null,
            cancellationToken: cancellationToken
        );

        if (!deleteResult.Succeeded) {
            if (deleteResult.StatusCode == HttpStatusCode.NotFound
                || string.Equals( deleteResult.AtErrorDetail?.Error, "RecordNotFound", StringComparison.Ordinal )) {
                return MediaLinkDeleteOutcome.NotFound;
            }

            if (deleteResult.StatusCode == HttpStatusCode.Conflict
                || string.Equals( deleteResult.AtErrorDetail?.Error, "InvalidSwap", StringComparison.Ordinal )) {
                return MediaLinkDeleteOutcome.RevisionConflict;
            }

            string error = (deleteResult.AtErrorDetail?.Message ?? $"HTTP {deleteResult.StatusCode}")
                .SanitizeForLogging( );
            throw new InvalidOperationException( $"Failed to delete record from ATProto PDS: {error}" );
        }

        await _carCacheLock.WaitAsync( cancellationToken );
        try {
            _cachedCarRecords = null;
            _cachedCarTimestamp = default;
            _ = _cachedRecordCids.Remove( recordUri );
        } finally {
            _ = _carCacheLock.Release( );
        }

        return MediaLinkDeleteOutcome.Deleted;
    }

    private async Task CacheRecordCidAsync(
        string recordUri,
        string cid,
        CancellationToken cancellationToken
    ) {
        await _carCacheLock.WaitAsync( cancellationToken );
        try {
            _cachedRecordCids[recordUri] = cid;
        } finally {
            _ = _carCacheLock.Release( );
        }
    }

    /// <summary>
    /// Converts the public <see cref="MediaLinkResult"/> into the on-PDS record shape, mapping each
    /// provider entry and stamping the lookup time.
    /// </summary>
    /// <param name="result">The result to convert.</param>
    /// <returns>The record ready to write to the PDS.</returns>
    /// <remarks>Input links are not included in the PDS record for user privacy; they are tracked only in SQLite.</remarks>
    private static MediaLinkResultRecord ConvertToRecord( MediaLinkResult result ) {
        List<ProviderResultRecord> providerResults = [];

        foreach ((SupportedProviders provider, MusicLookupResult lookupResult) in result.Results) {
            string providerName = provider switch {
                SupportedProviders.AppleMusic => "appleMusic",
                SupportedProviders.Spotify => "spotify",
                SupportedProviders.Tidal => "tidal",
                _ => provider.ToString( ).ToLowerInvariant( )
            };

            providerResults.Add( new ProviderResultRecord(
                provider: providerName,
                artist: lookupResult.Artist,
                title: lookupResult.Title,
                url: lookupResult.URL,
                marketRegion: lookupResult.MarketRegion,
                externalId: string.IsNullOrEmpty( lookupResult.ExternalId ) ? null : lookupResult.ExternalId,
                artUrl: string.IsNullOrEmpty( lookupResult.ArtUrl ) ? null : lookupResult.ArtUrl,
                isAlbum: lookupResult.IsAlbum
            ) );
        }

        return new MediaLinkResultRecord(
            results: providerResults,
            lookedUpAt: DateTimeOffset.UtcNow
        );
    }

    /// <summary>
    /// Converts an on-PDS record back into the public <see cref="MediaLinkResult"/>, mapping each
    /// provider name to its <see cref="SupportedProviders"/> value and skipping unrecognized providers.
    /// </summary>
    /// <param name="record">The record read from the PDS.</param>
    /// <returns>The converted result, or <see langword="null"/> when no provider entries could be mapped.</returns>
    /// <remarks>Input links are not stored in PDS records, only provider results.</remarks>
    private static MediaLinkResult? ConvertFromRecord( MediaLinkResultRecord record ) {
        MediaLinkResult result = new( ) {
            LookedUpAt = record.LookedUpAt.UtcDateTime
        };

        foreach (ProviderResultRecord providerResult in record.Results) {
            // Try to parse provider using consistent logic, skip if unknown
            if (!TryParseProvider( providerResult.Provider, out SupportedProviders provider )) {
                continue;
            }

            result.Results.Add( provider, new MusicLookupResult {
                Artist = providerResult.Artist,
                Title = providerResult.Title,
                ExternalId = providerResult.ExternalId ?? string.Empty,
                URL = providerResult.Url,
                ArtUrl = providerResult.ArtUrl ?? string.Empty,
                MarketRegion = providerResult.MarketRegion,
                IsAlbum = providerResult.IsAlbum
            } );
        }

        // Return null if no valid providers were parsed - prevents downstream errors
        if (result.Results.Count == 0) {
            return null;
        }

        // Input links are tracked only in SQLite, not in PDS records
        return result;
    }

    /// <summary>
    /// Builds the case-insensitive provider-name lookup map for <see cref="s_providerStringToEnum"/>,
    /// registering each <see cref="SupportedProviders"/> value under its enum name, its description
    /// attribute, and the description with spaces removed.
    /// </summary>
    /// <returns>The populated, case-insensitive name-to-enum map.</returns>
    private static Dictionary<string, SupportedProviders> CreateProviderMappings( ) {
        Dictionary<string, SupportedProviders> dict = new( StringComparer.OrdinalIgnoreCase );

        foreach (SupportedProviders p in Enum.GetValues<SupportedProviders>( )) {
            // Add enum name (e.g., "Spotify")
            dict[p.ToString( )] = p;

            // Add description (e.g., "Apple Music")
            string description = GetEnumDescription( p );
            dict[description] = p;

            // Add description without spaces (e.g., "AppleMusic")
            string descriptionNoSpaces = description.Replace( " ", "", StringComparison.Ordinal );
            if (descriptionNoSpaces != description) {
                dict[descriptionNoSpaces] = p;
            }
        }

        return dict;
    }

    /// <summary>
    /// Returns the <see cref="DescriptionAttribute"/> text for an enum value, or the value's name when
    /// no description attribute is present.
    /// </summary>
    /// <typeparam name="T">The enum type.</typeparam>
    /// <param name="enumValue">The enum value whose description is sought.</param>
    /// <returns>The description text, the value name as a fallback, or an empty string when the value has no name.</returns>
    private static string GetEnumDescription<T>( T enumValue ) where T : struct, Enum {
        string? value = enumValue.ToString( );
        if (value != null) {
            MemberInfo[] memberInfo = typeof( T ).GetMember( value );
            if (memberInfo.Length > 0) {
                object[] attrs = memberInfo[0].GetCustomAttributes( typeof( DescriptionAttribute ), false );
                if (attrs.Length > 0) {
                    return ((DescriptionAttribute)attrs[0]).Description;
                }
            }
            return value;
        }
        return string.Empty;
    }

    /// <summary>
    /// Attempts to map a provider name string to a <see cref="SupportedProviders"/> value using the
    /// cached case-insensitive lookup map (O(1) lookup).
    /// </summary>
    /// <param name="providerString">The provider name to resolve.</param>
    /// <param name="provider">On success, the resolved provider; otherwise the default value.</param>
    /// <returns><see langword="true"/> when the name resolved to a known provider; otherwise <see langword="false"/>.</returns>
    private static bool TryParseProvider( string providerString, out SupportedProviders provider ) {
        provider = default;

        return !string.IsNullOrWhiteSpace( providerString )
            && s_providerStringToEnum.Value.TryGetValue( providerString, out provider );
    }

    /// <summary>
    /// Lists every media-link record in a repository as <c>(AT-URI, result)</c> pairs by downloading
    /// and walking the repository CAR.
    /// </summary>
    /// <param name="pdsUri">The base URI of the PDS hosting the repository.</param>
    /// <param name="userDid">The repository owner's DID; validated before use.</param>
    /// <param name="cancellationToken">A token observed throughout the download, walk, and enumeration.</param>
    /// <param name="forceRefresh">
    /// When <see langword="true"/>, bypasses the TTL cache and performs one unconditional download
    /// under the single-flight lock. Use for admin-triggered manual refreshes that must fetch current
    /// data regardless of cache age.
    /// </param>
    /// <returns>An async sequence of <c>(AT-URI, result)</c> pairs for the lookup collection.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="userDid"/> is non-empty but malformed.</exception>
    public async IAsyncEnumerable<(string AtUri, MediaLinkResult Result)> ListAllRecordsAsync(
        Uri pdsUri,
        string userDid,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        bool forceRefresh = false
    ) {
        // Validate DID format before using it
        ATProtoUriHelper.ValidateDid( userDid, nameof( userDid ) );

        // Fetch all records via a single CAR download. Failures propagate so callers
        // can preserve the last known good cache rather than caching a zero-record result.
        List<(string AtUri, MediaLinkResult Result)> records =
            await FetchAllViaCarAsync( pdsUri, userDid, cancellationToken, forceRefresh );

        foreach ((string atUri, MediaLinkResult result) in records) {
            cancellationToken.ThrowIfCancellationRequested( );
            yield return (atUri, result);
        }
    }

    /// <summary>Buffer size, in bytes, used when streaming the CAR download.</summary>
    private const int ChunkSize = 81920; // 80 KB read buffer

    /// <summary>
    /// Freshness predicate evaluated against the cached timestamp. Authoritative only when
    /// evaluated under <see cref="_carCacheLock"/>; the pre-lock fast read is an optimization only.
    /// </summary>
    private bool IsCarCacheFresh( )
        => _cachedCarRecords is not null
           && DateTimeOffset.UtcNow - _cachedCarTimestamp < carCacheTtl;

    /// <summary>
    /// Serves the full-collection CAR result from the in-process cache when fresh, or performs one
    /// single-flight download (retry-wrapped) on a miss/expiry/force to populate it.
    /// </summary>
    /// <param name="pdsUri">The base URI of the PDS hosting the repository.</param>
    /// <param name="userDid">The repository owner's DID.</param>
    /// <param name="cancellationToken">A token observed during download and enumeration.</param>
    /// <param name="forceRefresh">
    /// When <see langword="true"/>, bypasses both freshness checks and performs one unconditional
    /// download under the lock — used for admin-triggered manual refreshes.
    /// </param>
    /// <returns>The list of <c>(AT-URI, result)</c> pairs for valid records in the collection.</returns>
    /// <exception cref="CarParseException">Thrown when the CAR download is truncated or the CAR fails to parse.</exception>
    private async Task<List<(string AtUri, MediaLinkResult Result)>> FetchAllViaCarAsync(
        Uri pdsUri,
        string userDid,
        CancellationToken cancellationToken,
        bool forceRefresh = false
    ) {
        // Cooperative-shutdown early exit: mirrors the ThrowIfCancellationRequested pattern
        // in DownloadAndParseCarAsync and ensures a pre-cancelled token throws OperationCanceledException
        // rather than TaskCanceledException from WaitAsync.
        cancellationToken.ThrowIfCancellationRequested( );

        // Pre-lock fast read — optimization only; a forced refresh always acquires the lock.
        if (!forceRefresh && IsCarCacheFresh( )) {
            LogCarCacheHit( logger, false );
            return _cachedCarRecords!;
        }

        await _carCacheLock.WaitAsync( cancellationToken );
        try {
            // Authoritative under-lock re-check: a concurrent consumer may have populated
            // while we waited, so serve its result without a second download.
            if (!forceRefresh && IsCarCacheFresh( )) {
                LogCarCacheHit( logger, true );
                return _cachedCarRecords!;
            }

            bool hadExistingEntry = _cachedCarRecords is not null;
            LogCarCacheMiss( logger, forceRefresh );
            LogCarCacheRefreshStart( logger );

            (List<(string AtUri, MediaLinkResult Result)> fresh, Dictionary<string, string> freshRecordCids) =
                await DownloadAndParseCarWithRetryAsync( pdsUri, userDid, cancellationToken );

            // Evict log: only when replacing a previously-expired entry, not on cold first populate.
            if (hadExistingEntry) {
                LogCarCacheEvict( logger );
            }

            _cachedCarRecords = fresh;
            _cachedCarTimestamp = DateTimeOffset.UtcNow;
            _cachedRecordCids = freshRecordCids;
            LogCarCacheRefreshComplete( logger, fresh.Count, _cachedCarTimestamp );

            return fresh;
        } finally {
            _ = _carCacheLock.Release( );
        }
    }

    /// <summary>
    /// Bounded retry loop: up to 3 total attempts to download and parse the CAR. Retries on
    /// CarParseException, HttpRequestException, and HttpClient-deadline OperationCanceledException.
    /// Caller-token cancellation propagates immediately and is never retried.
    /// </summary>
    private async Task<(List<(string AtUri, MediaLinkResult Result)> Records, Dictionary<string, string> RecordCids)>
        DownloadAndParseCarWithRetryAsync(
        Uri pdsUri,
        string userDid,
        CancellationToken cancellationToken
    ) {
        for (int attempt = 1; attempt <= 3; attempt++) {
            try {
                return await DownloadAndParseCarAsync( pdsUri, userDid, cancellationToken );
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                // Cooperative shutdown — never retried.
                throw;
            } catch (Exception ex) when (IsRetryableCarFault( ex, cancellationToken )) {
                if (attempt == 3) {
                    LogCarRetryExhausted( logger, ex );
                    throw;
                }
                LogCarRetryAttempt( logger, attempt, ex );
                await Task.Delay( BackoffFor( attempt + 1 ), cancellationToken );
            }
        }
        // The loop either returns or throws on attempt 3; this line is structurally unreachable.
        throw new UnreachableException( );
    }

    /// <summary>
    /// Returns true when <paramref name="ex"/> is a retryable CAR fault: a CarParseException,
    /// an HttpRequestException, or an HttpClient-deadline OperationCanceledException (caller token
    /// not signalled). Caller-token cancellation is excluded and must be rethrown by the caller.
    /// </summary>
    private static bool IsRetryableCarFault( Exception ex, CancellationToken callerToken ) =>
        ex is CarParseException
        || ex is HttpRequestException
        || (ex is OperationCanceledException && !callerToken.IsCancellationRequested);

    /// <summary>
    /// Returns the inter-attempt backoff delay for the given attempt number using equal-jitter
    /// exponential backoff with a 2-second base. Attempt 2 returns a value in [1s, 2s]; attempt 3
    /// returns a value in [2s, 4s]. The minimum half guarantees spacing to clear the PDS export
    /// race window; the jitter avoids synchronized retry storms.
    /// </summary>
    /// <param name="attempt">The attempt number about to be made (2 or 3); must be ≥ 2.</param>
    /// <returns>The delay to wait before the given attempt.</returns>
    internal static TimeSpan BackoffFor( int attempt ) {
        double baseSeconds = 2.0 * Math.Pow( 2.0, attempt - 2 ); // attempt=2→2s, attempt=3→4s
        double half = baseSeconds / 2.0;
        double jitter = Random.Shared.NextDouble( ) * half;
        return TimeSpan.FromSeconds( half + jitter );
    }

    /// <summary>
    /// Performs one download+parse attempt: fetches the repo CAR, buffers it, checks completeness,
    /// and walks the MST to materialize the full record list. Throws on any fault. The result list
    /// is declared here so each attempt starts from an empty list — a partial list from a failed
    /// mid-walk attempt is never concatenated with a fresh walk.
    /// </summary>
    /// <param name="pdsUri">The base URI of the PDS hosting the repository.</param>
    /// <param name="userDid">The repository owner's DID.</param>
    /// <param name="cancellationToken">A token observed during download and enumeration.</param>
    /// <returns>
    /// The fully-materialized list of valid <c>(AT-URI, result)</c> pairs and their record CIDs.
    /// The caller publishes both caches together while holding <see cref="_carCacheLock"/>.
    /// </returns>
    /// <exception cref="CarParseException">Thrown when the download is truncated or the CAR fails to parse.</exception>
    private async Task<(List<(string AtUri, MediaLinkResult Result)> Records, Dictionary<string, string> RecordCids)>
        DownloadAndParseCarAsync(
        Uri pdsUri,
        string userDid,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested( );

        Stopwatch stopwatch = Stopwatch.StartNew( );
        string getRepoUrl = $"{pdsUri.ToString( ).TrimEnd( '/' )}/xrpc/com.atproto.sync.getRepo?did={Uri.EscapeDataString( userDid )}";

        HttpClient httpClient = httpClientFactory.CreateClient( ATProtoSyncHttpClientName );

        ReadOnlyMemory<byte> carMemory;
        try {
            using HttpResponseMessage response = await httpClient.GetAsync(
                getRepoUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken );

            _ = response.EnsureSuccessStatusCode( );

            // Capture Content-Length for the post-download completeness check below.
            long? contentLength = response.Content.Headers.ContentLength;

            // Buffer the full CAR into memory — MST walk requires random access to blocks.
            using MemoryStream ms = new( );
            byte[] buffer = new byte[ChunkSize];
            Stream contentStream = await response.Content.ReadAsStreamAsync( cancellationToken );
            int read;
            while ((read = await contentStream.ReadAsync( buffer, cancellationToken )) > 0) {
                ms.Write( buffer, 0, read );
            }

            // Truncation guard: when Content-Length was advertised, a short read is a failed
            // transfer — throw so the retry loop can re-download a complete export.
            if (contentLength.HasValue && ms.Length < contentLength.Value) {
                throw new CarParseException(
                    $"CAR download truncated: read {ms.Length} of {contentLength.Value} advertised bytes." );
            }

            carMemory = ms.GetBuffer( ).AsMemory( 0, (int)ms.Length );
        } catch (CarParseException ex) {
            LogCarDownloadFailed( logger, ex );
            throw;
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Cooperative shutdown: the caller's token was signaled — rethrow for normal shutdown handling.
            throw;
        } catch (OperationCanceledException oce) {
            // HttpClient deadline fired (not the caller's token). Treat as a download failure so
            // consumers' catch (Exception) degradation paths handle it rather than treating it as
            // cooperative shutdown.
            LogCarDownloadFailed( logger, oce );
            throw;
        } catch (Exception ex) {
            LogCarDownloadFailed( logger, ex );
            throw;
        }

        stopwatch.Stop( );
        long elapsedMs = stopwatch.ElapsedMilliseconds;

        // results is declared here so each attempt starts from an empty list — a partial list
        // from a failed mid-walk is never concatenated with a fresh walk on retry.
        List<(string AtUri, MediaLinkResult Result)> results = [];
        Dictionary<string, string> recordCids = new( StringComparer.Ordinal );

        try {
            CarEnumerationResult enumResult =
                CarRepoReader.EnumerateCollection( carMemory, s_mediaLinkResultCollection.ToString( ), cancellationToken );

            if (enumResult.CommitVersion != 3) {
                LogCarCommitVersionUnexpected( logger, enumResult.CommitVersion );
            }

            int recordCount = 0;
            int skippedCount = 0;

            foreach ((string rkey, string cid, System.Text.Json.Nodes.JsonNode recordNode) in enumResult.Records) {
                cancellationToken.ThrowIfCancellationRequested( );

                // Validate rkey before building AT-URI or logging it verbatim.
                // MST keys come from an operator-controlled PDS; an attacker could inject
                // control characters or path separators. Skip invalid rkeys with a warning.
                if (!IsValidRkey( rkey )) {
                    LogCarRecordSkipped( logger, TruncateForLog( rkey, 64 ), "invalid rkey" );
                    skippedCount++;
                    continue;
                }

                // Strip $type if present to avoid friction with JsonSerializer
                if (recordNode is System.Text.Json.Nodes.JsonObject obj && obj.ContainsKey( "$type" )) {
                    _ = obj.Remove( "$type" );
                }

                try {
                    string json = recordNode.ToJsonString( );
                    MediaLinkResultRecord? record = JsonSerializer.Deserialize<MediaLinkResultRecord>( json );

                    if (record is null) {
                        LogCarRecordSkipped( logger, rkey, "deserialized to null" );
                        skippedCount++;
                        continue;
                    }

                    MediaLinkResult? converted = ConvertFromRecord( record );
                    if (converted is null) {
                        LogCarRecordSkipped( logger, rkey, "no valid providers" );
                        skippedCount++;
                        continue;
                    }

                    string atUri = ATProtoUriHelper.BuildLookupRecordUri( userDid, rkey );
                    results.Add( (atUri, converted) );
                    recordCids[atUri] = cid;
                    recordCount++;
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    LogCarRecordSkipped( logger, rkey, ex.Message.SanitizeForLogging( ) );
                    skippedCount++;
                }
            }

            LogCarDownloaded( logger, carMemory.Length, enumResult.BlockCount, elapsedMs );
            LogCarEnumerated( logger, recordCount, skippedCount );
        } catch (CarParseException ex) {
            LogCarDownloadFailed( logger, ex );
            throw;
        } catch (OperationCanceledException) {
            throw;
        }

        return (results, recordCids);
    }

    #region Rkey validation

    /// <summary>Maximum permitted record-key length (512 characters), per the atproto rkey rules.</summary>
    private const int MaxRkeyLength = 512;

    /// <summary>
    /// Validates a record key against the atproto rkey rules: length 1 to 512 and characters limited
    /// to ASCII letters, digits, and <c>. _ ~ : -</c>.
    /// </summary>
    /// <param name="rkey">The record key to validate.</param>
    /// <returns><see langword="true"/> when the key is within length and uses only allowed characters; otherwise <see langword="false"/>.</returns>
    private static bool IsValidRkey( string rkey ) {
        if (rkey.Length == 0 || rkey.Length > MaxRkeyLength) {
            return false;
        }

        foreach (char c in rkey) {
            if (!char.IsAsciiLetterOrDigit( c )
                && c != '.' && c != '_' && c != '~' && c != ':' && c != '-') {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Prepares a value for logging by truncating it to a maximum length and replacing ASCII control
    /// characters (CR, LF, TAB, and other C0 codes) with spaces, defending against log injection from
    /// untrusted record keys.
    /// </summary>
    /// <param name="value">The value to sanitize.</param>
    /// <param name="maxLen">The maximum number of characters to keep before appending an ellipsis.</param>
    /// <returns>The sanitized, length-bounded string, with <c>...</c> appended when truncated.</returns>
    private static string TruncateForLog( string value, int maxLen ) {
        ReadOnlySpan<char> source = value.Length <= maxLen
            ? value.AsSpan( )
            : value.AsSpan( 0, maxLen );
        bool hasControl = false;
        foreach (char c in source) {
            if (c < ' ' || c == 127) { hasControl = true; break; }
        }
        if (!hasControl) {
            return value.Length <= maxLen ? value : string.Concat( source, "..." );
        }
        System.Text.StringBuilder sb = new( source.Length + 3 );
        foreach (char c in source) {
            _ = sb.Append( c < ' ' || c == 127 ? ' ' : c );
        }
        if (value.Length > maxLen) {
            _ = sb.Append( "..." );
        }
        return sb.ToString( );
    }

    #endregion

    #region LoggerMessage Methods

    /// <summary>Logs that an existing record was updated (via the PutRecord path).</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="uri">The AT-URI of the updated record.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceUpdatedRecord,
        Level = LogLevel.Information,
        Message = "Successfully updated existing MediaLinkResult on ATProto PDS: {uri}" )]
    internal static partial void LogUpdatedRecord( ILogger logger, string uri );

    /// <summary>Logs that a new record was created (via the CreateRecord fallback path).</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="uri">The AT-URI of the created record.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCreatedRecord,
        Level = LogLevel.Information,
        Message = "Successfully created MediaLinkResult on ATProto PDS: {uri}" )]
    internal static partial void LogCreatedRecord( ILogger logger, string uri );

    /// <summary>Logs a failure while storing a record on the PDS.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that caused the failure.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceStoreError,
        Level = LogLevel.Error,
        Message = "Failed to store MediaLinkResult on ATProto PDS" )]
    internal static partial void LogStoreError( ILogger logger, Exception ex );

    /// <summary>Logs that a single-record fetch from the PDS failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="error">The sanitized error detail.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceGetRecordFailed,
        Level = LogLevel.Warning,
        Message = "Failed to get record from ATProto PDS: {error}" )]
    internal static partial void LogGetRecordFailed( ILogger logger, string error );

    /// <summary>Logs an unexpected exception while retrieving a record by AT-URI.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that caused the failure.</param>
    /// <param name="uri">The AT-URI that was being retrieved.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceRetrieveError,
        Level = LogLevel.Error,
        Message = "Failed to retrieve MediaLinkResult from ATProto PDS: {uri}" )]
    internal static partial void LogRetrieveError( ILogger logger, Exception ex, string uri );

    /// <summary>Logs completion of a repository CAR download and parse, with size and timing.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="bytes">The number of CAR bytes downloaded.</param>
    /// <param name="blocks">The number of blocks decoded from the CAR.</param>
    /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarDownloaded,
        Level = LogLevel.Information,
        Message = "CAR download complete: {Bytes} bytes, {Blocks} blocks, {ElapsedMs} ms" )]
    internal static partial void LogCarDownloaded( ILogger logger, long bytes, int blocks, long elapsedMs );

    /// <summary>Logs completion of CAR record enumeration, with counts of returned and skipped records.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="recordCount">The number of records returned.</param>
    /// <param name="skippedCount">The number of records skipped (invalid rkey, null, or unconvertible).</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarEnumerated,
        Level = LogLevel.Information,
        Message = "CAR enumeration complete: {RecordCount} records returned, {SkippedCount} skipped" )]
    internal static partial void LogCarEnumerated( ILogger logger, int recordCount, int skippedCount );

    /// <summary>Logs that the CAR commit version was not the expected value of 3; processing continues regardless.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="commitVersion">The commit version read from the CAR.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarCommitVersionUnexpected,
        Level = LogLevel.Warning,
        Message = "CAR commit version {CommitVersion} is not 3; proceeding anyway" )]
    internal static partial void LogCarCommitVersionUnexpected( ILogger logger, int commitVersion );

    /// <summary>Logs that the repository CAR download or parse failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that caused the failure, if any.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarDownloadFailed,
        Level = LogLevel.Error,
        Message = "CAR download or parse failed" )]
    internal static partial void LogCarDownloadFailed( ILogger logger, Exception? ex );

    /// <summary>Logs that a record was skipped during CAR enumeration, with the (sanitized) rkey and reason.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="rkey">The sanitized, truncated record key.</param>
    /// <param name="reason">Why the record was skipped (for example invalid rkey or no valid providers).</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarRecordSkipped,
        Level = LogLevel.Warning,
        Message = "Skipping record {Rkey}: {Reason}" )]
    internal static partial void LogCarRecordSkipped( ILogger logger, string rkey, string reason );

    /// <summary>Logs one CAR fetch+parse retry attempt before the backoff delay.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="attempt">The attempt number that just failed (1-indexed).</param>
    /// <param name="ex">The exception that caused the failure.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarRetryAttempt,
        Level = LogLevel.Warning,
        Message = "CAR fetch+parse attempt {Attempt} failed; retrying" )]
    internal static partial void LogCarRetryAttempt( ILogger logger, int attempt, Exception ex );

    /// <summary>Logs that all 3 CAR fetch+parse attempts are exhausted before the exception propagates.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception from the final attempt.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarRetryExhausted,
        Level = LogLevel.Error,
        Message = "CAR fetch+parse exhausted all retry attempts" )]
    internal static partial void LogCarRetryExhausted( ILogger logger, Exception ex );

    /// <summary>Logs that a full-collection read was served from the in-process cache.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="awaitedLock">
    /// <see langword="true"/> when served after waiting for the single-flight lock (awaiter reuse);
    /// <see langword="false"/> when served by the pre-lock fast read.
    /// </param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarCacheHit,
        Level = LogLevel.Debug,
        Message = "CAR cache hit (awaited lock: {AwaitedLock})" )]
    internal static partial void LogCarCacheHit( ILogger logger, bool awaitedLock );

    /// <summary>Logs a confirmed cache miss or forced refresh that will trigger a download.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="forced">Whether the miss was a forced refresh rather than a TTL expiry.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarCacheMiss,
        Level = LogLevel.Debug,
        Message = "CAR cache miss (forced: {Forced}); downloading" )]
    internal static partial void LogCarCacheMiss( ILogger logger, bool forced );

    /// <summary>Logs the start of the single-flight CAR download under the cache lock.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarCacheRefreshStart,
        Level = LogLevel.Debug,
        Message = "CAR cache refresh started" )]
    internal static partial void LogCarCacheRefreshStart( ILogger logger );

    /// <summary>Logs that an accepted transfer was committed to the cache.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="recordCount">Number of records in the freshly-populated cache entry.</param>
    /// <param name="timestamp">The UTC timestamp of the new cache entry.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarCacheRefreshComplete,
        Level = LogLevel.Debug,
        Message = "CAR cache refreshed: {RecordCount} records at {Timestamp}" )]
    internal static partial void LogCarCacheRefreshComplete( ILogger logger, int recordCount, DateTimeOffset timestamp );

    /// <summary>Logs that the cache field overwrite replaced a previously-expired entry.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarCacheEvict,
        Level = LogLevel.Debug,
        Message = "CAR cache evicted expired entry" )]
    internal static partial void LogCarCacheEvict( ILogger logger );

    #endregion
}
