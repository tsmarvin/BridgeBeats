using System.Text.Json.Serialization;

namespace BridgeBeats.Contracts.Records;

/// <summary>
/// The persisted, serialized form of a user's AT Protocol credentials, written to storage
/// as camelCase JSON. This is the at-rest counterpart to the live end-user OAuth credentials
/// produced during an OAuth exchange.
/// </summary>
/// <remarks>
/// Per idunno.Bluesky guidance, access tokens are short-lived and are NOT persisted; only the
/// refresh token and related metadata are stored so a session can be restored without
/// re-authenticating. See: https://bluesky.idunno.dev/docs/savingAndRestoringAuthentication.html.
/// Holds credential material. The dominant writer encrypts this record at rest via
/// ASP.NET Data Protection (<c>IDataProtector</c>, purpose
/// <c>BridgeBeats.ServiceAccount.ATProtoSession.v1</c>) before writing to Redis; the TTL is
/// reset on each persist.
/// </remarks>
public sealed record ATProtoPersistedCredentials {

    /// <summary>The OAuth refresh token used to mint new access tokens for this account.</summary>
    [JsonPropertyName( "refreshToken" )]
    public required string RefreshToken { get; init; }

    /// <summary>The DPoP proof key serialized as Base64, when one is persisted for this account; otherwise <see langword="null"/> for legacy authentication.</summary>
    [JsonPropertyName( "dPoPProofKey" )]
    public string? DPoPProofKey { get; init; }

    /// <summary>The most recent DPoP nonce from the server, when one is persisted; otherwise <see langword="null"/> for legacy authentication.</summary>
    [JsonPropertyName( "dPoPNonce" )]
    public string? DPoPNonce { get; init; }

    /// <summary>The service endpoint (PDS) that issued these credentials.</summary>
    [JsonPropertyName( "service" )]
    public required string Service { get; init; }

    /// <summary>The decentralized identifier (DID) of the authenticated account.</summary>
    [JsonPropertyName( "did" )]
    public required string Did { get; init; }

    /// <summary>The handle of the authenticated account; useful for logging and debugging.</summary>
    [JsonPropertyName( "handle" )]
    public required string Handle { get; init; }

    /// <summary>The authentication type (for example, <c>Bearer</c> or <c>DPoP</c>).</summary>
    [JsonPropertyName( "authenticationType" )]
    public required string AuthenticationType { get; init; }

    /// <summary>The absolute instant at which these credentials were written to storage; used to monitor credential age.</summary>
    [JsonPropertyName( "persistedAt" )]
    public required DateTimeOffset PersistedAt { get; init; }

    /// <summary>
    /// Returns a non-secret representation of the credentials, omitting all token fields so this
    /// record cannot leak credential material into logs or exception messages.
    /// </summary>
    /// <returns>A string showing only non-sensitive identity fields.</returns>
    public override string ToString( ) =>
        $"ATProtoPersistedCredentials {{ Handle = {Handle}, Did = {Did}, Service = {Service}, " +
        $"AuthenticationType = {AuthenticationType}, PersistedAt = {PersistedAt} }}";

}
