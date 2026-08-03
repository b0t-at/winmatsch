[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [switch] $AcquireTools
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolingModule = Join-Path $PSScriptRoot 'WindowsInstallerCorpus.Tooling.psm1'
Import-Module $toolingModule -Force
$toolManifest = Get-WindowsInstallerCorpusToolManifest
$innoVersion = $toolManifest.InnoVersion
$nsisVersion = $toolManifest.NsisVersion
$wixVersion = $toolManifest.WixVersion
$windowsSdkVersion = $toolManifest.WindowsSdkVersion
$toolRoot = if ($env:WINMATSCH_COMPILER_TOOL_ROOT) {
    $env:WINMATSCH_COMPILER_TOOL_ROOT
} elseif ($env:RUNNER_TEMP) {
    Join-Path $env:RUNNER_TEMP 'winmatsch-compiler-tools'
} else {
    Join-Path ([IO.Path]::GetTempPath()) 'winmatsch-compiler-tools'
}
$sourceRoot = Join-Path $PSScriptRoot 'Sources'
$packageRoot = Join-Path $toolRoot 'packages'
$innoRoot = Join-Path $toolRoot 'inno'
$nsisRoot = Join-Path $toolRoot 'nsis'
$wixRoot = Join-Path $toolRoot 'wix'
$balRoot = Join-Path $toolRoot 'bal'

New-Item -ItemType Directory -Force -Path $toolRoot, $packageRoot | Out-Null
$innoAcquisition = Get-VerifiedFile `
    -Uris $toolManifest.InnoInstallerUris `
    -Path (Join-Path $packageRoot "innosetup-$innoVersion.exe") `
    -ExpectedSha256 $toolManifest.InnoInstallerSha256 `
    -AllowDownload $AcquireTools.IsPresent
$nsisAcquisition = Get-VerifiedFile `
    -Uris $toolManifest.NsisInstallerUris `
    -Path (Join-Path $packageRoot "nsis-$nsisVersion-setup.exe") `
    -ExpectedSha256 $toolManifest.NsisInstallerSha256 `
    -AllowDownload $AcquireTools.IsPresent
$wixAcquisition = Get-VerifiedFile `
    -Uris $toolManifest.WixPackageUris `
    -Path (Join-Path $packageRoot "wix.$wixVersion.nupkg") `
    -ExpectedSha256 $toolManifest.WixPackageSha256 `
    -AllowDownload $AcquireTools.IsPresent
$balAcquisition = Get-VerifiedFile `
    -Uris $toolManifest.BalPackageUris `
    -Path (Join-Path $packageRoot "WixToolset.Bal.wixext.$wixVersion.nupkg") `
    -ExpectedSha256 $toolManifest.BalPackageSha256 `
    -AllowDownload $AcquireTools.IsPresent
$innoInstaller = $innoAcquisition.Path
$nsisInstaller = $nsisAcquisition.Path
$wixPackage = $wixAcquisition.Path
$balPackage = $balAcquisition.Path
$innoSigner = Assert-AuthenticodeSignature -Path $innoInstaller

if ($AcquireTools) {
    foreach ($directory in @($innoRoot, $nsisRoot, $wixRoot, $balRoot)) {
        if (Test-Path -LiteralPath $directory) {
            Remove-Item -LiteralPath $directory -Recurse
        }
    }

    $innoInstall = Start-Process -FilePath $innoInstaller -Wait -PassThru -ArgumentList @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/NOICONS',
        '/SP-',
        '/CURRENTUSER',
        "/DIR=$innoRoot"
    )
    if ($innoInstall.ExitCode -ne 0) { throw "Pinned Inno Setup acquisition failed." }

    $nsisInstall = Start-Process -FilePath $nsisInstaller -Wait -PassThru -ArgumentList @(
        '/S',
        "/D=$nsisRoot"
    )
    if ($nsisInstall.ExitCode -ne 0) { throw "Pinned NSIS acquisition failed." }

    $nugetConfig = Join-Path $toolRoot 'NuGet.Config'
    $escapedPackageRoot = [Security.SecurityElement]::Escape($packageRoot)
    Set-Content -LiteralPath $nugetConfig -Encoding utf8NoBOM -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="verified-local" value="$escapedPackageRoot" />
  </packageSources>
</configuration>
"@
    dotnet tool install wix --version $wixVersion --tool-path $wixRoot --configfile $nugetConfig
    if ($LASTEXITCODE -ne 0) { throw "Pinned WiX acquisition failed." }

    New-Item -ItemType Directory -Force -Path $balRoot | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($balPackage, $balRoot)
}

$inno = Join-Path $innoRoot 'ISCC.exe'
$innoVersionMarker = Join-Path $innoRoot 'unins000.exe'
$nsis = Join-Path $nsisRoot 'makensis.exe'
$wix = Join-Path $wixRoot 'wix.exe'
$balExtension = Join-Path $balRoot 'wixext5\WixToolset.BootstrapperApplications.wixext.dll'
$defaultWindowsSdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppxCandidates = Get-MakeAppxCandidatePaths `
    -SdkVersion $windowsSdkVersion `
    -DefaultSdkRoot $defaultWindowsSdkRoot

if (-not (Test-Path -LiteralPath $inno)) { throw "Pinned Inno Setup $innoVersion is not installed." }
if (-not (Test-Path -LiteralPath $innoVersionMarker)) {
    throw "Pinned Inno Setup $innoVersion version marker is absent."
}
if (-not (Test-Path -LiteralPath $nsis)) { throw "Pinned NSIS $nsisVersion is not installed." }
if (-not (Test-Path -LiteralPath $wix)) { throw "Pinned WiX $wixVersion is not installed under '$wixRoot'." }
if (-not (Test-Path -LiteralPath $balExtension)) {
    throw "Pinned WiX Bal extension $wixVersion is not installed under '$balRoot'."
}
$installedInnoSigner = Assert-AuthenticodeSignature -Path $inno

