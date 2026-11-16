using BridgeBeats.Contracts.Constants;
using BridgeBeats.Web.Logging;

namespace BridgeBeats.Web.Middleware;

/// <summary>
/// Restricts the liveness endpoint (<see cref="BridgeBeats.Contracts.Constants.EndpointPaths.Alive"/>)
/// to callers originating from internal networks, returning HTTP 403 for any external request.
/// </summary>
/// <remarks>
/// Requests to paths other than the liveness endpoint pass through untouched. A request is treated
/// as internal when its remote IP is loopback or falls within an IPv4 private range (10.0.0.0/8,
/// 172.16.0.0/12, or 192.168.0.0/16). IPv4-mapped IPv6 addresses (e.g. <c>::ffff:10.0.0.5</c>)
/// are normalised to their embedded IPv4 form before classification, so dual-stack internal callers
/// are admitted correctly. All other callers receive a 403 response and a blocked-access warning is
/// logged.
/// </remarks>
/// <param name="next">The next delegate in the request pipeline.</param>
/// <param name="logger">Logger used to record blocked external-access attempts.</param>
public partial class HealthEndpointAuthorizationMiddleware(
    RequestDelegate next,
    ILogger<HealthEndpointAuthorizationMiddleware> logger
) {
    /// <summary>
    /// Allows the liveness endpoint through only for internal callers; external callers receive HTTP 403.
    /// Any other path is forwarded to the next middleware unchanged.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the request has been forwarded or rejected.</returns>
    public async Task InvokeAsync( HttpContext context ) {
        // Only intercept requests to liveness endpoint
        if (!context.Request.Path.Equals( EndpointPaths.Alive, StringComparison.OrdinalIgnoreCase )) {
            await next( context );
            return;
        }

        // Check if the request is coming from localhost or internal Docker network
        string? remoteIp = context.Connection.RemoteIpAddress?.ToString( );

        // Allow localhost and Docker internal IPs (172.x.x.x, 10.x.x.x ranges)
        // Also allow requests from the same host (::1 for IPv6 localhost)
        if (IsInternalRequest( remoteIp )) {
            await next( context );
            return;
        }

        // Block external access
        LogBlockedExternalAccess( logger, remoteIp );
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync( "Forbidden: Liveness endpoint is not publicly accessible." );
    }

    /// <summary>
    /// Logs a warning when an external caller is blocked from reaching the liveness endpoint.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="ip">The remote IP address that was blocked, or <c>null</c> if it could not be determined.</param>
    [LoggerMessage(
        EventId = LogEventIds.Middleware.HealthEndpointAuthorizationMiddlewareBlockedExternalAccess,
        Level = LogLevel.Warning,
        Message = "Blocked external access to liveness endpoint from {IP}" )]
    private static partial void LogBlockedExternalAccess( ILogger logger, string? ip );

    /// <summary>
    /// Determines whether the supplied IP address represents an internal caller (loopback or an
    /// IPv4 private range). IPv4-mapped IPv6 addresses (e.g. <c>::ffff:10.0.0.5</c>) are normalised
    /// to their embedded IPv4 form before classification; pure IPv6 addresses other than loopback
    /// are treated as external.
    /// </summary>
    /// <param name="ipAddress">The remote IP address as a string, or <c>null</c> when unavailable.</param>
    /// <returns><c>true</c> if the address is loopback or in a private IPv4 range; otherwise <c>false</c>.</returns>
    private static bool IsInternalRequest( string? ipAddress ) {
        if (string.IsNullOrWhiteSpace( ipAddress )) {
            return false;
        }

        // Try to parse the IP address
        if (!System.Net.IPAddress.TryParse( ipAddress, out System.Net.IPAddress? parsedIp )) {
            return false;
        }

        // Normalise IPv4-mapped IPv6 addresses (e.g. ::ffff:10.0.0.5 → 10.0.0.5) so that
        // dual-stack listeners presenting internal callers as IPv6 are classified correctly.
        if (parsedIp.IsIPv4MappedToIPv6) {
            parsedIp = parsedIp.MapToIPv4( );
        }

        // Check for localhost (IPv4 and IPv6)
        if (System.Net.IPAddress.IsLoopback( parsedIp )) {
            return true;
        }

        // Convert to bytes for range checking (IPv4 only; pure IPv6 non-loopback is external)
        if (parsedIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) {
            return false;
        }

        byte[] bytes = parsedIp.GetAddressBytes( );

        // RFC 1918 private network range: 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) {
            return true;
        }

        // Private network: 10.0.0.0/8 (10.0.0.0 - 10.255.255.255)
        if (bytes[0] == 10) {
            return true;
        }

        // Private network: 192.168.0.0/16 (192.168.0.0 - 192.168.255.255)
        return bytes[0] == 192 && bytes[1] == 168;
    }
}
