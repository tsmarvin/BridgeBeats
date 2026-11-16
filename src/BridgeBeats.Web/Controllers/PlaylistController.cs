using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Infrastructure.Utilities;
using BridgeBeats.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace BridgeBeats.Web.Controllers;

/// <summary>
/// Serves playlist pages and creation, rooted at <c>playlist</c>. Creates playlists from a set of card ids
/// and rkeys, and renders a playlist's page or embeddable variant, regenerating missing cards from their
/// rkeys when needed.
/// </summary>
/// <param name="playlistService">Optional playlist service used to create and retrieve playlists and to supply the public domain; when disabled, playlist actions report unavailability.</param>
/// <param name="cardService">Optional Open Graph card service used to retrieve and re-store card results.</param>
/// <param name="mediaLinkService">Optional media-link service used to regenerate a card from its ISRC or UPC rkey.</param>
/// <param name="qrCodeService">Service that generates QR-code data URIs for embeddable playlists.</param>
/// <param name="logger">Optional logger for card regeneration and mismatch warnings.</param>
[Route( "playlist" )]
public partial class PlaylistController( IPlaylistService? playlistService, IOpenGraphCardService? cardService, IMediaLinkService? mediaLinkService, IQrCodeService qrCodeService, ILogger<PlaylistController>? logger = null ) : Controller {

    /// <summary>Optional playlist service used to create and retrieve playlists and to supply the public domain.</summary>
    private readonly IPlaylistService? _playlistService = playlistService;
    /// <summary>Optional Open Graph card service used to retrieve and re-store card results.</summary>
    private readonly IOpenGraphCardService? _cardService = cardService;
    /// <summary>Optional media-link service used to regenerate a card from its ISRC or UPC rkey.</summary>
    private readonly IMediaLinkService? _mediaLinkService = mediaLinkService;
    /// <summary>Service that generates QR-code data URIs for embeddable playlists.</summary>
    private readonly IQrCodeService _qrCodeService = qrCodeService;
    /// <summary>Optional logger for card regeneration and mismatch warnings.</summary>
    private readonly ILogger<PlaylistController>? _logger = logger;

    /// <summary>
    /// Creates a playlist from the supplied card ids and matching rkeys (up to 20 cards). When the caller is
    /// authenticated, the supplied title and description are stored and the playlist is associated with the
    /// user; anonymous callers create an untitled, unowned playlist.
    /// </summary>
    /// <param name="request">The creation payload (card ids, card rkeys, optional title and description) bound from the JSON request body.</param>
    /// <returns>
    /// HTTP POST <c>playlist/create</c>. <c>200 OK</c> with the playlist URL on success; <c>400 Bad Request</c>
    /// when the service is unavailable, the cards are missing or exceed 20, the rkeys do not match the ids, or
    /// an argument is invalid; <c>500</c> on an unexpected error. Requires a valid anti-forgery token.
    /// </returns>
    [HttpPost( "create" )]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePlaylist( [FromBody] CreatePlaylistRequest request ) {
        if (!ModelState.IsValid) {
            return BadRequest( ModelState );
        }

        if (_playlistService?.IsEnabled != true) {
            return BadRequest( new { error = "Playlist service not available" } );
        }

        if (request.CardIds == null || request.CardIds.Count == 0) {
            return BadRequest( new { error = "At least one card ID is required" } );
        }

        if (request.CardIds.Count > 20) {
            return BadRequest( new { error = "Playlist cannot contain more than 20 cards" } );
        }

        if (request.CardRkeys == null || request.CardRkeys.Count != request.CardIds.Count) {
            return BadRequest( new { error = "Card rkeys must match card IDs" } );
        }

        try {
            // Get user ID if authenticated
            string? userId = User?.Identity?.IsAuthenticated == true
                                ? User.FindFirst( ClaimTypes.NameIdentifier )?.Value
                                : null;

            // Only allow title and description for authenticated users
            string? title = userId != null ? request.Title : null;
            string? description = userId != null ? request.Description : null;

            string playlistUrl = await _playlistService.CreatePlaylistAsync(
                                            request.CardIds,
                                            request.CardRkeys,
                                            title,
                                            description,
                                            userId
                                        );

            return Ok( new { playlistUrl } );
        } catch (ArgumentException ex) {
            return BadRequest( new { error = ex.Message } );
        } catch (Exception ex) {
            LogCreateError( ex );
            return StatusCode( 500, new { error = "Failed to create playlist" } );
        }
    }

