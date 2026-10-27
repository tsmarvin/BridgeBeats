#!/bin/bash
# BridgeBeats Installation Script
# This script downloads and configures BridgeBeats for Docker deployment
#
# Usage:
#   curl -sSL https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.sh | bash
#   curl -sSL https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.sh | bash -s -- --branch main
#   curl -sSL https://raw.githubusercontent.com/tsmarvin/BridgeBeats/develop/containers/install.sh | bash -s -- --directory /opt/bridgebeats

set -e

# =============================================================================
# Configuration
# =============================================================================
BRANCH="develop"
INSTALL_DIR="./bridgebeats"
REPO_OWNER="tsmarvin"
REPO_NAME="BridgeBeats"
TIMESTAMP=$(date +%Y%m%d-%H%M%S)
UPGRADE_LOG="upgrade.log"

# UID for the .NET container's non-root user (default APP_UID in .NET containers)
CONTAINER_UID=1654

# Files to download from the repository
DOWNLOAD_FILES=("docker-compose.yml" "Caddyfile" ".env.example" "truncate_seq.sh")

# Files to backup during upgrades (excludes .env.example as it's just a template)
BACKUP_FILES=("docker-compose.yml" "Caddyfile")

# Secret files configuration: name|description|auto_generate
SECRET_FILES=(
    "apple_key.p8|Apple Music private key (.p8 file)|false"
    "spotify_client_secret.txt|Spotify API client secret|false"
    "tidal_client_secret.txt|Tidal API client secret|false"
    "discord_token.txt|Discord bot token|false"
    "atproto_password.txt|ATProto app password|false"
    "atproto_oauth_key.json|ATProto OAuth signing key (ES256 JWK)|true"
    "api_key_salt.txt|API key salt (random string for security)|true"
    "redis_password.txt|Redis password (random string for authentication)|true"
    "internal_service_key.txt|Internal service key (worker/API authentication)|true"
    "cloudflare_api_token.txt|Cloudflare API token for DNS challenges|false"
)

# =============================================================================
# Argument Parsing
# =============================================================================
while [[ $# -gt 0 ]]; do
    case $1 in
        --branch|-b)
            BRANCH="$2"
            shift 2
            ;;
        --directory|-d)
            INSTALL_DIR="$2"
            shift 2
            ;;
        --help|-h)
            echo "BridgeBeats Installation Script"
            echo ""
            echo "Usage: install.sh [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --branch, -b      Git branch to download from (default: develop)"
            echo "  --directory, -d   Installation directory (default: ./bridgebeats)"
            echo "  --help, -h        Show this help message"
            exit 0
            ;;
        *)
            echo "[ERROR] Unknown option: $1"
            echo "Use --help for usage information"
            exit 1
            ;;
    esac
done

# Construct base URL for raw file downloads
BASE_URL="https://raw.githubusercontent.com/${REPO_OWNER}/${REPO_NAME}/${BRANCH}/containers"

# =============================================================================
# Helper Functions
# =============================================================================
print_header() {
    echo ""
    echo "=========================================="
    echo "$1"
    echo "=========================================="
    echo ""
}

print_section() {
    echo ""
    echo "--- $1 ---"
    echo ""
}

log_upgrade() {
    local message="$1"
    echo "[${TIMESTAMP}] ${message}" >> "${UPGRADE_LOG}"
}

# Check if a secret file has content (without reading the actual value)
# Uses sudo if USE_SUDO is set and file is not readable
secret_has_content() {
    local file="$1"
    if [[ -f "$file" ]]; then
        # Check if file is readable by current user
        if [[ -r "$file" ]]; then
            # Use grep to check if file has non-whitespace content
            if grep -qE '\S' "$file" 2>/dev/null; then
                # Check it's not a placeholder
                if ! grep -qE '^(your_|REPLACE_WITH_)' "$file" 2>/dev/null; then
                    return 0
                fi
            fi
        elif [[ "$USE_SUDO" == "true" ]]; then
            # File exists but not readable, use sudo
            if sudo grep -qE '\S' "$file" 2>/dev/null; then
                # Check it's not a placeholder
                if ! sudo grep -qE '^(your_|REPLACE_WITH_)' "$file" 2>/dev/null; then
                    return 0
                fi
            fi
        else
            # File exists but not readable and no sudo - assume it has valid content
            # to avoid overwriting secrets we can't read
            echo "[WARN] Cannot read ${file} (permission denied) - assuming valid content"
            return 0
        fi
    fi
    return 1
}

# Create a mode-700 scratch directory for staging secret content. The secrets directory itself
# is mode 700 and owned by the container UID on upgrades, so it is not usable as scratch space
# for the installing user even when they are permitted to sudo.
make_secure_tmp_dir() {
    local tmp_dir
    if ! tmp_dir=$(mktemp -d 2>/dev/null); then
        return 1
    fi
    if ! chmod 700 "$tmp_dir" 2>/dev/null; then
        rmdir "$tmp_dir" 2>/dev/null
        return 1
    fi
    printf '%s\n' "$tmp_dir"
}

# True when the directory cannot be used by the current user but sudo is available.
# Only the containing directory decides this: it is the mode-700 / container-UID permission on
# secrets/ that blocks access, not anything about the secret file itself.
directory_needs_sudo() {
    local dir="$1"
    if [[ -w "$dir" ]] && [[ -r "$dir" ]]; then
        return 1
    fi
    [[ "$USE_SUDO" == "true" ]]
}

# True when the secret's contents cannot be inspected at all: the directory is not searchable by
# the current user and sudo is not available. Callers must abort on this rather than fall through
# to the create path, which would report a real key as missing.
secret_is_inaccessible() {
    local file="$1"
    local dir
    dir=$(dirname "$file")
    [[ -r "$dir" && -x "$dir" ]] && return 1
    [[ "$USE_SUDO" == "true" ]] && return 1
    return 0
}

# Echo the contents of a secret, reading through sudo only when the directory requires it.
# Returns non-zero when the content cannot be read at all.
read_secret_content() {
    local file="$1"
    if directory_needs_sudo "$(dirname "$file")"; then
        sudo cat "$file" 2>/dev/null
    else
        cat "$file" 2>/dev/null
    fi
}

# True when a secret file exists. On an upgraded install the secrets directory is mode 700 and
# owned by the container UID, so the current user cannot stat inside it without sudo; treating
# that as "missing" would take the fresh-install path and discard an existing key.
secret_file_exists() {
    local file="$1"
    [[ -f "$file" ]] && return 0
    if directory_needs_sudo "$(dirname "$file")"; then
        sudo test -f "$file" 2>/dev/null && return 0
    fi
    return 1
}

