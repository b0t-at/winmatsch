#!/usr/bin/env bash

set -euo pipefail

usage() {
    cat <<'EOF'
Usage: download-release-bundle.sh --repository OWNER/REPO --tag TAG --out DIR

Wait for the complete release asset set, checking every 10 seconds for up to
12 minutes, then download it into an empty output directory.
EOF
}

REPOSITORY=""
TAG=""
OUT=""

while [ $# -gt 0 ]; do
    case "$1" in
        --repository) REPOSITORY="${2:?}"; shift 2 ;;
        --tag)        TAG="${2:?}"; shift 2 ;;
        --out)        OUT="${2:?}"; shift 2 ;;
        -h|--help)    usage; exit 0 ;;
        *)            usage >&2; printf 'error: unknown argument: %s\n' "$1" >&2; exit 1 ;;
    esac
done

[[ "$REPOSITORY" =~ ^[^/]+/[^/]+$ ]] || { usage >&2; printf 'error: invalid repository\n' >&2; exit 1; }
[[ "$TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] \
    || { usage >&2; printf 'error: invalid release tag\n' >&2; exit 1; }
[ -n "$OUT" ] || { usage >&2; printf 'error: --out is required\n' >&2; exit 1; }
if [ -e "$OUT" ] && [ -n "$(find "$OUT" -mindepth 1 -print -quit)" ]; then
    printf 'error: output directory must be empty: %s\n' "$OUT" >&2
    exit 1
fi
command -v gh >/dev/null || { printf 'error: gh not found\n' >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
EXPECTED="$WORK/expected.txt"
{
    printf '%s\n' \
        "winmatsch-$TAG-win-x64.exe" \
        "winmatsch-$TAG-win-arm64.exe" \
        "winmatsch-$TAG-linux-x64" \
        "winmatsch-$TAG-linux-arm64" \
        "winmatsch-$TAG-osx-x64" \
        "winmatsch-$TAG-osx-arm64" \
        LICENSE THIRD-PARTY-NOTICES.txt SHA256SUMS.txt
} | LC_ALL=C sort >"$EXPECTED"

DEADLINE=$((SECONDS + 720))
ATTEMPT=0
LAST_RESULT="release assets not requested"
printf 'Waiting for the complete %s release bundle (10s interval, 12m timeout)\n' "$TAG" >&2
while true; do
    ATTEMPT=$((ATTEMPT + 1))
    ATTEMPT_DIR="$WORK/attempt-$ATTEMPT"
    mkdir "$ATTEMPT_DIR"
    if gh release download "$TAG" --repo "$REPOSITORY" --dir "$ATTEMPT_DIR" \
        2>"$WORK/download.err"; then
        find "$ATTEMPT_DIR" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' \
            | LC_ALL=C sort >"$WORK/actual.txt"
        if diff -q "$EXPECTED" "$WORK/actual.txt" >/dev/null; then
            mkdir -p "$OUT"
            mv "$ATTEMPT_DIR"/* "$OUT/"
            printf 'Downloaded the complete %s release bundle\n' "$TAG" >&2
            exit 0
        fi
        LAST_RESULT="release asset set is incomplete"
    else
        LAST_RESULT="$(tr '\n' ' ' <"$WORK/download.err")"
        [ -n "$LAST_RESULT" ] || LAST_RESULT="release download failed"
    fi

    REMAINING=$((DEADLINE - SECONDS))
    if (( REMAINING <= 0 )); then
        printf 'error: complete assets for %s were unavailable after 12 minutes (%s)\n' \
            "$TAG" "$LAST_RESULT" >&2
        exit 1
    fi
    SLEEP_FOR=$((REMAINING < 10 ? REMAINING : 10))
    printf '%s; retrying in %ss\n' "$LAST_RESULT" "$SLEEP_FOR" >&2
    sleep "$SLEEP_FOR"
done
