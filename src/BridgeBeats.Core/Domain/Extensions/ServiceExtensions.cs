using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Providers.Spotify;
using BridgeBeats.Core.Domain.Services;
using BridgeBeats.Core.Domain.Services.Cards;
using BridgeBeats.Core.Domain.Services.LinkResolver;
using BridgeBeats.Core.Domain.Services.Queue;
using BridgeBeats.Core.Infrastructure.Extensions;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Services.LinkResolver;
using Microsoft.AspNetCore.Html;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BridgeBeats.Core.Domain.Extensions;

/// <summary>
/// The BridgeBeats composition-root helpers. Holds the top-level <c>Add*</c> registration methods
/// (media-link resolver, card services, QR code, queue processors) plus a set of web/UI helpers
/// (enum descriptions, provider color/logo/icon markup, and OpenGraph metadata) used by the
/// presentation layer.
/// </summary>
public static class ServiceExtensions {

    /// <summary>
    /// Yields the value of the named capture group from every match of <paramref name="regex"/> in
    /// <paramref name="input"/>. Matches missing the group are skipped.
    /// </summary>
    /// <param name="regex">The regex to run.</param>
    /// <param name="input">The text to search.</param>
    /// <param name="groupName">The capture-group name to read from each match.</param>
    /// <returns>The named-group value for each match, in match order.</returns>
    /// <remarks>
    /// A near-identical internal copy lives in <c>Providers/Common/RegexExtensions</c>; this public
    /// copy is the one used by callers of this class. Keep the two in sync.
    /// </remarks>
    public static IEnumerable<string> GetGroupValues( this Regex regex, string input, string groupName ) {
        foreach (Match match in regex.Matches( input )) {
            if (match.Groups.ContainsKey( groupName )) {
                yield return match.Groups[groupName].Value;
            }
        }
    }

    /// <summary>
    /// Returns the text of the <see cref="System.ComponentModel.DescriptionAttribute"/> on an enum
    /// value, falling back to the value's name when no description attribute is present.
    /// </summary>
    /// <typeparam name="T">The enum type.</typeparam>
    /// <param name="enumValue">The enum value to describe.</param>
    /// <returns>
    /// The description attribute text, the value's name when no attribute is present, or an empty
    /// string when the value has no name.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <typeparamref name="T"/> is not an enum type.</exception>
    public static string GetDescription<T>( this T enumValue )
        where T : struct {
        Type type = enumValue.GetType();
        if (type.IsEnum == false) {
            throw new ArgumentException( "Must be an Enum!", nameof( enumValue ) );
        }

        //Tries to find a DescriptionAttribute for a potential friendly name
        //for the enum
        string? value = enumValue.ToString( );
        if (value != null) {
            MemberInfo[] memberInfo = type.GetMember(value);
            if (memberInfo.Length > 0) {
                object[] attrs = memberInfo[0].GetCustomAttributes(typeof(DescriptionAttribute), false);

                if (attrs != null && attrs.Length > 0) {
                    //Pull out the description value
                    return ((DescriptionAttribute)attrs[0]).Description;
                }
            }
            return value;
        }
        //If we have no description attribute, just return the ToString of the enum
        return string.Empty;
    }

    #region Web Extensions

    /// <summary>
    /// Returns the brand hex color for a provider, used for UI accents. Tidal switches between white
    /// and black by theme; unknown providers fall back to a default indigo.
    /// </summary>
    /// <param name="provider">The provider to color.</param>
    /// <param name="darkmode">Whether the dark-theme variant is wanted; only affects Tidal.</param>
    /// <returns>The hex color string, including the leading <c>#</c>.</returns>
    public static string GetProviderHexColor( this SupportedProviders provider, bool darkmode = false )
        => provider switch {
            SupportedProviders.AppleMusic => "#D60017",
            SupportedProviders.Spotify => "#1ED760",
            SupportedProviders.Tidal => darkmode ? "#FFFFFF" : "#000000",
            _ => "#6366F1"
        };

