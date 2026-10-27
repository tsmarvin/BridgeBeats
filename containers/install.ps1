#Requires -Version 5.1
<#
.SYNOPSIS
    BridgeBeats Installation Script for Windows

.DESCRIPTION
    This script downloads and configures BridgeBeats for Docker deployment on Windows.
    Linux and macOS hosts must use install.sh instead — it sets the required file ownership
    and permissions that this Windows installer does not apply.

    It validates dependencies, downloads configuration files, sets up secrets,
    and prepares the environment for running BridgeBeats via Docker Compose.

.PARAMETER Branch
    Git branch to download from (default: develop)

.PARAMETER Directory
    Installation directory (default: .\bridgebeats)

.EXAMPLE
    # Run directly from GitHub:
    iwr -useb https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.ps1 | iex

    # Or download and run with parameters:
    .\install.ps1 -Branch main -Directory C:\BridgeBeats

.LINK
    https://github.com/tsmarvin/BridgeBeats
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$Branch = 'develop',

    [Parameter()]
    [string]$Directory = '.\bridgebeats'
)

# =============================================================================
# Configuration
# =============================================================================
$ErrorActionPreference = 'Stop'
$RepoOwner  = 'tsmarvin'
$RepoName   = 'BridgeBeats'
$Timestamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$UpgradeLog = 'upgrade.log'

# =============================================================================
# ACL Capability (resolved up front)
# =============================================================================
# Every path that may hold secret material is ACL-restricted, and those restrictions are applied
# before any secret is copied. The capability and the acting principal are therefore resolved here,
# before any backup or secret handling runs: reading an unset variable later in the script yields
# $null, and "-not $null" is $true, so a guard written that way would pass while the restriction was
# silently skipped.
$isWindowsPlatform = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
$currentUser = $null
$aclCapabilityError = $null

if ($isWindowsPlatform) {
    # A Windows host is expected to be able to apply permissions. Absence of the capability is a
    # hard error rather than a silent downgrade.
    try {
        $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        if (-not $currentUser) { $aclCapabilityError = 'could not determine the current user' }
    } catch {
        $aclCapabilityError = $_.Exception.Message
    }
    if (-not $aclCapabilityError -and -not (Get-Command icacls -ErrorAction Ignore)) {
        $aclCapabilityError = 'icacls was not found on PATH'
    }
}

# True when this host can restrict a path to the current user. False means "ACLs do not apply here"
# (a non-Windows host), not "failed" — failures are reported through $aclCapabilityError.
$canRestrictAcl = $isWindowsPlatform -and (-not $aclCapabilityError)

# Marks that the resolution above actually ran. Callers require this to be $true before skipping a
# restriction: if the variables were somehow unset, "-not $canRestrictAcl" is $true, which would
# silently permit an unprotected copy. Requiring an explicit resolved flag makes that case fail.
$aclCapabilityResolved = $true

# Files to download from the repository
$DownloadFiles = @('docker-compose.yml', 'Caddyfile', '.env.example')

# Files to backup during upgrades (excludes .env.example as it's just a template)
$BackupFiles = @('docker-compose.yml', 'Caddyfile')

# Secret files configuration
$SecretFiles = @(
    @{ Name = 'apple_key.p8';              Description = 'Apple Music private key (.p8 file)';               AutoGenerate = $false }
    @{ Name = 'spotify_client_secret.txt'; Description = 'Spotify API client secret';                        AutoGenerate = $false }
    @{ Name = 'tidal_client_secret.txt';   Description = 'Tidal API client secret';                          AutoGenerate = $false }
    @{ Name = 'discord_token.txt';         Description = 'Discord bot token';                                AutoGenerate = $false }
    @{ Name = 'atproto_password.txt';      Description = 'ATProto app password';                             AutoGenerate = $false }
    @{ Name = 'atproto_oauth_key.json';    Description = 'ATProto OAuth signing key (ES256 JWK)';            AutoGenerate = $true }
    @{ Name = 'api_key_salt.txt';          Description = 'API key salt';                                     AutoGenerate = $true }
    @{ Name = 'redis_password.txt';        Description = 'Redis password';                                   AutoGenerate = $true }
    @{ Name = 'internal_service_key.txt';  Description = 'Internal service key (worker/API authentication)'; AutoGenerate = $true }
    @{ Name = 'cloudflare_api_token.txt';  Description = 'Cloudflare API token (DNS)';                       AutoGenerate = $false }
)

# Construct base URL for raw file downloads
$Domain = "https://raw.githubusercontent.com/$RepoOwner/$RepoName/$Branch/containers"

