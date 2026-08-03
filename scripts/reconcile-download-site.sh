#!/usr/bin/env bash
# Reconcile the live Azure download site with published GitHub releases.
# GitHub is authoritative: Azure-only v<version>/ trees are copied to the
# archive namespace at the Archive access tier, then removed from the live site.

set -euo pipefail

usage() {
    cat <<'EOF'
Usage: reconcile-download-site.sh --account NAME [options]

Options:
  --repository OWNER/REPO  GitHub repository (default: $GITHUB_REPOSITORY)
  --container NAME         Blob container (default: $web)
    --exclude-tag TAG        Treat a just-deleted release tag as absent
  --archive-prefix PREFIX  Archive namespace (default: archive)
  --archive-tier TIER      Azure destination tier (default: Archive)
  --afd-resource-group RG --afd-profile NAME --afd-endpoint NAME
                           Purge removed and mutable Front Door paths
  --dry-run                Resolve and print the plan without changing Azure
  -h, --help               Show this help

Requires GH_TOKEN, an authenticated Azure CLI session, and Blob Data
Contributor access to the container.
EOF
}

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*" >&2; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

REPOSITORY="${GITHUB_REPOSITORY:-}"
ACCOUNT="${WINMATSCH_STORAGE_ACCOUNT:-}"
CONTAINER="${WINMATSCH_WEB_CONTAINER:-\$web}"
EXCLUDE_TAG=""
ARCHIVE_PREFIX="archive"
ARCHIVE_TIER="Archive"
AFD_RG="" AFD_PROFILE="" AFD_ENDPOINT=""
DRY_RUN=0

while [ $# -gt 0 ]; do
    case "$1" in
        --repository)         REPOSITORY="${2:?}"; shift 2 ;;
        --account)            ACCOUNT="${2:?}"; shift 2 ;;
        --container)          CONTAINER="${2:?}"; shift 2 ;;
        --exclude-tag)        EXCLUDE_TAG="${2:?}"; shift 2 ;;
        --archive-prefix)     ARCHIVE_PREFIX="${2:?}"; shift 2 ;;
        --archive-tier)       ARCHIVE_TIER="${2:?}"; shift 2 ;;
        --afd-resource-group) AFD_RG="${2:?}"; shift 2 ;;
        --afd-profile)        AFD_PROFILE="${2:?}"; shift 2 ;;
        --afd-endpoint)       AFD_ENDPOINT="${2:?}"; shift 2 ;;
        --dry-run)            DRY_RUN=1; shift ;;
        -h|--help)            usage; exit 0 ;;
        *)                    usage >&2; die "unknown argument: $1" ;;
    esac
done

[ -n "$REPOSITORY" ] || die "--repository (or GITHUB_REPOSITORY) is required"
[ -n "$ACCOUNT" ] || die "--account (or WINMATSCH_STORAGE_ACCOUNT) is required"
[ -n "${GH_TOKEN:-}" ] || die "GH_TOKEN is required"
[[ "$REPOSITORY" =~ ^[^/]+/[^/]+$ ]] || die "repository must have OWNER/REPO form"
if [ -n "$EXCLUDE_TAG" ]; then
    [[ "$EXCLUDE_TAG" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$ ]] \
        || die "excluded tag is not a canonical release tag: $EXCLUDE_TAG"
fi
[[ "$ARCHIVE_PREFIX" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]*$ ]] \
    || die "archive prefix contains unsupported characters"
ARCHIVE_PREFIX="${ARCHIVE_PREFIX%/}"
case "$ARCHIVE_TIER" in
    Archive|Cold|Cool) ;;
    *) die "archive tier must be Archive, Cold, or Cool" ;;
esac

for command in gh jq az sha256sum; do
    command -v "$command" >/dev/null || die "$command not found"
done
PYTHON="$(command -v python3 || command -v python)" || die "python3 not found"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MANIFEST_TOOL="$SCRIPT_DIR/manage-release-versions.py"
[ -f "$MANIFEST_TOOL" ] || die "missing $MANIFEST_TOOL"

STAGING="$(mktemp -d)"
cleanup() { rm -rf "$STAGING"; }
trap cleanup EXIT