    /// <summary>
    /// Builds the HTML for a provider's full wordmark logo, emitting both a light-theme and a
    /// dark-theme <c>&lt;img&gt;</c> (CSS shows the one matching the active theme). Alt text is the
    /// provider description.
    /// </summary>
    /// <param name="provider">The provider whose logo to render.</param>
    /// <returns>The logo markup as <see cref="IHtmlContent"/>.</returns>
    public static IHtmlContent GetProviderLogo( this SupportedProviders provider ) {
        // Dark backgrounds need LIGHT logos, light backgrounds need DARK logos
        string darkThemeLogo = provider switch {
            SupportedProviders.AppleMusic => "/public/providerIcons/US-UK_Apple_Music_Listen_on_Lockup_RGB_wht_072720.svg",
            SupportedProviders.Spotify => "/public/providerIcons/Spotify_Full_Logo_Green_RGB.svg",
            SupportedProviders.Tidal => "/public/providerIcons/tidal-horizontal-white-cmyk.png",
            _ => "🎧"
        };

        string lightThemeLogo = provider switch {
            SupportedProviders.AppleMusic => "/public/providerIcons/US-UK_Apple_Music_Listen_on_Lockup_RGB_blk_072720.svg",
            SupportedProviders.Spotify => "/public/providerIcons/Spotify_Full_Logo_Green_CMYK.svg",
            SupportedProviders.Tidal => "/public/providerIcons/tidal-horizontal-black-cmyk.png",
            _ => "🎧"
        };
        string alt = provider.GetDescription() + " logo";

        // Return both images with classes for CSS-based theme switching
        return new HtmlString(
            $@"<img src=""{lightThemeLogo}"" alt=""{alt}"" class=""provider-logo provider-logo-light"" />" +
            $@"<img src=""{darkThemeLogo}"" alt=""{alt}"" class=""provider-logo provider-logo-dark"" />"
        );
    }

    /// <summary>
    /// Builds the HTML for a provider's compact icon, emitting both a light-theme and a dark-theme
    /// <c>&lt;img&gt;</c> (CSS shows the one matching the active theme). Alt text is the provider
    /// description.
    /// </summary>
    /// <param name="provider">The provider whose icon to render.</param>
    /// <returns>The icon markup as <see cref="IHtmlContent"/>.</returns>
    public static IHtmlContent GetProviderIcon( this SupportedProviders provider ) {
        // Dark backgrounds need LIGHT logos, light backgrounds need DARK logos
        string darkThemeIcon = provider switch {
            SupportedProviders.AppleMusic => "/public/providerIcons/Apple_Music_Icon_RGB_sm_073120.svg",
            SupportedProviders.Spotify => "/public/providerIcons/Spotify_Primary_Logo_Green_RGB.svg",
            SupportedProviders.Tidal => "/public/providerIcons/tidal-icon-white-cmyk.png",
            _ => "🎧"
        };

        string lightThemeIcon = provider switch {
            SupportedProviders.AppleMusic => "/public/providerIcons/Apple_Music_Icon_RGB_sm_073120.svg",
            SupportedProviders.Spotify => "/public/providerIcons/Spotify_Primary_Logo_Green_CMYK.svg",
            SupportedProviders.Tidal => "/public/providerIcons/tidal-icon-black-cmyk.png",
            _ => "🎧"
        };
        string alt = provider.GetDescription() + " icon";

        // Return both images with classes for CSS-based theme switching
        return new HtmlString(
            $@"<img src=""{lightThemeIcon}"" alt=""{alt}"" class=""provider-icon provider-icon-light"" />" +
            $@"<img src=""{darkThemeIcon}"" alt=""{alt}"" class=""provider-icon provider-icon-dark"" />"
        );
    }

    #endregion Web Extensions

    #region Queue Processor Registration