# =============================================================================
# Helper Functions
# =============================================================================
function Write-Header {
    param([string]$Message)
    Write-Host "`n==========================================" -ForegroundColor Cyan
    Write-Host $Message -ForegroundColor Cyan
    Write-Host "==========================================`n" -ForegroundColor Cyan
}

function Write-Section {
    param([string]$Message)
    Write-Host "`n--- $Message ---`n" -ForegroundColor Yellow
}

function Write-Ok {
    param([string]$Message)
    Write-Host "[OK] $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "[WARN] $Message" -ForegroundColor Yellow
}

function Write-Err {
    param([string]$Message)
    Write-Host "[ERROR] $Message" -ForegroundColor Red
}

function Write-UpgradeLog {
    param([string]$Message)
    $logPath = Join-Path -Path $Directory -ChildPath $UpgradeLog
    $logEntry = "[$Timestamp] $Message"
    Add-Content -Path $logPath -Value $logEntry
}

function Test-SecretHasContent {
    param([string]$FilePath)

    if (-not (Test-Path $FilePath)) {
        return $false
    }

    # Read content and check if it has non-placeholder content
    # We check for patterns but don't store the actual secret value
    $content = Get-Content -Path $FilePath -Raw -ErrorAction Ignore
    if ([string]::IsNullOrWhiteSpace($content)) {
        return $false
    }

    # Check for placeholder patterns
    if ($content -match '^(your_|REPLACE_WITH_)') {
        return $false
    }

    # Has non-whitespace, non-placeholder content
    return ($content -match '\S')
}

function Protect-PathToCurrentUser {
    param(
        [string]$Path,
        [string]$User
    )

    # Replaces the discretionary ACL with a single full-control entry for $User, then verifies the
    # result by reading the ACL back.
    #
    # `icacls /inheritance:r /grant:r` is deliberately not used: /inheritance:r removes only
    # *inherited* entries, so any explicit ACE already on the path survives. On an install that was
    # set up before this change, or where another tool granted access explicitly, that would leave a
    # second principal with read access while appearing to succeed.
    #
    # Inheritance is switched off and the target marked protected, so children start from this ACL.
    # No principal beyond $User is ever added: the Docker service account that resolves bind mounts on
    # a Windows host is not known here, and inventing one would grant access to an unverified account.
    try {
        $acl = Get-Acl -Path $Path -ErrorAction Stop
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($existing in @($acl.Access)) {
            $acl.RemoveAccessRule($existing) | Out-Null
        }

        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $User,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
        Set-Acl -Path $Path -AclObject $acl -ErrorAction Stop

        # Read back and confirm the intended state: no inherited entries, and every remaining explicit
        # entry grants the current user and nobody else.
        $verified = Get-Acl -Path $Path -ErrorAction Stop
        if (-not $verified.AreAccessRulesProtected) {
            Write-Err "ACL on $Path is still inheriting entries after being restricted"
            return $false
        }

        $entries = @($verified.Access)
        if ($entries.Count -eq 0) {
            Write-Err "ACL on $Path has no explicit entries after being restricted"
            return $false
        }

        foreach ($entry in $entries) {
            if ($entry.IdentityReference.Value -notlike "*$User") {
                Write-Err "ACL on $Path grants access to $($entry.IdentityReference.Value), which is not the current user"
                return $false
            }
        }
        return $true
    } catch {
        Write-Err "Could not restrict $Path : $($_.Exception.Message)"
        return $false
    }
}

function Get-TreeManifest {
    param([string]$Root)

    # One line per file: relative path, length, SHA256. Only metadata and hashes are produced, never
    # file content, so the manifest can be compared without exposing secret values.
    if (-not (Test-Path $Root)) { return @() }

    return Get-ChildItem -Path $Root -Recurse -File -Force -ErrorAction Stop | ForEach-Object {
        $relative = $_.FullName.Substring($Root.Length).TrimStart('\', '/')
        $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256 -ErrorAction Stop).Hash
        "$relative`t$($_.Length)`t$hash"
    } | Sort-Object
}

