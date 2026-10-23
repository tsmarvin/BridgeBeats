using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Logging;
using idunno.AtProto;
using idunno.Bluesky;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace BridgeBeats.Core.Infrastructure.Identity;

/// <summary>
/// Service for managing ATProto OAuth authentication flows. Implements the ATProto OAuth profile
/// with PAR, PKCE, and DPoP.
/// </summary>
/// <remarks>
/// The flow resolves a handle to its DID, PDS, and authorization server, then starts an authorization
/// request (via Pushed Authorization Requests when supported, otherwise a direct URL), completes the
/// code exchange, and supports refresh. Token-endpoint and metadata calls go through the named
/// <c>ATProtoOAuth</c> HttpClient, which is configured elsewhere with an SSRF-hardened primary handler
/// from the <c>idunno.Security</c> SSRF package; this class contains no SSRF logic of its own. Handle,
/// PDS, and authorization-server resolution use a default <c>BlueskyAgent</c> rather than that named
/// client. Pending-authorization secrets (the code verifier and DPoP key) are encrypted at rest by the
/// injected <see cref="IPersonalDataProtector"/>, the randomized personal-data protector.
/// </remarks>
public partial class ATProtoOAuthService : IATProtoOAuthService {

    /// <summary>
    /// Safety margin before token expiration to trigger refresh (30 seconds).
    /// </summary>
    private static readonly TimeSpan s_tokenExpirationMargin = TimeSpan.FromSeconds( 30 );

    /// <summary>
    /// How long to cache authorization server metadata (1 hour).
    /// </summary>
    private static readonly TimeSpan s_metadataCacheDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// OAuth scopes required for playlist operations.
    /// <list type="bullet">
    /// <item><description><c>atproto</c>: required for all ATProto OAuth.</description></item>
    /// <item><description><c>repo:link.bridgebeats.playlist</c>: granular permission for playlist record CRUD only.</description></item>
    /// </list>
    /// </summary>
    private static readonly string[] s_requiredScopes = ["atproto", "repo:link.bridgebeats.playlist"];

    /// <summary>
    /// The relative path to the OAuth callback endpoint.
    /// </summary>
    private const string OAuthCallbackPath = "/account/atproto-callback";

    /// <summary>
    /// The well-known path for client metadata. The client id must end with this path.
    /// </summary>
    private const string ClientMetadataPath = "/.well-known/client-metadata.json";

    /// <summary>
    /// The well-known path for authorization server metadata.
    /// </summary>
    private const string AuthServerMetadataPath = "/.well-known/oauth-authorization-server";

    /// <summary>
    /// Maximum number of retry attempts for DPoP nonce errors.
    /// </summary>
    private const int MaxDPoPNonceRetries = 2;

    /// <summary>
    /// Maximum number of distinct authorization server URIs to cache metadata for.
    /// Bounds process-wide memory growth on the anonymous OAuth login path: entries past this
    /// cap are dropped rather than cached, preventing resource-exhaustion via attacker-supplied handles.
    /// </summary>
    private const int MetadataCacheCapacity = 256;

    /// <summary>
    /// Process-wide bounded cache of authorization server metadata to avoid repeated discovery requests.
    /// Capped at <see cref="MetadataCacheCapacity"/> entries; each entry expires after
    /// <see cref="s_metadataCacheDuration"/> and is actively evicted by the cache runtime rather than
    /// merely skipped on read.
    /// </summary>
    private static readonly MemoryCache s_metadataCache = new( new MemoryCacheOptions {
        SizeLimit = MetadataCacheCapacity
    } );

    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly ILogger<ATProtoOAuthService> _logger;
    private readonly string _clientId;
    private readonly string _domain;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ATProtoSigningKeyProvider? _signingKeyProvider;
    private readonly IPersonalDataProtector _personalDataProtector;

