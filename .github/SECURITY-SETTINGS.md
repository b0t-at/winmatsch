# Repository security settings

The workflow files enforce immutable action references and least-privilege
`GITHUB_TOKEN` permissions. Keep the matching repository controls enabled:

- **Settings > Actions > General > Workflow permissions**: select **Read
  repository contents and packages permissions** and disable GitHub Actions
  creating or approving pull requests.
- **Settings > Actions > General > Actions permissions**: allow only required
  actions (`actions/*`, `softprops/action-gh-release`, and
  `zizmorcore/zizmor-action`) and require full-length commit SHA pins where the
  organization policy is available.
- **Settings > Rules > Rulesets**: protect `main` with pull requests, required
  reviews, and **Require review from Code Owners**. Protect `v*` tags from
  deletion or update and limit tag creation to release maintainers.
- **Settings > Actions > General > Fork pull request workflows**: require
  approval for first-time contributors.
- **Settings > Security**: enable the dependency graph, Dependabot alerts,
  Dependabot security updates, secret scanning, and push protection.

Artifact attestations are intentionally deferred until the private repository
has a supported verification or enforcement path; publishing an unused
attestation would not improve the release trust boundary.