    /// <summary>
    /// Renders a playlist page for the given id. Each card is loaded from the card service or regenerated from
    /// its <c>track:</c>/<c>album:</c> rkey via ISRC/UPC lookup, then projected into the view model using the
    /// primary provider's details. Cards that cannot be loaded or regenerated are skipped.
    /// </summary>
    /// <param name="id">The playlist id from the route.</param>
    /// <returns>
    /// HTTP GET <c>playlist/{id}</c>. The playlist view on success; <c>404 Not Found</c> when the service is
    /// unavailable or the playlist is unknown or expired.
    /// </returns>
    [HttpGet( "{id}" )]
    public async Task<IActionResult> Playlist( string id ) {
        if (_playlistService?.IsEnabled != true) {
            return NotFound( "Playlist service not available" );
        }

        PlaylistEntryDto? playlist = await _playlistService.GetPlaylistAsync( id );

        if (playlist == null) {
            return NotFound( "Playlist not found or expired" );
        }

        // Parse card IDs and rkeys
        string[] cardIds = playlist.CardIds.Split( ',', StringSplitOptions.RemoveEmptyEntries );
        string[] cardRkeys = playlist.CardRkeys.Split( ',', StringSplitOptions.RemoveEmptyEntries );
        List<PlaylistItemViewModel> items = [];

        // Ensure we have matching counts
        if (cardIds.Length != cardRkeys.Length) {
            LogMismatchedCards( id.SanitizeForLogging( ), cardIds.Length, cardRkeys.Length );
        }

        for (int i = 0; i < Math.Min( cardIds.Length, cardRkeys.Length ); i++) {
            string cardId = cardIds[i];
            string rkey = cardRkeys[i];

            // Try to get existing result from card service
            MediaLinkResult? result = _cardService?.GetResult( cardId );

            // If not found in cache, try to reconstruct from rkey
            if (result == null && _cardService != null && (rkey.StartsWith( "track:" ) || rkey.StartsWith( "album:" ))) {
                // Extract the external ID and type
                string[] parts = rkey.Split( ':', 2 );
                if (parts.Length == 2) {
                    bool isAlbum = parts[0] == "album";
                    string externalId = parts[1];

                    // Lookup by ISRC/UPC using media link service
                    result = isAlbum
                        ? await _mediaLinkService?.GetInfoByUPCAsync( externalId )!
                        : await _mediaLinkService?.GetInfoByISRCAsync( externalId )!;

                    // If we successfully recreated the result, store it back in the card service
                    if (result != null) {
                        _ = _cardService.StoreResult( result );
                        if (_logger?.IsEnabled( LogLevel.Information ) == true) {
                            string sanitizedCardId = cardId.SanitizeForLogging( );
                            string sanitizedRkey = rkey.SanitizeForLogging( );
                            string sanitizedId = id.SanitizeForLogging( );
                            LogCardRegenerated( sanitizedCardId, sanitizedRkey, sanitizedId );
                        }
                    }
                }
            }

            if (result == null) {
                if (_logger?.IsEnabled( LogLevel.Warning ) == true) {
                    string sanitizedCardId = cardId.SanitizeForLogging( );
                    string sanitizedRkey = rkey.SanitizeForLogging( );
                    string sanitizedId = id.SanitizeForLogging( );
                    LogCardLoadFailed( sanitizedCardId, sanitizedRkey, sanitizedId );
                }
                continue;
            }

            // Find primary result for display
            MusicLookupResult? primaryResult = result.Results.Values.FirstOrDefault( r => r.IsPrimary )
                                                   ?? result.Results.Values.FirstOrDefault( );

            if (primaryResult != null) {
                items.Add( new PlaylistItemViewModel {
                    CardId = cardId,
                    CardUrl = $"https://{_cardService?.Domain}/card/{cardId}",
                    Result = result,
                    Title = primaryResult.Title ?? "Unknown Title",
                    Artist = primaryResult.Artist ?? "Unknown Artist",
                    ArtUrl = primaryResult.ArtUrl,
                    IsAlbum = primaryResult.IsAlbum == true
                } );
            }
        }

        PlaylistViewModel viewModel = new( ) {
            PlaylistId = playlist.PlaylistId,
            Title = playlist.Title ?? "BridgeBeats",
            Description = playlist.Description,
            Items = items,
            Domain = _playlistService.Domain
        };

        return View( viewModel );
    }

