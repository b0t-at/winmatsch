# Repository security settings

The workflow files enforce immutable action references and least-privilege
`GITHUB_TOKEN` permissions. Keep the matching repository controls enabled:

- **Settings > Actions > General > Workflow permissions**: select **Read
  repository contents and packages permissions** and disable GitHub Actions
  creating or approving pull requests.
- **Settings > Actions > General > Actions permissions**: allow only required
  actions (`actions/*`, `Azure/login`, `softprops/action-gh-release`, and
  `zizmorcore/zizmor-action`) and require full-length commit SHA pins where the
  organization policy is available.
- **Settings > Environments > Release**: keep this exact environment name
  because it is part of the Entra federated-credential subject. If deployment
  branch/tag policies are enabled, allow `v*` tags and the default branch so
  publication events, release-deletion syncs, and manual backfills can run.
  Leave required reviewers disabled: permission to publish or delete a GitHub
  release is the release-management authorization boundary.
- **Settings > Rules > Rulesets**: protect `main` with pull requests, required
  reviews, and **Require review from Code Owners**. Protect `v*` tags from
  deletion or update and limit tag creation to release maintainers.
- **Settings > Actions > General > Fork pull request workflows**: require
  approval for first-time contributors.
- **Settings > Security**: enable the dependency graph, Dependabot alerts,
  Dependabot security updates, secret scanning, and push protection.

The Azure publication workflow requires these **repository** Actions secrets:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_STORAGE_ACCOUNT`
- `AZURE_STORAGE_CONTAINER`

The Entra service principal needs **Storage Blob Data Contributor** at the
`AZURE_STORAGE_CONTAINER` scope only:

```text
/subscriptions/<subscription-id>/resourceGroups/<resource-group>/providers/Microsoft.Storage/storageAccounts/<storage-account>/blobServices/default/containers/<container>
```

No subscription, resource-group, storage-account Contributor, storage-account
key access, or Azure Resource Manager Reader role is required by the workflow.
The container must already exist. When assigning the role by object ID, use the
enterprise application's **service principal object ID**, not the app
registration object ID.

Artifact attestations are intentionally deferred until the private repository
has a supported verification or enforcement path; publishing an unused
attestation would not improve the release trust boundary.
