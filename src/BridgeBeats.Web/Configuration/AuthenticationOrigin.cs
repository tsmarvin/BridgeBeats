namespace BridgeBeats.Web.Configuration;

internal sealed class AuthenticationOrigin {
    private AuthenticationOrigin( Uri origin, string? cookieDomain ) {
        Origin = origin;
        CookieDomain = cookieDomain;
    }

    internal Uri Origin { get; }

    internal string? CookieDomain { get; }

    internal static AuthenticationOrigin? Parse( string? domain ) {
        if (string.IsNullOrWhiteSpace( domain )) {
            return null;
        }

        string value = domain.Trim( );
        string absoluteValue = value.Contains( "://", StringComparison.Ordinal )
            ? value
            : $"https://{value}";
        if (!Uri.TryCreate( absoluteValue, UriKind.Absolute, out Uri? origin )) {
            throw new InvalidOperationException( "BridgeBeats:Domain must be a host or HTTPS origin with an optional port and no path, query, or fragment." );
        }

        if (origin.Host.EndsWith( "..", StringComparison.Ordinal )) {
            throw new InvalidOperationException( "BridgeBeats:Domain must be a host or HTTPS origin with an optional port and no path, query, or fragment." );
        }
        if (origin.Host.EndsWith( ".", StringComparison.Ordinal )) {
            origin = new UriBuilder( origin ) { Host = origin.Host[..^1] }.Uri;
        }

        if (!origin.Scheme.Equals( Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase ) ||
            !string.IsNullOrEmpty( origin.UserInfo ) ||
            origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty( origin.Query ) ||
            !string.IsNullOrEmpty( origin.Fragment ) ||
            !IsValidHost( origin.Host ) ||
            !HasValidPort( absoluteValue )) {
            throw new InvalidOperationException( "BridgeBeats:Domain must be a host or HTTPS origin with an optional port and no path, query, or fragment." );
        }

        string? cookieDomain = Uri.CheckHostName( origin.Host ) == UriHostNameType.Dns &&
                               origin.Host.Contains( '.', StringComparison.Ordinal )
            ? $".{origin.Host}"
            : null;
        return new AuthenticationOrigin( origin, cookieDomain );
    }

    private static bool IsValidHost( string host ) =>
        !string.IsNullOrWhiteSpace( host ) && Uri.CheckHostName( host ) != UriHostNameType.Unknown;

    private static bool HasValidPort( string absoluteValue ) {
        string authority = absoluteValue[(absoluteValue.IndexOf( "://", StringComparison.Ordinal ) + 3)..]
            .Split( '/', '?', '#' )[0];
        if (authority.StartsWith( "[", StringComparison.Ordinal )) {
            int bracketIndex = authority.IndexOf( ']' );
            if (bracketIndex < 0 || bracketIndex == authority.Length - 1) {
                return bracketIndex >= 0;
            }
            return authority[bracketIndex + 1] == ':' && int.TryParse( authority[(bracketIndex + 2)..], out int ipv6Port ) && ipv6Port is > 0 and <= 65535;
        }

        int portSeparator = authority.LastIndexOf( ':' );
        if (portSeparator < 0) {
            return true;
        }

        return int.TryParse( authority[(portSeparator + 1)..], out int port ) && port is > 0 and <= 65535;
    }
}
