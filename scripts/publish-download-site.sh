#!/usr/bin/env bash
# Publish a winmatsch release to the Azure Storage static-website download site.
#
# Layout produced in the $web container (see docs/download-site.md):
#   /index.html /404.html /versions.json          short-TTL entry points
#   /v<version>/...                               immutable canonical release
#   /latest/...                                   stable unversioned mirror
#
# The script stages everything locally first, verifies checksums, then
# uploads with az. --dry-run stops after staging so the layout can be
# inspected without touching Azure.
#
# Requirements: bash, python3, sha256sum, az (unless --dry-run).
# Auth: uses --auth-mode login (RBAC: Storage Blob Data Contributor) unless
# AZURE_STORAGE_CONNECTION_STRING or AZURE_STORAGE_KEY is set.

set -euo pipefail

usage() {
    cat <<'EOF'
Usage: publish-download-site.sh --version <v0.9.0> --dist <dir> [options]

Required:
  --version VER          Release version (tag form v0.9.0 or plain 0.9.0)
  --dist DIR             Directory with release artifacts (the 6 binaries,
                         SHA256SUMS.txt, LICENSE, THIRD-PARTY-NOTICES.txt)
  --account NAME         Storage account name (or env WINMATSCH_STORAGE_ACCOUNT)
                         [not required with --dry-run]

Options:
  --container NAME       Blob container (default: $web)
  --date ISO8601         Release date (default: now, UTC)
  --prerelease           Force pre-release flag (auto-detected from version)
  --notes-url URL        Release notes link (default: GitHub release for tag)
  --manifest-in FILE     Use local versions.json instead of downloading
  --staging DIR          Staging directory (default: temp dir)
  --skip-latest          Do not refresh the /latest/ mirror
  --dry-run              Stage and verify only; no az calls
  --afd-resource-group RG --afd-profile NAME --afd-endpoint NAME
                         Purge Front Door cache after upload (all three needed)
  -h, --help             Show this help
EOF
}

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*" >&2; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------- arguments

VERSION="" DIST="" ACCOUNT="${WINMATSCH_STORAGE_ACCOUNT:-}"
CONTAINER="${WINMATSCH_WEB_CONTAINER:-\$web}"
DATE="" PRERELEASE=0 NOTES_URL="" MANIFEST_IN="" STAGING="" DRY_RUN=0 SKIP_LATEST=0
AFD_RG="" AFD_PROFILE="" AFD_ENDPOINT=""

while [ $# -gt 0 ]; do
    case "$1" in
        --version)            VERSION="${2:?}"; shift 2 ;;
        --dist)               DIST="${2:?}"; shift 2 ;;
        --account)            ACCOUNT="${2:?}"; shift 2 ;;
        --container)          CONTAINER="${2:?}"; shift 2 ;;
        --date)               DATE="${2:?}"; shift 2 ;;
        --prerelease)         PRERELEASE=1; shift ;;
        --notes-url)          NOTES_URL="${2:?}"; shift 2 ;;
        --manifest-in)        MANIFEST_IN="${2:?}"; shift 2 ;;
        --staging)            STAGING="${2:?}"; shift 2 ;;
        --skip-latest)        SKIP_LATEST=1; shift ;;
        --dry-run)            DRY_RUN=1; shift ;;
        --afd-resource-group) AFD_RG="${2:?}"; shift 2 ;;
        --afd-profile)        AFD_PROFILE="${2:?}"; shift 2 ;;
        --afd-endpoint)       AFD_ENDPOINT="${2:?}"; shift 2 ;;
        -h|--help)            usage; exit 0 ;;
        *)                    usage >&2; die "unknown argument: $1" ;;
    esac
done

[ -n "$VERSION" ] || { usage >&2; die "--version is required"; }
[ -n "$DIST" ]    || { usage >&2; die "--dist is required"; }
[ -d "$DIST" ]    || die "dist directory not found: $DIST"
if [ "$DRY_RUN" -eq 0 ] && [ -z "$ACCOUNT" ]; then
    die "--account (or WINMATSCH_STORAGE_ACCOUNT) is required unless --dry-run"
fi

