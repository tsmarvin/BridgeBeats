namespace BridgeBeats.Web.Controllers;

/// <summary>
/// Source-generated <see cref="Microsoft.Extensions.Logging.LoggerMessageAttribute"/> logging methods
/// for <see cref="AppleMusicController"/>.
/// </summary>
public partial class AppleMusicController {
    /// <summary>
    /// Logs that storing the Apple Music token for a user failed.
    /// </summary>
    /// <param name="userId">The id of the user whose token could not be stored.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerStoreTokenFailed,
        Level = LogLevel.Error,
        Message = "Failed to store Apple Music token for user {UserId}" )]
    private partial void LogStoreTokenFailed( string userId );

    /// <summary>
    /// Logs that the Apple Music token was stored for a user.
    /// </summary>
    /// <param name="userId">The id of the user whose token was stored.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerTokenStored,
        Level = LogLevel.Information,
        Message = "Apple Music token stored for user {UserId}" )]
    private partial void LogTokenStored( string userId );

    /// <summary>
    /// Logs that the Apple Music API returned an error status while retrieving a user's playlists.
    /// </summary>
    /// <param name="userId">The id of the user whose playlists were requested.</param>
    /// <param name="statusCode">The HTTP status code returned by the Apple Music API.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerGetPlaylistsFailed,
        Level = LogLevel.Error,
        Message = "Failed to retrieve playlists for user {UserId}: {StatusCode}" )]
    private partial void LogGetPlaylistsFailed( string userId, int statusCode );

    /// <summary>
    /// Logs an unexpected error while retrieving a user's Apple Music playlists.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="userId">The id of the user whose playlists were requested.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerGetPlaylistsError,
        Level = LogLevel.Error,
        Message = "Error retrieving playlists for user {UserId}" )]
    private partial void LogGetPlaylistsError( Exception ex, string userId );

    /// <summary>
    /// Logs that resolving the ATProto URI from the cache failed with an invalid-operation error and is being skipped.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerCacheInvalidOp,
        Level = LogLevel.Warning,
        Message = "Failed to retrieve ATProto URI from cache due to invalid operation, continuing without it" )]
    private partial void LogCacheInvalidOp( Exception ex );

    /// <summary>
    /// Logs that resolving the ATProto URI from the cache failed with an argument error and is being skipped.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerCacheArgError,
        Level = LogLevel.Warning,
        Message = "Failed to retrieve ATProto URI from cache due to argument error, continuing without it" )]
    private partial void LogCacheArgError( Exception ex );

    /// <summary>
    /// Logs that resolving the ATProto URI from the cache failed and is being skipped.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerCacheError,
        Level = LogLevel.Warning,
        Message = "Failed to retrieve ATProto URI from cache, continuing without it" )]
    private partial void LogCacheError( Exception ex );

    /// <summary>
    /// Logs that the Apple Music API returned an error status while retrieving a playlist's tracks.
    /// </summary>
    /// <param name="userId">The id of the user whose playlist tracks were requested.</param>
    /// <param name="statusCode">The HTTP status code returned by the Apple Music API.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerGetTracksFailed,
        Level = LogLevel.Error,
        Message = "Failed to retrieve playlist tracks for user {UserId}: {StatusCode}" )]
    private partial void LogGetTracksFailed( string userId, int statusCode );

    /// <summary>
    /// Logs that a playlist exceeded the 1000-track processing cap and was truncated.
    /// </summary>
    /// <param name="playlistId">The id of the oversized playlist.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerPlaylistTooLarge,
        Level = LogLevel.Warning,
        Message = "Playlist {PlaylistId} exceeds 1000 tracks, processing limited to first 1000 tracks" )]
    private partial void LogPlaylistTooLarge( string playlistId );

    /// <summary>
    /// Logs an unexpected error while processing a playlist for a user.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="userId">The id of the user whose playlist was being processed.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerProcessPlaylistError,
        Level = LogLevel.Error,
        Message = "Error processing playlist for user {UserId}" )]
    private partial void LogProcessPlaylistError( Exception ex, string userId );

    /// <summary>
    /// Logs an error while processing an individual song during result streaming.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="songId">The (sanitized) song id that failed to process.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerProcessSongError,
        Level = LogLevel.Error,
        Message = "Error processing song {SongId}" )]
    private partial void LogProcessSongError( Exception ex, string songId );

    /// <summary>Logs an unexpected failure while writing the playlist result stream.</summary>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AppleMusicControllerPlaylistResultsStreamError,
        Level = LogLevel.Error,
        Message = "Error writing Apple Music playlist result stream" )]
    private partial void LogPlaylistResultsStreamError( Exception ex );
}
