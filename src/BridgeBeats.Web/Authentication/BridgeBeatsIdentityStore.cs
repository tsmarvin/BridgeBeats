using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using idunno.AtProto;
using idunno.AtProto.Authentication;
using idunno.Bluesky.AspNet.Authentication;
using idunno.Bluesky.AspNet.Authentication.Events;
using idunno.Bluesky.AspNet.Authentication.SQLite;
using Microsoft.Data.Sqlite;

namespace BridgeBeats.Web.Authentication;

/// <summary>
/// Keeps SDK credential writes tied to their OAuth session. SQLite serializes each write with
/// removal, so a refresh from a removed or replaced session cannot recreate its identity.
/// </summary>
internal sealed class BridgeBeatsIdentityStore(
    string connectionString,
    IHttpContextAccessor httpContextAccessor,
    ILoggerFactory loggerFactory
) : IIdentityStore {
    internal static readonly object ExpectedStampKey = new( );
    private static readonly object s_observedSessions = new( );
    private static readonly TimeSpan s_entryLifetime = TimeSpan.FromDays( 7 );
    private readonly SqliteIdentityStore _sdkStore = new( connectionString, loggerFactory: loggerFactory );

    public IdentityStoreEvents Events {
        get => _sdkStore.Events;
        set => _sdkStore.Events = value;
    }

    public Task<ClaimsIdentity?> GetIdentity( Did did, CancellationToken cancellationToken = default ) =>
        GetAndObserveAsync( did, cancellationToken );

    private async Task<ClaimsIdentity?> GetAndObserveAsync( Did did, CancellationToken cancellationToken ) {
        ClaimsIdentity? identity = await _sdkStore.GetIdentity( did, cancellationToken );
        if (identity is not null && httpContextAccessor.HttpContext is { } context) {
            if (context.Items[s_observedSessions] is not Dictionary<string, string> observed) {
                observed = new Dictionary<string, string>( StringComparer.Ordinal );
                context.Items[s_observedSessions] = observed;
            }
            _ = observed.TryAdd( did.ToString( ), ProofKeyHash( identity ) );
        }
        return identity;
    }

    public Task Add( ClaimsIdentity claimsIdentity, CancellationToken cancellationToken = default ) =>
        WriteAsync( claimsIdentity, add: true, cancellationToken );

    public Task Update( ClaimsIdentity identity, CancellationToken cancellationToken ) =>
        WriteAsync( identity, add: false, cancellationToken );

    private async Task WriteAsync( ClaimsIdentity identity, bool add, CancellationToken cancellationToken ) {
        ArgumentNullException.ThrowIfNull( identity );
        Did did = IdentityDid( identity );
        string proofKeyHash = ProofKeyHash( identity );
        using MemoryStream stream = new( );
        using (BinaryWriter writer = new( stream, Encoding.UTF8, leaveOpen: true )) {
            identity.WriteTo( writer );
        }
        IdentityStoreSettingContext setting = new( stream.ToArray( ) );
        await Events.PreStoring( setting );

        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( cancellationToken );
        await using SqliteCommand command = connection.CreateCommand( );
        command.CommandText = add
            ? """
              INSERT INTO "idunno_bluesky_identities" ("Did", "Identity", "ExpiresAtUtcTicks", "ProofKeyHash")
              SELECT @did, @identity, @expires, @proof
              WHERE EXISTS (SELECT 1 FROM AspNetUsers WHERE AtProtoDid = @did AND SecurityStamp = @stamp)
              ON CONFLICT("Did") DO UPDATE SET
                  "Identity" = excluded."Identity",
                  "ExpiresAtUtcTicks" = excluded."ExpiresAtUtcTicks",
                  "ProofKeyHash" = excluded."ProofKeyHash";
              """
            : """
              UPDATE "idunno_bluesky_identities"
              SET "Identity" = @identity, "ExpiresAtUtcTicks" = @expires
              WHERE "Did" = @did AND "ProofKeyHash" = @proof AND "ExpiresAtUtcTicks" > @now;
              """;
        command.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        command.Parameters.Add( "@identity", SqliteType.Blob ).Value = setting.Identity.ToArray( );
        command.Parameters.Add( "@expires", SqliteType.Integer ).Value = DateTime.UtcNow.Add( s_entryLifetime ).Ticks;
        command.Parameters.Add( "@proof", SqliteType.Text ).Value = proofKeyHash;
        if (add) {
            string expectedStamp = httpContextAccessor.HttpContext?.Items[ExpectedStampKey] as string
                ?? throw new InvalidOperationException( "The Bluesky account binding was not verified before storing credentials." );
            command.Parameters.Add( "@stamp", SqliteType.Text ).Value = expectedStamp;
        } else {
            command.Parameters.Add( "@now", SqliteType.Integer ).Value = DateTime.UtcNow.Ticks;
        }
        int written = await command.ExecuteNonQueryAsync( cancellationToken );
        if (add && written == 0) {
            throw new InvalidOperationException( "The Bluesky account changed while sign-in was completing." );
        }
    }

    public Task Remove( Did did, CancellationToken cancellationToken = default ) {
        string? observed = null;
        if (httpContextAccessor.HttpContext?.Items.TryGetValue( s_observedSessions, out object? value ) == true &&
            value is Dictionary<string, string> sessions) {
            _ = sessions.TryGetValue( did.ToString( ), out observed );
        }
        return RemoveSessionAsync( did, observed, requireMatch: true, cancellationToken );
    }

    internal Task RemoveCurrentAsync( Did did, CancellationToken cancellationToken = default ) =>
        RemoveSessionAsync( did, expectedProof: null, requireMatch: false, cancellationToken );

    private async Task RemoveSessionAsync( Did did, string? expectedProof, bool requireMatch, CancellationToken cancellationToken ) {
        ArgumentNullException.ThrowIfNull( did );
        if (requireMatch && expectedProof is null) { return; }
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( cancellationToken );
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync( cancellationToken );
        await using SqliteCommand remove = connection.CreateCommand( );
        remove.Transaction = transaction;
        remove.CommandText = !requireMatch
            ? "DELETE FROM \"idunno_bluesky_identities\" WHERE \"Did\" = @did;"
            : "DELETE FROM \"idunno_bluesky_identities\" WHERE \"Did\" = @did AND \"ProofKeyHash\" = @proof;";
        remove.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        if (expectedProof is not null) { remove.Parameters.Add( "@proof", SqliteType.Text ).Value = expectedProof; }
        int removed = await remove.ExecuteNonQueryAsync( cancellationToken );
        if (!requireMatch || removed > 0) {
            await using SqliteCommand rotate = connection.CreateCommand( );
            rotate.Transaction = transaction;
            // Reject Identity updates that loaded the account before this session ended.
            rotate.CommandText = "UPDATE AspNetUsers SET SecurityStamp = @stamp, ConcurrencyStamp = @concurrencyStamp WHERE AtProtoDid = @did;";
            rotate.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
            rotate.Parameters.Add( "@stamp", SqliteType.Text ).Value = Guid.NewGuid( ).ToString( "N" );
            rotate.Parameters.Add( "@concurrencyStamp", SqliteType.Text ).Value = Guid.NewGuid( ).ToString( "N" );
            _ = await rotate.ExecuteNonQueryAsync( cancellationToken );
        }
        await transaction.CommitAsync( cancellationToken );
    }

    public Task<string?> StartRefresh( Did did, CancellationToken cancellationToken = default ) =>
        _sdkStore.StartRefresh( did, cancellationToken );

    public Task<bool> EndRefresh( Did did, string? refreshLockToken, CancellationToken cancellationToken = default ) =>
        _sdkStore.EndRefresh( did, refreshLockToken, cancellationToken );

    public Task<bool> IsRefreshing( Did did, CancellationToken cancellationToken = default ) =>
        _sdkStore.IsRefreshing( did, cancellationToken );

    private static Did IdentityDid( ClaimsIdentity identity ) =>
        new( identity.FindFirst( AtProtoClaims.Did )?.Value ??
             throw new ArgumentException( "The identity has no DID.", nameof( identity ) ) );

    private static string ProofKeyHash( ClaimsIdentity identity ) {
        if (!AtProtoCredential.TryCreate( identity, out DPoPAccessCredentials? credentials ) || credentials is null) {
            throw new ArgumentException( "The identity has no OAuth proof key.", nameof( identity ) );
        }
        return Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( credentials.DPoPProofKey ) ) );
    }
}