AZ_AUTH=(--auth-mode login --only-show-errors)
RELEASES_JSON="$STAGING/github-releases.json"
CURRENT_MANIFEST="$STAGING/current-versions.json"
UPDATED_MANIFEST="$STAGING/versions.json"
STATE_JSON="$STAGING/reconcile-state.json"
STORAGE_NAMES="$STAGING/storage-names.txt"
STORAGE_TAGS="$STAGING/storage-tags.txt"
DELETE_RECORDS="$STAGING/delete-records.tsv"
: >"$DELETE_RECORDS"

# --------------------------------------------------------------- GitHub state

log "Reading published releases from GitHub ($REPOSITORY)"
gh api --paginate -X GET "repos/$REPOSITORY/releases?per_page=100" \
    | jq -s --arg excluded "$EXCLUDE_TAG" '[.[][] | select(.draft == false and .tag_name != $excluded) | {
        tagName: .tag_name,
        isPrerelease: .prerelease,
        publishedAt: .published_at,
        url: .html_url
    }]' >"$RELEASES_JSON"

LATEST_TAG=""
if gh api "repos/$REPOSITORY/releases/latest" >"$STAGING/github-latest.json" 2>"$STAGING/github-latest.err"; then
    LATEST_TAG="$(jq -er '.tag_name' "$STAGING/github-latest.json")"
elif grep -Eq 'HTTP 404|Not Found' "$STAGING/github-latest.err"; then
    log "GitHub has no latest stable release"
else
    cat "$STAGING/github-latest.err" >&2
    die "could not resolve GitHub's latest release"
fi

# ---------------------------------------------------------------- Azure state

# This optimistic transaction is serialized by the workflow's
# azure-download-site concurrency group; the ETag still fails closed if an
# operator or another writer changes the catalog outside that workflow.
log "Reading the live Azure catalog and version prefixes"
MANIFEST_ETAG="$(az storage blob show "${AZ_AUTH[@]}" \
    --account-name "$ACCOUNT" --container-name "$CONTAINER" \
    --name versions.json --query properties.etag --output tsv)"
[ -n "$MANIFEST_ETAG" ] || die "live versions.json has no ETag"
az storage blob download "${AZ_AUTH[@]}" \
    --account-name "$ACCOUNT" --container-name "$CONTAINER" \
    --name versions.json --file "$CURRENT_MANIFEST" --no-progress \
    --if-match "$MANIFEST_ETAG" --overwrite --output none
az storage blob list "${AZ_AUTH[@]}" \
    --account-name "$ACCOUNT" --container-name "$CONTAINER" \
    --num-results '*' --query '[].name' --output tsv >"$STORAGE_NAMES"
awk -F/ 'NF >= 2 { print $1 }' "$STORAGE_NAMES" | LC_ALL=C sort -u >"$STORAGE_TAGS"

UPDATED="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
"$PYTHON" "$MANIFEST_TOOL" reconcile-manifest \
    --manifest-in "$CURRENT_MANIFEST" \
    --github-releases "$RELEASES_JSON" \
    --github-latest-tag "$LATEST_TAG" \
    --storage-tags "$STORAGE_TAGS" \
    --updated "$UPDATED" \
    --manifest-out "$UPDATED_MANIFEST" \
    --state-out "$STATE_JSON"

mapfile -t ARCHIVE_TAGS < <(jq -r '.archiveTags[]' "$STATE_JSON")
GITHUB_RELEASE_COUNT="$(jq 'length' "$RELEASES_JSON")"
MANIFEST_CHANGED="$(jq -r '.manifestChanged' "$STATE_JSON")"
DESIRED_LATEST="$(jq -r '.latest // empty' "$STATE_JSON")"

log "Plan: $GITHUB_RELEASE_COUNT published release(s), latest=${DESIRED_LATEST:-<none>}, archive=${#ARCHIVE_TAGS[@]}"
if [ "${#ARCHIVE_TAGS[@]}" -gt 0 ]; then
    printf '  %s\n' "${ARCHIVE_TAGS[@]}" >&2
fi

