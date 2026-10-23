using System.Net;
using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Domain.Utilities;
using BridgeBeats.Core.Infrastructure.Logging;
using BridgeBeats.Core.Infrastructure.Storage;
using BridgeBeats.Core.Infrastructure.Utilities;
using StackExchange.Redis;

namespace BridgeBeats.Core.Infrastructure.Cache;

/// <summary>
/// Redis-backed implementation of <see cref="IMediaLinkCacheRepository"/> that uses Redis for lookup
/// indices and the ATProto PDS for persistent storage. Redis stores only the RecordUri pointers and
/// metadata; the actual <see cref="MediaLinkResult"/> body is retrieved from the PDS, which is the
/// source of truth.
/// </summary>
/// <remarks>
/// Each lookup key (<c>lookup:isrc:</c>, <c>lookup:upc:</c>, <c>lookup:url:</c>, <c>lookup:card:</c>,
/// <c>lookup:provider:</c>, <c>lookup:metadata:</c>) maps to a PDS record URI; the result body is
/// fetched from the PDS on a hit. Per-entry bookkeeping lives under <c>meta:{rkey}</c>
/// (JSON with CreatedAt, LastLookedUpAt, CardId, RecordUri), and the full set of keys written for an
/// entry is tracked under <c>keys:{rkey}</c> so they can be refreshed or removed together. Because
/// Redis stores only derived pointers, it can be rebuilt from the PDS at any time; the PDS is the
/// source of truth. Lookups return an <c>isStale</c> flag driven by the configured cache window.
/// </remarks>
public sealed partial class RedisMediaLinkCache : IMediaLinkCacheRepository {

    /// <summary>Redis connection used for all pointer operations.</summary>
    private readonly IConnectionMultiplexer _redis;

    /// <summary>ATProto storage used to read and write the durable result bodies.</summary>
    private readonly IATProtoStorageService _atprotoStorage;

    /// <summary>Logger for cache diagnostics.</summary>
    private readonly ILogger<RedisMediaLinkCache> _logger;

    /// <summary>Number of days a cached pointer lives and within which a record is considered fresh.</summary>
    private readonly int _cacheDays;

    /// <summary>The user's ATProto DID, used to build record URIs.</summary>
    private readonly string _userDID;

    /// <summary>Camel-case options used to serialize and deserialize cache metadata.</summary>
    private readonly JsonSerializerOptions _jsonOptions;

    // Key prefixes

    /// <summary>Pointer prefix keyed by ISRC. Literal value: <c>"lookup:isrc:"</c>.</summary>
    private const string LookupIsrcPrefix = "lookup:isrc:";

    /// <summary>Pointer prefix keyed by UPC. Literal value: <c>"lookup:upc:"</c>.</summary>
    private const string LookupUpcPrefix = "lookup:upc:";

    /// <summary>Pointer prefix keyed by hashed input URL. Literal value: <c>"lookup:url:"</c>.</summary>
    private const string LookupUrlPrefix = "lookup:url:";

    /// <summary>Pointer prefix keyed by card id. Literal value: <c>"lookup:card:"</c>.</summary>
    private const string LookupCardPrefix = "lookup:card:";

    /// <summary>Pointer prefix keyed by provider and provider id. Literal value: <c>"lookup:provider:"</c>.</summary>
    private const string LookupProviderPrefix = "lookup:provider:";

    /// <summary>Pointer prefix keyed by hashed title/artist metadata. Literal value: <c>"lookup:metadata:"</c>.</summary>
    private const string LookupMetadataPrefix = "lookup:metadata:";

    /// <summary>Prefix for per-entry metadata records. Literal value: <c>"meta:"</c>.</summary>
    private const string MetaPrefix = "meta:";

