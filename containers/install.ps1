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

    # Back up secrets before any modifications
    if (Test-Path $secretsDir) {
        $backupTimestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
        $backupDir = Join-Path $Directory "secrets_backup_$backupTimestamp"
        try {
            Copy-Item -Path $secretsDir -Destination $backupDir -Recurse
            Write-Ok "Backed up secrets to secrets_backup_$backupTimestamp/"
            Write-UpgradeLog "Backed up secrets to secrets_backup_$backupTimestamp/"
        } catch {
            Write-Warn 'Failed to back up secrets directory'
            Write-UpgradeLog 'WARN: Failed to back up secrets directory'
        }

        # Prune old backups, keep 3 most recent
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

# On Windows, the secrets directory inherits the current user's profile ACL.
# Linux-style ownership and mode-700 permission hardening is handled by install.sh on Linux hosts.

if (Test-Path $secretsDir) {
    Write-Ok "Secrets directory already exists: $secretsDir"
} else {
    New-Item -Path $secretsDir -ItemType Directory -Force | Out-Null
    Write-Ok "Created secrets directory: $secretsDir"
}

# Track secrets that need user attention
$missingSecrets = @()

foreach ($secret in $SecretFiles) {
    $secretPath = Join-Path -Path $secretsDir -ChildPath $secret.Name

    if (Test-Path $secretPath) {
        if (Test-SecretHasContent $secretPath) {
            Write-Ok "Secret exists and has content: $($secret.Name)"
        } else {
            Write-Warn "Secret exists but appears empty or contains placeholder content: $($secret.Name)"
        }
    } else {
        if ($secret.AutoGenerate) {
            # Auto-generate this secret
            if ($secret.Name -eq 'atproto_oauth_key.json') {
                $generatedValue = Get-ES256SigningKeyJwk
            } else {
                $generatedValue = Get-RandomString
            }
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