if [ "$DRY_RUN" -eq 1 ]; then
    log "Dry run: no Azure changes were made"
    exit 0
fi

if [ "${#ARCHIVE_TAGS[@]}" -gt 0 ] && \
   { [ -z "$AFD_RG" ] || [ -z "$AFD_PROFILE" ] || [ -z "$AFD_ENDPOINT" ]; }; then
    die "archiving live releases requires all three --afd-* options so immutable CDN paths can be purged"
fi

# --------------------------------------------------------------- archive copy

archive_version() {
    local tag="$1" source_json="$STAGING/sources-$1.json" source_count
    log "Archiving /$tag/ to /$ARCHIVE_PREFIX/$tag/ at the $ARCHIVE_TIER tier"
    az storage blob list "${AZ_AUTH[@]}" \
        --account-name "$ACCOUNT" --container-name "$CONTAINER" \
        --prefix "$tag/" --num-results '*' \
        --query '[].{name:name,etag:properties.etag,size:properties.contentLength}' \
        --output json >"$source_json"
    source_count="$(jq 'length' "$source_json")"
    [ "$source_count" -gt 0 ] || die "live prefix /$tag/ disappeared during reconciliation"

    while IFS=$'\t' read -r source_name source_etag source_size; do
        local local_path="$STAGING/live/$source_name"
        local destination_name="$ARCHIVE_PREFIX/$source_name"
        local digest destination_exists destination_json destination_size destination_digest destination_tier

        mkdir -p "$(dirname "$local_path")"
        az storage blob download "${AZ_AUTH[@]}" \
            --account-name "$ACCOUNT" --container-name "$CONTAINER" \
            --name "$source_name" --file "$local_path" --no-progress \
            --if-match "$source_etag" --overwrite --output none
        [ "$(wc -c <"$local_path" | tr -d '[:space:]')" = "$source_size" ] \
            || die "downloaded size changed for /$source_name"
        digest="$(sha256sum "$local_path" | cut -d ' ' -f 1)"

        destination_exists="$(az storage blob exists "${AZ_AUTH[@]}" \
            --account-name "$ACCOUNT" --container-name "$CONTAINER" \
            --name "$destination_name" --query exists --output tsv)"
        if [ "$destination_exists" != "true" ]; then
            log "  /$destination_name"
            az storage blob upload "${AZ_AUTH[@]}" \
                --account-name "$ACCOUNT" --container-name "$CONTAINER" \
                --file "$local_path" --name "$destination_name" \
                --overwrite false --if-none-match '*' --tier "$ARCHIVE_TIER" \
                --metadata "sha256=$digest" --no-progress --output none
        fi

        destination_json="$(az storage blob show "${AZ_AUTH[@]}" \
            --account-name "$ACCOUNT" --container-name "$CONTAINER" \
            --name "$destination_name" \
            --query '{size:properties.contentLength,digest:metadata.sha256,tier:properties.blobTier}' \
            --output json)"
        destination_size="$(jq -r '.size' <<<"$destination_json")"
        destination_digest="$(jq -r '.digest // empty' <<<"$destination_json")"
        destination_tier="$(jq -r '.tier // empty' <<<"$destination_json")"
        [ "$destination_size" = "$source_size" ] \
            || die "archive size does not match /$source_name"
        [ "$destination_digest" = "$digest" ] \
            || die "archive digest does not match /$source_name"
        [ "$destination_tier" = "$ARCHIVE_TIER" ] \
            || die "/$destination_name is in tier '$destination_tier', expected '$ARCHIVE_TIER'"

        printf '%s\t%s\n' "$source_name" "$source_etag" >>"$DELETE_RECORDS"
    done < <(jq -r '.[] | [.name, .etag, (.size | tostring)] | @tsv' "$source_json")
}

for tag in "${ARCHIVE_TAGS[@]}"; do
    archive_version "$tag"
done

# ------------------------------------------------------------ catalog update

