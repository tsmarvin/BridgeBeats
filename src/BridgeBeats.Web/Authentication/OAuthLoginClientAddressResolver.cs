using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace BridgeBeats.Web.Authentication;

/// <summary>
/// Uses the client address supplied by the configured reverse proxy for OAuth login limits.
/// Other peers are always partitioned by their connection address.
/// </summary>
/// <param name="trustedProxyHost">
/// DNS host of the reverse proxy trusted to supply <c>X-Forwarded-For</c>, or null to disable
/// forwarded addresses entirely.
/// </param>
/// <param name="resolveHost">
/// Resolves the trusted proxy host to its current addresses. Defaults to the system resolver;
/// tests supply a deterministic replacement.
/// </param>
/// <param name="loggerFactory">
/// Supplies the logger for lookup failures. Defaults to a no-op logger factory.
/// </param>
internal sealed class OAuthLoginClientAddressResolver(
    string? trustedProxyHost,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null,
    ILoggerFactory? loggerFactory = null
) : BackgroundService {
    private readonly string? _trustedProxyHost = string.IsNullOrWhiteSpace( trustedProxyHost ) ? null : trustedProxyHost;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost =
        resolveHost ?? ( ( host, token ) => Dns.GetHostAddressesAsync( host, token ) );
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<OAuthLoginClientAddressResolver>( );
    private IPAddress[] _trustedProxyAddresses = [];

    /// <summary>Returns the address used for the login rate limit.</summary>
    internal string GetPartitionKey( HttpContext context ) {
        IPAddress? peer = Normalize( context.Connection.RemoteIpAddress );
        if (peer is null) { return "unknown"; }

        IPAddress[] trustedAddresses = Volatile.Read( ref _trustedProxyAddresses );
        if (trustedAddresses.Contains( peer ) &&
            context.Request.Headers.TryGetValue( "X-Forwarded-For", out Microsoft.Extensions.Primitives.StringValues forwarded ) &&
            forwarded.Count == 1 && IPAddress.TryParse( forwarded[0], out IPAddress? client )) {
            return Normalize( client )!.ToString( );
        }

        return peer.ToString( );
    }

    /// <summary>
    /// Refreshes the proxy's current address. Any failed lookup disables forwarded addresses so a
    /// proxy that cannot be resolved never gains the ability to choose a login rate-limit bucket.
    /// Shutdown cancellation propagates so the host stops promptly.
    /// </summary>
    internal async Task RefreshAsync( CancellationToken cancellationToken ) {
        if (_trustedProxyHost is null) { return; }
        try {
            IPAddress[] addresses = await _resolveHost( _trustedProxyHost, cancellationToken );
            Volatile.Write( ref _trustedProxyAddresses, [.. addresses.Select( Normalize ).Where( address => address is not null ).Cast<IPAddress>( )] );
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            // Clear before logging: a resolver that throws must never keep stale trust, even if the
            // logging pipeline itself fails.
            Volatile.Write( ref _trustedProxyAddresses, [] );
            // Neither the host name nor the resolver's exception is recorded here. The warning is
            // actionable without them because trust is already off, and both can carry details of
            // the deployment's private network topology into logs.
            _logger.LogWarning(
                "Could not resolve the trusted OAuth proxy host; forwarded client addresses are ignored for login rate limiting." );
        }
    }

    /// <summary>Tracks Docker DNS changes when the reverse proxy container restarts.</summary>
    protected override async Task ExecuteAsync( CancellationToken stoppingToken ) {
        if (_trustedProxyHost is null) { return; }
        while (!stoppingToken.IsCancellationRequested) {
            await RefreshAsync( stoppingToken );
            try {
                await Task.Delay( TimeSpan.FromSeconds( 15 ), stoppingToken );
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            }
        }
    }

    private static IPAddress? Normalize( IPAddress? address ) =>
        address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4( ) : address;
}
