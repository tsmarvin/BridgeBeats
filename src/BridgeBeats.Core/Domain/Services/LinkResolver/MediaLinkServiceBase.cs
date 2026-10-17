using System.Text.Json;
using System.Text.RegularExpressions;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Domain.Services;
using BridgeBeats.Core.Infrastructure.Logging;
using BridgeBeats.Core.Infrastructure.Utilities;

namespace BridgeBeats.Services.LinkResolver {

    /// <summary>
    /// Shared base for the in-process <see cref="IMediaLinkService"/> resolvers. Holds the common
    /// machinery for resolving links across providers: extracting links from free text with the
    /// <see cref="ValidLink"/> regex, running each through every enabled provider, deduplicating
    /// results based on external IDs (ISRC/UPC), and combining the per-provider hits into one
    /// <see cref="MediaLinkResult"/> per distinct entity. One result is marked primary; alternates
    /// are matched by external id first, then by case-insensitive artist and title. Missing
    /// providers are back-filled by a secondary lookup off the first hit.
    /// </summary>
    /// <remarks>
    /// Concrete derived type: <see cref="BridgeBeats.Core.Domain.Services.LinkResolver.DefaultMediaLinkService"/>
    /// (the synchronous, in-process path). The caching path uses a separate orchestrated implementation
    /// and does not derive from this base. Prerelease Spotify links short-circuit to a user-facing
    /// message rather than a result.
    /// </remarks>
    /// <param name="enabledProvidersCollection">The enabled providers mapped to their direct lookup services, allowing runtime configuration of which providers are active.</param>
    /// <param name="logger">The logger used for structured error and trace diagnostics.</param>
    /// <param name="serializerOptions">JSON options used when tracing serialized inputs.</param>
    public abstract partial class MediaLinkServiceBase(
        Dictionary<SupportedProviders, IMusicLookupService> enabledProvidersCollection,
        ILogger<MediaLinkServiceBase> logger,
        JsonSerializerOptions serializerOptions
    ) : IMediaLinkService {

        /// <summary>Resolves every supported link in free text into a stream of combined results. Implemented by derived types.</summary>
        /// <param name="content">Free text that may contain one or more provider links.</param>
        /// <returns>An async stream of combined multi-provider results.</returns>
        public abstract IAsyncEnumerable<MediaLinkResult> GetInfoAsync( string content );

        /// <summary>Resolves a track by title and artist into a combined result. Implemented by derived types.</summary>
        /// <param name="title">The track or album title to search for.</param>
        /// <param name="artist">The artist name to search for.</param>
        /// <returns>The combined result, or <see langword="null"/> when nothing matched.</returns>
        public abstract Task<MediaLinkResult?> GetInfoAsync( string title, string artist );

        /// <summary>Resolves a track by ISRC into a combined result. Implemented by derived types.</summary>
        /// <param name="isrc">The International Standard Recording Code identifying the track.</param>
        /// <returns>The combined result, or <see langword="null"/> when nothing matched.</returns>
        public abstract Task<MediaLinkResult?> GetInfoByISRCAsync( string isrc );

        /// <summary>Resolves an album by UPC into a combined result. Implemented by derived types.</summary>
        /// <param name="upc">The Universal Product Code identifying the album.</param>
        /// <returns>The combined result, or <see langword="null"/> when nothing matched.</returns>
        public abstract Task<MediaLinkResult?> GetInfoByUPCAsync( string upc );

        /// <summary>Resolves an entity by a provider's own id into a combined result. Implemented by derived types.</summary>
        /// <param name="providerId">The entity id within the originating provider.</param>
        /// <param name="provider">The provider that <paramref name="providerId"/> belongs to.</param>
        /// <param name="isAlbum"><see langword="true"/> when the id refers to an album; otherwise a track.</param>
        /// <returns>The combined result, or <see langword="null"/> when nothing matched.</returns>
        public abstract Task<MediaLinkResult?> GetInfoByProviderIdAsync( string providerId, SupportedProviders provider, bool isAlbum );

        #region Base Class Defaults

        /// <summary>The logger shared with derived types for error and trace diagnostics.</summary>
        protected readonly ILogger<MediaLinkServiceBase> Logger = logger;
        /// <summary>JSON options used when tracing serialized lookup inputs.</summary>
        protected readonly JsonSerializerOptions SerializerOptions = serializerOptions;
        /// <summary>The enabled providers and their direct lookup services, iterated for every lookup.</summary>
        protected readonly Dictionary<SupportedProviders, IMusicLookupService> EnabledProviders = enabledProvidersCollection;

        /// <summary>
        /// The regex used to extract candidate links from free text. Defaults to an HTTPS link matcher;
        /// derived types may override it.
        /// </summary>
        protected virtual Regex ValidLink { get; init; } = ValidHttpsLink( );

        /// <summary>
        /// Extracts every link from free-text <paramref name="content"/> using <see cref="ValidLink"/>
        /// and runs each through every enabled provider, collecting the successful
        /// <see cref="MusicLookupResult"/>s keyed to their originating provider and input link. Provider
        /// failures are logged (with a sanitized link at trace level) and skipped; an outer failure is
        /// logged and yields whatever was gathered so far.
        /// </summary>
        /// <param name="content">Free text that may contain one or more provider links.</param>
        /// <returns>A map of each successful provider result to the provider and input link it came from.</returns>
        protected async Task<Dictionary<MusicLookupResult, (SupportedProviders provider, string inputLink)>> GetMusicLookupResults( string content ) {
            Dictionary<MusicLookupResult, (SupportedProviders provider, string inputLink)> linkResults = [];
            try {
                foreach (string link in ValidLink.GetGroupValues( content, "Link" )) {
                    if (string.IsNullOrWhiteSpace( link )) { continue; }

                    foreach ((SupportedProviders provider, IMusicLookupService svc) in EnabledProviders) {
                        try {
                            MusicLookupResult? lookup = await svc.GetInfoAsync( link );
                            if (lookup is not null) { linkResults.Add( lookup, (provider, link) ); }
                        } catch (Exception e) {
                            LogUrlLookupProviderError( Logger, e, provider );
                            if (Logger.IsEnabled( LogLevel.Trace )) {
                                string sanitizedLink = link.SanitizeForLogging( );
                                LogUrlLookupProviderTrace( Logger, sanitizedLink );
                            }
                        }
                    }
                }
            } catch (Exception ex) {
                LogUrlLookupError( Logger, ex );
                if (Logger.IsEnabled( LogLevel.Trace )) {
                    string sanitizedContent = content.SanitizeForLogging( );
                    LogUrlLookupTrace( Logger, sanitizedContent );
                }
            }
            return linkResults;
        }

        /// <summary>
        /// Searches the enabled providers in order for a title/artist match and returns the first hit
        /// with the provider that produced it. Returns <see langword="null"/> when either input is blank
        /// or no provider matched. Provider failures are logged and skipped.
        /// </summary>
        /// <param name="title">The track or album title to search for.</param>
        /// <param name="artist">The artist name to search for.</param>
        /// <returns>The first matching result and its provider, or <see langword="null"/> when none matched.</returns>
        protected async Task<(MusicLookupResult result, SupportedProviders provider)?> GetMusicLookupResults( string title, string artist ) {
            try {
                if (string.IsNullOrWhiteSpace( title ) || string.IsNullOrWhiteSpace( artist )) { return null; }

                foreach ((SupportedProviders provider, IMusicLookupService svc) in EnabledProviders) {
                    try {
                        MusicLookupResult? lookup = await svc.GetInfoAsync( title, artist );
                        if (lookup is not null) { return (lookup, provider); }
                    } catch (Exception ex) {
                        LogArtistTitleLookupProviderError( Logger, ex, provider );
                        if (Logger.IsEnabled( LogLevel.Trace )) {
                            string sanitizedTitle = title.SanitizeForLogging( );
                            string sanitizedArtist = artist.SanitizeForLogging( );
                            LogArtistTitleLookupProviderTrace( Logger, sanitizedTitle, sanitizedArtist );
                        }
                    }
                }
            } catch (Exception ex) {
                LogArtistTitleLookupError( Logger, ex );
                if (Logger.IsEnabled( LogLevel.Trace )) {
                    string sanitizedTitle = title.SanitizeForLogging( );
                    string sanitizedArtist = artist.SanitizeForLogging( );
                    LogArtistTitleLookupTrace( Logger, sanitizedTitle, sanitizedArtist );
                }
            }
            return null;
        }

        /// <summary>
        /// Searches the enabled providers in order for an external id, treating it as a UPC when
        /// <paramref name="isAlbum"/> is <see langword="true"/> or an ISRC otherwise, and returns the
        /// first hit with its provider. Returns <see langword="null"/> when the id is blank or no
        /// provider matched. Provider failures are logged and skipped.
        /// </summary>
        /// <param name="externalId">The ISRC (track) or UPC (album) to look up.</param>
        /// <param name="isAlbum"><see langword="true"/> to treat <paramref name="externalId"/> as a UPC; otherwise an ISRC.</param>
        /// <returns>The first matching result and its provider, or <see langword="null"/> when none matched.</returns>
        protected async Task<(MusicLookupResult result, SupportedProviders provider)?> GetMusicLookupResults( string externalId, bool isAlbum ) {
            try {
                if (string.IsNullOrWhiteSpace( externalId )) { return null; }

                foreach ((SupportedProviders provider, IMusicLookupService svc) in EnabledProviders) {
                    try {
                        MusicLookupResult? lookup = isAlbum
                                                        ? await svc.GetInfoByUPCAsync( externalId )
                                                        : await svc.GetInfoByISRCAsync( externalId );

                        if (lookup is not null) { return (lookup, provider); }
                    } catch (Exception ex) {
                        LogExternalIdLookupProviderError( Logger, ex, provider );
                        if (Logger.IsEnabled( LogLevel.Trace )) {
                            string sanitizedExternalId = externalId.SanitizeForLogging( );
                            LogExternalIdLookupProviderTrace( Logger, sanitizedExternalId, isAlbum );
                        }
                    }
                }
            } catch (Exception ex) {
                LogExternalIdLookupError( Logger, ex );
                if (Logger.IsEnabled( LogLevel.Trace )) {
                    string sanitizedExternalId = externalId.SanitizeForLogging( );
                    LogExternalIdLookupTrace( Logger, sanitizedExternalId, isAlbum );
                }
            }

            return null;
        }

        /// <summary>
        /// Looks up an entity directly against a single named provider by that provider's own id,
        /// marking the result primary. Returns <see langword="null"/> when the id is blank, the provider
        /// is not enabled, or the lookup found nothing. Failures are logged and swallowed.
        /// </summary>
        /// <param name="providerId">The entity id within <paramref name="provider"/>.</param>
        /// <param name="provider">The provider to query.</param>
        /// <param name="isAlbum"><see langword="true"/> when the id refers to an album; otherwise a track.</param>
        /// <returns>The matching result marked primary, or <see langword="null"/> when none was found.</returns>
        protected async Task<MusicLookupResult?> GetMusicLookupResultsByProviderId(
            string providerId,
            SupportedProviders provider,
            bool isAlbum
        ) {
            try {
                if (string.IsNullOrWhiteSpace( providerId )) { return null; }

                if (!EnabledProviders.TryGetValue( provider, out IMusicLookupService? svc )) {
                    LogProviderNotEnabled( Logger, provider );
                    return null;
                }
                MusicLookupResult? lookup = await svc.GetInfoByIDAsync( providerId, isAlbum );
                if (lookup is not null) {
                    lookup.IsPrimary = true;
                    return lookup;
                }
            } catch (Exception ex) {
                LogProviderIdLookupError( Logger, ex );
                if (Logger.IsEnabled( LogLevel.Trace )) {
                    string sanitizedProviderId = providerId.SanitizeForLogging( );
                    LogProviderIdLookupTrace( Logger, sanitizedProviderId, provider, isAlbum );
                }
            }

            return null;
        }

        /// <summary>
        /// Wraps a single provider result into a <see cref="MediaLinkResult"/> and back-fills the
        /// remaining enabled providers via <see cref="SyncLookupResult"/>. Returns <see langword="null"/>
        /// when <paramref name="lookupResults"/> is <see langword="null"/>.
        /// </summary>
        /// <param name="lookupResults">A single provider result and the provider it came from, or null.</param>
        /// <returns>The combined multi-provider result, or <see langword="null"/> when no input was given.</returns>
        protected async Task<MediaLinkResult?> CombineLookupInfoAsync(
            (MusicLookupResult dto, SupportedProviders provider)? lookupResults
        ) {
            if (lookupResults is null) { return null; }
            MediaLinkResult result = new();
            result.Results.Add( lookupResults.Value.provider, lookupResults.Value.dto );
            return await SyncLookupResult( result );
        }

        /// <summary>
        /// Combines the per-link, per-provider results into one <see cref="MediaLinkResult"/> per
        /// distinct entity, streaming each as it is built. For each result it marks the originating
        /// provider's hit primary, records the input link, and attaches matching alternates from the
        /// other providers — matched first by external id, then by case-insensitive artist and title.
        /// Entries already represented in an earlier result are skipped, and a Spotify prerelease link
        /// yields a user-facing message instead of a result. Each combined result is then back-filled
        /// for any still-missing provider via <see cref="SyncLookupResult"/>.
        /// </summary>
        /// <param name="linkResults">The per-link provider results gathered from the input content.</param>
        /// <returns>An async stream of combined results, one per distinct entity.</returns>
        protected async IAsyncEnumerable<MediaLinkResult> CombineLookupInfoAsync(
            Dictionary<MusicLookupResult, (SupportedProviders provider, string inputLink)> linkResults
        ) {
            List<MediaLinkResult> results = [];
            Dictionary<SupportedProviders, IEnumerable<MusicLookupResult>> resultsByProvider = [];
            foreach ((SupportedProviders provider, IMusicLookupService svc) in EnabledProviders) {
                IEnumerable<MusicLookupResult> providerResults = linkResults
                                                                        .Where( kv => kv.Value.provider == provider )
                                                                        .Select( lr => lr.Key );

                if (providerResults.Any( )) {
                    resultsByProvider.Add( provider, providerResults );
                }
            }

            foreach ((MusicLookupResult lookup, (SupportedProviders provider, string inputlink)) in linkResults) {
                // Different provider URLs can resolve to separately-created DTOs for the same
                // ISRC/UPC. Deduplicate by normalized external identity before falling back to
                // value equality for results that do not carry an external identifier.
                string? externalIdentity = MediaLookupResultIdentity.GetExternalKey( lookup );
                bool alreadyReturned = externalIdentity is not null
                    ? results.Any( existing => existing.Results.Values.Any( value =>
                        string.Equals(
                            MediaLookupResultIdentity.GetExternalKey( value ),
                            externalIdentity,
                            StringComparison.OrdinalIgnoreCase ) ) )
                    : results.Any( existing => existing.Results.Any( rr => rr.Key == provider && rr.Value == lookup ) );

                if (alreadyReturned) {
                    continue;
                }

                MediaLinkResult result = new();
                lookup.IsPrimary = true;
                result.InputLinks.Add( $"https://{inputlink}" );
                if (lookup.ExternalId == "prerelease") {
                    result.Messages ??= [];
                    result.Messages.Add( "Prerelease links aren't supported at the moment. Try a Title/Artist search to check other platforms." );
                } else {
                    result.Results.Add( provider, lookup );
                }

                foreach ((SupportedProviders alternateProvider, IEnumerable<MusicLookupResult> altProviderResults) in resultsByProvider) {
                    if ((int)alternateProvider == (int)provider) { continue; }
                    MusicLookupResult? altProviderMatch = altProviderResults
                                                                .FirstOrDefault( a => a.ExternalId == lookup.ExternalId )
                                                           ?? altProviderResults
                                                                .FirstOrDefault( r =>
                                                                    r.Artist.Trim( ).Equals( lookup.Artist.Trim( ), StringComparison.InvariantCultureIgnoreCase ) &&
                                                                    r.Title.Trim( ).Equals( lookup.Title.Trim( ), StringComparison.InvariantCultureIgnoreCase )
                                                            );

                    if (altProviderMatch is not null) {
                        altProviderMatch.IsPrimary = false;
                        result.Results.Add( alternateProvider, altProviderMatch );
                    }
                }

                MediaLinkResult outputResult = await SyncLookupResult( result );
                results.Add( outputResult );
                yield return outputResult;
            }
        }

        #endregion Base Class Defaults

        #region Base Class Private Implementations

        /// <summary>
        /// Back-fills a combined result by querying every enabled provider not already present, using
        /// the first existing result as the lookup seed (each provider's
        /// <c>GetInfoAsync(MusicLookupResult)</c>). Newly found provider results are added in place.
        /// Per-provider failures are logged (with the serialized input at trace level) and skipped. A
        /// result with no existing entries is returned unchanged.
        /// </summary>
        /// <param name="input">The partially populated combined result to complete.</param>
        /// <returns>The same result instance, with any newly resolved providers added.</returns>
        private protected virtual async Task<MediaLinkResult> SyncLookupResult( MediaLinkResult input ) {
            if (input.Results.Count == 0) { return input; }

            List<SupportedProviders> completedList = [.. input.Results.Select( p => p.Key )];
            MusicLookupResult firstValue = input.Results.Values.First( );

            foreach ((SupportedProviders provider, IMusicLookupService svc) in EnabledProviders.Where( e => completedList.Contains( e.Key ) == false )) {
                try {
                    MusicLookupResult? lookup = await svc.GetInfoAsync( firstValue );
                    if (lookup is not null) { input.Results.Add( provider, lookup ); }
                } catch (Exception ex) {
                    if (Logger.IsEnabled( LogLevel.Error )) {
                        LogSecondaryLookupError( Logger, ex, provider, firstValue.Artist, firstValue.Title, firstValue.ExternalId, firstValue.IsAlbum ?? false, string.Join( ", ", completedList.Select( l => l.ToString( ) ) ) );
                    }
                    if (Logger.IsEnabled( LogLevel.Trace )) {
                        string serializedInput = JsonSerializer.Serialize( input, SerializerOptions );
                        LogSecondaryLookupTrace( Logger, serializedInput );
                    }
                }
            }
            return input;
        }

        /// <summary>
        /// Source-generated regex that matches HTTPS URLs in free text, capturing the full match as
        /// <c>Url</c> and the post-scheme remainder as <c>Link</c>. Backs the default
        /// <see cref="ValidLink"/>.
        /// </summary>
        /// <returns>The compiled HTTPS-link regex.</returns>
        [GeneratedRegex( @"(?<Url>[Hh][Tt]{2}[Pp][Ss]:\/\/(?<Link>\w[\w\/\=\?\.\:\-%&]*))" )]
        private protected static partial Regex ValidHttpsLink( );

        #endregion Base Class Private Implementations

        #region LoggerMessage Definitions

        /// <summary>Logs (Error) that a provider failed while resolving a link by URL.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception raised by the provider lookup.</param>
        /// <param name="provider">The provider that failed.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.UrlLookupProviderError,
            Level = LogLevel.Error,
            Message = "Failed while getting initial media link lookup data by URL for {Provider}." )]
        private static partial void LogUrlLookupProviderError( ILogger logger, Exception ex, SupportedProviders provider );

        /// <summary>Logs (Trace) the sanitized link involved in a failed URL provider lookup.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="link">The sanitized link.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.UrlLookupProviderTrace,
            Level = LogLevel.Trace,
            Message = "link: {Link}" )]
        private static partial void LogUrlLookupProviderTrace( ILogger logger, string link );

        /// <summary>Logs (Error) that the overall URL-based lookup failed.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception raised during the lookup.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.UrlLookupError,
            Level = LogLevel.Error,
            Message = "Failed while getting initial media link lookup data by URL." )]
        private static partial void LogUrlLookupError( ILogger logger, Exception ex );

        /// <summary>Logs (Trace) the sanitized free-text content of a failed URL-based lookup.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="content">The sanitized content.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.UrlLookupTrace,
            Level = LogLevel.Trace,
            Message = "Content: {Content}" )]
        private static partial void LogUrlLookupTrace( ILogger logger, string content );

        /// <summary>Logs (Error) that a provider failed while resolving by artist and title.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception raised by the provider lookup.</param>
        /// <param name="provider">The provider that failed.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.ArtistTitleLookupProviderError,
            Level = LogLevel.Error,
            Message = "Failed while getting initial media link lookup data by artist/title for {Provider}." )]
        private static partial void LogArtistTitleLookupProviderError( ILogger logger, Exception ex, SupportedProviders provider );

        /// <summary>Logs (Trace) the sanitized title and artist of a failed per-provider artist/title lookup.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="title">The sanitized title.</param>
        /// <param name="artist">The sanitized artist.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.ArtistTitleLookupProviderTrace,
            Level = LogLevel.Trace,
            Message = "title: '{Title}', artist: '{Artist}'" )]
        private static partial void LogArtistTitleLookupProviderTrace( ILogger logger, string title, string artist );

        /// <summary>Logs (Error) that the overall artist/title lookup failed.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception raised during the lookup.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.ArtistTitleLookupError,
            Level = LogLevel.Error,
            Message = "Failed while getting initial media link lookup data by artist/title." )]
        private static partial void LogArtistTitleLookupError( ILogger logger, Exception ex );

        /// <summary>Logs (Trace) the sanitized title and artist of a failed overall artist/title lookup.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="title">The sanitized title.</param>
        /// <param name="artist">The sanitized artist.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.ArtistTitleLookupTrace,
            Level = LogLevel.Trace,
            Message = "title: '{Title}', artist: '{Artist}'" )]
        private static partial void LogArtistTitleLookupTrace( ILogger logger, string title, string artist );

        /// <summary>Logs (Error) that a provider failed while resolving by external id (ISRC/UPC).</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception raised by the provider lookup.</param>
        /// <param name="provider">The provider that failed.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.ExternalIdLookupProviderError,
            Level = LogLevel.Error,
            Message = "Failed while getting initial media link lookup data by externalId for {Provider}." )]
        private static partial void LogExternalIdLookupProviderError( ILogger logger, Exception ex, SupportedProviders provider );

        /// <summary>Logs (Trace) the sanitized external id and album flag of a failed per-provider external-id lookup.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="externalId">The sanitized external id.</param>
        /// <param name="isAlbum">Whether the id was treated as a UPC (album) rather than an ISRC.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.ExternalIdLookupProviderTrace,
            Level = LogLevel.Trace,
            Message = "externalId: '{ExternalId}', isAlbum: {IsAlbum}" )]
        private static partial void LogExternalIdLookupProviderTrace( ILogger logger, string externalId, bool isAlbum );

        /// <summary>Logs (Error) that the overall external-id lookup failed.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception raised during the lookup.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.ExternalIdLookupError,
            Level = LogLevel.Error,
            Message = "Failed while getting initial media link lookup data by externalId." )]
        private static partial void LogExternalIdLookupError( ILogger logger, Exception ex );

        /// <summary>Logs (Trace) the sanitized external id and album flag of a failed overall external-id lookup.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="externalId">The sanitized external id.</param>
        /// <param name="isAlbum">Whether the id was treated as a UPC (album) rather than an ISRC.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.ExternalIdLookupTrace,
            Level = LogLevel.Trace,
            Message = "externalId: '{ExternalId}', isAlbum: {IsAlbum}" )]
        private static partial void LogExternalIdLookupTrace( ILogger logger, string externalId, bool isAlbum );

        /// <summary>Logs (Warning) that a requested provider is not enabled or configured.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="provider">The provider that was requested but not enabled.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.ProviderNotEnabled,
            Level = LogLevel.Warning,
            Message = "Provider {Provider} is not enabled or configured" )]
        private static partial void LogProviderNotEnabled( ILogger logger, SupportedProviders provider );

        /// <summary>Logs (Error) that the provider-id lookup failed.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception raised during the lookup.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.ProviderIdLookupError,
            Level = LogLevel.Error,
            Message = "Failed while getting initial media link lookup data by providerId." )]
        private static partial void LogProviderIdLookupError( ILogger logger, Exception ex );

        /// <summary>Logs (Trace) the sanitized provider id, provider, and album flag of a failed provider-id lookup.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="providerId">The sanitized provider id.</param>
        /// <param name="provider">The provider that was queried.</param>
        /// <param name="isAlbum">Whether the id referred to an album.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.ProviderIdLookupTrace,
            Level = LogLevel.Trace,
            Message = "providerId: '{ProviderId}', provider: {Provider}, isAlbum: {IsAlbum}" )]
        private static partial void LogProviderIdLookupTrace( ILogger logger, string providerId, SupportedProviders provider, bool isAlbum );

        /// <summary>Logs (Error) that a secondary (back-fill) lookup against another provider failed.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception raised during the secondary lookup.</param>
        /// <param name="additionalProvider">The provider being back-filled when the failure occurred.</param>
        /// <param name="artist">The seed result's artist.</param>
        /// <param name="title">The seed result's title.</param>
        /// <param name="externalId">The seed result's external id.</param>
        /// <param name="isAlbum">Whether the seed result is an album.</param>
        /// <param name="provider">The originating provider(s) already present in the result.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.SecondaryLookupError,
            Level = LogLevel.Error,
            Message = "Error during secondary lookup via {AdditionalProvider} for artist '{Artist}', title '{Title}' externalId '{ExternalId}' isAlbum={IsAlbum} originalProvider(s)={Provider}" )]
        private static partial void LogSecondaryLookupError( ILogger logger, Exception ex, SupportedProviders additionalProvider, string artist, string title, string externalId, bool isAlbum, string provider );

        /// <summary>Logs (Trace) the serialized combined result that a failed secondary lookup was completing.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="inputData">The serialized input result.</param>
        [LoggerMessage(
            EventId = LogEventIds.Services.LinkResolver.SecondaryLookupTrace,
            Level = LogLevel.Trace,
            Message = "Input data: {InputData}" )]
        private static partial void LogSecondaryLookupTrace( ILogger logger, string inputData );

        #endregion LoggerMessage Definitions

    }
}