if [ "$MANIFEST_CHANGED" = "true" ]; then
    log "Updating /versions.json at ETag $MANIFEST_ETAG"
    az storage blob upload "${AZ_AUTH[@]}" \
        --account-name "$ACCOUNT" --container-name "$CONTAINER" \
        --file "$UPDATED_MANIFEST" --name versions.json --overwrite \
        --content-type 'application/json; charset=utf-8' \
        --content-cache 'public, max-age=300, must-revalidate' \
        --if-match "$MANIFEST_ETAG" --no-progress --output none
else
    log "/versions.json already matches GitHub"
fi

# -------------------------------------------------------------- latest mirror

ACTUAL_LATEST=""
latest_version_exists="$(az storage blob exists "${AZ_AUTH[@]}" \
    --account-name "$ACCOUNT" --container-name "$CONTAINER" \
    --name latest/version.txt --query exists --output tsv)"
if [ "$latest_version_exists" = "true" ]; then
    az storage blob download "${AZ_AUTH[@]}" \
        --account-name "$ACCOUNT" --container-name "$CONTAINER" \
        --name latest/version.txt --file "$STAGING/current-latest.txt" \
        --no-progress --overwrite --output none
    ACTUAL_LATEST="$(tr -d '\r\n' <"$STAGING/current-latest.txt")"
fi

build_expected_latest_names() {
    if [ -z "$DESIRED_LATEST" ]; then
        return
    fi
    jq -r --arg version "$DESIRED_LATEST" '
        .versions[] | select(.version == $version) | .artifacts[] |
        "winmatsch-" + .rid + (if .os == "windows" then ".exe" else "" end)
    ' "$UPDATED_MANIFEST"
    printf '%s\n' LICENSE THIRD-PARTY-NOTICES.txt index.html SHA256SUMS.txt version.txt latest.json
}

build_expected_latest_names | LC_ALL=C sort >"$STAGING/expected-latest-names.txt"
az storage blob list "${AZ_AUTH[@]}" \
    --account-name "$ACCOUNT" --container-name "$CONTAINER" \
    --prefix latest/ --num-results '*' --query '[].name' --output tsv \
    | sed 's#^latest/##' | LC_ALL=C sort >"$STAGING/actual-latest-names.txt"

LATEST_NEEDS_REFRESH=0
if [ "$ACTUAL_LATEST" != "$DESIRED_LATEST" ] || \
   ! diff -q "$STAGING/expected-latest-names.txt" "$STAGING/actual-latest-names.txt" >/dev/null; then
    LATEST_NEEDS_REFRESH=1
fi

content_type_for() {
    case "$1" in
        *.html)        printf '%s' 'text/html; charset=utf-8' ;;
        *.json)        printf '%s' 'application/json; charset=utf-8' ;;
        *.txt|LICENSE) printf '%s' 'text/plain; charset=utf-8' ;;
        *)             printf '%s' 'application/octet-stream' ;;
    esac
}

if [ "$LATEST_NEEDS_REFRESH" -eq 1 ] && [ -n "$DESIRED_LATEST" ]; then
    log "Refreshing /latest/ from GitHub latest v$DESIRED_LATEST"
    VERSION_DIR="$STAGING/latest-source"
    LATEST_DIR="$STAGING/latest"
    mkdir -p "$VERSION_DIR" "$LATEST_DIR"
    while IFS= read -r source_name; do
        az storage blob download "${AZ_AUTH[@]}" \
            --account-name "$ACCOUNT" --container-name "$CONTAINER" \
            --name "v$DESIRED_LATEST/$source_name" --file "$VERSION_DIR/$source_name" \
            --no-progress --overwrite --output none
    done < <(
        jq -r --arg version "$DESIRED_LATEST" \
            '.versions[] | select(.version == $version) | .artifacts[].name' "$UPDATED_MANIFEST"
        printf '%s\n' LICENSE THIRD-PARTY-NOTICES.txt index.html
    )
    "$PYTHON" "$MANIFEST_TOOL" stage-latest \
        --manifest "$UPDATED_MANIFEST" --version-dir "$VERSION_DIR" --out "$LATEST_DIR"

    while IFS= read -r local_path; do
        name="$(basename "$local_path")"
        type="$(content_type_for "$name")"
        args=(--account-name "$ACCOUNT" --container-name "$CONTAINER"
              --file "$local_path" --name "latest/$name" --overwrite
              --content-type "$type" --content-cache 'public, max-age=300, must-revalidate'
              --no-progress)
        [[ "$name" == winmatsch-* ]] && args+=(--content-disposition attachment)
        log "  /latest/$name"
        az storage blob upload "${AZ_AUTH[@]}" "${args[@]}" --output none
    done < <(find "$LATEST_DIR" -mindepth 1 -maxdepth 1 -type f | LC_ALL=C sort)
