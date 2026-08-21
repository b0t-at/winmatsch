using BinMatch.Analysis.Dependencies;
using BinMatch.Core;

namespace BinMatch.Rules.Policy;

/// <summary>
/// DEP-1: maps <em>Detected</em> payload runtime evidence (from
/// <c>PayloadDependencyAnalyzer</c>, supplied via
/// <see cref="PolicyEvidence.DependencyAnalyses"/>) to WinGet package dependencies whose
/// architecture matches the installer entry. Inferred or Ambiguous evidence never becomes a
/// mandatory dependency — it only produces an informational finding. The runtime package family
/// follows the shared framework the payload requests (desktop, ASP.NET Core, or base). When the
/// previous version pinned a different major of the same family, Detected evidence refreshes it
/// and records the rewrite; a pin from another family is reported for review instead.
/// </summary>
public sealed class Dep1PayloadDependencyRule : IRule
{
    private const string VcRedistPrefix = "Microsoft.VCRedist.2015+";
    private const string DotNetRuntimePrefix = "Microsoft.DotNet.Runtime.";
    private const string DotNetDesktopRuntimePrefix = "Microsoft.DotNet.DesktopRuntime.";
    private const string DotNetAspNetCorePrefix = "Microsoft.DotNet.AspNetCore.";

    private static readonly string[] _dotNetRuntimePrefixes =
        [DotNetDesktopRuntimePrefix, DotNetAspNetCorePrefix, DotNetRuntimePrefix];

    private readonly PolicyEvidence _evidence;
    private string? _rootRuntimeRewrite;

    public Dep1PayloadDependencyRule(PolicyEvidence? evidence = null)
    {
        _evidence = evidence ?? PolicyEvidence.Empty;
    }

