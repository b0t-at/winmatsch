[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [switch] $AcquireTools
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$innoVersion = '6.4.0'
$nsisVersion = '3.10'
$wixVersion = '5.0.2'
$windowsSdkVersion = '10.0.26100.0'
$innoInstallerUri = 'https://github.com/jrsoftware/issrc/releases/download/is-6_4_0/innosetup-6.4.0.exe'
$innoInstallerSha256 = 'A360DB165CFB1D42D195B020700181E7EAF5DB45C1249A24EDB51C3C33E9D659'
$nsisInstallerUri = 'https://pilotfiber.dl.sourceforge.net/project/nsis/NSIS%203/3.10/nsis-3.10-setup.exe'
$nsisInstallerSha256 = '4313D352E0DAFD1F22B6517126A655CAE3B444FA758D2845EDDFBE72F24F7BDD'
$wixPackageUri = 'https://api.nuget.org/v3-flatcontainer/wix/5.0.2/wix.5.0.2.nupkg'
$wixPackageSha256 = 'F30EF0C74E2A986126539C5780BE93AC24E8136EAF723B1937B26272703AE173'
$balPackageUri = 'https://api.nuget.org/v3-flatcontainer/wixtoolset.bal.wixext/5.0.2/wixtoolset.bal.wixext.5.0.2.nupkg'
$balPackageSha256 = '22422B50A925477E33C2B5F78D2965A9A09C6622D17BA5AA5365B20EC662B7C8'
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

function Assert-FileHash([string] $Path, [string] $ExpectedSha256) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -cne $ExpectedSha256) {
        throw "File '$Path' has SHA-256 '$actual', expected '$ExpectedSha256'."
    }
}

function Get-VerifiedFile(
    [string] $Uri,
    [string] $Path,
    [string] $ExpectedSha256,
    [bool] $AllowDownload
) {
    if (-not (Test-Path -LiteralPath $Path)) {
        if (-not $AllowDownload) {
            throw "Pinned package '$Path' is absent; rerun with -AcquireTools."
        }

        & curl.exe --fail --location --silent --show-error --proto '=https' --tlsv1.2 `
            --output $Path $Uri
        if ($LASTEXITCODE -ne 0) {
            throw "Pinned package download from '$Uri' failed."
        }
    }

    Assert-FileHash $Path $ExpectedSha256
    return (Resolve-Path -LiteralPath $Path).Path
}

function Assert-SignedTool([string] $Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Tool '$Path' does not have a valid Authenticode signature: $($signature.Status)."
    }
}

New-Item -ItemType Directory -Force -Path $toolRoot, $packageRoot | Out-Null
$innoInstaller = Get-VerifiedFile `
    $innoInstallerUri `
    (Join-Path $packageRoot "innosetup-$innoVersion.exe") `
    $innoInstallerSha256 `
    $AcquireTools.IsPresent
$nsisInstaller = Get-VerifiedFile `
    $nsisInstallerUri `
    (Join-Path $packageRoot "nsis-$nsisVersion-setup.exe") `
    $nsisInstallerSha256 `
    $AcquireTools.IsPresent
$wixPackage = Get-VerifiedFile `
    $wixPackageUri `
    (Join-Path $packageRoot "wix.$wixVersion.nupkg") `
    $wixPackageSha256 `
    $AcquireTools.IsPresent
$balPackage = Get-VerifiedFile `
    $balPackageUri `
    (Join-Path $packageRoot "WixToolset.Bal.wixext.$wixVersion.nupkg") `
    $balPackageSha256 `
    $AcquireTools.IsPresent

Assert-SignedTool $innoInstaller

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
$makeAppx = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\$windowsSdkVersion\x64\makeappx.exe"

if (-not (Test-Path -LiteralPath $inno)) { throw "Pinned Inno Setup $innoVersion is not installed." }
if (-not (Test-Path -LiteralPath $innoVersionMarker)) {
    throw "Pinned Inno Setup $innoVersion version marker is absent."
}
if (-not (Test-Path -LiteralPath $nsis)) { throw "Pinned NSIS $nsisVersion is not installed." }
if (-not (Test-Path -LiteralPath $wix)) { throw "Pinned WiX $wixVersion is not installed under '$wixRoot'." }
if (-not (Test-Path -LiteralPath $balExtension)) {
    throw "Pinned WiX Bal extension $wixVersion is not installed under '$balRoot'."
}
if (-not (Test-Path -LiteralPath $makeAppx)) {
    throw "Pinned Windows SDK $windowsSdkVersion MakeAppx was not found."
}

Assert-SignedTool $inno
Assert-SignedTool $makeAppx

$actualInnoVersion = (Get-Item -LiteralPath $innoVersionMarker).VersionInfo.ProductVersion.Trim()
if ($actualInnoVersion -cne $innoVersion) {
    throw "Inno Setup has version '$actualInnoVersion', expected exactly '$innoVersion'."
}

$actualNsisVersion = (& $nsis '/VERSION' | Select-Object -First 1).TrimStart('v')
if ($actualNsisVersion -cne $nsisVersion) {
    throw "NSIS has version '$actualNsisVersion', expected exactly '$nsisVersion'."
}

$actualSdkVersion = [Version](
    (Get-Item -LiteralPath $makeAppx).VersionInfo.FileVersion.Split(' ', 2)[0])
if ($actualSdkVersion.Major -ne 10 -or $actualSdkVersion.Minor -ne 0 -or $actualSdkVersion.Build -ne 26100) {
    throw "MakeAppx has serviced version '$actualSdkVersion', expected SDK build '10.0.26100'."
}

$actualWixVersion = (& $wix --version | Select-Object -First 1).Trim()
$normalizedWixVersion = ($actualWixVersion -split '\+', 2)[0]
if ($normalizedWixVersion -ne $wixVersion) {
    throw "WiX has version '$actualWixVersion', expected '$wixVersion'."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path

"TOOL InnoSetup $innoVersion $innoInstallerSha256"
"TOOL NSIS $nsisVersion $nsisInstallerSha256"
"TOOL WiX $wixVersion $wixPackageSha256"
"TOOL WixToolset.Bal.wixext $wixVersion $balPackageSha256"
"TOOL WindowsSDK $windowsSdkVersion $((Get-FileHash -LiteralPath $makeAppx -Algorithm SHA256).Hash)"

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