    /// <summary>
    /// The client_assertion_type value for private_key_jwt authentication per RFC 7523.
    /// </summary>
    private const string ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>
    /// Initializes a new instance of the <see cref="ATProtoOAuthService"/> class.
    /// </summary>
    /// <param name="dbContextFactory">Factory for creating database contexts.</param>
    /// <param name="logger">The logger for flow diagnostics and errors.</param>
    /// <param name="clientId">The OAuth client_id URL; must be the client-metadata.json URL.</param>
    /// <param name="httpClientFactory">Factory used to obtain the named, SSRF-guarded <c>ATProtoOAuth</c> client.</param>
    /// <param name="personalDataProtector">The protector used to encrypt sensitive OAuth state data at rest.</param>
    /// <param name="signingKeyProvider">Optional signing key provider for confidential client assertions. When null, operates as a public client.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required dependency is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="clientId"/> does not end with the client-metadata path.</exception>
    public ATProtoOAuthService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        ILogger<ATProtoOAuthService> logger,
        string clientId,
        IHttpClientFactory httpClientFactory,
        IPersonalDataProtector personalDataProtector,
        ATProtoSigningKeyProvider? signingKeyProvider = null
    ) {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException( nameof( dbContextFactory ) );
        _logger = logger ?? throw new ArgumentNullException( nameof( logger ) );
        _clientId = clientId ?? throw new ArgumentNullException( nameof( clientId ) );
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException( nameof( httpClientFactory ) );
        _personalDataProtector = personalDataProtector ?? throw new ArgumentNullException( nameof( personalDataProtector ) );
        _signingKeyProvider = signingKeyProvider;

        // Extract base URL from client ID
        if (!clientId.EndsWith( ClientMetadataPath, StringComparison.OrdinalIgnoreCase )) {
            throw new ArgumentException( $"Client ID must end with '{ClientMetadataPath}'", nameof( clientId ) );
        }
        _domain = clientId[..^ClientMetadataPath.Length];
    }

    /// <summary>
    /// Starts the OAuth authorization flow for an ATProto handle.
    /// </summary>
    /// <remarks>
    /// Resolves the handle to its DID, PDS, and authorization server; generates the PKCE verifier and
    /// challenge, the state value, and a fresh DPoP key; persists an encrypted <see cref="AtProtoOAuthState"/>
    /// row; and builds the authorization URL using Pushed Authorization Requests when the server supports
    /// them, otherwise a direct URL.
    /// </remarks>
    /// <param name="handle">The ATProto handle to authenticate. A leading <c>@</c> is trimmed.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A tuple of the authorization URL to redirect the user to and the generated state value.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="handle"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when handle, PDS, or authorization-server resolution fails.</exception>
    public async Task<(Uri AuthorizationUrl, string State)> StartAuthorizationAsync(
        string handle,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( handle );

        // Build redirect URI from the configured domain (same as client-metadata.json and token exchange)
        string redirectUri = $"{_domain}{OAuthCallbackPath}";

        // Normalize handle (remove @ prefix if present)
        handle = handle.TrimStart( '@' ).Trim( );

        LogStartFlow( _logger, handle );

        // Create an agent to resolve the handle and build the OAuth URL
        using BlueskyAgent agent = new( );

        // Resolve handle to DID
        Did did = await agent.ResolveHandle( handle, cancellationToken )
            ?? throw new InvalidOperationException( $"Failed to resolve handle '{handle}' to a DID" );

        if (_logger.IsEnabled( LogLevel.Debug )) {
            string didString = did.ToString( );
            LogResolvedHandle( _logger, handle, didString );
        }

        // Resolve PDS URI
        Uri pdsUri = await agent.ResolvePds( did, cancellationToken )
            ?? throw new InvalidOperationException( $"Failed to resolve PDS for DID '{did}'" );

        if (_logger.IsEnabled( LogLevel.Debug )) {
            string didString = did.ToString( );
            string pdsUriString = pdsUri.ToString( );
            LogResolvedPds( _logger, didString, pdsUriString );
        }

        // Resolve authorization server
        Uri authorizationServer = await agent.ResolveAuthorizationServer( pdsUri, cancellationToken )
            ?? throw new InvalidOperationException( $"Failed to resolve authorization server for PDS '{pdsUri}'" );

        if (_logger.IsEnabled( LogLevel.Debug )) {
            string pdsUriString = pdsUri.ToString( );
            string authServerString = authorizationServer.ToString( );
            LogResolvedAuthServer( _logger, pdsUriString, authServerString );
        }

        // Fetch authorization server metadata
        AuthorizationServerMetadata metadata = await GetAuthorizationServerMetadataAsync(
            authorizationServer,
            cancellationToken
        );

        if (_logger.IsEnabled( LogLevel.Debug )) {
            string tokenEndpoint = metadata.TokenEndpoint?.ToString( ) ?? "null";
            string parEndpoint = metadata.PushedAuthorizationRequestEndpoint?.ToString( ) ?? "null";
            LogFetchedMetadata( _logger, tokenEndpoint, parEndpoint );
        }

        // Generate PKCE code verifier and challenge
        string codeVerifier = GenerateCodeVerifier( );
        string codeChallenge = GenerateCodeChallenge( codeVerifier );

        // Generate state parameter
        string state = GenerateState( );

        // Generate DPoP key pair
        string dpoPKeyJwk = GenerateDPoPKey( );

        // Store the OAuth state for later verification.
        // Encrypt sensitive fields before persisting to the database
        await using ApplicationDbContext dbContext = await _dbContextFactory.CreateDbContextAsync( cancellationToken );
        AtProtoOAuthState oauthState = new( ) {
            State = state,
            CodeVerifier = _personalDataProtector.Protect( codeVerifier ),
            Handle = handle,
            Did = did.ToString( ),
            PdsUri = pdsUri.ToString( ),
            AuthorizationServerUri = authorizationServer.ToString( ),
            DPoPKeyJwk = _personalDataProtector.Protect( dpoPKeyJwk ),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes( 5 )
        };

        _ = dbContext.AtProtoOAuthStates.Add( oauthState );
        _ = await dbContext.SaveChangesAsync( cancellationToken );

        // Use PAR (Pushed Authorization Request) if available (required by ATProto spec)
        Uri authorizationUrl;
        if (metadata.PushedAuthorizationRequestEndpoint is not null) {
            authorizationUrl = await PerformPushedAuthorizationRequestAsync(
                metadata,
                dpoPKeyJwk,
                redirectUri,
                state,
                codeChallenge,
                handle,
                cancellationToken
            );
        } else {
            // Fallback to direct authorization URL (may not work with all ATProto servers)
            LogNoParSupport( _logger, authorizationServer.ToString( ) );
            authorizationUrl = BuildDirectAuthorizationUrl(
                metadata.AuthorizationEndpoint!,
                authorizationServer,
                redirectUri,
                state,
                codeChallenge,
                handle
            );
        }

        LogAuthStarted( _logger, handle, state );

        return (authorizationUrl, state);
    }

    /// <summary>
    /// Completes the OAuth authorization flow by exchanging the authorization code for tokens.
    /// </summary>
    /// <remarks>
    /// Loads and decrypts the stored state, rejecting the request if it has expired, if the issuer does
    /// not match the stored authorization server, or if the returned subject DID does not match the
    /// expected DID. The state row is single-use and is removed once the exchange completes.
    /// </remarks>
    /// <param name="state">The state value returned to the callback, used to locate the stored flow.</param>
    /// <param name="code">The authorization code returned by the authorization server.</param>
    /// <param name="iss">The issuer reported on the callback, validated against the stored authorization server.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An <see cref="ATProtoOAuthResult"/> with the DID, handle, tokens, DPoP key, expiry, and scope.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="state"/>, <paramref name="code"/>, or <paramref name="iss"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the state is not found, has expired, the issuer mismatches, or the returned DID mismatches.</exception>
    public async Task<ATProtoOAuthResult> CompleteAuthorizationAsync(
        string state,
        string code,
        string iss,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( state );
        ArgumentException.ThrowIfNullOrWhiteSpace( code );
        ArgumentException.ThrowIfNullOrWhiteSpace( iss );

        LogCompletingFlow( _logger, state );

        // Look up the OAuth state
        await using ApplicationDbContext dbContext = await _dbContextFactory.CreateDbContextAsync( cancellationToken );
        AtProtoOAuthState oauthState = await dbContext.AtProtoOAuthStates
            .FirstOrDefaultAsync( s => s.State == state, cancellationToken )
            ?? throw new InvalidOperationException( "OAuth state not found. The authorization flow may have expired." );

        // Decrypt sensitive fields that were encrypted before storage.
        // A CryptographicException here means the key ring has rotated since the flow started;
        // treat it as an expired flow so the user sees the same message as a genuine timeout.
        try {
            oauthState.CodeVerifier = _personalDataProtector.Unprotect( oauthState.CodeVerifier )!;
            oauthState.DPoPKeyJwk = _personalDataProtector.Unprotect( oauthState.DPoPKeyJwk )!;
        } catch (System.Security.Cryptography.CryptographicException) {
            _ = dbContext.AtProtoOAuthStates.Remove( oauthState );
            _ = await dbContext.SaveChangesAsync( cancellationToken );
            throw new InvalidOperationException( "OAuth state has expired. Please try logging in again." );
        }

        if (oauthState.ExpiresAt < DateTime.UtcNow) {
            // Clean up expired state
            _ = dbContext.AtProtoOAuthStates.Remove( oauthState );
            _ = await dbContext.SaveChangesAsync( cancellationToken );
            throw new InvalidOperationException( "OAuth state has expired. Please try logging in again." );
        }

        // Verify the issuer matches the expected authorization server
        if (!string.Equals( oauthState.AuthorizationServerUri?.TrimEnd( '/' ), iss.TrimEnd( '/' ), StringComparison.OrdinalIgnoreCase )) {
            LogIssuerMismatch( _logger, oauthState.AuthorizationServerUri ?? "null", iss );
            throw new InvalidOperationException( "Authorization server issuer does not match. Possible security issue." );
        }

        // Exchange the authorization code for tokens by calling the token endpoint with the PKCE
        // verifier and DPoP
        ATProtoOAuthResult result = await ExchangeCodeForTokensAsync(
            oauthState,
            code,
            cancellationToken
        );

        // Clean up the OAuth state (single-use)
        _ = dbContext.AtProtoOAuthStates.Remove( oauthState );
        _ = await dbContext.SaveChangesAsync( cancellationToken );

        LogAuthCompleted( _logger, result.Did, result.Handle );

        return result;
    }

    /// <summary>
    /// Refreshes the access and refresh tokens for a DID using the refresh-token grant.
    /// </summary>
    /// <remarks>
    /// Re-resolves the PDS and authorization server, then performs the refresh. The returned result's
    /// handle is empty, so callers should retain the previously known handle. Any failure is logged and
    /// returns <see langword="null"/> rather than throwing.
    /// </remarks>
    /// <param name="did">The DID whose tokens are being refreshed.</param>
    /// <param name="refreshToken">The current refresh token.</param>
    /// <param name="dpoPKeyJwk">The DPoP key JWK bound to the session.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A new <see cref="ATProtoOAuthResult"/> on success, or <see langword="null"/> on any failure.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="did"/>, <paramref name="refreshToken"/>, or <paramref name="dpoPKeyJwk"/> is null, empty, or whitespace.</exception>
    public async Task<ATProtoOAuthResult?> RefreshTokensAsync(
        string did,
        string refreshToken,
        string dpoPKeyJwk,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( did );
        ArgumentException.ThrowIfNullOrWhiteSpace( refreshToken );
        ArgumentException.ThrowIfNullOrWhiteSpace( dpoPKeyJwk );

        LogRefreshingTokens( _logger, did );

        try {
            // Resolve the user's PDS to find the token endpoint
            using BlueskyAgent agent = new( );
            Uri? pdsUri = await agent.ResolvePds( new Did( did ), cancellationToken );
            if (pdsUri is null) {
                LogRefreshPdsFailed( _logger, did );
                return null;
            }

            Uri? authServer = await agent.ResolveAuthorizationServer( pdsUri, cancellationToken );
            if (authServer is null) {
                LogRefreshAuthServerFailed( _logger, did );
                return null;
            }

            // Call the token endpoint with refresh_token grant using a DPoP-signed request
            ATProtoOAuthResult? result = await RefreshTokensFromServerAsync(
                authServer,
                refreshToken,
                dpoPKeyJwk,
                did,
                cancellationToken
            );

            if (result is not null) {
                LogRefreshSuccess( _logger, did );
            }

            return result;
        } catch (Exception ex) {
            LogRefreshError( _logger, ex, did );
            return null;
        }
    }

    /// <summary>
    /// Determines whether a token expiry is far enough in the future to be considered valid.
    /// </summary>
    /// <param name="tokenExpiration">The token's UTC expiry, or <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="tokenExpiration"/> is set and is later than the current
    /// time plus the safety margin; otherwise <see langword="false"/>.
    /// </returns>
    public bool IsTokenValid( DateTime? tokenExpiration ) {
        return tokenExpiration is not null && tokenExpiration.Value > DateTime.UtcNow.Add( s_tokenExpirationMargin );
    }

    /// <summary>
    /// Removes expired ATProto OAuth state rows from the database.
    /// </summary>
    /// <remarks>
    /// No-ops and returns zero when migrations are pending, to avoid querying a table that may not yet
    /// exist. Otherwise bulk-deletes all rows whose expiry is in the past.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of expired state rows removed.</returns>
    public async Task<int> CleanupExpiredStatesAsync( CancellationToken cancellationToken = default ) {
        await using ApplicationDbContext dbContext = await _dbContextFactory.CreateDbContextAsync( cancellationToken );

        // Skip cleanup if database has pending migrations (table may not exist yet)
        IEnumerable<string> pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pendingMigrations.Any( )) {
            return 0;
        }

        DateTime cutoff = DateTime.UtcNow;
        int deleted = await dbContext.AtProtoOAuthStates
            .Where( s => s.ExpiresAt < cutoff )
            .ExecuteDeleteAsync( cancellationToken );

        if (deleted > 0) {
            LogCleanedUpStates( _logger, deleted );
        }

        return deleted;
    }

    #region Private Helper Methods

    /// <summary>
    /// Generates a cryptographically random PKCE code verifier.
    /// </summary>
    /// <returns>A base64url-encoded string of 32 cryptographically random bytes.</returns>
    private static string GenerateCodeVerifier( ) {
        byte[] bytes = new byte[32];
        RandomNumberGenerator.Fill( bytes );
        return Base64UrlEncode( bytes );
    }

    /// <summary>
    /// Generates a PKCE code challenge from the verifier using the S256 method.
    /// </summary>
    /// <param name="codeVerifier">The PKCE code verifier.</param>
    /// <returns>The base64url-encoded SHA-256 hash of the verifier.</returns>
    private static string GenerateCodeChallenge( string codeVerifier ) {
        byte[] bytes = SHA256.HashData( Encoding.ASCII.GetBytes( codeVerifier ) );
        return Base64UrlEncode( bytes );
    }

    /// <summary>
    /// Generates a cryptographically random state parameter used for CSRF and replay binding.
    /// </summary>
    /// <returns>A base64url-encoded string of 32 cryptographically random bytes.</returns>
    private static string GenerateState( ) {
        byte[] bytes = new byte[32];
        RandomNumberGenerator.Fill( bytes );
        return Base64UrlEncode( bytes );
    }

    /// <summary>
    /// Generates a new EC P-256 DPoP key pair and returns the private key as JWK.
    /// </summary>
    /// <returns>
    /// A serialized JWK for a new P-256 key, including the private parameter <c>d</c> and a random <c>kid</c>.
    /// </returns>
    private static string GenerateDPoPKey( ) {
        using ECDsa ecdsa = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
        ECParameters parameters = ecdsa.ExportParameters( includePrivateParameters: true );

        var jwk = new {
            kty = "EC",
            crv = "P-256",
            x = Base64UrlEncode( parameters.Q.X! ),
            y = Base64UrlEncode( parameters.Q.Y! ),
            d = Base64UrlEncode( parameters.D! ),
            kid = Guid.NewGuid( ).ToString( "N" )
        };

        return JsonSerializer.Serialize( jwk );
    }

    /// <summary>
    /// Fetches and caches the authorization server metadata from the well-known endpoint.
    /// </summary>
    /// <remarks>
    /// Fetches the well-known metadata document through the named, SSRF-guarded <c>ATProtoOAuth</c> client
    /// and caches the parsed result for the configured duration.
    /// </remarks>
    /// <param name="authorizationServer">The authorization server base URI.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The parsed <see cref="AuthorizationServerMetadata"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the metadata cannot be fetched or parsed.</exception>
    private async Task<AuthorizationServerMetadata> GetAuthorizationServerMetadataAsync(
        Uri authorizationServer,
        CancellationToken cancellationToken
    ) {
        string cacheKey = authorizationServer.ToString().TrimEnd('/').ToLowerInvariant();

        // Check cache first. MemoryCache handles expiry and eviction natively; expired entries are
        // never returned and are actively removed, not merely skipped on read.
        if (s_metadataCache.TryGetValue( cacheKey, out AuthorizationServerMetadata? cached )) {
            if (_logger.IsEnabled( LogLevel.Debug )) {
                string authServerString = authorizationServer.ToString( );
                LogUsingCachedMetadata( _logger, authServerString );
            }
            return cached!;
        }

        // Fetch metadata from well-known endpoint
        Uri metadataUrl = new(authorizationServer, AuthServerMetadataPath);
        if (_logger.IsEnabled( LogLevel.Debug )) {
            string metadataUrlString = metadataUrl.ToString( );
            LogFetchingMetadata( _logger, metadataUrlString );
        }

        HttpClient httpClient = _httpClientFactory.CreateClient("ATProtoOAuth");
        using HttpResponseMessage response = await httpClient.GetAsync(metadataUrl, cancellationToken);

        if (!response.IsSuccessStatusCode) {
            string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            LogMetadataFetchFailed( _logger, metadataUrl.ToString( ), (int)response.StatusCode, errorContent.Length > 200 ? errorContent[..200] + "..." : errorContent );
            throw new InvalidOperationException( $"Failed to fetch authorization server metadata: {response.StatusCode}" );
        }

        string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        AuthorizationServerMetadata metadata;

        try {
            using JsonDocument doc = JsonDocument.Parse(responseContent);
            JsonElement root = doc.RootElement;

            metadata = new AuthorizationServerMetadata {
                Issuer = root.TryGetProperty( "issuer", out JsonElement issuerElem ) ? issuerElem.GetString( ) : null,
                AuthorizationEndpoint = root.TryGetProperty( "authorization_endpoint", out JsonElement authEndpointElem )
                    ? new Uri( authEndpointElem.GetString( )! )
                    : null,
                TokenEndpoint = root.TryGetProperty( "token_endpoint", out JsonElement tokenEndpointElem )
                    ? new Uri( tokenEndpointElem.GetString( )! )
                    : null,
                PushedAuthorizationRequestEndpoint = root.TryGetProperty( "pushed_authorization_request_endpoint", out JsonElement parEndpointElem )
                    ? new Uri( parEndpointElem.GetString( )! )
                    : null,
                RequiresPushedAuthorizationRequests = root.TryGetProperty( "require_pushed_authorization_requests", out JsonElement requireParElem )
                    && requireParElem.GetBoolean( ),
                DPoPSigningAlgValuesSupported = root.TryGetProperty( "dpop_signing_alg_values_supported", out JsonElement dpopAlgsElem )
                    ? [.. dpopAlgsElem.EnumerateArray( ).Select( e => e.GetString( )! )]
                    : []
            };
        } catch (JsonException ex) {
            LogMetadataParseError( _logger, ex, metadataUrl.ToString( ) );
            throw new InvalidOperationException( "Failed to parse authorization server metadata", ex );
        }

        // Cache the metadata with a bounded entry. Size = 1 counts against SizeLimit so the total
        // entry count never exceeds MetadataCacheCapacity; entries beyond the cap are dropped rather
        // than cached, and expired entries are actively evicted by the cache runtime.
        MemoryCacheEntryOptions entryOptions = new( ) {
            AbsoluteExpirationRelativeToNow = s_metadataCacheDuration,
            Size = 1
        };
        _ = s_metadataCache.Set( cacheKey, metadata, entryOptions );

        if (_logger.IsEnabled( LogLevel.Debug )) {
            string authServerString = authorizationServer.ToString( );
            LogCachedMetadata( _logger, authServerString, metadata.RequiresPushedAuthorizationRequests );
        }

        return metadata;
    }

    /// <summary>
    /// Performs a Pushed Authorization Request (PAR) and returns the authorization URL.
    /// </summary>
    /// <remarks>
    /// Pushes the authorization parameters (including an optional client assertion) to the server's PAR
    /// endpoint with DPoP, then constructs the authorization URL from the returned <c>request_uri</c>.
    /// </remarks>
    /// <param name="metadata">The authorization-server metadata, which must include PAR and authorization endpoints.</param>
    /// <param name="dpoPKeyJwk">The DPoP key JWK used to sign the request proof.</param>
    /// <param name="redirectUri">The callback redirect URI.</param>
    /// <param name="state">The OAuth state value.</param>
    /// <param name="codeChallenge">The PKCE code challenge.</param>
    /// <param name="loginHint">The login hint (the user's handle).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The authorization URL the user should be redirected to.</returns>
    /// <exception cref="InvalidOperationException">Thrown when required endpoints are missing or the PAR response cannot be parsed.</exception>
    private async Task<Uri> PerformPushedAuthorizationRequestAsync(
        AuthorizationServerMetadata metadata,
        string dpoPKeyJwk,
        string redirectUri,
        string state,
        string codeChallenge,
        string loginHint,
        CancellationToken cancellationToken
    ) {
        if (metadata.PushedAuthorizationRequestEndpoint is null) {
            throw new InvalidOperationException( "Authorization server does not have a PAR endpoint" );
        }

        if (metadata.AuthorizationEndpoint is null) {
            throw new InvalidOperationException( "Authorization server metadata missing authorization_endpoint" );
        }

        string scope = string.Join(" ", s_requiredScopes);

        // Build PAR request parameters
        Dictionary<string, string> parFormData = new()
        {
            ["client_id"] = _clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["scope"] = scope,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["login_hint"] = loginHint
        };

        // Add client assertion for confidential client authentication
        AddClientAssertionIfAvailable( parFormData, metadata.Issuer );

        // Execute PAR request with DPoP
        (string responseContent, _) = await ExecuteTokenRequestWithDPoPRetryAsync(
            metadata.PushedAuthorizationRequestEndpoint,
            parFormData,
            dpoPKeyJwk,
            accessToken: null,
            cancellationToken
        );

        // Parse PAR response
        string requestUri;
        try {
            using JsonDocument doc = JsonDocument.Parse(responseContent);
            JsonElement root = doc.RootElement;

            requestUri = root.GetProperty( "request_uri" ).GetString( )
                ?? throw new InvalidOperationException( "PAR response missing request_uri" );
        } catch (JsonException ex) {
            LogParParseError( _logger, ex );
            throw new InvalidOperationException( "Failed to parse PAR response", ex );
        }

        LogParSuccess( _logger, requestUri );

        // Build authorization URL with request_uri
        UriBuilder builder = new(metadata.AuthorizationEndpoint);
        System.Collections.Specialized.NameValueCollection query = System.Web.HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = _clientId;
        query["request_uri"] = requestUri;
        builder.Query = query.ToString( );

        return builder.Uri;
    }

    /// <summary>
    /// Builds a direct OAuth authorization URL (fallback when PAR is not available). Includes the
    /// issuer parameter to prevent mix-up attacks per ATProto spec.
    /// </summary>
    /// <remarks>
    /// Encodes the authorization parameters directly into the query string and adds the <c>iss</c>
    /// parameter identifying the authorization server.
    /// </remarks>
    /// <param name="authorizationEndpoint">The authorization endpoint URI.</param>
    /// <param name="authorizationServer">The authorization-server base URI, used for the <c>iss</c> parameter.</param>
    /// <param name="redirectUri">The callback redirect URI.</param>
    /// <param name="state">The OAuth state value.</param>
    /// <param name="codeChallenge">The PKCE code challenge.</param>
    /// <param name="loginHint">The login hint (the user's handle).</param>
    /// <returns>The authorization URL the user should be redirected to.</returns>
    private Uri BuildDirectAuthorizationUrl(
        Uri authorizationEndpoint,
        Uri authorizationServer,
        string redirectUri,
        string state,
        string codeChallenge,
        string loginHint
    ) {
        UriBuilder builder = new(authorizationEndpoint);

        string scope = string.Join( " ", s_requiredScopes );

        System.Collections.Specialized.NameValueCollection query = System.Web.HttpUtility.ParseQueryString( string.Empty );
        query["client_id"] = _clientId;
        query["response_type"] = "code";
        query["redirect_uri"] = redirectUri;
        query["state"] = state;
        query["scope"] = scope;
        query["code_challenge"] = codeChallenge;
        query["code_challenge_method"] = "S256";
        query["login_hint"] = loginHint;
        // Include issuer to prevent OAuth mix-up attacks (per ATProto spec recommendation)
        query["iss"] = authorizationServer.ToString( ).TrimEnd( '/' );

        builder.Query = query.ToString( );

        return builder.Uri;
    }

    /// <summary>
    /// Executes a token endpoint request with DPoP and automatic nonce retry logic.
    /// </summary>
    /// <remarks>
    /// On a <c>400</c> response containing <c>use_dpop_nonce</c> with a <c>DPoP-Nonce</c> header, retries
    /// with the supplied nonce up to <see cref="MaxDPoPNonceRetries"/> times.
    /// </remarks>
    /// <param name="endpoint">The token endpoint URI.</param>
    /// <param name="formData">The form data to send.</param>
    /// <param name="dpoPKeyJwk">The DPoP private key in JWK format.</param>
    /// <param name="accessToken">Optional access token for DPoP binding via the <c>ath</c> claim.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The response content and the last DPoP nonce received (if any).</returns>
    /// <exception cref="InvalidOperationException">Thrown when the request fails or retries are exhausted.</exception>
    private async Task<(string ResponseContent, string? DPoPNonce)> ExecuteTokenRequestWithDPoPRetryAsync(
        Uri endpoint,
        Dictionary<string, string> formData,
        string dpoPKeyJwk,
        string? accessToken,
        CancellationToken cancellationToken
    ) {
        HttpClient httpClient = _httpClientFactory.CreateClient("ATProtoOAuth");
        string? currentNonce = null;

        for (int attempt = 0; attempt <= MaxDPoPNonceRetries; attempt++) {
            // Create DPoP proof with current nonce (null on first attempt)
            string dpopProof = CreateDPoPProof(
                dpoPKeyJwk,
                "POST",
                endpoint.ToString(),
                accessToken,
                currentNonce
            );

            using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(formData)
            };
            request.Headers.Add( "DPoP", dpopProof );

            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);

            // Check for DPoP nonce requirement
            if (response.StatusCode == HttpStatusCode.BadRequest) {
                string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

                // Check if this is a use_dpop_nonce error
                if (errorContent.Contains( "use_dpop_nonce", StringComparison.OrdinalIgnoreCase ) &&
                    response.Headers.TryGetValues( "DPoP-Nonce", out IEnumerable<string>? nonceValues )) {
                    currentNonce = nonceValues.FirstOrDefault( );
                    if (!string.IsNullOrWhiteSpace( currentNonce ) && attempt < MaxDPoPNonceRetries) {
                        LogDPoPNonceRetry( _logger, attempt + 1, MaxDPoPNonceRetries );
                        continue;
                    }
                }

                // Not a nonce error or max retries exceeded
                LogTokenRequestFailed( _logger, (int)response.StatusCode, errorContent.Length > 200 ? errorContent[..200] + "..." : errorContent );
                throw new InvalidOperationException( $"Token request failed: {response.StatusCode}. Response: {errorContent}" );
            }

            if (!response.IsSuccessStatusCode) {
                string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                LogTokenRequestFailed( _logger, (int)response.StatusCode, errorContent.Length > 200 ? errorContent[..200] + "..." : errorContent );
                throw new InvalidOperationException( $"Token request failed: {response.StatusCode}" );
            }

            // Success - extract nonce from response headers if present for future use
            if (response.Headers.TryGetValues( "DPoP-Nonce", out IEnumerable<string>? respNonceValues )) {
                currentNonce = respNonceValues.FirstOrDefault( );
            }

            string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            return (responseContent, currentNonce);
        }

        throw new InvalidOperationException( "Token request failed after maximum DPoP nonce retries" );
    }

    /// <summary>
    /// Exchanges the authorization code for tokens at the server's token endpoint.
    /// </summary>
    /// <remarks>
    /// Sends the authorization-code grant with the stored code verifier and DPoP, then validates that the
    /// returned subject (<c>sub</c>) is present and matches the DID recorded in the stored state.
    /// </remarks>
    /// <param name="oauthState">The stored, decrypted OAuth state for this flow.</param>
    /// <param name="code">The authorization code to exchange.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An <see cref="ATProtoOAuthResult"/> built from the token response.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="oauthState"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="code"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the token endpoint is missing, the response is malformed, or the returned DID does not match.</exception>
    private async Task<ATProtoOAuthResult> ExchangeCodeForTokensAsync(
        AtProtoOAuthState oauthState,
        string code,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull( oauthState );
        ArgumentException.ThrowIfNullOrWhiteSpace( code );

        LogExchangingCode( _logger, oauthState.State );

        // Fetch authorization server metadata to get the token endpoint
        Uri authServer = new(oauthState.AuthorizationServerUri!);
        AuthorizationServerMetadata metadata = await GetAuthorizationServerMetadataAsync(
            authServer,
            cancellationToken
        );

        if (metadata.TokenEndpoint is null) {
            throw new InvalidOperationException( "Authorization server metadata missing token_endpoint" );
        }

        // Build the token request parameters
        Dictionary<string, string> formData = new( ) {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _clientId,
            ["redirect_uri"] = $"{_domain}{OAuthCallbackPath}",
            ["code_verifier"] = oauthState.CodeVerifier!
        };

        // Add client assertion for confidential client authentication
        AddClientAssertionIfAvailable( formData, metadata.Issuer );

        // Execute token request with DPoP nonce retry logic
        (string responseContent, _) = await ExecuteTokenRequestWithDPoPRetryAsync(
            metadata.TokenEndpoint,
            formData,
            oauthState.DPoPKeyJwk!,
            accessToken: null,
            cancellationToken
        );

        string accessToken;
        string refreshToken;
        int expiresIn;
        string? scope;
        string? sub;

        try {
            using JsonDocument doc = JsonDocument.Parse( responseContent );
            JsonElement root = doc.RootElement;

            // GetProperty() throws KeyNotFoundException if property doesn't exist.
            // GetString() returns null if property exists but is null/empty.
            // The null coalescing operator (??) then throws InvalidOperationException
            accessToken = root.GetProperty( "access_token" ).GetString( )
                ?? throw new InvalidOperationException( "access_token missing from token response" );
            refreshToken = root.GetProperty( "refresh_token" ).GetString( )
                ?? throw new InvalidOperationException( "refresh_token missing from token response" );
            expiresIn = root.GetProperty( "expires_in" ).GetInt32( );
            scope = root.TryGetProperty( "scope", out JsonElement scopeElem ) ? scopeElem.GetString( ) : null;
            sub = root.TryGetProperty( "sub", out JsonElement subElem ) ? subElem.GetString( ) : null;
        } catch (JsonException ex) {
            LogTokenParseError( _logger, ex, responseContent );
            throw new InvalidOperationException( "Token response had invalid format or missing required properties.", ex );
        } catch (KeyNotFoundException ex) {
            // This catches missing properties from GetProperty() calls above
            LogTokenMissingProperty( _logger, ex, responseContent );
            throw new InvalidOperationException( "Token response missing required property.", ex );
        }

        // Validate the sub (DID) - fail if missing or doesn't match
        if (string.IsNullOrWhiteSpace( sub )) {
            LogTokenMissingSub( _logger, responseContent );
            throw new InvalidOperationException( "Token response missing 'sub' (DID) claim" );
        }

        if (!string.Equals( sub, oauthState.Did, StringComparison.OrdinalIgnoreCase )) {
            LogTokenDidMismatch( _logger, oauthState.Did ?? "null", sub );
            throw new InvalidOperationException( "Token response DID does not match expected DID" );
        }

        LogCodeExchanged( _logger, sub );

        // Return the OAuth result
        return new ATProtoOAuthResult {
            Did = sub,
            Handle = oauthState.Handle!,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            DPoPKeyJwk = oauthState.DPoPKeyJwk!,
            TokenExpiration = DateTime.UtcNow.AddSeconds( expiresIn ),
            Scope = scope ?? string.Join( " ", s_requiredScopes )
        };
    }

    /// <summary>
    /// Refreshes tokens from the authorization server.
    /// </summary>
    /// <remarks>
    /// When the response includes a subject (<c>sub</c>), it must match the supplied DID. Any parsing or
    /// validation failure returns <see langword="null"/> rather than throwing.
    /// </remarks>
    /// <param name="authorizationServer">The authorization-server base URI.</param>
    /// <param name="refreshToken">The current refresh token.</param>
    /// <param name="dpoPKeyJwk">The DPoP key JWK bound to the session.</param>
    /// <param name="did">The expected DID for the refreshed tokens.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A new <see cref="ATProtoOAuthResult"/> with an empty handle on success, or <see langword="null"/> on
    /// failure or DID mismatch.
    /// </returns>
    private async Task<ATProtoOAuthResult?> RefreshTokensFromServerAsync(
        Uri authorizationServer,
        string refreshToken,
        string dpoPKeyJwk,
        string did,
        CancellationToken cancellationToken
    ) {
        // Fetch authorization server metadata to get the token endpoint
        AuthorizationServerMetadata metadata = await GetAuthorizationServerMetadataAsync(
            authorizationServer,
            cancellationToken
        );

        if (metadata.TokenEndpoint is null) {
            LogMissingTokenEndpoint( _logger, did );
            return null;
        }

        // Build the refresh token request parameters
        Dictionary<string, string> formData = new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _clientId
        };

        // Add client assertion for confidential client authentication
        AddClientAssertionIfAvailable( formData, metadata.Issuer );

        // Execute token request with DPoP nonce retry logic
        (string responseContent, _) = await ExecuteTokenRequestWithDPoPRetryAsync(
            metadata.TokenEndpoint,
            formData,
            dpoPKeyJwk,
            accessToken: null,
            cancellationToken
        );

        // Parse the token response
        string accessTokenResult;
        string refreshTokenResult;
        int expiresIn;
        string? scope;
        string? sub;

        try {
            using JsonDocument doc = JsonDocument.Parse(responseContent);
            JsonElement root = doc.RootElement;

            accessTokenResult = root.GetProperty( "access_token" ).GetString( )
                ?? throw new InvalidOperationException( "access_token missing from refresh response" );
            refreshTokenResult = root.GetProperty( "refresh_token" ).GetString( )
                ?? throw new InvalidOperationException( "refresh_token missing from refresh response" );
            expiresIn = root.GetProperty( "expires_in" ).GetInt32( );
            scope = root.TryGetProperty( "scope", out JsonElement scopeElem ) ? scopeElem.GetString( ) : null;
            sub = root.TryGetProperty( "sub", out JsonElement subElem ) ? subElem.GetString( ) : null;
        } catch (Exception ex) when (ex is JsonException or KeyNotFoundException) {
            LogRefreshParseError( _logger, ex, did );
            return null;
        }

        // Validate the sub (DID) matches if present
        if (!string.IsNullOrWhiteSpace( sub ) && !string.Equals( sub, did, StringComparison.OrdinalIgnoreCase )) {
            LogRefreshDidMismatch( _logger, did, sub );
            return null;
        }

        LogRefreshSuccess( _logger, did );

        // Return the refreshed OAuth result
        return new ATProtoOAuthResult {
            Did = did,
            Handle = string.Empty, // Handle not returned in refresh response
            AccessToken = accessTokenResult,
            RefreshToken = refreshTokenResult,
            DPoPKeyJwk = dpoPKeyJwk,
            TokenExpiration = DateTime.UtcNow.AddSeconds( expiresIn ),
            Scope = scope ?? string.Join( " ", s_requiredScopes )
        };
    }

    /// <summary>
    /// Adds client assertion fields to the form data for confidential client authentication. If no
    /// signing key is configured (public client mode), this is a no-op.
    /// </summary>
    /// <param name="formData">The form data dictionary to add assertion fields to.</param>
    /// <param name="audience">The authorization server issuer URI used as the JWT audience.</param>
    private void AddClientAssertionIfAvailable( Dictionary<string, string> formData, string? audience ) {
        if (_signingKeyProvider is null || string.IsNullOrWhiteSpace( audience )) {
            return;
        }

        string clientAssertion = CreateClientAssertionJwt( audience );
        formData["client_assertion_type"] = ClientAssertionType;
        formData["client_assertion"] = clientAssertion;
    }

    /// <summary>
    /// Creates a client assertion JWT for private_key_jwt authentication per RFC 7523. The JWT is
    /// signed with the persistent ES256 signing key and sent to the authorization server to prove the
    /// client's identity.
    /// </summary>
    /// <remarks>
    /// The assertion sets <c>iss</c> and <c>sub</c> to the client id, <c>aud</c> to the supplied audience,
    /// a random <c>jti</c>, and a five-minute expiry, and is signed with the provisioned P-256 key using ES256.
    /// </remarks>
    /// <param name="audience">The authorization server issuer URI used as the JWT audience.</param>
    /// <returns>A compact-serialized JWT suitable for the client_assertion parameter.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no signing key provider is configured.</exception>
    private string CreateClientAssertionJwt( string audience ) {
        _ = _signingKeyProvider
            ?? throw new InvalidOperationException( "Cannot create client assertion without a signing key provider." );

        JwtSecurityTokenHandler handler = new( );
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds( );

        SecurityTokenDescriptor descriptor = new( ) {
            Claims = new Dictionary<string, object> {
                ["iss"] = _clientId,
                ["sub"] = _clientId,
                ["aud"] = audience,
                ["jti"] = Guid.NewGuid( ).ToString( "N" ),
                ["iat"] = now
            },
            Expires = DateTimeOffset.FromUnixTimeSeconds( now + 300 ).UtcDateTime, // 5 minute expiry
            SigningCredentials = new SigningCredentials(
                new ECDsaSecurityKey( _signingKeyProvider.SigningKey ) { KeyId = _signingKeyProvider.KeyId },
                SecurityAlgorithms.EcdsaSha256
            )
        };

        SecurityToken token = handler.CreateToken( descriptor );
        return handler.WriteToken( token );
    }

    /// <summary>
    /// Creates a DPoP proof JWT for the given HTTP method and URL.
    /// </summary>
    /// <remarks>
    /// Validates the DPoP JWK (EC, P-256, with <c>x</c>, <c>y</c>, <c>d</c>, and <c>kid</c>) and rebuilds the
    /// P-256 key. The header uses <c>typ=dpop+jwt</c> and <c>alg=ES256</c> and embeds the public key. The
    /// payload sets <c>htm</c>, <c>htu</c>, a random <c>jti</c>, <c>iat</c>, and <c>nbf</c> (five seconds
    /// before issuance for clock-skew tolerance); it adds <c>ath</c> (the base64url SHA-256 of the access
    /// token) when an access token is present, and <c>nonce</c> when one is supplied. The ECDSA signature is
    /// produced in DER form and converted to the raw R‖S concatenation that JOSE requires.
    /// </remarks>
    /// <param name="dpoPKeyJwk">The DPoP private key in JWK format.</param>
    /// <param name="httpMethod">The HTTP method (for example, "POST", "GET").</param>
    /// <param name="url">The full URL of the request.</param>
    /// <param name="accessToken">Optional access token to bind in the proof.</param>
    /// <param name="nonce">Optional server-provided nonce for DPoP nonce binding.</param>
    /// <returns>The DPoP proof JWT.</returns>
    /// <exception cref="ArgumentException">Thrown when the DPoP JWK is not an EC P-256 key or is missing a required parameter.</exception>
    private static string CreateDPoPProof( string dpoPKeyJwk, string httpMethod, string url, string? accessToken, string? nonce = null ) {
        // Parse the JWK to get the EC key parameters
        using JsonDocument jwkDoc = JsonDocument.Parse( dpoPKeyJwk );
        JsonElement jwk = jwkDoc.RootElement;

        // Validate required JWK parameters
        if (!jwk.TryGetProperty( "kty", out JsonElement ktyProp ) || ktyProp.GetString( ) != "EC") {
            throw new ArgumentException( "DPoP key JWK must have 'kty' set to 'EC'.", nameof( dpoPKeyJwk ) );
        }
        if (!jwk.TryGetProperty( "crv", out JsonElement crvProp ) || crvProp.GetString( ) != "P-256") {
            throw new ArgumentException( "DPoP key JWK must have 'crv' set to 'P-256'.", nameof( dpoPKeyJwk ) );
        }
        if (!jwk.TryGetProperty( "x", out JsonElement xProp ) || string.IsNullOrWhiteSpace( xProp.GetString( ) )) {
            throw new ArgumentException( "DPoP key JWK does not contain required parameter 'x'.", nameof( dpoPKeyJwk ) );
        }
        if (!jwk.TryGetProperty( "y", out JsonElement yProp ) || string.IsNullOrWhiteSpace( yProp.GetString( ) )) {
            throw new ArgumentException( "DPoP key JWK does not contain required parameter 'y'.", nameof( dpoPKeyJwk ) );
        }
        if (!jwk.TryGetProperty( "d", out JsonElement dProp ) || string.IsNullOrWhiteSpace( dProp.GetString( ) )) {
            throw new ArgumentException( "DPoP key JWK does not contain required private key parameter 'd'.", nameof( dpoPKeyJwk ) );
        }
        if (!jwk.TryGetProperty( "kid", out JsonElement kidProp ) || string.IsNullOrWhiteSpace( kidProp.GetString( ) )) {
            throw new ArgumentException( "DPoP key JWK does not contain required parameter 'kid'.", nameof( dpoPKeyJwk ) );
        }

        string x = xProp.GetString( )!;
        string y = yProp.GetString( )!;
        string d = dProp.GetString( )!;
        string kid = kidProp.GetString( )!;

        // Create the ECDsa key from JWK parameters
        byte[] xBytes = Base64UrlDecode( x );
        byte[] yBytes = Base64UrlDecode( y );
        byte[] dBytes = Base64UrlDecode( d );

        using ECDsa ecdsa = ECDsa.Create( new ECParameters {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = xBytes, Y = yBytes },
            D = dBytes
        } );

        // Build the DPoP JWT manually to ensure the jwk header is a proper JSON object.
        // JwtSecurityTokenHandler strips the jwk from AdditionalHeaderClaims, so we construct
        // the JWT directly: header.payload.signature (RFC 9449 / RFC 7515 compact serialization).
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds( );
        long nbf = now - 5;

        // Build header with jwk as an embedded JSON object (not a string)
        Dictionary<string, object> header = new( ) {
            ["typ"] = "dpop+jwt",
            ["alg"] = "ES256",
            ["kid"] = kid,
            ["jwk"] = new Dictionary<string, string> {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = x,
                ["y"] = y
            }
        };

        // Build payload with required DPoP claims
        Dictionary<string, object> payload = new( ) {
            ["htm"] = httpMethod,
            ["htu"] = url,
            ["jti"] = Guid.NewGuid( ).ToString( "N" ),
            ["iat"] = now,
            ["nbf"] = nbf
        };

        // Add access token hash if provided
        if (!string.IsNullOrWhiteSpace( accessToken )) {
            byte[] hash = SHA256.HashData( Encoding.ASCII.GetBytes( accessToken ) );
            payload["ath"] = Base64UrlEncode( hash );
        }

        // Add nonce if provided (required for DPoP nonce binding per RFC 9449)
        if (!string.IsNullOrWhiteSpace( nonce )) {
            payload["nonce"] = nonce;
        }

        // Encode header and payload
        string headerJson = JsonSerializer.Serialize( header );
        string payloadJson = JsonSerializer.Serialize( payload );
        string headerB64 = Base64UrlEncode( Encoding.UTF8.GetBytes( headerJson ) );
        string payloadB64 = Base64UrlEncode( Encoding.UTF8.GetBytes( payloadJson ) );

        // Sign with ES256 (ECDSA using P-256 and SHA-256)
        string signingInput = $"{headerB64}.{payloadB64}";
        byte[] signatureBytes = ecdsa.SignData( Encoding.UTF8.GetBytes( signingInput ), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence );

        // Convert DER-encoded signature to the raw R||S format required by JWS (RFC 7518 Section 3.4)
        byte[] rawSignature = ConvertDerToRawEcdsaSignature( signatureBytes, 32 );
        string signatureB64 = Base64UrlEncode( rawSignature );

        return $"{headerB64}.{payloadB64}.{signatureB64}";
    }

    /// <summary>
    /// Converts a DER-encoded ECDSA signature to the raw R||S format required by JWS (RFC 7518 Section 3.4).
    /// </summary>
    /// <remarks>
    /// Parses the DER SEQUENCE of two INTEGERs (R and S) and writes each as a fixed-width component,
    /// trimming or left-padding as needed.
    /// </remarks>
    /// <param name="derSignature">The DER-encoded signature.</param>
    /// <param name="componentLength">The fixed byte length of each component (32 for P-256).</param>
    /// <returns>The raw signature: R followed by S, each <paramref name="componentLength"/> bytes.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="derSignature"/> is not a valid DER ECDSA signature.</exception>
    private static byte[] ConvertDerToRawEcdsaSignature( byte[] derSignature, int componentLength ) {
        // DER format: 0x30 [total-length] 0x02 [r-length] [r-value] 0x02 [s-length] [s-value]
        int offset = 2; // Skip SEQUENCE tag and length
        if (derSignature[0] != 0x30) {
            throw new ArgumentException( "Invalid DER signature format.", nameof( derSignature ) );
        }

        // Parse R
        if (derSignature[offset] != 0x02) {
            throw new ArgumentException( "Invalid DER signature format: expected INTEGER tag for R.", nameof( derSignature ) );
        }
        offset++;
        int rLength = derSignature[offset++];
        byte[] rBytes = derSignature[offset..(offset + rLength)];
        offset += rLength;

        // Parse S
        if (derSignature[offset] != 0x02) {
            throw new ArgumentException( "Invalid DER signature format: expected INTEGER tag for S.", nameof( derSignature ) );
        }
        offset++;
        int sLength = derSignature[offset++];
        byte[] sBytes = derSignature[offset..(offset + sLength)];

        // Pad or trim R and S to componentLength bytes
        byte[] raw = new byte[componentLength * 2];
        CopyComponentToRaw( rBytes, raw, 0, componentLength );
        CopyComponentToRaw( sBytes, raw, componentLength, componentLength );

        return raw;
    }

    /// <summary>
    /// Copies a DER integer component into a fixed-length raw buffer, handling leading zeros and padding.
    /// </summary>
    /// <param name="component">The component bytes parsed from the DER signature.</param>
    /// <param name="raw">The destination raw-signature buffer.</param>
    /// <param name="targetOffset">The offset in <paramref name="raw"/> at which to write the component.</param>
    /// <param name="componentLength">The fixed byte length of the component.</param>
    private static void CopyComponentToRaw( byte[] component, byte[] raw, int targetOffset, int componentLength ) {
        if (component.Length == componentLength) {
            Array.Copy( component, 0, raw, targetOffset, componentLength );
        } else if (component.Length > componentLength) {
            // Strip leading zero padding (DER adds 0x00 prefix for positive integers with high bit set)
            int skip = component.Length - componentLength;
            Array.Copy( component, skip, raw, targetOffset, componentLength );
        } else {
            // Left-pad with zeros
            int pad = componentLength - component.Length;
            Array.Copy( component, 0, raw, targetOffset + pad, component.Length );
        }
    }

    /// <summary>
    /// Decodes a base64url-encoded string to its raw bytes, restoring standard padding.
    /// </summary>
    /// <param name="base64Url">The base64url-encoded input.</param>
    /// <returns>The decoded bytes.</returns>
    private static byte[] Base64UrlDecode( string base64Url ) {
        string padded = base64Url.PadRight( base64Url.Length + ((4 - (base64Url.Length % 4)) % 4), '=' );
        string base64 = padded.Replace( '-', '+' ).Replace( '_', '/' );
        return Convert.FromBase64String( base64 );
    }

    /// <summary>
    /// Encodes bytes as a base64url string with padding removed.
    /// </summary>
    /// <param name="bytes">The bytes to encode.</param>
    /// <returns>The base64url-encoded string.</returns>
    private static string Base64UrlEncode( byte[] bytes ) {
        return Convert.ToBase64String( bytes )
            .TrimEnd( '=' )
            .Replace( '+', '-' )
            .Replace( '/', '_' );
    }

    #endregion

    #region LoggerMessage Methods

    /// <summary>Logs that the OAuth flow is starting for a handle.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="handle">The handle being authenticated.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceStartFlow,
        Level = LogLevel.Information,
        Message = "Starting ATProto OAuth flow for handle: {Handle}" )]
    internal static partial void LogStartFlow( ILogger logger, string handle );

    /// <summary>Logs that a handle was resolved to a DID.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="handle">The resolved handle.</param>
    /// <param name="did">The resulting DID.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceResolvedHandle,
        Level = LogLevel.Debug,
        Message = "Resolved handle {Handle} to DID {Did}" )]
    internal static partial void LogResolvedHandle( ILogger logger, string handle, string did );

    /// <summary>Logs that a DID was resolved to a PDS URI.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="did">The DID being resolved.</param>
    /// <param name="pdsUri">The resulting PDS URI.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceResolvedPds,
        Level = LogLevel.Debug,
        Message = "Resolved DID {Did} to PDS {PdsUri}" )]
    internal static partial void LogResolvedPds( ILogger logger, string did, string pdsUri );

    /// <summary>Logs that a PDS was resolved to an authorization server.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="pdsUri">The PDS URI being resolved.</param>
    /// <param name="authServer">The resulting authorization-server URI.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceResolvedAuthServer,
        Level = LogLevel.Debug,
        Message = "Resolved PDS {PdsUri} to authorization server {AuthServer}" )]
    internal static partial void LogResolvedAuthServer( ILogger logger, string pdsUri, string authServer );

    /// <summary>Logs the key endpoints discovered in the authorization-server metadata.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="tokenEndpoint">The token endpoint, or a placeholder when absent.</param>
    /// <param name="parEndpoint">The PAR endpoint, or a placeholder when absent.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceFetchedMetadata,
        Level = LogLevel.Debug,
        Message = "Fetched auth server metadata. Token endpoint: {TokenEndpoint}, PAR endpoint: {ParEndpoint}" )]
    internal static partial void LogFetchedMetadata( ILogger logger, string tokenEndpoint, string parEndpoint );

    /// <summary>Logs that the authorization server does not support PAR and a direct URL will be used.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="authServer">The authorization-server URI.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceNoParSupport,
        Level = LogLevel.Warning,
        Message = "Authorization server {AuthServer} does not support PAR. Using direct authorization URL." )]
    internal static partial void LogNoParSupport( ILogger logger, string authServer );

    /// <summary>Logs that authorization has started for a handle and state.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="handle">The handle being authenticated.</param>
    /// <param name="state">The generated state value.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceAuthStarted,
        Level = LogLevel.Information,
        Message = "OAuth authorization started for handle {Handle}, state {State}" )]
    internal static partial void LogAuthStarted( ILogger logger, string handle, string state );

    /// <summary>Logs that the OAuth flow is being completed for a state.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="state">The state value identifying the flow.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceCompletingFlow,
        Level = LogLevel.Information,
        Message = "Completing ATProto OAuth flow for state: {State}" )]
    internal static partial void LogCompletingFlow( ILogger logger, string state );

    /// <summary>Logs that the reported issuer did not match the expected authorization server.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="expected">The expected issuer.</param>
    /// <param name="actual">The issuer reported on the callback.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceIssuerMismatch,
        Level = LogLevel.Warning,
        Message = "Issuer mismatch. Expected: {Expected}, Got: {Actual}" )]
    internal static partial void LogIssuerMismatch( ILogger logger, string expected, string actual );

    /// <summary>Logs that authorization completed for a DID and handle.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="did">The authenticated DID.</param>
    /// <param name="handle">The authenticated handle.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceAuthCompleted,
        Level = LogLevel.Information,
        Message = "OAuth authorization completed for DID {Did}, handle {Handle}" )]
    internal static partial void LogAuthCompleted( ILogger logger, string did, string handle );

    /// <summary>Logs that a token refresh is starting for a DID.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="did">The DID whose tokens are being refreshed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceRefreshingTokens,
        Level = LogLevel.Debug,
        Message = "Refreshing ATProto tokens for DID: {Did}" )]
    internal static partial void LogRefreshingTokens( ILogger logger, string did );

    /// <summary>Logs that PDS resolution failed during a token refresh.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="did">The DID whose refresh failed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceRefreshPdsFailed,
        Level = LogLevel.Warning,
        Message = "Failed to resolve PDS for DID {Did} during token refresh" )]
    internal static partial void LogRefreshPdsFailed( ILogger logger, string did );

    /// <summary>Logs that authorization-server resolution failed during a token refresh.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="did">The DID whose refresh failed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceRefreshAuthServerFailed,
        Level = LogLevel.Warning,
        Message = "Failed to resolve auth server for DID {Did} during token refresh" )]
    internal static partial void LogRefreshAuthServerFailed( ILogger logger, string did );

    /// <summary>Logs that tokens were successfully refreshed for a DID.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="did">The DID whose tokens were refreshed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceRefreshSuccess,
        Level = LogLevel.Information,
        Message = "Successfully refreshed ATProto tokens for DID {Did}" )]
    internal static partial void LogRefreshSuccess( ILogger logger, string did );

    /// <summary>Logs an error raised while refreshing tokens for a DID.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="did">The DID whose refresh failed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceRefreshError,
        Level = LogLevel.Error,
        Message = "Failed to refresh ATProto tokens for DID {Did}" )]
    internal static partial void LogRefreshError( ILogger logger, Exception ex, string did );

    /// <summary>Logs the number of expired OAuth state rows cleaned up.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="count">The number of rows removed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceCleanedUpStates,
        Level = LogLevel.Information,
        Message = "Cleaned up {Count} expired ATProto OAuth states" )]
    internal static partial void LogCleanedUpStates( ILogger logger, int count );

    /// <summary>Logs that cached authorization-server metadata is being used.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="authServer">The authorization-server URI.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceUsingCachedMetadata,
        Level = LogLevel.Debug,
        Message = "Using cached authorization server metadata for {AuthServer}" )]
    internal static partial void LogUsingCachedMetadata( ILogger logger, string authServer );

    /// <summary>Logs that authorization-server metadata is being fetched.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="metadataUrl">The metadata document URL.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceFetchingMetadata,
        Level = LogLevel.Debug,
        Message = "Fetching authorization server metadata from {MetadataUrl}" )]
    internal static partial void LogFetchingMetadata( ILogger logger, string metadataUrl );

    /// <summary>Logs that fetching authorization-server metadata failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="metadataUrl">The metadata document URL.</param>
    /// <param name="statusCode">The HTTP status code returned.</param>
    /// <param name="response">A truncated copy of the response body.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceMetadataFetchFailed,
        Level = LogLevel.Warning,
        Message = "Failed to fetch authorization server metadata from {MetadataUrl}. Status: {StatusCode}. Response: {Response}" )]
    internal static partial void LogMetadataFetchFailed( ILogger logger, string metadataUrl, int statusCode, string response );

    /// <summary>Logs that parsing authorization-server metadata failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="metadataUrl">The metadata document URL.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceMetadataParseError,
        Level = LogLevel.Error,
        Message = "Failed to parse authorization server metadata from {MetadataUrl}" )]
    internal static partial void LogMetadataParseError( ILogger logger, Exception ex, string metadataUrl );

    /// <summary>Logs that authorization-server metadata was cached.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="authServer">The authorization-server URI.</param>
    /// <param name="parRequired">Whether the server requires PAR.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceCachedMetadata,
        Level = LogLevel.Debug,
        Message = "Cached authorization server metadata for {AuthServer}. PAR required: {ParRequired}" )]
    internal static partial void LogCachedMetadata( ILogger logger, string authServer, bool parRequired );

    /// <summary>Logs that the Pushed Authorization Request succeeded.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="requestUri">The request URI returned by the server.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceParSuccess,
        Level = LogLevel.Debug,
        Message = "PAR successful. Request URI: {RequestUri}" )]
    internal static partial void LogParSuccess( ILogger logger, string requestUri );

    /// <summary>Logs that parsing the PAR response failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceParParseError,
        Level = LogLevel.Error,
        Message = "Failed to parse PAR response" )]
    internal static partial void LogParParseError( ILogger logger, Exception ex );

    /// <summary>Logs a retry triggered by a server-required DPoP nonce.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="attempt">The current retry attempt number.</param>
    /// <param name="maxRetries">The maximum number of retries.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceDPoPNonceRetry,
        Level = LogLevel.Debug,
        Message = "Received use_dpop_nonce error, retrying with nonce. Attempt {Attempt}/{MaxRetries}" )]
    internal static partial void LogDPoPNonceRetry( ILogger logger, int attempt, int maxRetries );

    /// <summary>Logs that a token request failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="statusCode">The HTTP status code returned.</param>
    /// <param name="response">A truncated copy of the response body.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceTokenRequestFailed,
        Level = LogLevel.Error,
        Message = "Token request failed with status {StatusCode}. Response: {Response}" )]
    internal static partial void LogTokenRequestFailed( ILogger logger, int statusCode, string response );

    /// <summary>Logs that an authorization code is being exchanged for tokens.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="state">The state value identifying the flow.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceExchangingCode,
        Level = LogLevel.Information,
        Message = "Exchanging authorization code for tokens. State: {State}" )]
    internal static partial void LogExchangingCode( ILogger logger, string state );

    /// <summary>Logs that parsing the token response failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="responseContent">The response body that could not be parsed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceTokenParseError,
        Level = LogLevel.Error,
        Message = "Failed to parse token response. Content: {ResponseContent}" )]
    internal static partial void LogTokenParseError( ILogger logger, Exception ex, string responseContent );

    /// <summary>Logs that the token response was missing a required property.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="responseContent">The response body that was missing a property.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceTokenMissingProperty,
        Level = LogLevel.Error,
        Message = "Token response missing required property. Content: {ResponseContent}" )]
    internal static partial void LogTokenMissingProperty( ILogger logger, Exception ex, string responseContent );

    /// <summary>Logs that the token response was missing the <c>sub</c> (DID) claim.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="responseContent">The response body that was missing the claim.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceTokenMissingSub,
        Level = LogLevel.Error,
        Message = "Token response missing 'sub' (DID) claim. Content: {ResponseContent}" )]
    internal static partial void LogTokenMissingSub( ILogger logger, string responseContent );

    /// <summary>Logs a DID mismatch between the token response and the expected DID.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="expected">The expected DID.</param>
    /// <param name="actual">The DID returned in the token response.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceTokenDidMismatch,
        Level = LogLevel.Warning,
        Message = "DID mismatch in token response. Expected: {Expected}, Got: {Actual}" )]
    internal static partial void LogTokenDidMismatch( ILogger logger, string expected, string actual );

    /// <summary>Logs that an authorization code was successfully exchanged for tokens.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="did">The DID the tokens were issued for.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceCodeExchanged,
        Level = LogLevel.Information,
        Message = "Successfully exchanged authorization code for tokens. DID: {Did}" )]
    internal static partial void LogCodeExchanged( ILogger logger, string did );

    /// <summary>Logs that the authorization-server metadata lacked a token endpoint during refresh.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="did">The DID whose refresh could not proceed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceMissingTokenEndpoint,
        Level = LogLevel.Warning,
        Message = "Authorization server metadata missing token_endpoint for DID {Did}" )]
    internal static partial void LogMissingTokenEndpoint( ILogger logger, string did );

    /// <summary>Logs that parsing the token-refresh response failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="did">The DID whose refresh response could not be parsed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceRefreshParseError,
        Level = LogLevel.Error,
        Message = "Failed to parse token refresh response for DID {Did}" )]
    internal static partial void LogRefreshParseError( ILogger logger, Exception ex, string did );

    /// <summary>Logs a DID mismatch between the token-refresh response and the expected DID.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="expected">The expected DID.</param>
    /// <param name="actual">The DID returned in the refresh response.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Identity.ATProtoOAuthServiceRefreshDidMismatch,
        Level = LogLevel.Warning,
        Message = "DID mismatch in token refresh response. Expected: {Expected}, Got: {Actual}" )]
    internal static partial void LogRefreshDidMismatch( ILogger logger, string expected, string actual );

    #endregion
}

/// <summary>
/// Represents the metadata from an OAuth 2.0 authorization server's well-known endpoint.
/// </summary>
internal sealed record AuthorizationServerMetadata {
    /// <summary>Gets the authorization server's issuer identifier, used as the audience for client assertions.</summary>
    public string? Issuer { get; init; }

    /// <summary>Gets the authorization endpoint URL, or <see langword="null"/> when not advertised.</summary>
    public Uri? AuthorizationEndpoint { get; init; }

    /// <summary>Gets the token endpoint URL, or <see langword="null"/> when not advertised.</summary>
    public Uri? TokenEndpoint { get; init; }

    /// <summary>Gets the pushed authorization request (PAR) endpoint URL, or <see langword="null"/> when not supported.</summary>
    public Uri? PushedAuthorizationRequestEndpoint { get; init; }

    /// <summary>Gets a value indicating whether the server requires pushed authorization requests.</summary>
    public bool RequiresPushedAuthorizationRequests { get; init; }

    /// <summary>Gets the DPoP signing algorithms the server supports.</summary>
    public string[] DPoPSigningAlgValuesSupported { get; init; } = [];
}