    /// <summary>Prefix for the set tracking all keys written for an entry. Literal value: <c>"keys:"</c>.</summary>
    private const string KeysPrefix = "keys:";

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisMediaLinkCache"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer, used for pointer operations.</param>
    /// <param name="atprotoStorage">Service for storing and retrieving result bodies on the ATProto PDS.</param>
    /// <param name="logger">Logger for cache diagnostics.</param>
    /// <param name="cacheDays">Pointer lifetime and freshness window, in days.</param>
    /// <param name="userDID">The user DID (Decentralized Identifier) that posts the records to the PDS, used to build record URIs.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="redis"/>, <paramref name="atprotoStorage"/>, <paramref name="logger"/>, or <paramref name="userDID"/> is null.</exception>
    public RedisMediaLinkCache(
        IConnectionMultiplexer redis,
        IATProtoStorageService atprotoStorage,
        ILogger<RedisMediaLinkCache> logger,
        int cacheDays,
        string userDID
    ) {
        _redis = redis ?? throw new ArgumentNullException( nameof( redis ) );
        _atprotoStorage = atprotoStorage ?? throw new ArgumentNullException( nameof( atprotoStorage ) );
        _logger = logger ?? throw new ArgumentNullException( nameof( logger ) );
        _cacheDays = cacheDays;
        _userDID = userDID ?? throw new ArgumentNullException( nameof( userDID ) );
        _jsonOptions = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Looks up a cached result by input URL.
    /// </summary>
    /// <param name="inputLink">The input link to look up.</param>
    /// <returns>
    /// The result, its PDS record URI, and a staleness flag, or null when no pointer exists or the
    /// link is blank.
    /// </returns>
    /// <remarks>The URL is hashed to form the pointer key <c>lookup:url:{hash}</c>.</remarks>
    public async Task<(MediaLinkResult result, string recordUri, bool isStale)?> TryGetCachedResultAsync( string inputLink ) {
        if (string.IsNullOrWhiteSpace( inputLink )) { return null; }

        IDatabase db = _redis.GetDatabase( );
        string urlHash = HashUtility.HashUrl( inputLink );
        string key = $"{LookupUrlPrefix}{urlHash}";

        RedisValue recordUri = await db.StringGetAsync( key );
        return recordUri.IsNullOrEmpty
            ? null
            : await GetResultFromPdsAsync( recordUri.ToString( ) );
    }

    /// <summary>
    /// Looks up a cached result by ISRC (track code).
    /// </summary>
    /// <param name="isrc">The ISRC to look up; trimmed and upper-cased for the key.</param>
    /// <returns>The result, its record URI, and a staleness flag, or null when not found.</returns>
    /// <remarks>
    /// On a Redis pointer miss this falls back to querying the PDS directly by external id, so a
    /// result present on the PDS but missing from Redis can still be found (and re-indexed).
    /// </remarks>
    public async Task<(MediaLinkResult result, string recordUri, bool isStale)?> TryGetCachedResultByISRCAsync( string isrc ) {
        if (string.IsNullOrWhiteSpace( isrc )) { return null; }

        IDatabase db = _redis.GetDatabase( );
        string key = $"{LookupIsrcPrefix}{isrc.Trim().ToUpperInvariant()}";

        RedisValue recordUri = await db.StringGetAsync( key );

        // If not in Redis, try to generate the expected rkey and check PDS directly
        return recordUri.IsNullOrEmpty
            ? await TryGetFromPdsByExternalIdAsync( isrc.Trim( ).ToUpperInvariant( ), false )
            : await GetResultFromPdsAsync( recordUri.ToString( ) );
    }

    /// <summary>
    /// Looks up a cached result by UPC (album code).
    /// </summary>
    /// <param name="upc">The UPC to look up; trimmed and upper-cased for the key.</param>
    /// <returns>The result, its record URI, and a staleness flag, or null when not found.</returns>
    /// <remarks>
    /// On a Redis pointer miss this falls back to querying the PDS directly by external id, treating
    /// the lookup as an album.
    /// </remarks>
    public async Task<(MediaLinkResult result, string recordUri, bool isStale)?> TryGetCachedResultByUPCAsync( string upc ) {
        if (string.IsNullOrWhiteSpace( upc )) { return null; }

        IDatabase db = _redis.GetDatabase( );
        string key = $"{LookupUpcPrefix}{upc.Trim().ToUpperInvariant()}";

        RedisValue recordUri = await db.StringGetAsync( key );

        // If not in Redis, try to generate the expected rkey and check PDS directly
        return recordUri.IsNullOrEmpty
            ? await TryGetFromPdsByExternalIdAsync( upc.Trim( ).ToUpperInvariant( ), true )
            : await GetResultFromPdsAsync( recordUri.ToString( ) );
    }

    /// <summary>
    /// Looks up a cached result by title and artist metadata.
    /// </summary>
    /// <param name="title">The title component (may be blank if artist is supplied).</param>
    /// <param name="artist">The artist component (may be blank if title is supplied).</param>
    /// <returns>The result, its record URI, and a staleness flag, or null when not found or both inputs are blank.</returns>
    /// <remarks>
    /// The title and artist are lower-cased, trimmed, joined with a pipe, and SHA-256/Base32-hashed
    /// to form the pointer key <c>lookup:metadata:{hash}</c>.
    /// </remarks>
    public async Task<(MediaLinkResult result, string recordUri, bool isStale)?> TryGetCachedResultByMetadataAsync( string title, string artist ) {
        if (string.IsNullOrWhiteSpace( title ) && string.IsNullOrWhiteSpace( artist )) { return null; }

        IDatabase db = _redis.GetDatabase( );
        string metadataKey = $"{title?.Trim().ToLowerInvariant() ?? ""}|{artist?.Trim().ToLowerInvariant() ?? ""}";
        string metadataHash = HashUtility.ComputeSha256Base32( metadataKey );
        string key = $"{LookupMetadataPrefix}{metadataHash}";

        RedisValue recordUri = await db.StringGetAsync( key );
        return recordUri.IsNullOrEmpty
            ? null
            : await GetResultFromPdsAsync( recordUri.ToString( ) );
    }

    /// <summary>
    /// Looks up a cached result by a provider's own id.
    /// </summary>
    /// <param name="providerId">The provider-specific id; trimmed for the key.</param>
    /// <param name="provider">The provider that owns the id.</param>
    /// <param name="isAlbum">Whether the id refers to an album. Currently informational only.</param>
    /// <returns>The result, its record URI, and a staleness flag, or null when not found.</returns>
    /// <remarks>The pointer key is <c>lookup:provider:{provider}:{providerId}</c>.</remarks>
    public async Task<(MediaLinkResult result, string recordUri, bool isStale)?> TryGetCachedResultByProviderIdAsync(
        string providerId,
        SupportedProviders provider,
        bool isAlbum
    ) {
        if (string.IsNullOrWhiteSpace( providerId )) { return null; }

        IDatabase db = _redis.GetDatabase( );
        string key = $"{LookupProviderPrefix}{provider}:{providerId.Trim()}";

        RedisValue recordUri = await db.StringGetAsync( key );
        return recordUri.IsNullOrEmpty
            ? null
            : await GetResultFromPdsAsync( recordUri.ToString( ) );
    }

    /// <summary>
    /// Looks up a cached result by shareable card id.
    /// </summary>
    /// <param name="cardId">The card id; trimmed and lower-cased for the key.</param>
    /// <returns>The result, its record URI, and a staleness flag, or null when not found.</returns>
    /// <remarks>The pointer key is <c>lookup:card:{cardId}</c>.</remarks>
    public async Task<(MediaLinkResult result, string recordUri, bool isStale)?> TryGetCachedResultByCardIdAsync( string cardId ) {
        if (string.IsNullOrWhiteSpace( cardId )) { return null; }

        IDatabase db = _redis.GetDatabase( );
        string key = $"{LookupCardPrefix}{cardId.Trim().ToLowerInvariant()}";

        RedisValue recordUri = await db.StringGetAsync( key );
        return recordUri.IsNullOrEmpty
            ? null
            : await GetResultFromPdsAsync( recordUri.ToString( ) );
    }

    /// <summary>
    /// Indexes an already-stored result by its <paramref name="recordUri"/>, (re)building all
    /// Redis lookup pointers. Does NOT write the result body to the PDS.
    /// </summary>
    /// <param name="result">The result whose lookup keys are indexed.</param>
    /// <param name="recordUri">The AT-URI of the already-stored PDS record.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the Redis pointers have been (re)built.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="recordUri"/> is null, empty, or whitespace.</exception>
    public async Task IndexResultAsync( MediaLinkResult result, string recordUri, CancellationToken cancellationToken = default ) {
        ArgumentNullException.ThrowIfNull( result );
        ArgumentException.ThrowIfNullOrWhiteSpace( recordUri );

        try {
            string rkey = RecordKeyGenerator.GenerateRkey( result );

            // Remove old lookup keys before adding new ones (explicit cleanup on refresh)
            await RemoveExistingLookupKeysAsync( rkey );

            // Add lookup indices to Redis
            await AddInputLinksAsync( recordUri, result );

            if (_logger.IsEnabled( LogLevel.Information )) {
                string sanitizedRkey = rkey.SanitizeForLogging( );
                string sanitizedRecordUri = recordUri.SanitizeForLogging( );
                LogCachedResult( _logger, sanitizedRkey, sanitizedRecordUri );
            }
        } catch (Exception ex) {
            LogCacheError( _logger, ex );
            throw;
        }
    }

    /// <summary>
    /// Builds the full set of Redis lookup pointers for a result, all pointing at one PDS record URI.
    /// </summary>
    /// <param name="recordUri">The PDS record URI the pointers should reference.</param>
    /// <param name="result">The result whose links, ids, and metadata become pointers.</param>
    /// <returns>A task that completes once all pointers and bookkeeping keys are written.</returns>
    /// <remarks>
    /// Writes a <c>meta:{rkey}</c> bookkeeping record, a <c>lookup:card:</c> pointer, URL pointers
    /// for every input and provider URL, ISRC/UPC pointers from each provider's external id,
    /// title/artist metadata pointers, and provider-id pointers. All written keys are recorded in
    /// <c>keys:{rkey}</c> so they can be refreshed or removed as a unit, and all share the configured
    /// cache-day TTL. When an entry for the same record already exists, only the TTLs are refreshed
    /// rather than rewriting the pointers. This is the same method the Maintenance worker calls to rebuild
    /// Redis from the PDS.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="recordUri"/> is blank or the result has no provider results.</exception>
    public async Task AddInputLinksAsync( string recordUri, MediaLinkResult result ) {
        ArgumentNullException.ThrowIfNull( result );
        if (string.IsNullOrWhiteSpace( recordUri )) {
            throw new ArgumentException( "Record URI cannot be null or empty", nameof( recordUri ) );
        }

        if (result.Results.Count == 0) {
            throw new ArgumentException( "MediaLinkResult must contain at least one provider result", nameof( result ) );
        }

        try {
            IDatabase db = _redis.GetDatabase( );
            string rkey = RecordKeyGenerator.GenerateRkey( result );
            string cardId = RecordKeyGenerator.GenerateCardId( rkey );
            TimeSpan expiry = TimeSpan.FromDays( _cacheDays );
            bool isAlbum = result.Results.Values.First( ).IsAlbum ?? false;

            // Check if this record already exists in cache with the same RecordUri
            // If so, just refresh TTL on existing keys and skip the full write
            string metaKey = $"{MetaPrefix}{rkey}";
            RedisValue existingMetaJson = await db.StringGetAsync( metaKey );
            if (!existingMetaJson.IsNullOrEmpty) {
                CacheEntryMetadata? existingMeta = JsonSerializer.Deserialize<CacheEntryMetadata>(
                    existingMetaJson.ToString( ),
                    _jsonOptions
                );

                if (existingMeta?.RecordUri == recordUri) {
                    // Record already cached with same URI - just refresh TTL on all keys
                    await RefreshTtlForRkeyAsync( db, rkey, expiry );
                    if (_logger.IsEnabled( LogLevel.Debug )) {
                        string sanitizedRkey = rkey.SanitizeForLogging( );
                        LogCacheEntryExists( _logger, sanitizedRkey );
                    }
                    return;
                }
            }

            // Track all keys we create for this rkey (for cleanup on refresh)
            List<string> allKeys = [];

            // Store metadata
            CacheEntryMetadata metadata = new( ) {
                Rkey = rkey,
                CardId = cardId,
                RecordUri = recordUri,
                CreatedAt = DateTime.UtcNow,
                LastLookedUpAt = DateTime.UtcNow
            };
            _ = await db.StringSetAsync( metaKey, JsonSerializer.Serialize( metadata, _jsonOptions ), expiry );
            allKeys.Add( metaKey );

            // Store card ID lookup
            string cardKey = $"{LookupCardPrefix}{cardId}";
            _ = await db.StringSetAsync( cardKey, recordUri, expiry );
            allKeys.Add( cardKey );

            // Store URL lookups (input links + service URLs)
            List<string> allUrls = [.. result.InputLinks];
            allUrls.AddRange( result.Results.Values
                .Where( r => !string.IsNullOrWhiteSpace( r.URL ) )
                .Select( r => r.URL ) );

            foreach (string url in allUrls.Where( u => !string.IsNullOrWhiteSpace( u ) ).Distinct( )) {
                string urlHash = HashUtility.HashUrl( url );
                string urlKey = $"{LookupUrlPrefix}{urlHash}";
                _ = await db.StringSetAsync( urlKey, recordUri, expiry );
                allKeys.Add( urlKey );
            }

            // Store external ID lookups and metadata lookups
            foreach (MusicLookupResult lookupResult in result.Results.Values) {
                // External ID (ISRC or UPC)
                if (!string.IsNullOrWhiteSpace( lookupResult.ExternalId )) {
                    string prefix = isAlbum ? LookupUpcPrefix : LookupIsrcPrefix;
                    string externalIdKey = $"{prefix}{lookupResult.ExternalId.Trim().ToUpperInvariant()}";
                    _ = await db.StringSetAsync( externalIdKey, recordUri, expiry );
                    allKeys.Add( externalIdKey );
                }

                // Metadata lookup
                if (!string.IsNullOrWhiteSpace( lookupResult.Title ) || !string.IsNullOrWhiteSpace( lookupResult.Artist )) {
                    string metadataValue = $"{lookupResult.Title?.Trim().ToLowerInvariant() ?? ""}|{lookupResult.Artist?.Trim().ToLowerInvariant() ?? ""}";
                    if (metadataValue != "|") {
                        string metadataHash = HashUtility.ComputeSha256Base32( metadataValue );
                        string metadataLookupKey = $"{LookupMetadataPrefix}{metadataHash}";
                        _ = await db.StringSetAsync( metadataLookupKey, recordUri, expiry );
                        allKeys.Add( metadataLookupKey );
                    }
                }
            }

            // Store provider ID lookups
            foreach ((SupportedProviders provider, MusicLookupResult dto) in result.Results) {
                string? providerId = ExtractProviderIdFromUrl( provider, dto.URL );
                if (!string.IsNullOrWhiteSpace( providerId )) {
                    string providerKey = $"{LookupProviderPrefix}{provider}:{providerId}";
                    _ = await db.StringSetAsync( providerKey, recordUri, expiry );
                    allKeys.Add( providerKey );
                }
            }

            // Store the set of all keys for this rkey (for cleanup on refresh)
            string keysSetKey = $"{KeysPrefix}{rkey}";
            _ = await db.KeyDeleteAsync( keysSetKey );
            if (allKeys.Count > 0) {
                RedisValue[] keyValues = [.. allKeys.Select( k => (RedisValue)k )];
                _ = await db.SetAddAsync( keysSetKey, keyValues );
                _ = await db.KeyExpireAsync( keysSetKey, expiry );
            }

            // Verify the meta key was actually written (spot check)
            RedisValue verifyValue = await db.StringGetAsync( metaKey );
            if (verifyValue.IsNullOrEmpty) {
                if (_logger.IsEnabled( LogLevel.Warning )) {
                    string sanitizedRkey = rkey.SanitizeForLogging( );
                    LogWriteVerificationFailed( _logger, sanitizedRkey );
                }
            }

            if (_logger.IsEnabled( LogLevel.Information )) {
                string sanitizedRkey = rkey.SanitizeForLogging( );
                string sanitizedCardId = cardId.SanitizeForLogging( );
                LogCreatedLookupEntries( _logger, sanitizedRkey, sanitizedCardId, allKeys.Count );
            }
        } catch (Exception ex) {
            LogAddInputLinksError( _logger, ex );
            throw;
        }
    }

    /// <summary>
    /// Removes all existing lookup keys for an rkey before adding new ones.
    /// This ensures stale keys are cleaned up when a record is refreshed.
    /// </summary>
    /// <param name="rkey">The record key whose pointers should be removed.</param>
    /// <returns>A task that completes once the keys are deleted.</returns>
    /// <remarks>Reads the <c>keys:{rkey}</c> set to find every key written for the record, then deletes them all.</remarks>
    private async Task RemoveExistingLookupKeysAsync( string rkey ) {
        IDatabase db = _redis.GetDatabase( );
        string keysSetKey = $"{KeysPrefix}{rkey}";

        RedisValue[] existingKeys = await db.SetMembersAsync( keysSetKey );
        if (existingKeys.Length > 0) {
            RedisKey[] keysToDelete = [.. existingKeys.Select( k => (RedisKey)k.ToString( ) )];
            _ = await db.KeyDeleteAsync( keysToDelete );
            _ = await db.KeyDeleteAsync( keysSetKey );

            if (_logger.IsEnabled( LogLevel.Debug )) {
                string sanitizedRkey = rkey.SanitizeForLogging( );
                LogRemovedLookupKeys( _logger, existingKeys.Length, sanitizedRkey );
            }
        }
    }

    /// <summary>
    /// Refreshes the TTL for all lookup keys associated with an rkey, and on the tracking set itself.
    /// This is used during bootstrap to extend TTL for records that already exist with the same RecordUri.
    /// </summary>
    /// <param name="db">The Redis database to operate on.</param>
    /// <param name="rkey">The record key whose pointers should have their TTL extended.</param>
    /// <param name="expiry">The new TTL to apply.</param>
    /// <returns>A task that completes once all TTLs are refreshed.</returns>
    private static async Task RefreshTtlForRkeyAsync( IDatabase db, string rkey, TimeSpan expiry ) {
        string keysSetKey = $"{KeysPrefix}{rkey}";
        RedisValue[] existingKeys = await db.SetMembersAsync( keysSetKey );

        if (existingKeys.Length == 0) {
            return;
        }

        // Refresh TTL on all keys associated with this rkey
        foreach (RedisValue key in existingKeys) {
            _ = await db.KeyExpireAsync( key.ToString( ), expiry );
        }

        // Also refresh TTL on the keys set itself
        _ = await db.KeyExpireAsync( keysSetKey, expiry );
    }

    /// <summary>
    /// Attempts to retrieve a result from the PDS by generating the expected recordUri from an
    /// external ID, and re-indexes it in Redis on success. This is a fallback when the lookup key
    /// isn't in Redis but the record may exist on the PDS.
    /// </summary>
    /// <param name="externalId">The ISRC or UPC to resolve.</param>
    /// <param name="isAlbum">Whether the external id is a UPC (album) rather than an ISRC (track).</param>
    /// <returns>The result, record URI, and staleness flag, or null when the PDS has no matching record or an error occurs.</returns>
    private async Task<(MediaLinkResult result, string recordUri, bool isStale)?> TryGetFromPdsByExternalIdAsync(
        string externalId,
        bool isAlbum
    ) {
        try {
            string rkey = RecordKeyGenerator.GenerateRkey( externalId, isAlbum );
            string recordUri = ATProtoUriHelper.BuildLookupRecordUri( _userDID, rkey );

            MediaLinkResult? pdsResult = await _atprotoStorage.GetMediaLinkResultAsync( recordUri );
            if (pdsResult != null) {
                // Populate Redis cache for future lookups
                await AddInputLinksAsync( recordUri, pdsResult );
                return CheckRecordFreshness( pdsResult, recordUri );
            }
        } catch (Exception ex) {
            if (_logger.IsEnabled( LogLevel.Warning )) {
                string sanitizedExternalId = externalId.SanitizeForLogging( );
                LogPdsLookupFailed( _logger, ex, sanitizedExternalId );
            }
        }

        return null;
    }

    /// <summary>
    /// Retrieves a result body from the ATProto PDS by record URI and checks its freshness.
    /// </summary>
    /// <param name="recordUri">The PDS record URI to fetch.</param>
    /// <returns>
    /// The result, its record URI, and a staleness flag, or null when the record is absent on the
    /// PDS (it will be cleaned up on TTL expiry) or a fetch error occurs.
    /// </returns>
    private async Task<(MediaLinkResult result, string recordUri, bool isStale)?> GetResultFromPdsAsync( string recordUri ) {
        try {
            MediaLinkResult? result = await _atprotoStorage.GetMediaLinkResultAsync( recordUri );
            if (result != null) {
                if (_logger.IsEnabled( LogLevel.Debug )) {
                    string sanitizedRecordUri = recordUri.SanitizeForLogging( );
                    LogCacheHit( _logger, sanitizedRecordUri );
                }
                return CheckRecordFreshness( result, recordUri );
            }

            // Record not found on PDS - remove from Redis
            if (_logger.IsEnabled( LogLevel.Information )) {
                string sanitizedRecordUri = recordUri.SanitizeForLogging( );
                LogRecordNotFound( _logger, sanitizedRecordUri );
            }
        } catch (HttpRequestException ex) {
            LogHttpError( _logger, ex, ex.StatusCode );
        } catch (Exception ex) {
            LogPdsError( _logger, ex );
        }

        return null;
    }

    /// <summary>
    /// Checks if a result is stale based on the cache days configuration.
    /// </summary>
    /// <param name="result">The fetched result.</param>
    /// <param name="recordUri">The record URI it came from.</param>
    /// <returns>The result, its URI, and a staleness flag.</returns>
    /// <remarks>A record is stale when its last-looked-up time is older than the configured cache window.</remarks>
    private (MediaLinkResult result, string recordUri, bool isStale) CheckRecordFreshness(
        MediaLinkResult result,
        string recordUri
    ) {
        DateTime expirationDate = DateTime.UtcNow.AddDays( -_cacheDays );
        bool isStale = result.LookedUpAt < expirationDate;
        return (result, recordUri, isStale);
    }

    /// <summary>
    /// Extracts the provider-specific ID from a service URL using the shared parser utility,
    /// returning null on any parse failure.
    /// </summary>
    /// <param name="provider">The provider whose URL format to parse.</param>
    /// <param name="url">The URL to extract the id from.</param>
    /// <returns>The provider id, or null if it could not be extracted.</returns>
    private string? ExtractProviderIdFromUrl( SupportedProviders provider, string url ) {
        try {
            return ProviderUrlParser.ExtractId( provider, url );
        } catch (Exception ex) {
            LogExtractProviderIdFailed( _logger, ex, provider );
            return null;
        }
    }

    /// <summary>
    /// Metadata stored in Redis for a cache entry under <c>meta:{rkey}</c>, linking a record key and
    /// card id to a PDS record URI with timestamps.
    /// </summary>
    private sealed class CacheEntryMetadata {
        /// <summary>The record key this metadata describes.</summary>
        public string Rkey { get; set; } = string.Empty;

        /// <summary>The shareable card id for the entry.</summary>
        public string CardId { get; set; } = string.Empty;

        /// <summary>The PDS record URI the entry's pointers reference.</summary>
        public string RecordUri { get; set; } = string.Empty;

        /// <summary>When the entry was first created.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>When the entry was last looked up.</summary>
        public DateTime LastLookedUpAt { get; set; }
    }

    #region LoggerMessage Methods

    /// <summary>Logs that a result was cached on the PDS and indexed in Redis.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="rkey">The sanitized record key.</param>
    /// <param name="recordUri">The sanitized PDS record URI.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCacheCachedResult,
        Level = LogLevel.Information,
        Message = "Cached result with rkey: {Rkey}, recordUri: {RecordUri}" )]
    internal static partial void LogCachedResult( ILogger logger, string rkey, string recordUri );

    /// <summary>Logs an error raised while caching a result.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCacheCacheError,
        Level = LogLevel.Error,
        Message = "Error while caching result" )]
    internal static partial void LogCacheError( ILogger logger, Exception ex );

    /// <summary>Logs that an entry already existed for the record, so only its TTL was refreshed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="rkey">The sanitized record key.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCacheEntryExists,
        Level = LogLevel.Debug,
        Message = "Cache entry already exists for rkey: {Rkey}, refreshed TTL" )]
    internal static partial void LogCacheEntryExists( ILogger logger, string rkey );

    /// <summary>Logs that the post-write verification could not find the meta key.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="rkey">The sanitized record key.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCacheWriteVerificationFailed,
        Level = LogLevel.Error,
        Message = "Redis write verification FAILED for rkey: {Rkey} - meta key not found after write!" )]
    internal static partial void LogWriteVerificationFailed( ILogger logger, string rkey );

    /// <summary>Logs that the lookup pointers for a record were created.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="rkey">The sanitized record key.</param>
    /// <param name="cardId">The sanitized card id.</param>
    /// <param name="keyCount">Number of pointer keys written.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCacheCreatedLookupEntries,
        Level = LogLevel.Information,
        Message = "Created Redis lookup entries for rkey: {Rkey}, cardId: {CardId}, {KeyCount} keys" )]
    internal static partial void LogCreatedLookupEntries( ILogger logger, string rkey, string cardId, int keyCount );

    /// <summary>Logs an error raised while adding input-link pointers.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCacheAddInputLinksError,
        Level = LogLevel.Error,
        Message = "Error while adding input links to Redis cache" )]
    internal static partial void LogAddInputLinksError( ILogger logger, Exception ex );

    /// <summary>Logs that existing lookup keys were removed before re-indexing.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="keyCount">Number of keys removed.</param>
    /// <param name="rkey">The sanitized record key.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCacheRemovedLookupKeys,
        Level = LogLevel.Information,
        Message = "Removed {KeyCount} existing lookup keys for rkey: {Rkey}" )]
    internal static partial void LogRemovedLookupKeys( ILogger logger, int keyCount, string rkey );

    /// <summary>Logs that the PDS fallback lookup by external id failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception.</param>
    /// <param name="externalId">The sanitized external id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCachePdsLookupFailed,
        Level = LogLevel.Warning,
        Message = "Failed to retrieve result from PDS by external ID: {ExternalId}" )]
    internal static partial void LogPdsLookupFailed( ILogger logger, Exception ex, string externalId );

    /// <summary>Logs a cache hit on a record URI.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="recordUri">The sanitized record URI.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCacheCacheHit,
        Level = LogLevel.Information,
        Message = "Cache hit for RecordUri: {RecordUri}" )]
    internal static partial void LogCacheHit( ILogger logger, string recordUri );

    /// <summary>Logs that a pointed-to record was missing on the PDS (it will clear on TTL expiry).</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="recordUri">The sanitized record URI.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCacheRecordNotFound,
        Level = LogLevel.Warning,
        Message = "Record not found on PDS, will be cleaned up on TTL expiry: {RecordUri}" )]
    internal static partial void LogRecordNotFound( ILogger logger, string recordUri );

    /// <summary>Logs an HTTP error raised while fetching a cached result from the PDS.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception.</param>
    /// <param name="statusCode">The HTTP status code, if available.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCacheHttpError,
        Level = LogLevel.Error,
        Message = "HTTP error while trying to get cached result, StatusCode: {StatusCode}" )]
    internal static partial void LogHttpError( ILogger logger, Exception ex, HttpStatusCode? statusCode );

    /// <summary>Logs a non-HTTP error raised while fetching a cached result from the PDS.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCachePdsError,
        Level = LogLevel.Error,
        Message = "Error while trying to get cached result from PDS" )]
    internal static partial void LogPdsError( ILogger logger, Exception ex );

    /// <summary>Logs that a provider id could not be extracted from a URL.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception.</param>
    /// <param name="provider">The provider whose URL was being parsed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Cache.RedisMediaLinkCacheExtractProviderIdFailed,
        Level = LogLevel.Warning,
        Message = "Failed to extract provider ID from URL for {Provider}." )]
    internal static partial void LogExtractProviderIdFailed( ILogger logger, Exception ex, SupportedProviders provider );

    #endregion
}