PYTHON="$(command -v python3 || command -v python)" || die "python3 not found"
command -v sha256sum >/dev/null || die "sha256sum not found"
if [ "$DRY_RUN" -eq 0 ]; then
    command -v az >/dev/null || die "az CLI not found (use --dry-run to stage locally)"
fi

AZ_AUTH=()
if [ -z "${AZURE_STORAGE_CONNECTION_STRING:-}" ] && [ -z "${AZURE_STORAGE_KEY:-}" ]; then
    AZ_AUTH=(--auth-mode login)
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SITE_INDEX="$REPO_ROOT/site/index.html"
MANIFEST_TOOL="$SCRIPT_DIR/update-versions-manifest.py"
[ -f "$SITE_INDEX" ]    || die "missing $SITE_INDEX"
[ -f "$MANIFEST_TOOL" ] || die "missing $MANIFEST_TOOL"

VER="${VERSION#v}"; VER="${VER#V}"
TAG="v$VER"
DATE="${DATE:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"
NOTES_URL="${NOTES_URL:-https://github.com/b0t-at/winmatsch/releases/tag/$TAG}"

if [ -z "$STAGING" ]; then
    STAGING="$(mktemp -d)"
    KEEP_STAGING="$DRY_RUN"   # keep temp staging around for inspection on dry runs
else
    mkdir -p "$STAGING"
    KEEP_STAGING=1
fi
cleanup() { [ "${KEEP_STAGING:-1}" -eq 1 ] || rm -rf "$STAGING"; }
trap cleanup EXIT

# ------------------------------------------------------------- verify dist

RIDS=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
SUMS="$DIST/SHA256SUMS.txt"
[ -f "$SUMS" ] || die "missing $SUMS"

binary_name() {
    local rid="$1" ext=""
    case "$rid" in win-*) ext=".exe" ;; esac
    printf 'winmatsch-%s-%s%s' "$TAG" "$rid" "$ext"
}

log "Verifying release artifacts in $DIST"
MISSING=0
for rid in "${RIDS[@]}"; do
    name="$(binary_name "$rid")"
    if [ ! -s "$DIST/$name" ]; then
        warn "missing or empty: $name"
        MISSING=1
    elif ! grep -Fq " $name" "$SUMS" && ! grep -Fq "*$name" "$SUMS"; then
        warn "no checksum entry for $name in SHA256SUMS.txt"
        MISSING=1
    fi
done
[ "$MISSING" -eq 0 ] || die "dist directory is incomplete for $TAG"
for extra in LICENSE THIRD-PARTY-NOTICES.txt; do
    [ -f "$DIST/$extra" ] || die "missing $DIST/$extra"
done
(cd "$DIST" && sha256sum --check --quiet --ignore-missing SHA256SUMS.txt) \
    || die "checksum verification failed"
log "Checksums OK"

# --------------------------------------------------------------- manifest

# Build the artifact metadata (rid/os/arch/size/sha256) as JSON.
ARTIFACTS_JSON="$STAGING/.artifacts.json"
"$PYTHON" - "$DIST" "$TAG" "$VER" >"$ARTIFACTS_JSON" <<'PY'
import hashlib, json, os, sys
dist, tag, ver = sys.argv[1], sys.argv[2], sys.argv[3]
sums = {}
with open(os.path.join(dist, "SHA256SUMS.txt"), encoding="utf-8") as fh:
    for line in fh:
        parts = line.strip().split(maxsplit=1)
        if len(parts) == 2:
            sums[parts[1].lstrip("*")] = parts[0]
rid_meta = {
    "win-x64": ("windows", "x64"), "win-arm64": ("windows", "arm64"),
    "linux-x64": ("linux", "x64"), "linux-arm64": ("linux", "arm64"),
    "osx-x64": ("macos", "x64"), "osx-arm64": ("macos", "arm64"),
}
artifacts = []
for rid, (os_name, arch) in rid_meta.items():
    name = f"winmatsch-{tag}-{rid}" + (".exe" if rid.startswith("win-") else "")
    path = os.path.join(dist, name)
    sha = sums.get(name) or hashlib.sha256(open(path, "rb").read()).hexdigest()
    artifacts.append({
        "name": name, "rid": rid, "os": os_name, "arch": arch,
        "url": f"/v{ver}/{name}", "sizeBytes": os.path.getsize(path), "sha256": sha,
    })