    public string Id => RuleCatalogueIds.Dep1;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Adds architecture-matched runtime dependencies from Detected payload evidence.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _rootRuntimeRewrite = null;
        InstallerManifest manifest = context.Manifests.Installer;
        if (manifest.Installers is not { } installers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            PayloadDependencyAnalysis? analysis = _evidence.FindDependencyAnalysis(installer.InstallerUrl);
            if (analysis is null)
            {
                continue;
            }

            ProcessInstaller(context, manifest, installer, i, analysis);
        }
    }

    private void ProcessInstaller(
        ManifestContext context,
        InstallerManifest manifest,
        Installer installer,
        int index,
        PayloadDependencyAnalysis analysis)
    {
        foreach (DependencyEvidence evidence in analysis.Evidence)
        {
            switch (evidence.Status)
            {
                case DependencyEvidenceStatus.Detected:
                    ProcessDetected(context, manifest, installer, index, evidence);
                    break;
                case DependencyEvidenceStatus.Inferred:
                case DependencyEvidenceStatus.Ambiguous:
                case DependencyEvidenceStatus.Unavailable:
                    context.AddFinding(this, RuleSeverity.Info,
                        $"{evidence.Status} {Describe(evidence)} evidence from '{evidence.PayloadPath}' is not strong enough for a mandatory dependency; confirm manually or via a package override.",
                        $"Installers[{index}]");
                    break;
                case DependencyEvidenceStatus.Absent:
                    break;
            }
        }
    }

    private void ProcessDetected(
        ManifestContext context,
        InstallerManifest manifest,
        Installer installer,
        int index,
        DependencyEvidence evidence)
    {
        if (installer.Architecture is not { } installerArchitecture
            || installerArchitecture == Architecture.Neutral)
        {
            return;
        }

        if (evidence.Architecture is not { } payloadArchitecture
            || payloadArchitecture != installerArchitecture)
        {
            context.AddFinding(this, RuleSeverity.Info,
                $"Detected {Describe(evidence)} evidence from '{evidence.PayloadPath}' targets architecture '{evidence.Architecture?.ToString() ?? "unknown"}', which does not match the installer's '{installerArchitecture}'; no dependency added.",
                $"Installers[{index}]");
            return;
        }

        string? identifier = MapPackageIdentifier(evidence, installerArchitecture);
        if (identifier is null)
        {
            return;
        }

        if (evidence.Kind == DependencyEvidenceKind.DotNetRuntime)
        {
            VerifyPreviousDotNetMajor(context, manifest, installer, index, evidence);
            if (TryRefreshDotNetRuntime(context, manifest, installer, index, identifier))
            {
                return;
            }

            if (FindConflictingDotNetRuntime(manifest, installer, identifier) is { } conflicting)
            {
                // Appending would leave two mandatory runtimes; a carried pin from another
                // runtime family must be resolved by review (or an override), never by stacking
                // a second dependency.
                context.AddFinding(this, RuleSeverity.Warning,
                    $"Detected .NET runtime dependency '{identifier}' conflicts with the already-declared dependency '{conflicting}'; resolve the declared runtime instead of adding a second mandatory runtime.",
                    $"Installers[{index}]");
                return;
            }
        }

        AddDependency(context, manifest, installer, index, identifier, evidence);
    }

    /// <summary>
    /// Replaces an already-declared dependency on the same runtime family whose major the payload
    /// contradicts. The rewrite stays inside one family, is driven by Detected evidence read from
    /// the payload's own runtime configuration, and is recorded as change evidence, so a carried
    /// forward pin cannot silently outlive the runtime it described.
    /// </summary>
    private bool TryRefreshDotNetRuntime(
        ManifestContext context,
        InstallerManifest manifest,
        Installer installer,
        int index,
        string identifier)
    {
        if (FindFamilyPrefix(identifier) is not { } prefix)
        {
            return false;
        }

        Dependencies? effective = EffectiveInstallerValues.GetDependencies(manifest, installer);
        List<PackageDependency> declared = effective?.PackageDependencies ?? [];
        int position = -1;
        for (int i = 0; i < declared.Count; i++)
        {
            string? id = declared[i].PackageIdentifier?.Value;
            if (id is not null
                && id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(id, identifier, StringComparison.OrdinalIgnoreCase))
            {
                position = i;
                break;
            }
        }

        if (position < 0)
        {
            return false;
        }

        string stale = declared[position].PackageIdentifier!.Value;

        // A pin the installer merely inherits belongs to the manifest root; rewriting it there
        // keeps root and installer consistent instead of leaving a contradictory root value.
        if (installer.Dependencies is null
            && TryRefreshRootDotNetRuntime(context, manifest, stale, identifier))
        {
            context.AddTrace(this,
                $"Installers[{index}]: replaced package dependency '{stale}' with '{identifier}' from Detected payload evidence.");
            return true;
        }

        // Creating a bare per-installer Dependencies object would mask the manifest-root
        // defaults (WindowsFeatures, external deps, ...); clone the effective set first.
        if (installer.Dependencies is null && effective is not null)
        {
            installer.Dependencies = ManifestValues.CloneDependencies(effective);
        }

        if (installer.Dependencies?.PackageDependencies is not { } target || position >= target.Count)
        {
            return false;
        }

        target[position] = new PackageDependency { PackageIdentifier = new PackageIdentifier(identifier) };
        context.AddChangeEvidence(
            this,
            ManifestContext.GetInstallerManifestPath(context.Manifests),
            $"Installers[{index}].Dependencies.PackageDependencies[{position}].PackageIdentifier",
            $"Detected payload runtime configuration replaces the stale dependency '{stale}'",
            RuleChangeConfidence.High);
        context.AddTrace(this,
            $"Installers[{index}]: replaced package dependency '{stale}' with '{identifier}' from Detected payload evidence.");
        return true;
    }

    /// <summary>
    /// Rewrites a stale runtime pin declared at the manifest root. Refused once another installer
    /// already claimed the root pin for a different runtime major, so mixed payloads fall back to
    /// per-installer dependencies instead of fighting over one shared value.
    /// </summary>
    private bool TryRefreshRootDotNetRuntime(
        ManifestContext context,
        InstallerManifest manifest,
        string stale,
        string identifier)
    {
        if (_rootRuntimeRewrite is not null
            && !string.Equals(_rootRuntimeRewrite, identifier, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (manifest.Dependencies?.PackageDependencies is not { } root)
        {
            return false;
        }

        for (int i = 0; i < root.Count; i++)
        {
            if (!string.Equals(root[i].PackageIdentifier?.Value, stale, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            root[i] = new PackageDependency { PackageIdentifier = new PackageIdentifier(identifier) };
            _rootRuntimeRewrite = identifier;
            context.AddChangeEvidence(
                this,
                ManifestContext.GetInstallerManifestPath(context.Manifests),
                $"Dependencies.PackageDependencies[{i}].PackageIdentifier",
                $"Detected payload runtime configuration replaces the stale dependency '{stale}'",
                RuleChangeConfidence.High);
            return true;
        }

        return false;
    }

    /// <summary>An already-declared .NET runtime dependency from a different family, or null.</summary>
    private static string? FindConflictingDotNetRuntime(
        InstallerManifest manifest,
        Installer installer,
        string identifier)
    {
        Dependencies? effective = EffectiveInstallerValues.GetDependencies(manifest, installer);
        foreach (PackageDependency dependency in effective?.PackageDependencies ?? [])
        {
            string? id = dependency.PackageIdentifier?.Value;
            if (id is not null
                && !string.Equals(id, identifier, StringComparison.OrdinalIgnoreCase)
                && FindFamilyPrefix(id) is not null)
            {
                return id;
            }
        }

        return null;
    }

    private static string? FindFamilyPrefix(string identifier)
    {
        foreach (string prefix in _dotNetRuntimePrefixes)
        {
            if (identifier.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return prefix;
            }
        }

        return null;
    }

    private static string? MapPackageIdentifier(DependencyEvidence evidence, Architecture architecture)
    {
        switch (evidence.Kind)
        {
            case DependencyEvidenceKind.VisualCppRuntime:
                string? suffix = architecture switch
                {
                    Architecture.X64 => "x64",
                    Architecture.X86 => "x86",
                    Architecture.Arm64 => "arm64",
                    _ => null,
                };
                return suffix is null ? null : $"{VcRedistPrefix}.{suffix}";
            case DependencyEvidenceKind.DotNetRuntime:
                if (evidence.RuntimeMajor is not { } major)
                {
                    return null;
                }

                string prefix = evidence.RuntimeFamily switch
                {
                    DotNetRuntimeFamily.WindowsDesktop => DotNetDesktopRuntimePrefix,
                    DotNetRuntimeFamily.AspNetCore => DotNetAspNetCorePrefix,
                    _ => DotNetRuntimePrefix,
                };
                return $"{prefix}{major}";
            default:
                return null;
        }
    }

    private void AddDependency(
        ManifestContext context,
        InstallerManifest manifest,
        Installer installer,
        int index,
        string identifier,
        DependencyEvidence evidence)
    {
        Dependencies? effective = EffectiveInstallerValues.GetDependencies(manifest, installer);
        if (HasPackageDependency(effective, identifier))
        {
            return;
        }

        // Creating a bare per-installer Dependencies object would mask the manifest-root
        // defaults (WindowsFeatures, external deps, ...); clone the effective set first.
        if (installer.Dependencies is null && effective is not null)
        {
            installer.Dependencies = ManifestValues.CloneDependencies(effective);
        }

        Dependencies dependencies = installer.Dependencies ??= new Dependencies();
        List<PackageDependency> packageDependencies = dependencies.PackageDependencies ??= [];
        packageDependencies.Add(new PackageDependency { PackageIdentifier = new PackageIdentifier(identifier) });
        string signals = evidence.Signals.Count == 0 ? "payload metadata" : string.Join(", ", evidence.Signals);
        context.AddChangeEvidence(
            this,
            ManifestContext.GetInstallerManifestPath(context.Manifests),
            $"Installers[{index}].Dependencies.PackageDependencies[{packageDependencies.Count - 1}].PackageIdentifier",
            $"Detected payload evidence from '{evidence.PayloadPath}' ({signals})",
            RuleChangeConfidence.High);
        context.AddTrace(this,
            $"Installers[{index}]: added package dependency '{identifier}' from Detected payload evidence ('{evidence.PayloadPath}').");
    }

    private void VerifyPreviousDotNetMajor(
        ManifestContext context,
        InstallerManifest manifest,
        Installer installer,
        int index,
        DependencyEvidence evidence)
    {
        if (context.Previous is not { } previous || evidence.RuntimeMajor is not { } detectedMajor)
        {
            return;
        }

        Installer? previousMatch = PolicyValues.FindPreviousByEntryKey(
            manifest,
            installer,
            previous.Installer,
            out bool ambiguous);
        if (ambiguous)
        {
            context.AddFinding(
                this,
                RuleSeverity.Warning,
                "Several previous installers share this entry's semantic identity; verify the previous .NET dependency manually.",
                $"Installers[{index}]");
            return;
        }

        if (previousMatch is null)
        {
            return;
        }

        Dependencies? previousDependencies = EffectiveInstallerValues.GetDependencies(previous.Installer, previousMatch);
        foreach (PackageDependency dependency in previousDependencies?.PackageDependencies ?? [])
        {
            string? id = dependency.PackageIdentifier?.Value;
            if (id is null || FindFamilyPrefix(id) is not { } prefix)
            {
                continue;
            }

            if (int.TryParse(id.AsSpan(prefix.Length), out int previousMajor)
                && previousMajor != detectedMajor)
            {
                context.AddFinding(this, RuleSeverity.Warning,
                    $"The previous version pinned .NET runtime major {previousMajor} but the new payload's runtime configuration targets major {detectedMajor}; verify and update the dependency.",
                    $"Installers[{index}]");
            }
        }
    }

    private static bool HasPackageDependency(Dependencies? dependencies, string identifier)
        => dependencies?.PackageDependencies?.Any(d =>
            string.Equals(d.PackageIdentifier?.Value, identifier, StringComparison.OrdinalIgnoreCase)) == true;

    private static string Describe(DependencyEvidence evidence)
        => evidence.Kind == DependencyEvidenceKind.VisualCppRuntime ? "VC++ runtime" : ".NET runtime";
}
