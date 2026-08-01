using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// META-5: field-set parity with the previous merged version (the wingetbot
/// "Missing Properties value based on version X" class). Fields the previous default locale
/// declared but the new manifests dropped are carried forward when they are still valid:
/// non-URL fields verbatim, URL fields only when <see cref="PolicyEvidence.ConfirmedUrls"/>
/// re-validated the previous value. A previous root <c>ReleaseDate</c> is recomputed from
/// supplied release metadata — never copied. Dropping a field silently requires an explicit
/// <see cref="OverridePack.DroppedFields"/> entry; otherwise a finding calls the drop out.
/// Per-installer <c>InstallerSwitches</c> and <c>Dependencies</c> carry-over is owned by
/// WM0007 <c>PreserveOnUpdateRule</c> (which matches entries by uniqueness key); this rule
/// deliberately covers only the root/locale field-set so the two do not fight.
/// </summary>
public sealed class Meta5FieldSetParityRule : IRule
{
    private readonly PolicyEvidence _evidence;
    private readonly OverridePackSet _overridePacks;

    public Meta5FieldSetParityRule(PolicyEvidence? evidence = null, OverridePackSet? overridePacks = null)
    {
        _evidence = evidence ?? PolicyEvidence.Empty;
        _overridePacks = overridePacks ?? OverridePackSet.Empty;
    }