$actualInnoVersion = (Get-Item -LiteralPath $innoVersionMarker).VersionInfo.ProductVersion.Trim()
if ($actualInnoVersion -cne $innoVersion) {
    throw "Inno Setup has version '$actualInnoVersion', expected exactly '$innoVersion'."
}

$actualNsisVersion = (& $nsis '/VERSION' | Select-Object -First 1).TrimStart('v')
if ($actualNsisVersion -cne $nsisVersion) {
    throw "NSIS has version '$actualNsisVersion', expected exactly '$nsisVersion'."
}

$makeAppxAcquisition = Resolve-ApprovedMakeAppx `
    -CandidatePaths $makeAppxCandidates `
    -SdkVersion $windowsSdkVersion `
    -MinimumFileVersion ([Version]$toolManifest.MakeAppxMinimumFileVersion) `
    -ApprovedSignerOrganization $toolManifest.MakeAppxSignerOrganization
$makeAppx = $makeAppxAcquisition.Path

$actualWixVersion = (& $wix --version | Select-Object -First 1).Trim()
$normalizedWixVersion = ($actualWixVersion -split '\+', 2)[0]
if ($normalizedWixVersion -ne $wixVersion) {
    throw "WiX has version '$actualWixVersion', expected '$wixVersion'."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path

"PROVENANCE InnoSetup version=$innoVersion sha256=$($innoAcquisition.Sha256) source=$($innoAcquisition.Source) installerSigner=$innoSigner installedSigner=$installedInnoSigner constraint=exact-version-sha256-valid-authenticode"
"PROVENANCE NSIS version=$nsisVersion sha256=$($nsisAcquisition.Sha256) source=$($nsisAcquisition.Source) constraint=exact-version-sha256"
"PROVENANCE WiX version=$wixVersion sha256=$($wixAcquisition.Sha256) source=$($wixAcquisition.Source) constraint=exact-version-sha256"
"PROVENANCE WixToolset.Bal.wixext version=$wixVersion sha256=$($balAcquisition.Sha256) source=$($balAcquisition.Source) constraint=exact-version-sha256"
"PROVENANCE WindowsSDK-MakeAppx sdkVersion=$windowsSdkVersion fileVersion=$($makeAppxAcquisition.FileVersion) sha256=$($makeAppxAcquisition.Sha256) source=$($makeAppxAcquisition.Source) signer=$($makeAppxAcquisition.Signer) constraint=valid-microsoft-authenticode-sdk-build-$(([Version]$windowsSdkVersion).Build)-minimum-$($toolManifest.MakeAppxMinimumFileVersion)"

& $inno "/O$resolvedOutput" '/Ffixture-inno' (Join-Path $sourceRoot 'fixture.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno fixture compilation failed." }

& $nsis "/DOUTFILE=$(Join-Path $resolvedOutput 'fixture-nsis.exe')" `
    (Join-Path $sourceRoot 'fixture.nsi')
if ($LASTEXITCODE -ne 0) { throw "NSIS fixture compilation failed." }

$wixMsi = Join-Path $resolvedOutput 'fixture-wix.msi'
& $wix build (Join-Path $sourceRoot 'fixture.wxs') -o $wixMsi
if ($LASTEXITCODE -ne 0) { throw "WiX MSI fixture compilation failed." }
Copy-Item -LiteralPath $wixMsi -Destination (Join-Path $resolvedOutput 'fixture-msi.msi')

$bundleTemplate = Get-Content -LiteralPath (Join-Path $sourceRoot 'fixture-bundle.wxs') -Raw
$bundleSource = Join-Path $resolvedOutput 'fixture-bundle.generated.wxs'
$escapedMsi = [Security.SecurityElement]::Escape($wixMsi)
Set-Content -LiteralPath $bundleSource -Value $bundleTemplate.Replace('__MSI_PATH__', $escapedMsi) `
    -Encoding utf8NoBOM
& $wix build $bundleSource -dcl mszip -ext $balExtension `
    -o (Join-Path $resolvedOutput 'fixture-burn.exe')
if ($LASTEXITCODE -ne 0) { throw "WiX Burn fixture compilation failed." }
Remove-Item -LiteralPath $bundleSource

$layout = Join-Path $resolvedOutput 'msix-layout'
New-Item -ItemType Directory -Force -Path (Join-Path $layout 'Assets') | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'AppxManifest.xml') -Destination $layout
Copy-Item -LiteralPath (Join-Path $resolvedOutput 'fixture-inno.exe') `
    -Destination (Join-Path $layout 'fixture.exe')
$logo = [Convert]::FromBase64String(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=')
[IO.File]::WriteAllBytes((Join-Path $layout 'Assets\logo.png'), $logo)
& $makeAppx pack /d $layout /p (Join-Path $resolvedOutput 'fixture.msix') /o
if ($LASTEXITCODE -ne 0) { throw "MSIX fixture compilation failed." }
Remove-Item -LiteralPath $layout -Recurse

Get-ChildItem -LiteralPath $resolvedOutput -File |
    Where-Object Extension -In '.exe', '.msi', '.msix' |
    Sort-Object Name |
    ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        "OUTPUT $($_.Name) $($hash.Hash)"
    }
