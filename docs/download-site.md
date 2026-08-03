# Download site (Azure Blob Storage + Front Door)

The release workflow writes the complete winmatsch download site into the
configured Azure Blob container (`winmatsch` in the current deployment). It is
designed to sit behind **Azure Front Door** with that container mounted at the
origin root. There is no web server and no compute: every URL is either a blob
or a page rendered client-side from a JSON manifest.

## URL contract

| URL | What you get | Cache |
| --- | --- | --- |
| `/` | Version browser: latest release, quick-install snippets, all versions | short (300 s) |
| `/versions.json` | Machine-readable manifest of all releases (see schema below) | short (300 s) |
| `/latest/` | Human page for the newest stable release | short (300 s) |
| `/latest/winmatsch-<rid>[.exe]` | Newest stable binary under a **stable, unversioned name** | short (300 s) |
| `/latest/version.txt` | Plain-text version number (for scripts) | short (300 s) |
| `/latest/latest.json` | Newest release metadata incl. stable URLs | short (300 s) |
| `/latest/SHA256SUMS.txt` | Checksums rewritten for the unversioned names | short (300 s) |
| `/v<version>/` | Human page for one release (e.g. `/v0.9.0/`) | short (300 s) |
| `/v<version>/winmatsch-v<version>-<rid>[.exe]` | Canonical immutable binary, exact release-asset name | immutable (1 y) |
| `/v<version>/SHA256SUMS.txt`, `LICENSE`, `THIRD-PARTY-NOTICES.txt` | Release documents | immutable (1 y) |

RIDs: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64` (`.exe` suffix on Windows only). Examples:

```sh
# Always the newest stable release, stable URL:
curl -fLO https://<host>/latest/winmatsch-linux-x64
curl -fsSL https://<host>/latest/SHA256SUMS.txt | sha256sum -c --ignore-missing

# Pin an exact version (immutable):
curl -fLO https://<host>/v0.9.0/winmatsch-v0.9.0-linux-x64

# What is the newest version?
curl -fsSL https://<host>/latest/version.txt
```

## How it works

- **One page, three roles.** `site/index.html` is self-contained (inline
  CSS/JS, no external assets). It is uploaded to `/index.html`,
  `/404.html`, `/latest/index.html` and every `/v<version>/index.html`.
  The hosting layer serves `index.html` for each directory request, and the
  page routes client-side on `location.pathname`: root → version browser,
  `/latest/` → stable channel page, `/v<version>/` → that release. As the
  error document the same file also heals slash-less or unknown paths by
  rendering the right view or a version list.
- **One manifest.** `/versions.json` is the single source of truth; the
  page fetches it at runtime, so publishing a release only writes blobs —
  no HTML changes, no Front Door configuration changes.
- **`/latest` is a blob mirror, not a redirect.** Releases stay pure
  data-plane uploads (no control-plane permissions needed in CI). The
  mirror uses unversioned file names so the URLs never change, plus a
  rewritten `SHA256SUMS.txt` that matches those names.
- **Architecture detection** is best-effort and suggest-only: Client
  Hints (`navigator.userAgentData.getHighEntropyValues`) on Chromium,
  falling back to the UA string, and a WebGL renderer probe to spot
  Apple Silicon in Safari. The full artifact list is always shown; the
  detected build is merely highlighted.

## versions.json schema (v1)

```json
{
  "schemaVersion": 1,
  "project": "winmatsch",
  "updated": "2026-08-03T12:00:00Z",
  "latest": "0.9.0",
  "versions": [
    {
      "version": "0.9.0",
      "tag": "v0.9.0",
      "date": "2026-08-03T12:00:00Z",
      "prerelease": false,
      "path": "/v0.9.0/",
      "sha256sums": "/v0.9.0/SHA256SUMS.txt",
      "notes": "https://github.com/b0t-at/winmatsch/releases/tag/v0.9.0",
      "artifacts": [
        {
          "name": "winmatsch-v0.9.0-win-x64.exe",
          "rid": "win-x64",
          "os": "windows",
          "arch": "x64",
          "url": "/v0.9.0/winmatsch-v0.9.0-win-x64.exe",
          "sizeBytes": 12345678,
          "sha256": "…64 hex chars…"
        }
      ]
    }
  ]
}
```

- `versions` is sorted descending by semver; pre-releases carry
  `prerelease: true` and never become `latest`.
- `latest` is the highest stable version, or `null` when only
  pre-releases exist.
- `os` is `windows` | `linux` | `macos`; `arch` is `x64` | `arm64`.

## Publishing a release

```sh
scripts/publish-download-site.sh \
    --version v0.9.0 \
    --dist dist/ \
    --account <storage-account> \
    --container winmatsch