elif [ "$LATEST_NEEDS_REFRESH" -eq 1 ]; then
    log "Removing /latest/ because GitHub has no latest stable release"
else
    log "/latest/ already matches GitHub latest v${DESIRED_LATEST:-<none>}"
fi

if [ "$LATEST_NEEDS_REFRESH" -eq 1 ]; then
    az storage blob list "${AZ_AUTH[@]}" \
        --account-name "$ACCOUNT" --container-name "$CONTAINER" \
        --prefix latest/ --num-results '*' --query '[].name' --output tsv \
        >"$STAGING/latest-after-upload.txt"
    while IFS= read -r blob_name; do
        short_name="${blob_name#latest/}"
        if ! grep -Fxq "$short_name" "$STAGING/expected-latest-names.txt"; then
            log "Removing stale /$blob_name"
            az storage blob delete "${AZ_AUTH[@]}" \
                --account-name "$ACCOUNT" --container-name "$CONTAINER" \
                --name "$blob_name" --delete-snapshots include --output none
        fi
    done <"$STAGING/latest-after-upload.txt"
fi

# ---------------------------------------------------------- remove live copies

while IFS=$'\t' read -r source_name source_etag; do
    [ -n "$source_name" ] || continue
    log "Removing archived live blob /$source_name"
    az storage blob delete "${AZ_AUTH[@]}" \
        --account-name "$ACCOUNT" --container-name "$CONTAINER" \
        --name "$source_name" --if-match "$source_etag" \
        --delete-snapshots include --output none
done <"$DELETE_RECORDS"

# ------------------------------------------------------------------ CDN purge

if [ -n "$AFD_RG" ] && [ -n "$AFD_PROFILE" ] && [ -n "$AFD_ENDPOINT" ]; then
    AFD_PATHS=("/" "/index.html" "/404.html" "/versions.json" "/latest" "/latest/" "/latest/*")
    for tag in "${ARCHIVE_TAGS[@]}"; do
        version="${tag#v}"
        AFD_PATHS+=("/$tag" "/$tag/" "/$tag/*" "/$version" "/$version/")
    done
    log "Purging ${#AFD_PATHS[@]} Front Door paths"
    az afd endpoint purge --only-show-errors \
        --resource-group "$AFD_RG" --profile-name "$AFD_PROFILE" \
        --endpoint-name "$AFD_ENDPOINT" --content-paths "${AFD_PATHS[@]}" \
        >/dev/null
elif [ -n "$AFD_RG$AFD_PROFILE$AFD_ENDPOINT" ]; then
    warn "Front Door purge skipped: need all three --afd-* options"
fi

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    {
        printf '## Azure release mirror reconciled\n\n'
        printf '| | |\n| --- | --- |\n'
        printf '| GitHub releases | %s published |\n' "$GITHUB_RELEASE_COUNT"
        printf '| GitHub latest | `%s` |\n' "${LATEST_TAG:-none}"
        printf '| Azure live latest | `%s` |\n' "${DESIRED_LATEST:-none}"
        if [ "${#ARCHIVE_TAGS[@]}" -gt 0 ]; then
            printf '| Archived | `%s` |\n' "$(IFS=', '; printf '%s' "${ARCHIVE_TAGS[*]}")"
        else
            printf '| Archived | none |\n'
        fi
        printf '| Archive destination | `%s/%s/` (%s tier) |\n\n' \
            "$CONTAINER" "$ARCHIVE_PREFIX" "$ARCHIVE_TIER"
    } >>"$GITHUB_STEP_SUMMARY"
fi

log "Azure now mirrors published GitHub releases"