json.dump(artifacts, sys.stdout, indent=2)
PY

# Fetch the current manifest unless one was supplied.
CURRENT_MANIFEST="$STAGING/.current-versions.json"
if [ -n "$MANIFEST_IN" ]; then
    cp "$MANIFEST_IN" "$CURRENT_MANIFEST"
elif [ "$DRY_RUN" -eq 1 ]; then
    warn "dry run without --manifest-in: starting from an empty manifest"
    rm -f "$CURRENT_MANIFEST"
else
    log "Downloading current versions.json"
    if ! az storage blob download "${AZ_AUTH[@]}" \
            --account-name "$ACCOUNT" --container-name "$CONTAINER" \
            --name versions.json --file "$CURRENT_MANIFEST" --no-progress \
            >/dev/null 2>&1; then
        warn "no existing versions.json (first release?) — starting fresh"
        rm -f "$CURRENT_MANIFEST"
    fi
fi

MANIFEST_ARGS=(--version "$VER" --date "$DATE" --artifacts-file "$ARTIFACTS_JSON"
               --out "$STAGING/versions.json" --notes-url "$NOTES_URL")
[ -f "$CURRENT_MANIFEST" ] && MANIFEST_ARGS+=(--in "$CURRENT_MANIFEST")
[ "$PRERELEASE" -eq 1 ] && MANIFEST_ARGS+=(--prerelease)
LATEST="$("$PYTHON" "$MANIFEST_TOOL" "${MANIFEST_ARGS[@]}")"
log "Manifest updated: this=$VER latest=${LATEST:-<none>}"

# ---------------------------------------------------------------- staging

log "Staging site layout in $STAGING"
VDIR="$STAGING/v$VER"
mkdir -p "$VDIR"
for rid in "${RIDS[@]}"; do
    cp "$DIST/$(binary_name "$rid")" "$VDIR/"
done
cp "$SUMS" "$DIST/LICENSE" "$DIST/THIRD-PARTY-NOTICES.txt" "$VDIR/"
cp "$SITE_INDEX" "$VDIR/index.html"
cp "$SITE_INDEX" "$STAGING/index.html"
cp "$SITE_INDEX" "$STAGING/404.html"

REFRESH_LATEST=0
if [ "$SKIP_LATEST" -eq 0 ] && [ -n "$LATEST" ] && [ "$LATEST" = "$VER" ]; then
    REFRESH_LATEST=1
    log "Staging /latest/ mirror (stable unversioned names)"
    LDIR="$STAGING/latest"
    mkdir -p "$LDIR"
    for rid in "${RIDS[@]}"; do
        ext=""
        case "$rid" in win-*) ext=".exe" ;; esac
        cp "$DIST/$(binary_name "$rid")" "$LDIR/winmatsch-$rid$ext"
    done
    cp "$DIST/LICENSE" "$DIST/THIRD-PARTY-NOTICES.txt" "$LDIR/"
    cp "$SITE_INDEX" "$LDIR/index.html"
    # Rewritten checksums, version.txt and latest.json for the stable names.
    "$PYTHON" - "$STAGING" "$VER" <<'PY'
import json, os, sys
staging, ver = sys.argv[1], sys.argv[2]
manifest = json.load(open(os.path.join(staging, "versions.json"), encoding="utf-8"))
entry = next(v for v in manifest["versions"] if v["version"] == ver)
lines, stable_artifacts = [], []
for a in entry["artifacts"]:
    stable = f"winmatsch-{a['rid']}" + (".exe" if a["os"] == "windows" else "")
    lines.append(f"{a['sha256']}  {stable}")
    stable_artifacts.append({**a, "name": stable, "url": f"/latest/{stable}"})
latest_dir = os.path.join(staging, "latest")
with open(os.path.join(latest_dir, "SHA256SUMS.txt"), "w", encoding="utf-8", newline="\n") as fh:
    fh.write("\n".join(lines) + "\n")
with open(os.path.join(latest_dir, "version.txt"), "w", encoding="utf-8", newline="\n") as fh:
    fh.write(ver + "\n")