```

The script (bash + python3 + az; no jq required):

1. verifies all six binaries, `SHA256SUMS.txt`, `LICENSE` and
   `THIRD-PARTY-NOTICES.txt` are present and the checksums match,
2. reads the current `/versions.json` at a specific Azure Blob ETag and
   upserts the release via `scripts/update-versions-manifest.py` (semver sort,
   `latest` recompute),
3. stages the full layout locally, then uploads in a safe order:
   immutable `/v<version>/` first, the ETag-guarded `versions.json`, then
   `/latest/` (only when this release *is* the newest stable) and the entry
   pages,
4. optionally purges the Front Door cache for `/`, `/index.html`,
   `/404.html`, `/versions.json` and `/latest/*`.

Useful flags: `--dry-run` (stage + print the upload plan, no az calls),
`--staging DIR` (inspect the exact container layout), `--skip-latest`,
`--prerelease` (auto-detected from the version string), `--manifest-in`
(offline manifest for dry-run tests). Re-running the same version is
idempotent when its catalog metadata and SHA-256 values are unchanged. Existing
versioned release files are never overwritten; a different file at an
already-published path fails. The short-lived `index.html` page shell is the
only mutable file inside a version directory.

Auth: uses Azure CLI RBAC (`--auth-mode login`; role **Storage Blob Data
Contributor** on the target container). The Front Door purge additionally needs
`Microsoft.Cdn/profiles/afdendpoints/purge/action` (e.g. **CDN Profile
Contributor**).

### Suggested CI wiring

The checked-in
[publish workflow](../.github/workflows/publish-azure-release.yml) triggers on
`release: published`, preserving the human draft-review gate from
[release.md](release.md). It validates the GitHub release, logs in through the
`Release` environment's Entra OIDC federation, and invokes this script with the
configured account and container. Its manual input can safely backfill an
already-published tag.

## Front Door / storage notes

- With the configured named container, point Front Door at the blob endpoint,
  mount `/winmatsch` as the route's origin path, and configure equivalent
  index/error routing for `/`, `/latest/`, and `/v<version>/`. The public
  origin must not include the container segment because the page and manifest
  intentionally use root-relative URLs.
- Azure Storage's native static website endpoint is an alternative, but it
  always serves from the reserved `$web` container. To use that endpoint,
  configure `AZURE_STORAGE_CONTAINER=$web`, then set index document =
  `index.html` and error document = `404.html`.
- Let Front Door honor origin `Cache-Control` headers (default). The
  publish script sets long/immutable TTLs for `/v<version>/` assets and
  short TTLs everywhere else, so purging is a safety net rather than a
  requirement.
- Enable compression for text content types; binaries are already
  incompressible enough to skip.
- A Front Door rule can 308-redirect `/latest` → `/latest/` and `/v<x>` →
  `/v<x>/`. The native static website endpoint can fall back to the configured
  `404.html`; a named-container Front Door route needs the equivalent error
  rewrite.
- The checked-in workflow deliberately does not purge Front Door so the OIDC
  identity needs only container-scoped data-plane RBAC. Purge can be enabled
  later by passing all three `--afd-*` flags and granting the additional
  control-plane action.
- `$web` must be quoted in shells: `--container '$web'`.

## Local testing

```sh
# Fake release + dry run (no Azure needed):
mkdir -p /tmp/dist && cd /tmp/dist
for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do
  ext=""; [ "${rid#win-}" != "$rid" ] && ext=".exe"
  head -c 1024 /dev/urandom > "winmatsch-v0.1.0-$rid$ext"
done
printf 'license' > LICENSE; printf 'tpn' > THIRD-PARTY-NOTICES.txt
sha256sum winmatsch-* LICENSE THIRD-PARTY-NOTICES.txt > SHA256SUMS.txt
cd -

scripts/publish-download-site.sh --version v0.1.0 --dist /tmp/dist \
    --staging /tmp/site --dry-run

# Serve the staged layout and click around:
python3 -m http.server 8080 --directory /tmp/site
```

`python3 -m http.server` serves `index.html` for directories just like
the static website does, so `/`, `/latest/` and `/v0.1.0/` all render.