function Test-SecretsTreeCopied {
    param(
        [string]$Source,
        [string]$Destination
    )

    # Copies the tree into a fresh destination and verifies the result against a manifest of the
    # source. Returns $true only when every file is present with identical content; on any failure
    # the partial copy is removed so a later run cannot mistake it for a rollback point.
    try {
        $sourceManifest = Get-TreeManifest -Root $Source
        if (-not $sourceManifest -or $sourceManifest.Count -eq 0) {
            Write-Err 'No secrets were found to back up'
            return $false
        }

        if (-not (Test-Path $Destination)) {
            New-Item -Path $Destination -ItemType Directory -Force -ErrorAction Stop | Out-Null
        }

        # Copy the contents, not the directory: Copy-Item -Recurse of a directory into an existing
        # destination nests the tree one level deeper than the backup is read from.
        foreach ($item in Get-ChildItem -Path $Source -Force -ErrorAction Stop) {
            Copy-Item -Path $item.FullName -Destination $Destination -Recurse -Force -ErrorAction Stop
        }

        $copyManifest = Get-TreeManifest -Root $Destination
        if (Compare-Object -ReferenceObject $sourceManifest -DifferenceObject $copyManifest) {
            Write-Err 'The secrets backup does not match the source; no rollback copy was created'
            Remove-Item -Path $Destination -Recurse -Force -ErrorAction Ignore
            return $false
        }

        return $true
    } catch {
        Write-Err "Failed to back up the secrets directory: $($_.Exception.Message)"
        Remove-Item -Path $Destination -Recurse -Force -ErrorAction Ignore
        return $false
    }
}

function Get-SigningKeyShape {
    param([string]$Content)

    # Fail-closed shape check mirroring the application's loader: content that is neither a PEM
    # private key nor an EC P-256 private JWK is rejected rather than accepted for being non-blank.
    # This is a shape check, not a parse, so the application still performs final validation.
    if ($Content -match '-----BEGIN[^-]*PRIVATE KEY-----') {
        return 'ok'
    }

    if (-not $Content.TrimStart().StartsWith('{')) {
        return 'not a JSON JWK or a PEM private key'
    }

    # Leading whitespace is tolerated: the application trims before deciding whether the content is
    # a JWK, so rejecting it here would refuse a key the application would load.
    $trimmed = $Content.TrimStart()
    foreach ($member in @('kty', 'crv', 'x', 'y', 'd', 'kid')) {
        if ($trimmed -notmatch ('"' + $member + '"')) {
            return "JWK is missing the $member member"
        }
    }

    if ($trimmed -notmatch '"EC"' -or $trimmed -notmatch '"P-256"') {
        return 'JWK is not an EC P-256 key'
    }

    return 'ok'
}

function Get-SigningKeyState {
    param([string]$FilePath)

    # Startup rejects a blank, whitespace-only, placeholder, or "{}" ATProto signing key
    # (BlueskyAuthenticationConfiguration.LoadSigningKey), so none of those shapes may be
    # accepted as a working key. PEM and JWK content both contain other characters.
    #
    # An unreadable file is reported separately from unusable content: a read failure is not
    # evidence that the key is broken, and replacing it would invalidate a key the application
    # may currently be publishing.
    try {
        $content = Get-Content -Path $FilePath -Raw -ErrorAction Stop
    } catch {
        return 'Unreadable'
    }

    if ([string]::IsNullOrWhiteSpace($content)) {
        return 'Unusable'
    }

    if ([string]::IsNullOrWhiteSpace($content.Replace('{', '').Replace('}', ''))) {
        return 'Unusable'
    }

    if ($content -match '^(your_|REPLACE_WITH_)') {
        return 'Unusable'
    }

    # Non-blank is not the same as loadable. A malformed key is reported separately rather than as
    # unusable, because replacing it would discard content the operator may have intended.
    if ((Get-SigningKeyShape -Content $content) -ne 'ok') {
        return 'Malformed'
    }

    return 'Usable'
}

function Backup-SecretFile {
    param([string]$FilePath)

    # Returns $true only when a byte-identical copy is verified on disk. The caller must not
    # replace the original otherwise: without a readable copy, replacing a live signing key
    # would discard the only record of a published key.
    # The copy stays inside the secrets directory, which docker-compose.yml mounts file by
    # file, so no container ever loads it.
    $backupPath = "$FilePath.bak.$Timestamp"
    try {
        Copy-Item -Path $FilePath -Destination $backupPath -Force -ErrorAction Stop
    } catch {
        Write-Err "Could not back up $FilePath : $($_.Exception.Message)"
        return $false
    }

    try {
        $original = (Get-FileHash -Path $FilePath -Algorithm SHA256 -ErrorAction Stop).Hash
        $copy = (Get-FileHash -Path $backupPath -Algorithm SHA256 -ErrorAction Stop).Hash
    } catch {
        Write-Err "Could not verify the backup of $FilePath : $($_.Exception.Message)"
        Remove-Item -Path $backupPath -Force -ErrorAction Ignore
        return $false
    }

    if ($original -ne $copy) {
        Write-Err "Backup of $FilePath did not verify; leaving the original untouched"
        Remove-Item -Path $backupPath -Force -ErrorAction Ignore
        return $false
    }

    Write-Ok "Backed up existing $([System.IO.Path]::GetFileName($FilePath)) to $([System.IO.Path]::GetFileName($backupPath))"
    return $true
}