    /// <summary>
    /// Registers the worker-side queue processor for one provider: the shared queue infrastructure, a
    /// singleton provider-specific request queue, and the hosted
    /// <c>QueueProcessorBackgroundService</c> that drains the queue using
    /// <typeparamref name="TLookupService"/> resolved from DI.
    /// </summary>
    /// <typeparam name="TLookupService">The direct lookup service the processor uses to perform lookups.</typeparam>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="provider">The provider this processor serves.</param>
    /// <param name="configureSettings">Optional configuration of <c>QueueSettings</c>; applied when non-null.</param>
    /// <returns>The same <paramref name="services"/>, to allow call chaining.</returns>
    public static IServiceCollection AddQueueProcessor<TLookupService>(
        this IServiceCollection services,
        SupportedProviders provider,
        Action<QueueSettings>? configureSettings = null
    ) where TLookupService : class, IMusicLookupService {
        // Configure queue settings with defaults from the record definition
        // The QueueSettings record already has sensible defaults via init properties
        if (configureSettings is not null) {
            _ = services.Configure( configureSettings );
        }

        // Register shared queue infrastructure
        _ = services.AddQueueInfrastructure( );

        // Register the provider-specific queue via the single factory that owns the
        // "wrap with SpotifyBulkQueueDecorator if Spotify" conditional.
        _ = services.AddSingleton<IRequestQueue<QueuedLookupRequest>>(
            sp => QueueServiceExtensions.CreateProviderQueue( sp, provider ) );

        // Register the background service
        _ = services.AddHostedService( sp => new QueueProcessorBackgroundService(
            sp.GetRequiredService<IConnectionMultiplexer>( ),
            sp.GetRequiredService<IRequestQueue<QueuedLookupRequest>>( ),
            sp.GetRequiredService<IRateLimitTracker>( ),
            sp.GetRequiredService<ISagaStateManager>( ),
            sp.GetRequiredService<TLookupService>( ),
            provider,
            sp.GetRequiredService<ILogger<QueueProcessorBackgroundService>>( ),
            sp.GetRequiredService<IOptions<QueueSettings>>( ).Value
        ) );

        return services;
    }

    /// <summary>
    /// Registers the worker-side queue processor for one provider, using a factory to build the
    /// lookup service rather than resolving it by type. Registers the shared queue infrastructure, a
    /// singleton provider-specific request queue, and the hosted
    /// <c>QueueProcessorBackgroundService</c>.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="provider">The provider this processor serves.</param>
    /// <param name="lookupServiceFactory">Factory that produces the lookup service the processor uses.</param>
    /// <param name="configureSettings">Optional configuration of <c>QueueSettings</c>; applied when non-null.</param>
    /// <returns>The same <paramref name="services"/>, to allow call chaining.</returns>
    public static IServiceCollection AddQueueProcessor(
        this IServiceCollection services,
        SupportedProviders provider,
        Func<IServiceProvider, IMusicLookupService> lookupServiceFactory,
        Action<QueueSettings>? configureSettings = null
    ) {
        // Configure queue settings with defaults from the record definition
        // The QueueSettings record already has sensible defaults via init properties
        if (configureSettings is not null) {
            _ = services.Configure( configureSettings );
        }

        // Register shared queue infrastructure
        _ = services.AddQueueInfrastructure( );

        // Register the provider-specific queue via the single factory that owns the
        // "wrap with SpotifyBulkQueueDecorator if Spotify" conditional.
        _ = services.AddSingleton<IRequestQueue<QueuedLookupRequest>>(
            sp => QueueServiceExtensions.CreateProviderQueue( sp, provider ) );

        // Register the background service using the factory
        _ = services.AddHostedService( sp => new QueueProcessorBackgroundService(
            sp.GetRequiredService<IConnectionMultiplexer>( ),
            sp.GetRequiredService<IRequestQueue<QueuedLookupRequest>>( ),
            sp.GetRequiredService<IRateLimitTracker>( ),
            sp.GetRequiredService<ISagaStateManager>( ),
            lookupServiceFactory( sp ),
            provider,
            sp.GetRequiredService<ILogger<QueueProcessorBackgroundService>>( ),
            sp.GetRequiredService<IOptions<QueueSettings>>( ).Value
        ) );

        return services;
    }

    #endregion Queue Processor Registration

    #region Media Link Resolver Registration