    public string Id => RuleCatalogueIds.Meta5;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Carries still-valid fields from the previous version or requires an explicit drop override.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Previous is not { } previous)
        {
            return;
        }

        _overridePacks.TryGet(context.Manifests.Installer.PackageIdentifier, out OverridePack? pack);
        ImmutableStringSet droppedFields = new(pack?.DroppedFields ?? []);

        CarryLocaleFields(context, previous, droppedFields);
        CarryInstallerRootFields(context, previous, droppedFields);
        CheckPerInstallerFieldPresence(context, previous, droppedFields);
    }

    /// <summary>
    /// WM0007 carries per-installer InstallerSwitches/Dependencies only when a unique
    /// Architecture+InstallerType+Scope match exists; when the type or scope changed, the
    /// fields silently vanish. This parity pass reports such drops (no value is ever
    /// fabricated — re-detection or an explicit drop override resolves the finding).
    /// </summary>
    private void CheckPerInstallerFieldPresence(
        ManifestContext context,
        PackageManifests previous,
        ImmutableStringSet droppedFields)
    {
        InstallerManifest manifest = context.Manifests.Installer;
        if (manifest.Installers is not { } installers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            Installer? match = PolicyValues.FindPreviousByEntryKey(manifest, installer, previous.Installer);
            if (match is null)
            {
                match = FindPreviousByArchitecture(installer, previous.Installer, out bool ambiguous);
                if (ambiguous)
                {
                    // Several previous entries share this architecture (e.g. user/machine
                    // twins) and the entry key no longer matches; report once per entry when
                    // any candidate declared the fields instead of silently skipping.
                    ReportAmbiguousCandidates(context, previous.Installer, installer, manifest, droppedFields, i);
                    continue;
                }
            }

            if (match is null)
            {
                continue;
            }

            if (EffectiveInstallerValues.GetInstallerSwitches(manifest, installer) is null
                && EffectiveInstallerValues.GetInstallerSwitches(previous.Installer, match) is not null
                && !Skip(context, droppedFields, "InstallerSwitches"))
            {
                context.AddFinding(this, RuleSeverity.Warning,
                    "The previous version declared InstallerSwitches for this entry but the new manifest has none; re-detect the switches or add an explicit drop override.",
                    $"Installers[{i}]");
            }

            if (EffectiveInstallerValues.GetDependencies(manifest, installer) is null
                && EffectiveInstallerValues.GetDependencies(previous.Installer, match) is not null
                && !Skip(context, droppedFields, "Dependencies"))
            {
                context.AddFinding(this, RuleSeverity.Warning,
                    "The previous version declared Dependencies for this entry but the new manifest has none; verify the payload or add an explicit drop override.",
                    $"Installers[{i}]");
            }
        }
    }

    private void ReportAmbiguousCandidates(
        ManifestContext context,
        InstallerManifest previousManifest,
        Installer installer,
        InstallerManifest manifest,
        ImmutableStringSet droppedFields,
        int index)
    {
        List<Installer> candidates = [];
        foreach (Installer candidate in previousManifest.Installers ?? [])
        {
            if (candidate.Architecture == installer.Architecture)
            {
                candidates.Add(candidate);
            }
        }

        bool anySwitches = candidates.Any(c => EffectiveInstallerValues.GetInstallerSwitches(previousManifest, c) is not null);
        if (anySwitches
            && EffectiveInstallerValues.GetInstallerSwitches(manifest, installer) is null
            && !Skip(context, droppedFields, "InstallerSwitches"))
        {
            context.AddFinding(this, RuleSeverity.Warning,
                "The previous version's same-architecture entries declared InstallerSwitches but this entry has none and the layout changed too much for a unique match; review the switches or add an explicit drop override.",
                $"Installers[{index}]");
        }

        bool anyDependencies = candidates.Any(c => EffectiveInstallerValues.GetDependencies(previousManifest, c) is not null);
        if (anyDependencies
            && EffectiveInstallerValues.GetDependencies(manifest, installer) is null
            && !Skip(context, droppedFields, "Dependencies"))
        {
            context.AddFinding(this, RuleSeverity.Warning,
                "The previous version's same-architecture entries declared Dependencies but this entry has none and the layout changed too much for a unique match; review the dependencies or add an explicit drop override.",
                $"Installers[{index}]");
        }
    }

    /// <summary>The unique previous installer with the same architecture, or null; <paramref name="ambiguous"/> is set when several match.</summary>
    private static Installer? FindPreviousByArchitecture(
        Installer current,
        InstallerManifest previousManifest,
        out bool ambiguous)
    {
        ambiguous = false;
        if (previousManifest.Installers is not { } previousInstallers || current.Architecture is null)
        {
            return null;
        }

        Installer? match = null;
        foreach (Installer candidate in previousInstallers)
        {
            if (candidate.Architecture != current.Architecture)
            {
                continue;
            }

            if (match is not null)
            {
                ambiguous = true;
                return null;
            }

            match = candidate;
        }

        return match;
    }

    private void CarryLocaleFields(ManifestContext context, PackageManifests previous, ImmutableStringSet droppedFields)
    {
        DefaultLocaleManifest locale = context.Manifests.DefaultLocale;
        DefaultLocaleManifest previousLocale = previous.DefaultLocale;
        string manifestPath = PolicyValues.GetLocaleManifestPath(context.Manifests, locale);
        string previousVersion = previous.Installer.PackageVersion?.Value ?? "previous";

        CarryText(context, droppedFields, previousLocale.Author, () => locale.Author, v => locale.Author = v, nameof(locale.Author), manifestPath, previousVersion);
        CarryText(context, droppedFields, previousLocale.Moniker, () => locale.Moniker, v => locale.Moniker = v, nameof(locale.Moniker), manifestPath, previousVersion);
        CarryText(context, droppedFields, previousLocale.License, () => locale.License, v => locale.License = v, nameof(locale.License), manifestPath, previousVersion);
        CarryText(context, droppedFields, previousLocale.Copyright, () => locale.Copyright, v => locale.Copyright = v, nameof(locale.Copyright), manifestPath, previousVersion);
        CarryText(context, droppedFields, previousLocale.ShortDescription, () => locale.ShortDescription, v => locale.ShortDescription = v, nameof(locale.ShortDescription), manifestPath, previousVersion);
        CarryText(context, droppedFields, previousLocale.Description, () => locale.Description, v => locale.Description = v, nameof(locale.Description), manifestPath, previousVersion);
        CarryText(context, droppedFields, previousLocale.InstallationNotes, () => locale.InstallationNotes, v => locale.InstallationNotes = v, nameof(locale.InstallationNotes), manifestPath, previousVersion);

        CarryUrl(context, droppedFields, previousLocale.PublisherUrl, () => locale.PublisherUrl, v => locale.PublisherUrl = v, nameof(locale.PublisherUrl), manifestPath, previousVersion);
        CarryUrl(context, droppedFields, previousLocale.PublisherSupportUrl, () => locale.PublisherSupportUrl, v => locale.PublisherSupportUrl = v, nameof(locale.PublisherSupportUrl), manifestPath, previousVersion);
        CarryUrl(context, droppedFields, previousLocale.PrivacyUrl, () => locale.PrivacyUrl, v => locale.PrivacyUrl = v, nameof(locale.PrivacyUrl), manifestPath, previousVersion);
        CarryUrl(context, droppedFields, previousLocale.PackageUrl, () => locale.PackageUrl, v => locale.PackageUrl = v, nameof(locale.PackageUrl), manifestPath, previousVersion);
        CarryUrl(context, droppedFields, previousLocale.LicenseUrl, () => locale.LicenseUrl, v => locale.LicenseUrl = v, nameof(locale.LicenseUrl), manifestPath, previousVersion);
        CarryUrl(context, droppedFields, previousLocale.CopyrightUrl, () => locale.CopyrightUrl, v => locale.CopyrightUrl = v, nameof(locale.CopyrightUrl), manifestPath, previousVersion);
        CarryUrl(context, droppedFields, previousLocale.PurchaseUrl, () => locale.PurchaseUrl, v => locale.PurchaseUrl = v, nameof(locale.PurchaseUrl), manifestPath, previousVersion);

        if (locale.Tags is null && previousLocale.Tags is { Count: > 0 } tags && !Skip(context, droppedFields, nameof(locale.Tags)))
        {
            locale.Tags = ManifestValues.CloneStringList(tags);
            AddListEvidence(context, manifestPath, nameof(locale.Tags), tags.Count, previousVersion);
            RecordCarry(context, $"DefaultLocale.{nameof(locale.Tags)}");
        }

        if (locale.Documentations is null && previousLocale.Documentations is { Count: > 0 } docs && !Skip(context, droppedFields, nameof(locale.Documentations)))
        {
            locale.Documentations = ManifestValues.CloneList(docs, ManifestValues.CloneDocumentation);
            AddEvidence(context, manifestPath, nameof(locale.Documentations), previousVersion);
            for (int i = 0; i < docs.Count; i++)
            {
                // The snapshot diff reports mapping leaves, so attach evidence per leaf path.
                AddEvidence(context, manifestPath, $"Documentations[{i}]", previousVersion);
                AddEvidence(context, manifestPath, $"Documentations[{i}].DocumentLabel", previousVersion);
                AddEvidence(context, manifestPath, $"Documentations[{i}].DocumentUrl", previousVersion);
            }

            RecordCarry(context, $"DefaultLocale.{nameof(locale.Documentations)}");
        }
    }

    private void CarryInstallerRootFields(ManifestContext context, PackageManifests previous, ImmutableStringSet droppedFields)
    {
        InstallerManifest manifest = context.Manifests.Installer;
        InstallerManifest previousManifest = previous.Installer;
        string manifestPath = ManifestContext.GetInstallerManifestPath(context.Manifests);
        string previousVersion = previousManifest.PackageVersion?.Value ?? "previous";

        if (manifest.MinimumOSVersion is null && previousManifest.MinimumOSVersion is { } minimumOS
            && !Skip(context, droppedFields, nameof(manifest.MinimumOSVersion)))
        {
            manifest.MinimumOSVersion = minimumOS;
            AddEvidence(context, manifestPath, nameof(manifest.MinimumOSVersion), previousVersion);
            RecordCarry(context, $"Installer.{nameof(manifest.MinimumOSVersion)}");
        }

        if (manifest.InstallModes is null && previousManifest.InstallModes is { Count: > 0 } modes
            && !Skip(context, droppedFields, nameof(manifest.InstallModes)))
        {
            manifest.InstallModes = [.. modes];
            AddListEvidence(context, manifestPath, nameof(manifest.InstallModes), modes.Count, previousVersion);
            RecordCarry(context, $"Installer.{nameof(manifest.InstallModes)}");
        }

        if (!HasAnyReleaseDate(manifest) && HasAnyReleaseDate(previousManifest)
            && !Skip(context, droppedFields, nameof(manifest.ReleaseDate)))
        {
            if (_evidence.ReleaseDate is { } releaseDate)
            {
                manifest.ReleaseDate = releaseDate;
                context.AddChangeEvidence(
                    this,
                    ManifestContext.GetInstallerManifestPath(context.Manifests),
                    "ReleaseDate",
                    "recomputed from supplied release metadata (the previous manifest declared one)",
                    RuleChangeConfidence.High);
                context.AddTrace(this, "Installer: recomputed ReleaseDate from supplied release metadata.");
            }
            else
            {
                context.AddFinding(this, RuleSeverity.Warning,
                    "The previous version declared ReleaseDate but no release-date evidence was supplied for the new version; the field must be recomputed, not copied.");
            }
        }
    }

    private static bool HasAnyReleaseDate(InstallerManifest manifest)
        => manifest.ReleaseDate is not null
            || manifest.Installers?.Any(static i => i.ReleaseDate is not null) == true;

    private void CarryText(
        ManifestContext context,
        ImmutableStringSet droppedFields,
        string? previousValue,
        Func<string?> get,
        Action<string> set,
        string fieldName,
        string manifestPath,
        string previousVersion)
    {
        if (previousValue is null || get() is not null || Skip(context, droppedFields, fieldName))
        {
            return;
        }

        set(previousValue);
        AddEvidence(context, manifestPath, fieldName, previousVersion);
        RecordCarry(context, $"DefaultLocale.{fieldName}");
    }

    private void CarryUrl(
        ManifestContext context,
        ImmutableStringSet droppedFields,
        string? previousValue,
        Func<string?> get,
        Action<string> set,
        string fieldName,
        string manifestPath,
        string previousVersion)
    {
        if (previousValue is null || get() is not null || Skip(context, droppedFields, fieldName))
        {
            return;
        }

        if (!_evidence.IsUrlConfirmed(previousValue))
        {
            context.AddFinding(this, RuleSeverity.Warning,
                $"{fieldName} from the previous version was dropped and no confirmed-URL evidence re-validated it; supply URL evidence to carry it forward or add an explicit drop override.");
            return;
        }

        set(previousValue);
        context.AddChangeEvidence(
            this,
            manifestPath,
            fieldName,
            $"previous merged manifest ({previousVersion}); URL re-validated by supplied evidence",
            RuleChangeConfidence.High);
        RecordCarry(context, $"DefaultLocale.{fieldName} (URL re-validated by supplied evidence)");
    }

    private void AddEvidence(ManifestContext context, string manifestPath, string fieldPath, string previousVersion)
        => context.AddChangeEvidence(
            this,
            manifestPath,
            fieldPath,
            $"previous merged manifest ({previousVersion})",
            RuleChangeConfidence.High);

    /// <summary>
    /// Attaches carry evidence for a cloned list at the parent key and each item index, so
    /// whichever paths the snapshot diff reports resolve to the same provenance.
    /// </summary>
    private void AddListEvidence(ManifestContext context, string manifestPath, string fieldName, int count, string previousVersion)
    {
        AddEvidence(context, manifestPath, fieldName, previousVersion);
        for (int i = 0; i < count; i++)
        {
            AddEvidence(context, manifestPath, $"{fieldName}[{i}]", previousVersion);
        }
    }

    private bool Skip(ManifestContext context, ImmutableStringSet droppedFields, string fieldName)
    {
        if (!droppedFields.Contains(fieldName))
        {
            return false;
        }

        context.AddTrace(this, $"{fieldName}: drop explicitly authorized by the package override's DroppedFields.");
        return true;
    }

    private void RecordCarry(ManifestContext context, string what)
        => context.AddTrace(this, $"Carried {what} over from the previous merged version.");

    private readonly struct ImmutableStringSet(IEnumerable<string> values)
    {
        private readonly HashSet<string> _values = new(values, StringComparer.OrdinalIgnoreCase);

        public bool Contains(string value) => _values.Contains(value);
    }
}