function New-SigningKeyJwk {
    param([string]$FilePath)

    # Generate first, then write, so a generation failure cannot leave a truncated key behind.
    try {
        $generated = Get-ES256SigningKeyJwk
    } catch {
        Write-Err "Could not generate the ATProto OAuth signing key: $($_.Exception.Message)"
        return $false
    }

    if ([string]::IsNullOrWhiteSpace($generated)) {
        Write-Err 'Could not generate the ATProto OAuth signing key: generation produced no content'
        return $false
    }

    try {
        Set-Content -Path $FilePath -Value $generated -NoNewline
    } catch {
        Write-Err "Could not write $FilePath : $($_.Exception.Message)"
        return $false
    }

    return $true
}

function Get-RandomString {
    param([int]$Length = 32)

    # Try openssl first
    $opensslPath = Get-Command openssl -ErrorAction Ignore
    if ($opensslPath) {
        try {
            $result = & openssl rand -base64 $Length 2>$null
            if ($LASTEXITCODE -eq 0 -and $result) {
                return $result.Trim()
            }
        } catch {
            # Fall through to .NET method
        }
    }

    # Use .NET cryptography
    $bytes = New-Object byte[] $Length
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    return [Convert]::ToBase64String($bytes)
}

function Get-ES256SigningKeyJwk {
    # Generate an ES256 (P-256) key pair and return it as a JWK JSON string
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create(
        [System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256')
    )
    try {
        $params = $ecdsa.ExportParameters($true)

        $b64url = {
            param([byte[]]$bytes)
            [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        }

        return (@{
            kty = 'EC'
            crv = 'P-256'
            x   = & $b64url $params.Q.X
            y   = & $b64url $params.Q.Y
            d   = & $b64url $params.D
            kid = [Guid]::NewGuid().ToString('N')
            alg = 'ES256'
            use = 'sig'
        } | ConvertTo-Json -Compress)
    } finally {
        $ecdsa.Dispose()
    }
}

function Get-EnvVarNames {
    param([string]$FilePath)
    $names = @()

    if (-not (Test-Path $FilePath)) {
        return $names
    }

    Get-Content $FilePath | ForEach-Object {
        if ($_ -match '^([A-Z_][A-Z0-9_]*)=') {
            $names += $Matches[1]
        }
    }
    return $names | Sort-Object -Unique
}

function Get-EnvVarLine {
    param(
        [string]$FilePath,
        [string]$VarName
    )

    Get-Content -Path $FilePath | Where-Object { $_ -match "^$VarName=" } | Select-Object -First 1
}

# =============================================================================
# Dependency Validation
# =============================================================================
Write-Header 'BridgeBeats Installation Script'

Write-Host "Branch: $Branch"
Write-Host "Install Directory: $Directory`n"

Write-Section 'Checking Dependencies'

$missingDeps = @()
$warnings = @()

# Check for Docker
$dockerCmd = Get-Command docker -ErrorAction Ignore
if ($dockerCmd) {
    try {
        $dockerVersion = & docker --version 2>$null
        Write-Ok "Docker: $dockerVersion"
    } catch {
        Write-Ok 'Docker: available'
    }
} else {
    $missingDeps += 'docker - Install from https://docs.docker.com/get-docker/'
}

# Check for Docker Compose (v2 plugin)
$composeWorks = $false
try {
    $null = & docker compose version 2>$null
    if ($LASTEXITCODE -eq 0) {
        $composeVersion = & docker compose version --short 2>$null
        Write-Ok "Docker Compose: $composeVersion"

        # Check for v2+
        if ($composeVersion -match '^1\.') {
            $missingDeps += "docker compose v2+ - Current version $composeVersion is too old. Update Docker or install compose plugin."
        } else {
            $composeWorks = $true
        }
    }
} catch {
    # Compose not available
}

if (-not $composeWorks -and $missingDeps.Count -eq 0) {
    $missingDeps += 'docker compose - Install Docker Compose plugin: https://docs.docker.com/compose/install/'
}

# Check for openssl (optional)
$opensslCmd = Get-Command openssl -ErrorAction Ignore
if ($opensslCmd) {
    Write-Ok 'openssl: available (for generating secure random secrets)'
} else {
    $warnings += 'openssl not found - Will use .NET cryptography for generating random secrets'
}

# Report warnings
if ($warnings.Count -gt 0) {
    Write-Host ''
    foreach ($warning in $warnings) {
        Write-Warn $warning
    }
}

# Report missing dependencies
if ($missingDeps.Count -gt 0) {
    Write-Err "`nMissing required dependencies:"
    foreach ($dep in $missingDeps) {
        Write-Host "  - $dep"
    }
    Write-Host "`nPlease install the missing dependencies and run this script again."
    exit 1
}

Write-Host ''
Write-Ok 'All required dependencies are available'

# =============================================================================
# Create Installation Directory
# =============================================================================
Write-Section 'Setting Up Installation Directory'

if (Test-Path $Directory) {
    Write-Ok "Directory already exists: $Directory"
} else {
    New-Item -Path $Directory -ItemType Directory -Force | Out-Null
    Write-Ok "Created directory: $Directory"
}

# Convert to absolute path and change to it
$Directory = Resolve-Path $Directory
Set-Location $Directory
Write-Ok "Working directory: $Directory"

# =============================================================================
# Download Configuration Files
# =============================================================================
Write-Section 'Downloading Configuration Files'

foreach ($file in $DownloadFiles) {
    $sourceUrl = "$Domain/$file"
    $destPath = Join-Path -Path $Directory -ChildPath $file

    # Backup existing file if it's in the backup list
    if ((Test-Path $destPath) -and ($file -in $BackupFiles)) {
        $backupName = "$file.bak.$Timestamp"
        $backupPath = Join-Path -Path $Directory -ChildPath $backupName
        Copy-Item -Path $destPath -Destination $backupPath
        Write-Ok "Backed up existing $file to $backupName"
        Write-UpgradeLog "Backed up $file to $backupName"
    }

    # Download the file
    try {
        Invoke-WebRequest -Uri $sourceUrl -OutFile $destPath -UseBasicParsing
        Write-Ok "Downloaded: $file"
    } catch {
        Write-Err "Failed to download: $file"
        Write-Host "  URL: $sourceUrl"
        Write-Host "  Error: $($_.Exception.Message)"
        exit 1
    }
}

# =============================================================================
# Setup Logs Directory
# =============================================================================
Write-Section 'Setting Up Logs'

$logsDir = Join-Path -Path $Directory -ChildPath 'logs'

if (Test-Path $logsDir) {
    Write-Ok "Logs directory already exists: $logsDir"
} else {
    New-Item -Path $logsDir -ItemType Directory -Force | Out-Null
    Write-Ok "Created logs directory: $logsDir"
}

$dataDirs = @(
    (Join-Path $Directory 'data\app'),
    (Join-Path $Directory 'data\dp-keys'),
    (Join-Path $Directory 'data\redis'),
    (Join-Path $Directory 'data\pds'),
    (Join-Path $Directory 'logs\caddy')
)
foreach ($d in $dataDirs) {
    if (-not (Test-Path $d)) { New-Item -Path $d -ItemType Directory -Force | Out-Null }
}
Write-Ok 'Data directories present: data\{app,dp-keys,redis,pds}, logs\caddy'

# =============================================================================
# Check for Existing Containers
# =============================================================================
Write-Section 'Checking Existing Deployment'

$secretsDir = Join-Path -Path $Directory -ChildPath 'secrets'

# BridgeBeats container names from docker-compose.yml
$BridgeBeatsContainers = @('bridgebeats', 'bridgebeats-redis', 'bridgebeats-pds', 'bridgebeats-caddy')

# Check if any BridgeBeats containers exist
$containersExist = $false
$existingContainers = @()
try {
    $allContainers = & docker ps -a --format '{{.Names}}' 2>$null
    if ($LASTEXITCODE -eq 0 -and $allContainers) {
        foreach ($container in $BridgeBeatsContainers) {
            if ($allContainers -contains $container) {
                $containersExist = $true
                $existingContainers += $container
            }
        }
    }
} catch {
    # Docker not available or error
}

if ($containersExist) {
    Write-Ok 'Existing BridgeBeats containers detected:'
    foreach ($container in $existingContainers) {
        Write-Host "  - $container"
    }

    # Log upgrade session header
    Write-UpgradeLog ''
    Write-UpgradeLog "=== Upgrade: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
    Write-UpgradeLog "Branch: $Branch"

    # Back up secrets before any modifications. A failed backup must stop the run: the prune below
    # removes older copies, so continuing after a failure could leave no rollback point at all.
    if (Test-Path $secretsDir) {
        $backupTimestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
        $backupName = "secrets_backup_$backupTimestamp"
        $backupDir = Join-Path $Directory $backupName

        # A sibling backup directory inherits the ACL of the install root, not of the secrets
        # directory it mirrors, so its ACL is replaced with the intended one *before* any secret
        # material is copied into it (children then inherit that ACL). The install root itself is
        # deliberately left alone: it also holds the Compose file and data directories.
        # Failure is fatal, so the copy cannot proceed into an unprotected directory.
        #
        # An unset capability is treated as a failure on Windows, never as permission to copy: the
        # intended ACL must be established and verified before the first secret is written.
        $backupRestricted = $false
        if (-not $aclCapabilityResolved) {
            Write-Err "Cannot restrict $backupName : the ACL capability was never resolved"
        } elseif ($aclCapabilityError) {
            Write-Err "Cannot restrict $backupName : $aclCapabilityError"
        } elseif (-not $canRestrictAcl) {
            # Not a Windows host, so there is no ACL to apply here.
            $backupRestricted = $true
        } else {
            if (-not (Test-Path $backupDir)) {
                New-Item -Path $backupDir -ItemType Directory -Force | Out-Null
            }
            $backupRestricted = Protect-PathToCurrentUser -Path $backupDir -User $currentUser
        }

        if (-not $backupRestricted) {
            Write-Err "Refusing to write secret backups: $backupName is not restricted to the current user."
            Remove-Item -Path $backupDir -Recurse -Force -ErrorAction Ignore
            Write-UpgradeLog 'ERROR: Backup directory ACL was not restricted'
            exit 1
        }

        $backedUp = $false
        if (Test-SecretsTreeCopied -Source $secretsDir -Destination $backupDir) {
            $backedUp = $true
            Write-Ok "Backed up secrets to $backupName/"
            Write-UpgradeLog "Backed up secrets to $backupName/"
        } else {
            Write-Err 'Failed to back up the secrets directory; no rollback copy was created'
            Write-Err 'Old backups were left in place. Fix the file access permissions, then re-run this script.'
            Write-UpgradeLog 'ERROR: Failed to back up secrets directory'
        }

        if (-not $backedUp) {
            exit 1
        }

        # Each retained backup holds the same private keys, so the older siblings receive the same
        # intended ACL. A failure here is reported rather than ignored.
        if ($canRestrictAcl) {
            foreach ($existingBackup in Get-ChildItem -Path $Directory -Directory -Filter 'secrets_backup_*' -ErrorAction Ignore) {
                if (-not (Protect-PathToCurrentUser -Path $existingBackup.FullName -User $currentUser)) {
                    Write-Warn "Could not restrict $($existingBackup.Name)"
                }
            }
        }

        # Prune old backups, keep 3 most recent. Only reached once the new copy is verified.
        Get-ChildItem -Path $Directory -Directory -Filter 'secrets_backup_*' |
            Sort-Object Name -Descending |
            Select-Object -Skip 3 |
            Remove-Item -Recurse -Force
    }

    # Log current container image information with RepoDigests
    Write-Host "`nCurrent container images:"
    Write-UpgradeLog 'Pre-update images:'

    foreach ($container in $existingContainers) {
        $image = & docker inspect --format '{{.Config.Image}}' $container 2>$null
        if (-not $image) {
            $image = 'unknown'
        }

        if ($image -ne 'unknown') {
            $digest = & docker inspect --format '{{index .RepoDigests 0}}' $image 2>$null
            if (-not $digest) {
                $digest = 'unknown'
            }
        } else {
            $digest = 'unknown'
        }

        Write-Host "  $container`: image=$image digest=$digest"
        Write-UpgradeLog "  $container`: image=$image digest=$digest"
    }

    # Log rollback info
    $composeBackup = "docker-compose.yml.bak.$Timestamp"
    $composeBackupPath = Join-Path -Path $Directory -ChildPath $composeBackup
    if (Test-Path $composeBackupPath) {
        Write-UpgradeLog "Rollback: Restore $composeBackup and run 'docker compose up -d'"
    }

    Write-Host "`nImage information has been logged to $UpgradeLog for rollback reference.`n"

    # Prompt for pulling new images
    $pullResponse = Read-Host 'Would you like to pull the latest container images? (y/N)'
    if ($pullResponse -match '^[Yy]') {
        Write-Host "`nPulling latest images..."
        & docker compose pull
        Write-UpgradeLog "Pulled latest images at $(Get-Date -Format 'HH:mm:ss')"
        Write-Ok 'Images updated'
    } else {
        Write-UpgradeLog 'Image pull skipped by user'
        Write-Ok 'Skipping image pull'
    }
} else {
    Write-Ok 'No existing containers found (fresh installation)'
}

# =============================================================================
# Setup Secrets Directory
# =============================================================================
Write-Section 'Setting Up Secrets'

# On Windows the secrets directory inherits the ACL of whatever directory it is created in, which
# for an arbitrary -Directory is not the current user's profile and may grant other principals
# access to the signing key. Restrict the directory to the installing user explicitly. No other
# principal is granted access: the container reads secrets through bind mounts resolved by the
# Docker service, not through a Windows account on this host.
if (Test-Path $secretsDir) {
    Write-Ok "Secrets directory already exists: $secretsDir"
} else {
    New-Item -Path $secretsDir -ItemType Directory -Force | Out-Null
    Write-Ok "Created secrets directory: $secretsDir"
}

# The secrets directory is restricted before any secret is written into it, and a failure is fatal:
# continuing would store private keys in a directory other local accounts can read, and then report
# success. The capability was resolved at the top of the script; nothing is re-derived here.
$secretsRestricted = $false
if (-not $aclCapabilityResolved) {
    Write-Err 'Could not restrict the secrets directory ACL: the ACL capability was never resolved'
} elseif ($aclCapabilityError) {
    Write-Err "Could not restrict the secrets directory ACL: $aclCapabilityError"
} elseif (-not $canRestrictAcl) {
    # Not a Windows host, so there is no ACL to apply here.
    $secretsRestricted = $true
} elseif (Protect-PathToCurrentUser -Path $secretsDir -User $currentUser) {
    $secretsRestricted = $true
    Write-Ok 'Restricted the secrets directory to the installing user'
}

if (-not $secretsRestricted) {
    # Braces around $env:USERNAME matter: PowerShell reads "$env:USERNAME:(OI)(CI)F" as a
    # scope-qualified variable name and interpolates an empty account.
    Write-Err 'Refusing to continue: the secrets directory ACL was not restricted, so stored private keys would be readable by other accounts on this host.'
    Write-Err "Restrict it and re-run: icacls `"$secretsDir`" /inheritance:r /grant:r `"${env:USERNAME}:(OI)(CI)F`""
    exit 1
}

# Track secrets that need user attention
$missingSecrets = @()

foreach ($secret in $SecretFiles) {
    $secretPath = Join-Path -Path $secretsDir -ChildPath $secret.Name

    if (Test-Path $secretPath) {
        # The ATProto signing key is the one secret the application refuses to start without,
        # so an unusable file must be replaced here rather than reported and left in place.
        if ($secret.Name -eq 'atproto_oauth_key.json') {
            switch (Get-SigningKeyState -FilePath $secretPath) {
                'Usable' {
                    Write-Ok "Secret exists and has content: $($secret.Name)"
                }
                'Unusable' {
                    Write-Warn "$($secret.Name) is empty, placeholder, or '{}' - a usable ES256 signing key is required"
                    if (-not (Backup-SecretFile $secretPath)) {
                        Write-Err "Refusing to replace $($secret.Name) without a verified backup. Fix the file's access permissions, then re-run this script."
                        exit 1
                    }
                    if (New-SigningKeyJwk -FilePath $secretPath) {
                        Write-Ok "Regenerated $($secret.Name) with a new ES256 signing key"
                        Write-UpgradeLog "Regenerated $($secret.Name) (previous content was unusable)"
                    } else {
                        Write-Err "Could not generate a replacement $($secret.Name). Supply an ES256 JWK manually, then re-run this script."
                        exit 1
                    }
                }
                'Malformed' {
                    # Non-blank but not a loadable ES256 key. Replacing it could discard content the
                    # operator intended, so fail with guidance instead of overwriting.
                    Write-Err "$($secret.Name) is not a usable ES256 signing key."
                    Write-Err 'It must be an ES256 private JWK (kty/crv/x/y/d/kid) or a PEM private key.'
                    Write-Err "Review $secretPath, or move it aside and re-run to generate a new key."
                    exit 1
                }
                default {
                    # An unreadable file is not evidence that the key is broken.
                    Write-Err "Cannot read $($secret.Name) to verify it. Fix the file's access permissions, then re-run this script."
                    exit 1
                }
            }
        } elseif (Test-SecretHasContent $secretPath) {
            Write-Ok "Secret exists and has content: $($secret.Name)"
        } else {
            Write-Warn "Secret exists but appears empty or contains placeholder content: $($secret.Name)"
        }
    } else {
        if ($secret.AutoGenerate) {
            # Auto-generate this secret
            if ($secret.Name -eq 'atproto_oauth_key.json') {
                if (-not (New-SigningKeyJwk -FilePath $secretPath)) {
                    Write-Err "Could not generate $($secret.Name). Supply an ES256 JWK manually, then re-run this script."
                    exit 1
                }
                Write-Ok "Auto-generated: $($secret.Name)"
                continue
            }
            $generatedValue = Get-RandomString
            Set-Content -Path $secretPath -Value $generatedValue -NoNewline
            Write-Ok "Auto-generated: $($secret.Name)"
        } else {
            # Create placeholder
            if ($secret.Name -eq 'apple_key.p8') {
                $placeholder = @'
-----BEGIN PRIVATE KEY-----
REPLACE_WITH_YOUR_APPLE_MUSIC_PRIVATE_KEY
-----END PRIVATE KEY-----
'@
            } else {
                $baseName = [System.IO.Path]::GetFileNameWithoutExtension($secret.Name)
                $placeholder = "your_${baseName}_here"
            }
            Set-Content -Path $secretPath -Value $placeholder
            $missingSecrets += $secret
            Write-Warn "Created placeholder: $($secret.Name) (needs your input)"
        }
    }
}

# =============================================================================
# Setup Environment File
# =============================================================================
Write-Section 'Configuring Environment'

$envPath = Join-Path $Directory '.env'
$envExamplePath = Join-Path $Directory '.env.example'

if (Test-Path $envPath) {
    Write-Ok 'Environment file exists: .env'

    # Check for new variables in .env.example
    if (Test-Path $envExamplePath) {
        $existingVars = Get-EnvVarNames $envPath
        $exampleVars = Get-EnvVarNames $envExamplePath

        $newVars = @()
        foreach ($var in $exampleVars) {
            if ($var -notin $existingVars) {
                $newVars += $var
            }
        }

        if ($newVars.Count -gt 0) {
            Write-Host ''
            Write-Warn 'New environment variables found in .env.example:'
            Write-Host '       Please add these to your .env file:'
            Write-Host ''
            foreach ($var in $newVars) {
                $line = Get-EnvVarLine $envExamplePath $var
                Write-Host "  $line"
            }
            Write-Host ''
        }
    }
} else {
    if (Test-Path $envExamplePath) {
        Copy-Item $envExamplePath $envPath
        Write-Ok 'Created .env from .env.example'
        Write-Warn 'Please edit .env with your configuration values'
    } else {
        Write-Err 'No .env.example found to create .env from'
        exit 1
    }
}


# =============================================================================
# Summary and Next Steps
# =============================================================================
Write-Header 'Installation Complete'

Write-Host "Installation directory: $Directory`n"

# Report secrets that need attention
if ($missingSecrets.Count -gt 0) {
    Write-Host "[ACTION REQUIRED] The following secrets need your input:`n" -ForegroundColor Magenta
    foreach ($secret in $missingSecrets) {
        Write-Host "  - secrets\$($secret.Name)"
        Write-Host "    $($secret.Description)`n"
    }
}

# Check .env for empty required values
Write-Host 'Checking environment configuration...'
$envIssues = @()

# Required environment variables to check
$requiredEnvVars = @(
    'DOMAIN'
    'CADDY_ADMIN_EMAIL'
    'PDS_HOSTNAME'
    'PDS_JWT_SECRET'
    'PDS_ADMIN_PASSWORD'
)

# At least one provider required
$providerVars = @(
    'APPLE_TEAM_ID'
    'SPOTIFY_CLIENT_ID'
    'TIDAL_CLIENT_ID'
)

$envContent = Get-Content -Path $envPath -ErrorAction Ignore

foreach ($var in $requiredEnvVars) {
    $line = $envContent | Where-Object { $_ -match "^$var=\s*$" }
    if ($line) {
        $envIssues += "$var - Required but empty"
    }
}

$providerFound = $false
foreach ($var in $providerVars) {
    $line = $envContent | Where-Object { $_ -match "^$var=.+$" }
    if ($line) {
        # Check it's not empty
        $emptyLine = $envContent | Where-Object { $_ -match "^$var=\s*$" }
        if (-not $emptyLine) {
            $providerFound = $true
            break
        }
    }
}

if (-not $providerFound) {
    $envIssues += 'At least one music provider required (APPLE_TEAM_ID, SPOTIFY_CLIENT_ID, or TIDAL_CLIENT_ID)'
}

if ($envIssues.Count -gt 0) {
    Write-Host "`n[ACTION REQUIRED] Environment configuration issues:`n" -ForegroundColor Magenta
    foreach ($issue in $envIssues) {
        Write-Host "  - $issue"
    }
    Write-Host "`nEdit .env to configure these values."
}

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host 'Next Steps' -ForegroundColor Cyan
Write-Host "==========================================`n" -ForegroundColor Cyan
Write-Host "1. Edit secrets in: $Directory\secrets\"
Write-Host "2. Edit environment in: $Directory\.env"
Write-Host "3. Start BridgeBeats with:`n"
Write-Host "   cd $Directory"
Write-Host "   docker compose up -d`n"
Write-Host 'For more information, see:'
Write-Host "  https://github.com/$RepoOwner/$RepoName/blob/$Branch/docs/DEPLOYMENT.md`n"
