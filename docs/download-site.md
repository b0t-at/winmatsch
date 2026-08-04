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
| `/<version>/` | Front Door alias for the same human release page (e.g. `/0.9.0/`); no duplicate storage folder | short (300 s) |
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
  `/latest/` → stable channel page, and both `/v<version>/` and
  `/<version>/` → that release. Canonical links and artifact URLs retain the
  explicit `/v<version>/` form.
- **GitHub-authoritative mirror.** Published GitHub Releases are the source of
  truth. `/versions.json` is the site's synchronized projection, which the page
  fetches at runtime; GitHub's native latest-release endpoint determines the
  `/latest` mirror.
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
4. removes a deprecated physical `/<version>/index.html` alias if one exists,
5. optionally purges Front Door: the mutable blob paths (`/`, `/index.html`,
   `/404.html`, `/versions.json`, `/latest`, `/latest/`, `/latest/*`) plus
   the rewrite-rule page URLs of the published version (`/v<version>` and
   `/<version>`, with and without trailing slash) — Front Door caches by
   request URL, so rewritten page routes are cached separately from
   `/index.html`,
6. writes a GitHub Actions job summary (when `GITHUB_STEP_SUMMARY` is set)
   with the published binaries — sizes and SHA-256 digests — plus blob
   counts, upload and Front Door purge durations. Dry runs summarise the
   upload plan instead.

After publication, and whenever a GitHub release is deleted,
`scripts/reconcile-download-site.sh` compares the complete published GitHub
release list with the live Azure `v<version>/` prefixes. Azure-only versions
are copied to `archive/v<version>/` at the Archive access tier, verified, and
then removed from the live namespace and `/versions.json`. The reconciler also
rebuilds `/latest/` from GitHub's designated latest release and purges affected
Front Door paths. A failed run is safe to rerun: catalog writes use the current
blob ETag, archive copies are verified before live blobs are removed, and
existing verified archive copies are reused.

Useful flags: `--dry-run` (stage + print the upload plan, no az calls),
`--staging DIR` (inspect the exact container layout), `--skip-latest`,
`--prerelease` (auto-detected from the version string), `--manifest-in`
(offline manifest for dry-run tests). Re-running the same version is
idempotent when its catalog metadata and SHA-256 values are unchanged. Existing
versioned release files are never overwritten; a different file at an
already-published path fails. The short-lived `index.html` page shell is the
only mutable file inside a version directory.

Auth: uses Azure CLI RBAC (`--auth-mode login`; role **Storage Blob Data
Contributor** on the target container). The Front Door purge additionally
needs the control-plane action
`Microsoft.Cdn/profiles/afdendpoints/purge/action` on the Front Door profile.
The least-privileged built-in role that includes it is **CDN Profile
Contributor** (`Microsoft.Cdn/profiles/*`; the other CDN/Front Door built-in
roles — CDN Endpoint Contributor, Azure Front Door Profile Reader, the Domain
and Secret roles — do not cover `afdendpoints/purge`). Assign it at the scope
of the single Front Door profile to keep the blast radius to that one
resource:

```sh
az role assignment create \
  --role "CDN Profile Contributor" \
  --assignee-object-id "$(az ad sp show --id <workflow-client-id> --query id -o tsv)" \
  --assignee-principal-type ServicePrincipal \
  --scope "/subscriptions/<subscription-id>/resourceGroups/<afd-resource-group>/providers/Microsoft.Cdn/profiles/<afd-profile>"
```

If even profile-scoped write access is too broad, a purge-only custom role
(`profiles/read`, `profiles/afdendpoints/read`, `profiles/afdendpoints/purge/action`)
is the tighter alternative.

### Suggested CI wiring

The checked-in
[publish workflow](../.github/workflows/publish-azure-release.yml) triggers on
both `release: published` and `release: deleted`. Publication validates and
uploads the GitHub release before synchronization; deletion runs only the
synchronizer. Both paths log in through the `Release` environment's Entra OIDC
federation. The manual input can safely backfill an already-published tag, or
be left empty to repair any published releases missing from Azure and then
synchronize the complete mirror.

When the `Release` environment additionally defines the secrets
`AZURE_AFD_RESOURCE_GROUP`, `AZURE_AFD_PROFILE` and `AZURE_AFD_ENDPOINT`
(all three, or none), the workflow passes the matching `--afd-*` flags so
every publish requests a Front Door purge without waiting for Azure's
long-running-operation acknowledgement. The publisher and synchronizer instead
check the endpoint's `/versions.json` every 10 seconds until its JSON matches
the uploaded manifest, with a 12-minute timeout. `AZURE_AFD_ENDPOINT` is the
endpoint *resource name* (e.g.
`WinMatsch`), not the `<name>-<hash>.z0X.azurefd.net` hostname — validation
rejects values containing a dot. Without the trio the workflow only warns:
edge PoPs then keep serving cached pages until the five-minute origin TTL
expires. A rejected purge request or propagation timeout fails the workflow
run — rerun it after fixing RBAC; republishing the same version is idempotent.

## Recommended Front Door Standard/Premium configuration

- **Origin:** `<account>.blob.core.windows.net`, HTTPS `443`, with the same
  value as the origin host header and certificate-name validation enabled.
  Use origin path `/winmatsch`.
- **Health probe:** `HEAD /winmatsch/index.html` over HTTPS.
- **Route:** pattern `/*`, forwarding protocol **HTTPS only**, caching and
  compression enabled, query strings ignored, cache behavior **Honor origin**.
  The publisher already sends five-minute cache headers for mutable pages and
  one-year immutable headers for versioned release files.
- **HTTPS:** enable the built-in HTTP-to-HTTPS redirect with status `308`.
- **Rule `root-page`:** keep the portal configuration shown below for the site
  root: match **Request path** with operator **Equal** and value `/`, then
  apply **URL rewrite** with source pattern `/`, destination `/index.html`,
  and **Preserve unmatched path = No**.
- **Rule `download-pages`:** for the remaining page routes, match **Request
  path** with operator **RegEx**, transform **Lowercase**, and enter this as
  one match value. Front Door's RegEx validator accepts exactly one value, so
  the alternatives are combined in the expression:

  ```text
  ^latest/?$|^v?[0-9]+\.[0-9]+\.[0-9]+/?$|^v?[0-9]+\.[0-9]+\.[0-9]+-[0-9a-z.-]+/?$
  ```

  Apply the same **URL rewrite** action as in `root-page`. Together the two
  rules serve the SPA shell for `/`, `/latest`, `/latest/`, `/v0.9.0[/]`, and
  `/0.9.0[/]` while binaries, `versions.json`, checksums, and licenses continue
  to map directly to blobs.
- **Origin access:** prefer Front Door Premium with Private Link if the
  container should remain private. Otherwise the container needs anonymous
  blob-read access so Front Door can fetch it.
- **Security headers:** add a conditionless response-header rule for
  `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
  `X-Frame-Options: DENY`, and, once the final custom domain is fixed,
  `Strict-Transport-Security: max-age=31536000; includeSubDomains`.

The public URL must not include the `winmatsch` container segment because the
page and manifest intentionally use root-relative links.

## Storage alternatives and operations

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
- The workflow purges Front Door only when the three `AZURE_AFD_*` secrets
  are configured, so the OIDC identity can stay on container-scoped
  data-plane RBAC until the purge-only custom role above is assigned.
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
the static website does, so `/`, `/latest/`, and `/v0.1.0/` render. The bare
`/0.1.0/` alias exists only at Front Door; no duplicate version directory is
staged or stored.