    /// <summary>
    /// Renders the embeddable playlist view for the given id, optionally including a QR code that links to the
    /// embed. Cards are loaded or regenerated the same way as the full playlist page.
    /// </summary>
    /// <param name="id">The playlist id from the route.</param>
    /// <param name="qr">When true, generates and includes a QR-code data URI; bound from the query string. Defaults to false.</param>
    /// <returns>
    /// HTTP GET <c>playlist/{id}/embed</c>. The playlist view on success; <c>404 Not Found</c> when the
    /// service is unavailable or the playlist is unknown or expired.
    /// </returns>
    [HttpGet( "{id}/embed" )]
    public async Task<IActionResult> Embed( string id, [FromQuery] bool qr = false ) {
        if (_playlistService?.IsEnabled != true) {
            return NotFound( "Playlist service not available" );
        }

        PlaylistEntryDto? playlist = await _playlistService.GetPlaylistAsync( id );

        if (playlist == null) {
            return NotFound( "Playlist not found or expired" );
        }

        // Parse card IDs and rkeys
        string[] cardIds = playlist.CardIds.Split( ',', StringSplitOptions.RemoveEmptyEntries );
        string[] cardRkeys = playlist.CardRkeys.Split( ',', StringSplitOptions.RemoveEmptyEntries );
        List<PlaylistItemViewModel> items = [];

        for (int i = 0; i < Math.Min( cardIds.Length, cardRkeys.Length ); i++) {
            string cardId = cardIds[i];
            string rkey = cardRkeys[i];

            MediaLinkResult? result = _cardService?.GetResult( cardId );

            // Try to regenerate if not in cache
            if (result == null && _cardService != null && (rkey.StartsWith( "track:" ) || rkey.StartsWith( "album:" ))) {
                string[] parts = rkey.Split( ':', 2 );
                if (parts.Length == 2) {
                    bool isAlbum = parts[0] == "album";
                    string externalId = parts[1];

                    result = isAlbum
                        ? await _mediaLinkService?.GetInfoByUPCAsync( externalId )!
                        : await _mediaLinkService?.GetInfoByISRCAsync( externalId )!;

                    if (result != null) {
                        _ = _cardService.StoreResult( result );
                        if (_logger?.IsEnabled( LogLevel.Information ) == true) {
                            string sanitizedCardId = cardId.SanitizeForLogging( );
                            string sanitizedId = id.SanitizeForLogging( );
                            LogEmbedCardRegenerated( sanitizedCardId, sanitizedId );
                        }
                    }
                }
            }

            if (result == null) {
                if (_logger?.IsEnabled( LogLevel.Warning ) == true) {
                    string sanitizedCardId = cardId.SanitizeForLogging( );
                    string sanitizedId = id.SanitizeForLogging( );
                    LogEmbedCardLoadFailed( sanitizedCardId, sanitizedId );
                }
                continue;
            }

            MusicLookupResult? primaryResult = result.Results.Values.FirstOrDefault( r => r.IsPrimary )
                                                   ?? result.Results.Values.FirstOrDefault( );

            if (primaryResult != null) {
                items.Add( new PlaylistItemViewModel {
                    CardId = cardId,
                    CardUrl = $"https://{_cardService?.Domain}/card/{cardId}",
                    Result = result,
                    Title = primaryResult.Title ?? "Unknown Title",
                    Artist = primaryResult.Artist ?? "Unknown Artist",
                    ArtUrl = primaryResult.ArtUrl,
                    IsAlbum = primaryResult.IsAlbum == true
                } );
            }
        }

        // Generate QR code data URI if requested
        string? qrCodeDataUri = null;
        if (qr) {
            string embedUrl = $"https://{_playlistService.Domain}/playlist/{id}/embed";
            qrCodeDataUri = _qrCodeService.GenerateQrCodeDataUri( embedUrl );
        }

        PlaylistViewModel viewModel = new( ) {
            PlaylistId = playlist.PlaylistId,
            Title = playlist.Title ?? "BridgeBeats Playlist",
            Description = playlist.Description,
            Items = items,
            Domain = _playlistService.Domain,
            QrCodeDataUri = qrCodeDataUri
        };

        return View( viewModel );
    }

    /// <summary>
    /// Request payload for creating a playlist.
    /// </summary>
    public record CreatePlaylistRequest {
        /// <summary>The card ids to include in the playlist, in order (up to 20).</summary>
        public List<string> CardIds { get; init; } = [];

        /// <summary>The rkeys corresponding to each card id, used to regenerate cards that are no longer cached.</summary>
        public List<string> CardRkeys { get; init; } = [];

        /// <summary>The optional playlist title; stored only for authenticated callers. Maximum 200 characters.</summary>
        [StringLength( 200, MinimumLength = 1 )]
        public string? Title { get; init; }

        /// <summary>The optional playlist description; stored only for authenticated callers. Maximum 1000 characters.</summary>
        [StringLength( 1000, MinimumLength = 1 )]
        public string? Description { get; init; }
    }
}
