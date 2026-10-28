using System.Data.Common;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using idunno.AtProto;
using idunno.AtProto.Authentication;
using idunno.Bluesky.AspNet.Authentication;
using idunno.Bluesky.AspNet.Authentication.Events;
using idunno.Bluesky.AspNet.Authentication.SQLite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BridgeBeats.Web.Authentication;

/// <summary>
/// Keeps SDK credential writes tied to their OAuth session. SQLite serializes each write with
/// removal, so a refresh from a removed or replaced session cannot recreate its identity.
/// </summary>
internal sealed partial class BridgeBeatsIdentityStore(
    string connectionString,
    IHttpContextAccessor httpContextAccessor,
    ILoggerFactory loggerFactory
) : IIdentityStore {
    internal static readonly object ExpectedStampKey = new( );
    internal static readonly object ExpectedBrowserSessionKey = new( );
    internal static readonly object PendingBrowserSessionKey = new( );
    internal static readonly object ReplacedIdentityKey = new( );
    internal sealed record BrowserSessionExpectation( string Did, string SessionId );
    internal sealed record PendingBrowserSession( string Did, string SessionId, DateTimeOffset Expires );
    private sealed record StoredCredentialRow( byte[] Identity, string? ProofKeyHash, long ExpiresAtUtcTicks );
    private static readonly object s_observedSessions = new( );
    private static readonly TimeSpan s_entryLifetime = TimeSpan.FromDays( 7 );
    private static readonly TimeSpan s_sweepInterval = TimeSpan.FromMinutes( 5 );
    private const int WriteAttempts = 8;
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

    /// <summary>
    /// Writes <paramref name="credentials"/> unless the stored row is no longer the row those credentials
    /// came from, or already carries a later expiry. The guard is evaluated by the write statement itself,
    /// so a refresh or a replacement landing in between leaves the guard unmatched instead of overwriting
    /// the newer row.
    /// </summary>
    /// <remarks>
    /// The row the caller's credentials came from is identified by its proof-key hash, so credentials
    /// lifted from a grant that has since been replaced cannot reinstate it. AT Proto refresh tokens are
    /// single use, and a replacement grant is already stored, so writing the superseded pair back would
    /// reinstate a spent refresh token and sign the user out at the next refresh. A row that has expired,
    /// or that was removed, is likewise left alone: only an active row is updated, never recreated.
    /// </remarks>
    public async Task<bool> UpdateIfNewer( AccessCredentials credentials, CancellationToken cancellationToken = default ) {
        ArgumentNullException.ThrowIfNull( credentials );
        ClaimsIdentity identity = IIdentityStore.BuildClaimsIdentity( credentials );
        Did did = IdentityDid( identity );
        string didValue = did.ToString( );
        string proofKeyHash = ProofKeyHash( identity );
        IdentityStoreSettingContext setting = new( SerializeIdentity( identity ) );
        await Events.PreStoring( setting );
        byte[] serialized = setting.Identity.ToArray( );
        for (int attempt = 0; attempt < WriteAttempts; attempt++) {
            // The comparison runs before the writer's transaction is open. It can invoke the store's
            // PostRetrieval handlers, which belong to the application rather than to this store, and no
            // write is held open while they run. The guard below is what makes the decision safe: it
            // names the row this comparison read, and the transaction is what evaluates it.
            StoredCredentialRow? stored = await ReadCredentialRowAsync( didValue, cancellationToken );
            if (stored is null ||
                stored.ProofKeyHash != proofKeyHash ||
                stored.ExpiresAtUtcTicks <= DateTime.UtcNow.Ticks ||
                await StoredExpiryIsLaterAsync( stored.Identity, credentials )) {
                return false;
            }
            await using SqliteConnection connection = new( connectionString );
            await connection.OpenAsync( cancellationToken );
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync( cancellationToken );
            await using SqliteCommand write = connection.CreateCommand( );
            write.Transaction = transaction;
            // The guard repeats the comparison this attempt already made, against the row as it stands
            // inside the transaction, so a row replaced after that comparison leaves it unmatched.
            write.CommandText = """
                                UPDATE "idunno_bluesky_identities"
                                SET "Identity" = @identity
                                WHERE "Did" = @did AND "Identity" = @stored AND "ProofKeyHash" = @proof
                                  AND "ExpiresAtUtcTicks" > @now;
                                """;
            write.Parameters.Add( "@did", SqliteType.Text ).Value = didValue;
            write.Parameters.Add( "@identity", SqliteType.Blob ).Value = serialized;
            write.Parameters.Add( "@stored", SqliteType.Blob ).Value = stored.Identity;
            write.Parameters.Add( "@proof", SqliteType.Text ).Value = proofKeyHash;
            write.Parameters.Add( "@now", SqliteType.Integer ).Value = DateTime.UtcNow.Ticks;
            bool written = await write.ExecuteNonQueryAsync( cancellationToken ) > 0;
            await transaction.CommitAsync( cancellationToken );
            if (written) { return true; }
        }
        return false;
    }

    private static byte[] SerializeIdentity( ClaimsIdentity identity ) {
        using MemoryStream stream = new( );
        using (BinaryWriter writer = new( stream, Encoding.UTF8, leaveOpen: true )) {
            identity.WriteTo( writer );
        }
        return stream.ToArray( );
    }

    private async Task<StoredCredentialRow?> ReadCredentialRowAsync( string did, CancellationToken cancellationToken ) {
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( cancellationToken );
        await using SqliteCommand read = connection.CreateCommand( );
        read.CommandText = """
                           SELECT "Identity", "ProofKeyHash", "ExpiresAtUtcTicks"
                           FROM "idunno_bluesky_identities" WHERE "Did" = @did;
                           """;
        read.Parameters.Add( "@did", SqliteType.Text ).Value = did;
        await using SqliteDataReader reader = await read.ExecuteReaderAsync( cancellationToken );
        return await reader.ReadAsync( cancellationToken )
            ? new StoredCredentialRow( reader.GetFieldValue<byte[]>( 0 ),
                reader.IsDBNull( 1 ) ? null : reader.GetString( 1 ), reader.GetInt64( 2 ) )
            : null;
    }

    private async Task<bool> StoredExpiryIsLaterAsync( byte[] stored, AccessCredentials candidate ) {
        try {
            IdentityStoreRetrievedContext retrieved = new( stored );
            await Events.PostRetrieval( retrieved );
            using MemoryStream stream = new( retrieved.Identity.ToArray( ) );
            using BinaryReader reader = new( stream, Encoding.UTF8 );
            ClaimsIdentity identity = new( reader );
            return AtProtoCredential.TryCreate( identity, out DPoPAccessCredentials? storedCredentials ) &&
                   storedCredentials is not null &&
                   storedCredentials.ExpiresOn > candidate.ExpiresOn;
        } catch (Exception ex) {
            // A row this class cannot read cannot be compared. Let the write proceed rather than
            // leaving unreadable credentials in place.
            _logger.LogWarning( ex, "Stored Bluesky credentials could not be compared before a credential write." );
            return false;
        }
    }

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
        await using SqliteTransaction? transaction = add
            ? (SqliteTransaction)await connection.BeginTransactionAsync( cancellationToken ) : null;
        byte[]? replacedIdentity = null;
        if (add) {
            await using SqliteCommand previous = connection.CreateCommand( );
            previous.Transaction = transaction;
            previous.CommandText = "SELECT \"Identity\" FROM \"idunno_bluesky_identities\" WHERE \"Did\" = @did AND \"ProofKeyHash\" != @proof;";
            previous.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
            previous.Parameters.Add( "@proof", SqliteType.Text ).Value = proofKeyHash;
            replacedIdentity = await previous.ExecuteScalarAsync( cancellationToken ) as byte[];
        }
        await using SqliteCommand command = connection.CreateCommand( );
        command.Transaction = transaction;
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
        if (add) {
            HttpContext context = httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException( "The Bluesky browser context is missing." );
            PendingBrowserSession pending = context.Items[PendingBrowserSessionKey] as PendingBrowserSession
                ?? throw new InvalidOperationException( "The Bluesky browser session was not prepared before storing credentials." );
            if (pending.Did != did.ToString( )) {
                throw new InvalidOperationException( "The Bluesky browser session belongs to another DID." );
            }
            await using SqliteCommand register = connection.CreateCommand( );
            register.Transaction = transaction;
            register.CommandText = "INSERT INTO \"bridgebeats_bluesky_sessions\" (\"SessionId\", \"Did\", \"ExpiresAtUtcTicks\") VALUES (@session, @did, @expires);";
            register.Parameters.Add( "@session", SqliteType.Text ).Value = pending.SessionId;
            register.Parameters.Add( "@did", SqliteType.Text ).Value = pending.Did;
            register.Parameters.Add( "@expires", SqliteType.Integer ).Value = pending.Expires.UtcTicks;
            _ = await register.ExecuteNonQueryAsync( cancellationToken );
            await transaction!.CommitAsync( cancellationToken );
            if (replacedIdentity is not null) {
                try {
                    IdentityStoreRetrievedContext retrieved = new( replacedIdentity );
                    await Events.PostRetrieval( retrieved );
                    using MemoryStream previousStream = new( retrieved.Identity.ToArray( ) );
                    using BinaryReader reader = new( previousStream, Encoding.UTF8 );
                    context.Items[ReplacedIdentityKey] = new ClaimsIdentity( reader );
                } catch (Exception ex) {
                    _logger.LogWarning( ex, "Replaced Bluesky credentials could not be read for revocation." );
                }
            }
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

    public async Task Remove( Did did, CancellationToken cancellationToken = default ) {
        string? observed = null;
        if (httpContextAccessor.HttpContext?.Items.TryGetValue( s_observedSessions, out object? value ) == true &&
            value is Dictionary<string, string> sessions) {
            _ = sessions.TryGetValue( did.ToString( ), out observed );
        }
        // The SDK revokes the credential itself before calling this, so the removed row is discarded here.
        _ = await RemoveSessionAsync( did, observed, requireMatch: true, cancellationToken );
    }

    /// <returns>The stored credential row this call deleted, or <see langword="null"/> when no row was removed.</returns>
    internal Task<byte[]?> RemoveCurrentAsync( Did did, CancellationToken cancellationToken = default ) =>
        RemoveSessionAsync( did, expectedProof: null, requireMatch: false, cancellationToken );

    /// <summary>
    /// Deletes the credential and browser rows for <paramref name="did"/> on the supplied connection and
    /// transaction, so account deletion can commit the rows and the local user together. The caller owns
    /// the transaction and must not treat the rows as removed until it commits.
    /// </summary>
    /// <returns>The stored credential row this call deleted, or <see langword="null"/> when the DID had none.</returns>
    internal static async Task<byte[]?> RemoveAccountAsync( Did did, DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken = default ) {
        ArgumentNullException.ThrowIfNull( did );
        byte[]? removed;
        await using (DbCommand removeIdentity = connection.CreateCommand( )) {
            removeIdentity.Transaction = transaction;
            removeIdentity.CommandText = "DELETE FROM \"idunno_bluesky_identities\" WHERE \"Did\" = @did RETURNING \"Identity\";";
            _ = removeIdentity.Parameters.Add( CreateParameter( removeIdentity, "@did", did.ToString( ) ) );
            removed = await removeIdentity.ExecuteScalarAsync( cancellationToken ) as byte[];
        }
        await using (DbCommand removeBrowsers = connection.CreateCommand( )) {
            removeBrowsers.Transaction = transaction;
            removeBrowsers.CommandText = """
                                        DELETE FROM "bridgebeats_bluesky_sessions" WHERE "Did" = @did;
                                        DELETE FROM "idunno_bluesky_refresh_locks" WHERE "Did" = @did;
                                        """;
            _ = removeBrowsers.Parameters.Add( CreateParameter( removeBrowsers, "@did", did.ToString( ) ) );
            _ = await removeBrowsers.ExecuteNonQueryAsync( cancellationToken );
        }
        return removed;
    }

    private static DbParameter CreateParameter( DbCommand command, string name, object value ) {
        DbParameter parameter = command.CreateParameter( );
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }

    /// <summary>
    /// Restores a stored row to the credential it holds. The row is protected by the configured store
    /// events, so it is passed through <see cref="IdentityStoreEvents.PostRetrieval"/> before parsing.
    /// Call this after the deleting transaction commits, so no user callback runs while the write is open.
    /// </summary>
    internal async Task<ClaimsIdentity?> ReadCredentialAsync( byte[]? stored, string did ) {
        if (stored is null) { return null; }
        try {
            IdentityStoreRetrievedContext retrieved = new( stored );
            await Events.PostRetrieval( retrieved );
            using MemoryStream stream = new( retrieved.Identity.ToArray( ) );
            using BinaryReader reader = new( stream, Encoding.UTF8 );
            return new ClaimsIdentity( reader );
        } catch (Exception ex) {
            LogCredentialReadFailed( _logger, ex, did );
            return null;
        }
    }

    [LoggerMessage( EventId = Logging.LogEventIds.Authentication.BlueskyIdentityReadFailed, Level = LogLevel.Warning,
        Message = "Stored Bluesky credentials for {Did} could not be read for remote revocation." )]
    private static partial void LogCredentialReadFailed( ILogger logger, Exception exception, string did );

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

    /// <returns>
    /// The stored credential row deleted when this was the final active browser, or <see langword="null"/>
    /// when the session row was already gone or another browser still holds the shared credential.
    /// </returns>
    internal async Task<byte[]?> RemoveBrowserSessionAsync( Did did, string sessionId,
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
            return null;
        }
        await using SqliteCommand remaining = connection.CreateCommand( );
        remaining.Transaction = transaction;
        remaining.CommandText = """
                                SELECT 1 FROM "bridgebeats_bluesky_sessions"
                                WHERE "Did" = @did AND "ExpiresAtUtcTicks" > @now LIMIT 1;
                                """;
        remaining.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        remaining.Parameters.Add( "@now", SqliteType.Integer ).Value = DateTime.UtcNow.Ticks;
        if (await remaining.ExecuteScalarAsync( cancellationToken ) is not null) {
            await transaction.CommitAsync( cancellationToken );
            return null;
        }
        byte[]? removed = await DeleteIdentityAsync( connection, transaction, did, cancellationToken );
        await UpdateConcurrencyStampAsync( connection, transaction, did, cancellationToken );
        await transaction.CommitAsync( cancellationToken );
        return removed;
    }

    private async Task<byte[]?> RemoveSessionAsync( Did did, string? expectedProof, bool requireMatch,
        CancellationToken cancellationToken ) {
        ArgumentNullException.ThrowIfNull( did );
        if (requireMatch && expectedProof is null) { return null; }
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( cancellationToken );
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync( cancellationToken );
        await using SqliteCommand remove = connection.CreateCommand( );
        remove.Transaction = transaction;
        // RETURNING yields the row this statement deleted, so the caller revokes the credential that
        // actually left the store rather than whatever a later read happens to find under the DID.
        remove.CommandText = !requireMatch
            ? "DELETE FROM \"idunno_bluesky_identities\" WHERE \"Did\" = @did RETURNING \"Identity\";"
            : "DELETE FROM \"idunno_bluesky_identities\" WHERE \"Did\" = @did AND \"ProofKeyHash\" = @proof RETURNING \"Identity\";";
        remove.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        if (expectedProof is not null) { remove.Parameters.Add( "@proof", SqliteType.Text ).Value = expectedProof; }
        byte[]? removed = await remove.ExecuteScalarAsync( cancellationToken ) as byte[];
        if (!requireMatch || removed is not null) {
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
        return removed;
    }

    private static async Task<byte[]?> DeleteIdentityAsync( SqliteConnection connection, SqliteTransaction transaction,
        Did did, CancellationToken cancellationToken ) {
        await using SqliteCommand removeIdentity = connection.CreateCommand( );
        removeIdentity.Transaction = transaction;
        removeIdentity.CommandText = "DELETE FROM \"idunno_bluesky_identities\" WHERE \"Did\" = @did RETURNING \"Identity\";";
        removeIdentity.Parameters.Add( "@did", SqliteType.Text ).Value = did.ToString( );
        return await removeIdentity.ExecuteScalarAsync( cancellationToken ) as byte[];
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
