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
    internal static readonly object ExpectedBrowserSessionKey = new( );
    internal sealed record BrowserSessionExpectation( string Did, string SessionId );
    private static readonly object s_observedSessions = new( );
    private static readonly TimeSpan s_entryLifetime = TimeSpan.FromDays( 7 );
    private static readonly TimeSpan s_sweepInterval = TimeSpan.FromMinutes( 5 );
    private readonly SqliteIdentityStore _sdkStore = new( connectionString, loggerFactory: loggerFactory );
    private readonly ILogger _logger = loggerFactory.CreateLogger<BridgeBeatsIdentityStore>( );
    private long _lastSweepTicks;

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
                AND (@previousSession IS NULL OR EXISTS (
                    SELECT 1 FROM "bridgebeats_bluesky_sessions"
                    WHERE "SessionId" = @previousSession AND "Did" = @previousDid AND "ExpiresAtUtcTicks" > @now))
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
        command.Parameters.Add( "@now", SqliteType.Integer ).Value = DateTime.UtcNow.Ticks;
        if (add) {
            string expectedStamp = httpContextAccessor.HttpContext?.Items[ExpectedStampKey] as string
                ?? throw new InvalidOperationException( "The Bluesky account binding was not verified before storing credentials." );
            command.Parameters.Add( "@stamp", SqliteType.Text ).Value = expectedStamp;
            BrowserSessionExpectation? previous = httpContextAccessor.HttpContext?.Items[ExpectedBrowserSessionKey]
                as BrowserSessionExpectation;
            command.Parameters.Add( "@previousSession", SqliteType.Text ).Value = (object?)previous?.SessionId ?? DBNull.Value;
            command.Parameters.Add( "@previousDid", SqliteType.Text ).Value = (object?)previous?.Did ?? DBNull.Value;
        }
        int written = await command.ExecuteNonQueryAsync( cancellationToken );
        if (add && written == 0) {
            throw new InvalidOperationException( "The Bluesky account changed while sign-in was completing." );
        }
        if (written > 0) { await SweepExpiredAsync( connection ); }
    }

    private async Task SweepExpiredAsync( SqliteConnection connection ) {
        long now = DateTime.UtcNow.Ticks;
        long previous = Interlocked.Read( ref _lastSweepTicks );
        if (now - previous < s_sweepInterval.Ticks ||
            Interlocked.CompareExchange( ref _lastSweepTicks, now, previous ) != previous) { return; }
        try {
            await using SqliteCommand sweep = connection.CreateCommand( );
            sweep.CommandText = """
                                DELETE FROM "idunno_bluesky_identities" WHERE "ExpiresAtUtcTicks" <= @now;
                                DELETE FROM "idunno_bluesky_refresh_locks" WHERE "ExpiresAtUtcTicks" <= @now;
                                DELETE FROM "bridgebeats_bluesky_sessions" WHERE "ExpiresAtUtcTicks" <= @now;
                                """;
            sweep.Parameters.Add( "@now", SqliteType.Integer ).Value = now;
            _ = await sweep.ExecuteNonQueryAsync( );
        } catch (Exception ex) {
            _logger.LogWarning( ex, "Expired Bluesky identity cleanup failed." );
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

    internal async Task RegisterBrowserSessionAsync( Did did, string sessionId, DateTimeOffset expires,
        CancellationToken cancellationToken = default ) {
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( cancellationToken );
        await using SqliteCommand register = connection.CreateCommand( );
        register.CommandText = """
                               INSERT INTO "bridgebeats_bluesky_sessions" ("SessionId", "Did", "ExpiresAtUtcTicks")
                               SELECT @session, @did, @expires
                               WHERE EXISTS (SELECT 1 FROM AspNetUsers WHERE AtProtoDid = @did AND SecurityStamp = @stamp)
                                 AND EXISTS (SELECT 1 FROM "idunno_bluesky_identities" WHERE "Did" = @did)
                                 AND (@previousSession IS NULL OR EXISTS (
                                     SELECT 1 FROM "bridgebeats_bluesky_sessions"
                                     WHERE "SessionId" = @previousSession AND "Did" = @previousDid AND "ExpiresAtUtcTicks" > @now));
                               """;
        register.Parameters.Add( "@session", SqliteType.Text ).Value = sessionId;
        register.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        register.Parameters.Add( "@expires", SqliteType.Integer ).Value = expires.UtcTicks;
        register.Parameters.Add( "@stamp", SqliteType.Text ).Value =
            httpContextAccessor.HttpContext?.Items[ExpectedStampKey] as string
            ?? throw new InvalidOperationException( "The Bluesky account binding was not verified before registering the browser." );
        BrowserSessionExpectation? previous = httpContextAccessor.HttpContext?.Items[ExpectedBrowserSessionKey]
            as BrowserSessionExpectation;
        register.Parameters.Add( "@previousSession", SqliteType.Text ).Value = (object?)previous?.SessionId ?? DBNull.Value;
        register.Parameters.Add( "@previousDid", SqliteType.Text ).Value = (object?)previous?.Did ?? DBNull.Value;
        register.Parameters.Add( "@now", SqliteType.Integer ).Value = DateTime.UtcNow.Ticks;
        if (await register.ExecuteNonQueryAsync( cancellationToken ) == 0) {
            throw new InvalidOperationException( "The Bluesky account changed while sign-in was completing." );
        }
    }

    internal async Task<bool> IsBrowserSessionCurrentAsync( Did did, string sessionId,
        CancellationToken cancellationToken = default ) {
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( cancellationToken );
        await using SqliteCommand command = connection.CreateCommand( );
        command.CommandText = """
                              SELECT 1 FROM "bridgebeats_bluesky_sessions"
                              WHERE "SessionId" = @session AND "Did" = @did AND "ExpiresAtUtcTicks" > @now;
                              """;
        command.Parameters.Add( "@session", SqliteType.Text ).Value = sessionId;
        command.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        command.Parameters.Add( "@now", SqliteType.Integer ).Value = DateTime.UtcNow.Ticks;
        return await command.ExecuteScalarAsync( cancellationToken ) is not null;
    }

    internal async Task<bool> ExtendBrowserSessionAsync( Did did, string sessionId, DateTimeOffset expires,
        CancellationToken cancellationToken = default ) {
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( cancellationToken );
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync( cancellationToken );
        await using SqliteCommand command = connection.CreateCommand( );
        command.Transaction = transaction;
        command.CommandText = """
                              UPDATE "bridgebeats_bluesky_sessions" SET "ExpiresAtUtcTicks" = @expires
                              WHERE "SessionId" = @session AND "Did" = @did AND "ExpiresAtUtcTicks" > @now;
                              """;
        command.Parameters.Add( "@session", SqliteType.Text ).Value = sessionId;
        command.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        command.Parameters.Add( "@expires", SqliteType.Integer ).Value = expires.UtcTicks;
        command.Parameters.Add( "@now", SqliteType.Integer ).Value = DateTime.UtcNow.Ticks;
        if (await command.ExecuteNonQueryAsync( cancellationToken ) == 0) {
            await transaction.CommitAsync( cancellationToken );
            return false;
        }
        await using SqliteCommand extendIdentity = connection.CreateCommand( );
        extendIdentity.Transaction = transaction;
        extendIdentity.CommandText = """
                                     UPDATE "idunno_bluesky_identities" SET "ExpiresAtUtcTicks" = MAX("ExpiresAtUtcTicks", @expires)
                                     WHERE "Did" = @did AND "ExpiresAtUtcTicks" > @now;
                                     """;
        extendIdentity.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        extendIdentity.Parameters.Add( "@expires", SqliteType.Integer ).Value = expires.UtcTicks;
        extendIdentity.Parameters.Add( "@now", SqliteType.Integer ).Value = DateTime.UtcNow.Ticks;
        bool extended = await extendIdentity.ExecuteNonQueryAsync( cancellationToken ) > 0;
        await transaction.CommitAsync( cancellationToken );
        return extended;
    }

    /// <returns>Whether this was the final active browser and its shared credential was removed.</returns>
    internal async Task<bool> RemoveBrowserSessionAsync( Did did, string sessionId,
        CancellationToken cancellationToken = default ) {
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( cancellationToken );
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync( cancellationToken );
        await using SqliteCommand remove = connection.CreateCommand( );
        remove.Transaction = transaction;
        remove.CommandText = "DELETE FROM \"bridgebeats_bluesky_sessions\" WHERE \"SessionId\" = @session AND \"Did\" = @did;";
        remove.Parameters.Add( "@session", SqliteType.Text ).Value = sessionId;
        remove.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        if (await remove.ExecuteNonQueryAsync( cancellationToken ) == 0) {
            await transaction.CommitAsync( cancellationToken );
            return false;
        }
        await using SqliteCommand remaining = connection.CreateCommand( );
        remaining.Transaction = transaction;
        remaining.CommandText = """
                                SELECT 1 FROM "bridgebeats_bluesky_sessions"
                                WHERE "Did" = @did AND "ExpiresAtUtcTicks" > @now LIMIT 1;
                                """;
        remaining.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        remaining.Parameters.Add( "@now", SqliteType.Integer ).Value = DateTime.UtcNow.Ticks;
        bool last = await remaining.ExecuteScalarAsync( cancellationToken ) is null;
        if (last) {
            await using SqliteCommand removeIdentity = connection.CreateCommand( );
            removeIdentity.Transaction = transaction;
            removeIdentity.CommandText = "DELETE FROM \"idunno_bluesky_identities\" WHERE \"Did\" = @did;";
            removeIdentity.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
            _ = await removeIdentity.ExecuteNonQueryAsync( cancellationToken );
            await UpdateConcurrencyStampAsync( connection, transaction, did, cancellationToken );
        }
        await transaction.CommitAsync( cancellationToken );
        return last;
    }

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
            await using SqliteCommand removeBrowsers = connection.CreateCommand( );
            removeBrowsers.Transaction = transaction;
            removeBrowsers.CommandText = "DELETE FROM \"bridgebeats_bluesky_sessions\" WHERE \"Did\" = @did;";
            removeBrowsers.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
            _ = await removeBrowsers.ExecuteNonQueryAsync( cancellationToken );
            if (requireMatch) {
                await UpdateConcurrencyStampAsync( connection, transaction, did, cancellationToken );
            } else {
                await RotateStampAsync( connection, transaction, did, cancellationToken );
            }
        }
        await transaction.CommitAsync( cancellationToken );
    }

    private static async Task RotateStampAsync( SqliteConnection connection, SqliteTransaction transaction, Did did,
        CancellationToken cancellationToken ) {
        await using SqliteCommand rotate = connection.CreateCommand( );
        rotate.Transaction = transaction;
        // Global invalidation rejects tickets and Identity updates loaded before account revocation.
        rotate.CommandText = "UPDATE AspNetUsers SET SecurityStamp = @stamp, ConcurrencyStamp = @concurrencyStamp WHERE AtProtoDid = @did;";
        rotate.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        rotate.Parameters.Add( "@stamp", SqliteType.Text ).Value = Guid.NewGuid( ).ToString( "N" );
        rotate.Parameters.Add( "@concurrencyStamp", SqliteType.Text ).Value = Guid.NewGuid( ).ToString( "N" );
        _ = await rotate.ExecuteNonQueryAsync( cancellationToken );
    }

    private static async Task UpdateConcurrencyStampAsync( SqliteConnection connection, SqliteTransaction transaction,
        Did did, CancellationToken cancellationToken ) {
        await using SqliteCommand update = connection.CreateCommand( );
        update.Transaction = transaction;
        update.CommandText = "UPDATE AspNetUsers SET ConcurrencyStamp = @stamp WHERE AtProtoDid = @did;";
        update.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        update.Parameters.Add( "@stamp", SqliteType.Text ).Value = Guid.NewGuid( ).ToString( "N" );
        _ = await update.ExecuteNonQueryAsync( cancellationToken );
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