    /// <summary>
    /// Registers the <see cref="IMediaLinkService"/> implementation for the chosen path. When
    /// <paramref name="useCaching"/> is set, registers the orchestrated path
    /// (<c>LookupOrchestrator</c> as a singleton plus the <c>CachingMediaLinkService</c>); otherwise
    /// registers the in-process <c>DefaultMediaLinkService</c> backed by the enabled providers'
    /// direct services.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="enabledProviders">The providers the resolver should query.</param>
    /// <param name="useCaching">
    /// <see langword="true"/> to use the cached, queue-orchestrated path; <see langword="false"/> for
    /// the direct in-process path.
    /// </param>
    /// <returns>The same <paramref name="services"/>, to allow call chaining.</returns>
    public static IServiceCollection AddMediaLinkResolver(
        this IServiceCollection services,
        HashSet<SupportedProviders> enabledProviders,
        bool useCaching
    ) {
        if (useCaching) {
            // Register the lookup orchestrator
            _ = services.AddSingleton<ILookupOrchestrator>( s =>
                new LookupOrchestrator(
                    s.GetRequiredService<IMediaLinkCacheRepository>( ),
                    s.GetRequiredService<IRequestDeduplicator>( ),
                    s.GetRequiredService<ISagaStateManager>( ),
                    s.GetRequiredService<IProviderQueueResolver<QueuedLookupRequest>>( ),
                    s.GetRequiredService<IATProtoStorageService>( ),
                    enabledProviders,
                    s.GetRequiredService<ILogger<LookupOrchestrator>>( ),
                    s.GetService<IOptions<QueueSettings>>( )?.Value
                )
            );

            // Register CachingMediaLinkService that delegates to the orchestrator
            _ = services.AddTransient<IMediaLinkService>( s =>
                new CachingMediaLinkService(
                    s.GetRequiredService<ILookupOrchestrator>( ),
                    s.GetRequiredService<ILogger<CachingMediaLinkService>>( )
                )
            );

            // Register the saga-backed progress probe
            _ = services.AddTransient<ILookupProgressProbe>( s =>
                new LookupProgressProbe(
                    s.GetRequiredService<ISagaStateManager>( ),
                    s.GetRequiredService<ILogger<LookupProgressProbe>>( )
                )
            );
        } else {
            // Use default (non-caching) service
            _ = services.AddTransient<IMediaLinkService>( s => {
                Dictionary<SupportedProviders, IMusicLookupService> providerServices = GetEnabledProviderServices( enabledProviders, s );

                return new DefaultMediaLinkService(
                    providerServices,
                    s.GetRequiredService<ILogger<DefaultMediaLinkService>>( ),
                    s.GetRequiredService<JsonSerializerOptions>( )
                );
            } );

            // Register the no-op probe for the non-caching path (no sagas)
            _ = services.AddTransient<ILookupProgressProbe>( _ => new NullLookupProgressProbe( ) );
        }

        return services;
    }

    /// <summary>
    /// Resolves the direct lookup service for each enabled provider, keyed by provider, for the
    /// in-process resolver path. Providers with no matching case are skipped.
    /// </summary>
    /// <param name="enabledProviders">The providers to resolve services for.</param>
    /// <param name="serviceProvider">The service provider to resolve the direct services from.</param>
    /// <returns>A map from provider to its resolved direct lookup service.</returns>
    private static Dictionary<SupportedProviders, IMusicLookupService> GetEnabledProviderServices(
        HashSet<SupportedProviders> enabledProviders,
        IServiceProvider serviceProvider
    ) {
        Dictionary<SupportedProviders, IMusicLookupService> results = [];
        foreach (SupportedProviders provider in enabledProviders) {
            switch (provider) {
                case SupportedProviders.AppleMusic:
                    results.Add( SupportedProviders.AppleMusic, serviceProvider.GetRequiredService<Providers.AppleMusic.AppleMusicLookupService>( ) );
                    break;
                case SupportedProviders.Spotify:
                    results.Add( SupportedProviders.Spotify, serviceProvider.GetRequiredService<Providers.Spotify.SpotifyLookupService>( ) );
                    break;
                case SupportedProviders.Tidal:
                    results.Add( SupportedProviders.Tidal, serviceProvider.GetRequiredService<Providers.Tidal.TidalLookupService>( ) );
                    break;
            }
        }
        return results;
    }

    #endregion Media Link Resolver Registration

    #region Card Services Registration