with open(os.path.join(latest_dir, "latest.json"), "w", encoding="utf-8", newline="\n") as fh:
    json.dump({
        "version": entry["version"], "tag": entry["tag"], "date": entry["date"],
        "path": entry["path"], "notes": entry.get("notes"), "artifacts": stable_artifacts,
    }, fh, indent=2)
    fh.write("\n")
PY
else
    [ "$SKIP_LATEST" -eq 1 ] && log "Skipping /latest/ mirror (--skip-latest)"
    [ "$SKIP_LATEST" -eq 0 ] && log "Not refreshing /latest/ ($VER is not the latest stable: ${LATEST:-<none>})"
fi

# ------------------------------------------------- headers & upload plan

TTL_SHORT="public, max-age=300, must-revalidate"
TTL_IMMUTABLE="public, max-age=31536000, immutable"

# Emits: content-type|cache-control|content-disposition for a container path.
headers_for() {
    local path="$1" base type cache dispo=""
    base="$(basename "$path")"
    case "$base" in
        *.html)           type="text/html; charset=utf-8" ;;
        *.json)           type="application/json; charset=utf-8" ;;
        *.txt|LICENSE)    type="text/plain; charset=utf-8" ;;
        winmatsch-*)      type="application/octet-stream"; dispo="attachment" ;;
        *)                type="application/octet-stream" ;;
    esac
    case "$path" in
        v*/index.html)    cache="$TTL_SHORT" ;;
        v*/*)             cache="$TTL_IMMUTABLE" ;;
        *)                cache="$TTL_SHORT" ;;
    esac
    printf '%s|%s|%s' "$type" "$cache" "$dispo"
}

# Upload order keeps the site consistent: immutable version folder first,
# then the latest mirror, then the manifest and entry pages that reference them.
upload_list() {
    (cd "$STAGING" && {
        find "v$VER" -type f | LC_ALL=C sort
        [ "$REFRESH_LATEST" -eq 1 ] && find latest -type f | LC_ALL=C sort
        printf '%s\n' versions.json index.html 404.html
    })
}

if [ "$DRY_RUN" -eq 1 ]; then
    log "Dry run — upload plan (staged in $STAGING):"
    printf '%-52s %-32s %s\n' "PATH" "CONTENT-TYPE" "CACHE-CONTROL" >&2
    while IFS= read -r path; do
        IFS='|' read -r type cache _ <<<"$(headers_for "$path")"
        printf '%-52s %-32s %s\n' "/$path" "$type" "$cache" >&2
    done < <(upload_list)
    log "No changes were made to Azure."
    exit 0
fi

# ----------------------------------------------------------------- upload

log "Uploading to storage account '$ACCOUNT', container '$CONTAINER'"
while IFS= read -r path; do
    IFS='|' read -r type cache dispo <<<"$(headers_for "$path")"
    args=(--account-name "$ACCOUNT" --container-name "$CONTAINER"
          --file "$STAGING/$path" --name "$path" --overwrite
          --content-type "$type" --content-cache "$cache" --no-progress)
    [ -n "$dispo" ] && args+=(--content-disposition "$dispo")
    log "  /$path"
    az storage blob upload "${AZ_AUTH[@]}" "${args[@]}" >/dev/null
done < <(upload_list)
log "Upload complete"

# ------------------------------------------------------------------ purge

if [ -n "$AFD_PROFILE" ] && [ -n "$AFD_ENDPOINT" ] && [ -n "$AFD_RG" ]; then
    log "Purging Front Door cache ($AFD_PROFILE/$AFD_ENDPOINT)"
    az afd endpoint purge \
        --resource-group "$AFD_RG" --profile-name "$AFD_PROFILE" \
        --endpoint-name "$AFD_ENDPOINT" \
        --content-paths "/" "/index.html" "/404.html" "/versions.json" "/latest/*" \
        >/dev/null
    log "Purge requested"
elif [ -n "$AFD_PROFILE$AFD_ENDPOINT$AFD_RG" ]; then
    warn "Front Door purge skipped: need all of --afd-resource-group, --afd-profile, --afd-endpoint"
else
    log "Front Door purge not requested (pass --afd-* flags to enable)"
fi

log "Done. Published $TAG$( [ "$REFRESH_LATEST" -eq 1 ] && printf ' and refreshed /latest/' )."
