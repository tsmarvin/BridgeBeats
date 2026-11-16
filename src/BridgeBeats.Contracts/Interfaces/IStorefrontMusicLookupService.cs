using BridgeBeats.Contracts.DTOs;

namespace BridgeBeats.Contracts.Interfaces;

/// <summary>
/// Optional provider capability for lookups scoped to a specific catalog storefront.
/// </summary>
/// <remarks>
/// Providers whose catalogs vary by country implement this interface in addition to
/// <see cref="IMusicLookupService"/>. Callers can continue using the base interface when no
/// storefront is available.
/// </remarks>
public interface IStorefrontMusicLookupService : IMusicLookupService {

    /// <summary>Resolves a track by ISRC in the supplied storefront.</summary>
    Task<MusicLookupResult?> GetInfoByISRCAsync( string isrc, string storefront );

    /// <summary>Resolves an album by UPC in the supplied storefront.</summary>
    Task<MusicLookupResult?> GetInfoByUPCAsync( string upc, string storefront );

    /// <summary>Resolves a provider-native id in the supplied storefront.</summary>
    Task<MusicLookupResult?> GetInfoByIDAsync( string providerId, bool isAlbum, string storefront );
}
