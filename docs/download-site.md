# Download site (Azure Storage static website + Front Door)

The download site distributes winmatsch release binaries from an Azure
Storage **static website** (the `$web` container) behind **Azure Front
Door**. There is no web server and no compute: every URL is either a blob
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
  Azure static websites serve the configured *index document* for each
  "directory" request, and the page routes client-side on
  `location.pathname`: root → version browser, `/latest/` → stable
  channel page, `/v<version>/` → that release. As the 404 *error
  document* the same file also heals `/latest` (no trailing slash) and
  unknown paths by rendering the right view or a version list.
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
    --afd-resource-group <rg> --afd-profile <profile> --afd-endpoint <endpoint>
```

The script (bash + python3 + az; no jq required):

1. verifies all six binaries, `SHA256SUMS.txt`, `LICENSE` and
   `THIRD-PARTY-NOTICES.txt` are present and the checksums match,
2. downloads the current `/versions.json` and upserts the release via
   `scripts/update-versions-manifest.py` (semver sort, `latest` recompute),
3. stages the full layout locally, then uploads in a safe order:
   immutable `/v<version>/` first, then `/latest/` (only when this release
   *is* the newest stable), then `versions.json` and the entry pages,
4. optionally purges the Front Door cache for `/`, `/index.html`,
   `/404.html`, `/versions.json` and `/latest/*`.

Useful flags: `--dry-run` (stage + print the upload plan, no az calls),
`--staging DIR` (inspect the exact container layout), `--skip-latest`,
`--prerelease` (auto-detected from the version string), `--manifest-in`
(offline manifest for tests). Re-running the same version is idempotent —
blobs are overwritten in place.

Auth: uses `az` RBAC (`--auth-mode login`; role **Storage Blob Data
Contributor** on the account) unless `AZURE_STORAGE_CONNECTION_STRING` or
`AZURE_STORAGE_KEY` is set. The Front Door purge additionally needs
`Microsoft.Cdn/profiles/afdendpoints/purge/action` (e.g. **CDN Profile
Contributor**).

### Suggested CI wiring

Trigger on `release: published` so the human draft-review gate from
[release.md](release.md) still applies — the site only updates once a
maintainer publishes the draft release:

```yaml
name: Publish download site
on:
  release:
    types: [published]
permissions:
  id-token: write
  contents: read
jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Download release assets
        run: gh release download "$TAG" --dir dist
        env:
          GH_TOKEN: ${{ github.token }}
          TAG: ${{ github.event.release.tag_name }}
      - uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
      - name: Publish
        run: |
          scripts/publish-download-site.sh \
            --version "${{ github.event.release.tag_name }}" \
            --dist dist \
            --account "${{ vars.DOWNLOAD_SITE_STORAGE_ACCOUNT }}" \
            --afd-resource-group "${{ vars.DOWNLOAD_SITE_RG }}" \
            --afd-profile "${{ vars.DOWNLOAD_SITE_AFD_PROFILE }}" \
            --afd-endpoint "${{ vars.DOWNLOAD_SITE_AFD_ENDPOINT }}"
```

Mark pre-releases with `--prerelease` (or rely on auto-detection from
tags like `v0.9.0-rc.1`).

## Front Door / storage notes

- Point the Front Door origin at the **static website endpoint**
  (`<account>.z<zone>.web.core.windows.net`), *not* the blob endpoint —
  index and error documents only work on the web endpoint.
- Set index document = `index.html` and error document = `404.html`
  (`az storage blob service-properties update --static-website ...`).
- Let Front Door honor origin `Cache-Control` headers (default). The
  publish script sets long/immutable TTLs for `/v<version>/` assets and
  short TTLs everywhere else, so purging is a safety net rather than a
  requirement.
- Enable compression for text content types; binaries are already
  incompressible enough to skip.
- Optional polish: a Front Door rule that 308-redirects `/latest` →
  `/latest/` and `/v<x>` → `/v<x>/`. Without it, Azure returns the 404
  error document for slash-less directory URLs — which is this same page,
  so visitors still land on the right view (with a 404 status code).
- `$web` must be quoted in shells: `--container-name '$web'`.

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
