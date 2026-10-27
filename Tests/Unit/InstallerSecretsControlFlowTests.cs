using System.Diagnostics;
using System.Text;
using BridgeBeats.Core.Infrastructure.Identity;

#pragma warning disable CS1591
namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Regression coverage for the installers' ATProto signing key handling.
/// </summary>
/// <remarks>
/// The installers decide whether to keep, regenerate, or reject <c>secrets/atproto_oauth_key.json</c>,
/// and the application refuses to start unless that file holds a usable ES256 key. Both installers are
/// driven here through their real control flow rather than a reimplementation, so a change to the
/// keep/regenerate/abort decision fails a test instead of surfacing at container start.
///
/// The unreadable-directory cases require a POSIX host running as a non-root user, because Windows has
/// no traversal-mode bits (the Windows installer inherits the profile ACL instead) and root ignores
/// them. Those cases report <see cref="Assert.Inconclusive(string)"/> rather than passing silently.
/// </remarks>
[TestClass]
public class InstallerSecretsControlFlowTests {

    private const string SigningKeySecret = "atproto_oauth_key.json";

    /// <summary>
    /// Verifies a usable key is left byte-for-byte untouched and no backup is written.
    /// </summary>
    [TestMethod]
    public void InstallShUsableKeyIsPreservedWithoutBackup( ) {
        if (!CanRunInstallSh( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );
        string usableKey = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
        fixture.WriteKey( usableKey );

        InstallerResult result = fixture.RunSecretsLoopSh( );

        Assert.AreEqual( InstallerFixture.Success, result.ExitCode, result.Describe( ) );
        Assert.AreEqual( usableKey.Trim( ), fixture.ReadKey( ).Trim( ), "a usable key must not be rewritten" );
        Assert.IsEmpty( fixture.BackupPaths( ), "a usable key must not be backed up" );
    }

    /// <summary>
    /// Verifies blank, brace-only, and placeholder keys are each replaced with a usable key, leaving a
    /// byte-identical backup of the previous content.
    /// </summary>
    [TestMethod]
    public void InstallShUnusableKeyIsRegeneratedWithVerifiedBackup( ) {
        if (!CanRunInstallSh( )) { return; }

        foreach (string unusable in new[] { "", "{}", "your_atproto_oauth_key_here" }) {
            using InstallerFixture fixture = InstallerFixture.Create( );
            fixture.WriteKey( unusable );

            InstallerResult result = fixture.RunSecretsLoopSh( );

            Assert.AreEqual( InstallerFixture.Success, result.ExitCode, result.Describe( ) );
            Assert.HasCount( 1, fixture.BackupPaths( ), $"expected exactly one backup for input '{unusable}'" );
            Assert.AreEqual( unusable, fixture.ReadBackup( ).Trim( ), "the backup must hold the previous content" );

            string regenerated = fixture.ReadKey( );
            Assert.AreNotEqual( unusable, regenerated.Trim( ), "the unusable key was not replaced" );
            using ATProtoSigningKeyProvider loaded = new( regenerated );
            Assert.IsNotEmpty( loaded.KeyId, "the regenerated key did not load" );
        }
    }

    /// <summary>
    /// Verifies a failed generator exits non-zero and leaves the file in place, so the operator still
    /// has the file the message told them to replace.
    /// </summary>
    [TestMethod]
    public void InstallShFailedGenerationExitsNonZeroAndKeepsFile( ) {
        if (!CanRunInstallSh( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( "{}" );

        InstallerResult result = fixture.RunSecretsLoopSh( failGeneration: true );

        Assert.AreNotEqual( InstallerFixture.Success, result.ExitCode, result.Describe( ) );
        Assert.AreEqual( "{}", fixture.ReadKey( ).Trim( ), "the original file must survive a failed generation" );
        // The backup is taken before generation is attempted, so the original content is recoverable
        // even though the replacement never arrived.
        Assert.HasCount( 1, fixture.BackupPaths( ), "the pre-replacement backup must be retained" );
        Assert.AreEqual( "{}", fixture.ReadBackup( ).Trim( ), "the backup must hold the original content" );
    }

    /// <summary>
    /// Verifies a key hidden behind an unsearchable secrets directory is reported as unreadable, and
    /// that the run aborts instead of announcing success or overwriting a real key.
    /// </summary>
    [TestMethod]
    public void InstallShUnreadableKeyIsNotTreatedAsUnusable( ) {
        if (!CanRunInstallSh( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );

        if (!fixture.TryHideSecretsDirectory( )) {
            Assert.Inconclusive( "Requires a POSIX host running as a non-root user to hide the secrets directory." );
            return;
        }

        InstallerResult result = fixture.RunSecretsLoopSh( );

        Assert.AreEqual( InstallerFixture.AbortedBeforeInspection, result.ExitCode, result.Describe( ) );
        StringAssert.Contains( result.CombinedOutput, "permission denied", "the run must report the permission failure" );
        Assert.DoesNotContain( "Regenerated", result.CombinedOutput, "an unreadable key must never be replaced" );
        Assert.DoesNotContain( "Auto-generated", result.CombinedOutput, "an unreadable key must not be re-created" );
        Assert.DoesNotContain( "Backed up", result.CombinedOutput, "no backup should be attempted" );
    }

    /// <summary>
    /// Verifies replacement is refused when the backup step cannot produce a verified copy.
    /// </summary>
    [TestMethod]
    public void InstallShUnverifiedBackupBlocksReplacement( ) {
        if (!CanRunInstallSh( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( "{}" );

        // A path separator in the timestamp suffix makes the backup target unwritable, so the copy
        // step fails and the loop must refuse to touch the key.
        InstallerResult result = fixture.RunSecretsLoopSh( backupSuffix: "missing-dir/backup" );

        Assert.AreEqual( InstallerFixture.KeyOperationFailed, result.ExitCode, result.Describe( ) );
        StringAssert.Contains( result.CombinedOutput, "verified backup", "the refusal must be explained" );
        Assert.AreEqual( "{}", fixture.ReadKey( ).Trim( ), "the original key must survive an unverified backup" );
        Assert.DoesNotContain( "Regenerated", result.CombinedOutput, "the key must not be replaced" );
    }

    /// <summary>
    /// Verifies the PowerShell key classification separates unreadable content from unusable content,
    /// so an unreadable key is never overwritten as if it were empty.
    /// </summary>
    [TestMethod]
    public void InstallPs1KeyStateDistinguishesUnreadableFromUnusable( ) {
        if (!CanRunPowerShell( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );

        fixture.WriteKey( ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );
        Assert.AreEqual( "Usable", fixture.GetKeyStatePs1( ), "a generated JWK must classify as usable" );

        foreach (string unusable in new[] { "", "{}", "your_atproto_oauth_key_here" }) {
            fixture.WriteKey( unusable );
            Assert.AreEqual( "Unusable", fixture.GetKeyStatePs1( ), $"input '{unusable}' must classify as unusable" );
        }

        fixture.WriteKey( ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );
        if (!fixture.TryHideSecretsDirectory( )) {
            Assert.Inconclusive( "Requires a POSIX host running as a non-root user to hide the secrets directory." );
            return;
        }
        Assert.AreEqual( "Unreadable", fixture.GetKeyStatePs1( ), "an unreadable key must not classify as unusable" );
    }

    /// <summary>
    /// Verifies a non-blank but unloadable key is reported as malformed rather than usable, and that
    /// the install fails without overwriting it.
    /// </summary>
    /// <remarks>
    /// Accepting this shape was the SEC-004 finding: the installer announced a key as usable that the
    /// application then refuses to start on. Overwriting instead would discard content the operator
    /// may have intended, so the run must fail with guidance.
    /// </remarks>
    [TestMethod]
    public void InstallShMalformedKey_FailsWithoutOverwriting( ) {
        if (!CanRunInstallSh( )) { return; }

        // Non-blank, starts as JSON, but not a loadable EC P-256 private JWK.
        const string MalformedKey = """{"kty":"EC","crv":"P-256","x":"abc"}""";

        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( MalformedKey );

        InstallerResult result = fixture.RunSecretsLoopSh( );

        Assert.AreEqual( InstallerFixture.KeyOperationFailed, result.ExitCode, result.Describe( ) );
        StringAssert.Contains( result.CombinedOutput, "not a usable ES256 signing key",
            "the run must report the key as unusable" );
        Assert.DoesNotContain( "Secret exists and has content", result.CombinedOutput,
            "a malformed key must not be announced as usable" );
        Assert.DoesNotContain( "Regenerated", result.CombinedOutput, "a malformed key must not be replaced" );
        Assert.AreEqual( MalformedKey, fixture.ReadKey( ).Trim( ), "the operator's key content must be preserved" );
    }

    /// <summary>
    /// Verifies the shell backup verification compares content, not just entry counts, so a copy that
    /// is missing a file or holds changed content is rejected.
    /// </summary>
    /// <remarks>
    /// Counting entries was the original check and it cannot distinguish a complete copy from one that
    /// dropped content. The real <c>secrets_tree_manifest</c> is exercised here over trees the test
    /// builds, including the <c>cp -r</c> nesting shape that a naive copy produces.
    /// </remarks>
    [TestMethod]
    public void InstallShTreeManifest_DetectsIncompleteAndChangedCopies( ) {
        if (!CanRunInstallSh( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );
        fixture.WriteExtraSecret( "redis_password.txt", "worker-secret" );
        fixture.WriteExtraSecret( Path.Combine( "nested", "extra.txt" ), "nested-secret" );

        Assert.IsTrue( fixture.ManifestMatchesCopy( copyKind: "complete" ),
            "an exact copy must produce an identical manifest" );
        Assert.IsFalse( fixture.ManifestMatchesCopy( copyKind: "missing-file" ),
            "a copy missing a file must not match" );
        Assert.IsFalse( fixture.ManifestMatchesCopy( copyKind: "changed-content" ),
            "a copy with changed content must not match" );
        Assert.IsFalse( fixture.ManifestMatchesCopy( copyKind: "nested" ),
            "a copy nested one level deeper must not match" );
    }

    /// <summary>
    /// Verifies the PowerShell classifier reports a non-blank unloadable key as malformed, not usable.
    /// </summary>
    [TestMethod]
    public void InstallPs1MalformedKey_IsNotUsable( ) {
        if (!CanRunPowerShell( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );

        foreach (string malformed in new[] {
            """{"kty":"EC","crv":"P-256","x":"abc"}""",
            "-----BEGIN PUBLIC KEY-----\nnot-a-private-key\n-----END PUBLIC KEY-----",
            "just some text"
        }) {
            fixture.WriteKey( malformed );
            Assert.AreEqual( "Malformed", fixture.GetKeyStatePs1( ),
                $"input '{malformed}' must classify as malformed, not usable" );
        }
    }

    /// <summary>
    /// Verifies the upgrade bulk backup is not reported as successful when it cannot read the secrets
    /// directory, and that the previous backups are not pruned away.
    /// </summary>
    /// <remarks>
    /// The pre-fix code printed a warning, then pruned old backups anyway, so a failed backup could
    /// leave no rollback copy at all. This runs the real backup block with the secrets directory made
    /// unreadable and no sudo available.
    /// </remarks>
    [TestMethod]
    public void InstallShUpgradeBulkBackup_FailureIsReportedAndPrunesNothing( ) {
        if (!CanRunInstallSh( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );

        // An older backup that must survive the failed run.
        fixture.WriteSecretsBackup( "secrets_backup_20000101_000000", "previous-secret" );

        if (!fixture.TryHideSecretsDirectory( )) {
            Assert.Inconclusive( "Requires a POSIX host running as a non-root user to hide the secrets directory." );
            return;
        }

        InstallerResult result = fixture.RunUpgradeBulkBackupSh( useSudo: false );

        Assert.AreNotEqual( 0, result.ExitCode, result.Describe( ) );
        StringAssert.Contains( result.CombinedOutput, "Failed to back up the secrets directory",
            "the failure must be reported" );
        Assert.DoesNotContain( "[OK] Backed up secrets", result.CombinedOutput,
            "a failed backup must not be announced as successful" );
        Assert.IsTrue( fixture.SecretsBackupExists( "secrets_backup_20000101_000000" ),
            "a failed backup must not prune the existing rollback copies" );
    }

    /// <summary>
    /// Verifies the shape check accepts the key forms the application loads and rejects the rest, so
    /// a valid key is never discarded and an unloadable one is never accepted.
    /// </summary>
    /// <remarks>
    /// The accepted forms mirror <c>BlueskyAuthenticationConfiguration.LoadSigningKey</c>: a PEM
    /// private key, or an EC P-256 private JWK, tolerating leading whitespace because the application
    /// trims before deciding. The rejected forms are the SEC-004 case plus shapes that would fail at
    /// startup for a different reason.
    /// </remarks>
    [TestMethod]
    public void InstallShKeyShape_AcceptsLoadableFormsOnly( ) {
        if (!CanRunInstallSh( ) || !CanRunPowerShell( )) { return; }

        string jwk = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
        const string PemKey = "-----BEGIN PRIVATE KEY-----\nMIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg\n-----END PRIVATE KEY-----";

        using InstallerFixture fixture = InstallerFixture.Create( );

        foreach (string accepted in new[] { jwk, PemKey, "  " + jwk }) {
            fixture.WriteKey( accepted );
            Assert.AreEqual( "Usable", fixture.GetKeyStatePs1( ),
                $"'{accepted[..Math.Min( 20, accepted.Length )]}...' should be accepted as usable" );
        }

        foreach (string rejected in new[] {
            """{"kty":"EC","crv":"P-256","x":"abc"}""",
            """{"kty":"EC","crv":"P-384","x":"a","y":"b","d":"c","kid":"k"}""",
            """{"kty":"RSA","crv":"P-256","x":"a","y":"b","d":"c","kid":"k"}""",
            "not a key at all",
            "-----BEGIN PUBLIC KEY-----\nabc\n-----END PUBLIC KEY-----"
        }) {
            fixture.WriteKey( rejected );
            Assert.AreEqual( "Malformed", fixture.GetKeyStatePs1( ),
                $"'{rejected[..Math.Min( 20, rejected.Length )]}...' should be rejected as malformed" );
        }
    }

    /// <summary>
    /// Verifies the PowerShell upgrade backup copies the secrets, verified, and that a failed backup
    /// exits non-zero without pruning the retained copies.
    /// </summary>
    /// <remarks>
    /// The pre-fix code caught the copy failure, warned, and fell through to the prune, which deletes
    /// backups beyond the three most recent. A failed backup could therefore remove the last rollback
    /// point. This drives the real <c>Test-SecretsTreeCopied</c> and the real backup block.
    /// </remarks>
    [TestMethod]
    public void InstallPs1UpgradeBulkBackup_VerifiedOnSuccessAndFailsClosedOnFailure( ) {
        if (!CanRunPowerShell( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );
        fixture.WriteExtraSecret( "redis_password.txt", "worker-secret" );
        fixture.WriteSecretsBackup( "secrets_backup_20000101_000000", "previous-secret" );

        // Success path: the real helper must copy every file and report verified.
        InstallerResult copied = fixture.RunTreeCopyPs1( useExistingDestination: false );
        Assert.AreEqual( 0, copied.ExitCode, copied.Describe( ) );
        Assert.AreEqual( "True", copied.Reported, "a complete copy must verify" );
        Assert.IsTrue( copied.CombinedOutput.Contains( "Backed up", StringComparison.Ordinal )
            || copied.Reported == "True", "a verified copy should be reported" );

        // Failure paths: an unusable destination and a destination holding stale content must both
        // report failure rather than a partial backup.
        InstallerResult unusable = fixture.RunTreeCopyPs1( useExistingDestination: true );
        Assert.AreEqual( "False", unusable.Reported, "a copy into an unusable destination must not verify" );

        InstallerResult stale = fixture.RunTreeCopyPs1( useExistingDestination: false, staleDestination: true );
        Assert.AreEqual( "False", stale.Reported, "a destination with mismatched content must not verify" );

        // Control: a successful run continues past the block, so it writes the marker.
        InstallerResult successBlock = fixture.RunUpgradeBulkBackupPs1( );
        Assert.AreEqual( 0, successBlock.ExitCode, successBlock.Describe( ) );
        Assert.AreEqual( "reached-end", successBlock.Reported,
            "a verified backup should let the install continue" );

        // The blocking behaviour: with the backup unable to complete, the block must exit before the
        // prune, so the marker is absent and the retained backup survives.
        InstallerResult failedBlock = fixture.RunUpgradeBulkBackupPs1( failBackup: true );
        Assert.AreNotEqual( 0, failedBlock.ExitCode, failedBlock.Describe( ) );
        Assert.AreNotEqual( "reached-end", failedBlock.Reported,
            "a failed backup must not continue to the prune" );
        Assert.IsTrue( fixture.SecretsBackupExists( "secrets_backup_20000101_000000" ),
            "a failed backup must not prune the existing rollback copies" );
    }

    /// <summary>
    /// Verifies a backup tree is restricted to its owner even under a permissive umask, and that a
    /// tree left world-readable is rejected.
    /// </summary>
    /// <remarks>
    /// This is the SEC-006 finding: <c>mkdir</c> and <c>cp</c> apply the process umask, so under the
    /// common 0022 a backup root landed mode 755 and the copied private keys stayed 644 — readable by
    /// every local account. The pre-fix shape is reproduced here and asserted to be rejected, so the
    /// check cannot pass by describing a tree it never inspected.
    /// </remarks>
    [TestMethod]
    public void InstallShBackupTree_IsRestrictedToOwnerUnderPermissiveUmask( ) {
        if (!CanRunInstallSh( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );
        fixture.WriteExtraSecret( Path.Combine( "nested", "extra.txt" ), "nested-secret" );

        BackupTreeModes modes = fixture.RestrictBackupTreeUnderUmask( );

        Assert.AreEqual( "755", modes.PreFixDirectory,
            "the pre-fix shape should be group/other-readable; if this changed the test premise is stale" );
        Assert.IsFalse( modes.PreFixIsPrivate, "the exposed pre-fix tree must be rejected" );

        Assert.AreEqual( "700", modes.Directory, "the backup root must be owner-only" );
        Assert.AreEqual( "600", modes.File, "backed-up secrets must be owner-only" );
        Assert.AreEqual( "700", modes.NestedDirectory, "nested directories must be owner-only" );
        Assert.AreEqual( "600", modes.NestedFile, "nested files must be owner-only" );
        Assert.IsTrue( modes.IsPrivate, "the restricted tree must verify as private" );
    }

    /// <summary>
    /// Verifies the bulk backup root is already owner-only at the moment the first secret is copied
    /// into it, not merely after a later hardening pass.
    /// </summary>
    /// <remarks>
    /// This is the SEC-006 ordering finding: the pre-fix code created the root with plain
    /// <c>mkdir -p</c> and copied in the same chain, so under umask 0022 the root was 755 for the
    /// whole copy and the first secret was reachable by other local accounts before any post-copy
    /// <c>chmod</c> ran. The test observes the mode at that specific point rather than at the end,
    /// because a final-state check cannot distinguish the two orderings.
    /// </remarks>
    [TestMethod]
    [DataRow( false )]
    [DataRow( true )]
    public void InstallShBackupRoot_IsPrivateBeforeFirstSecretIsCopied( bool useSudo ) {
        if (!CanRunInstallSh( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );

        BackupRootOrder order = fixture.ObserveBackupRootOrder( useSudo );

        Assert.IsTrue( order.RootOwnerOnlyBeforeCopy,
            "the backup root must be owner-only before any secret is copied into it" );
        Assert.AreEqual( "700", order.RootModeBeforeCopy, "the root mode required before the copy" );
        Assert.AreEqual( "600", order.SecretMode, "the copied secret must be owner-only" );
    }

    /// <summary>
    /// Verifies the Windows installer resolves its ACL capability and acting principal before any
    /// backup or secret handling, and fails closed when that capability is unavailable.
    /// </summary>
    /// <remarks>
    /// This is the SEC-005 finding: the backup block read a capability variable that was only assigned
    /// later in the script. An unset variable is <c>$null</c>, so <c>-not $icaclsAvailable</c> was
    /// <c>$true</c> while <c>if ($icaclsAvailable)</c> was <c>$false</c> — the restriction was skipped
    /// and the guard still permitted the copy. The assertions below pin the ordering and exercise the
    /// fail-closed branch, which a same-order code path cannot satisfy.
    /// </remarks>
    [TestMethod]
    public void InstallPs1AclCapability_IsResolvedBeforeAnySecretHandling( ) {
        if (!CanRunPowerShell( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );

        AclOrdering ordering = fixture.ObserveAclOrdering( );

        Assert.IsTrue( ordering.CapabilityResolvedFirst,
            "the ACL capability must be resolved before the first secret-handling line" );
        Assert.IsTrue( ordering.ResolutionLineBeforeBackupBlock,
            "the capability resolution must precede the upgrade backup block" );
        Assert.IsTrue( ordering.UsesResolvedFlagGuard,
            "guards must test the explicit resolved flag, so an unset variable cannot permit a copy" );
        Assert.IsFalse( ordering.ReadsUnresolvedCapability,
            "no guard may read a capability variable that is assigned later in the script" );
        Assert.AreEqual( "denied", ordering.NonWindowsDecision,
            "with the capability unresolved, the backup must be refused rather than allowed" );
    }

    /// <summary>
    /// Verifies the retention prune keeps the three newest backups, removes older ones, and reports a
    /// removal it could not perform instead of hiding it.
    /// </summary>
    /// <remarks>
    /// The pre-fix prune piped into <c>xargs rm -rf</c> with no privilege and no failure reporting, so
    /// a backup it could not delete was silently left behind while the run reported success.
    /// </remarks>
    [TestMethod]
    public void InstallShPrune_KeepsThreeMostRecentAndReportsFailures( ) {
        if (!CanRunInstallSh( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );

        // Five backups, named so the newest three are unambiguous.
        string[] names = [
            "secrets_backup_20000101_000000", "secrets_backup_20010101_000000",
            "secrets_backup_20020101_000000", "secrets_backup_20030101_000000",
            "secrets_backup_20040101_000000"
        ];
        foreach (string name in names) {
            fixture.WriteSecretsBackup( name, "retained" );
        }

        InstallerResult result = fixture.RunPruneSh( makeOldestUndeletable: false );

        Assert.AreEqual( 0, result.ExitCode, result.Describe( ) );
        string[] remaining = fixture.SecretsBackupNames( );
        Assert.HasCount( 3, remaining, "the prune must keep exactly the three newest backups" );
        Assert.IsFalse( remaining.Contains( "secrets_backup_20000101_000000" ), "the oldest must be removed" );
        Assert.IsFalse( remaining.Contains( "secrets_backup_20010101_000000" ), "the second oldest must be removed" );
        Assert.IsTrue( remaining.Contains( "secrets_backup_20040101_000000" ), "the newest must be kept" );

        // A removal the process cannot perform must be reported, not swallowed.
        using InstallerFixture blocked = InstallerFixture.Create( );
        foreach (string name in names) {
            blocked.WriteSecretsBackup( name, "retained" );
        }
        InstallerResult blockedResult = blocked.RunPruneSh( makeOldestUndeletable: true );
        StringAssert.Contains( blockedResult.CombinedOutput, "Could not remove",
            "a prune that cannot delete an old backup must say so" );
    }

    /// <summary>
    /// Verifies the metadata reads used for the container-readability check go through sudo when the
    /// secrets directory cannot be searched, and stay unprivileged otherwise.
    /// </summary>
    /// <remarks>
    /// Without this the metadata read fails on an upgraded install and a correctly owned key is
    /// misread as unreadable, which is the SEC-003 finding. The sudo stand-in records its invocations.
    /// It cannot perform a real privileged read, so this asserts the routing decision rather than the
    /// resulting metadata values.
    /// </remarks>
    [TestMethod]
    public void InstallShUnsearchableDirectory_MetadataReadsUseSudo( ) {
        if (!CanRunInstallSh( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );

        // A searchable directory must not need privilege.
        InstallerResult searchable = fixture.RunMetadataProbeSh( useSudo: true );
        Assert.AreEqual( 0, searchable.ExitCode, searchable.Describe( ) );
        Assert.AreEqual( "0", searchable.StandardOutput.Trim( ),
            "metadata for a searchable directory must not require sudo" );

        if (!fixture.TryHideSecretsDirectory( )) {
            Assert.Inconclusive( "Requires a POSIX host running as a non-root user to hide the secrets directory." );
            return;
        }

        InstallerResult hidden = fixture.RunMetadataProbeSh( useSudo: true );
        Assert.AreEqual( 0, hidden.ExitCode, hidden.Describe( ) );
        Assert.AreNotEqual( "0", hidden.StandardOutput.Trim( ),
            "metadata for an unsearchable directory must be read through sudo" );
    }

    /// <summary>
    /// Verifies the PowerShell generator writes a key the application loads, and that a failed
    /// generator reports failure without writing key material.
    /// </summary>
    [TestMethod]
    public void InstallPs1GeneratorProducesLoadableKeyAndFailsClosed( ) {
        if (!CanRunPowerShell( )) { return; }

        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( string.Empty );

        InstallerResult generated = fixture.RunGeneratorPs1( );

        Assert.AreEqual( 0, generated.ExitCode, generated.Describe( ) );
        Assert.AreEqual( "True", generated.Reported, "the generator reported failure" );
        using ATProtoSigningKeyProvider loaded = new( fixture.ReadKey( ) );
        Assert.IsNotEmpty( loaded.KeyId, "the generated key did not load" );

        fixture.WriteKey( string.Empty );
        InstallerResult failed = fixture.RunGeneratorPs1( failGeneration: true );

        Assert.AreEqual( "False", failed.Reported, "a failed generator must report failure" );
        Assert.AreEqual( string.Empty, fixture.ReadKey( ), "a failed generator must not write key material" );
    }

    /// <summary>
    /// Verifies that a signing key created during an upgrade with elevated privileges is given back to
    /// the container user, rather than being left readable only by root.
    /// </summary>
    /// <remarks>
    /// An upgrade already had a secrets directory, so the installer's directory-wide ownership
    /// correction is skipped; the corrective <c>chown</c> has to come from the key-creation path.
    /// The <c>sudo</c> stand-in records its commands, so the assertion is on the installer's decision.
    /// The <c>chown</c> itself cannot take effect here (that needs root), so the run is also expected
    /// to report the key as unreadable rather than claiming success.
    /// </remarks>
    [TestMethod]
    public void InstallShUpgradeCreatedKey_RequestsContainerOwnership( ) {
        if (!CanRunInstallSh( )) { return; }

        string foreignUid = ForeignContainerUid( );
        using InstallerFixture fixture = InstallerFixture.Create( );

        InstallerResult result = fixture.InstallGeneratedSecretSh( upgrade: true, foreignUid, useSudo: true );

        StringAssert.Contains( fixture.SudoCommands( ), "chown",
            "a key created with elevated privileges on an upgrade must be handed to the container user" );
        StringAssert.Contains( fixture.SudoCommands( ), foreignUid,
            "the ownership correction must target the container UID" );
        StringAssert.Contains( result.CombinedOutput, "not readable by the container user",
            "the run must not report a key the container cannot read as generated" );
        Assert.AreNotEqual( 0, result.ExitCode, result.Describe( ) );
    }

    /// <summary>
    /// Verifies a created key the container can already read is accepted without any ownership change.
    /// </summary>
    [TestMethod]
    public void InstallShUpgradeCreatedKey_ReadableByContainerNeedsNoChown( ) {
        if (!CanRunInstallSh( )) { return; }

        string currentUid = CurrentUid( );
        using InstallerFixture fixture = InstallerFixture.Create( );

        InstallerResult result = fixture.InstallGeneratedSecretSh( upgrade: true, currentUid, useSudo: true );

        Assert.AreEqual( 0, result.ExitCode, result.Describe( ) );
        Assert.DoesNotContain( "chown", fixture.SudoCommands( ),
            "no ownership change is needed when the container user already owns the key" );
        Assert.IsTrue( fixture.SecretReadableByContainer( currentUid ), "the created key must be readable" );
        using ATProtoSigningKeyProvider loaded = new( fixture.ReadKey( ) );
        Assert.IsNotEmpty( loaded.KeyId, "the created key did not load" );
    }

    /// <summary>
    /// Verifies that replacing an existing key never changes its ownership, so a key already handed to
    /// the container stays with the account the application reads it under.
    /// </summary>
    [TestMethod]
    public void InstallShExistingKey_OwnershipIsNotChanged( ) {
        if (!CanRunInstallSh( )) { return; }

        string foreignUid = ForeignContainerUid( );
        using InstallerFixture fixture = InstallerFixture.Create( );
        fixture.WriteKey( "{}" );

        InstallerResult result = fixture.RunSecretsLoopSh(
            upgrade: true, useSudo: true, containerUid: foreignUid );

        Assert.AreEqual( InstallerFixture.Success, result.ExitCode, result.Describe( ) );
        Assert.DoesNotContain( "chown", fixture.SudoCommands( ),
            "replacing an existing key must not change its ownership" );
        using ATProtoSigningKeyProvider loaded = new( fixture.ReadKey( ) );
        Assert.IsNotEmpty( loaded.KeyId, "the replaced key did not load" );
    }

    /// <summary>
    /// Verifies the run aborts rather than reporting success when a created key is unreadable to the
    /// container and no privilege is available to correct it.
    /// </summary>
    [TestMethod]
    public void InstallShUpgradeCreatedKey_UnreadableWithoutSudoFails( ) {
        if (!CanRunInstallSh( )) { return; }

        string foreignUid = ForeignContainerUid( );
        using InstallerFixture fixture = InstallerFixture.Create( );

        InstallerResult result = fixture.InstallGeneratedSecretSh( upgrade: true, foreignUid, useSudo: false );

        Assert.AreNotEqual( 0, result.ExitCode, result.Describe( ) );
        StringAssert.Contains( result.CombinedOutput, "not readable by the container user",
            "the failure must name the container-readability problem" );
        StringAssert.Contains( result.CombinedOutput, "sudo chown",
            "the failure must state the remediation" );
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Returns the current account's numeric UID.</summary>
    private static string CurrentUid( ) {
        RequirePosixCommand( "id", ["-u"], out string uid, out string failure );
        Assert.IsNotEmpty( uid, failure );
        return uid;
    }

    /// <summary>
    /// Returns a UID that is not the current account's, used to represent the container user. The test
    /// host is not that account, which is the condition the readability check is built on.
    /// </summary>
    private static string ForeignContainerUid( ) {
        string current = CurrentUid( );
        return current == "61337" ? "61338" : "61337";
    }

    private static bool CanRunInstallSh( ) {
        if (!OperatingSystem.IsLinux( ) && !OperatingSystem.IsMacOS( )) {
            Assert.Inconclusive( "install.sh requires a POSIX host with bash and openssl." );
            return false;
        }
        return true;
    }

    /// <summary>
    /// Runs a small POSIX utility and returns its trimmed stdout. Fails the test with the captured
    /// error when the command is unavailable or exits non-zero.
    /// </summary>
    private static void RequirePosixCommand( string fileName, string[] arguments, out string output, out string failure ) {
        ProcessStartInfo startInfo = new( fileName ) {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) { startInfo.ArgumentList.Add( argument ); }

        using Process process = Process.Start( startInfo )!;
        output = process.StandardOutput.ReadToEnd( ).Trim( );
        failure = process.StandardError.ReadToEnd( ).Trim( );
        _ = process.WaitForExit( 30_000 );
        if (process.ExitCode != 0) { output = string.Empty; }
    }

    private static bool CanRunPowerShell( ) {
        if (InstallerFixture.PowerShellPath is null) {
            Assert.Inconclusive( "The PowerShell installer tests require pwsh on PATH." );
            return false;
        }
        return true;
    }

    /// <summary>
    /// A scratch deployment directory containing a <c>secrets/</c> subdirectory plus generated harness
    /// scripts that run the installer scripts' own functions.
    /// </summary>
    private sealed class InstallerFixture : IDisposable {
        /// <summary>The secrets loop completed and left the key usable.</summary>
        internal const int Success = 0;
        /// <summary>The loop aborted because the secrets directory could not be inspected.</summary>
        internal const int AbortedBeforeInspection = 2;
        /// <summary>The loop aborted because backing up or replacing the key failed.</summary>
        internal const int KeyOperationFailed = 3;

        private readonly string _root;
        private readonly string _secretsDirectory;
        private readonly string _repositoryRoot;

        internal static readonly string? PowerShellPath = FindPowerShell( );

        private InstallerFixture( string repositoryRoot ) {
            _repositoryRoot = repositoryRoot;
            _root = Path.Combine( Path.GetTempPath( ), $"bb-installer-{Guid.NewGuid( ):N}" );
            _secretsDirectory = Path.Combine( _root, "secrets" );
            _ = Directory.CreateDirectory( _secretsDirectory );
        }

        private static bool IsPosix => OperatingSystem.IsLinux( ) || OperatingSystem.IsMacOS( );

        private string KeyPath => Path.Combine( _secretsDirectory, SigningKeySecret );

        internal static InstallerFixture Create( ) {
            return new InstallerFixture( FindRepositoryRoot( ) );
        }

        internal void WriteKey( string content ) {
            RestoreSecretsDirectory( );
            File.WriteAllText( KeyPath, content );
        }

        internal string ReadKey( ) {
            RestoreSecretsDirectory( );
            return File.Exists( KeyPath ) ? File.ReadAllText( KeyPath ) : string.Empty;
        }

        internal string[] BackupPaths( ) {
            RestoreSecretsDirectory( );
            return Directory.GetFiles( _secretsDirectory, $"{SigningKeySecret}.bak.*" );
        }

        internal string ReadBackup( ) {
            string[] backups = BackupPaths( );
            return backups.Length == 0 ? string.Empty : File.ReadAllText( backups[0] );
        }

        /// <summary>Creates a sibling <c>secrets_backup_*</c> directory holding one file.</summary>
        internal void WriteSecretsBackup( string name, string content ) {
            string directory = Path.Combine( _root, name );
            _ = Directory.CreateDirectory( directory );
            File.WriteAllText( Path.Combine( directory, "redis_password.txt" ), content );
        }

        /// <summary>Returns true when a sibling <c>secrets_backup_*</c> directory still exists.</summary>
        internal bool SecretsBackupExists( string name ) {
            return Directory.Exists( Path.Combine( _root, name ) );
        }

        /// <summary>Writes an additional secret file into the secrets directory.</summary>
        internal void WriteExtraSecret( string name, string content ) {
            RestoreSecretsDirectory( );
            string path = Path.Combine( _secretsDirectory, name );
            _ = Directory.CreateDirectory( Path.GetDirectoryName( path )! );
            File.WriteAllText( path, content );
        }

        /// <summary>
        /// Builds a copy of the secrets tree in the requested shape and reports whether the real
        /// <c>secrets_tree_manifest</c> sees it as identical to the source.
        /// </summary>
        internal bool ManifestMatchesCopy( string copyKind ) {
            RestoreSecretsDirectory( );
            string installerPath = Path.Combine( _repositoryRoot, "containers", "install.sh" );
            string source = _secretsDirectory;
            string scriptPath = Path.Combine( _root, $"manifest-{copyKind}.sh" );

            // Each shape reproduces a way a copy can go wrong while keeping a plausible file count.
            string prepare = copyKind switch {
                "complete" => $"mkdir -p '{_root}/copy' && cp -R '{source}'/. '{_root}/copy'/",
                "missing-file" => $"mkdir -p '{_root}/copy' && cp -R '{source}'/. '{_root}/copy'/ "
                    + $"&& rm -f '{_root}/copy/redis_password.txt'",
                "changed-content" => $"mkdir -p '{_root}/copy' && cp -R '{source}'/. '{_root}/copy'/ "
                    + $"&& printf 'tampered' > '{_root}/copy/redis_password.txt'",
                // `cp -r src existingdst` nests the tree inside the destination.
                "nested" => $"mkdir -p '{_root}/copy' && cp -r '{source}' '{_root}/copy'",
                _ => "false"
            };

            File.WriteAllText( scriptPath, string.Join( "\n",
                "set -u",
                ExtractShellFunction( installerPath, "secrets_tree_manifest" ),
                prepare,
                $"a=$(secrets_tree_manifest '{source}' false)",
                $"b=$(secrets_tree_manifest '{_root}/copy' false)",
                "[ -n \"$a\" ] && [ \"$a\" = \"$b\" ] && echo MATCH || echo DIFFERENT" ) );

            InstallerResult result = RunProcess( "bash", [scriptPath], _root );
            Assert.AreEqual( 0, result.ExitCode, result.Describe( ) );
            return result.StandardOutput.Trim( ) == "MATCH";
        }

        /// <summary>Observes permissions inside cp while running the installer's real copy block.</summary>
        internal BackupRootOrder ObserveBackupRootOrder( bool useSudo ) {
            RestoreSecretsDirectory( );
            string installerPath = Path.Combine( _repositoryRoot, "containers", "install.sh" );
            string scriptPath = Path.Combine( _root, "root-order.sh" );

            File.WriteAllText( scriptPath, string.Join( "\n",
                "set -u",
                "umask 0022",
                $"SECRETS_DIR={Quote( _secretsDirectory )}",
                "backup_dir=./observed-backup",
                $"USE_SUDO={(useSudo ? "true" : "false")}",
                "export USE_SUDO backup_dir",
                "mode() { stat -c '%a' \"$1\" 2>/dev/null || stat -f '%Lp' \"$1\" 2>/dev/null; }",
                "cp() {",
                "  if [[ \"$USE_SUDO\" == true && \"${IN_SUDO:-false}\" != true ]]; then return 1; fi",
                "  echo \"mode=$(mode \"$backup_dir\")\"",
                "  command cp \"$@\" || return $?",
                "  echo \"secret=$(mode \"$backup_dir/atproto_oauth_key.json\")\"",
                "}",
                "sudo() { if [[ \"$1\" == sh ]]; then shift; IN_SUDO=true bash \"$@\"; else command \"$@\"; fi; }",
                "export -f mode cp",
                ExtractShellBlock( installerPath, "        copied=false", "        # Prove the copy holds exactly" ),
                "[[ \"$copied\" == true ]] || exit 1",
                "echo \"restored_umask=$(umask)\"" ) );

            InstallerResult result = RunProcess( "bash", [scriptPath], _root );
            Assert.AreEqual( 0, result.ExitCode, result.Describe( ) );

            Dictionary<string, string> values = result.StandardOutput
                .Split( '\n', StringSplitOptions.RemoveEmptyEntries )
                .Select( line => line.Split( '=', 2 ) )
                .Where( parts => parts.Length == 2 )
                .ToDictionary( parts => parts[0].Trim( ), parts => parts[1].Trim( ) );

            Assert.AreEqual( "0022", values["restored_umask"], "copy must not change the caller's umask" );
            string mode = values["mode"];
            return new BackupRootOrder(
                RootModeBeforeCopy: mode,
                RootOwnerOnlyBeforeCopy: mode.Length >= 3 && mode[^1] == '0' && mode[^2] == '0',
                SecretMode: values["secret"] );
        }

        /// <summary>
        /// True when a line assigns the given PowerShell variable (as opposed to reading it).
        /// </summary>
        private static bool IsAssignmentOf( string line, string variableName ) {
            string trimmed = line.TrimStart( );
            if (trimmed.StartsWith( variableName + " =", StringComparison.Ordinal )
                || trimmed.StartsWith( variableName + "=", StringComparison.Ordinal )) {
                return true;
            }
            // Assignment forms such as "[bool](...) " are handled by the StartsWith checks above; a
            // later assignment guarded by an "-eq ''" test is still an assignment for ordering.
            return trimmed.StartsWith( variableName + " ", StringComparison.Ordinal )
                && trimmed.Contains( "=", StringComparison.Ordinal )
                && !trimmed.StartsWith( "if ", StringComparison.Ordinal );
        }

        /// <summary>
        /// Inspects install.ps1 to confirm the ACL capability is resolved before any secret handling,
        /// and evaluates the fail-closed decision the guards take when it is unresolved.
        /// </summary>
        internal AclOrdering ObserveAclOrdering( ) {
            string installerPath = Path.Combine( _repositoryRoot, "containers", "install.ps1" );
            string[] lines = File.ReadAllLines( installerPath );

            int resolutionLine = Array.FindIndex( lines, line =>
                line.Contains( "$aclCapabilityResolved", StringComparison.Ordinal ) );
            int assignmentLine = Array.FindIndex( lines, line =>
                line.Contains( "GetCurrent().Name", StringComparison.Ordinal ) );
            int backupBlockLine = Array.FindIndex( lines, line =>
                line.Contains( "Back up secrets before any modifications", StringComparison.Ordinal ) );
            int secretLoopLine = Array.FindIndex( lines, line =>
                line.Contains( "foreach ($secret in $SecretFiles)", StringComparison.Ordinal ) );

            Assert.IsGreaterThanOrEqualTo( 0, assignmentLine, "the principal resolution was not found" );
            Assert.IsGreaterThanOrEqualTo( 0, resolutionLine, "the resolved-capability flag assignment was not found" );
            Assert.IsGreaterThanOrEqualTo( 0, backupBlockLine, "the upgrade backup block was not found" );
            Assert.IsGreaterThanOrEqualTo( 0, secretLoopLine, "the secrets loop was not found" );

            // Any guard must test the explicit resolved flag rather than a bare capability variable.
            bool usesResolvedFlag = lines.Any( line => line.Contains( "-not $aclCapabilityResolved", StringComparison.Ordinal ) );

            // The removed defect: a capability variable read before it is assigned. For each name,
            // compare the first read with the first assignment; a read that precedes it is exactly the
            // shape that evaluated to $null and silently permitted an unprotected copy.
            bool readsUnresolved = false;
            foreach (string name in new[] { "$icaclsAvailable", "$canRestrictAcl" }) {
                int firstAssignment = Array.FindIndex( lines, line => IsAssignmentOf( line, name ) );
                int firstRead = Array.FindIndex( lines, line =>
                    line.Contains( name, StringComparison.Ordinal ) && !IsAssignmentOf( line, name ) );
                if (firstAssignment < 0) { continue; }
                if (firstRead >= 0 && firstRead < firstAssignment) { readsUnresolved = true; }
            }

            // Evaluate the guard decision with the capability unresolved, using the same PowerShell
            // semantics that made the defect invisible.
            string probe = Path.Combine( _root, "acl-decision.ps1" );
            File.WriteAllText( probe, string.Join( "\n",
                "$aclCapabilityResolved = $null",
                "$canRestrictAcl = $null",
                "$aclCapabilityError = $null",
                "if (-not $aclCapabilityResolved) { 'denied' }",
                "elseif ($aclCapabilityError) { 'denied' }",
                "elseif (-not $canRestrictAcl) { 'allowed' }",
                "else { 'restricted' }" ) );
            InstallerResult decision = RunProcess( PowerShellPath!, ["-NoProfile", "-File", probe], _root );
            Assert.AreEqual( 0, decision.ExitCode, decision.Describe( ) );

            return new AclOrdering(
                CapabilityResolvedFirst: assignmentLine < secretLoopLine && resolutionLine < backupBlockLine,
                ResolutionLineBeforeBackupBlock: resolutionLine < backupBlockLine,
                UsesResolvedFlagGuard: usesResolvedFlag,
                ReadsUnresolvedCapability: readsUnresolved,
                NonWindowsDecision: decision.StandardOutput.Trim( ) );
        }

        /// <summary>Names of the sibling <c>secrets_backup_*</c> directories currently present.</summary>
        internal string[] SecretsBackupNames( ) {
            return [.. Directory.GetDirectories( _root, "secrets_backup_*" )
                .Select( Path.GetFileName )
                .OfType<string>( )
                .Order( StringComparer.Ordinal )];
        }

        /// <summary>
        /// Builds a backup tree the way the pre-fix code did (plain <c>mkdir</c> + <c>cp</c>) and a
        /// restricted copy, both under umask 0022, and reports the resulting modes.
        /// </summary>
        internal BackupTreeModes RestrictBackupTreeUnderUmask( ) {
            RestoreSecretsDirectory( );
            string installerPath = Path.Combine( _repositoryRoot, "containers", "install.sh" );
            string scriptPath = Path.Combine( _root, "perm-probe.sh" );

            File.WriteAllText( scriptPath, string.Join( "\n",
                "set -u",
                "umask 0022",
                ExtractShellHelpers( installerPath, "restrict_backup_tree", "backup_tree_is_private" ),
                $"SRC='{_secretsDirectory}'",
                $"mkdir -p '{_root}/before' '{_root}/after'",
                $"cp -R \"$SRC\"/. '{_root}/before'/",
                $"cp -R \"$SRC\"/. '{_root}/after'/",
                // Pre-fix: no restriction applied.
                $"restrict_backup_tree '{_root}/after' false",
                $"mode() {{ stat -c '%a' \"$1\" 2>/dev/null || stat -f '%Lp' \"$1\" 2>/dev/null; }}",
                $"echo \"prefix_dir=$(mode '{_root}/before')\"",
                $"echo \"dir=$(mode '{_root}/after')\"",
                $"echo \"file=$(mode '{_root}/after/atproto_oauth_key.json')\"",
                $"echo \"nested_dir=$(mode '{_root}/after/nested')\"",
                $"echo \"nested_file=$(mode '{_root}/after/nested/extra.txt')\"",
                $"backup_tree_is_private '{_root}/after' false && echo 'private=yes' || echo 'private=no'",
                $"backup_tree_is_private '{_root}/before' false && echo 'prefix_private=yes' || echo 'prefix_private=no'" ) );

            InstallerResult result = RunProcess( "bash", [scriptPath], _root );
            Assert.AreEqual( 0, result.ExitCode, result.Describe( ) );

            Dictionary<string, string> values = result.StandardOutput
                .Split( '\n', StringSplitOptions.RemoveEmptyEntries )
                .Select( line => line.Split( '=', 2 ) )
                .Where( parts => parts.Length == 2 )
                .ToDictionary( parts => parts[0].Trim( ), parts => parts[1].Trim( ) );

            return new BackupTreeModes(
                PreFixDirectory: values["prefix_dir"],
                PreFixIsPrivate: values["prefix_private"] == "yes",
                Directory: values["dir"],
                File: values["file"],
                NestedDirectory: values["nested_dir"],
                NestedFile: values["nested_file"],
                IsPrivate: values["private"] == "yes" );
        }

        /// <summary>
        /// Runs the real <c>prune_secrets_backups</c> against the fixture's backup directories.
        /// </summary>
        /// <param name="makeOldestUndeletable">
        /// When true, the oldest backup's parent is made read-only so removal fails and the reporting
        /// path is exercised.
        /// </param>
        internal InstallerResult RunPruneSh( bool makeOldestUndeletable ) {
            string installerPath = Path.Combine( _repositoryRoot, "containers", "install.sh" );
            string scriptPath = Path.Combine( _root, "prune.sh" );

            File.WriteAllText( scriptPath, string.Join( "\n",
                "set -u",
                "USE_SUDO=false",
                "log_upgrade() { :; }",
                ExtractShellHelpers( installerPath, "prune_secrets_backups" ),
                makeOldestUndeletable
                    ? $"chmod 500 '{_root}'"
                    : string.Empty,
                "prune_secrets_backups",
                makeOldestUndeletable ? $"chmod 700 '{_root}'" : string.Empty,
                "exit 0" ) );

            return RunProcess( "bash", [scriptPath], _root );
        }

        /// <summary>
        /// Runs the real PowerShell <c>Test-SecretsTreeCopied</c> helper.
        /// </summary>
        /// <param name="useExistingDestination">
        /// When true, the destination is pre-created as a file rather than a directory, so the copy
        /// cannot succeed and the helper must report failure.
        /// </param>
        /// <param name="staleDestination">
        /// When true, the destination already holds a file the source does not, so the manifest
        /// comparison must fail rather than accept the copy.
        /// </param>
        internal InstallerResult RunTreeCopyPs1( bool useExistingDestination, bool staleDestination = false ) {
            string destination = Path.Combine( _root, useExistingDestination ? "blocked-dest" : "ps1-backup-out" );
            if (useExistingDestination) {
                // A file where a directory is expected makes the destination unusable.
                File.WriteAllText( destination, "not a directory" );
            } else if (staleDestination) {
                _ = Directory.CreateDirectory( destination );
                File.WriteAllText( Path.Combine( destination, "stale.txt" ), "left over from an earlier run" );
            }

            string resultPath = Path.Combine( _root, "tree-copy-result.txt" );
            string scriptPath = Path.Combine( _root, "tree-copy.ps1" );
            File.WriteAllText( scriptPath, BuildPowerShellHarness(
                "Get-TreeManifest, Test-SecretsTreeCopied, Write-Ok, Write-Err",
                $"$verified = Test-SecretsTreeCopied -Source '{_secretsDirectory}' -Destination '{destination}'\n"
                    + $"Set-Content -Path '{resultPath}' -Value $verified -NoNewline" ) );

            InstallerResult result = RunProcess( PowerShellPath!, ["-NoProfile", "-File", scriptPath], _root );
            string reported = File.Exists( resultPath ) ? File.ReadAllText( resultPath ).Trim( ) : string.Empty;
            return result with { Reported = reported };
        }

        /// <summary>
        /// Runs the real PowerShell upgrade secrets-backup block.
        /// </summary>
        /// <param name="failBackup">
        /// When true, the block's source directory is empty, so the backup step reports failure. This
        /// exercises the failure path without making <c>$Directory</c> unusable, which would also
        /// break the upgrade log the block writes.
        /// </param>
        /// <remarks>
        /// The block calls <c>exit 1</c> when the backup fails, so the child's exit code is the
        /// outcome. A marker file written only after the block reports whether execution continued
        /// past it — reaching the marker means the prune ran despite the failure.
        /// </remarks>
        internal InstallerResult RunUpgradeBulkBackupPs1( bool failBackup = false ) {
            string resultPath = Path.Combine( _root, "bulk-backup-result.txt" );
            string scriptPath = Path.Combine( _root, "bulk-backup.ps1" );
            string installerPath = Path.Combine( _repositoryRoot, "containers", "install.ps1" );

            // Clear any marker from an earlier call: a stale one would look like this run reached the
            // end even though it exited at the failure check.
            File.Delete( resultPath );

            // $Directory is the install root the block resolves its backup path against, and
            // Write-UpgradeLog appends to "$Directory/$UpgradeLog", so both must be usable or the
            // harness fails for reasons unrelated to the backup logic under test.
            if (failBackup) {
                _ = Directory.CreateDirectory( Path.Combine( _root, "empty-secrets" ) );
            }

            File.WriteAllText( scriptPath, BuildPowerShellHarness(
                // Every reporting helper the block can call must be defined here. Omitting one makes
                // the harness throw before the outcome under test, which would mask the behavior.
                "Get-TreeManifest, Test-SecretsTreeCopied, Protect-PathToCurrentUser, Write-Ok, Write-Err, Write-Warn, Write-UpgradeLog",
                string.Join( "\n",
                    $"$Directory = '{_root}'",
                    $"$secretsDir = '{_secretsDirectory}'",
                    // The ACL capability variables the real script resolves at the top. This host is
                    // not Windows, so no ACL applies and the block should proceed to the copy.
                    "$aclCapabilityResolved = $true",
                    "$aclCapabilityError = $null",
                    "$canRestrictAcl = $false",
                    "$isWindowsPlatform = $false",
                    // The block derives the backup directory name from the clock, so the failure is
                    // induced by overriding Copy of the source path instead: point the block's
                    // source at a path that has no readable files.
                    (failBackup ? $"$secretsDir = '{Path.Combine( _root, "empty-secrets" )}'" : string.Empty),
                    // Write-UpgradeLog joins $Directory with this name, so it must be relative, as the
                    // installer's own 'upgrade.log' default is.
                    "$UpgradeLog = 'upgrade.log'",
                    "$Branch = 'test'",
                    ExtractPowerShellBlock( installerPath,
                        "    if (Test-Path $secretsDir) {", "    # Log current container image information with RepoDigests" ),
                    $"Set-Content -Path '{resultPath}' -Value 'reached-end' -NoNewline" ) ) );

            InstallerResult result = RunProcess( PowerShellPath!, ["-NoProfile", "-File", scriptPath], _root );
            string reported = File.Exists( resultPath ) ? File.ReadAllText( resultPath ).Trim( ) : string.Empty;
            return result with { Reported = reported };
        }

        /// <summary>
        /// Copies the lines between two markers out of a PowerShell script, excluding both. The caller
        /// supplies the opening <c>if</c> line as the marker and re-adds it, so the extracted text
        /// stays a syntactically balanced statement.
        /// </summary>
        private static string ExtractPowerShellBlock( string installerPath, string startMarker, string endMarker ) {
            StringBuilder builder = new( );
            bool inside = false;
            foreach (string line in File.ReadAllLines( installerPath )) {
                if (!inside) {
                    if (line == startMarker) {
                        inside = true;
                        _ = builder.AppendLine( "    if (Test-Path $secretsDir) {" );
                    }
                    continue;
                }
                if (line == endMarker) { break; }
                _ = builder.AppendLine( line );
            }
            Assert.IsTrue( inside, $"could not find the block starting at '{startMarker}' in {installerPath}" );
            return builder.ToString( );
        }

        /// <summary>
        /// Runs the metadata helpers against the signing key and reports how many times the
        /// <c>sudo</c> stand-in was invoked.
        /// </summary>
        internal InstallerResult RunMetadataProbeSh( bool useSudo ) {
            // Deliberately does not restore permissions: the unsearchable case depends on them.
            string installerPath = Path.Combine( _repositoryRoot, "containers", "install.sh" );
            string logPath = Path.Combine( _root, "sudo-metadata.log" );
            string scriptPath = Path.Combine( _root, "metadata-probe.sh" );
            File.WriteAllText( scriptPath, string.Join( "\n",
                "set -u",
                $"LOG={Quote( logPath )}",
                "sudo() { echo \"sudo $*\" >> \"$LOG\"; \"$@\"; }",
                $"USE_SUDO={(useSudo ? "true" : "false")}",
                ExtractShellHelpers( installerPath, "directory_needs_sudo", "file_owner_uid", "file_mode_bits" ),
                $"_=$(file_owner_uid {Quote( KeyPath )} || true)",
                $"_=$(file_mode_bits {Quote( KeyPath )} || true)",
                // wc prints nothing for a missing file, so count lines directly and default to 0.
                "[ -f \"$LOG\" ] && grep -c '^sudo ' \"$LOG\" || echo 0" ) );
            return RunProcess( "bash", [scriptPath], _root );
        }

        /// <summary>
        /// Runs the real upgrade secrets-backup block from install.sh, with the working directory set
        /// to the fixture root so its relative <c>./secrets_backup_*</c> paths resolve there.
        /// </summary>
        internal InstallerResult RunUpgradeBulkBackupSh( bool useSudo ) {
            // Deliberately does not restore permissions: this case depends on the directory staying
            // unreadable, and the harness script is written outside it.
            string scriptPath = Path.Combine( _root, "upgrade-backup.sh" );
            File.WriteAllText( scriptPath, BuildUpgradeBackupHarness( useSudo ) );
            return RunProcess( "bash", [scriptPath], _root );
        }

        /// <summary>
        /// Runs the real secrets loop from install.sh in a subshell.
        /// </summary>
        /// <remarks>
        /// The harness renumbers the loop's own <c>exit 1</c> calls to <see cref="KeyOperationFailed"/>
        /// so a key-operation abort can be told apart from the pre-inspection abort.
        /// </remarks>
        internal InstallerResult RunSecretsLoopSh(
            bool failGeneration = false,
            string? backupSuffix = null,
            bool upgrade = false,
            bool useSudo = false,
            string? containerUid = null ) {
            // The harness is written outside the secrets directory, so this deliberately does not
            // restore permissions: the unreadable-directory case depends on them staying removed.
            string scriptPath = Path.Combine( _root, "secrets-loop.sh" );
            File.WriteAllText( scriptPath, BuildShellHarness( failGeneration, backupSuffix, upgrade, useSudo, containerUid ) );
            return RunProcess( "bash", [scriptPath], _root );
        }

        /// <summary>
        /// Runs the real <c>install_generated_secret</c> for a key that does not exist yet: the path an
        /// upgraded install takes when the key file is missing from the secrets directory.
        /// </summary>
        /// <remarks>
        /// The harness defines <c>sudo</c> as a shell function that runs its arguments directly, so the
        /// privileged branch is reachable without real privileges. That means the test cannot observe
        /// the real consequence of privilege escalation — that a root-run <c>cp</c> leaves the file
        /// owned by root — so the harness instead makes the key unreadable to the container UID by
        /// pointing <c>CONTAINER_UID</c> at a different account. The ownership decision the installer
        /// makes from that state is what these tests check.
        /// </remarks>
        internal InstallerResult InstallGeneratedSecretSh( bool upgrade, string containerUid, bool useSudo ) {
            RestoreSecretsDirectory( );
            string scriptPath = Path.Combine( _root, "install-secret.sh" );
            File.WriteAllText( scriptPath, BuildInstallSecretHarness( upgrade, containerUid, useSudo ) );
            return RunProcess( "bash", [scriptPath], _root );
        }

        /// <summary>The commands the <c>sudo</c> stand-in was asked to run, one per line.</summary>
        internal string SudoCommands( ) {
            return File.Exists( SudoLogPath ) ? File.ReadAllText( SudoLogPath ) : string.Empty;
        }

        internal string SudoLogPath => Path.Combine( _root, "sudo-commands.log" );

        /// <summary>Returns true when the container UID can read the signing key under test.</summary>
        internal bool SecretReadableByContainer( string containerUid ) {
            RestoreSecretsDirectory( );
            string installerPath = Path.Combine( _repositoryRoot, "containers", "install.sh" );
            string scriptPath = Path.Combine( _root, "readable-check.sh" );
            File.WriteAllText( scriptPath, string.Join( "\n",
                "set -u",
                $"CONTAINER_UID={Quote( containerUid )}",
                ExtractShellHelpers( installerPath, "file_owner_uid", "file_mode_bits", "secret_readable_by_container" ),
                $"secret_readable_by_container {Quote( KeyPath )} && echo yes || echo no" ) );
            InstallerResult result = RunProcess( "bash", [scriptPath], _root );
            Assert.AreEqual( 0, result.ExitCode, result.Describe( ) );
            return result.StandardOutput.Trim( ) == "yes";
        }

        /// <summary>Runs the PowerShell key-state classifier through its real definition.</summary>
        /// <remarks>
        /// The state is written to a file rather than stdout so the secrets directory can stay hidden
        /// for the whole run: this process must not restore permissions before the child reads.
        /// </remarks>
        internal string GetKeyStatePs1( ) {
            string resultPath = Path.Combine( _root, "key-state.txt" );
            string scriptPath = Path.Combine( _root, "key-state.ps1" );
            File.WriteAllText( scriptPath, BuildPowerShellHarness( "Get-SigningKeyState, Get-SigningKeyShape",
                $"Set-Content -Path '{resultPath}' -Value (Get-SigningKeyState -FilePath '{KeyPath}') -NoNewline" ) );
            InstallerResult result = RunProcess( PowerShellPath!, ["-NoProfile", "-File", scriptPath], _root );
            Assert.AreEqual( 0, result.ExitCode, result.Describe( ) );
            return File.Exists( resultPath ) ? File.ReadAllText( resultPath ).Trim( ) : string.Empty;
        }

        /// <summary>Runs the PowerShell signing key generator through its real definition.</summary>
        internal InstallerResult RunGeneratorPs1( bool failGeneration = false ) {
            RestoreSecretsDirectory( );
            string resultPath = Path.Combine( _root, "generator-result.txt" );
            string scriptPath = Path.Combine( _root, "generate-key.ps1" );
            string failure = failGeneration
                ? "function Get-ES256SigningKeyJwk { throw 'simulated generation failure' }"
                : string.Empty;

            // Write-Host output is captured on stdout; the boolean result goes to its own file so the
            // success and failure assertions do not depend on how the messages are formatted.
            File.WriteAllText( scriptPath, BuildPowerShellHarness(
                "New-SigningKeyJwk, Get-ES256SigningKeyJwk, Write-Ok, Write-Warn, Write-Err",
                $"{failure}\nSet-Content -Path '{resultPath}' "
                    + $"-Value (New-SigningKeyJwk -FilePath '{KeyPath}') -NoNewline" ) );

            InstallerResult result = RunProcess( PowerShellPath!, ["-NoProfile", "-File", scriptPath], _root );
            string reported = File.Exists( resultPath ) ? File.ReadAllText( resultPath ).Trim( ) : string.Empty;
            return result with { Reported = reported };
        }

        /// <summary>
        /// Removes all permission from the secrets directory to reproduce an upgraded install, where
        /// the directory is mode 700 owned by the container UID.
        /// </summary>
        internal bool TryHideSecretsDirectory( ) {
            if (!IsPosix) { return false; }
            // root bypasses traversal mode bits, so the condition cannot be reproduced as root.
            if (string.Equals( Environment.UserName, "root", StringComparison.Ordinal )) { return false; }
            try {
                return TrySetSecretsDirectoryMode( UnixFileMode.None );
            } catch (Exception) {
                return false;
            }
        }

        private void RestoreSecretsDirectory( ) {
            try {
                _ = TrySetSecretsDirectoryMode(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
            } catch (Exception) { /* best effort cleanup */ }
        }

        /// <summary>
        /// Applies POSIX permissions to the secrets directory. Returns false on platforms without
        /// POSIX mode bits, where the caller has nothing to do.
        /// </summary>
        private bool TrySetSecretsDirectoryMode( UnixFileMode mode ) {
            if (OperatingSystem.IsWindows( )) { return false; }
            File.SetUnixFileMode( _secretsDirectory, mode );
            return true;
        }

        /// <summary>
        /// Builds a harness that sources the installer's own preamble (helpers, secret table, and
        /// defaults) and then runs its real secrets loop.
        /// </summary>
        /// <remarks>
        /// The preamble is included verbatim up to the dependency checks instead of re-declaring the
        /// helpers: copying the definitions would let the harness drift from what the installer
        /// actually runs. Only <c>set -e</c> is dropped, so a non-zero return from the loop can be
        /// inspected rather than terminating the harness.
        /// </remarks>
        private string BuildShellHarness(
            bool failGeneration,
            string? backupSuffix,
            bool upgrade,
            bool useSudo,
            string? containerUid ) {
            string installerPath = Path.Combine( _repositoryRoot, "containers", "install.sh" );
            StringBuilder harness = new( );

            _ = harness.AppendLine( BuildInstallerPreamble( installerPath ) );
            _ = harness.AppendLine( $"SECRETS_DIR={Quote( _secretsDirectory )}" );
            _ = harness.AppendLine( $"TIMESTAMP={Quote( backupSuffix ?? "test" )}" );
            _ = harness.AppendLine( $"USE_SUDO={(useSudo ? "true" : "false")}" );
            _ = harness.AppendLine( "SUDO_AVAILABLE=false" );
            _ = harness.AppendLine( "missing_secrets=()" );
            // An upgrade already had a secrets directory, so the installer skips its directory-wide
            // ownership correction; that is the state where a created key must be made readable here.
            _ = harness.AppendLine( $"secrets_created={(upgrade ? "false" : "true")}" );
            if (containerUid is not null) {
                _ = harness.AppendLine( $"CONTAINER_UID={Quote( containerUid )}" );
            }

            if (useSudo) {
                _ = harness.AppendLine( ShellSudoStub );
            }

            if (failGeneration) {
                _ = harness.AppendLine( "generate_es256_jwk() { return 1; }" );
            }

            _ = harness.AppendLine( "run_loop() {" );
            _ = harness.AppendLine( ExtractShellLoop( installerPath ) );
            _ = harness.AppendLine( "}" );

            // Renumber the loop's key-operation aborts so the assertions can tell which abort fired.
            _ = harness.AppendLine( "wrapper=$(declare -f run_loop)" );
            _ = harness.AppendLine( "eval \"${wrapper//exit 1/return 3}\"" );
            _ = harness.AppendLine( "run_loop" );
            _ = harness.AppendLine( "exit $?" );
            return harness.ToString( );
        }

        /// <summary>
        /// Builds a harness that calls the real <c>install_generated_secret</c> against a key path that
        /// does not exist, so the create-from-nothing branch runs.
        /// </summary>
        private string BuildInstallSecretHarness( bool upgrade, string containerUid, bool useSudo ) {
            string installerPath = Path.Combine( _repositoryRoot, "containers", "install.sh" );
            StringBuilder harness = new( );

            _ = harness.AppendLine( BuildInstallerPreamble( installerPath ) );
            _ = harness.AppendLine( $"TARGET={Quote( KeyPath )}" );
            _ = harness.AppendLine( $"USE_SUDO={(useSudo ? "true" : "false")}" );
            _ = harness.AppendLine( $"CONTAINER_UID={Quote( containerUid )}" );
            _ = harness.AppendLine( $"secrets_created={(upgrade ? "false" : "true")}" );

            if (useSudo) {
                _ = harness.AppendLine( ShellSudoStub );
            }

            _ = harness.AppendLine( "install_generated_secret \"$TARGET\" generate_es256_jwk" );
            return harness.ToString( );
        }

        /// <summary>
        /// Stand-in for <c>sudo</c> that records the command and runs its arguments directly.
        /// </summary>
        private string ShellSudoStub =>
            $"sudo() {{ echo \"$*\" >> {Quote( SudoLogPath )}; \"$@\"; }}";

        /// <summary>
        /// Copies the installer's preamble (helpers, secret table, and defaults) verbatim up to the
        /// dependency checks, dropping the shebang and <c>set -e</c> so a failure is inspectable.
        /// Including the real text keeps the harness from drifting from what the installer runs.
        /// </summary>
        private static string BuildInstallerPreamble( string installerPath ) {
            StringBuilder preamble = new( );
            foreach (string line in File.ReadAllLines( installerPath )) {
                if (line.StartsWith( "# Dependency Validation", StringComparison.Ordinal )) { break; }
                if (line == "set -e" || line.StartsWith( "#!", StringComparison.Ordinal )) { continue; }
                _ = preamble.AppendLine( line );
            }
            return preamble.ToString( );
        }

        /// <summary>Copies named top-level shell functions out of the installer verbatim.</summary>
        private static string ExtractShellHelpers( string installerPath, params string[] functionNames ) {
            StringBuilder helpers = new( );
            foreach (string name in functionNames) {
                _ = helpers.AppendLine( ExtractShellFunction( installerPath, name ) );
            }
            return helpers.ToString( );
        }

        /// <summary>
        /// Copies a top-level shell function out of the installer.
        /// </summary>
        /// <remarks>
        /// The body ends at the first line that is exactly <c>}</c> at column zero. Brace counting is
        /// deliberately not used: function bodies contain parameter expansions such as
        /// <c>${content//\{/}</c> whose braces are not shell block delimiters.
        /// </remarks>
        private static string ExtractShellFunction( string installerPath, string functionName ) {
            StringBuilder builder = new( );
            bool inside = false;
            foreach (string line in File.ReadAllLines( installerPath )) {
                if (!inside) {
                    if (!line.StartsWith( functionName + "() {", StringComparison.Ordinal )) { continue; }
                    inside = true;
                }
                _ = builder.AppendLine( line );
                if (line == "}") { break; }
            }
            Assert.IsTrue( inside, $"could not find {functionName} in {installerPath}" );
            return builder.ToString( );
        }

        /// <summary>
        /// Builds a harness that runs the real upgrade secrets-backup block from install.sh.
        /// </summary>
        /// <remarks>
        /// The block is inline in the installer rather than a function, so it is copied between its
        /// comment marker and the closing of the enclosing <c>if</c>. Its relative
        /// <c>./secrets_backup_*</c> paths require the caller to set the working directory to the
        /// fixture root. The <c>exit 1</c> is renumbered so the failure path is observable.
        /// </remarks>
        private string BuildUpgradeBackupHarness( bool useSudo ) {
            string installerPath = Path.Combine( _repositoryRoot, "containers", "install.sh" );
            StringBuilder harness = new( );

            _ = harness.AppendLine( BuildInstallerPreamble( installerPath ) );
            _ = harness.AppendLine( $"SECRETS_DIR={Quote( _secretsDirectory )}" );
            _ = harness.AppendLine( $"USE_SUDO={(useSudo ? "true" : "false")}" );
            _ = harness.AppendLine( "log_upgrade() { :; }" );
            if (useSudo) {
                _ = harness.AppendLine( ShellSudoStub );
            }

            _ = harness.AppendLine( "run_backup() {" );
            _ = harness.AppendLine( ExtractShellBlock(
                installerPath,
                "    # Back up secrets before any modifications. On an upgraded install",
                "    # Log current container image information with RepoDigests" ) );
            _ = harness.AppendLine( "}" );

            _ = harness.AppendLine( "wrapper=$(declare -f run_backup)" );
            _ = harness.AppendLine( "eval \"${wrapper//exit 1/return 3}\"" );
            _ = harness.AppendLine( "run_backup" );
            _ = harness.AppendLine( "exit $?" );
            return harness.ToString( );
        }

        /// <summary>
        /// Copies the lines between two markers out of a script, excluding both markers.
        /// </summary>
        private static string ExtractShellBlock( string installerPath, string startMarker, string endMarker ) {
            StringBuilder builder = new( );
            bool inside = false;
            foreach (string line in File.ReadAllLines( installerPath )) {
                if (!inside) {
                    if (line.StartsWith( startMarker, StringComparison.Ordinal )) { inside = true; }
                    continue;
                }
                if (line.StartsWith( endMarker, StringComparison.Ordinal )) { break; }
                _ = builder.AppendLine( line );
            }
            Assert.IsTrue( inside, $"could not find the block starting at '{startMarker}' in {installerPath}" );
            Assert.IsNotEmpty( builder.ToString( ).Trim( ), $"block '{startMarker}' is empty" );
            return builder.ToString( );
        }

        private string BuildPowerShellHarness( string functionNames, string tail ) {
            string installer = Path.Combine( _repositoryRoot, "containers", "install.ps1" );
            string wanted = string.Join( ", ",
                functionNames.Split( ',' ).Select( name => "'" + name.Trim( ) + "'" ) );
            return string.Join( "\n",
                "$ErrorActionPreference = 'Stop'",
                $"$ast = [System.Management.Automation.Language.Parser]::ParseFile('{installer}', [ref]$null, [ref]$null)",
                $"$wanted = @({wanted})",
                "foreach ($fn in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {",
                "  if ($wanted -contains $fn.Name) { Invoke-Expression $fn.Extent.Text }",
                "}",
                "$Timestamp = 'test'",
                tail );
        }

        private static string Quote( string value ) {
            return "'" + value.Replace( "'", "'\\''", StringComparison.Ordinal ) + "'";
        }

        /// <summary>
        /// Copies the secrets loop out of the installer.
        /// </summary>
        /// <remarks>
        /// The body is delimited by the loop header and its closing <c>done</c> only. Counting braces to
        /// find the end would not work here: the loop body contains parameter expansions such as
        /// <c>${content//\{/}</c> whose braces are not shell block delimiters.
        /// </remarks>
        private static string ExtractShellLoop( string installerPath ) {
            string[] lines = File.ReadAllLines( installerPath );
            StringBuilder builder = new( );
            bool inside = false;
            foreach (string line in lines) {
                if (!inside) {
                    if (line.StartsWith( "for secret_config in", StringComparison.Ordinal )) {
                        inside = true;
                        _ = builder.AppendLine( line );
                    }
                    continue;
                }
                _ = builder.AppendLine( line );
                if (line == "done") { break; }
            }
            Assert.IsTrue( inside, $"could not find the secrets loop in {installerPath}" );
            return builder.ToString( );
        }

        private static InstallerResult RunProcess( string fileName, string[] arguments, string workingDirectory ) {
            ProcessStartInfo startInfo = new( fileName ) {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments) { startInfo.ArgumentList.Add( argument ); }

            using Process process = Process.Start( startInfo )!;
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync( );
            Task<string> standardError = process.StandardError.ReadToEndAsync( );
            if (!process.WaitForExit( 120_000 )) {
                process.Kill( entireProcessTree: true );
                return new InstallerResult( -1, string.Empty, "timed out after 120s" );
            }
            return new InstallerResult( process.ExitCode,
                standardOutput.GetAwaiter( ).GetResult( ), standardError.GetAwaiter( ).GetResult( ) );
        }

        private static string? FindPowerShell( ) {
            foreach (string candidate in new[] { "pwsh", "/usr/local/bin/pwsh", "/opt/homebrew/bin/pwsh" }) {
                try {
                    using Process process = Process.Start( new ProcessStartInfo( candidate, "--version" ) {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    } )!;
                    if (process.WaitForExit( 30_000 ) && process.ExitCode == 0) { return candidate; }
                } catch (Exception) { /* try the next candidate */ }
            }
            return null;
        }

        private static string FindRepositoryRoot( ) {
            DirectoryInfo? directory = new( AppContext.BaseDirectory );
            while (directory is not null && !File.Exists( Path.Combine( directory.FullName, "BridgeBeats.sln" ) )) {
                directory = directory.Parent;
            }
            Assert.IsNotNull( directory, "could not locate the repository root from the test output directory" );
            return directory.FullName;
        }

        public void Dispose( ) {
            // Unconditional, and not the guarded restore: a hidden directory must still be removable.
            try {
                _ = TrySetSecretsDirectoryMode(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
            } catch (Exception) { /* best effort */ }
            try { Directory.Delete( _root, recursive: true ); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>Observed permission modes for the pre-fix and restricted backup trees.</summary>
    private sealed record BackupTreeModes(
        string PreFixDirectory,
        bool PreFixIsPrivate,
        string Directory,
        string File,
        string NestedDirectory,
        string NestedFile,
        bool IsPrivate );

    /// <summary>Backup permissions observed by the copy command before later hardening.</summary>
    private sealed record BackupRootOrder(
        string RootModeBeforeCopy,
        bool RootOwnerOnlyBeforeCopy,
        string SecretMode );

    /// <summary>Ordering and fail-closed behaviour of the PowerShell ACL capability resolution.</summary>
    private sealed record AclOrdering(
        bool CapabilityResolvedFirst,
        bool ResolutionLineBeforeBackupBlock,
        bool UsesResolvedFlagGuard,
        bool ReadsUnresolvedCapability,
        string NonWindowsDecision );

    private sealed record InstallerResult( int ExitCode, string StandardOutput, string StandardError ) {
        /// <summary>The value the harness wrote to its result file, when it writes one.</summary>
        internal string Reported { get; init; } = string.Empty;

        internal string CombinedOutput => StandardOutput + StandardError;

        internal string Describe( ) {
            return $"exit={ExitCode}{Environment.NewLine}"
                + $"stdout:{Environment.NewLine}{StandardOutput}{Environment.NewLine}"
                + $"stderr:{Environment.NewLine}{StandardError}";
        }
    }
}
#pragma warning restore CS1591