# Validate ATProto signing key content by shape, without echoing any of it. Prints "ok" when the
# content is an ES256 private key the application can load, otherwise a short reason.
#
# Fail-closed: content that is neither a private JWK nor a PEM private key is rejected rather than
# accepted merely because it is non-blank. Accepting it would report a generated or preserved key
# as usable while the application refuses to start on it.
signing_key_shape() {
    local content="$1"
    local trimmed

    if [[ "$content" == *"-----BEGIN"*"PRIVATE KEY-----"* ]]; then
        printf 'ok\n'
        return 0
    fi

    # Leading whitespace is tolerated: the application trims before deciding whether the content is
    # a JWK, so rejecting it here would refuse a key the application would load.
    trimmed="${content#"${content%%[![:space:]]*}"}"
    if [[ "$trimmed" != \{* ]]; then
        printf 'not a JSON JWK or a PEM private key\n'
        return 0
    fi

    # The application requires an EC P-256 private JWK carrying all of these members. Matching the
    # quoted member names is a shape check, not a parse: it cannot confirm the values decode, so the
    # installer still leaves final validation to the application at startup.
    local member
    for member in kty crv x y d kid; do
        if [[ "$trimmed" != *"\"${member}\""* ]]; then
            printf 'JWK is missing the %s member\n' "${member}"
            return 0
        fi
    done
    if [[ "$trimmed" != *'"EC"'* ]] || [[ "$trimmed" != *'"P-256"'* ]]; then
        printf 'JWK is not an EC P-256 key\n'
        return 0
    fi

    printf 'ok\n'
}
# Classify an existing signing key as usable, unusable, or unreadable. A read failure is a
# separate outcome from unusable content: without the ability to read the file, replacing it
# could destroy a key the application is currently using and publishing.
signing_key_state() {
    local file="$1"
    local raw stripped

    # Without sudo access there is nothing to distinguish here; the read below fails and the
    # key is reported unreadable rather than usable or unusable.
    if ! raw=$(read_secret_content "$file"); then
        printf 'unreadable\n'
        return 0
    fi

    # Strip whitespace and braces one pattern at a time: a bracketed class mixing a named class
    # with literal braces does not remove the braces, which would read "{}" as usable. The stripped
    # form is only for the emptiness test; the shape check needs the original content.
    stripped="${raw//[[:space:]]/}"
    stripped="${stripped//\{/}"
    stripped="${stripped//\}/}"
    if [[ -z "$stripped" ]]; then
        printf 'unusable\n'
        return 0
    fi
    if [[ "$raw" =~ ^[[:space:]]*(your_|REPLACE_WITH_) ]]; then
        printf 'unusable\n'
        return 0
    fi

    # Non-blank is not the same as loadable. A malformed key is reported separately rather than as
    # unusable, because replacing it would discard content the operator may have intended: the
    # caller must fail with an actionable message instead of silently overwriting it.
    if [[ "$(signing_key_shape "$raw")" != "ok" ]]; then
        printf 'malformed\n'
        return 0
    fi

    printf 'usable\n'
}

# Print a manifest of a directory tree: one line per file with its path relative to the tree, its
# size, and its checksum. Used to prove a backup holds exactly what the source holds.
#
# Only metadata and checksums are emitted; file content never reaches stdout. Both trees are read
# with the same privilege as each other, so a backup created through sudo is compared with
# privileged reads rather than being read as empty by an unprivileged listing.
secrets_tree_manifest() {
    local dir="$1"
    local use_sudo="$2"

    # The inner shell receives the tree root as $1 and one or more file paths as the rest, so each
    # checksum is computed in the same process that knows the relative path.
    local snippet='
        root="$1"; shift
        for f in "$@"; do
            stats=$(cksum < "$f" 2>/dev/null) || continue
            printf "%s\t%s\n" "${f#"$root"/}" "$stats"
        done'

    if [[ "$use_sudo" == "true" ]]; then
        sudo find "$dir" -type f -exec sh -c "$snippet" sh "$dir" {} + 2>/dev/null | LC_ALL=C sort
    else
        find "$dir" -type f -exec sh -c "$snippet" sh "$dir" {} + 2>/dev/null | LC_ALL=C sort
    fi
}

# True when a directory tree needs elevated reads: either it is not searchable by the current user,
# or it is owned by another account (which is how a sudo-created copy appears).
tree_needs_sudo() {
    local dir="$1"
    directory_needs_sudo "$dir" && return 0
    [[ "$USE_SUDO" == "true" ]] && [[ "$(file_owner_uid "$dir" 2>/dev/null)" != "$(id -u)" ]]
}

# Restrict a backup tree to its owner: directories mode 700, files mode 600. `mkdir` and `cp` apply
# the process umask, so under the default 0022 a backup root lands mode 755 and copied files keep a
# world-readable mode — a private key readable by every local account. Applied to the whole tree so
# nested directories cannot keep looser permissions than the root.
restrict_backup_tree() {
    local dir="$1"
    local use_sudo="$2"

    if [[ "$use_sudo" == "true" ]]; then
        sudo chmod 700 "$dir" 2>/dev/null || return 1
        sudo find "$dir" -type d -exec chmod 700 {} + 2>/dev/null || return 1
        sudo find "$dir" -type f -exec chmod 600 {} + 2>/dev/null || return 1
    else
        chmod 700 "$dir" 2>/dev/null || return 1
        find "$dir" -type d -exec chmod 700 {} + 2>/dev/null || return 1
        find "$dir" -type f -exec chmod 600 {} + 2>/dev/null || return 1
    fi
    return 0
}

# True when no directory or file in the tree grants group or other permissions. Prints the first
# offending path and fails when one is found, so the caller can fail closed rather than report a
# backup whose permissions were never confirmed.
backup_tree_is_private() {
    local dir="$1"
    local use_sudo="$2"
    local offending
    local snippet='
        for f in "$@"; do
            mode=$(stat -c "%a" "$f" 2>/dev/null || stat -f "%Lp" "$f" 2>/dev/null) || { echo "$f"; exit 0; }
            if (( (8#$mode & 8#77) != 0 )); then echo "$f"; exit 0; fi
        done'

    if [[ "$use_sudo" == "true" ]]; then
        offending=$(sudo find "$dir" \( -type d -o -type f \) -exec sh -c "$snippet" sh {} + 2>/dev/null)
    else
        offending=$(find "$dir" \( -type d -o -type f \) -exec sh -c "$snippet" sh {} + 2>/dev/null)
    fi

    if [[ -n "$offending" ]]; then
        echo "[ERROR] Backup path ${offending} is readable beyond its owner" >&2
        return 1
    fi
    return 0
}

# Keep the three most recent backups and remove older ones. Removal failures are reported instead of
# hidden: a root-owned backup cannot be removed without privilege, and silently leaving it while
# reporting success would misstate what is actually on disk.
prune_secrets_backups() {
    local keep=3
    local failed=0 index
    local -a backups=()
    local dir

    while IFS= read -r dir; do
        [[ -n "$dir" ]] && backups+=("$dir")
    done < <(ls -dt ./secrets_backup_*/ 2>/dev/null)

    for (( index = keep; index < ${#backups[@]}; index++ )); do
        dir="${backups[$index]}"
        # Path safety: only ever operate on a direct child backup directory of the install root.
        case "$dir" in
            ./secrets_backup_*/) ;;
            *) continue ;;
        esac

        if rm -rf -- "$dir" 2>/dev/null; then
            :
        elif [[ "$USE_SUDO" == "true" ]] && sudo rm -rf -- "$dir" 2>/dev/null; then
            :
        else
            failed=$((failed + 1))
        fi
    done

    if (( failed > 0 )); then
        echo "[WARN] Could not remove ${failed} old backup director$( (( failed == 1 )) && echo y || echo ies)"
        echo "       Remove them manually if they are no longer needed: ls -dt $(pwd)/secrets_backup_*/"
        log_upgrade "WARN: ${failed} old backup director(ies) could not be removed"
    fi
    return 0
}

# Keep a timestamped copy of a secret before replacing it, and verify the copy is byte-identical.
# The caller must treat a non-zero return as "do not touch the original": replacing a live signing
# key without a readable copy would discard the only record of a published key.
# The copy stays inside the secrets directory, which docker-compose.yml mounts file by file, so no
# container ever loads it.
backup_secret_file() {
    local path="$1"
    local backup="${path}.bak.${TIMESTAMP}"
    local copied=false verified=false

    if directory_needs_sudo "$(dirname "$path")"; then
        cp -p "$path" "$backup" 2>/dev/null && copied=true
        [[ "$copied" == "true" ]] || { sudo cp -p "$path" "$backup" 2>/dev/null && copied=true; }
        cmp -s "$path" "$backup" 2>/dev/null && verified=true
        [[ "$verified" == "true" ]] || { sudo cmp -s "$path" "$backup" 2>/dev/null && verified=true; }
    else
        cp -p "$path" "$backup" 2>/dev/null && copied=true
        cmp -s "$path" "$backup" 2>/dev/null && verified=true
    fi

    if [[ "$copied" != "true" ]] || [[ "$verified" != "true" ]]; then
        echo "[ERROR] Could not back up ${path}; leaving the original untouched" >&2
        rm -f "$backup" 2>/dev/null || sudo rm -f "$backup" 2>/dev/null || true
        return 1
    fi

    # The copy sits inside a mode-700 directory, so it is already unreachable to other accounts; this
    # keeps the file itself owner-only too, matching the secret it mirrors.
    if directory_needs_sudo "$(dirname "$path")"; then
        sudo chmod 600 "$backup" 2>/dev/null || true
    else
        chmod 600 "$backup" 2>/dev/null || true
    fi

    echo "[OK] Backed up existing $(basename "$path") to $(basename "$backup")"
    return 0
}

# Echo the numeric owner UID of a file, or fail. Prefers the GNU option and falls back to BSD.
# Reads through sudo when the containing directory cannot be searched by the current user, which is
# the state an upgraded install leaves behind (mode 700, owned by the container UID). Without that
# the metadata read fails, and a caller would misread a correctly owned key as unreadable.
file_owner_uid() {
    local path="$1"
    if directory_needs_sudo "$(dirname "$path")"; then
        sudo stat -c '%u' "$path" 2>/dev/null || sudo stat -f '%u' "$path" 2>/dev/null
    else
        stat -c '%u' "$path" 2>/dev/null || stat -f '%u' "$path" 2>/dev/null
    fi
}

# Echo the permission bits of a file in octal, or fail. Prefers the GNU option and falls back to BSD.
# Reads through sudo for the same reason as file_owner_uid.
file_mode_bits() {
    local path="$1"
    if directory_needs_sudo "$(dirname "$path")"; then
        sudo stat -c '%a' "$path" 2>/dev/null || sudo stat -f '%Lp' "$path" 2>/dev/null
    else
        stat -c '%a' "$path" 2>/dev/null || stat -f '%Lp' "$path" 2>/dev/null
    fi
}

# True when the container user (UID ${CONTAINER_UID}) can read a file. Ownership is checked rather
# than attempting a read, because the installing user may not be a member of the container's UID and
# a privileged read would succeed regardless of who can actually read the file in the container.
secret_readable_by_container() {
    local path="$1"
    local owner mode

    owner=$(file_owner_uid "$path") || return 1
    if [[ "$owner" == "$CONTAINER_UID" ]]; then
        return 0
    fi

    mode=$(file_mode_bits "$path") || return 1
    # Any read permission for "other" also lets the container user read it.
    (( (8#$mode & 8#4) != 0 ))
}

# Generate a secret with the named generator function and install it only when the generated
# content is non-blank, so a failed generation cannot leave a truncated secret behind.
#
# Generated content is staged in a private scratch directory rather than beside the secret: on
# upgrades the secrets directory is mode 700 and owned by the container UID, so a temp file there
# cannot be created without elevated privileges. Replacing an existing file writes into it rather
# than renaming over it, because the container reads the key as UID ${CONTAINER_UID} through the
# docker-compose bind mount and losing that ownership would lock the application out of its key.
install_generated_secret() {
    local path="$1"
    local generator="$2"
    local dir created_new=false
    local tmp_dir tmp_file installed=false used_sudo=false

    dir=$(dirname "$path")
    if ! tmp_dir=$(make_secure_tmp_dir); then
        echo "[ERROR] Could not create a temporary directory to stage ${path}" >&2
        return 1
    fi
    tmp_file="${tmp_dir}/generated"

    if ! "$generator" > "$tmp_file" 2>/dev/null || ! grep -qE '\S' "$tmp_file" 2>/dev/null; then
        rm -rf "$tmp_dir"
        return 1
    fi
    chmod 600 "$tmp_file" 2>/dev/null || true

    if [[ -f "$path" ]]; then
        # cp onto an existing file keeps that file's owner and mode, which is what the container
        # needs in order to read the key through the bind mount.
        if directory_needs_sudo "$dir"; then
            used_sudo=true
            sudo cp "$tmp_file" "$path" 2>/dev/null && installed=true
        else
            cp "$tmp_file" "$path" 2>/dev/null && installed=true
            if [[ "$installed" != "true" ]] && [[ "$USE_SUDO" == "true" ]]; then
                used_sudo=true
                sudo cp "$tmp_file" "$path" 2>/dev/null && installed=true
            fi
        fi
    else
        created_new=true
        cp "$tmp_file" "$path" 2>/dev/null && installed=true
        if [[ "$installed" != "true" ]] && [[ "$USE_SUDO" == "true" ]]; then
            used_sudo=true
            sudo cp "$tmp_file" "$path" 2>/dev/null && installed=true
        fi
    fi

    rm -rf "$tmp_dir"

    if [[ "$installed" != "true" ]]; then
        return 1
    fi

    # An existing file keeps its own mode; one placed with sudo needs the restrictive mode applied
    # with the same privileges.
    if [[ "$used_sudo" == "true" ]]; then
        sudo chmod 600 "$path" 2>/dev/null || true
    else
        chmod 600 "$path" 2>/dev/null || true
    fi

    # A file this run created has no established owner to preserve, and a root-owned copy placed by
    # sudo would be unreadable to the container user. A fresh install corrects the whole directory
    # afterwards, but that step is skipped when the directory already existed, so ownership has to
    # be settled here for an upgrade: otherwise the installer reports a generated key the container
    # cannot read.
    if [[ "$created_new" == "true" ]]; then
        if ! secret_readable_by_container "$path" && [[ "$USE_SUDO" == "true" ]]; then
            sudo chown "${CONTAINER_UID}:${CONTAINER_UID}" "$path" 2>/dev/null || true
        fi
        if ! secret_readable_by_container "$path" && [[ "$secrets_created" != "true" ]]; then
            echo "[ERROR] $(basename "$path") is not readable by the container user (UID ${CONTAINER_UID})" >&2
            echo "        Grant the container read access or allow this script to use sudo, then re-run:" >&2
            echo "        sudo chown ${CONTAINER_UID}:${CONTAINER_UID} ${path}" >&2
            return 1
        fi
    fi
    return 0
}

# Generate a cryptographically secure random string using openssl
generate_random_string() {
    openssl rand -base64 32
}

# Generate an ES256 (P-256) signing key in JWK format
generate_es256_jwk() {
    # Generate an EC P-256 key pair with openssl
    local privkey
    privkey=$(openssl ecparam -genkey -name prime256v1 -noout 2>/dev/null)

    # Export key parameters as human-readable text
    local params
    params=$(echo "$privkey" | openssl ec -text -noout 2>/dev/null)

    # Extract the private-key hex lines (between "priv:" and "pub:") and strip colons
    local priv_hex
    priv_hex=$(echo "$params" \
        | awk '/^priv:/{found=1; next} /^pub:/{found=0} found{print}' \
        | tr -d ' :\n')

    # Extract the public-key hex lines (between "pub:" and "ASN1 OID:") and strip colons
    local pub_hex
    pub_hex=$(echo "$params" \
        | awk '/^pub:/{found=1; next} /^ASN1 OID:/{found=0} found{print}' \
        | tr -d ' :\n')

    # Validate the public key starts with 04 (uncompressed point prefix)
    if [ "${pub_hex:0:2}" != "04" ]; then
        echo "[WARN] openssl ec output format unexpected - ATProto OAuth key needs manual generation" >&2
        return 1
    fi

    # Slice x and y from the uncompressed public key (04 || x[32] || y[32])
    # Each coordinate is 32 bytes = 64 hex chars; skip the leading "04" (2 hex chars)
    local x_hex="${pub_hex:2:64}"
    local y_hex="${pub_hex:66:64}"

    # Left-pad d to exactly 32 bytes (64 hex chars) with leading zeros
    # openssl may emit fewer than 32 bytes when the leading byte(s) are zero
    local d_hex
    d_hex=$(printf '%064s' "$priv_hex" | tr ' ' '0')
    # Take the rightmost 64 hex chars in case openssl emitted more than 32 bytes
    d_hex="${d_hex: -64}"

    # base64url-encode a hex string: convert hex pairs to \xNN escapes, emit binary via
    # printf, then encode with openssl base64 (no line-wrap) and apply URL-safe charset.
    # Input is strictly validated hex [0-9a-f] — no % characters, safe as printf format.
    b64url_from_hex() {
        local hex_escapes
        hex_escapes=$(printf '%s' "$1" | sed 's/\(..\)/\\x\1/g')
        printf "$hex_escapes" | openssl base64 -A | tr '+/' '-_' | tr -d '='
    }

    local x_b64 y_b64 d_b64 kid
    x_b64=$(b64url_from_hex "$x_hex")
    y_b64=$(b64url_from_hex "$y_hex")
    d_b64=$(b64url_from_hex "$d_hex")
    kid=$(openssl rand -hex 16)

    printf '{"kty":"EC","crv":"P-256","x":"%s","y":"%s","d":"%s","kid":"%s","alg":"ES256","use":"sig"}\n' \
        "$x_b64" "$y_b64" "$d_b64" "$kid"
}

# Get environment variable names from a file
get_env_var_names() {
    local file="$1"
    grep -E '^[A-Z_][A-Z0-9_]*=' "$file" 2>/dev/null | cut -d= -f1 | sort -u
}

# Get the full line for an environment variable from a file
get_env_var_line() {
    local file="$1"
    local var_name="$2"
    grep -E "^${var_name}=" "$file" 2>/dev/null | head -1
}

# =============================================================================
# Dependency Validation
# =============================================================================
print_header "BridgeBeats Installation Script"

echo "Branch: ${BRANCH}"
echo "Install Directory: ${INSTALL_DIR}"
echo ""

print_section "Checking Dependencies"

missing_deps=()
warnings=()

# Check for Docker
if command -v docker &> /dev/null; then
    docker_version=$(docker --version 2>/dev/null || echo "unknown")
    echo "[OK] Docker: ${docker_version}"
else
    missing_deps+=("docker - Install from https://docs.docker.com/get-docker/")
fi

# Check for Docker Compose (v2 plugin)
if docker compose version &> /dev/null 2>&1; then
    compose_version=$(docker compose version --short 2>/dev/null || echo "unknown")
    echo "[OK] Docker Compose: ${compose_version}"

    # Check for v2+
    if [[ "$compose_version" =~ ^1\. ]]; then
        missing_deps+=("docker compose v2+ - Current version ${compose_version} is too old. Update Docker or install compose plugin.")
    fi
else
    missing_deps+=("docker compose - Install Docker Compose plugin: https://docs.docker.com/compose/install/")
fi

# Check for curl or wget
if command -v curl &> /dev/null; then
    echo "[OK] curl: available"
    DOWNLOAD_CMD="curl -sSL -o"
elif command -v wget &> /dev/null; then
    echo "[OK] wget: available"
    DOWNLOAD_CMD="wget -q -O"
else
    missing_deps+=("curl or wget - Required for downloading files")
fi

# Check for openssl (required for generating secure secrets and the ATProto OAuth signing key)
if command -v openssl &> /dev/null; then
    echo "[OK] openssl: available (for generating secure random secrets)"
else
    missing_deps+=("openssl - Required for generating secure secrets and the ATProto OAuth signing key. Install via your package manager, e.g. apt-get install -y openssl")
fi

# Report missing dependencies
if [[ ${#warnings[@]} -gt 0 ]]; then
    echo ""
    for warning in "${warnings[@]}"; do
        echo "[WARN] ${warning}"
    done
fi

if [[ ${#missing_deps[@]} -gt 0 ]]; then
    echo ""
    echo "[ERROR] Missing required dependencies:"
    for dep in "${missing_deps[@]}"; do
        echo "  - ${dep}"
    done
    echo ""
    echo "Please install the missing dependencies and run this script again."
    exit 1
fi

echo ""
echo "[OK] All required dependencies are available"

# =============================================================================
# Sudo Consent
# =============================================================================
print_section "Privilege Check"

USE_SUDO=false
SUDO_AVAILABLE=false

# Check if sudo is available
if command -v sudo &> /dev/null; then
    SUDO_AVAILABLE=true
fi

# Explain why sudo is needed
echo "This script may need elevated privileges (sudo) to:"
echo "  1. Set ownership of secrets directory for container access (fresh install)"
echo "  2. Set ownership of logs directory so the container can write log files"
echo "  3. Read existing secrets owned by the container user (re-run/upgrade)"
echo ""

if [[ "$SUDO_AVAILABLE" == "true" ]]; then
    read -p "Allow this script to use sudo when needed? (Y/n): " sudo_response
    if [[ ! "$sudo_response" =~ ^[Nn]$ ]]; then
        USE_SUDO=true
        echo "[OK] Sudo access granted"
    else
        echo "[OK] Sudo declined - manual commands will be shown when needed"
        echo ""
        echo "You may need to run these commands manually later:"
        echo "  sudo chown -R ${CONTAINER_UID}:${CONTAINER_UID} \$(pwd)/secrets"
        echo "  sudo chown -R ${CONTAINER_UID}:${CONTAINER_UID} \$(pwd)/logs"
    fi
else
    echo "[WARN] sudo not available on this system"
    echo ""
    echo "You may need to run these commands manually later:"
    echo "  sudo chown -R ${CONTAINER_UID}:${CONTAINER_UID} \$(pwd)/secrets"
    echo "  sudo chown -R ${CONTAINER_UID}:${CONTAINER_UID} \$(pwd)/logs"
fi

# =============================================================================
# Create Installation Directory
# =============================================================================
print_section "Setting Up Installation Directory"

if [[ -d "$INSTALL_DIR" ]]; then
    echo "[OK] Directory already exists: ${INSTALL_DIR}"
else
    mkdir -p "$INSTALL_DIR"
    echo "[OK] Created directory: ${INSTALL_DIR}"
fi

cd "$INSTALL_DIR"
echo "[OK] Working directory: $(pwd)"

# =============================================================================
# Download Configuration Files
# =============================================================================
print_section "Downloading Configuration Files"

# Helper to check if file should be backed up
should_backup() {
    local file="$1"
    for backup_file in "${BACKUP_FILES[@]}"; do
        if [[ "$file" == "$backup_file" ]]; then
            return 0
        fi
    done
    return 1
}

for file in "${DOWNLOAD_FILES[@]}"; do
    source_url="${BASE_URL}/${file}"

    # Backup existing file if it's in the backup list
    if [[ -f "$file" ]] && should_backup "$file"; then
        backup_name="${file}.bak.${TIMESTAMP}"
        cp "$file" "$backup_name"
        echo "[OK] Backed up existing ${file} to ${backup_name}"
        log_upgrade "Backed up ${file} to ${backup_name}"
    fi

    # Download the file
    if [[ "$DOWNLOAD_CMD" == "curl -sSL -o" ]]; then
        curl -sSL -o "$file" "$source_url"
    else
        wget -q -O "$file" "$source_url"
    fi

    if [[ -f "$file" ]]; then
        echo "[OK] Downloaded: ${file}"
    else
        echo "[ERROR] Failed to download: ${file}"
        exit 1
    fi
done

# Mark the trim script executable (required for direct invocation by cron)
if [[ -f "truncate_seq.sh" ]]; then
    chmod +x "truncate_seq.sh"
fi

# =============================================================================
# Setup Logs Directory
# =============================================================================
print_section "Setting Up Logs"

LOGS_DIR="./logs"

if [[ -d "$LOGS_DIR" ]]; then
    echo "[OK] Logs directory already exists: ${LOGS_DIR}"
else
    mkdir -p "$LOGS_DIR"
    echo "[OK] Created logs directory: ${LOGS_DIR}"
fi

# Set ownership of logs directory to container UID so the non-root container user can write logs.
# The container runs as UID ${CONTAINER_UID}; without this the bind-mounted ./logs stays owned by
# the host user and Serilog's file sink silently fails to write. Applied every run so existing
# installs with incorrect ownership are self-healed on upgrade.
if [[ "$USE_SUDO" == "true" ]]; then
    if sudo chown -R "${CONTAINER_UID}:${CONTAINER_UID}" "$LOGS_DIR" 2>/dev/null; then
        echo "[OK] Set logs ownership to UID ${CONTAINER_UID} (container user)"
    else
        echo "[WARN] Could not set logs ownership. Please run manually:"
        echo "       sudo chown -R ${CONTAINER_UID}:${CONTAINER_UID} $(pwd)/logs"
    fi
else
    echo ""
    echo "[ACTION REQUIRED] Set logs ownership for container access:"
    echo "  sudo chown -R ${CONTAINER_UID}:${CONTAINER_UID} $(pwd)/logs"
fi

# =============================================================================
# Setup Data Directories
# =============================================================================
print_section "Setting Up Data Directories"

# Create bind-mount targets for all persistent-data services (idempotent)
mkdir -p ./data/app ./data/dp-keys ./data/redis ./data/pds ./logs/caddy
echo "[OK] Data directories present: ./data/{app,dp-keys,redis,pds} ./logs/caddy"

# Set ownership per the actual writing UID of each service.
# Bind-mounts present host directory ownership directly to the container —
# wrong ownership causes silent persistence failures or startup crashes.
#
# Ownership table:
#   ./data/app, ./data/dp-keys  → 1654:1654 (.NET app; APP_UID=1654)
#   ./data/pds                  → 0:0 root (PDS image has no USER directive)
#   ./data/redis                → 0:0 root (compose command: override uses "sh -c …";
#                                   gosu step-down is skipped, so redis-server runs as root.
#                                   If the "sh -c" override is removed, change to 999:1000.)
#   ./logs/caddy                → 0:0 root (caddy writes access logs as root)
#
# IMPORTANT: chown ./logs/caddy specifically — never the parent ./logs tree.
# The parent ./logs is owned by ${CONTAINER_UID} (set by the Logs block above)
# so the app's Serilog file sink can write there. Chowning the whole ./logs
# tree to root would break app logging.
if [[ "$USE_SUDO" == "true" ]]; then
    if sudo chown -R "${CONTAINER_UID}:${CONTAINER_UID}" ./data/app ./data/dp-keys 2>/dev/null; then
        echo "[OK] Set ./data/app and ./data/dp-keys ownership to UID ${CONTAINER_UID} (app user)"
    else
        echo "[WARN] Could not set data/app or data/dp-keys ownership. Please run manually:"
        echo "       sudo chown -R ${CONTAINER_UID}:${CONTAINER_UID} $(pwd)/data/app $(pwd)/data/dp-keys"
    fi
    if sudo chown -R 0:0 ./data/pds ./data/redis ./logs/caddy 2>/dev/null; then
        echo "[OK] Set ./data/pds, ./data/redis, and ./logs/caddy ownership to root"
    else
        echo "[WARN] Could not set data/pds, data/redis, or logs/caddy ownership. Please run manually:"
        echo "       sudo chown -R 0:0 $(pwd)/data/pds $(pwd)/data/redis $(pwd)/logs/caddy"
    fi
else
    echo ""
    echo "[ACTION REQUIRED] Set data directory ownership for container access:"
    echo "  sudo chown -R ${CONTAINER_UID}:${CONTAINER_UID} $(pwd)/data/app $(pwd)/data/dp-keys"
    echo "  sudo chown -R 0:0 $(pwd)/data/pds $(pwd)/data/redis $(pwd)/logs/caddy"
fi

# =============================================================================
# Check for Existing Containers
# =============================================================================
print_section "Checking Existing Deployment"

SECRETS_DIR="./secrets"

# BridgeBeats container names from docker-compose.yml
BRIDGEBEATS_CONTAINERS=("bridgebeats" "bridgebeats-redis" "bridgebeats-pds" "bridgebeats-caddy")

# Check if any BridgeBeats containers exist
containers_exist=false
existing_containers=()
for container in "${BRIDGEBEATS_CONTAINERS[@]}"; do
    if docker ps -a --format '{{.Names}}' 2>/dev/null | grep -q "^${container}$"; then
        containers_exist=true
        existing_containers+=("$container")
    fi
done

if [[ "$containers_exist" == "true" ]]; then
    echo "[OK] Existing BridgeBeats containers detected:"
    for container in "${existing_containers[@]}"; do
        echo "  - ${container}"
    done

    # Log upgrade session header
    log_upgrade ""
    log_upgrade "=== Upgrade: $(date '+%Y-%m-%d %H:%M:%S') ==="
    log_upgrade "Branch: ${BRANCH}"

    # Back up secrets before any modifications. On an upgraded install the directory is mode 700 and
    # owned by the container UID, so an unprivileged copy cannot read it: the copy is attempted
    # through sudo and verified before the script reports a backup, and the prune only runs after a
    # verified copy exists. Pruning on a failed backup would delete the previous good copies and
    # leave nothing to roll back to.
    if [[ -d "$SECRETS_DIR" ]]; then
        backup_timestamp=$(date +%Y%m%d_%H%M%S)
        backup_dir="./secrets_backup_${backup_timestamp}"

        # Create the backup root with mode 700 BEFORE copying anything. `mkdir` and `cp` apply the
        # process umask, so with the common 0022 the root would otherwise be 755 and the first secret
        # copied into it would already be reachable by other local accounts for the duration of the
        # copy. The recursive hardening below still runs; this closes the window in which the tree
        # exists but is not yet restricted.
        #
        # Copy the *contents* rather than the directory: `cp -r src dst` nests the tree inside an
        # existing dst, which would put the secrets one level deeper than the backup is read from.
        copied=false
        if [[ -e "$backup_dir" ]]; then
            echo "[ERROR] Backup path already exists: ${backup_dir}"
            exit 1
        fi

        if (umask 077 && mkdir -m 700 "$backup_dir" && cp -R "$SECRETS_DIR"/. "$backup_dir"/) 2>/dev/null; then
            copied=true
        elif [[ "$USE_SUDO" == "true" ]]; then
            if sudo rm -rf "$backup_dir" 2>/dev/null \
                && sudo sh -c 'umask 077 && mkdir -m 700 "$2" && cp -R "$1"/. "$2"/' sh "$SECRETS_DIR" "$backup_dir" 2>/dev/null; then
                copied=true
            fi
        fi

        # Prove the copy holds exactly what the source holds, by comparing a path/size/checksum
        # manifest of both trees. An entry count alone cannot tell a complete copy from one that
        # missed content; the manifest is read at matching privilege so a sudo-created copy is not
        # read as empty. No secret content is printed.
        verified=false
        copy_source_unreadable=false
        if [[ "$copied" == "true" ]]; then
            manifest_privileged=false
            if tree_needs_sudo "$SECRETS_DIR" || tree_needs_sudo "$backup_dir"; then
                if [[ "$USE_SUDO" != "true" ]]; then
                    copy_source_unreadable=true
                else
                    manifest_privileged=true
                fi
            fi

            if [[ "$copy_source_unreadable" != "true" ]] \
                && restrict_backup_tree "$backup_dir" "$manifest_privileged" \
                && backup_tree_is_private "$backup_dir" "$manifest_privileged"; then
                source_manifest=$(secrets_tree_manifest "$SECRETS_DIR" "$manifest_privileged")
                copy_manifest=$(secrets_tree_manifest "$backup_dir" "$manifest_privileged")
                if [[ -n "$source_manifest" ]] && [[ "$source_manifest" == "$copy_manifest" ]]; then
                    verified=true
                fi
            fi
        fi

        if [[ "$verified" == "true" ]]; then
            echo "[OK] Backed up secrets to ${backup_dir}/"
            log_upgrade "Backed up secrets to ${backup_dir}/"

            # Prune old backups, keep 3 most recent. Only reached once the new copy is proven complete
            # and restricted to its owner, so an unverified copy is never a candidate for deletion.
            prune_secrets_backups
        else
            # Drop the partial copy: leaving it in place would let a later run mistake it for a
            # rollback point that does not actually hold every secret.
            rm -rf "$backup_dir" 2>/dev/null || sudo rm -rf "$backup_dir" 2>/dev/null || true
            echo "[ERROR] Failed to back up the secrets directory; no rollback copy was created"
            echo "        Old backups were left in place. Allow sudo or fix the permissions on $(pwd)/secrets, then re-run this script."
            log_upgrade "ERROR: Failed to back up secrets directory"
            exit 1
        fi
    fi

    # Log current container image information with RepoDigests
    echo ""
    echo "Current container images:"
    log_upgrade "Pre-update images:"

    for container in "${existing_containers[@]}"; do
        image=$(docker inspect --format="{{.Config.Image}}" "$container" 2>/dev/null || echo "unknown")
        if [[ -n "$image" && "$image" != "unknown" ]]; then
            digest=$(docker inspect --format="{{index .RepoDigests 0}}" "$image" 2>/dev/null || echo "unknown")
        else
            digest="unknown"
        fi
        echo "  ${container}: image=${image} digest=${digest}"
        log_upgrade "  ${container}: image=${image} digest=${digest}"
    done

    # Log rollback info
    compose_backup="docker-compose.yml.bak.${TIMESTAMP}"
    if [[ -f "$compose_backup" ]]; then
        log_upgrade "Rollback: Restore ${compose_backup} and run 'docker compose up -d'"
    fi

    echo ""
    echo "Image information has been logged to ${UPGRADE_LOG} for rollback reference."
    echo ""

    # Prompt for pulling new images
    read -p "Would you like to pull the latest container images? (y/N): " pull_response
    if [[ "$pull_response" =~ ^[Yy]$ ]]; then
        echo ""
        echo "Pulling latest images..."
        docker compose pull
        log_upgrade "Pulled latest images at $(date '+%H:%M:%S')"
        echo "[OK] Images updated"
    else
        log_upgrade "Image pull skipped by user"
        echo "[OK] Skipping image pull"
    fi
else
    echo "[OK] No existing containers found (fresh installation)"
fi

# =============================================================================
# Setup Secrets Directory
# =============================================================================
print_section "Setting Up Secrets"

secrets_created=false

if [[ -d "$SECRETS_DIR" ]]; then
    echo "[OK] Secrets directory already exists: ${SECRETS_DIR}"
else
    mkdir -p "$SECRETS_DIR"
    chmod 700 "$SECRETS_DIR"
    secrets_created=true
    echo "[OK] Created secrets directory: ${SECRETS_DIR}"
fi

# Track secrets that need user attention
missing_secrets=()

for secret_config in "${SECRET_FILES[@]}"; do
    IFS='|' read -r secret_name description auto_generate <<< "$secret_config"
    secret_path="${SECRETS_DIR}/${secret_name}"

    if [[ -d "$secret_path" ]]; then
        rm -rf "$secret_path"
        echo "[WARN] Removed directory at ${secret_name} (expected file)"
    fi

    # Without sudo, a mode-700 secrets directory hides its contents entirely. Abort instead of
    # reporting the key as missing and taking the create path over a real signing key.
    if secret_is_inaccessible "$secret_path"; then
        echo "[ERROR] Cannot inspect $(dirname "$secret_path") (permission denied)"
        echo "        Allow sudo or fix the directory permissions, then re-run this script."
        exit 2
    fi

    if secret_file_exists "$secret_path"; then
        # The ATProto signing key is the one secret the application refuses to start without,
        # so an unusable file must be replaced here rather than reported and left in place.
        if [[ "$secret_name" == "atproto_oauth_key.json" ]]; then
            case "$(signing_key_state "$secret_path")" in
                usable)
                    echo "[OK] Secret exists and has content: ${secret_name}"
                    ;;
                unusable)
                    echo "[WARN] ${secret_name} is empty, placeholder, or '{}' - a usable ES256 signing key is required"
                    if ! backup_secret_file "$secret_path"; then
                        echo "[ERROR] Refusing to replace ${secret_name} without a verified backup"
                        echo "        Fix the permissions on $(dirname "$secret_path") or allow sudo, then re-run this script."
                        exit 1
                    fi
                    if install_generated_secret "$secret_path" generate_es256_jwk; then
                        echo "[OK] Regenerated ${secret_name} with a new ES256 signing key"
                        log_upgrade "Regenerated ${secret_name} (previous content was unusable)"
                    else
                        echo "[ERROR] Could not generate a replacement ${secret_name}"
                        echo "        Verify openssl is working ('openssl version'), or supply an ES256 JWK manually, then re-run this script."
                        exit 1
                    fi
                    ;;
                malformed)
                    # Non-blank but not a loadable ES256 key. Replacing it could discard content the
                    # operator intended, so fail with guidance instead of overwriting.
                    echo "[ERROR] ${secret_name} is not a usable ES256 signing key"
                    echo "        It must be an ES256 private JWK (kty/crv/x/y/d/kid) or a PEM private key."
                    echo "        Review $(dirname "$secret_path")/${secret_name}, or move it aside and re-run to generate a new key."
                    exit 1
                    ;;
                *)
                    # Unreadable content is not evidence that the key is broken, and replacing it
                    # would invalidate a key the application may be publishing.
                    echo "[ERROR] Cannot read ${secret_name} to verify it"
                    echo "        Fix the permissions on $(dirname "$secret_path") or allow sudo, then re-run this script."
                    exit 1
                    ;;
            esac
        elif secret_has_content "$secret_path"; then
            echo "[OK] Secret exists and has content: ${secret_name}"
        else
            echo "[WARN] Secret exists but appears empty or contains placeholder content: ${secret_name}"
        fi
    else
        if [[ "$auto_generate" == "true" ]]; then
            # Auto-generate this secret
            if [[ "$secret_name" == "atproto_oauth_key.json" ]]; then
                if ! install_generated_secret "$secret_path" generate_es256_jwk; then
                    echo "[ERROR] Could not generate ${secret_name}"
                    echo "        Verify openssl is working ('openssl version'), or supply an ES256 JWK manually, then re-run this script."
                    exit 1
                fi
            else
                generate_random_string > "$secret_path"
                chmod 600 "$secret_path"
            fi
            echo "[OK] Auto-generated: ${secret_name}"
        else
            # Create placeholder
            if [[ "$secret_name" == "apple_key.p8" ]]; then
                cat > "$secret_path" << 'EOF'
-----BEGIN PRIVATE KEY-----
REPLACE_WITH_YOUR_APPLE_MUSIC_PRIVATE_KEY
-----END PRIVATE KEY-----
EOF
            else
                echo "your_${secret_name%.*}_here" > "$secret_path"
            fi
            chmod 600 "$secret_path"
            missing_secrets+=("${secret_name}|${description}")
            echo "[WARN] Created placeholder: ${secret_name} (needs your input)"
        fi
    fi
done

# Set ownership of secrets directory to container UID so the non-root container user can read them
if [[ "$secrets_created" == "true" ]]; then
    if [[ "$USE_SUDO" == "true" ]]; then
        echo ""
        echo "Setting ownership of secrets for container access..."
        if sudo chown -R "${CONTAINER_UID}:${CONTAINER_UID}" "$SECRETS_DIR" 2>/dev/null; then
            echo "[OK] Set secrets ownership to UID ${CONTAINER_UID} (container user)"
        else
            echo "[WARN] Could not set secrets ownership. Please run manually:"
            echo "       sudo chown -R ${CONTAINER_UID}:${CONTAINER_UID} $(pwd)/secrets"
        fi
    else
        echo ""
        echo "[ACTION REQUIRED] Set secrets ownership for container access:"
        echo "  sudo chown -R ${CONTAINER_UID}:${CONTAINER_UID} $(pwd)/secrets"
    fi
fi

# =============================================================================
# Setup Environment File
# =============================================================================
print_section "Configuring Environment"

if [[ -f ".env" ]]; then
    echo "[OK] Environment file exists: .env"

    # Check for new variables in .env.example
    if [[ -f ".env.example" ]]; then
        existing_vars=$(get_env_var_names ".env")
        example_vars=$(get_env_var_names ".env.example")

        new_vars=()
        while IFS= read -r var; do
            if ! echo "$existing_vars" | grep -q "^${var}$"; then
                new_vars+=("$var")
            fi
        done <<< "$example_vars"

        if [[ ${#new_vars[@]} -gt 0 ]]; then
            echo ""
            echo "[WARN] New environment variables found in .env.example:"
            echo "       Please add these to your .env file:"
            echo ""
            for var in "${new_vars[@]}"; do
                line=$(get_env_var_line ".env.example" "$var")
                echo "  ${line}"
            done
            echo ""
        fi
    fi
else
    if [[ -f ".env.example" ]]; then
        cp ".env.example" ".env"
        echo "[OK] Created .env from .env.example"
        echo "[WARN] Please edit .env with your configuration values"
    else
        echo "[ERROR] No .env.example found to create .env from"
        exit 1
    fi
fi


# =============================================================================
# Setup PDS Sequencer Trim
# =============================================================================
print_section "Setting Up PDS Sequencer Trim"

# The PDS sequencer.sqlite grows unbounded at bot-write volume (repo_seq table
# is never trimmed by the stock PDS). A daily host-cron job deletes rows older
# than 14 days (336 hours) using the vendored truncate_seq.sh script.
# The trim runs directly on the host using the host sqlite3 binary.
# Requires: GNU date and sqlite3 on the host (apt-get install -y sqlite3).

DEPLOY_DIR="$(pwd)"
TRIM_SCRIPT="${DEPLOY_DIR}/truncate_seq.sh"
TRIM_DB="${DEPLOY_DIR}/data/pds/sequencer.sqlite"
TRIM_LOG="${DEPLOY_DIR}/logs/pds-trim.log"
CRON_FILE="/etc/cron.d/bridgebeats-pds-trim"

CRON_CONTENT="# BridgeBeats PDS sequencer trim — 14-day (336h) retention. The PDS repo_seq table
# grows unbounded at bot-write volume; this daily job bounds it to a 14-day window.
# Managed by install.sh; re-running install.sh overwrites this file (no duplicate entries).
SHELL=/bin/sh
17 4 * * * root ${TRIM_SCRIPT} ${TRIM_DB} 336 >> ${TRIM_LOG} 2>&1"

CRON_AVAILABLE=false
if [[ -d "/etc/cron.d" ]]; then
    CRON_AVAILABLE=true
fi

if [[ "$USE_SUDO" == "true" ]] && [[ "$CRON_AVAILABLE" == "true" ]]; then
    # Ensure sqlite3 is present on the host
    if ! command -v sqlite3 &> /dev/null; then
        echo "sqlite3 not found — installing..."
        sudo apt-get update -qq
        if sudo apt-get install -y sqlite3; then
            echo "[OK] Installed sqlite3"
        else
            echo "[WARN] Could not install sqlite3 automatically. Please install it manually:"
            echo "       sudo apt-get install -y sqlite3"
        fi
    else
        echo "[OK] sqlite3 is available"
    fi

    # Register the cron entry by overwriting the fixed file (idempotent — no duplicate schedules)
    if echo "${CRON_CONTENT}" | sudo tee "${CRON_FILE}" > /dev/null; then
        sudo chmod 0644 "${CRON_FILE}"
        echo "[OK] Registered daily PDS sequencer trim (14-day retention) via ${CRON_FILE}"
    else
        echo "[WARN] Could not write ${CRON_FILE}. Please create it manually (see ACTION REQUIRED below)."
    fi
else
    if [[ "$CRON_AVAILABLE" == "false" ]]; then
        echo "[WARN] /etc/cron.d not found — cron may not be available on this system"
    fi
    echo ""
    echo "[ACTION REQUIRED] Register the PDS sequencer trim cron entry manually:"
    echo "  Ensure sqlite3 is installed:   sudo apt-get install -y sqlite3"
    echo "  Create ${CRON_FILE} with the following content:"
    echo ""
    echo "${CRON_CONTENT}"
    echo ""
fi

# =============================================================================
# Summary and Next Steps
# =============================================================================
print_header "Installation Complete"

echo "Installation directory: $(pwd)"
echo ""

# Report secrets that need attention
if [[ ${#missing_secrets[@]} -gt 0 ]]; then
    echo "[ACTION REQUIRED] The following secrets need your input:"
    echo ""
    for secret_info in "${missing_secrets[@]}"; do
        IFS='|' read -r name desc <<< "$secret_info"
        echo "  - secrets/${name}"
        echo "    ${desc}"
        echo ""
    done
fi

# Check .env for empty required values
echo "Checking environment configuration..."
env_issues=()

# Required environment variables to check
required_env_vars=(
    "DOMAIN"
    "CADDY_ADMIN_EMAIL"
    "PDS_HOSTNAME"
    "PDS_JWT_SECRET"
    "PDS_ADMIN_PASSWORD"
)

# At least one provider required
provider_vars=(
    "APPLE_TEAM_ID"
    "SPOTIFY_CLIENT_ID"
    "TIDAL_CLIENT_ID"
)

for var in "${required_env_vars[@]}"; do
    if grep -qE "^${var}=\s*$" ".env" 2>/dev/null; then
        env_issues+=("${var} - Required but empty")
    fi
done

provider_found=false
for var in "${provider_vars[@]}"; do
    if grep -qE "^${var}=.+$" ".env" 2>/dev/null; then
        if ! grep -qE "^${var}=\s*$" ".env" 2>/dev/null; then
            provider_found=true
            break
        fi
    fi
done

if [[ "$provider_found" == "false" ]]; then
    env_issues+=("At least one music provider required (APPLE_TEAM_ID, SPOTIFY_CLIENT_ID, or TIDAL_CLIENT_ID)")
fi

if [[ ${#env_issues[@]} -gt 0 ]]; then
    echo ""
    echo "[ACTION REQUIRED] Environment configuration issues:"
    echo ""
    for issue in "${env_issues[@]}"; do
        echo "  - ${issue}"
    done
    echo ""
    echo "Edit .env to configure these values."
fi

echo ""
echo "=========================================="
echo "Next Steps"
echo "=========================================="
echo ""
echo "1. Edit secrets in: $(pwd)/secrets/"
echo "2. Edit environment in: $(pwd)/.env"
echo "3. Start BridgeBeats with:"
echo ""
echo "   cd $(pwd)"
echo "   docker compose up -d"
echo ""
echo "For more information, see:"
echo "  https://github.com/${REPO_OWNER}/${REPO_NAME}/blob/${BRANCH}/docs/DEPLOYMENT.md"
echo ""
