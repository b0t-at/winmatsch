using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// DEP-1: maps <em>Detected</em> payload runtime evidence (from
/// <c>PayloadDependencyAnalyzer</c>, supplied via
/// <see cref="PolicyEvidence.DependencyAnalyses"/>) to WinGet package dependencies whose
/// architecture matches the installer entry. Inferred or Ambiguous evidence never becomes a
/// mandatory dependency — it only produces an informational finding. When the previous version
/// pinned a .NET runtime major that Detected evidence now contradicts, a finding requests
/// verification instead of a silent identifier rewrite.
/// </summary>
public sealed class Dep1PayloadDependencyRule : IRule
{
    private const string VcRedistPrefix = "Microsoft.VCRedist.2015+";
    private const string DotNetRuntimePrefix = "Microsoft.DotNet.Runtime.";

    private readonly PolicyEvidence _evidence;

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
        }

        AddDependency(context, manifest, installer, index, identifier, evidence);
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
                return evidence.RuntimeMajor is { } major ? $"{DotNetRuntimePrefix}{major}" : null;
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

        Dependencies dependencies = installer.Dependencies ??= new Dependencies();
        List<PackageDependency> packageDependencies = dependencies.PackageDependencies ??= [];
        if (HasPackageDependency(dependencies, identifier))
        {
            return;
        }

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

        Installer? previousMatch = PolicyValues.FindPreviousByEntryKey(manifest, installer, previous.Installer);
        if (previousMatch is null)
        {
            return;
        }

        Dependencies? previousDependencies = EffectiveInstallerValues.GetDependencies(previous.Installer, previousMatch);
        foreach (PackageDependency dependency in previousDependencies?.PackageDependencies ?? [])
        {
            string? id = dependency.PackageIdentifier?.Value;
            if (id is null || !id.StartsWith(DotNetRuntimePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(id.AsSpan(DotNetRuntimePrefix.Length), out int previousMajor)
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