    /// <summary>
    /// Registers the card-related services: the singleton <see cref="IOpenGraphCardService"/> and
    /// <see cref="IPlaylistService"/>, plus the hosted <c>PlaylistCleanupService</c> that purges
    /// expired anonymous playlists.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="domain">The public domain used to build card and playlist URLs.</param>
    /// <param name="cardCacheExpirationHours">How long a stored card is retained, in hours; must be greater than zero.</param>
    /// <param name="cardCacheCleanupInterval">How often the lazy expiry sweep runs, expressed as a count of store operations; must be greater than zero.</param>
    /// <param name="cardCacheMaxEntries">The maximum number of entries retained in the in-memory store before nearest-expiry eviction begins; must be greater than zero.</param>
    /// <returns>The same <paramref name="services"/>, to allow call chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="cardCacheExpirationHours"/>, <paramref name="cardCacheCleanupInterval"/>,
    /// or <paramref name="cardCacheMaxEntries"/> is not greater than zero.
    /// </exception>
    public static IServiceCollection AddCardServices(
        this IServiceCollection services,
        string domain,
        int cardCacheExpirationHours,
        int cardCacheCleanupInterval,
        int cardCacheMaxEntries
    ) {
        // Validate card cache settings
        if (cardCacheExpirationHours <= 0) {
            throw new InvalidOperationException( $"CardCacheExpirationHours must be greater than zero. Current value: {cardCacheExpirationHours}" );
        }
        if (cardCacheCleanupInterval <= 0) {
            throw new InvalidOperationException( $"CardCacheCleanupInterval must be greater than zero. Current value: {cardCacheCleanupInterval}" );
        }
        if (cardCacheMaxEntries <= 0) {
            throw new InvalidOperationException( $"CardCacheMaxEntries must be greater than zero. Current value: {cardCacheMaxEntries}" );
        }

        // OpenGraph card service
        _ = services.AddSingleton<IOpenGraphCardService, OpenGraphCardService>(
            _ => new OpenGraphCardService(
                domain,
                cardCacheExpirationHours,
                cardCacheCleanupInterval,
                cardCacheMaxEntries
            )
        );

        // Playlist service (singleton with DbContextFactory for thread-safe database access)
        _ = services.AddSingleton<IPlaylistService, PlaylistService>(
            p => new PlaylistService( domain, p.GetRequiredService<IDbContextFactory<ApplicationDbContext>>( ) )
        );

        // Register playlist cleanup background service
        _ = services.AddHostedService<PlaylistCleanupService>( );

        return services;
    }

    #endregion Card Services Registration

    #region QR Code Service Registration

    /// <summary>
    /// Registers the singleton <see cref="IQrCodeService"/> used to render URLs to QR-code data URIs.
    /// </summary>
    /// <param name="services">The service collection to add the registration to.</param>
    /// <returns>The same <paramref name="services"/>, to allow call chaining.</returns>
    public static IServiceCollection AddQrCodeService( this IServiceCollection services ) {
        _ = services.AddSingleton<IQrCodeService, QrCodeService>( );

        return services;
    }

    #endregion QR Code Service Registration

    #region OpenGraph Extensions

