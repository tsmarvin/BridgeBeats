using System.Net;

namespace BridgeBeats.Web.Authentication;

/// <summary>
/// Uses the client address supplied by the configured reverse proxy for OAuth login limits.
/// Other peers are always partitioned by their connection address.
/// </summary>
internal sealed class OAuthLoginClientAddressResolver( string? trustedProxyHost ) : BackgroundService {
    private readonly string? _trustedProxyHost = string.IsNullOrWhiteSpace( trustedProxyHost ) ? null : trustedProxyHost;
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

    /// <summary>Refreshes the proxy's current address; a failed lookup disables forwarded addresses.</summary>
    internal async Task RefreshAsync( CancellationToken cancellationToken ) {
        if (_trustedProxyHost is null) { return; }
        try {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync( _trustedProxyHost, cancellationToken );
            Volatile.Write( ref _trustedProxyAddresses, [.. addresses.Select( Normalize ).Where( address => address is not null ).Cast<IPAddress>( )] );
        } catch (System.Net.Sockets.SocketException) {
            Volatile.Write( ref _trustedProxyAddresses, [] );
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
