#!/usr/bin/env bash

set -euo pipefail

usage() {
    cat <<'EOF'
Usage: wait-for-front-door-manifest.sh --host HOST --expected FILE

Poll https://HOST/versions.json every 10 seconds for up to 12 minutes until
its JSON content matches FILE. Prints the elapsed seconds on success.
EOF
}

HOST=""
EXPECTED=""

while [ $# -gt 0 ]; do
    case "$1" in
        --host)     HOST="${2:?}"; shift 2 ;;
        --expected) EXPECTED="${2:?}"; shift 2 ;;
        -h|--help)  usage; exit 0 ;;
        *)          usage >&2; printf 'error: unknown argument: %s\n' "$1" >&2; exit 1 ;;
    esac
done

[ -n "$HOST" ] || { usage >&2; printf 'error: --host is required\n' >&2; exit 1; }
[ -f "$EXPECTED" ] || { usage >&2; printf 'error: --expected must be a file\n' >&2; exit 1; }
command -v curl >/dev/null || { printf 'error: curl not found\n' >&2; exit 1; }
command -v jq >/dev/null || { printf 'error: jq not found\n' >&2; exit 1; }

EXPECTED_JSON="$(jq -S -c . "$EXPECTED")"
URL="https://$HOST/versions.json"
STARTED=$SECONDS
DEADLINE=$((SECONDS + 720))
LAST_RESULT="not requested"
ERROR_FILE="$(mktemp)"
trap 'rm -f "$ERROR_FILE"' EXIT

printf 'Waiting for %s to match the uploaded manifest (10s interval, 12m timeout)\n' "$URL" >&2
while true; do
    REMAINING=$((DEADLINE - SECONDS))
    if (( REMAINING <= 0 )); then
        printf 'error: Front Door did not serve the uploaded /versions.json within 12 minutes (%s)\n' \
            "$LAST_RESULT" >&2
        exit 1
    fi

    CURL_TIMEOUT=$((REMAINING < 10 ? REMAINING : 10))
    if BODY="$(curl --fail --silent --show-error --max-time "$CURL_TIMEOUT" \
        "$URL" 2>"$ERROR_FILE")"; then
        if ACTUAL_JSON="$(jq -S -c . <<<"$BODY" 2>"$ERROR_FILE")"; then
            if [ "$ACTUAL_JSON" = "$EXPECTED_JSON" ]; then
                printf '%s\n' "$((SECONDS - STARTED))"
                exit 0
            fi
            LAST_RESULT="$(jq -r '
                "served latest=\(.latest // "<none>"), versions=" +
                ([.versions[].version] | join(","))
            ' <<<"$BODY" 2>/dev/null || printf 'served different JSON')"
        else
            LAST_RESULT="served invalid JSON"
        fi
    else
        LAST_RESULT="$(tr '\n' ' ' <"$ERROR_FILE")"
        [ -n "$LAST_RESULT" ] || LAST_RESULT="request failed"
    fi

    REMAINING=$((DEADLINE - SECONDS))
    if (( REMAINING <= 0 )); then
        printf 'error: Front Door did not serve the uploaded /versions.json within 12 minutes (%s)\n' \
            "$LAST_RESULT" >&2
        exit 1
    fi
    SLEEP_FOR=$((REMAINING < 10 ? REMAINING : 10))
    printf 'Front Door manifest not updated yet (%s); retrying in %ss\n' \
        "$LAST_RESULT" "$SLEEP_FOR" >&2
    sleep "$SLEEP_FOR"
done