    /// <summary>
    /// Builds the OpenGraph meta-tag dictionary for a resolved multi-provider result. Walks the
    /// per-provider results (ordered by provider) to pick a title, artist, artwork image, and
    /// external id, preferring the provider marked primary, and emits <c>og:*</c>,
    /// <c>music:musician</c>, and a <c>theme-color</c> drawn from the primary provider's brand color.
    /// </summary>
    /// <param name="result">The resolved result to describe.</param>
    /// <returns>
    /// A dictionary of meta-tag name to value. The external id is labeled <c>UPC</c> for albums and
    /// <c>ISRC</c> for tracks; image tags are omitted when no artwork is available.
    /// </returns>
    public static Dictionary<string, string> ToOpenGraphMetadata( this MediaLinkResult result ) {
        Dictionary<string, string> metadata = [];

        string title = string.Empty;
        string description = string.Empty;
        string image = string.Empty;
        bool isAlbum = false;
        string artist = string.Empty;
        string externalId = string.Empty;
        SupportedProviders? primaryProvider = null;

        // Extract information from results, prioritizing the primary result
        foreach ((SupportedProviders provider, MusicLookupResult dto) in result.Results.OrderBy( kv => kv.Key )) {

            if (string.IsNullOrWhiteSpace( image ) && !string.IsNullOrWhiteSpace( dto.ArtUrl )) {
                image = dto.ArtUrl;
            }

            if (string.IsNullOrWhiteSpace( externalId ) && !string.IsNullOrWhiteSpace( dto.ExternalId )) {
                externalId = dto.ExternalId;
            }

            if (string.IsNullOrWhiteSpace( title )) {
                title = dto.Title;
                isAlbum = dto.IsAlbum ?? false;
                artist = dto.Artist;
            }

            if (dto.IsPrimary) {
                title = dto.Title;
                isAlbum = dto.IsAlbum ?? false;
                artist = dto.Artist;
                primaryProvider = provider;
                if (string.IsNullOrWhiteSpace( dto.ArtUrl ) == false) {
                    image = dto.ArtUrl;
                }
                if (string.IsNullOrWhiteSpace( dto.ExternalId ) == false) {
                    externalId = dto.ExternalId;
                }
            }
        }

        // Build description with artist and ISRC/UPC (similar to old Discord embed format)
        description = $"Artist: {artist}";
        if (!string.IsNullOrWhiteSpace( externalId )) {
            string externalIdPrefix = isAlbum ? "UPC" : "ISRC";
            description += $"\n{externalIdPrefix}: {externalId}";
        }

        // Set OpenGraph properties
        metadata["og:type"] = "music." + (isAlbum ? "album" : "song");
        metadata["og:title"] = title;
        metadata["og:description"] = description;

        if (!string.IsNullOrWhiteSpace( image )) {
            metadata["og:image"] = image;
            metadata["og:image:alt"] = $"{title} artwork";
        }

        // Add music-specific metadata
        metadata["music:musician"] = artist;

        // Add theme color based on primary provider (for Discord embed color)
        if (primaryProvider.HasValue) {
            string themeColor = GetPrimaryProviderColorHex( primaryProvider.Value );
            metadata["theme-color"] = themeColor;
        }

        return metadata;
    }

    /// <summary>
    /// Returns the brand hex color used for the OpenGraph <c>theme-color</c> tag. Unlike
    /// <see cref="GetProviderHexColor"/>, this is theme-agnostic and always returns Tidal's white;
    /// unknown providers fall back to the default indigo.
    /// </summary>
    /// <param name="provider">The primary provider whose brand color to use.</param>
    /// <returns>The hex color string, including the leading <c>#</c>.</returns>
    private static string GetPrimaryProviderColorHex( SupportedProviders provider )
        => provider switch {
            SupportedProviders.AppleMusic => "#D60017",
            SupportedProviders.Spotify => "#1ED760",
            SupportedProviders.Tidal => "#FFFFFF",
            _ => "#6366F1"  // Default purple
        };

    #endregion OpenGraph Extensions

    /// <summary>
    /// Registers the full BridgeBeats application service set in one call: the media-link resolver,
    /// the card services, and the QR-code service. This is the top-level composition-root entry point
    /// for the web app.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="enabledProviders">The providers the resolver should query.</param>
    /// <param name="useCaching">
    /// <see langword="true"/> to use the cached, queue-orchestrated resolver path;
    /// <see langword="false"/> for the direct in-process path.
    /// </param>
    /// <param name="domain">The public domain used to build card and playlist URLs.</param>
    /// <param name="cardCacheExpirationHours">How long a stored card is retained, in hours; must be greater than zero.</param>
    /// <param name="cardCacheCleanupInterval">How often the lazy card-expiry sweep runs, as a count of store operations; must be greater than zero.</param>
    /// <param name="cardCacheMaxEntries">The maximum number of entries retained in the in-memory store before nearest-expiry eviction begins; must be greater than zero.</param>
    /// <returns>The same <paramref name="services"/>, to allow call chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="cardCacheExpirationHours"/>, <paramref name="cardCacheCleanupInterval"/>,
    /// or <paramref name="cardCacheMaxEntries"/> is not greater than zero.
    /// </exception>
    public static IServiceCollection AddBridgeBeatsServices(
        this IServiceCollection services,
        HashSet<SupportedProviders> enabledProviders,
        bool useCaching,
        string domain,
        int cardCacheExpirationHours,
        int cardCacheCleanupInterval,
        int cardCacheMaxEntries
    ) {
        _ = services.AddMediaLinkResolver( enabledProviders, useCaching );
        _ = services.AddCardServices( domain, cardCacheExpirationHours, cardCacheCleanupInterval, cardCacheMaxEntries );
        _ = services.AddQrCodeService( );

        return services;
    }

}
